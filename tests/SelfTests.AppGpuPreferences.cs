// 文件用途 偏好 硬件和台账操作全是注入的
// 不碰注册表 应用进程 显卡设备 对话框和截图
#if PAVISE_SELFTEST
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        private static int appGpuChecks;
        private const string AppGpuPath = @"C:\AppGpuFixture\one.exe";

        internal static int RunAppGpuPreferencesRegressionTests()
        {
            string root = Path.Combine(Path.GetTempPath(), "PaviseAppGpuTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string oldLog = Logger.LogPath;
            Func<List<string>> oldRestore = LegacyPurge.RestoreHook;
            Func<bool> oldRegistry = LegacyPurge.DeleteRegistryHook;
            bool oldSkip = LegacyPurge.SkipRegistryDelete;
            Action<string>[] tests =
            {
                AppGpuConfirmedHardware,
                AppGpuPreviewAndConfirmation,
                AppGpuPreservesOtherFieldsAndAbsence,
                AppGpuAlreadyLowPowerIsUnowned,
                AppGpuInputAndUnsupportedRestore,
                AppGpuProposalCannotOverrideNewUserChoice,
                AppGpuStageHandoffPreservesOriginal,
                AppGpuStagePendingIsNotOwnership,
                AppGpuJournalMustPrecedeWrite,
                AppGpuWriteFailureAndReadback,
                AppGpuUncertainApplySurvivesRestart,
                AppGpuRestorePreparationAndFailure,
                AppGpuRestoreIntentSurvivesRestart,
                AppGpuCleanupTombstoneSurvivesRestart,
                AppGpuExternalChangesWin,
                AppGpuConcurrentOtherFieldChange,
                AppGpuStrictJournalAndPersistentList,
                AppGpuSerializedAndReentrantCalls,
                AppGpuResetRequiresKnownRecovery,
                AppGpuResetAbandonsUnprovableRecords,
                AppGpuAutoEnrollFilters,
                AppGpuAutoEnrollRespectsExistingChoices,
                AppGpuAutoHandledListIsOnceEver
            };
            appGpuChecks = 0;
            try
            {
                foreach (Action<string> test in tests)
                {
                    Settings.UseTransientStoreForCurrentProcess();
                    Logger.ResetWriteBarrierForTest(); Logger.LogPath = Path.Combine(root, "decisions.log");
                    LegacyPurge.SkipRegistryDelete = false;
                    LegacyPurge.RestoreHook = delegate { throw new InvalidOperationException("Unmocked app-GPU restore"); };
                    LegacyPurge.DeleteRegistryHook = delegate { throw new InvalidOperationException("Unmocked app-GPU registry delete"); };
                    test(root);
                    Console.WriteLine("PASS " + test.Method.Name);
                }
                Console.WriteLine("PASS app-gpu assertions=" + appGpuChecks + " registry=mocked hardware=mocked windows_shown=false");
                return tests.Length;
            }
            finally
            {
                LegacyPurge.RestoreHook = oldRestore; LegacyPurge.DeleteRegistryHook = oldRegistry;
                LegacyPurge.SkipRegistryDelete = oldSkip;
                Logger.ResetWriteBarrierForTest(); Logger.LogPath = oldLog;
                Settings.UseTransientStoreForCurrentProcess();
                string actual = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
                string temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
                if (actual.StartsWith(temporary + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    && Path.GetFileName(actual).StartsWith("PaviseAppGpuTests-", StringComparison.Ordinal)
                    && Directory.Exists(actual)) Directory.Delete(actual, true);
            }
        }

        private static void AppGpuCheck(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("App GPU preference regression: " + message);
            Interlocked.Increment(ref appGpuChecks);
        }

        private sealed class AppGpuLedgerFake : IAppGpuPreferenceLedger
        {
            internal string Value = "", LastRead;
            internal int Reads, Writes;
            internal bool DenyRead, DenyWrite, LoseWrites;
            internal Func<string, bool> Reject;
            internal Action<string> AfterWrite;
            public bool TryRead(out string value)
            {
                Reads++; value = null;
                if (DenyRead) return false;
                value = Value; LastRead = value; return true;
            }
            public bool TryWrite(string value)
            {
                Writes++;
                if (DenyWrite || (Reject != null && Reject(value))) return false;
                if (!LoseWrites) Value = value;
                if (AfterWrite != null) AfterWrite(value);
                return true;
            }
        }

        private sealed class AppGpuControlFake : IAppGpuPreferenceControl
        {
            internal readonly Dictionary<string, string> Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            internal readonly AppGpuLedgerFake Ledger;
            internal bool Hardware = true, Exists = true, DenyRead, DenyWrite, IgnoreWrite;
            internal bool ThrowBeforeWrite, ThrowAfterWrite, UseRealStage, DenyStageWrite;
            internal int Reads, Attempts, Writes, StageWrites, StageReleases;
            internal Action BeforeWrite, AfterWrite, AfterStageWrite;
            internal AppGpuControlFake(AppGpuLedgerFake ledger) { Ledger = ledger; }
            public bool Supported { get { return Hardware; } }
            public bool IsExecutablePresent(string path) { return Exists; }
            internal string Value(string path) { string value; Values.TryGetValue(path, out value); return value; }
            internal void Set(string path, string value) { if (value == null) Values.Remove(path); else Values[path] = value; }
            public bool TryRead(string path, out string value)
            { Reads++; value = Value(path); return !DenyRead; }
            public bool TryGetStagedOriginal(string path, out bool staged, out string original)
            {
                staged = false; original = null;
                return !UseRealStage || GpuPrefStage.TryGetStagedOriginal(path, out staged, out original);
            }
            public bool TryReleaseStage(string path)
            { StageReleases++; return !UseRealStage || GpuPrefStage.TryReleaseForManualPreference(path); }
            public AppGpuPreferenceWriteResult CompareExchange(string path, string expected, string replacement)
            {
                Attempts++;
                if (BeforeWrite != null) BeforeWrite();
                if (ThrowBeforeWrite) throw new IOException("Unknown fake write outcome before dispatch");
                if (Value(path) != expected) return AppGpuPreferenceWriteResult.Changed;
                if (DenyWrite) return AppGpuPreferenceWriteResult.NotIssued;
                string phase = PrefFieldText.ReadField(replacement, "GpuPreference") == "1" ? "P" : "R";
                string prefix = "\n" + phase + "\t" + Convert.ToBase64String(Encoding.UTF8.GetBytes(path)) + "\t";
                AppGpuCheck(Ledger.Value.IndexOf(prefix, StringComparison.Ordinal) >= 0 && Ledger.LastRead == Ledger.Value,
                    "registry mutation did not follow a verified " + phase + " journal");
                Writes++;
                if (!IgnoreWrite) Set(path, replacement);
                if (AfterWrite != null) AfterWrite();
                if (ThrowAfterWrite) throw new IOException("Unknown fake write outcome after dispatch");
                return AppGpuPreferenceWriteResult.Written;
            }
            internal bool StageWrite(string path, string value)
            {
                if (DenyStageWrite) return false;
                StageWrites++; Set(path, value);
                if (AfterStageWrite != null) AfterStageWrite();
                return true;
            }
        }

        private sealed class AppGpuFixture : IDisposable
        {
            internal readonly AppGpuLedgerFake Ledger = new AppGpuLedgerFake();
            internal readonly AppGpuControlFake Control;
            internal AppGpuPreferenceManager Manager;
            private readonly AppGpuPreferenceManager previous;
            private readonly GpuPrefStage.ReadPreferenceOverride oldRead;
            private readonly Func<string, string, bool> oldWrite;
            private readonly Func<string, string, GpuPrefStage.WriteResult> oldWriteResult;
            private readonly Func<bool> oldSupported;
            internal AppGpuFixture()
            {
                Control = new AppGpuControlFake(Ledger);
                Manager = new AppGpuPreferenceManager(Control, Ledger);
                previous = AppGpuPreferences.OverrideForTest;
                oldRead = GpuPrefStage.ReadPreferenceForTest; oldWrite = GpuPrefStage.WritePreferenceForTest;
                oldWriteResult = GpuPrefStage.WriteResultForTest;
                oldSupported = GpuPrefStage.SupportedForTest;
                AppGpuPreferences.OverrideForTest = Manager;
                GpuPrefStage.ReadPreferenceForTest = Control.TryRead;
                GpuPrefStage.WritePreferenceForTest = Control.StageWrite;
                GpuPrefStage.WriteResultForTest = null;
                GpuPrefStage.SupportedForTest = delegate { return Control.Hardware; };
                GpuPrefStage.ForgetReceiptForTest();
            }
            internal AppGpuPreferenceChange Prepare(string path)
            {
                AppGpuPreferenceChange change;
                AppGpuCheck(Manager.Prepare(path, out change) == AppGpuPreferenceResult.Success && change != null,
                    "fixture could not prepare " + path);
                return change;
            }
            internal void Own(string original)
            {
                Control.Set(AppGpuPath, original);
                AppGpuCheck(Manager.Apply(Prepare(AppGpuPath), true) == AppGpuPreferenceResult.Success,
                    "fixture did not establish an owned low-power preference");
            }
            internal void Reload()
            {
                Manager = new AppGpuPreferenceManager(Control, Ledger);
                AppGpuPreferences.OverrideForTest = Manager;
                GpuPrefStage.ForgetReceiptForTest();
            }
            public void Dispose()
            {
                AppGpuPreferences.OverrideForTest = previous;
                GpuPrefStage.ReadPreferenceForTest = oldRead; GpuPrefStage.WritePreferenceForTest = oldWrite;
                GpuPrefStage.WriteResultForTest = oldWriteResult;
                GpuPrefStage.SupportedForTest = oldSupported;
                GpuPrefStage.ForgetReceiptForTest();
            }
        }

        private static void AppGpuAutoEnrollFilters(string root)
        {
            const string windows = @"C:\Windows\";
            AppGpuCheck(GameMode.AutoGpuEligible("someapp", @"C:\Tools\someapp.exe", windows),
                "an ordinary background app was filtered out");
            AppGpuCheck(!GameMode.AutoGpuEligible("dwm", @"C:\Windows\System32\dwm.exe", windows),
                "a windows-directory process was eligible for auto GPU preference");
            AppGpuCheck(!GameMode.AutoGpuEligible("obs64", @"C:\Portable\obs64.exe", windows),
                "a capture host was eligible for auto GPU preference");
            AppGpuCheck(!GameMode.AutoGpuEligible("Discord", @"C:\Portable\Discord.exe", windows),
                "a communication overlay host was eligible for auto GPU preference");
            AppGpuCheck(!GameMode.AutoGpuEligible("MSIAfterburner", @"C:\Tools\MSIAfterburner.exe", windows),
                "a hardware control tool was eligible for auto GPU preference");
            AppGpuCheck(!GameMode.AutoGpuEligible(null, @"C:\Tools\x.exe", windows)
                && !GameMode.AutoGpuEligible("x", null, windows),
                "a candidate with missing identity was eligible");
        }

        private static void AppGpuAutoEnrollRespectsExistingChoices(string root)
        {
            using (var f = new AppGpuFixture())
            {
                // 全新程序 没有既有偏好 自动认领成功 真的写进省电偏好
                AppGpuCheck(GameMode.AutoGpuEnroll(f.Manager, AppGpuPath) == AppGpuPreferenceResult.Success
                    && PrefFieldText.ReadField(f.Control.Value(AppGpuPath), "GpuPreference") == "1",
                    "auto enroll did not claim a fresh app");
                AppGpuCheck(GameMode.AutoGpuEnroll(f.Manager, AppGpuPath) == AppGpuPreferenceResult.AlreadyPresent,
                    "auto enroll re-claimed an already managed app");
            }
            using (var f = new AppGpuFixture())
            {
                // 既有明确偏好是用户或外部工具的选择 自动路径永不覆盖 也不留账
                const string other = @"C:\AppGpuFixture\two.exe";
                f.Control.Set(other, "GpuPreference=2;");
                AppGpuCheck(GameMode.AutoGpuEnroll(f.Manager, other) == AppGpuPreferenceResult.NeedsConfirmation
                    && f.Control.Value(other) == "GpuPreference=2;" && f.Ledger.Value == "",
                    "auto enroll overrode an existing explicit preference");
                // 本来就是省电偏好 按不拥有记 不发注册表写
                const string low = @"C:\AppGpuFixture\three.exe";
                f.Control.Set(low, "GpuPreference=1;");
                int writes = f.Control.Writes;
                AppGpuCheck(GameMode.AutoGpuEnroll(f.Manager, low) == AppGpuPreferenceResult.Success
                    && f.Control.Writes == writes && f.Control.Value(low) == "GpuPreference=1;",
                    "an already-low-power app caused a registry write");
            }
        }

        private static void AppGpuAutoHandledListIsOnceEver(string root)
        {
            const string path = @"C:\AppGpuFixture\auto.exe";
            AppGpuCheck(!GameMode.AutoGpuAlreadyHandled(path), "a fresh path was already handled");
            AppGpuCheck(GameMode.RememberAutoGpuHandled(path) && GameMode.AutoGpuAlreadyHandled(path),
                "a handled path was not persisted");
            AppGpuCheck(GameMode.AutoGpuAlreadyHandled(@"c:\appgpufixture\AUTO.EXE"),
                "the handled lookup was case sensitive");
            AppGpuCheck(!GameMode.AutoGpuAlreadyHandled(@"C:\AppGpuFixture\other.exe"),
                "an unrelated path inherited handled state");
            // 封顶后最老的先出 新记录保留
            for (int i = 0; i < 300; i++)
                AppGpuCheck(GameMode.RememberAutoGpuHandled(@"C:\AppGpuFixture\bulk" + i + ".exe"),
                    "a bulk handled save failed");
            AppGpuCheck(!GameMode.AutoGpuAlreadyHandled(path)
                && GameMode.AutoGpuAlreadyHandled(@"C:\AppGpuFixture\bulk299.exe"),
                "the handled list cap did not evict oldest first");
            AppGpuCheck(!GameMode.RememberAutoGpuHandled("bad\npath"),
                "a path containing a newline was accepted into the handled list");
        }

        private static GpuAdapter AppGpuAdapter(string id, bool known, bool integrated, GpuVendor vendor)
        { return new GpuAdapter { HardwareId = id, IntegratedKnown = known, Integrated = integrated, Vendor = vendor }; }

        private static void AppGpuConfirmedHardware(string root)
        {
            var intelB580 = AppGpuAdapter("PCI\\VEN_8086&DEV_E20B", true, false, GpuVendor.Intel);
            var nvidia = AppGpuAdapter("PCI\\VEN_10DE&DEV_1234", true, false, GpuVendor.Nvidia);
            var igpu = AppGpuAdapter("PCI\\VEN_8086&DEV_1234", true, true, GpuVendor.Intel);
            var guessed = AppGpuAdapter("PCI\\VEN_8086&DEV_5678", false, true, GpuVendor.Intel);
            AppGpuCheck(!AppGpuPreferences.ConfirmedHybrid(null) && !AppGpuPreferences.ConfirmedHybrid(new GpuAdapter[0]), "absent hardware was considered hybrid");
            AppGpuCheck(!AppGpuPreferences.ConfirmedHybrid(new[] { intelB580, nvidia })
                && !AppGpuPreferences.ConfirmedHybrid(new[] { guessed, nvidia }), "vendor/heuristic misclassified an Intel discrete GPU as integrated");
            AppGpuCheck(AppGpuPreferences.ConfirmedHybrid(new[] { igpu, intelB580 })
                && !AppGpuPreferences.ConfirmedHybrid(new[] { igpu }), "confirmed mixed hardware was classified incorrectly");
            AppGpuCheck(!AppGpuPreferences.ConfirmedHybrid(new[] { igpu, AppGpuAdapter(igpu.HardwareId, true, false, GpuVendor.Intel) }),
                "contradictory records for one adapter were treated as two GPUs");
        }

        private static void AppGpuPreviewAndConfirmation(string root)
        {
            using (var f = new AppGpuFixture())
            {
                const string original = "GpuPreference=2;SwapEffectUpgradeEnable=0;AutoHDREnable=0;";
                f.Control.Set(AppGpuPath, original);
                AppGpuPreferenceChange change = f.Prepare(AppGpuPath);
                AppGpuCheck(change.NeedsConfirmation && change.CurrentPreference == "2" && change.OriginalPreference == "2"
                    && change.Name == "one" && change.ExePath == AppGpuPath && f.Control.Writes == 0 && f.Ledger.Value == "",
                    "preview mutated settings or failed to disclose an existing preference");
                AppGpuCheck(f.Manager.Apply(change, false) == AppGpuPreferenceResult.NeedsConfirmation
                    && f.Control.Value(AppGpuPath) == original && f.Ledger.Value == "", "rejected confirmation changed the application preference");
                AppGpuCheck(f.Manager.Apply(change, true) == AppGpuPreferenceResult.Success
                    && PrefFieldText.ReadField(f.Control.Value(AppGpuPath), "GpuPreference") == "1"
                    && PrefFieldText.ReadField(f.Control.Value(AppGpuPath), "SwapEffectUpgradeEnable") == "0",
                    "confirmed preference did not preserve unrelated settings");
                AppGpuCheck(f.Manager.Apply(change, true) == AppGpuPreferenceResult.AlreadyPresent && f.Control.Writes == 1,
                    "repeated apply overwrote the saved original or issued another write");
            }
        }

        private static void AppGpuPreservesOtherFieldsAndAbsence(string root)
        {
            foreach (string original in new[] { null, "", "GpuPreference=0;", "SwapEffectUpgradeEnable=0;GpuPreference=2;AutoHDR=0;" })
            using (var f = new AppGpuFixture())
            {
                f.Own(original);
                f.Reload();
                int writes = f.Control.Writes;
                AppGpuCheck(f.Manager.HealFromCrash() && f.Control.Writes == writes
                    && PrefFieldText.ReadField(f.Control.Value(AppGpuPath), "GpuPreference") == "1", "startup undid or enforced a persistent app preference");
                AppGpuCheck(f.Manager.Remove(AppGpuPath) == AppGpuPreferenceResult.Success && !f.Manager.HasResidue
                    && f.Control.Value(AppGpuPath) == original, "remove failed to restore the original field/absent value");
            }
            using (var f = new AppGpuFixture())
            {
                f.Own("GpuPreference=2;SwapEffectUpgradeEnable=0;");
                f.Control.Set(AppGpuPath, "GpuPreference=1;SwapEffectUpgradeEnable=1;ExternalSetting=keep;");
                AppGpuCheck(f.Manager.Remove(AppGpuPath) == AppGpuPreferenceResult.Success
                    && f.Control.Value(AppGpuPath) == "GpuPreference=2;SwapEffectUpgradeEnable=1;ExternalSetting=keep;",
                    "restore replaced the whole registry string instead of only the owned field");
            }
        }

        private static void AppGpuAlreadyLowPowerIsUnowned(string root)
        {
            using (var f = new AppGpuFixture())
            {
                f.Control.Set(AppGpuPath, "GpuPreference=1;Other=keep;");
                AppGpuPreferenceChange change = f.Prepare(AppGpuPath);
                AppGpuCheck(change.AlreadyLowPower && !change.NeedsConfirmation
                    && f.Manager.Apply(change, false) == AppGpuPreferenceResult.Success && f.Control.Writes == 0,
                    "existing low-power preference was claimed or rewritten");
                List<AppGpuPreferenceEntry> entries;
                AppGpuCheck(f.Manager.TryGetEntries(out entries) && entries.Count == 1
                    && entries[0].State == AppGpuPreferenceState.AlreadyLowPower, "unowned list entry was not represented accurately");
                f.Control.Set(AppGpuPath, "GpuPreference=2;Other=user;");
                AppGpuCheck(f.Manager.Remove(AppGpuPath) == AppGpuPreferenceResult.Success
                    && f.Control.Value(AppGpuPath) == "GpuPreference=2;Other=user;" && f.Control.Writes == 0,
                    "removing an unowned entry changed the user's current preference");
            }
        }

        private static void AppGpuInputAndUnsupportedRestore(string root)
        {
            using (var f = new AppGpuFixture())
            {
                foreach (string path in new[] { null, "", "relative.exe", "C:relative.exe", @"\rooted.exe", @"C:\app.txt", @"\\.\device.exe", "C:\\app.exe\0" })
                {
                    AppGpuPreferenceChange change;
                    AppGpuCheck(f.Manager.Prepare(path, out change) == AppGpuPreferenceResult.InvalidPath && change == null,
                        "invalid/relative executable path was accepted: " + path);
                }
                AppGpuPreferenceChange prepared = f.Prepare(AppGpuPath);
                f.Control.Hardware = false;
                AppGpuCheck(f.Manager.Apply(prepared, true) == AppGpuPreferenceResult.Unsupported && f.Control.Writes == 0,
                    "lost hardware capability did not revoke a prepared change");
                f.Control.Hardware = true; f.Own("GpuPreference=2;");
                f.Control.Hardware = false; f.Control.Exists = false;
                AppGpuCheck(f.Manager.Remove(AppGpuPath) == AppGpuPreferenceResult.Success
                    && f.Control.Value(AppGpuPath) == "GpuPreference=2;", "missing GPU/executable prevented owned recovery");
            }
        }

        private static void AppGpuProposalCannotOverrideNewUserChoice(string root)
        {
            using (var f = new AppGpuFixture())
            using (var other = new AppGpuFixture())
            {
                f.Control.Set(AppGpuPath, "GpuPreference=2;");
                AppGpuPreferenceChange change = f.Prepare(AppGpuPath);
                f.Control.Set(AppGpuPath, "GpuPreference=0;External=1;");
                AppGpuCheck(f.Manager.Apply(change, true) == AppGpuPreferenceResult.Changed && f.Control.Writes == 0
                    && f.Ledger.Value == "", "confirmed preview overwrote a newer user change");
                AppGpuCheck(other.Manager.Apply(change, true) == AppGpuPreferenceResult.Changed && other.Control.Writes == 0,
                    "a proposal from another manager instance was accepted");
            }
        }

        private static void AppGpuStageHandoffPreservesOriginal(string root)
        {
            foreach (string original in new[] { null, "GpuPreference=0;Other=keep;", "SwapEffectUpgradeEnable=0;" })
            using (var f = new AppGpuFixture())
            {
                Settings.SaveStr("GpuPrefStage", "");
                f.Control.UseRealStage = true; f.Control.Set(AppGpuPath, original);
                AppGpuCheck(GpuPrefStage.Stage(AppGpuPath) && PrefFieldText.ReadField(f.Control.Value(AppGpuPath), "GpuPreference") == "2",
                    "real staging flow did not establish the temporary preference");
                int stageWrites = f.Control.StageWrites;
                AppGpuPreferenceChange change = f.Prepare(AppGpuPath);
                AppGpuCheck(change.CurrentPreference == "2" && change.OriginalPreference == PrefFieldText.ReadField(original, "GpuPreference")
                    && f.Control.StageWrites == stageWrites, "preparing handoff wrote settings or mistook temporary 2 for the user original");
                AppGpuCheck(f.Manager.Apply(change, true) == AppGpuPreferenceResult.Success && !GpuPrefStage.HasResidue,
                    "manual preference could not take over the temporary staging receipt");
                AppGpuCheck(GpuPrefStage.Restore() && PrefFieldText.ReadField(f.Control.Value(AppGpuPath), "GpuPreference") == "1",
                    "game exit overwrote the explicitly saved app preference");
                stageWrites = f.Control.StageWrites;
                AppGpuCheck(GpuPrefStage.Stage(AppGpuPath) && f.Control.StageWrites == stageWrites,
                    "armed-game staging reapplied over the managed application list");
                AppGpuCheck(f.Manager.Remove(AppGpuPath) == AppGpuPreferenceResult.Success && f.Control.Value(AppGpuPath) == original,
                    "removing a handed-off app restored temporary GPU2 instead of the actual original");
            }
        }

        private static void AppGpuStagePendingIsNotOwnership(string root)
        {
            using (var f = new AppGpuFixture())
            {
                f.Control.UseRealStage = true; f.Control.DenyStageWrite = true;
                AppGpuCheck(!GpuPrefStage.Stage(AppGpuPath), "failed temporary write was reported successful");
                f.Control.Set(AppGpuPath, "GpuPreference=2;");
                f.Control.DenyStageWrite = false; GpuPrefStage.ForgetReceiptForTest();
                AppGpuCheck(!GpuPrefStage.Restore() && f.Control.Value(AppGpuPath) == "GpuPreference=2;",
                    "unknown temporary prepared receipt claimed an external high-performance preference");
                AppGpuPreferenceChange change;
                AppGpuCheck(f.Manager.Prepare(AppGpuPath, out change) == AppGpuPreferenceResult.Busy,
                    "unknown temporary receipt was used as a trustworthy manual baseline");
                f.Control.Set(AppGpuPath, null);
                AppGpuCheck(GpuPrefStage.Restore(), "unapplied temporary receipt could not be settled after state was known");
            }
            string prepared = GpuPrefStage.EncodeJournal(AppGpuPath, null);
            string legacy = prepared.Substring("2|P|".Length);
            foreach (string raw in new[] { prepared, legacy, prepared.Replace("2|P|", "2|R|") })
            using (var f = new AppGpuFixture())
            {
                Settings.SaveStr("GpuPrefStage", raw);
                f.Control.UseRealStage = true; f.Control.Set(AppGpuPath, "GpuPreference=2;Other=user;");
                bool staged; string original;
                AppGpuCheck(!GpuPrefStage.TryGetStagedOriginal(AppGpuPath, out staged, out original)
                    && !GpuPrefStage.Restore() && GpuPrefStage.HasResidue && f.Control.StageWrites == 0,
                    "pending/legacy/unknown restore receipt was mistaken for temporary GPU ownership");
            }
            using (var f = new AppGpuFixture())
            {
                Settings.SaveStr("GpuPrefStage", prepared.Replace("2|P|", "2|S|"));
                f.Control.Set(AppGpuPath, "GpuPreference=2;Other=user;");
                AppGpuCheck(GpuPrefStage.Restore() && !GpuPrefStage.HasResidue && f.Control.StageWrites == 0
                    && f.Control.Value(AppGpuPath) == "GpuPreference=2;Other=user;",
                    "settled temporary receipt repeated restoration against a newer user preference");
            }
            foreach (bool loseReceipt in new[] { false, true })
            using (var f = new AppGpuFixture())
            {
                Settings.SaveStr("GpuPrefStage", "");
                f.Control.UseRealStage = true;
                f.Control.AfterStageWrite = delegate { Settings.SuspendWritesForReset(); };
                AppGpuCheck(!GpuPrefStage.Stage(AppGpuPath) && f.Control.Value(AppGpuPath) == "GpuPreference=2;",
                    "failed temporary ownership persistence was reported successful");
                string raw;
                AppGpuCheck(Settings.TryLoadStr("GpuPrefStage", out raw) && raw == prepared,
                    "temporary write lost its prepared original after ownership persistence failed");
                // 只把这个测试自己的临时设置重开 持久那份 P 留着
                Settings.UseTransientStoreForCurrentProcess(); Settings.SaveStr("GpuPrefStage", raw);
                f.Control.AfterStageWrite = null;
                if (loseReceipt) GpuPrefStage.ForgetReceiptForTest();
                int writes = f.Control.StageWrites;
                if (loseReceipt)
                    AppGpuCheck(!GpuPrefStage.Restore() && f.Control.StageWrites == writes && GpuPrefStage.HasResidue,
                        "new-process temporary P inferred ownership from GPU2");
                else
                    AppGpuCheck(GpuPrefStage.Restore() && f.Control.Value(AppGpuPath) == null && !GpuPrefStage.HasResidue,
                        "same-process confirmed temporary write could not restore using its receipt");
            }
            foreach (string raw in new[] { "broken", prepared.Replace("2|P|", "2|Z|"),
                GpuPrefStage.EncodeJournal(AppGpuPath, "GpuPreference=1;"),
                GpuPrefStage.EncodeJournal(AppGpuPath, "GpuPreference=2;"),
                GpuPrefStage.EncodeJournal(AppGpuPath, "GpuPreference=0;GpuPreference=2;"),
                GpuPrefStage.EncodeJournal(AppGpuPath, "GpuPreference;"),
                GpuPrefStage.EncodeJournal(AppGpuPath, "GpuPreference=;") })
            using (var f = new AppGpuFixture())
            {
                Settings.SaveStr("GpuPrefStage", raw);
                f.Control.Set(AppGpuPath, "GpuPreference=2;");
                AppGpuCheck(!GpuPrefStage.Restore() && GpuPrefStage.HasResidue && f.Control.StageWrites == 0,
                    "malformed temporary journal supplied an untrustworthy recovery original");
            }
            foreach (string current in new[] { "GpuPreference=3;", "GpuPreference=0;GpuPreference=2;",
                "GpuPreference;", "GpuPreference=;" })
            using (var f = new AppGpuFixture())
            {
                Settings.SaveStr("GpuPrefStage", ""); f.Control.Set(AppGpuPath, current);
                AppGpuCheck(!GpuPrefStage.Stage(AppGpuPath) && !GpuPrefStage.HasResidue && f.Control.StageWrites == 0,
                    "automatic stage changed an unknown/ambiguous existing preference");
            }
            using (var f = new AppGpuFixture())
            {
                Settings.SaveStr("GpuPrefStage", "");
                GpuPrefStage.WriteResultForTest = delegate { return GpuPrefStage.WriteResult.NotIssued; };
                AppGpuCheck(!GpuPrefStage.Stage(AppGpuPath), "definitely rejected temporary write reported success");
                f.Control.Set(AppGpuPath, "GpuPreference=2;Other=user;");
                AppGpuCheck(GpuPrefStage.Restore() && !GpuPrefStage.HasResidue && f.Control.StageWrites == 0
                    && f.Control.Value(AppGpuPath) == "GpuPreference=2;Other=user;",
                    "known unissued temporary P claimed a later external setting");
            }
            foreach (bool failRollback in new[] { false, true })
            using (var f = new AppGpuFixture())
            {
                Settings.SaveStr("GpuPrefStage", "");
                AppGpuCheck(GpuPrefStage.Stage(AppGpuPath), "temporary retry fixture did not stage");
                int writes = f.Control.StageWrites;
                bool reject = true;
                GpuPrefStage.WriteResultForTest = delegate(string path, string value)
                {
                    if (reject)
                    {
                        if (failRollback) Settings.SuspendWritesForReset();
                        return GpuPrefStage.WriteResult.NotIssued;
                    }
                    return f.Control.StageWrite(path, value) ? GpuPrefStage.WriteResult.Written : GpuPrefStage.WriteResult.Unconfirmed;
                };
                AppGpuCheck(!GpuPrefStage.Restore() && GpuPrefStage.HasResidue && f.Control.StageWrites == writes,
                    "temporary no-dispatch restore lost debt or pretended to succeed");
                string raw;
                AppGpuCheck(Settings.TryLoadStr("GpuPrefStage", out raw)
                    && raw.StartsWith(failRollback ? "2|R|" : "2|O|", StringComparison.Ordinal),
                    "known unissued temporary restore had an incorrect recovery phase");
                if (failRollback)
                { Settings.UseTransientStoreForCurrentProcess(); Settings.SaveStr("GpuPrefStage", raw); }
                reject = false;
                AppGpuCheck(GpuPrefStage.Restore() && !GpuPrefStage.HasResidue && f.Control.Value(AppGpuPath) == null
                    && f.Control.StageWrites == writes + 1, "known unissued temporary restore could not retry with its live receipt");
            }
            foreach (int observationRead in new[] { 1, 2 })
            foreach (bool failSettlement in new[] { false, true })
            using (var f = new AppGpuFixture())
            {
                Settings.SaveStr("GpuPrefStage", "");
                AppGpuCheck(GpuPrefStage.Stage(AppGpuPath), "temporary external-change fixture did not stage");
                int reads = 0, writes = f.Control.StageWrites;
                GpuPrefStage.ReadPreferenceForTest = delegate(string path, out string value)
                {
                    if (++reads == observationRead)
                    {
                        f.Control.Set(path, "GpuPreference=1;Other=external;");
                        if (failSettlement) Settings.SuspendWritesForReset();
                    }
                    return f.Control.TryRead(path, out value);
                };
                GpuPrefStage.Restore();
                AppGpuCheck(f.Control.Value(AppGpuPath) == "GpuPreference=1;Other=external;" && f.Control.StageWrites == writes,
                    "temporary restore overwrote a user change observed during its fresh read");
                if (failSettlement)
                {
                    string raw;
                    AppGpuCheck(Settings.TryLoadStr("GpuPrefStage", out raw) && raw.Length != 0,
                        "failed settlement discarded the durable temporary journal");
                    Settings.UseTransientStoreForCurrentProcess(); Settings.SaveStr("GpuPrefStage", raw);
                }
                GpuPrefStage.ReadPreferenceForTest = f.Control.TryRead;
                f.Control.Set(AppGpuPath, "GpuPreference=2;Other=newer;");
                bool staged; string original;
                AppGpuCheck(GpuPrefStage.TryGetStagedOriginal(AppGpuPath, out staged, out original) && !staged,
                    "settled temporary ownership supplied an old manual-handoff baseline");
                AppGpuCheck(GpuPrefStage.Stage(AppGpuPath) && GpuPrefStage.Restore() && f.Control.StageWrites == writes
                    && f.Control.Value(AppGpuPath) == "GpuPreference=2;Other=newer;",
                    "observed external ownership was reclaimed when GPU2 appeared again");
            }
        }

        private static void AppGpuJournalMustPrecedeWrite(string root)
        {
            foreach (string failure in new[] { "unreadable", "rejected", "lost", "readback" })
            using (var f = new AppGpuFixture())
            {
                AppGpuPreferenceChange change = f.Prepare(AppGpuPath);
                if (failure == "unreadable") f.Ledger.DenyRead = true;
                else if (failure == "rejected") f.Ledger.DenyWrite = true;
                else if (failure == "lost") f.Ledger.LoseWrites = true;
                else f.Ledger.AfterWrite = delegate { f.Ledger.DenyRead = true; };
                AppGpuCheck(f.Manager.Apply(change, true) == AppGpuPreferenceResult.JournalFailed && f.Control.Writes == 0,
                    "unverified original journal allowed a preference write: " + failure);
                AppGpuCheck(f.Manager.HasResidue, "unreadable/uncommitted recovery state was hidden: " + failure);
            }
        }

        private static void AppGpuWriteFailureAndReadback(string root)
        {
            foreach (string failure in new[] { "not-issued", "ignored", "read-failed", "external" })
            using (var f = new AppGpuFixture())
            {
                AppGpuPreferenceChange change = f.Prepare(AppGpuPath);
                f.Control.DenyWrite = failure == "not-issued"; f.Control.IgnoreWrite = failure == "ignored";
                if (failure == "read-failed") f.Control.AfterWrite = delegate { f.Control.DenyRead = true; };
                if (failure == "external") f.Control.AfterWrite = delegate { f.Control.Set(AppGpuPath, "GpuPreference=2;External=1;"); };
                AppGpuPreferenceResult result = f.Manager.Apply(change, false);
                AppGpuCheck(result != AppGpuPreferenceResult.Success, "unverified/failed registry write was reported applied: " + failure);
                if (failure == "not-issued") AppGpuCheck(!f.Manager.HasResidue && f.Control.Value(AppGpuPath) == null,
                    "definite write rejection retained ownership");
                if (failure == "external") AppGpuCheck(f.Manager.Remove(AppGpuPath) == AppGpuPreferenceResult.Success
                    && f.Control.Value(AppGpuPath) == "GpuPreference=2;External=1;", "external post-write change was overwritten during cleanup");
            }
        }

        private static void AppGpuUncertainApplySurvivesRestart(string root)
        {
            using (var f = new AppGpuFixture())
            {
                f.Control.ThrowAfterWrite = true;
                AppGpuCheck(f.Manager.Apply(f.Prepare(AppGpuPath), false) == AppGpuPreferenceResult.RecoveryPending,
                    "ambiguous apply lost its pending state");
                f.Control.ThrowAfterWrite = false; f.Reload();
                int writes = f.Control.Writes;
                AppGpuCheck(!f.Manager.HealFromCrash() && !f.Manager.RestoreAll() && f.Manager.HasResidue
                    && f.Control.Writes == writes, "reloaded P inferred ownership solely from current GPU1");
                AppGpuCheck(f.Manager.Forget(AppGpuPath) == AppGpuPreferenceResult.Success && !f.Manager.HasResidue
                    && f.Control.Value(AppGpuPath) == "GpuPreference=1;" && f.Control.Writes == writes,
                    "explicit forget did not retain the current Windows preference");
            }
            using (var f = new AppGpuFixture())
            {
                f.Ledger.Reject = delegate(string value) { return value.IndexOf("\nO\t", StringComparison.Ordinal) >= 0; };
                AppGpuCheck(f.Manager.Apply(f.Prepare(AppGpuPath), false) == AppGpuPreferenceResult.JournalFailed,
                    "failed owned-receipt persistence was reported successful");
                f.Ledger.Reject = null;
                AppGpuCheck(f.Manager.Remove(AppGpuPath) == AppGpuPreferenceResult.Success && f.Control.Value(AppGpuPath) == null,
                    "live known write receipt could not restore after journal access returned");
            }
        }

        private static void AppGpuRestorePreparationAndFailure(string root)
        {
            foreach (string failure in new[] { "journal", "not-issued" })
            using (var f = new AppGpuFixture())
            {
                f.Own("GpuPreference=2;");
                int writes = f.Control.Writes;
                if (failure == "journal") f.Ledger.Reject = delegate(string raw) { return raw.IndexOf("\nR\t", StringComparison.Ordinal) >= 0; };
                else f.Control.DenyWrite = true;
                AppGpuCheck(f.Manager.Remove(AppGpuPath) != AppGpuPreferenceResult.Success && f.Manager.HasResidue
                    && f.Control.Writes == writes, "failed restore preparation issued a native write or lost debt");
                f.Ledger.Reject = null; f.Control.DenyWrite = false;
                AppGpuCheck(f.Manager.Remove(AppGpuPath) == AppGpuPreferenceResult.Success
                    && f.Control.Value(AppGpuPath) == "GpuPreference=2;", "definitely unissued restore could not retry");
            }
        }

        private static void AppGpuRestoreIntentSurvivesRestart(string root)
        {
            using (var f = new AppGpuFixture())
            {
                f.Own("GpuPreference=2;");
                f.Control.ThrowBeforeWrite = true;
                AppGpuCheck(f.Manager.Remove(AppGpuPath) == AppGpuPreferenceResult.RecoveryPending, "ambiguous restore did not retain R");
                f.Control.ThrowBeforeWrite = false; f.Reload();
                int writes = f.Control.Writes;
                AppGpuCheck(!f.Manager.HealFromCrash() && !f.Manager.RestoreAll() && f.Control.Writes == writes,
                    "new process reissued an unknown restore against GPU1");
                f.Control.Set(AppGpuPath, "GpuPreference=2;");
                AppGpuCheck(f.Manager.HealFromCrash() && !f.Manager.HasResidue && f.Control.Writes == writes,
                    "observed restored target could not settle without another write");
            }
        }

        private static void AppGpuCleanupTombstoneSurvivesRestart(string root)
        {
            using (var f = new AppGpuFixture())
            {
                f.Own("GpuPreference=2;");
                f.Ledger.Reject = delegate(string value) { return value.Length == 0; };
                AppGpuCheck(f.Manager.Remove(AppGpuPath) == AppGpuPreferenceResult.JournalFailed
                    && f.Ledger.Value.IndexOf("\nS\t", StringComparison.Ordinal) >= 0, "successful restore did not retain a durable settled marker");
                f.Control.Set(AppGpuPath, "GpuPreference=1;User=keep;");
                int writes = f.Control.Writes; f.Reload(); f.Ledger.Reject = null;
                AppGpuCheck(f.Manager.RestoreAll() && !f.Manager.HasResidue && f.Control.Writes == writes
                    && f.Control.Value(AppGpuPath) == "GpuPreference=1;User=keep;", "settled cleanup reapplied restoration after the user changed GPU again");
            }
        }

        private static void AppGpuExternalChangesWin(string root)
        {
            using (var f = new AppGpuFixture())
            {
                f.Own(null); f.Control.Set(AppGpuPath, "GpuPreference=2;Other=user;");
                List<AppGpuPreferenceEntry> entries;
                AppGpuCheck(f.Manager.TryGetEntries(out entries) && entries[0].State == AppGpuPreferenceState.ExternalChange,
                    "external preference change was not relinquished");
                f.Control.Set(AppGpuPath, "GpuPreference=1;Other=later;"); f.Reload();
                int writes = f.Control.Writes;
                AppGpuCheck(f.Manager.Remove(AppGpuPath) == AppGpuPreferenceResult.Success
                    && f.Control.Writes == writes && f.Control.Value(AppGpuPath) == "GpuPreference=1;Other=later;",
                    "persisted external ownership was reacquired when GPU1 reappeared");
            }
        }

        private static void AppGpuConcurrentOtherFieldChange(string root)
        {
            using (var f = new AppGpuFixture())
            {
                f.Own("GpuPreference=2;Other=before;");
                f.Control.BeforeWrite = delegate { f.Control.Set(AppGpuPath, "GpuPreference=1;Other=user;"); };
                AppGpuCheck(f.Manager.Remove(AppGpuPath) == AppGpuPreferenceResult.Changed && f.Manager.HasResidue,
                    "CAS conflict in an unrelated field silently forgot GPU ownership");
                f.Control.BeforeWrite = null;
                AppGpuCheck(f.Manager.Remove(AppGpuPath) == AppGpuPreferenceResult.Success
                    && f.Control.Value(AppGpuPath) == "GpuPreference=2;Other=user;", "retry lost the newer unrelated field");
            }
        }

        private static void AppGpuStrictJournalAndPersistentList(string root)
        {
            string valid;
            using (var f = new AppGpuFixture()) { f.Own(null); valid = f.Ledger.Value; }
            string row = valid.Split('\n')[1];
            foreach (string invalid in new[] { "broken", valid.TrimEnd('\n'), "2\n" + row + "\n", "1\n" + row + "\n" + row + "\n", valid.Replace("\nO\t", "\nU\t"), valid.Replace("\nO\t", "\nZ\t") })
            using (var f = new AppGpuFixture())
            {
                f.Ledger.Value = invalid;
                List<AppGpuPreferenceEntry> entries;
                AppGpuCheck(f.Manager.HasResidue && f.Manager.HasManagedPath(AppGpuPath) && !f.Manager.TryGetEntries(out entries)
                    && !f.Manager.RestoreAll() && f.Control.Writes == 0 && f.Ledger.Value == invalid,
                    "malformed journal was discarded or used to mutate Windows preferences");
            }
            using (var f = new AppGpuFixture())
            {
                f.Own(null);
                const string second = @"C:\AppGpuFixture\second.exe";
                AppGpuCheck(f.Manager.Apply(f.Prepare(second), false) == AppGpuPreferenceResult.Success, "second explicit application was not added");
                f.Reload(); List<AppGpuPreferenceEntry> entries;
                AppGpuCheck(f.Manager.TryGetEntries(out entries) && entries.Count == 2 && f.Manager.HasManagedPath(AppGpuPath.ToUpperInvariant()),
                    "persistent list did not survive restart or used case-sensitive path identity");
                int writes = f.Control.Writes;
                AppGpuCheck(f.Manager.HealFromCrash() && f.Control.Writes == writes, "restart treated the explicit list as a session tweak");
                AppGpuCheck(f.Manager.RestoreAll() && !f.Manager.HasResidue && f.Control.Values.Count == 0,
                    "restore-all did not restore exactly the selected apps");
            }
        }

        private static void AppGpuSerializedAndReentrantCalls(string root)
        {
            using (var f = new AppGpuFixture())
            using (var entered = new ManualResetEvent(false))
            using (var release = new ManualResetEvent(false))
            using (var removalStarted = new ManualResetEvent(false))
            using (var removalDone = new ManualResetEvent(false))
            {
                AppGpuPreferenceChange change = f.Prepare(AppGpuPath);
                AppGpuPreferenceResult applied = AppGpuPreferenceResult.Busy, removed = AppGpuPreferenceResult.Busy;
                Exception error = null; int callbacks = 0;
                f.Control.BeforeWrite = delegate
                {
                    if (Interlocked.Increment(ref callbacks) != 1) return;
                    AppGpuCheck(f.Manager.Remove(AppGpuPath) == AppGpuPreferenceResult.Busy, "same-thread callback reentered a preference mutation");
                    entered.Set();
                    if (!release.WaitOne(3000)) throw new TimeoutException("Owned fake GPU write was not released");
                };
                var applying = new Thread(delegate() { try { applied = f.Manager.Apply(change, false); } catch (Exception ex) { error = ex; } });
                var removing = new Thread(delegate()
                {
                    removalStarted.Set();
                    try { removed = f.Manager.Remove(AppGpuPath); } catch (Exception ex) { error = ex; }
                    finally { removalDone.Set(); }
                });
                applying.IsBackground = removing.IsBackground = true;
                try
                {
                    applying.Start(); AppGpuCheck(entered.WaitOne(3000), "serialized fixture never reached its write");
                    removing.Start();
                    AppGpuCheck(removalStarted.WaitOne(3000) && !removalDone.WaitOne(20), "remove passed an in-flight apply before its receipt");
                    release.Set();
                    AppGpuCheck(applying.Join(3000) && removing.Join(3000) && error == null
                        && applied == AppGpuPreferenceResult.Success && removed == AppGpuPreferenceResult.Success
                        && !f.Manager.HasResidue && f.Control.Value(AppGpuPath) == null,
                        "serialized apply/remove lost its original or failed to drain");
                }
                finally
                {
                    release.Set();
                    if ((applying.ThreadState & ThreadState.Unstarted) == 0) applying.Join(3000);
                    if ((removing.ThreadState & ThreadState.Unstarted) == 0) removing.Join(3000);
                }
            }
        }

        private static void AppGpuResetRequiresKnownRecovery(string root)
        {
            using (var f = new AppGpuFixture())
            {
                var reset = new ResetFlowFixture(root, "app-gpu-reset");
                f.Own("GpuPreference=2;"); f.Control.DenyWrite = true;
                reset.SetRestore(delegate { return f.Manager.RestoreAll() ? new List<string>() : new List<string> { "app GPU recovery" }; });
                int files; string failure;
                AppGpuCheck(!Program.TryResetUserData(reset.DirectoryPath, reset.Stop(true), out files, out failure)
                    && f.Manager.HasResidue && reset.RegistryCalls == 0, "reset deleted data before app preference restoration succeeded");
                reset.AssertOriginalFiles(); reset.AssertRegistryPresent();
                f.Control.DenyWrite = false;
                AppGpuCheck(Program.TryResetUserData(reset.DirectoryPath, delegate { return true; }, out files, out failure)
                    && !f.Manager.HasResidue && f.Control.Value(AppGpuPath) == "GpuPreference=2;", "reset could not retry owned preference recovery");
                reset.AssertOwnedFilesGone(); reset.AssertForeignFiles();
            }
        }

        // 清除全部配置的放弃语义:崩溃窗口留下且被外部改写的 P 记录永远无法认领
        //   重置语境下按仅移除记录结清 保留系统现状;可认领的 O 记录与瞬时失败不放弃
        private static void AppGpuResetAbandonsUnprovableRecords(string root)
        {
            using (var f = new AppGpuFixture())
            {
                f.Control.Set(AppGpuPath, "GpuPreference=2;");
                AppGpuPreferenceChange change = f.Prepare(AppGpuPath);
                f.Control.ThrowBeforeWrite = true;
                AppGpuCheck(f.Manager.Apply(change, true) == AppGpuPreferenceResult.RecoveryPending,
                    "setup: the apply must crash into an uncertain P record");
                f.Control.ThrowBeforeWrite = false;
                // 外部把偏好改成节能 值既不是基线也无收据 从此无法认领
                f.Control.Set(AppGpuPath, "GpuPreference=1;External=keep;");
                AppGpuCheck(!f.Manager.RestoreAll(), "restore-all must refuse the unclaimable record");
                AppGpuCheck(f.Manager.AbandonUnprovableForReset(), "reset must abandon the unclaimable record");
                AppGpuCheck(!f.Manager.HasResidue
                    && f.Control.Value(AppGpuPath) == "GpuPreference=1;External=keep;",
                    "abandoning must clear the residue while keeping the external value");
            }
            using (var f = new AppGpuFixture())
            {
                f.Own("GpuPreference=2;");
                f.Control.DenyWrite = true;
                AppGpuCheck(!f.Manager.RestoreAll(), "setup: a transient write failure fails restore-all");
                AppGpuCheck(!f.Manager.AbandonUnprovableForReset() && f.Manager.HasResidue,
                    "an owned record must never be abandoned by reset");
                f.Control.DenyWrite = false;
                AppGpuCheck(f.Manager.RestoreAll() && !f.Manager.HasResidue
                    && f.Control.Value(AppGpuPath) == "GpuPreference=2;",
                    "the owned record must still restore after the failure clears");
            }
        }
    }
}
#endif
