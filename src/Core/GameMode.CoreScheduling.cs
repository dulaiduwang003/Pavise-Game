using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private CoreIsolationClient coreIsolation;
        private bool isolationBlocked;

        private bool EnsureManualIsolation(int pid, long creation, ulong gameMask)
        {
            PolicySnapshot snapshot = sessionPolicy;
            if (snapshot == null || !snapshot.ManualPlacement || !snapshot.CorePlan.IsolationOn) return true;
            if (stopping || isolationBlocked) return false;
            if (CoreScheduling.Validate(snapshot.CorePlan) != null || snapshot.CorePlan.GameMask != gameMask)
            {
                isolationBlocked = true;
                CoreIsolationClient.State = "schedule.isolation.failed";
                Logger.Warn(Lang.T("schedule.isolation.failed") + " (invalid-session-plan)");
                return false;
            }
            if (coreIsolation == null) coreIsolation = new CoreIsolationClient();
            if (coreIsolation.Ensure(snapshot.CorePlan.IsolationMask, pid, creation, gameMask)) return true;
            isolationBlocked = true;
            return false;
        }

        // Hard affinity is an add-on to exclusive cores, off by default
        //   CPU Sets are only a hint to the scheduler, threads that set their own hard affinity aren't constrained
        //   When on, write affinity once more to already-suppressed background processes to really keep them out of the exclusive range
        //   Cost per the v2.0 measurements: with background squeezed into a narrow range, the game waits longer on its locks
        //   Hence off by default and only applied when exclusivity has actually taken effect
        private void ApplyBackgroundHardAffinity()
        {
            ulong target = HardAffinityTarget();
            if (target == 0)
            {
                if (core.SqueezedCount(SuppressReason.Background) > 0)
                    core.ClearSqueezes(SuppressReason.Background);
                return;
            }
            foreach (int pid in core.PidsWith(SuppressReason.Background))
            {
                long creation = core.CreationOf(pid);
                if (creation <= 0) continue;
                string name = core.NameOf(pid);
                if (string.IsNullOrEmpty(name)) continue;
                bool changed;
                core.SetSqueeze(pid, creation, name, target, SuppressReason.Background, out changed);
            }
        }

        // Outside the exclusive range is what background can use, returns 0 when exclusivity hasn't really taken effect, meaning no write this pass
        //   Leave at least two logical cores, otherwise background can't even be scheduled, worse than no isolation
        private ulong HardAffinityTarget()
        {
            if (!hardAffinityOn || stopping) return 0;
            PolicySnapshot snapshot = sessionPolicy;
            if (snapshot == null || !snapshot.CorePlan.IsolationOn) return 0;
            if (CoreIsolationClient.State != "schedule.isolation.active") return 0;
            ulong all = CpuTopology.AllMask;
            ulong outside = all & ~CoreIsolationClient.ActiveMask;
            return outside != 0 && CpuTopology.CountSetBits(outside) >= 2 ? outside : 0;
        }

        private bool StopCoreIsolation()
        {
            bool clean = coreIsolation == null || coreIsolation.Stop();
            if (clean) { coreIsolation = null; isolationBlocked = false; }
            return clean;
        }

        private void RollBackUnconfirmedIsolation()
        {
            PolicySnapshot snapshot = sessionPolicy;
            if (snapshot == null || !snapshot.CorePlan.IsolationOn || isolationBlocked) return;
            bool clean = StopCoreIsolation();
            isolationBlocked = true;
            CoreIsolationClient.State = clean ? "schedule.isolation.failed" : "schedule.isolation.pending";
            Logger.Warn(Lang.T(CoreIsolationClient.State) + " (game-placement-unconfirmed)");
        }

#if PAVISE_SELFTEST
        internal void ProbeUseIsolationWorker(CoreSchedulingPlan plan)
        {
            Settings.SaveStr(CoreScheduling.Key, plan.Encode());
            sessionPolicy = PolicyResolver.Global();
            coreIsolation = new CoreIsolationClient(); coreIsolation.UseTestWorker();
        }
        internal bool ProbeStopIsolationWorker() { return StopCoreIsolation(); }
        // Called only by standalone integration tests on child processes they launched, normal self-tests never start real processes
        internal bool ProbeManualPlacement(IntPtr handle, int pid, long creation, ulong original, ulong target)
        {
            uint[] ids = Native.QueryCpuSets(handle);
            if (ids == null) return false;
            gameBoost[pid] = new Snap { Creation = creation, Aff = original, CpuSets = ids, Name = "core-test-child" };
            var pass = new BoostPass { ManualPlacement = true, UseStrict = true, DesiredMask = target,
                RendererPid = pid, RendererCreation = creation, RendererName = "core-test-child" };
            string text; bool verified;
            return ApplyPlacementStage(handle, pid, pass, true, true, out text, out verified) && verified;
        }

        internal bool ProbeRestoreManualPlacement(int pid, bool includeManual)
        {
            return RestoreIrqProofHardPin(IntPtr.Zero, pid, includeManual);
        }
#endif
        // Shares sync with per-game save, UI must pass the global version used to validate the plan
        internal string SaveCoreScheduling(CoreSchedulingPlan plan, string expectedGlobal,
            string profileId, bool followGlobal, string expectedProfile)
        {
            lock (sync)
            {
                if (stopping || ProfileStoreSaveFailed) return Lang.T("schedule.error.save");
                CoreSchedulingPlan global = CoreScheduling.LoadGlobal();
                if (CoreScheduling.GlobalToken() != expectedGlobal || expectedGlobal == "unreadable")
                    return Lang.T("schedule.error.changed");
                if (profileId == null)
                {
                    string error = CoreScheduling.Validate(plan);
                    if (error != null) return Lang.T(error);
                    if (!Settings.SaveStr(CoreScheduling.Key, plan.Encode())) return Lang.T("schedule.error.save");
                    corePartitionOn = false;
                    coreDomainAltOn = false;
                    if (!active) CpuTopology.SetCustomMask(plan.GameMask);
                }
                else
                {
                    GameProfile current = FindProfileLocked(profileId);
                    if (current == null) return Lang.T("schedule.error.save");
                    if (CoreScheduling.ProfileToken(current) != expectedProfile) return Lang.T("schedule.error.changed");
                    GameProfile replacement = current.Clone();
                    foreach (string key in CoreScheduling.PlacementKeys) replacement.Overrides.Remove(key);
                    replacement.Overrides.Remove(CoreScheduling.Key);
                    if (!followGlobal)
                    {
                        CoreSchedulingPlan local = plan.Clone();
                        local.IsolationOn = global.IsolationOn;
                        local.IsolationMask = global.IsolationMask;
                        string error = CoreScheduling.Validate(local);
                        if (error != null) return Lang.T(error);
                        // Global isolation isn't copied into the game's persisted override
                        local.IsolationOn = false;
                        local.IsolationMask = 0;
                        replacement.Overrides[CoreScheduling.Key] = local.Encode();
                    }
                    var next = new List<GameProfile>(profiles);
                    int index = profiles.IndexOf(current);
                    next[index] = replacement;
                    if (!SaveProfileSnapshotLocked(next)) return Lang.T("schedule.error.save");
                    profiles[index] = replacement;
                }
            }
            RequestPolicyApply();
            return null;
        }
    }
}
