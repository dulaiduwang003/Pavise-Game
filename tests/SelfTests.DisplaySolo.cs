// All display path counting, topology queries and topology writes are
// injected. No native display access, registry or windows are used.
#if PAVISE_SELFTEST
using System;
using System.Collections.Generic;
using System.Threading;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        private static int displaySoloChecks;

        internal static int RunDisplaySoloRegressionTests()
        {
            Action[] tests =
            {
                DisplaySoloSingleMonitorTouchesNothing,
                DisplaySoloAlreadyInternalTouchesNothing,
                DisplaySoloSnapshotsBeforeSwitchingAndVerifies,
                DisplaySoloVerifyFailureRevertsAndFails,
                DisplaySoloKeepsEarlierSnapshotOnReengage,
                DisplaySoloRestoreSwitchesBackAndClears,
                DisplaySoloRestoreAcceptsUnpluggedSecondDisplay,
                DisplaySoloRestoreAcceptsRejectedWriteOnSingleDisplay,
                DisplaySoloRemoteSessionNeitherSwitchesNorSettles,
                DisplaySoloCorruptSnapshotStaysAsDebt,
                DisplaySoloCrashHealRestores,
                DisplaySoloPrimitiveFailuresFailClosed
            };
            displaySoloChecks = 0;
            foreach (Action test in tests)
            {
                Settings.UseTransientStoreForCurrentProcess();
                Lang.Cur = 0;
                DisplaySolo.ResetForTest();
                try { test(); }
                finally { DisplaySolo.ResetForTest(); }
                Console.WriteLine("PASS " + test.Method.Name);
            }
            Console.WriteLine("PASS display-solo assertions=" + displaySoloChecks
                + " native_display=mocked settings=transient windows_shown=false");
            return tests.Length;
        }

        private static void SoloCheck(bool good, string message)
        {
            if (!good) throw new InvalidOperationException("Display solo regression: " + message);
            Interlocked.Increment(ref displaySoloChecks);
        }

        // 双屏扩展桌面的假件 写入会真的改变后续查询到的拓扑
        private sealed class DisplaySoloFake
        {
            internal int Paths = 2;
            internal uint Topology = DisplaySolo.TopologyExtend;
            internal bool QueryFail, SetFail, IgnoreSet, Remote;
            internal readonly List<uint> Sets = new List<uint>();

            internal void Install()
            {
                DisplaySolo.RemoteForTest = delegate { return Remote; };
                DisplaySolo.PathCountForTest = delegate { return Paths; };
                DisplaySolo.TopologyForTest = delegate(out uint topology)
                { topology = Topology; return !QueryFail; };
                DisplaySolo.SetForTest = delegate(uint topology)
                {
                    if (SetFail) return false;
                    Sets.Add(topology);
                    if (!IgnoreSet) Topology = topology;
                    return true;
                };
            }
        }

        private static void DisplaySoloSingleMonitorTouchesNothing()
        {
            var fake = new DisplaySoloFake { Paths = 1 };
            fake.Install();
            SoloCheck(DisplaySolo.Activate(), "a single display must be a successful no-op");
            SoloCheck(fake.Sets.Count == 0 && !DisplaySolo.HasResidue(),
                "a single display must not be switched or journaled");
        }

        private static void DisplaySoloAlreadyInternalTouchesNothing()
        {
            var fake = new DisplaySoloFake { Topology = DisplaySolo.TopologyInternal };
            fake.Install();
            SoloCheck(DisplaySolo.Activate(), "an already-internal topology must be a successful no-op");
            SoloCheck(fake.Sets.Count == 0 && !DisplaySolo.HasResidue(),
                "an already-internal topology must not be switched or journaled");
        }

        private static void DisplaySoloSnapshotsBeforeSwitchingAndVerifies()
        {
            var fake = new DisplaySoloFake();
            fake.Install();
            SoloCheck(DisplaySolo.Activate(), "switching a dual-extend desktop must succeed");
            SoloCheck(Settings.LoadStr(DisplaySolo.SnapKey, "")
                == DisplaySolo.TopologyExtend.ToString(), "the original topology was not journaled");
            SoloCheck(fake.Sets.Count == 1 && fake.Sets[0] == DisplaySolo.TopologyInternal
                && fake.Topology == DisplaySolo.TopologyInternal,
                "the switch to internal-only did not happen exactly once");
            SoloCheck(DisplaySolo.HasResidue(), "an engaged switch must count as residue");
        }

        private static void DisplaySoloVerifyFailureRevertsAndFails()
        {
            var fake = new DisplaySoloFake { IgnoreSet = true };
            fake.Install();
            SoloCheck(!DisplaySolo.Activate(), "a switch the readback cannot confirm must fail");
            SoloCheck(fake.Sets.Count == 2 && fake.Sets[1] == DisplaySolo.TopologyExtend,
                "an unconfirmed switch must be reverted to the original topology");
            SoloCheck(DisplaySolo.HasResidue(),
                "the journal from a failed switch must stay as the record");
        }

        private static void DisplaySoloRestoreAcceptsRejectedWriteOnSingleDisplay()
        {
            var fake = new DisplaySoloFake();
            fake.Install();
            SoloCheck(DisplaySolo.Activate(), "engage before unplugging");
            // 副屏拔掉后 API 可能直接拒绝写多屏拓扑 与"写成功验不出"同样按单屏收尾
            fake.Paths = 1;
            fake.SetFail = true;
            SoloCheck(DisplaySolo.Restore(), "a rejected restore write on a single display must settle");
            SoloCheck(!DisplaySolo.HasResidue(), "the settled restore must clear the journal");
        }

        private static void DisplaySoloKeepsEarlierSnapshotOnReengage()
        {
            var fake = new DisplaySoloFake();
            fake.Install();
            Settings.SaveStr(DisplaySolo.SnapKey, DisplaySolo.TopologyClone.ToString());
            SoloCheck(DisplaySolo.Activate(), "re-engaging over an unpaid snapshot must still switch");
            SoloCheck(Settings.LoadStr(DisplaySolo.SnapKey, "")
                == DisplaySolo.TopologyClone.ToString(),
                "an earlier snapshot holds the true original and must not be overwritten");
            SoloCheck(DisplaySolo.Restore() && fake.Topology == DisplaySolo.TopologyClone,
                "restore must pay back to the earlier snapshot's topology");
        }

        private static void DisplaySoloRestoreSwitchesBackAndClears()
        {
            var fake = new DisplaySoloFake();
            fake.Install();
            SoloCheck(DisplaySolo.Activate(), "engage before restore");
            SoloCheck(DisplaySolo.Restore(), "restoring a healthy dual desktop must succeed");
            SoloCheck(fake.Topology == DisplaySolo.TopologyExtend && !DisplaySolo.HasResidue(),
                "restore must return to the original topology and clear the journal");
            SoloCheck(DisplaySolo.Restore() && fake.Sets.Count == 2,
                "a second restore must be a no-op");
        }

        private static void DisplaySoloRestoreAcceptsUnpluggedSecondDisplay()
        {
            var fake = new DisplaySoloFake();
            fake.Install();
            SoloCheck(DisplaySolo.Activate(), "engage before unplugging");
            // 副屏拔了 切回扩展验不出扩展 但机器已是单屏 物理世界优先
            fake.Paths = 1;
            fake.IgnoreSet = true;
            SoloCheck(DisplaySolo.Restore(), "restore on a now-single display must settle");
            SoloCheck(!DisplaySolo.HasResidue(), "a settled restore must clear the journal");
        }

        // 远程会话里看到的显示配置说的是远程桌面 不是物理机 既不切换也不结账
        private static void DisplaySoloRemoteSessionNeitherSwitchesNorSettles()
        {
            var fake = new DisplaySoloFake { Remote = true };
            fake.Install();
            SoloCheck(DisplaySolo.Activate() && fake.Sets.Count == 0 && !DisplaySolo.HasResidue(),
                "a remote session must be a no-op for activation");
            // 崩溃残账 + 经 RDP 启动补撤 + 远程只有一条路径:不许按"单屏"把物理机的原拓扑记录清掉
            fake.Remote = false;
            SoloCheck(DisplaySolo.Activate(), "engage on the console for the settle half");
            fake.Remote = true;
            fake.Paths = 1;
            fake.IgnoreSet = true;
            SoloCheck(!DisplaySolo.Restore() && DisplaySolo.HasResidue(),
                "a remote session settled the snapshot against the remote display config");
            // 回到物理机 才能按真实路径数结账
            fake.Remote = false;
            fake.IgnoreSet = false;
            SoloCheck(DisplaySolo.Restore() && !DisplaySolo.HasResidue(),
                "returning to the console must settle normally");
        }

        private static void DisplaySoloCorruptSnapshotStaysAsDebt()
        {
            var fake = new DisplaySoloFake();
            fake.Install();
            Settings.SaveStr(DisplaySolo.SnapKey, "garbage");
            SoloCheck(!DisplaySolo.Restore(), "an unparsable snapshot must not restore blindly");
            SoloCheck(DisplaySolo.HasResidue() && fake.Sets.Count == 0,
                "an unparsable snapshot must stay recorded and never reach the display");
        }

        private static void DisplaySoloPrimitiveFailuresFailClosed()
        {
            var fake = new DisplaySoloFake { QueryFail = true };
            fake.Install();
            SoloCheck(!DisplaySolo.Activate(), "an unreadable topology must fail closed");
            SoloCheck(fake.Sets.Count == 0 && !DisplaySolo.HasResidue(),
                "nothing may be switched or journaled when the topology cannot be read");
            DisplaySolo.ResetForTest();
            fake = new DisplaySoloFake { SetFail = true };
            fake.Install();
            SoloCheck(!DisplaySolo.Activate(), "a rejected topology write must fail");
            SoloCheck(fake.Sets.Count == 0 && DisplaySolo.HasResidue(),
                "the journal written before the rejected switch must stay as the record");
        }

        private static void DisplaySoloCrashHealRestores()
        {
            var fake = new DisplaySoloFake { Topology = DisplaySolo.TopologyInternal };
            fake.Install();
            // 上局崩了 拓扑停在仅内屏 快照里记着原来的扩展
            Settings.SaveStr(DisplaySolo.SnapKey, DisplaySolo.TopologyExtend.ToString());
            SoloCheck(DisplaySolo.HealFromCrash(), "crash healing must settle the leftover switch");
            SoloCheck(fake.Topology == DisplaySolo.TopologyExtend && !DisplaySolo.HasResidue(),
                "crash healing must return to the recorded topology and clear the journal");
        }
    }
}
#endif
