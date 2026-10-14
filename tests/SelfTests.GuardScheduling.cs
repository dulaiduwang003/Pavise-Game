#if PAVISE_SELFTEST
using System;
using System.IO;
using System.Reflection;
using System.Collections.Generic;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        private sealed class GuardRegistryFixture : IDisposable
        {
            internal readonly string Key = @"Software\PaviseAutoTests\GuardScheduling\" + Guid.NewGuid().ToString("N");
            internal readonly ReversibleReg[] DvrValues, MmcssValues;
            internal GuardRegistryFixture()
            {
                Settings.UseTransientStoreForCurrentProcess();
                using (var key = Registry.CurrentUser.CreateSubKey(Key))
                {
                    key.SetValue("GameDVR_Enabled", 1, RegistryValueKind.DWord);
                    key.SetValue("AppCaptureEnabled", 1, RegistryValueKind.DWord);
                    key.SetValue("SystemResponsiveness", 20, RegistryValueKind.DWord);
                    key.SetValue("Priority", 2, RegistryValueKind.DWord);
                    key.SetValue("Scheduling Category", "Medium", RegistryValueKind.String);
                    key.SetValue("SFIO Priority", "Normal", RegistryValueKind.String);
                }
                DvrValues = new[] {
                    Value("GameDVR_Enabled", RegistryValueKind.DWord, "PrevGameDvr"),
                    Value("AppCaptureEnabled", RegistryValueKind.DWord, "PrevGameDvrCap") };
                MmcssValues = new[] {
                    Value("SystemResponsiveness", RegistryValueKind.DWord, "Mmcss_Resp"),
                    Value("Priority", RegistryValueKind.DWord, "Mmcss_Pri"),
                    Value("Scheduling Category", RegistryValueKind.String, "Mmcss_Sched"),
                    Value("SFIO Priority", RegistryValueKind.String, "Mmcss_Sfio"),
                    Value("NoLazyMode", RegistryValueKind.DWord, "Mmcss_NoLazy") };
                GameDvr.ValuesForTest = DvrValues;
                Mmcss.ValuesForTest = MmcssValues;
            }
            private ReversibleReg Value(string name, RegistryValueKind kind, string slot)
            { return new ReversibleReg(Registry.CurrentUser, Key, name, kind, slot); }
            internal object Read(string name)
            { using (var key = Registry.CurrentUser.OpenSubKey(Key)) return key.GetValue(name); }
            internal void CheckApplied()
            {
                Eq(0, Read("GameDVR_Enabled")); Eq(0, Read("AppCaptureEnabled"));
                Eq(10, Read("SystemResponsiveness")); Eq("High", Read("Scheduling Category"));
                Eq(1, Read("NoLazyMode")); Eq(2, Read("Priority")); Eq("Normal", Read("SFIO Priority"));
            }
            internal void CheckOriginal()
            {
                Eq(1, Read("GameDVR_Enabled")); Eq(1, Read("AppCaptureEnabled"));
                Eq(20, Read("SystemResponsiveness")); Eq("Medium", Read("Scheduling Category"));
                Eq<object>(null, Read("NoLazyMode"));
                Eq(false, GameDvr.HasResidue); Eq(false, Mmcss.HasResidue());
            }
            internal void CheckDvrRestoredWithMmcssApplied()
            {
                Eq(1, Read("GameDVR_Enabled")); Eq(1, Read("AppCaptureEnabled"));
                Eq(false, GameDvr.HasResidue);
                Eq(10, Read("SystemResponsiveness")); Eq("High", Read("Scheduling Category"));
                Eq(1, Read("NoLazyMode")); Eq(2, Read("Priority")); Eq("Normal", Read("SFIO Priority"));
            }
            public void Dispose()
            {
                GameDvr.ValuesForTest = DvrValues; Mmcss.ValuesForTest = MmcssValues;
                GameDvr.Restore(); Mmcss.Restore();
                GameDvr.ValuesForTest = null; Mmcss.ValuesForTest = null;
                Registry.CurrentUser.DeleteSubKeyTree(Key, false);
                Settings.UseTransientStoreForCurrentProcess();
            }
        }

        private static void GuardSync(GameMode mode)
        { typeof(GameMode).GetMethod("SyncGuardScheduling", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(mode, null); }

        private static void RunGuardSchedulingRegressionTests()
        {
            foreach (string preset in new[] { "0", "1", "6", "4", "2" })
            using (var fixture = new GuardRegistryFixture())
            {
                Settings.SaveStr(PolicyCatalog.KeyPreset, preset);
                Settings.Save("GameDvrOff", false); Settings.Save("GmMmcss", false);
                string dir = Path.Combine(Path.GetTempPath(), "PaviseGuardScheduling-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                var mode = new GameMode(dir, new SuppressionCore());
                mode.InitializeAutomation();
                mode.Enabled = true;
                fixture.CheckDvrRestoredWithMmcssApplied();
                Eq(false, mode.KillGameDvr);
                Eq("restored", mode.AutomaticExecutionState("GameDvrOff"));
                Eq(true, mode.SetGameDvrSuppression(true));
                fixture.CheckApplied();
                Eq("applied", mode.AutomaticExecutionState("GameDvrOff"));
                Eq("applied", mode.AutomaticExecutionState("GmMmcss"));
                string originalReceipt = Settings.LoadStr("Mmcss_Resp", "");
                Eq(true, originalReceipt.Length > 0);
                var active = typeof(GameMode).GetField("active", BindingFlags.Instance | BindingFlags.NonPublic);
                active.SetValue(mode, true); GuardSync(mode); fixture.CheckApplied();
                active.SetValue(mode, false); GuardSync(mode); fixture.CheckApplied();
                Eq(originalReceipt, Settings.LoadStr("Mmcss_Resp", ""));
                Eq(true, mode.SetGameDvrSuppression(false));
                fixture.CheckDvrRestoredWithMmcssApplied();
                Settings.SaveStr(PolicyCatalog.KeyPreset, preset == "4" ? "6" : "4");
                mode.RefreshAutomaticPreferences(); GuardSync(mode);
                Eq(false, mode.KillGameDvr); Eq(false, Settings.Load("GameDvrOff", true));
                fixture.CheckDvrRestoredWithMmcssApplied();
                Eq(originalReceipt, Settings.LoadStr("Mmcss_Resp", ""));
                Eq(true, mode.SetGameDvrSuppression(true)); fixture.CheckApplied();
                mode.Enabled = false; fixture.CheckOriginal();
                Eq("restored", mode.AutomaticExecutionState("GmMmcss"));
                mode.Enabled = true; fixture.CheckApplied();
                Eq(true, mode.Stop()); fixture.CheckOriginal();
                Eq(false, mode.SetGameDvrSuppression(false));
                Eq(true, Settings.Load("GameDvrOff", false));
                GuardSync(mode); fixture.CheckOriginal();
            }
            using (var fixture = new GuardRegistryFixture())
            {
                string dir = Path.Combine(Path.GetTempPath(), "PaviseGuardRetryLimit-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                var mode = new GameMode(dir, new SuppressionCore()); mode.InitializeAutomation();
                var values = (ReversibleReg[])fixture.MmcssValues.Clone();
                values[2] = new ReversibleReg(null, fixture.Key, "Scheduling Category", RegistryValueKind.String, "Mmcss_Sched");
                Mmcss.ValuesForTest = values; mode.Enabled = true;
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var retryAt = (Dictionary<string, long>)typeof(GameMode).GetField("envNextAttempt", flags).GetValue(mode);
                for (int round = 0; round < 100; round++) GuardSync(mode);
                Eq(true, ActionFailureStore.IsBlocked("GmMmcss", "system"));
                Eq(false, retryAt.ContainsKey("mmcss"));
                retryAt["mmcss"] = 0; GuardSync(mode);
                Eq(true, ActionFailureStore.IsBlocked("GmMmcss", "system")); Eq("stopped", mode.AutomaticExecutionState("GmMmcss"));
                retryAt.Remove("mmcss");
                for (int round = 0; round < 100; round++) GuardSync(mode);
                Eq(true, ActionFailureStore.IsBlocked("GmMmcss", "system"));
                Eq(20, fixture.Read("SystemResponsiveness")); Eq<object>(null, fixture.Read("NoLazyMode"));
                Mmcss.ValuesForTest = fixture.MmcssValues;
                mode.Enabled = false; fixture.CheckOriginal();
                mode.Enabled = true;
                Eq(0, fixture.Read("GameDVR_Enabled")); Eq(20, fixture.Read("SystemResponsiveness"));
                Eq(true, ActionFailureStore.IsBlocked("GmMmcss", "system"));
                Eq(true, ActionFailureStore.RequestRetry(ActionFailureStore.RecordKey("GmMmcss", "system")));
                GuardSync(mode); fixture.CheckApplied(); Eq(false, ActionFailureStore.IsIsolated("GmMmcss", "system"));
                Eq(true, mode.Stop()); fixture.CheckOriginal();
            }
            using (var fixture = new GuardRegistryFixture())
            {
                Eq(true, Mmcss.Activate());
                var values = (ReversibleReg[])fixture.MmcssValues.Clone();
                values[2] = new ReversibleReg(null, fixture.Key, "Scheduling Category", RegistryValueKind.String, "Mmcss_Sched");
                Mmcss.ValuesForTest = values; Eq(false, Mmcss.Restore());
                string dir = Path.Combine(Path.GetTempPath(), "PaviseGuardPendingRecovery-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                var mode = new GameMode(dir, new SuppressionCore()); mode.InitializeAutomation(); mode.Enabled = true;
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var retryAt = (Dictionary<string, long>)typeof(GameMode).GetField("envNextAttempt", flags).GetValue(mode);
                retryAt["mmcss"] = 0; GuardSync(mode);
                var pending = (HashSet<string>)typeof(GameMode).GetField("envRestorePending", flags).GetValue(mode);
                Eq(true, pending.Contains("mmcss")); Eq(true, Mmcss.HasResidue());
                var completed = typeof(GameMode).GetMethod("EnvRestoreCompleted", flags);
                Eq(false, (bool)completed.Invoke(mode, new object[] { true }));
                Eq(true, pending.Contains("mmcss"));
                Eq(true, (bool)typeof(GameMode).GetMethod("EnvActive", flags).Invoke(mode, null));
                Mmcss.ValuesForTest = fixture.MmcssValues; retryAt["mmcss"] = 0; GuardSync(mode);
                Eq(false, pending.Contains("mmcss")); Eq(false, Mmcss.HasResidue());
                Eq(true, (bool)completed.Invoke(mode, new object[] { true }));
                Eq(20, fixture.Read("SystemResponsiveness")); Eq("Medium", fixture.Read("Scheduling Category"));
                mode.Enabled = false; fixture.CheckOriginal(); Eq(true, mode.Stop());
            }
            using (var fixture = new GuardRegistryFixture())
            {
                using (var key = Registry.CurrentUser.OpenSubKey(fixture.Key, true))
                {
                    key.SetValue("SystemResponsiveness", 10, RegistryValueKind.DWord);
                    key.SetValue("Scheduling Category", "High", RegistryValueKind.String);
                    key.SetValue("NoLazyMode", 1, RegistryValueKind.DWord);
                }
                string dir = Path.Combine(Path.GetTempPath(), "PaviseGuardAlreadyConfigured-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                var mode = new GameMode(dir, new SuppressionCore()); mode.InitializeAutomation(); mode.Enabled = true;
                Eq(false, Mmcss.HasResidue());
                Mmcss.ValuesForTest = null;
                for (int round = 0; round < 100; round++) GuardSync(mode);
                Eq("applied", mode.AutomaticExecutionState("GmMmcss"));
                Mmcss.ValuesForTest = fixture.MmcssValues; Eq(true, mode.Stop());
                Eq(10, fixture.Read("SystemResponsiveness")); Eq("High", fixture.Read("Scheduling Category"));
            }
            using (var fixture = new GuardRegistryFixture())
            {
                string dir = Path.Combine(Path.GetTempPath(), "PaviseGuardRestoreRetry-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                var mode = new GameMode(dir, new SuppressionCore());
                mode.InitializeAutomation(); mode.Enabled = true;
                var values = (ReversibleReg[])fixture.MmcssValues.Clone();
                values[0] = new ReversibleReg(null, fixture.Key, "SystemResponsiveness", RegistryValueKind.DWord, "Mmcss_Resp");
                Mmcss.ValuesForTest = values;
                mode.Enabled = false;
                Eq("restore-failed", mode.AutomaticExecutionState("GmMmcss"));
                var envActive = typeof(GameMode).GetMethod("EnvActive", BindingFlags.Instance | BindingFlags.NonPublic);
                Eq(true, (bool)envActive.Invoke(mode, null));
                Mmcss.ValuesForTest = fixture.MmcssValues;
                GuardSync(mode); fixture.CheckOriginal();
                Eq(false, (bool)envActive.Invoke(mode, null));
                Eq(true, mode.Stop());
            }
            using (var fixture = new GuardRegistryFixture())
            {
                var values = (ReversibleReg[])fixture.MmcssValues.Clone();
                values[2] = new ReversibleReg(null, fixture.Key, "Scheduling Category", RegistryValueKind.String, "Mmcss_Sched");
                Mmcss.ValuesForTest = values;
                Eq(false, Mmcss.Activate());
                fixture.CheckOriginal();
                Mmcss.ValuesForTest = fixture.MmcssValues;
                Eq(true, Mmcss.Activate());
                values = (ReversibleReg[])fixture.MmcssValues.Clone();
                values[0] = new ReversibleReg(null, fixture.Key, "SystemResponsiveness", RegistryValueKind.DWord, "Mmcss_Resp");
                Mmcss.ValuesForTest = values;
                Eq(false, Mmcss.Restore());
                Eq(true, Settings.LoadStr("Mmcss_Resp", "").Length > 0);
                Mmcss.ValuesForTest = fixture.MmcssValues;
                Mmcss.HealFromCrash(); fixture.CheckOriginal();
            }
            using (var fixture = new GuardRegistryFixture())
            {
                Eq(true, GameDvr.Activate()); Eq(true, Mmcss.Activate());
                GameDvr.HealFromCrash(); Mmcss.HealFromCrash(); fixture.CheckOriginal();
                Eq(true, Mmcss.Activate());
                using (var key = Registry.CurrentUser.OpenSubKey(fixture.Key, true))
                    key.SetValue("SystemResponsiveness", 30, RegistryValueKind.DWord);
                Eq(true, Mmcss.Restore());
                Eq(30, fixture.Read("SystemResponsiveness"));
                Eq(false, Mmcss.HasResidue());
            }
            using (var fixture = new GuardRegistryFixture())
            {
                GameDvr.ValuesForTest = new[] { fixture.DvrValues[0],
                    new ReversibleReg(null, fixture.Key, "AppCaptureEnabled", RegistryValueKind.DWord, "PrevGameDvrCap") };
                Eq(false, GameDvr.Activate()); Eq(true, GameDvr.HasResidue);
                GameDvr.ValuesForTest = fixture.DvrValues;
                Eq(true, GameDvr.Restore()); fixture.CheckOriginal();
            }
            Eq(false, GameDvr.Activate()); Eq(false, Mmcss.Activate());
        }
    }
}
#endif
