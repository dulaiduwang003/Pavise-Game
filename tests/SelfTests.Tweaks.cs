// @author bdth 2074055628@qq.com
// 文件用途 驱动调优键位映射与网卡清单编解码的自测

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

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

        private static void TestGameDescendantsExemption()
        {
            var parents = new Dictionary<int, int>
            {
                { 200, 100 }, { 300, 200 }, { 500, 400 }, { 600, 100 }, { 700, 600 }
            };
            var roots = new HashSet<int> { 100 };
            HashSet<int> got = GameMode.WalkDescendants(parents, roots, 600, 24);

            Eq(true, got.Contains(200));
            Eq(true, got.Contains(300));
            Eq(false, got.Contains(100));
            Eq(false, got.Contains(500));
            Eq(false, got.Contains(600));
            Eq(false, got.Contains(700));
            Eq(2, got.Count);

            var cycle = new Dictionary<int, int> { { 1000, 1100 }, { 1100, 1000 } };
            GameMode.WalkDescendants(cycle, new HashSet<int> { 9900 }, 0, 24);
            Eq(0, GameMode.WalkDescendants(null, roots, 0, 24).Count);
            Eq(0, GameMode.WalkDescendants(parents, new HashSet<int>(), 0, 24).Count);
        }

        private static void TestBoostClearsEfficiencyMode()
        {
            if (!Native.PowerThrottlingSupported) Skip("power throttling unavailable");
            using (Process probe = Process.Start(new ProcessStartInfo("cmd.exe", "/c pause")
            { UseShellExecute = false, RedirectStandardInput = true, CreateNoWindow = true }))
            {
                Thread.Sleep(250);
                IntPtr h = Native.OpenProcess(
                    Native.PROCESS_SET_INFORMATION | Native.PROCESS_SET_LIMITED_INFORMATION
                    | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, probe.Id);
                if (h == IntPtr.Zero) Skip("cannot open the probe process");
                try
                {
                    Native.ApplyEcoQoS(h);
                    Eq(true, WaitQoSState(h, false));

                    Native.ApplyHighQoS(h, Native.OsBuild() >= 22000);
                    Eq(true, WaitQoSState(h, true));
                }
                finally
                {
                    Native.CloseHandle(h);
                    try { probe.StandardInput.Close(); if (!probe.WaitForExit(3000)) probe.Kill(); } catch { }
                }
            }
        }

        private static bool WaitQoSState(IntPtr h, bool expectCleared)
        {
            bool ok = GameMode.HighQoSVerified(h) == expectCleared;
            for (int i = 0; !ok && i < 40; i++)
            {
                Thread.Sleep(25);
                ok = GameMode.HighQoSVerified(h) == expectCleared;
            }
            return ok;
        }

        private static void TestNetThrottleRangeJudgement()
        {
            Eq(10, NetTweak.SystemDefault);
            int? cur = NetTweak.Current();
            bool needs = NetTweak.NeedsRepair();
            if (!cur.HasValue) Eq(false, needs);
            else Eq(cur.Value < 1 || cur.Value > 70, needs);
            if (string.IsNullOrEmpty(NetTweak.Describe()))
                throw new Exception("net throttle description was empty");
        }

        private static void TestDevicePowerBitMerge()
        {
            Eq(0x08, DevicePowerTweak.Merge(null, true));
            Eq(0x18, DevicePowerTweak.Merge(0x10, true));
            Eq(0x18, DevicePowerTweak.Merge(0x18, true));
            Eq(0x10, DevicePowerTweak.Merge(0x18, false));
            Eq(0, DevicePowerTweak.Merge(0x08, false));
        }

        private static void TestRetiredFeaturesRegistry()
        {
            Eq(0, MsiModeTweak.ParseList("").Length);
            Eq(2, MsiModeTweak.ParseList("a;b").Length);

            int count = 0;
            foreach (RetiredFeature f in RetiredFeatures.Entries)
            {
                count++;
                if (string.IsNullOrEmpty(f.Name))
                    throw new Exception("retired feature had an empty name");
                if (string.IsNullOrEmpty(f.RemovedIn))
                    throw new Exception("retired feature missing removal version: " + f.Name);
                if (string.IsNullOrEmpty(f.Reason))
                    throw new Exception("retired feature missing reason: " + f.Name);
                f.HasResidue();
            }
            Eq(true, count >= 3);
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

        private static void TestIfeoWritesFullPerfOptionsTriple()
        {
            string sandbox = @"Software\PaviseTest\IFEO3_" + System.Diagnostics.Process.GetCurrentProcess().Id;
            IfeoBoost.Hive = Microsoft.Win32.Registry.CurrentUser;
            IfeoBoost.RootOverride = sandbox;
            IfeoBoost.ClearArmed();
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(sandbox)) { }
                IfeoBoost.EnsureForGame("triple");
                using (var p = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(sandbox + @"\triple.exe\PerfOptions"))
                {
                    if (p == null) throw new Exception("PerfOptions key was not created");
                    Eq(3, (int)p.GetValue("CpuPriorityClass", -1));
                    Eq(3, (int)p.GetValue("IoPriority", -1));
                    Eq(5, (int)p.GetValue("PagePriority", -1));
                }
                Eq(true, IfeoBoost.RestoreAll());
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(sandbox + @"\triple.exe"))
                    if (k != null) throw new Exception("all three values must be removed, key left behind");
            }
            finally
            {
                IfeoBoost.Hive = Microsoft.Win32.Registry.LocalMachine;
                IfeoBoost.RootOverride = null;
                IfeoBoost.ClearArmed();
                try { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(sandbox, false); } catch { }
            }
        }

        private static void TestIfeoPreArmAppliesBeforeGameStarts()
        {
            string sandbox = @"Software\PaviseTest\IFEOArm_" + System.Diagnostics.Process.GetCurrentProcess().Id;
            IfeoBoost.Hive = Microsoft.Win32.Registry.CurrentUser;
            IfeoBoost.RootOverride = sandbox;
            IfeoBoost.ClearArmed();
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(sandbox)) { }
                Eq(true, IfeoBoost.Arm("armed"));
                Eq(false, IfeoBoost.Arm("armed.exe"));
                Eq(1, IfeoBoost.Armed().Length);

                using (var p = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(sandbox + @"\armed.exe"))
                    if (p != null) throw new Exception("pre-arm must start from a clean key");

                Eq(1, IfeoBoost.PreArmAll());
                using (var p = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(sandbox + @"\armed.exe\PerfOptions"))
                {
                    if (p == null) throw new Exception("pre-arm did not create PerfOptions");
                    Eq(3, (int)p.GetValue("CpuPriorityClass", -1));
                }
                Eq(true, IfeoBoost.RestoreAll());
                Eq(1, IfeoBoost.ClearArmed());
                Eq(0, IfeoBoost.Armed().Length);
                Eq(0, IfeoBoost.PreArmAll());
            }
            finally
            {
                IfeoBoost.Hive = Microsoft.Win32.Registry.LocalMachine;
                IfeoBoost.RootOverride = null;
                IfeoBoost.ClearArmed();
                try { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(sandbox, false); } catch { }
            }
        }

        private static void TestLegacyPurgeKeepsDataWhenRestoreFails()
        {
            string dir = Path.Combine(Path.GetTempPath(), "PavisePurge_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            LegacyPurge.SkipRegistryDelete = true;
            try
            {
                string games = Path.Combine(dir, "Pavise.games.txt");
                string white = Path.Combine(dir, "Pavise.whitelist.txt");
                File.WriteAllText(games, "game-a");
                File.WriteAllText(white, "white-a");

                Settings.Save("PurgeV180Done", false);
                LegacyPurge.RestoreHook = delegate { return new List<string> { "电源计划", "IFEO" }; };
                LegacyPurge.RunOnce(dir);

                if (!File.Exists(games) || !File.Exists(white))
                    throw new Exception("restore failed but data was deleted anyway");
                Eq(false, Settings.Load("PurgeV180Done", false));

                LegacyPurge.RestoreHook = delegate { return new List<string>(); };
                LegacyPurge.RunOnce(dir);
                if (File.Exists(games) || File.Exists(white))
                    throw new Exception("restore succeeded but data was not purged");
                Eq(true, Settings.Load("PurgeV180Done", false));

                File.WriteAllText(games, "re-added-by-user");
                LegacyPurge.RestoreHook = delegate { throw new Exception("must not restore twice"); };
                LegacyPurge.RunOnce(dir);
                Eq(true, File.Exists(games));
            }
            finally
            {
                LegacyPurge.RestoreHook = null;
                LegacyPurge.SkipRegistryDelete = false;
                Settings.Save("PurgeV180Done", false);
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        private static void TestLegacyPurgeNeverTouchesForeignFiles()
        {
            string dir = Path.Combine(Path.GetTempPath(), "PavisePurge2_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            LegacyPurge.SkipRegistryDelete = true;
            try
            {
                string exe = Path.Combine(dir, "Pavise.exe");
                string portable = Path.Combine(dir, "Pavise.portable");
                string userFile = Path.Combine(dir, "我的存档.sav");
                File.WriteAllText(exe, "MZ");
                File.WriteAllText(portable, "");
                File.WriteAllText(userFile, "save-data");
                File.WriteAllText(Path.Combine(dir, "Pavise.games.txt"), "g");

                Settings.Save("PurgeV180Done", false);
                LegacyPurge.RestoreHook = delegate { return new List<string>(); };
                LegacyPurge.RunOnce(dir);

                if (!File.Exists(exe)) throw new Exception("purge deleted Pavise.exe");
                if (!File.Exists(portable)) throw new Exception("purge deleted the portable marker");
                if (!File.Exists(userFile)) throw new Exception("purge deleted an unrelated user file");
                Eq(false, File.Exists(Path.Combine(dir, "Pavise.games.txt")));
            }
            finally
            {
                LegacyPurge.RestoreHook = null;
                LegacyPurge.SkipRegistryDelete = false;
                Settings.Save("PurgeV180Done", false);
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        private static void TestKernelAntiCheatNamingIsHonest()
        {
            Eq("Ricochet", KernelAntiCheat.MatchByExe("cod22-cod"));
            Eq("Ricochet", KernelAntiCheat.MatchByExe(@"D:\Games\COD\cod.exe"));
            Eq("Ricochet", KernelAntiCheat.MatchByExe("ModernWarfare.exe"));
            if (KernelAntiCheat.MatchByExe("notepad.exe") != null)
                throw new Exception("an unknown executable must not be given an anti-cheat name");
            if (KernelAntiCheat.MatchByExe(null) != null)
                throw new Exception("null input must not produce a name");
            if (KernelAntiCheat.MatchByExe("") != null)
                throw new Exception("empty input must not produce a name");
            if (KernelAntiCheat.ServiceExists("PaviseDefinitelyNotAService_" + Guid.NewGuid().ToString("N")))
                throw new Exception("a nonexistent service must not be reported as installed");
        }
    }
}
