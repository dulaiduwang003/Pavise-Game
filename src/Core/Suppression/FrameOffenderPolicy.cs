// @author bdth 2074055628@qq.com
// 文件用途 把帧归因指认出的抢占者提一档压制 并用下一个观察窗验证这一手有没有用
using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal enum OffenderState
    {
        Idle = 0,
        Nominated = 1,
        Applied = 2,
        Verified = 3,
        Ineffective = 4,
        Quarantined = 5
    }

    internal static class FrameOffenderPolicy
    {
        internal const double NominateExplained = 0.30;
        internal const double EffectiveDrop = 0.70;
        internal const double ContribDrop = 0.85;
        internal const int MaxStrikes = 2;

        private static readonly object lk = new object();
        private static readonly Dictionary<uint, int> strikes = new Dictionary<uint, int>();
        private static uint pid;
        private static string name;
        private static OffenderState state;
        private static double baselineExplained;
        private static double baselineExcessPerFrame;
        private static DateTime baselineStart;
        private static double heldMs;
        private static int windowsSinceApply;
        private static int escalations;
        private static int verified;
        private static int ineffective;
        private static bool enabled;
        private static bool frozen;

        public static bool Enabled { get { lock (lk) return enabled; } }

        public static void Freeze(string why)
        {
            lock (lk)
            {
                if (frozen) return;
                frozen = true;
                if (state == OffenderState.Nominated || state == OffenderState.Applied)
                {
                    Logger.Log(Lang.F("log.frameoffender.4", name ?? "?", why ?? "?"));
                    pid = 0; name = null; state = OffenderState.Idle;
                }
            }
        }

        internal static bool Frozen { get { lock (lk) return frozen; } }

        public static void CancelPending()
        {
            lock (lk)
            {
                if (state != OffenderState.Nominated) return;
                pid = 0; name = null; state = OffenderState.Idle;
            }
        }

        internal static bool HasCandidate { get { lock (lk) return state == OffenderState.Nominated || state == OffenderState.Applied; } }

        public static void Reset(bool on)
        {
            lock (lk)
            {
                strikes.Clear();
                pid = 0; name = null; state = OffenderState.Idle;
                baselineExplained = 0; baselineExcessPerFrame = 0; heldMs = 0;
                baselineStart = DateTime.MinValue;
                windowsSinceApply = 0;
                escalations = 0; verified = 0; ineffective = 0;
                enabled = on;
                frozen = false;
            }
        }

        public static void Nominate(uint offenderPid, string offenderName, double heldTotalMs,
            double explained, double excessPerFrame, DateTime windowStart)
        {
            if (offenderPid == 0) return;
            DateTime started;
            bool startKnown = TryStartTime(offenderPid, out started);
            if (!startKnown) return;
            if (windowStart != DateTime.MinValue && started > windowStart) return;
            lock (lk)
            {
                if (!enabled || frozen) return;
                if (explained < NominateExplained) return;
                if (state == OffenderState.Applied) { if (offenderPid == pid) heldMs = heldTotalMs; return; }
                if (state == OffenderState.Nominated) { if (offenderPid == pid) heldMs = heldTotalMs; return; }
                int s;
                if (strikes.TryGetValue(offenderPid, out s) && s >= MaxStrikes) return;
                pid = offenderPid;
                name = offenderName;
                heldMs = heldTotalMs;
                baselineExplained = explained;
                baselineExcessPerFrame = excessPerFrame;
                baselineStart = started;
                state = OffenderState.Nominated;
                windowsSinceApply = 0;
            }
        }

        internal static Func<uint, DateTime?> StartTimeProbe = null;

        private static bool TryStartTime(uint p, out DateTime t)
        {
            t = DateTime.MinValue;
            Func<uint, DateTime?> probe = StartTimeProbe;
            if (probe != null)
            {
                DateTime? r = probe(p);
                if (!r.HasValue) return false;
                t = r.Value;
                return true;
            }
            try
            {
                using (var proc = System.Diagnostics.Process.GetProcessById((int)p))
                { t = proc.StartTime; return true; }
            }
            catch { return false; }
        }

        public static SuppressionLevel Escalate(int candidatePid, SuppressionLevel desired)
        {
            if (desired == SuppressionLevel.None) return desired;
            uint want;
            DateTime wantStart;
            lock (lk)
            {
                if (!enabled || frozen) return desired;
                if ((uint)candidatePid != pid) return desired;
                if (state != OffenderState.Nominated && state != OffenderState.Applied) return desired;
                want = pid; wantStart = baselineStart;
            }
            DateTime nowStart;
            if (!TryStartTime(want, out nowStart) || nowStart != wantStart)
            {
                lock (lk)
                {
                    if (pid == want) { pid = 0; name = null; state = OffenderState.Idle; }
                }
                return desired;
            }
            lock (lk)
            {
                if (!enabled || frozen || (uint)candidatePid != pid) return desired;
                if (state != OffenderState.Nominated && state != OffenderState.Applied) return desired;
                if (desired >= SuppressionLevel.Isolated)
                {
                    if (state == OffenderState.Nominated) state = OffenderState.Idle;
                    return desired;
                }
                if (state == OffenderState.Nominated)
                {
                    state = OffenderState.Applied;
                    windowsSinceApply = 0;
                    escalations++;
                    Logger.Log(Lang.F("log.frameoffender.1", name ?? "?", candidatePid.ToString(),
                        SuppressionLevelText.Of(desired), SuppressionLevelText.Of(desired + 1)));
                }
                return desired + 1;
            }
        }

        public static void ReportWindow(FrameFaultVerdict v)
        {
            if (v == null) return;
            uint who;
            lock (lk) { who = pid; }
            int frames = v.SlowFrames + v.NormalFrames;
            double perFrame = frames > 0 ? v.TotalExcessMs / frames : 0;
            ReportWindow(v.ExplainedBy(who), perFrame);
        }

        internal static void ReportWindow(double explained, double excessPerFrame)
        {
            lock (lk)
            {
                if (!enabled || frozen || state != OffenderState.Applied) return;
                windowsSinceApply++;
                if (windowsSinceApply < 2) return;
                bool framesBetter = baselineExcessPerFrame <= 0
                    || excessPerFrame <= baselineExcessPerFrame * EffectiveDrop;
                bool contribDown = explained <= baselineExplained * ContribDrop;
                if (framesBetter && contribDown)
                {
                    state = OffenderState.Verified;
                    verified++;
                    Logger.Log(Lang.F("log.frameoffender.2", name ?? "?",
                        (baselineExcessPerFrame).ToString("0.00"), (excessPerFrame).ToString("0.00")));
                    return;
                }
                int s;
                strikes.TryGetValue(pid, out s);
                strikes[pid] = s + 1;
                state = strikes[pid] >= MaxStrikes ? OffenderState.Quarantined : OffenderState.Ineffective;
                ineffective++;
                Logger.Log(Lang.F("log.frameoffender.3", name ?? "?",
                    (baselineExcessPerFrame).ToString("0.00"), (excessPerFrame).ToString("0.00"),
                    state == OffenderState.Quarantined ? Lang.T("t.frameoffender.1") : ""));
                pid = 0; name = null;
            }
        }

        public static string Summarize()
        {
            int esc, ver, inef;
            string who;
            lock (lk) { esc = escalations; ver = verified; inef = ineffective; who = name; }
            if (esc == 0) return null;
            if (ver > 0) return Lang.F("t.frameoffender.2", esc.ToString(), ver.ToString());
            if (inef > 0) return Lang.F("t.frameoffender.3", esc.ToString());
            return Lang.F("t.frameoffender.4", esc.ToString());
        }

        internal static OffenderState StateForTest { get { lock (lk) return state; } }
        internal static uint PidForTest { get { lock (lk) return pid; } }
    }
}
