// 文件用途 显示路径计数 拓扑查询和拓扑写入全是注入的
// 不碰原生显示接口 注册表和窗口
// 对局单屏已下架 这里只回归崩溃残账的还原路径 快照由测试直接播种
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
                DisplaySoloRestoreSwitchesBackAndClears,
                DisplaySoloRestoreAcceptsUnpluggedSecondDisplay,
                DisplaySoloRestoreAcceptsRejectedWriteOnSingleDisplay,
                DisplaySoloRemoteSessionDoesNotSettle,
                DisplaySoloCorruptSnapshotStaysAsDebt,
                DisplaySoloCrashHealRestores
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

        // 旧版本对局中崩溃后的机器假件 拓扑停在仅内屏 写入会真的改变后续查询到的拓扑
        private sealed class DisplaySoloFake
        {
            internal int Paths = 1;
            internal uint Topology = DisplaySolo.TopologyInternal;
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

        // 快照播种 = 旧版本单屏激活期间崩溃留下的残账
        private static void SeedCrashSnapshot(uint original)
        {
            Settings.SaveStr(DisplaySolo.SnapKey, original.ToString());
        }

        private static void DisplaySoloRestoreSwitchesBackAndClears()
        {
            var fake = new DisplaySoloFake { Paths = 2 };
            fake.Install();
            SeedCrashSnapshot(DisplaySolo.TopologyExtend);
            SoloCheck(DisplaySolo.Restore(), "restoring a healthy dual desktop must succeed");
            SoloCheck(fake.Topology == DisplaySolo.TopologyExtend && !DisplaySolo.HasResidue(),
                "restore must return to the recorded topology and clear the journal");
            SoloCheck(DisplaySolo.Restore() && fake.Sets.Count == 1,
                "a second restore must be a no-op");
        }

        private static void DisplaySoloRestoreAcceptsUnpluggedSecondDisplay()
        {
            // 副屏拔了 切回扩展验不出扩展 但机器已是单屏 物理世界优先
            var fake = new DisplaySoloFake { IgnoreSet = true };
            fake.Install();
            SeedCrashSnapshot(DisplaySolo.TopologyExtend);
            SoloCheck(DisplaySolo.Restore(), "restore on a now-single display must settle");
            SoloCheck(!DisplaySolo.HasResidue(), "a settled restore must clear the journal");
        }

        private static void DisplaySoloRestoreAcceptsRejectedWriteOnSingleDisplay()
        {
            // 副屏拔掉后 API 可能直接拒绝写多屏拓扑 与"写成功验不出"同样按单屏收尾
            var fake = new DisplaySoloFake { SetFail = true };
            fake.Install();
            SeedCrashSnapshot(DisplaySolo.TopologyExtend);
            SoloCheck(DisplaySolo.Restore(), "a rejected restore write on a single display must settle");
            SoloCheck(!DisplaySolo.HasResidue(), "the settled restore must clear the journal");
        }

        // 崩溃残账 + 经 RDP 启动补撤 + 远程只有一条路径:不许按"单屏"把物理机的原拓扑记录清掉
        private static void DisplaySoloRemoteSessionDoesNotSettle()
        {
            var fake = new DisplaySoloFake { Remote = true, IgnoreSet = true };
            fake.Install();
            SeedCrashSnapshot(DisplaySolo.TopologyExtend);
            SoloCheck(!DisplaySolo.Restore() && DisplaySolo.HasResidue(),
                "a remote session settled the snapshot against the remote display config");
            // 回到物理机 才能按真实路径数结账
            fake.Remote = false;
            fake.IgnoreSet = false;
            fake.Paths = 2;
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

        private static void DisplaySoloCrashHealRestores()
        {
            var fake = new DisplaySoloFake { Paths = 2 };
            fake.Install();
            SeedCrashSnapshot(DisplaySolo.TopologyExtend);
            SoloCheck(DisplaySolo.HealFromCrash(), "crash healing must settle the leftover switch");
            SoloCheck(fake.Topology == DisplaySolo.TopologyExtend && !DisplaySolo.HasResidue(),
                "crash healing must return to the recorded topology and clear the journal");
        }
    }
}
#endif
