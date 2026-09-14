// @author bdth 2074055628@qq.com
// File purpose Suppression core apply partial: handle-level write, verify, restore and non-writable neutralization
using System;
using System.Collections.Generic;
using System.Threading;

namespace PaviseApp
{
    internal sealed partial class SuppressionCore
    {
        private bool ThrottleMatches(IntPtr h, SuppressionLevel level, uint originalPriority, ulong originalAffinity,
            uint[] originalCpuSets, int desiredGpu, bool antiCheat, ulong desiredAffinity, bool affinityOwned = false)
        {
            uint desiredPriority = DesiredPriority(level, originalPriority, antiCheat);
            if (Native.GetPriorityClass(h) != desiredPriority) return false;
            if (desiredGpu >= 0)
            {
                int gpuCur;
                if (Native.D3DKMTGetProcessSchedulingPriorityClass(h, out gpuCur) == 0
                    && gpuCur != desiredGpu) return false;
            }
            int desiredIo = DesiredIoPriority(level);
            int desiredPage = DesiredPagePriority(level, antiCheat);
            if (Native.QueryIoPriority(h) != desiredIo || Native.QueryPagePriority(h) != desiredPage) return false;

            if (Native.PowerThrottlingSupported)
            {
                int qosControl, qosState;
                if (!Native.TryQueryPowerThrottling(h, out qosControl, out qosState)
                    || (qosControl & 1) == 0 || (qosState & 1) == 0) return false;
            }

            // Anti-cheat runs on explicit placement; background no longer pins cores, only restores limits left by older versions
            if (!Native.CpuSetsMatch(h, originalCpuSets ?? new uint[0])) return false;
            // Entries whose placement was refused leave affinity to the process itself; no longer used to judge drift
            if (!CpuTopology.MultiGroup && !affinityOwned && Native.QueryAffinity(h) != desiredAffinity) return false;
            return true;
        }

        // The strong anti-cheat suppression level dropped the starvation part; scanning anti-cheats like ACE suspend game threads while scanning game memory
        //   IDLE priority gives it almost no time slice under full CPU load, stretching the suspend window from hundreds of ms to seconds; the player sees a freeze
        //   Timer resolution capping amplifies its throttle sleeps a dozen-fold, page priority 1 makes the scan page-fault all the way; all the same amplifier
        //   The priority boost kept in Apply only rescues lock waits, not suspended threads, so it can't be fed this hard
        //   Very-low disk IO stays, disk scanning is the main harm; EcoQoS and E-core frequency cap stay, the core ingredients of suppression are unchanged
        internal static uint DesiredPriority(SuppressionLevel level, uint originalPriority, bool antiCheat)
        {
            uint desired = originalPriority == 0 || originalPriority == uint.MaxValue
                ? Native.NORMAL_PRIORITY_CLASS : originalPriority;
            if (level >= SuppressionLevel.Restrained)
                desired = level >= SuppressionLevel.Isolated && !antiCheat
                    ? Native.IDLE_PRIORITY_CLASS : Native.BELOW_NORMAL_PRIORITY_CLASS;
            return desired;
        }

        internal static int DesiredIoPriority(SuppressionLevel level)
        {
            return level >= SuppressionLevel.Isolated ? 0 : 1;
        }

        internal static int DesiredPagePriority(SuppressionLevel level, bool antiCheat)
        {
            return level >= SuppressionLevel.Isolated && !antiCheat ? 1 : 3;
        }

        internal static bool DesiredTimerSeal(SuppressionLevel level, bool antiCheat)
        {
            return level >= SuppressionLevel.Isolated && !antiCheat;
        }

        internal static int DesiredGpuClass(bool demoteEnabled, SuppressReason reasons,
            SuppressionLevel backgroundLevel, int origGpu)
        {
            if (origGpu < 0) return -1;
            if (!demoteEnabled || (reasons & SuppressReason.Background) == 0
                || backgroundLevel < SuppressionLevel.Restrained) return origGpu;
            return backgroundLevel >= SuppressionLevel.Isolated
                ? Native.GpuPriorityIdle : Native.GpuPriorityBelowNormal;
        }

        // Anti-cheat uses explicit placement; plain background and old heavy-load records return to original affinity
        private ulong DesiredAffinityOf(Entry e)
        {
            return SuppressionAffinityPolicy.DesiredAffinity(
                e.Reasons, e.SqueezeAff, e.OrigAff, allMask, BackgroundPinsAllowed);
        }

        // Patrol rewrite comes through here; when placement was refused and affinity is the only failing step, drop the placement and keep the other knobs
        //   Anti-cheat processes reset their own affinity then refuse the write; without giving up it backs off to the cap then logs one exception a minute
        private bool ApplyEntryLocked(IntPtr h, Entry e, int pid)
        {
            bool applied = ApplyThrottle(h, e.Level, e.OrigPri, e.OrigAff, e.OrigCpuSets, DesiredGpu(e),
                e.OrigBoost, AntiCheatThrottled(e), DesiredAffinityOf(e), e.SqueezeRefused);
            if (applied || e.SqueezeAff == 0 || !AffinityOnlyFailure(LastApplyError)) return applied;
            e.SqueezeAff = 0;
            e.SqueezeRefused = true;
            Logger.Log(Lang.T("log.suppressioncore.squeeze.1") + e.Name + " pid " + pid
                + Lang.T("log.suppressioncore.squeeze.2"));
            return ApplyThrottle(h, e.Level, e.OrigPri, e.OrigAff, e.OrigCpuSets, DesiredGpu(e),
                e.OrigBoost, AntiCheatThrottled(e), DesiredAffinityOf(e), true);
        }

        // Give up when an anti-cheat entry won't take writes: it self-protects, retrying only spams the log; what was written reverts to original, what won't revert retries at match end
        private bool GiveUpAntiCheatLocked(IntPtr h, int pid, Entry e)
        {
            if (e == null || e.GaveUp || e.OrigPri == uint.MaxValue) return false;
            if ((e.Reasons & SuppressReason.AntiCheat) == 0 || (e.Reasons & SuppressReason.Background) != 0) return false;
            string detail = LastApplyError;
            bool restored = RunMutation(delegate
            {
                return RestoreValues(h, e.OrigPri, e.OrigAff, e.OrigIo, e.OrigPg, allMask,
                    e.OrigCpuSets, e.OrigQoSControl, e.OrigQoSState, e.OrigGpu, e.OrigBoost);
            });
            e.GaveUp = true;
            e.GaveUpRestored = restored;
            e.Applied = false;
            e.SqueezeAff = 0;
            e.NextReconcileTicks = long.MaxValue;
            Logger.Log(Lang.T("log.suppressioncore.giveup.1") + e.Name + " pid " + pid
                + Lang.T("log.suppressioncore.giveup.2") + (string.IsNullOrEmpty(detail) ? "" : detail)
                + Lang.T(restored ? "log.suppressioncore.giveup.3" : "log.suppressioncore.giveup.4"));
            return true;
        }

        internal static bool AffinityOnlyFailure(string detail)
        {
            if (string.IsNullOrEmpty(detail)) return false;
            foreach (string step in detail.Split(','))
                if (step != "affinity-write" && step != "affinity-readback") return false;
            return true;
        }

        // With hardware scheduling on, the process scheduling class belongs to the GPU; writing does nothing and logs a gpu-write failure
        private static int DesiredGpu(Entry e)
        {
            return DesiredGpuClass(GpuDemoteEnabled && !HagsTweak.SchedulingActiveCached(),
                e.Reasons, e.BackgroundLevel, e.OrigGpu);
        }

        private static void ScheduleAfterMatch(Entry e, int pid)
        {
            e.ReconcileFailures = 0;
            if (e.FastReconcileRemaining > 0)
            {
                e.FastReconcileRemaining--;
                e.NextReconcileTicks = DateTime.UtcNow.AddSeconds(
                    e.FastReconcileRemaining > 0 ? ReconcileFastSeconds
                    : ReconcileStableBaseSeconds + PositiveMod(pid, ReconcileStableJitterSeconds)).Ticks;
                return;
            }
            e.NextReconcileTicks = DateTime.UtcNow.AddSeconds(
                ReconcileStableBaseSeconds + PositiveMod(pid, ReconcileStableJitterSeconds)).Ticks;
        }

        private static void ScheduleAfterApply(Entry e, bool applied, int pid)
        {
            if (applied)
            {
                e.ReconcileFailures = 0;
                e.FastReconcileRemaining = 2;
                e.NextReconcileTicks = DateTime.UtcNow.AddSeconds(ReconcileFastSeconds).Ticks;
                return;
            }
            if (e.ReconcileFailures < 8) e.ReconcileFailures++;
            int seconds = ReconcileFastSeconds;
            for (int i = 1; i < e.ReconcileFailures && seconds < ReconcileFailureCapSeconds; i++)
                seconds = Math.Min(ReconcileFailureCapSeconds, seconds * 2);
            e.NextReconcileTicks = DateTime.UtcNow.AddSeconds(seconds).Ticks;
        }

        private static int PositiveMod(int value, int modulo)
        {
            int result = value % modulo;
            return result < 0 ? result + modulo : result;
        }

        private bool ApplyThrottle(IntPtr h, SuppressionLevel level, uint originalPriority, ulong originalAffinity,
            uint[] originalCpuSets, int desiredGpu, int origBoost, bool antiCheat, ulong desiredAffinity,
            bool affinityOwned = false)
        {
            BeginMutation();
            try
            {
            Interlocked.Increment(ref applyOperations);
            var failed = new List<string>();
            uint desiredPriority = DesiredPriority(level, originalPriority, antiCheat);
            if (Native.GetPriorityClass(h) != desiredPriority)
                if (!Native.SetPriorityClass(h, desiredPriority)) failed.Add("priority-write");
            if (desiredGpu >= 0)
            {
                int gpuCur;
                if (Native.D3DKMTGetProcessSchedulingPriorityClass(h, out gpuCur) == 0 && gpuCur != desiredGpu)
                {
                    if (Native.D3DKMTSetProcessSchedulingPriorityClass(h, desiredGpu) != 0) failed.Add("gpu-write");
                    else if (Native.D3DKMTGetProcessSchedulingPriorityClass(h, out gpuCur) != 0
                        || gpuCur != desiredGpu) failed.Add("gpu-readback");
                }
            }

            // Anti-cheat runs on explicit placement; background no longer pins cores, only restores limits left by older versions
            if (!Native.CpuSetsMatch(h, originalCpuSets)
                && !Native.RestoreCpuSetsVerified(h, originalCpuSets))
                failed.Add("cpu-sets-restore");
            // Entries whose placement was refused leave affinity to the process itself; writing just gets refused again, other knobs as usual
            if (!CpuTopology.MultiGroup && !affinityOwned)
            {
                ulong originalAllowed = originalAffinity != 0 ? originalAffinity : allMask;
                bool squeezing = desiredAffinity != originalAllowed;
                if (Native.QueryAffinity(h) != desiredAffinity
                    && !Native.SetProcessAffinityMask(h, (UIntPtr)desiredAffinity))
                    failed.Add(squeezing ? "affinity-write" : "affinity-restore");
                if (Native.QueryAffinity(h) != desiredAffinity)
                    failed.Add(squeezing ? "affinity-readback" : "affinity-restore-readback");
            }
            int io = DesiredIoPriority(level);
            if (Native.QueryIoPriority(h) != io
                && !Native.TrySetIoPriority(h, io))
                failed.Add("io-write");
            int pg = DesiredPagePriority(level, antiCheat);
            if (Native.QueryPagePriority(h) != pg
                && !Native.TrySetPagePriority(h, pg))
                failed.Add("page-write");
            if (Native.PowerThrottlingSupported)
            {
                bool sealTimer = DesiredTimerSeal(level, antiCheat);
                uint want = Native.EcoQoSWantMask(sealTimer);
                int qosControl;
                int qosState;
                bool queried = Native.TryQueryPowerThrottling(h, out qosControl, out qosState);
                bool overSealed = queried && !sealTimer && ((uint)qosControl & 4u) != 0;
                if ((!queried || ((uint)qosControl & want) != want || ((uint)qosState & want) != want || overSealed)
                    && !Native.ApplyEcoQoS(h, sealTimer))
                    failed.Add("eco-write");
            }
            // Since 2.0.1 isolation no longer disables dynamic priority boost; only restores what earlier versions turned off
            //   That's Windows' quick relief for priority inversion; when the game waits on an isolated process's lock it instantly lifts it to finish
            //   With it off only the once-a-second anti-starvation fallback remains; the user sees occasional ~1s whole-frame freezes
            //   Boost is one to eight levels; lifted from IDLE it still sits below the game's HIGH, can't steal a core the game is using
            if (origBoost == 0)
            {
                int boostNow = Native.QueryBoostDisabled(h);
                if (boostNow == 1 && !Native.TrySetBoostDisabled(h, false))
                    failed.Add("boost-unwrite");
            }
            if (Native.GetPriorityClass(h) != desiredPriority) failed.Add("priority-readback");
            if (Native.QueryIoPriority(h) != io) failed.Add("io-readback");
            if (Native.QueryPagePriority(h) != pg) failed.Add("page-readback");
            if (Native.PowerThrottlingSupported && !EcoStateVisible(h)) failed.Add("eco-readback");
            LastApplyError = string.Join(",", failed.ToArray());
            // Trim after the whole set of knobs has landed; anti-cheat processes are not trimmed
            //   A scanning process whose pages were dropped just re-faults, which only makes the scan longer
            if (failed.Count == 0 && !antiCheat) WsTrim.MaybeTrim(h);
            return failed.Count == 0;
            }
            finally { EndMutation(); }
        }

        private static bool EcoStateVisible(IntPtr h)
        {
            for (int attempt = 0; ; attempt++)
            {
                int qosControl, qosState;
                if (Native.TryQueryPowerThrottling(h, out qosControl, out qosState)
                    && (qosControl & 1) != 0 && (qosState & 1) != 0) return true;
                if (attempt >= 80) return false;
                if (attempt < 77) Thread.SpinWait(1000);
                else Thread.Sleep(1);
            }
        }

        public static bool RestoreValues(IntPtr h, uint pri, ulong aff, int io, int pg, ulong allMask)
        {
            return RestoreValues(h, pri, aff, io, pg, allMask, new uint[0]);
        }

        public static bool RestoreValues(IntPtr h, uint pri, ulong aff, int io, int pg, ulong allMask,
            uint[] cpuSets)
        {

            return RestoreValues(h, pri, aff, io, pg, allMask, cpuSets, -1, -1);
        }

        public static bool RestoreValues(IntPtr h, uint pri, ulong aff, int io, int pg, ulong allMask,
            uint[] cpuSets, int qosControl, int qosState)
        {
            return RestoreValues(h, pri, aff, io, pg, allMask, cpuSets, qosControl, qosState, -1);
        }

        public static bool RestoreValues(IntPtr h, uint pri, ulong aff, int io, int pg, ulong allMask,
            uint[] cpuSets, int qosControl, int qosState, int gpu)
        {
            return RestoreValues(h, pri, aff, io, pg, allMask, cpuSets, qosControl, qosState, gpu, -1);
        }

        public static bool RestoreValues(IntPtr h, uint pri, ulong aff, int io, int pg, ulong allMask,
            uint[] cpuSets, int qosControl, int qosState, int gpu, int boost)
        {
            bool ok = Native.RestoreCpuSetsVerified(h, cpuSets);
            uint desiredPriority = pri == 0 || pri == uint.MaxValue ? Native.NORMAL_PRIORITY_CLASS : pri;
            ok &= Native.SetPriorityClass(h, desiredPriority);
            ulong desiredAffinity = aff != 0 ? aff : allMask;
            // Skip the write when already at the original value; a process that resets its own affinity and refuses writes must not be judged a restore failure
            if (!CpuTopology.MultiGroup && Native.QueryAffinity(h) != desiredAffinity)
                ok &= Native.SetProcessAffinityMask(h, (UIntPtr)desiredAffinity);
            int rio = io >= 0 ? io : 2; ok &= Native.TrySetIoPriority(h, rio);
            int rpg = pg >= 0 ? pg : 5; ok &= Native.TrySetPagePriority(h, rpg);
            if (Native.PowerThrottlingSupported)
                ok &= Native.RestorePowerThrottling(h, qosControl, qosState);
            if (boost >= 0) ok &= Native.TrySetBoostDisabled(h, boost == 1);
            ok &= Native.GetPriorityClass(h) == desiredPriority;
            ok &= Native.QueryIoPriority(h) == rio;
            ok &= Native.QueryPagePriority(h) == rpg;
            if (!CpuTopology.MultiGroup) ok &= Native.QueryAffinity(h) == desiredAffinity;
            if (gpu >= 0)
            {
                int gpuCur;
                if (Native.D3DKMTGetProcessSchedulingPriorityClass(h, out gpuCur) == 0 && gpuCur != gpu)
                {
                    Native.D3DKMTSetProcessSchedulingPriorityClass(h, gpu);
                    ok &= Native.D3DKMTGetProcessSchedulingPriorityClass(h, out gpuCur) == 0 && gpuCur == gpu;
                }
            }
            return ok;
        }

        private RestoreResult RestoreOne(int pid, Entry e)
        {
            IntPtr h = Native.OpenProcess(Native.PROCESS_SET_INFORMATION | Native.PROCESS_SET_LIMITED_INFORMATION
                | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero)
            {
                IntPtr hq = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (hq == IntPtr.Zero)
                    return Native.LastOpenProcessFailureWasNoSuchProcess()
                        ? RestoreResult.Gone
                        : RestoreResult.Protected;
                try
                {
                    if (!Native.StillActive(hq)) return RestoreResult.Gone;
                    string nm = Native.ImageName(hq);
                    if (nm != null && !SameName(nm, e.Name)) return RestoreResult.Gone;
                    long creation, cpu; ulong io;
                    if (e.Creation > 0
                        && Native.QueryProcessSample(hq, out creation, out cpu, out io)
                        && creation != e.Creation)
                        return RestoreResult.Gone;
                    return RestoreResult.Protected;
                }
                finally { Native.CloseHandle(hq); }
            }
            try
            {
                if (!Native.StillActive(h)) return RestoreResult.Gone;
                if (e.Creation <= 0) return RestoreResult.Protected;
                if (e.Name != null)
                {
                    string cur = Native.ImageName(h);
                    if (cur == null) return RestoreResult.Protected;
                    if (!SameName(cur, e.Name)) return RestoreResult.Gone;
                }
                long creation, cpu; ulong io;
                if (e.Creation > 0)
                {
                    if (!Native.QueryProcessSample(h, out creation, out cpu, out io)) return RestoreResult.Protected;
                    if (creation != e.Creation) return RestoreResult.Gone;
                }
                if (RunMutation(delegate
                    {
                        return RestoreValues(h, e.OrigPri, e.OrigAff, e.OrigIo, e.OrigPg,
                            allMask, e.OrigCpuSets, e.OrigQoSControl, e.OrigQoSState,
                            e.OrigGpu, e.OrigBoost);
                    }))
                    return RestoreResult.Restored;
                return Native.StillActive(h) ? RestoreResult.Protected : RestoreResult.Gone;
            }
            finally { Native.CloseHandle(h); }
        }

        private static bool SameProcess(IntPtr h, Entry e)
        {
            if (e.Creation <= 0) return false;
            if (e.Name != null)
            {
                string cur = Native.ImageName(h);
                if (cur == null || !SameName(cur, e.Name)) return false;
            }
            if (e.Creation > 0)
            {
                long creation, cpu; ulong io;
                if (!Native.QueryProcessSample(h, out creation, out cpu, out io)) return false;
                if (creation != e.Creation) return false;
            }
            return true;
        }

        private static bool SameName(string a, string b)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        internal const string SelfProtectedDetail = "self-protected";

        internal static bool FullyBlockedDetail(string detail)
        {
            return detail != null
                && detail.Contains("priority-write")
                && detail.Contains("io-write")
                && detail.Contains("page-write");
        }

        internal static bool SnapshotMatchesCurrent(IntPtr h, uint pri, ulong aff, int io, int pg,
            uint[] cpuSets, int qosControl, int qosState, int gpu, int boost)
        {
            if (!SnapshotMatchesCurrent(h, pri, aff, io, pg, cpuSets, qosControl, qosState, gpu)) return false;
            if (boost >= 0)
            {
                int now = Native.QueryBoostDisabled(h);
                if (now >= 0 && now != boost) return false;
            }
            return true;
        }

        internal static bool SnapshotMatchesCurrent(IntPtr h, uint pri, ulong aff, int io, int pg,
            uint[] cpuSets, int qosControl, int qosState, int gpu)
        {
            if (pri == 0 || pri == uint.MaxValue || io < 0 || pg < 0) return false;
            if (Native.GetPriorityClass(h) != pri) return false;
            if (Native.QueryIoPriority(h) != io) return false;
            if (Native.QueryPagePriority(h) != pg) return false;
            if (!CpuTopology.MultiGroup && Native.QueryAffinity(h) != aff) return false;
            if (!Native.CpuSetsMatch(h, cpuSets ?? new uint[0])) return false;
            if (qosControl >= 0 && Native.PowerThrottlingSupported)
            {
                int qc, qs;
                if (!Native.TryQueryPowerThrottling(h, out qc, out qs)
                    || qc != qosControl || qs != qosState) return false;
            }
            if (gpu >= 0)
            {
                int g;
                if (Native.D3DKMTGetProcessSchedulingPriorityClass(h, out g) != 0 || g != gpu) return false;
            }
            return true;
        }

        private bool TryNeutralizeUnwritableLocked(IntPtr h, int pid, Entry e)
        {
            if (e == null || e.OrigPri == uint.MaxValue) return false;
            if (!FullyBlockedDetail(LastApplyError)) return false;
            BeginMutation();
            try
            {
                if (Native.PowerThrottlingSupported)
                    Native.RestorePowerThrottling(h, e.OrigQoSControl, e.OrigQoSState);
                if (e.OrigBoost == 0) Native.TrySetBoostDisabled(h, false);
                if (e.OrigGpu >= 0)
                {
                    int gpuCur;
                    if (Native.D3DKMTGetProcessSchedulingPriorityClass(h, out gpuCur) == 0 && gpuCur != e.OrigGpu)
                        Native.D3DKMTSetProcessSchedulingPriorityClass(h, e.OrigGpu);
                }
                if (!SnapshotMatchesCurrent(h, e.OrigPri, e.OrigAff, e.OrigIo, e.OrigPg,
                        e.OrigCpuSets, e.OrigQoSControl, e.OrigQoSState, e.OrigGpu))
                    return false;

                e.OrigPri = uint.MaxValue;
                e.Applied = false;
                e.NextRetryTicks = 0;
                PersistJournalLocked();
                bool newlyListed = SelfProtectedRoster.Mark(e.Name);
                if (newlyListed || ShouldLogProtected(e.Name + "-unwritable"))
                    Logger.Log(Lang.T("log.suppressioncoreapply.1") + e.Name + " pid " + pid
                        + Lang.T("log.suppressioncoreapply.2")
                        + (newlyListed ? Lang.T("log.suppressioncoreapply.3") : ""));
                return true;
            }
            finally { EndMutation(); }
        }
    }
}
