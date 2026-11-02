using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Diagnostics;

namespace PaviseApp
{
    internal sealed class AntiCheatAffinityGameContext
    {
        internal readonly int Pid;
        internal readonly long Birth, Session;
        internal readonly int Group;
        internal readonly ulong ProtectedMask, GameDomainMask;
        internal readonly HashSet<int> GameAncestors;
        internal AntiCheatAffinityGameContext(int pid, long birth, long session, int group, ulong protectedMask, ulong gameDomainMask, HashSet<int> gameAncestors = null)
        { Pid = pid; Birth = birth; Session = session; Group = group; ProtectedMask = protectedMask; GameDomainMask = gameDomainMask; GameAncestors = gameAncestors; }
        internal string Key { get { return Pid + ":" + Birth + ":" + Session; } }
    }

    internal sealed class AntiCheatAffinityTarget
    {
        internal int Pid;
        internal long Birth;
        internal string Name, Group;
        internal string Key { get { return Pid + ":" + Birth; } }
    }

    // The worker owns mutable state. UI readers receive a newly published snapshot.
    internal sealed class AntiCheatAffinityView
    {
        internal readonly string Group, Name, Code, Detail, Kind;
        internal readonly ushort ProcessorGroup;
        internal readonly ulong Mask;
        internal readonly bool Bound, Pending;
        internal readonly int Pid;
        internal AntiCheatAffinityView(AntiCheatAffinityTarget target, string code, string detail, bool bound, bool pending, ushort group, ulong mask, string kind)
        { Group = target.Group; Name = target.Name; Pid = target.Pid; Code = code; Detail = detail; Bound = bound; Pending = pending; ProcessorGroup = group; Mask = mask; Kind = kind; }
    }

    internal sealed class AntiCheatAffinityRuntime
    {
        internal delegate bool Identify(int pid, long creation, string name, out AntiCheatAffinityIdentity identity, out string reason);
        internal delegate bool Inspect(AntiCheatAffinityIdentity identity, out ushort group, out ulong mask, out string reason);
        private sealed class TargetState
        {
            internal AntiCheatAffinityTarget Target;
            internal AntiCheatAffinityIdentity Identity;
            internal AntiCheatAffinityChoice Choice;
            internal ushort Group;
            internal ulong Allowed, Protected, Domain;
            internal bool Prepared;
            internal string Failure;
            internal AntiCheatAffinityResult Halted;
            internal bool Admitted;
        }
        private readonly AntiCheatAffinityLease lease;
        private readonly Func<AntiCheatAffinitySnapshot> capture;
        private readonly Identify identify;
        private readonly Inspect inspect;
        private readonly Func<int, long, string, bool> prepare;
        private readonly Func<AntiCheatAffinityGameContext, bool> gameAlive;
        private readonly Func<AntiCheatAffinityTarget, AntiCheatAffinityGameContext, string> admission;
        private readonly Func<long> nowMilliseconds;
        private readonly Dictionary<string, TargetState> targets = new Dictionary<string, TargetState>(StringComparer.Ordinal);
        private volatile AntiCheatAffinityView[] views = new AntiCheatAffinityView[0];
        private AntiCheatAffinitySnapshot topology;
        private string session;
        private bool initialized;
        internal bool PendingRecovery { get { return lease.PendingRecovery; } }
        private long capturedAt;

        internal AntiCheatAffinityRuntime(Func<int, long, string, bool> prepare, Func<AntiCheatAffinityTarget, AntiCheatAffinityGameContext, string> admission)
            : this(new AntiCheatAffinityLease(), AntiCheatAffinityTopology.Capture,
                  AntiCheatAffinityLease.TryIdentify, AntiCheatAffinityLease.TryInspect, prepare, GameAlive, admission) { }
        internal AntiCheatAffinityRuntime(AntiCheatAffinityLease lease, Func<AntiCheatAffinitySnapshot> capture,
            Identify identify, Inspect inspect, Func<int, long, string, bool> prepare, Func<AntiCheatAffinityGameContext, bool> gameAlive,
            Func<AntiCheatAffinityTarget, AntiCheatAffinityGameContext, string> admission = null, Func<long> nowMilliseconds = null)
        { this.lease = lease; this.capture = capture; this.identify = identify; this.inspect = inspect; this.prepare = prepare; this.gameAlive = gameAlive; this.admission = admission; this.nowMilliseconds = nowMilliseconds ?? ClockMilliseconds; }

        private static long ClockMilliseconds() { return (long)(Stopwatch.GetTimestamp() * (1000.0 / Stopwatch.Frequency)); }

        private static bool GameAlive(AntiCheatAffinityGameContext context)
        {
            if (context == null || context.Pid <= 0 || context.Birth <= 0) return false;
            IntPtr handle = Native.OpenProcess(4096, false, context.Pid);
            if (handle == IntPtr.Zero) return false;
            try { return Native.QueryProcessIdentityState(handle, context.Birth) == 0; }
            finally { Native.CloseHandle(handle); }
        }

        internal void Tick(AntiCheatAffinityGameContext context, IList<AntiCheatAffinityTarget> current)
        {
            if (!initialized) { lease.Recover(); initialized = true; }
            if (context == null || !gameAlive(context)) { Stop(); return; }
            if (session != context.Key)
            {
                if (!Stop()) return;
                session = context.Key;
                topology = capture();
                capturedAt = nowMilliseconds();
            }
            long elapsed = nowMilliseconds() - capturedAt;
            bool refreshed = elapsed < 0 || elapsed >= 30000;
            if (refreshed)
            {
                topology = capture(); capturedAt = nowMilliseconds();
                // Re-evaluate previously unavailable topology after parking or
                // allocation changes; failed process access stays stopped.
                foreach (TargetState retry in targets.Values)
                    if (retry.Choice != null && !retry.Choice.Applicable && retry.Halted == null)
                    { retry.Choice = null; retry.Failure = null; }
            }
            HashSet<string> present = new HashSet<string>(current.Select(t => t.Key), StringComparer.Ordinal);
            foreach (string key in new List<string>(targets.Keys))
            {
                TargetState old = targets[key];
                if (present.Contains(key)) continue;
                if (old.Identity == null || lease.Release(old.Identity)) targets.Remove(key);
            }
            List<AntiCheatAffinityView> next = new List<AntiCheatAffinityView>();
            foreach (AntiCheatAffinityTarget target in current)
            {
                // A game process must never be admitted through an anti-cheat name collision.
                if (target.Pid == context.Pid) continue;
                TargetState state;
                if (!targets.TryGetValue(target.Key, out state))
                { state = new TargetState { Target = target }; targets.Add(target.Key, state); }
                if (context.GameAncestors != null && context.GameAncestors.Contains(target.Pid))
                {
                    bool restored = state.Identity == null || lease.Release(state.Identity);
                    state.Halted = new AntiCheatAffinityResult("inheritance-risk", "game-process-or-ancestor", false, !restored);
                }
                if (state.Halted != null)
                {
                    if (state.Halted.PendingRecovery && state.Identity != null && lease.Release(state.Identity))
                        state.Halted = new AntiCheatAffinityResult(state.Halted.Code, state.Halted.Detail, false, false);
                    next.Add(View(state, state.Halted.Code, state.Halted.Detail, false, state.Halted.PendingRecovery)); continue;
                }
                ulong protectedMask = context.Group == state.Group ? context.ProtectedMask : 0;
                ulong domainMask = context.Group == state.Group ? context.GameDomainMask : 0;
                if (state.Choice != null && (state.Protected != protectedMask || state.Domain != domainMask
                    || (refreshed && state.Choice.Applicable && !PoolStillAvailable(topology, state, protectedMask))))
                {
                    if (!lease.Release(state.Identity)) { next.Add(View(state, "recovery-pending", "game-layout-changed", false, true)); continue; }
                    state.Choice = null; state.Failure = null;
                    topology = capture(); capturedAt = nowMilliseconds();
                }
                if (state.Failure != null) { next.Add(View(state, "unavailable", state.Failure, false, false)); continue; }
                string reason;
                if (state.Identity == null && !identify(target.Pid, target.Birth, target.Name, out state.Identity, out reason))
                { state.Failure = reason; next.Add(View(state, "unavailable", reason, false, false)); continue; }
                if (!state.Admitted)
                {
                    reason = admission == null ? null : admission(target, context);
                    if (!string.IsNullOrEmpty(reason))
                    { state.Failure = reason; next.Add(View(state, "unavailable", reason, false, false)); continue; }
                    state.Admitted = true;
                }
                if (state.Choice == null)
                {
                    if (!state.Prepared)
                    {
                        if (!prepare(target.Pid, target.Birth, target.Name))
                        { next.Add(View(state, "handoff-pending", "previous-placement-recovery", false, false)); continue; }
                        state.Prepared = true;
                    }
                    if (!inspect(state.Identity, out state.Group, out state.Allowed, out reason))
                    { state.Failure = reason; next.Add(View(state, "unavailable", reason, false, false)); continue; }
                    state.Protected = context.Group == state.Group ? context.ProtectedMask : 0;
                    state.Domain = context.Group == state.Group ? context.GameDomainMask : 0;
                    state.Choice = AntiCheatAffinityTopology.Select(topology, state.Group, state.Allowed, state.Protected, state.Domain);
                    if (!state.Choice.Applicable)
                    { state.Failure = state.Choice.Reason; next.Add(View(state, "unavailable", state.Failure, false, false)); continue; }
                }
                AntiCheatAffinityResult result = lease.Ensure(state.Identity, state.Group, state.Choice.Mask);
                if (!result.Bound && !result.PendingRecovery && result.Code != "already-matched") state.Halted = result;
                next.Add(View(state, result.Code, result.Detail, result.Bound, result.PendingRecovery));
            }
            foreach (TargetState pending in targets.Values)
                if (!present.Contains(pending.Target.Key)) next.Add(View(pending, "recovery-pending", "process-release-pending", false, true));
            Publish(next.ToArray());
        }

        private static AntiCheatAffinityView View(TargetState state, string code, string detail, bool bound, bool pending)
        { return new AntiCheatAffinityView(state.Target, code, detail, bound, pending, state.Group,
            state.Choice == null ? 0 : state.Choice.Mask, state.Choice == null ? "none" : state.Choice.Kind); }

        private static bool PoolStillAvailable(AntiCheatAffinitySnapshot snapshot, TargetState state, ulong protectedMask)
        {
            if (snapshot == null || !snapshot.Valid || !snapshot.CpuSetMetadata) return false;
            ulong covered = 0;
            foreach (AntiCheatAffinityCore core in snapshot.Cores)
            {
                if (core.Group != state.Group || (core.Mask & state.Choice.Mask) == 0) continue;
                if ((core.Mask & state.Choice.Mask) != core.Mask || core.Allocated || (core.Mask & protectedMask) != 0) return false;
                // Parking an idle, already admitted core does not revoke this
                // placement. Rebinding on each idle parking transition would churn.
                covered |= core.Mask;
            }
            return covered == state.Choice.Mask;
        }

        private void Publish(AntiCheatAffinityView[] next)
        {
            string previous = string.Join(";", views.Select(v => v.Group + ":" + v.Name + ":" + v.Code + ":" + v.Detail + ":" + v.Mask).ToArray());
            string current = string.Join(";", next.Select(v => v.Group + ":" + v.Name + ":" + v.Code + ":" + v.Detail + ":" + v.Mask).ToArray());
            views = next;
            if (previous != current)
                foreach (AntiCheatAffinityView view in next)
                    Logger.Info("AC AFFINITY " + view.Name + " pid=" + view.Pid + " " + view.Code + " " + view.Detail
                        + " group=" + view.ProcessorGroup + " mask=0x" + view.Mask.ToString("X", CultureInfo.InvariantCulture));
        }

        internal bool Stop()
        {
            bool ok = lease.ReleaseAll();
            if (ok) { targets.Clear(); topology = null; session = null; Publish(new AntiCheatAffinityView[0]); }
            else Publish(targets.Values.Select(t => View(t, "recovery-pending", "restoration-unverified", false, true)).ToArray());
            return ok;
        }

        internal string Summary(string group)
        {
            AntiCheatAffinityView[] rows = views.Where(v => v.Group == group).ToArray();
            if (rows.Length == 0) return "";
            if (rows.Any(v => v.Pending)) return PerformancePolicy.Text("硬绑恢复待核验", "Affinity recovery pending");
            int bound = rows.Count(v => v.Bound);
            if (bound > 0) return PerformancePolicy.Text("硬绑 ", "Hard bound ") + bound
                + (rows.Length > bound ? PerformancePolicy.Text(" · 部分未执行", " · partial") : "");
            if (rows.Any(v => v.Code == "conflict")) return PerformancePolicy.Text("亲和性冲突", "Affinity conflict");
            if (rows.All(v => v.Code == "already-matched")) return PerformancePolicy.Text("已有目标亲和性", "Target affinity already set");
            return PerformancePolicy.Text("硬绑未执行", "Affinity unavailable");
        }

        internal string Details(string group)
        {
            return string.Join("\r\n", views.Where(v => v.Group == group).Select(v => v.Name + " (PID " + v.Pid + "): "
                + (v.Bound ? PerformancePolicy.Text("硬亲和性已回读", "Hard affinity verified") : Explain(v.Code, v.Detail))
                + (v.Mask == 0 ? "" : " · G" + v.ProcessorGroup + " CPU " + string.Join(",", Enumerable.Range(0,64).Where(i => (v.Mask & (1UL << i)) != 0).Select(i => i.ToString(CultureInfo.InvariantCulture)).ToArray()))
                + " · " + KindText(v.Kind)).ToArray());
        }

        private static string KindText(string kind)
        {
            if (kind == "efficiency") return PerformancePolicy.Text("能效核池", "Efficiency cores");
            if (kind == "auxiliary") return PerformancePolicy.Text("辅助物理核", "Auxiliary physical cores");
            if (kind == "alternate-cache-domain") return PerformancePolicy.Text("其他共享缓存域", "Alternate shared-cache domain");
            return PerformancePolicy.Text("尚无目标核池", "No selected core pool");
        }

        private static string Explain(string code, string detail)
        {
            if (code == "conflict") return PerformancePolicy.Text("亲和性持续被改写，本局停止纠偏", "Repeated affinity conflict; corrections stopped for this session");
            if (code == "recovery-pending") return PerformancePolicy.Text("原亲和性恢复待核验", "Original affinity restoration is unverified");
            if (code == "handoff-pending") return PerformancePolicy.Text("等待原核心分配恢复", "Waiting for earlier placement recovery");
            if (code == "already-matched") return PerformancePolicy.Text("已有目标亲和性，未接管恢复", "Target affinity already set; restoration not acquired");
            if (detail == "game-process-or-ancestor" || code == "inheritance-risk") return PerformancePolicy.Text("游戏进程或其祖先，避免亲和性继承", "Game process or ancestor; avoiding affinity inheritance");
            if (detail == "launcher-helper-or-unknown-role" || detail == "not-session-zero-service") return PerformancePolicy.Text("启动器或非独立服务进程，保留原分配", "Launcher or non-service process; preserving placement");
            if (detail == "shared-or-unverified-service-host" || detail == "process-not-in-running-services" || detail == "running-services-unavailable") return PerformancePolicy.Text("无法核验独立反作弊服务身份", "Dedicated anti-cheat service identity could not be verified");
            if (detail == "insufficient-game-core-capacity") return PerformancePolicy.Text("剩余游戏物理核不足", "Insufficient remaining physical cores for the game");
            if (detail == "insufficient-unreserved-cores") return PerformancePolicy.Text("可用辅助物理核不足", "Insufficient available auxiliary physical cores");
            if (detail == "alternate-cache-domain-unavailable") return PerformancePolicy.Text("其他缓存域当前停放或不可用", "Alternate cache domain is parked or unavailable");
            return PerformancePolicy.Text("当前条件未执行硬绑", "Hard affinity unavailable under current conditions") + " (" + detail + ")";
        }
    }
}
