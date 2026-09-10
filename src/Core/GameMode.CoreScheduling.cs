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

        // 硬亲和是核心独占的附加项 默认关
        //   CPU Set 只是给调度器的提示 自己设过硬亲和的线程不受约束
        //   打开后给已压制的后台再写一遍亲和 把它们真正挡在独占范围之外
        //   代价见 v2.0 的实测结论 后台被挤到窄范围时 游戏等它的锁会等更久
        //   所以默认关 且只在独占确实生效时才动手
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

        // 独占范围之外就是后台可用的核 独占没真生效就返回 0 表示这轮不写
        //   至少留两颗逻辑核 否则后台连调度都排不开 比不隔离更糟
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
        // 仅独立集成测试对自己启动的子进程调用，不由正常自测启动真实进程。
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
        // 与逐游戏保存共用 sync。UI 必须带上用于校验方案的全局版本。
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
                        // 全局隔离不复制进游戏的持久化覆盖。
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
