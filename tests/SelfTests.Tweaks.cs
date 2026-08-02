// @author bdth 2074055628@qq.com
// 文件用途 驱动调优键位映射与网卡清单编解码的自测

using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        private static void TestDrsKeyIdMapping()
        {
            Eq(NvApi.SettingPreferredPState, NvDrsTweaks.SettingIdOf(NvDrsTweaks.KeyPState));
            Eq(NvApi.SettingFrlFps, NvDrsTweaks.SettingIdOf(NvDrsTweaks.KeyFrl));
            Eq(0x007BA09Eu, NvDrsTweaks.SettingIdOf(NvDrsTweaks.KeyPreRender));
            Eq(0x0005F543u, NvDrsTweaks.SettingIdOf(NvDrsTweaks.KeyLowLatCpl));
            var ids = new HashSet<uint>
            {
                NvDrsTweaks.SettingIdOf(NvDrsTweaks.KeyPState),
                NvDrsTweaks.SettingIdOf(NvDrsTweaks.KeyFrl),
                NvDrsTweaks.SettingIdOf(NvDrsTweaks.KeyPreRender),
                NvDrsTweaks.SettingIdOf(NvDrsTweaks.KeyLowLatCpl)
            };
            Eq(4, ids.Count);
        }

        private static void TestDrsSnapshotRoundtrip()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { NvDrsTweaks.KeyPState, "1" },
                { NvDrsTweaks.KeyFrl, "absent" },
                { NvDrsTweaks.KeyPreRender, "2" },
                { NvDrsTweaks.KeyLowLatCpl, "absent" }
            };
            var back = NvDrsTweaks.ParseSnapshot(NvDrsTweaks.SerializeSnapshot(map));
            Eq(4, back.Count);
            foreach (var kv in map) Eq(kv.Value, back[kv.Key]);
        }

        private static void TestNagleListCodec()
        {
            Eq(0, NagleTweak.ParseList("").Length);
            Eq(0, NagleTweak.ParseList(null).Length);
            string[] two = NagleTweak.ParseList("{aaa};{bbb}");
            Eq(2, two.Length);
            Eq("{aaa}", two[0]);
            Eq("{bbb}", two[1]);
        }

        private static void TestRenderLaneIdentifiesBusyThread()
        {
            using (var stop = new System.Threading.ManualResetEvent(false))
            {
                var busy = new System.Threading.Thread(delegate ()
                {
                    while (!stop.WaitOne(0)) { }
                });
                busy.IsBackground = true;
                busy.Start();
                try
                {
                    RenderLane.Candidate best;
                    int self = System.Diagnostics.Process.GetCurrentProcess().Id;
                    if (!RenderLane.TryIdentify(self, out best))
                        throw new Exception("thread identification failed on the test process itself");
                    if (best.Share < RenderLane.MinDominantShare)
                        throw new Exception("the spinning thread did not dominate: share=" + best.Share.ToString("F2"));
                }
                finally { stop.Set(); busy.Join(2000); }
            }
        }

        private static void TestRenderLaneJournalCodec()
        {
            int pid, tid, pri; long creation;
            Eq(false, RenderLane.ParseJournal("", out pid, out creation, out tid, out pri));
            Eq(false, RenderLane.ParseJournal("1|2|3", out pid, out creation, out tid, out pri));
            Eq(false, RenderLane.ParseJournal("0|2|3|4", out pid, out creation, out tid, out pri));
            Eq(true, RenderLane.ParseJournal("1234|99887766|4321|1", out pid, out creation, out tid, out pri));
            Eq(1234, pid);
            Eq(99887766L, creation);
            Eq(4321, tid);
            Eq(1, pri);
        }

        private static void TestIfeoSandboxRoundtrip()
        {
            string sandbox = @"Software\PaviseTest\IFEO_" + System.Diagnostics.Process.GetCurrentProcess().Id;
            IfeoBoost.Hive = Microsoft.Win32.Registry.CurrentUser;
            IfeoBoost.RootOverride = sandbox;
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(sandbox)) { }
                IfeoBoost.EnsureForGame("probe");
                using (var p = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(sandbox + @"\probe.exe\PerfOptions"))
                {
                    if (p == null) throw new Exception("IFEO PerfOptions key was not created");
                    Eq(3, (int)p.GetValue("CpuPriorityClass", -1));
                }
                IfeoBoost.EnsureForGame("probe.exe");
                Eq(1, IfeoBoost.ParseList(Settings.LoadStr("IfeoList", "")).Length);
                Eq(true, IfeoBoost.RestoreAll());
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(sandbox + @"\probe.exe"))
                    if (k != null) throw new Exception("IFEO exe key left behind after restore");
                Eq(0, IfeoBoost.ParseList(Settings.LoadStr("IfeoList", "")).Length);
            }
            finally
            {
                IfeoBoost.Hive = Microsoft.Win32.Registry.LocalMachine;
                IfeoBoost.RootOverride = null;
                try { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(sandbox, false); } catch { }
            }
        }
    }
}
