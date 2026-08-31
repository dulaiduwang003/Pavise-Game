// All memory status, quota queries and quota writes are injected. No native
// process access, registry or windows are used.
#if PAVISE_SELFTEST
using System;
using System.IO;
using System.Threading;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        private static int memShieldChecks;
        private const ulong MemGiB = 1024UL * 1024UL * 1024UL;

        internal static int RunMemShieldRegressionTests()
        {
            string root = Path.Combine(Path.GetTempPath(), "PaviseMemShield-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string previousLog = Logger.LogPath;
            Action<string>[] tests =
            {
                MemShieldSmallRamMachineSitsOut,
                MemShieldUnalignedWorkingSetStillEngages,
                MemShieldEngagedSurvivesTransientQueryFailure,
                MemShieldRendererSwapClearsSkip,
                MemShieldGameExitDuringVerifyIsNotAFuse,
                MemShieldPressureGatingNeedsConsecutiveSamples,
                MemShieldNeverOverridesExistingHardMinimum,
                MemShieldEngageJournalsBeforeWritingAndVerifies,
                MemShieldReadbackMismatchFusesAndReverts,
                MemShieldExternalQuotaChangeFuses,
                MemShieldReleaseRestoresOriginalQuota,
                MemShieldForeignSnapshotBlocksEngage,
                MemShieldFailedRestoreKeepsDebt,
                MemShieldCrashRecoveryClearsWhenGameGone,
                MemShieldIdleTouchesNothing
            };
            memShieldChecks = 0;
            try
            {
                foreach (Action<string> test in tests)
                {
                    Settings.UseTransientStoreForCurrentProcess();
                    Logger.ResetWriteBarrierForTest();
                    Logger.LogPath = Path.Combine(root, "decisions.log");
                    Lang.Cur = 0;
                    MemShield.ResetForTest();
                    try { test(root); }
                    finally { MemShield.ResetForTest(); }
                    Console.WriteLine("PASS " + test.Method.Name);
                }
                Console.WriteLine("PASS mem-shield assertions=" + memShieldChecks
                    + " native_memory=mocked settings=transient windows_shown=false");
                return tests.Length;
            }
            finally
            {
                Logger.ResetWriteBarrierForTest(); Logger.LogPath = previousLog;
                Settings.UseTransientStoreForCurrentProcess();
                string actual = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
                string temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
                if (actual.StartsWith(temporary + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    && Path.GetFileName(actual).StartsWith("PaviseMemShield-", StringComparison.Ordinal)
                    && Directory.Exists(actual)) Directory.Delete(actual, true);
            }
        }

        private static void MemCheck(bool good, string message)
        {
            if (!good) throw new InvalidOperationException("Memory residency regression: " + message);
            Interlocked.Increment(ref memShieldChecks);
        }

        // 单目标假件：一台 16GB 机器和一个游戏进程的配额状态
        private sealed class MemShieldFake
        {
            internal ulong Total = 16UL * MemGiB, Avail = 4UL * MemGiB;
            internal bool StatusFail;
            internal int Pid = 100;
            internal long Creation = 111;
            internal ulong Ws = 6UL * MemGiB;
            internal ulong Min = 200UL * 1024, Max = 1380UL * 1024;
            internal uint Flags;
            internal bool DenyOpen, IgnoreSet, ExitAfterSet;
            internal int Queries, Sets;
            internal bool JournalMissingAtSet;

            internal MemShieldFake()
            {
                MemShield.MemoryStatusForTest = delegate(out ulong total, out ulong avail)
                {
                    total = Total; avail = Avail;
                    return !StatusFail;
                };
                MemShield.QueryForTest = delegate(int pid, out bool gone, out long creation,
                    out ulong ws, out ulong min, out ulong max, out uint flags)
                {
                    Queries++;
                    gone = false; creation = 0; ws = 0; min = 0; max = 0; flags = 0;
                    if (pid != Pid || (ExitAfterSet && Sets > 0)) { gone = true; return false; }
                    if (DenyOpen) return false;
                    creation = Creation; ws = Ws; min = Min; max = Max; flags = Flags;
                    return true;
                };
                MemShield.SetForTest = delegate(int pid, long creation, ulong min, ulong max, uint flags)
                {
                    Sets++;
                    if (Settings.LoadStr("MemShieldSnap", "").Length == 0) JournalMissingAtSet = true;
                    if (pid != Pid || creation != Creation || IgnoreSet) return;
                    // 内核按页存配额 回读按页返 假件必须还原这层取整语义
                    //   否则引擎写不对齐的值也能"回读吻合" 真机上却会自熔断
                    Min = min & ~4095UL; Max = max & ~4095UL; Flags = flags;
                };
            }

            internal void Step()
            {
                MemShield.StepForTest(Pid, Creation);
            }

            // 连续三次吃紧采样把护盾推到挂上
            internal void Engage()
            {
                Avail = 2UL * MemGiB;
                for (int i = 0; i < MemShield.EngageSamples; i++) Step();
            }
        }

        // 小内存机器整局旁观 触发时刻恰是系统最需要自由修剪的时刻 锁了反而更抖
        private static void MemShieldSmallRamMachineSitsOut(string root)
        {
            var f = new MemShieldFake();
            f.Total = 8UL * MemGiB;
            f.Avail = 1UL * MemGiB;
            for (int i = 0; i <= MemShield.EngageSamples; i++) f.Step();
            MemCheck(MemShield.Stage == ShieldStage.Skipped && f.Sets == 0 && !MemShield.HasResidue(),
                "a sub-16GB machine must sit out without touching the quota");
        }

        // 真机工作集×0.8 几乎从不页对齐 引擎必须自己按页对齐 否则回读必不等而自熔断
        private static void MemShieldUnalignedWorkingSetStillEngages(string root)
        {
            var f = new MemShieldFake();
            f.Ws = 6UL * MemGiB + 4096UL * 3 + 1234;
            f.Engage();
            MemCheck(MemShield.Stage == ShieldStage.Engaged && !MemShield.Fused && f.Sets == 1,
                "an unaligned working set fused or failed to engage");
            MemCheck(f.Min % 4096 == 0 && f.Min > 0,
                "the pinned minimum must be written page-aligned");
        }

        // 挂载后的瞬时查询失败(局中反作弊翻脸/瞬时错误)只跳过本轮 监控不许静默死亡
        private static void MemShieldEngagedSurvivesTransientQueryFailure(string root)
        {
            var f = new MemShieldFake();
            f.Engage();
            MemCheck(MemShield.Stage == ShieldStage.Engaged, "engage before the transient failure");
            f.DenyOpen = true;
            f.Step();
            MemCheck(MemShield.Stage == ShieldStage.Engaged,
                "a transient query failure demoted an engaged shield to Skipped");
            f.DenyOpen = false;
            // 恢复后监控继续 外力篡改仍要被抓到
            f.Flags = 0;
            f.Step();
            MemCheck(MemShield.Stage == ShieldStage.Fused,
                "monitoring did not resume after the transient failure cleared");
        }

        // 跳过只对当初那个进程粘滞 同局换手(启动器→真渲染进程)要重新评估
        private static void MemShieldRendererSwapClearsSkip(string root)
        {
            var f = new MemShieldFake();
            f.DenyOpen = true;
            f.Avail = 2UL * MemGiB;
            f.Step();
            MemCheck(MemShield.Stage == ShieldStage.Skipped, "the first renderer must be skipped");
            f.DenyOpen = false;
            f.Pid = 200; f.Creation = 222;
            // 换手后的第一次采样要能穿过粘滞门 走真实入口验证
            MemShield.SampleIfDue(true, 200, 222);
            MemCheck(MemShield.Stage == ShieldStage.Observing,
                "the swapped-in renderer did not break the sticky skip");
            for (int i = 0; i < MemShield.EngageSamples; i++) MemShield.StepForTest(200, 222);
            MemCheck(MemShield.Stage == ShieldStage.Engaged && f.Sets == 1,
                "the swapped-in real renderer was never re-evaluated");
        }

        // 写入与回读之间游戏退出是良性时序 配额随进程消亡 不许按机器不采纳记熔断
        private static void MemShieldGameExitDuringVerifyIsNotAFuse(string root)
        {
            var f = new MemShieldFake();
            f.ExitAfterSet = true;
            f.Avail = 2UL * MemGiB;
            for (int i = 0; i < MemShield.EngageSamples; i++) f.Step();
            MemCheck(!MemShield.Fused && f.Sets == 1,
                "a game exiting inside the verify window was fused as a bad machine");
            // 快照必须当场清账 裸留会被下一个渲染进程当成外账 recoveryBlocked 冻到局末
            MemCheck(!MemShield.HasResidue() && !MemShield.RecoveryBlockedForTest,
                "the benign-exit path left its own snapshot behind as foreign debt");
        }

        private static void MemShieldPressureGatingNeedsConsecutiveSamples(string root)
        {
            var f = new MemShieldFake();
            f.Avail = 4UL * MemGiB;
            f.Step();
            MemCheck(MemShield.Stage == ShieldStage.Idle && f.Sets == 0,
                "plentiful memory started an observation or a write");
            f.Avail = 2UL * MemGiB;
            f.Step(); f.Step();
            MemCheck(MemShield.Stage == ShieldStage.Observing && f.Sets == 0,
                "two pressured samples were enough to write a quota");
            // 中途缓解要清零计数 重新从头数
            f.Avail = 4UL * MemGiB;
            f.Step();
            f.Avail = 2UL * MemGiB;
            f.Step(); f.Step();
            MemCheck(f.Sets == 0, "a relieved sample did not reset the pressure run");
            f.Step();
            MemCheck(MemShield.Stage == ShieldStage.Engaged && f.Sets == 1,
                "three consecutive pressured samples did not engage exactly once");
        }

        private static void MemShieldNeverOverridesExistingHardMinimum(string root)
        {
            var f = new MemShieldFake();
            f.Flags = MemShield.HardMinEnable;
            f.Avail = 2UL * MemGiB;
            f.Step();
            MemCheck(MemShield.Stage == ShieldStage.Skipped && f.Sets == 0
                && !MemShield.HasResidue(),
                "an externally owned hard minimum was overridden or journaled");

            // 内存状态读不到是瞬时情况 不落任何结论
            MemShield.ResetForTest();
            var status = new MemShieldFake();
            status.StatusFail = true; status.Avail = 2UL * MemGiB;
            status.Step();
            MemCheck(MemShield.Stage == ShieldStage.Idle && status.Sets == 0,
                "a failed memory status advanced the state machine");

            // 句柄被拒（多半反作弊）是本局跳过 不熔断
            MemShield.ResetForTest();
            var denied = new MemShieldFake();
            denied.DenyOpen = true; denied.Avail = 2UL * MemGiB;
            denied.Step();
            MemCheck(MemShield.Stage == ShieldStage.Skipped && denied.Sets == 0 && !MemShield.Fused,
                "a denied process handle was not treated as a per-match skip");
        }

        private static void MemShieldEngageJournalsBeforeWritingAndVerifies(string root)
        {
            var f = new MemShieldFake();
            f.Engage();
            // 引擎按页对齐后写入 期望值同样按页取整
            ulong expected = (ulong)(6UL * MemGiB * MemShield.PinFactor) & ~4095UL;
            MemCheck(MemShield.Stage == ShieldStage.Engaged && !f.JournalMissingAtSet
                && f.Min == expected && (f.Flags & MemShield.HardMinEnable) != 0
                && (f.Flags & MemShield.HardMaxDisable) != 0 && f.Max >= expected,
                "engage wrote before journaling, or wrote unexpected quota values");
            MemCheck(MemShield.HasResidue() && MemShield.Summarize() != null,
                "an engaged shield reported no residue or no summary");
            // 已挂上后再采样只核实 不重写
            int sets = f.Sets;
            f.Step();
            MemCheck(f.Sets == sets && MemShield.Stage == ShieldStage.Engaged,
                "a healthy engaged shield rewrote its quota");
        }

        private static void MemShieldReadbackMismatchFusesAndReverts(string root)
        {
            var f = new MemShieldFake();
            f.IgnoreSet = true;
            f.Engage();
            MemCheck(MemShield.Stage == ShieldStage.Fused && MemShield.Fused,
                "an unhonoured quota write did not fuse");
            MemCheck(Settings.LoadStr("MemShieldSnap", "") == "" && !MemShield.HasResidue(),
                "a fused engage kept its recovery journal despite nothing to undo");
            // 熔断后不再尝试
            int sets = f.Sets;
            f.Step();
            MemCheck(f.Sets == sets, "a fused shield attempted another write");
            MemShield.ClearFuse();
            MemCheck(!MemShield.Fused && MemShield.Stage == ShieldStage.Idle,
                "clearing the fuse did not restore the idle stage");
        }

        private static void MemShieldExternalQuotaChangeFuses(string root)
        {
            var f = new MemShieldFake();
            f.Engage();
            MemCheck(MemShield.Stage == ShieldStage.Engaged, "external-change fixture failed to engage");
            f.Flags = 0; // 外力清掉了硬下限
            f.Step();
            MemCheck(MemShield.Stage == ShieldStage.Fused && MemShield.Fused
                && Settings.LoadStr("MemShieldSnap", "") == "",
                "an externally cleared quota was not fused and settled");
        }

        private static void MemShieldReleaseRestoresOriginalQuota(string root)
        {
            var f = new MemShieldFake();
            f.Engage();
            MemCheck(MemShield.Release() && f.Min == 200UL * 1024 && f.Max == 1380UL * 1024
                && (f.Flags & MemShield.HardMinEnable) == 0,
                "release did not restore the original working-set quota");
            MemCheck(!MemShield.HasResidue() && MemShield.Stage == ShieldStage.Idle
                && MemShield.Summarize() == null && Settings.LoadStr("MemShieldSnap", "") == "",
                "a released shield kept residue, summary or its journal");
        }

        private static void MemShieldForeignSnapshotBlocksEngage(string root)
        {
            var f = new MemShieldFake();
            MemCheck(Settings.SaveStr("MemShieldSnap", "1|999|5|204800|1413120|0"),
                "foreign snapshot fixture could not seed the journal");
            f.Engage();
            MemCheck(f.Sets == 0 && MemShield.RecoveryBlockedForTest && MemShield.HasResidue(),
                "an unresolved recovery record did not block a new quota write");
            // 记录属于已消失的进程 释放路径应当收尾并解除封锁
            MemCheck(MemShield.Release() && !MemShield.HasResidue()
                && Settings.LoadStr("MemShieldSnap", "") == "",
                "a record for a vanished process could not be settled");
        }

        private static void MemShieldFailedRestoreKeepsDebt(string root)
        {
            var f = new MemShieldFake();
            f.Engage();
            MemShield.RestoreForTest = delegate { return false; };
            MemCheck(!MemShield.Release() && MemShield.RecoveryBlockedForTest && MemShield.HasResidue(),
                "a denied restoration was reported as success");
            int sets = f.Sets;
            f.Step();
            MemCheck(f.Sets == sets, "a blocked recovery still allowed new writes");
            MemShield.RestoreForTest = delegate { return true; };
            MemCheck(MemShield.Release() && !MemShield.HasResidue(),
                "a recovered restoration path could not settle the debt");
        }

        private static void MemShieldCrashRecoveryClearsWhenGameGone(string root)
        {
            var f = new MemShieldFake();
            string snap = "1|" + f.Pid + "|" + f.Creation + "|204800|1413120|0";
            MemCheck(Settings.SaveStr("MemShieldSnap", snap), "crash fixture could not seed the journal");
            f.Pid = 555; // 原进程已不在
            MemCheck(MemShield.HealFromCrash() && !MemShield.HasResidue(),
                "a crash record for an exited game was not cleared");
            MemCheck(Settings.SaveStr("MemShieldSnap", "garbage")
                && !MemShield.Release() && MemShield.RecoveryBlockedForTest,
                "a malformed crash record was silently discarded");
        }

        private static void MemShieldIdleTouchesNothing(string root)
        {
            // 未启用时每秒都会走这条路 不许碰任何原语（假件未注入 触碰即抛）
            MemShield.ResetForTest();
            MemShield.SampleIfDue(false, 0, 0);
            MemShield.SampleIfDue(false, 1234, 5);
            MemCheck(MemShield.Stage == ShieldStage.Idle && !MemShield.HasResidue(),
                "an idle shield changed state without being enabled");
        }
    }
}
#endif
