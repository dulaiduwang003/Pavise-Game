#if PAVISE_SELFTEST
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        internal static void RunBackgroundScopeTests()
        {
            var services = new List<KeyValuePair<string, int>> { new KeyValuePair<string, int>("WSearch", 800) };
            var p = new ProcEntry { Pid = 800, Session = 0, Creation = 100, Name = "SearchIndexer", Path = @"C:\Windows\System32\SearchIndexer.exe" };
            Eq(true, BackgroundServicePolicy.Eligible(PerformancePreset.Competitive, p, services, @"C:\Windows"));
            Eq(true, BackgroundServicePolicy.Eligible(PerformancePreset.Extreme, p, services, @"C:\Windows"));
            Eq(false, BackgroundServicePolicy.Eligible(PerformancePreset.Standard, p, services, @"C:\Windows"));
            Eq(false, BackgroundServicePolicy.Eligible(PerformancePreset.Handheld, p, services, @"C:\Windows"));
            Eq(false, BackgroundServicePolicy.Eligible(PerformancePreset.Extreme, p, null, @"C:\Windows"));
            services.Add(new KeyValuePair<string, int>("Audiosrv", 800));
            Eq(false, BackgroundServicePolicy.Eligible(PerformancePreset.Extreme, p, services, @"C:\Windows"));
            services.RemoveAt(1); p.Path = @"C:\Tools\SearchIndexer.exe";
            Eq(false, BackgroundServicePolicy.Eligible(PerformancePreset.Extreme, p, services, @"C:\Windows"));
            p.Path = @"C:\Windows\System32\SearchIndexer.exe"; p.Session = 9;
            Eq(false, BackgroundServicePolicy.Eligible(PerformancePreset.Extreme, p, services, @"C:\Windows"));
            p.Session = 0; p.Creation = 0;
            Eq(false, BackgroundServicePolicy.Eligible(PerformancePreset.Extreme, p, services, @"C:\Windows"));
            p.Creation = 100; p.Name = "svchost"; p.Path = @"C:\Windows\System32\svchost.exe";
            Eq(false, BackgroundServicePolicy.Eligible(PerformancePreset.Extreme, p, services, @"C:\Windows"));
            p.Name = "spoolsv"; p.Path = @"C:\Windows\System32\spoolsv.exe";
            services[0] = new KeyValuePair<string, int>("Spooler", 800);
            Eq(true, BackgroundServicePolicy.Eligible(PerformancePreset.Extreme, p, services, @"C:\Windows"));

            var profile = new GameProfile { Root = @"C:\Games\Example", ExecutablePath = @"C:\Games\Example\game.exe" };
            var snapshot = new ProcessSnapshot(new[] {
                new ProcEntry { Pid=100, Session=7, Creation=10, Name="game", Path=profile.ExecutablePath },
                new ProcEntry { Pid=101, Session=7, Creation=11, Name="updater", Path=@"C:\Games\Example\updater.exe" }
            });
            Eq(true, FamilyBoundary.CollectProtectedLibraryFamily(new[]{profile}, snapshot, 999, 7).Contains(101));
            var exact = FamilyBoundary.CollectProtectedLibraryFamily(new[]{profile}, snapshot, 999, 7, null, true);
            Eq(true, exact.Contains(100)); Eq(false, exact.Contains(101));
            Console.WriteLine("BACKGROUND_SCOPE policy: service identity, sessions, shared-host rejection, exact game scope PASS");

            string temp = Path.Combine(Path.GetTempPath(), "PaviseScope-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            var core = new SuppressionCore(Path.Combine(temp, "state"));
            Process target = CpuCapHelper("--cpu-cap-test-target");
            try
            {
                Eq("READY", CpuCapLine(target));
                Eq(true, Native.TrySetBoostDisabled(target.Handle, false));
                long creation = CpuCapWant(target)[target.Id];
                Eq(AcquireResult.ApplyFailed, core.Acquire(target.Id, target.ProcessName, SuppressReason.Background, null, SuppressionLevel.Isolated, creation+1));
                Eq(0, Native.QueryBoostDisabled(target.Handle));
                core.Acquire(target.Id, target.ProcessName, SuppressReason.Background, null, SuppressionLevel.Isolated, creation);
                Eq(0, Native.QueryBoostDisabled(target.Handle));
                Eq(true, File.Exists(Path.Combine(temp, "state")));
                var mode = new GameMode(temp, core);
                WhitelistRule serviceRule;
                Eq(true, WhitelistRule.TryCreate(WhitelistRuleKind.ExactPath, @"C:\Windows\System32\SearchIndexer.exe", out serviceRule));
                typeof(GameMode).GetMethod("AddWhiteRuleNoSave", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(mode, new object[]{serviceRule});
                var serviceSnapshot = new ProcessSnapshot(new[]{new ProcEntry { Pid=800, Session=0, Creation=100,
                    Name="SearchIndexer", Path=@"C:\Windows\System32\SearchIndexer.exe" }});
                object evaluation = typeof(GameMode).GetMethod("EvaluateWhitelist", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(mode, new object[]{serviceSnapshot});
                var protectedPids = (HashSet<int>)evaluation.GetType().GetField("Protected").GetValue(evaluation);
                Eq(true, protectedPids.Contains(800));
                foreach (PerformancePreset preset in PresetValue.VisibleOrder())
                {
                    Eq(true, mode.TrySetPreset(preset));
                    core.Reconcile(target.Id, target.ProcessName, SuppressReason.Background, true);
                    Eq(0, Native.QueryBoostDisabled(target.Handle));
                }
                Eq(true, Native.TrySetBoostDisabled(target.Handle, true));
                Eq(true, Native.SetPriorityClass(target.Handle, Native.NORMAL_PRIORITY_CLASS));
                core.Reconcile(target.Id, target.ProcessName, SuppressReason.Background, true);
                Eq(0, Native.QueryBoostDisabled(target.Handle));
                core.Acquire(target.Id, target.ProcessName, SuppressReason.AntiCheat, "test", SuppressionLevel.Isolated, creation);
                Eq(0, Native.QueryBoostDisabled(target.Handle));
                core.Release(target.Id, SuppressReason.AntiCheat);
                Eq(0, Native.QueryBoostDisabled(target.Handle));
                SuppressionCore.HealFromCrash(Path.Combine(temp, "state"));
                Eq(0, Native.QueryBoostDisabled(target.Handle));
                core.Release(target.Id, SuppressReason.Background);
                Eq(0, Native.QueryBoostDisabled(target.Handle));
                Eq(true, Native.TrySetBoostDisabled(target.Handle, true));
                foreach (PerformancePreset preset in PresetValue.VisibleOrder())
                {
                    Eq(true, mode.TrySetPreset(preset));
                    core.Acquire(target.Id, target.ProcessName, SuppressReason.Background, null, SuppressionLevel.Isolated, creation);
                    Eq(1, Native.QueryBoostDisabled(target.Handle));
                    Eq(true, Native.SetPriorityClass(target.Handle, Native.NORMAL_PRIORITY_CLASS));
                    core.Reconcile(target.Id, target.ProcessName, SuppressReason.Background, true);
                    Eq(1, Native.QueryBoostDisabled(target.Handle));
                    SuppressionCore.HealFromCrash(Path.Combine(temp, "state"));
                    Eq(1, Native.QueryBoostDisabled(target.Handle));
                    Eq(true, core.Release(target.Id, SuppressReason.Background));
                    Eq(1, Native.QueryBoostDisabled(target.Handle));
                }
                Eq(false, target.HasExited);
                Console.WriteLine("BACKGROUND_SCOPE native: stale identity, original enabled/disabled boosts retained across all modes, legacy undo, durable recovery, release PASS");
            }
            finally { core.Release(target.Id, SuppressReason.Background); CpuCapKill(target); }
        }
    }
}
#endif
