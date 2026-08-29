// Headless configuration regressions: no PanelForm construction, native windows,
// process affinity changes or real registry writes.
#if PAVISE_SELFTEST
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Windows.Forms;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        internal static int RunUiConfigAuditRegressionTests()
        {
            string root = Path.Combine(Path.GetTempPath(), "PaviseUiConfigAudit-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string oldLog = Logger.LogPath;
            int oldLanguage = Lang.Cur;
            try
            {
                Settings.UseTransientStoreForCurrentProcess();
                Logger.ResetWriteBarrierForTest();
                Logger.LogPath = Path.Combine(root, "decisions.log");
                Lang.Cur = 0;
                int failed = 0;
                Action<string>[] cases = { UiConfigManualAllOverridesGlobalPartition,
                    UiConfigGlobalManualAllIsDirty, UiConfigGlobalManualAllClearsPartition,
                    UiConfigProfilePartitionToManualAllIsDirty, UiConfigCorePartialFollowAndTopology,
                    UiConfigGlobalPartialAndAllPreset,
                    UiConfigPowerYieldFusedRemainsEditable, UiConfigPowerYieldDescriptionRecovers,
                    UiConfigPowerYieldProfileCanRetry, UiConfigPowerYieldGlobalConsent,
                    UiConfigPowerYieldCapabilityLoss, UiConfigPowerYieldProfileConsent,
                    UiConfigPowerYieldInheritanceAndClear, UiConfigGuardedFailedSaves,
                    UiConfigAccessibilityPartialBackupRemainsVisible,
                    UiConfigUnsupportedGraphicsRemainDisableable,
                    UiConfigUnsupportedGlobalGraphicsRemainDisableable,
                    UiConfigCachedAmdSupportNeedsApi,
                    UiConfigSupportedVendorProfiles, UiConfigGraphicsBindingContracts,
                    UiConfigGraphicsPresentationDoesNotProbe,
                    UiConfigGraphicsInheritanceAndFailures, UiConfigCoreFailedSave,
                    UiConfigVramGlobalFailedSave, UiConfigVramGlobalConsentAndFailedOff };
                foreach (Action<string> test in cases)
                {
                    try { test(root); Console.WriteLine("PASS " + test.Method.Name); }
                    catch (Exception error)
                    {
                        while (error.InnerException != null) error = error.InnerException;
                        failed++;
                        Console.WriteLine("FAIL " + test.Method.Name + ": " + error.Message);
                    }
                }
                if (failed != 0) throw new InvalidOperationException("UI config audit failures=" + failed);
                Console.WriteLine("PASS UI config audit windows_shown=false affinity_writes=0 registry=mocked");
                return cases.Length;
            }
            finally
            {
                Lang.Cur = oldLanguage;
                Logger.ResetWriteBarrierForTest();
                Logger.LogPath = oldLog;
                Settings.UseTransientStoreForCurrentProcess();
                string actual = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
                string temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
                if (actual.StartsWith(temporary + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    && Path.GetFileName(actual).StartsWith("PaviseUiConfigAudit-", StringComparison.Ordinal)
                    && Directory.Exists(actual)) Directory.Delete(actual, true);
            }
        }

        private static void UiConfigManualAllOverridesGlobalPartition(string root)
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
            FieldInfo bgField = typeof(CpuTopology).GetField("backgroundIds", flags);
            FieldInfo gameField = typeof(CpuTopology).GetField("partitionGameIds", flags);
            FieldInfo customField = typeof(CpuTopology).GetField("customSet", flags);
            object oldBackground = bgField.GetValue(null);
            object oldGame = gameField.GetValue(null);
            object oldCustom = customField.GetValue(null);
            ulong oldAll = CpuTopology.AllMask;
            try
            {
                // Pure topology values only. SetCustomMask/Initialize are not called.
                CpuTopology.AllMask = 0xFUL;
                bgField.SetValue(null, new uint[] { 2, 3 });
                gameField.SetValue(null, new uint[] { 0, 1 });
                customField.SetValue(null, null);
                using (var fixture = new FamilyPolicyFixture(root, "manual-all"))
                {
                    Settings.Save(PolicyCatalog.KeyStrictCores, true);
                    FamilyPolicySetField(fixture.Mode, "gameMask", 0xFUL);
                    FamilyPolicySetField(fixture.Mode, "strictMask", 0x3UL);
                    var form = (PanelForm)FormatterServices.GetUninitializedObject(typeof(PanelForm));
                    GC.SuppressFinalize(form);
                    UiConfigSetField(form, "gameMode", fixture.Mode);
                    UiConfigSetField(form, "cfgProfileId", fixture.First.Id);
                    UiConfigSetField(form, "cfgProfile", fixture.Current(fixture.First.Id));
                    UiConfigSetField(form, "cfgRowSync", new List<Action>());
                    UiConfigSetField(form, "cfgCorePending", 0xFUL);
                    UiConfigSetField(form, "cfgCorePartitionAvailable", true);
                    UiConfigSetField(form, "cfgCorePartIndex", 2);
                    UiConfigCall(form, "ApplyCfgCoreMask");

                    GameProfile saved = fixture.Current(fixture.First.Id);
                    PolicySnapshot snapshot = PolicyResolver.For(saved);
                    object[] arguments = { snapshot, false };
                    ulong actual = (ulong)typeof(GameMode).GetMethod("EffectiveGameMask",
                        BindingFlags.Instance | BindingFlags.NonPublic).Invoke(fixture.Mode, arguments);
                    string summary = (string)UiConfigCall(form, "CfgCoreSummary");
                    Console.WriteLine("MANUAL_ALL summary=" + summary + " saved_mask='"
                        + saved.Overrides[PolicyCatalog.KeyCoreMask] + "' inherited_strict="
                        + snapshot.StrictCores + " actual_mask=" + actual.ToString("X"));
                    Eq(Lang.F("cfg.core.over", Lang.T("cpu.place.all")), summary);
                    Eq(false, snapshot.StrictCores);
                    Eq(0xFUL, actual);
                }
            }
            finally
            {
                CpuTopology.AllMask = oldAll;
                bgField.SetValue(null, oldBackground);
                gameField.SetValue(null, oldGame);
                customField.SetValue(null, oldCustom);
            }
        }

        private sealed class UiConfigCoreFixture : IDisposable
        {
            internal readonly FamilyPolicyFixture Family;
            internal readonly PanelForm Form;
            internal readonly DBPanel Panel = new DBPanel();
            private readonly CpuTopology.TopologySnapshot topology = CpuTopology.CaptureTopologyForTest();
            private readonly Dictionary<FieldInfo, object> originals = new Dictionary<FieldInfo, object>();

            internal UiConfigCoreFixture(string root, string name)
            {
                foreach (string field in new[] { "backgroundIds", "partitionGameIds", "customSet", "squeezeCache" })
                {
                    FieldInfo info = typeof(CpuTopology).GetField(field, BindingFlags.Static | BindingFlags.NonPublic);
                    originals.Add(info, info.GetValue(null));
                }
                CpuTopology.InjectTopologyForTest(0xF, new ulong[] { 1, 2, 4, 8 }, new ulong[] { 3, 12 }, 0, 0, 0, 0, false, false);
                typeof(CpuTopology).GetField("backgroundIds", BindingFlags.Static | BindingFlags.NonPublic)
                    .SetValue(null, new uint[] { 2, 3 });
                typeof(CpuTopology).GetField("partitionGameIds", BindingFlags.Static | BindingFlags.NonPublic)
                    .SetValue(null, new uint[] { 0, 1 });
                Family = new FamilyPolicyFixture(root, name);
                FamilyPolicySetField(Family.Mode, "gameMask", 0xFUL);
                FamilyPolicySetField(Family.Mode, "strictMask", 0x3UL);
                Family.Mode.CorePartitionEnabled = true;
                Form = (PanelForm)FormatterServices.GetUninitializedObject(typeof(PanelForm));
                GC.SuppressFinalize(Form);
                UiConfigSetField(Form, "gameMode", Family.Mode);
                UiConfigSetField(Form, "cfgProfileId", Family.First.Id);
                UiConfigSetField(Form, "cfgProfile", Family.Current(Family.First.Id));
                UiConfigSetField(Form, "cfgRowSync", new List<Action>());
                UiConfigSetField(Form, "cfgCoreAllIndex", 1);
                UiConfigSetField(Form, "cfgCorePartIndex", 2);
                UiConfigSetField(Form, "cfgCoreManualIndex", 3);
                UiConfigSetField(Form, "cfgCorePartitionAvailable", true);
                UiConfigSetField(Form, "corePending", 0xFUL);
                UiConfigSetField(Form, "coreManualPicked", true);
                UiConfigSetField(Form, "coreManualIndex", 2);
                var picker = new TierPicker { Labels = new[] { "All", "Partition", "Manual" }, Index = 2 };
                UiConfigSetField(Form, "pickPolicyCores", picker);
                Panel.Controls.Add(picker);
                UiConfigCall(Form, "BuildCoreManualGroup", Panel, 0xFUL);
            }

            internal void SetFakeCustom(ulong mask)
            {
                Type type = typeof(CpuTopology).GetNestedType("CustomCoreSet", BindingFlags.NonPublic);
                object custom = mask == 0 ? null : Activator.CreateInstance(type, true);
                if (custom != null)
                {
                    type.GetField("Mask").SetValue(custom, mask);
                    type.GetField("Ids").SetValue(custom, new uint[] { 0, 1 });
                }
                typeof(CpuTopology).GetField("customSet", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, custom);
            }

            internal PillButton ApplyButton { get { return (PillButton)UiConfigGetField(Form, "coreApplyBtn"); } }

            public void Dispose()
            {
                Eq(false, Panel.IsHandleCreated);
                Panel.Dispose(); Family.Dispose();
                CpuTopology.RestoreTopologyForTest(topology);
                foreach (KeyValuePair<FieldInfo, object> old in originals) old.Key.SetValue(null, old.Value);
            }
        }

        private static void UiConfigGlobalManualAllIsDirty(string root)
        {
            using (var fixture = new UiConfigCoreFixture(root, "global-manual-dirty"))
            {
                UiConfigCall(fixture.Form, "SyncCorePage");
                Console.WriteLine("GLOBAL_ALL partition=" + fixture.Family.Mode.CorePartitionEnabled
                    + " custom=" + CpuTopology.CustomMask + " apply_enabled=" + fixture.ApplyButton.Enabled);
                Eq(true, fixture.ApplyButton.Enabled);
            }
        }

        private static void UiConfigGlobalManualAllClearsPartition(string root)
        {
            using (var fixture = new UiConfigCoreFixture(root, "global-manual-apply"))
            {
                fixture.SetFakeCustom(3);
                UiConfigCall(fixture.Form, "SyncCorePage");
                Eq(true, fixture.ApplyButton.Enabled);
                // A zero custom mask only clears the managed in-memory mask; it
                // never invokes CPU-set enumeration or changes process affinity.
                fixture.ApplyButton.PerformClick();
                Console.WriteLine("GLOBAL_ALL after_apply partition=" + fixture.Family.Mode.CorePartitionEnabled
                    + " custom=" + CpuTopology.CustomMask);
                Eq(false, fixture.Family.Mode.CorePartitionEnabled);
                Eq(false, Settings.Load(PolicyCatalog.KeyStrictCores, true));
                Eq(0UL, CpuTopology.CustomMask);
            }
        }

        private static void UiConfigProfilePartitionToManualAllIsDirty(string root)
        {
            using (var fixture = new UiConfigCoreFixture(root, "profile-partition-to-all"))
            {
                UiConfigCall(fixture.Form, "ApplyCfgCorePlacement", 2);
                var picker = new TierPicker { Labels = new[] { "Follow", "All", "Partition", "Manual" }, Index = 3 };
                fixture.Panel.Controls.Add(picker);
                UiConfigSetField(fixture.Form, "cfgCorePicker", picker);
                UiConfigSetField(fixture.Form, "cfgCoreManualPicked", true);
                UiConfigSetField(fixture.Form, "cfgCorePending", 0xFUL);
                UiConfigCall(fixture.Form, "BuildCfgCoreManualGroup", fixture.Panel, 0);
                UiConfigCall(fixture.Form, "SyncCfgCoreTab");
                var apply = (PillButton)UiConfigGetField(fixture.Form, "cfgCoreApply");
                Console.WriteLine("PROFILE_ALL from_partition apply_enabled=" + apply.Enabled);
                Eq(true, apply.Enabled);
                apply.PerformClick();
                Eq(false, PolicyResolver.For(fixture.Family.Current(fixture.Family.First.Id)).StrictCores);
            }
        }

        private static void UiConfigCorePartialFollowAndTopology(string root)
        {
            using (var fixture = new UiConfigCoreFixture(root, "profile-core-transitions"))
            {
                UiConfigSetField(fixture.Form, "cfgCorePending", 0xCUL);
                UiConfigCall(fixture.Form, "ApplyCfgCoreMask");
                GameProfile profile = fixture.Family.Current(fixture.Family.First.Id);
                Eq("C", profile.Overrides[PolicyCatalog.KeyCoreMask]);
                Eq(false, profile.Overrides.ContainsKey(PolicyCatalog.KeyStrictCores));
                Eq("0", profile.Overrides[PolicyCatalog.KeyBoost]);
                Eq("2", profile.Overrides[PolicyCatalog.KeyPreset]);
                fixture.SetFakeCustom(0xC);
                object[] args = { PolicyResolver.For(profile), false };
                Eq(0xCUL, (ulong)FamilyPolicyInvoke(fixture.Family.Mode, "EffectiveGameMask", args));

                UiConfigCall(fixture.Form, "ApplyCfgCorePlacement", 0);
                profile = fixture.Family.Current(fixture.Family.First.Id);
                Eq(false, profile.Overrides.ContainsKey(PolicyCatalog.KeyCoreMask));
                Eq(true, PolicyResolver.For(profile).StrictCores);
                Eq("0", profile.Overrides[PolicyCatalog.KeyBoost]);
                UiConfigCall(fixture.Form, "ApplyCfgCorePlacement", 1);
                profile = fixture.Family.Current(fixture.Family.First.Id);
                Eq("0", profile.Overrides[PolicyCatalog.KeyStrictCores]);
                Eq("", profile.Overrides[PolicyCatalog.KeyCoreMask]);
                Eq("0", profile.Overrides[PolicyCatalog.KeyBoost]);

                // The all-core override remains all cores after a topology change;
                // no saved finite mask from the old machine may restrict the new one.
                fixture.SetFakeCustom(0);
                CpuTopology.AllMask = 0xFF;
                FamilyPolicySetField(fixture.Family.Mode, "gameMask", 0xFFUL);
                args = new object[] { PolicyResolver.For(profile), false };
                Eq(0xFFUL, (ulong)FamilyPolicyInvoke(fixture.Family.Mode, "EffectiveGameMask", args));
            }
        }

        private static void UiConfigGlobalPartialAndAllPreset(string root)
        {
            using (var fixture = new UiConfigCoreFixture(root, "global-core-transitions"))
            {
                // Active-session edits store next-session intent without native
                // CPU-set enumeration. No game worker is started in this fixture.
                FamilyPolicySetField(fixture.Family.Mode, "active", true);
                UiConfigSetField(fixture.Form, "corePending", 0xCUL);
                UiConfigCall(fixture.Form, "SyncCorePage");
                fixture.ApplyButton.PerformClick();
                Eq("C", Settings.LoadStr(PolicyCatalog.KeyCoreMask, ""));
                Eq(true, fixture.Family.Mode.CorePartitionEnabled);
                UiConfigCall(fixture.Form, "ApplyCorePlacement", 0);
                Eq(false, fixture.Family.Mode.CorePartitionEnabled);
                Eq("", Settings.LoadStr(PolicyCatalog.KeyCoreMask, "missing"));
                Eq(false, (bool)UiConfigGetField(fixture.Form, "coreManualPicked"));
            }
        }

        private sealed class UiConfigPowerYieldFixture : IDisposable
        {
            internal readonly FamilyPolicyFixture Family;
            internal readonly PanelForm Form;
            internal readonly Toggle Toggle;
            internal readonly SettingCard Card;
            internal readonly DBPanel ProfilePanel = new DBPanel();
            private readonly Dictionary<FieldInfo, object> originals = new Dictionary<FieldInfo, object>();
            private readonly IntelLowLatencyEngine previousIntel;
            private readonly IntelFakeControl intelApi;

            internal UiConfigPowerYieldFixture(string root, string name)
            {
                // Seed discovery caches rather than opening system power/EMI handles.
                SetStatic(typeof(Native), "hasBattery", 1);
                SetStatic(typeof(EnergyMeter), "probed", true);
                SetStatic(typeof(EnergyMeter), "devicePath", "mock-energy-meter");
                SetStatic(typeof(EnergyMeter), "unreadable", false);
                SetStatic(typeof(EnergyMeter), "railIndex", new[] { 0, -1, -1, -1 });
                SetStatic(typeof(NvApi), "state", -1);
                SetStatic(typeof(AdlxApi), "state", -1);
                SetStatic(typeof(GpuInventory), "cached", new GpuAdapter[0]);
                var intelLedger = new IntelFakeLedger();
                intelApi = new IntelFakeControl(intelLedger);
                previousIntel = IntelGraphicsTweaks.ReplaceEngineForTest(new IntelLowLatencyEngine(intelApi, intelLedger));
                Family = new FamilyPolicyFixture(root, name);
                Form = (PanelForm)FormatterServices.GetUninitializedObject(typeof(PanelForm));
                GC.SuppressFinalize(Form);
                Toggle = new Toggle(); Card = new SettingCard();
                UiConfigSetField(Form, "gameMode", Family.Mode);
                UiConfigSetField(Form, "elevated", true);
                UiConfigSetField(Form, "swPolicyPowerYield", Toggle);
                UiConfigSetField(Form, "cardPolicyPowerYield", Card);
                UiConfigSetField(Form, "cfgProfileId", Family.First.Id);
                UiConfigSetField(Form, "cfgProfile", Family.Current(Family.First.Id));
                UiConfigSetField(Form, "cfgRowSync", new List<Action>());
                UiConfigSetField(Form, "cfgCardByKey", new Dictionary<string, SettingCard>());
            }

            internal void SetStatic(Type type, string name, object value)
            {
                FieldInfo field = type.GetField(name, BindingFlags.Static | BindingFlags.NonPublic);
                originals.Add(field, field.GetValue(null));
                field.SetValue(null, value);
            }

            internal void Refresh()
            {
                Toggle.SetSilently(PowerBudgetYieldRunner.EnabledSetting);
                UiConfigCall(Form, "RefreshPolicyPresentation");
            }

            internal void Capabilities(bool laptop, bool watts, bool elevated)
            {
                typeof(Native).GetField("hasBattery", BindingFlags.Static | BindingFlags.NonPublic)
                    .SetValue(null, laptop ? 1 : 0);
                typeof(EnergyMeter).GetField("unreadable", BindingFlags.Static | BindingFlags.NonPublic)
                    .SetValue(null, !watts);
                UiConfigSetField(Form, "elevated", elevated);
            }

            internal void RefreshProfile() { UiConfigCall(Form, "SyncCfgRows"); }

            internal void BuildGlobalGraphics(DBPanel page)
            {
                UiConfigSetField(Form, "pageGraphics", page);
                UiConfigSetField(Form, "pageTabPanels", new Dictionary<Control, DBPanel[]>());
                UiConfigSetField(Form, "tabScrollPositions", new Dictionary<DBPanel, Point>());
                UiConfigSetField(Form, "stackBase", new Dictionary<Control, Dictionary<Control, int>>());
                UiConfigSetField(Form, "graphicsSync", new List<Action>());
                UiConfigCall(Form, "BuildGraphicsPage");
            }

            internal bool Pick(string value)
            {
                bool result = (bool)UiConfigCall(Form, "ApplyCfgPolicyChoice", PolicyCatalog.KeyPowerYield, value);
                RefreshProfile();
                return result;
            }

            internal TierPicker BuildProfilePicker()
            {
                return BuildProfilePicker(PolicyCatalog.KeyPowerYield);
            }

            internal TierPicker BuildProfilePicker(string key)
            {
                object[] args = { ProfilePanel, 0, PolicyCatalog.ItemOf(key) };
                UiConfigCall(Form, "AddCfgPickerRow", args);
                RefreshProfile();
                foreach (Control card in ProfilePanel.Controls)
                    foreach (Control child in card.Controls)
                        if (child is TierPicker) return (TierPicker)child;
                throw new InvalidOperationException("Policy picker was not built: " + key);
            }

            public void Dispose()
            {
                try
                {
                    Eq(false, ProfilePanel.IsHandleCreated);
                    Eq(0, intelApi.Writes);
                }
                finally
                {
                    Toggle.Dispose(); Card.Dispose(); ProfilePanel.Dispose(); Family.Dispose();
                    IntelGraphicsTweaks.ReplaceEngineForTest(previousIntel);
                    foreach (KeyValuePair<FieldInfo, object> old in originals) old.Key.SetValue(null, old.Value);
                }
            }
        }

        private static void UiConfigPowerYieldFailValidation()
        {
            Settings.Save(PowerBudgetYieldRunner.EnabledKey, true);
            var state = new PowerBudgetYield();
            state.Begin(0, true);
            YieldAction action = YieldAction.None;
            for (int sample = 1; sample <= PowerBudgetYield.MinSamples; sample++)
                action = state.Advance(PowerBudgetYield.ObserveTicks * sample / PowerBudgetYield.MinSamples, 95, 30, 50);
            Eq(YieldAction.Engage, action);
            for (int sample = 1; sample <= PowerBudgetYield.MinSamples; sample++)
                action = state.Advance(PowerBudgetYield.ObserveTicks
                    + PowerBudgetYield.VerifyTicks * sample / PowerBudgetYield.MinSamples, 95, 30, 50);
            Eq(YieldAction.Revert, action);
            Eq(true, PowerBudgetYield.Fused);
            Eq(true, PowerBudgetYieldRunner.EnabledSetting);
        }

        private static void UiConfigPowerYieldFusedRemainsEditable(string root)
        {
            using (var fixture = new UiConfigPowerYieldFixture(root, "power-yield-fused"))
            {
                UiConfigPowerYieldFailValidation();
                fixture.Refresh();
                Console.WriteLine("POWER_YIELD fused=" + PowerBudgetYield.Fused
                    + " saved_on=" + PowerBudgetYieldRunner.EnabledSetting + " UI_enabled=" + fixture.Toggle.Enabled);
                Eq(true, fixture.Toggle.Checked);
                Eq(true, fixture.Toggle.Enabled);
                Eq(Lang.T("gm.poweryield.fused"), fixture.Card.Desc);
            }
        }

        private static void UiConfigPowerYieldDescriptionRecovers(string root)
        {
            using (var fixture = new UiConfigPowerYieldFixture(root, "power-yield-description"))
            {
                UiConfigPowerYieldFailValidation();
                fixture.Refresh();
                Eq(Lang.T("gm.poweryield.fused"), fixture.Card.Desc);
                PowerBudgetYield.ClearFuse();
                fixture.Refresh();
                Eq(Lang.T("gm.poweryield.sub"), fixture.Card.Desc);
                Eq(false, fixture.Toggle.IsHandleCreated);
                Eq(false, fixture.Card.IsHandleCreated);
            }
        }

        private static void UiConfigPowerYieldProfileCanRetry(string root)
        {
            using (var fixture = new UiConfigPowerYieldFixture(root, "power-yield-profile-retry"))
            {
                UiConfigPowerYieldFailValidation();
                object[] arguments = { PolicyCatalog.ItemOf(PolicyCatalog.KeyPowerYield), null };
                bool supported = (bool)UiConfigCall(fixture.Form, "CfgItemSupported", arguments);
                Console.WriteLine("POWER_YIELD profile_supported=" + supported + " reason=" + arguments[1]);
                Eq(true, supported);
            }
        }

        private static void UiConfigPowerYieldGlobalConsent(string root)
        {
            using (var fixture = new UiConfigPowerYieldFixture(root, "power-yield-global-consent"))
            {
                UiConfigPowerYieldFailValidation();
                int prompts = 0; bool accept = false;
                fixture.Form.PowerYieldConfirmationForTest = delegate
                {
                    prompts++;
                    Eq(false, PowerBudgetYieldRunner.EnabledSetting);
                    Eq(true, PowerBudgetYield.Fused);
                    return accept;
                };
                UiConfigCall(fixture.Form, "OnPowerYieldToggle", false);
                Eq(0, prompts); Eq(true, PowerBudgetYield.Fused);
                UiConfigCall(fixture.Form, "OnPowerYieldToggle", true);
                Eq(1, prompts); Eq(false, PowerBudgetYieldRunner.EnabledSetting);
                Eq(false, fixture.Toggle.Checked); Eq(true, PowerBudgetYield.Fused);
                accept = true;
                UiConfigCall(fixture.Form, "OnPowerYieldToggle", true);
                Eq(2, prompts); Eq(true, PowerBudgetYieldRunner.EnabledSetting);
                Eq(false, PowerBudgetYield.Fused); Eq(Lang.T("gm.poweryield.sub"), fixture.Card.Desc);
            }
        }

        private static void UiConfigPowerYieldCapabilityLoss(string root)
        {
            for (int missing = 0; missing < 3; missing++)
                using (var fixture = new UiConfigPowerYieldFixture(root, "power-yield-capability-" + missing))
                {
                    UiConfigPowerYieldFailValidation();
                    fixture.Capabilities(missing != 0, missing != 1, missing != 2);
                    fixture.Form.PowerYieldConfirmationForTest = delegate { throw new Exception("Unsupported enable prompted"); };
                    fixture.Refresh();
                    Eq(true, fixture.Toggle.Checked); Eq(true, fixture.Toggle.Enabled);
                    UiConfigCall(fixture.Form, "OnPowerYieldToggle", false);
                    Eq(false, fixture.Toggle.Enabled); Eq(false, PowerBudgetYieldRunner.EnabledSetting);
                    UiConfigCall(fixture.Form, "OnPowerYieldToggle", true);
                    Eq(false, PowerBudgetYieldRunner.EnabledSetting); Eq(true, PowerBudgetYield.Fused);

                    fixture.Family.Mode.SetProfileOverride(fixture.Family.First.Id, PolicyCatalog.KeyPowerYield, "1");
                    fixture.RefreshProfile();
                    TierPicker picker = fixture.BuildProfilePicker();
                    Eq(true, picker.Enabled); Eq(2, picker.Index);
                    Eq(true, fixture.Pick("0")); Eq(false, picker.Enabled);
                    Eq(false, fixture.Pick("1"));
                    Eq("0", fixture.Family.Current(fixture.Family.First.Id).Overrides[PolicyCatalog.KeyPowerYield]);
                }
        }

        private static void UiConfigPowerYieldProfileConsent(string root)
        {
            using (var fixture = new UiConfigPowerYieldFixture(root, "power-yield-profile-consent"))
            {
                UiConfigPowerYieldFailValidation();
                Settings.Save(PowerBudgetYieldRunner.EnabledKey, false);
                int prompts = 0; bool accept = false;
                string before = File.ReadAllText(fixture.Family.LibraryFile);
                fixture.Form.PowerYieldConfirmationForTest = delegate
                {
                    prompts++; Eq(before, File.ReadAllText(fixture.Family.LibraryFile));
                    Eq(true, PowerBudgetYield.Fused); return accept;
                };
                Eq(false, fixture.Pick("1")); Eq(1, prompts);
                Eq(before, File.ReadAllText(fixture.Family.LibraryFile)); Eq(true, PowerBudgetYield.Fused);
                accept = true;
                Eq(true, fixture.Pick("1")); Eq(2, prompts);
                Eq(false, PowerBudgetYield.Fused);
                Eq("1", fixture.Family.Current(fixture.Family.First.Id).Overrides[PolicyCatalog.KeyPowerYield]);
                Eq(true, fixture.Pick("0")); Eq(2, prompts);
                Eq("0", fixture.Family.Current(fixture.Family.First.Id).Overrides[PolicyCatalog.KeyPowerYield]);
            }
        }

        private static void UiConfigPowerYieldInheritanceAndClear(string root)
        {
            foreach (bool clearAll in new[] { false, true })
                using (var fixture = new UiConfigPowerYieldFixture(root, "power-yield-inherit-" + clearAll))
                {
                    UiConfigPowerYieldFailValidation();
                    fixture.Family.Mode.SetProfileOverride(fixture.Family.First.Id, PolicyCatalog.KeyPowerYield, "0");
                    fixture.RefreshProfile();
                    string before = File.ReadAllText(fixture.Family.LibraryFile);
                    bool accept = false; int prompts = 0;
                    fixture.Form.PowerYieldConfirmationForTest = delegate
                    {
                        prompts++; Eq(before, File.ReadAllText(fixture.Family.LibraryFile)); return accept;
                    };
                    Func<bool> apply = delegate
                    {
                        return clearAll ? (bool)UiConfigCall(fixture.Form, "ApplyCfgClearAllOverrides") : fixture.Pick(null);
                    };
                    Eq(false, apply()); Eq(1, prompts); Eq(true, PowerBudgetYield.Fused);
                    Eq(before, File.ReadAllText(fixture.Family.LibraryFile));
                    accept = true;
                    Eq(true, apply()); Eq(2, prompts); Eq(false, PowerBudgetYield.Fused);
                    GameProfile current = fixture.Family.Current(fixture.Family.First.Id);
                    Eq(false, current.Overrides.ContainsKey(PolicyCatalog.KeyPowerYield));
                    Eq(true, PolicyResolver.For(current).PowerYield);
                    if (!clearAll) Eq("0", current.Overrides[PolicyCatalog.KeyBoost]);
                }
        }

        private static void UiConfigGuardedFailedSaves(string root)
        {
            using (var fixture = new UiConfigPowerYieldFixture(root, "power-yield-global-save-failed"))
            {
                UiConfigPowerYieldFailValidation();
                Settings.Save(PowerBudgetYieldRunner.EnabledKey, false);
                fixture.Form.PowerYieldConfirmationForTest = delegate { return true; };
                Settings.SuspendWritesForReset();
                UiConfigCall(fixture.Form, "OnPowerYieldToggle", true);
                Eq(false, PowerBudgetYieldRunner.EnabledSetting);
                Eq(false, fixture.Toggle.Checked); Eq(true, PowerBudgetYield.Fused);
            }
            using (var fixture = new UiConfigPowerYieldFixture(root, "power-yield-profile-save-failed"))
            {
                UiConfigPowerYieldFailValidation();
                Settings.Save(PowerBudgetYieldRunner.EnabledKey, false);
                fixture.Form.PowerYieldConfirmationForTest = delegate { return true; };
                string before = File.ReadAllText(fixture.Family.LibraryFile);
                using (var lease = new FileStream(fixture.Family.LibraryFile, FileMode.Open, FileAccess.Read, FileShare.Read))
                    Eq(false, fixture.Pick("1"));
                Eq(true, fixture.Family.Mode.ProfileStoreSaveFailed);
                Eq(before, File.ReadAllText(fixture.Family.LibraryFile)); Eq(true, PowerBudgetYield.Fused);
            }
            using (var fixture = new UiConfigPowerYieldFixture(root, "vram-profile-consent"))
            {
                Settings.Save("VramShieldFuse", true);
                int prompts = 0;
                bool accept = false;
                string before = File.ReadAllText(fixture.Family.LibraryFile);
                fixture.Form.VramShieldConfirmationForTest = delegate
                {
                    prompts++;
                    Eq(before, File.ReadAllText(fixture.Family.LibraryFile));
                    Eq(true, VramShield.Fused);
                    return accept;
                };
                Eq(false, (bool)UiConfigCall(fixture.Form, "ApplyCfgPolicyChoice", PolicyCatalog.KeyVramShield, "1"));
                Eq(1, prompts); Eq(true, VramShield.Fused);
                Eq(before, File.ReadAllText(fixture.Family.LibraryFile));
                accept = true;
                Eq(true, (bool)UiConfigCall(fixture.Form, "ApplyCfgPolicyChoice", PolicyCatalog.KeyVramShield, "1"));
                Eq(2, prompts); Eq(false, VramShield.Fused);
                Eq("1", fixture.Family.Current(fixture.Family.First.Id).Overrides[PolicyCatalog.KeyVramShield]);
            }
            using (var fixture = new UiConfigPowerYieldFixture(root, "vram-profile-save-failed"))
            {
                Settings.Save("VramShieldFuse", true);
                int prompts = 0;
                fixture.Form.VramShieldConfirmationForTest = delegate { prompts++; return true; };
                string before = File.ReadAllText(fixture.Family.LibraryFile);
                bool saved;
                using (var lease = new FileStream(fixture.Family.LibraryFile, FileMode.Open, FileAccess.Read, FileShare.Read))
                    saved = (bool)UiConfigCall(fixture.Form, "ApplyCfgPolicyChoice", PolicyCatalog.KeyVramShield, "1");
                Console.WriteLine("VRAM_PROFILE saved=" + saved + " fuse=" + VramShield.Fused);
                Eq(false, saved); Eq(1, prompts);
                Eq(true, fixture.Family.Mode.ProfileStoreSaveFailed);
                Eq(before, File.ReadAllText(fixture.Family.LibraryFile)); Eq(true, VramShield.Fused);
            }
        }

        private static void UiConfigAccessibilityPartialBackupRemainsVisible(string root)
        {
            var form = (PanelForm)FormatterServices.GetUninitializedObject(typeof(PanelForm));
            GC.SuppressFinalize(form);
            using (var toggle = new Toggle())
            {
                UiConfigSetField(form, "swAccessKeys", toggle);
                foreach (string slot in new[] { "PrevFilterKeys", "PrevStickyKeys", "PrevToggleKeys" })
                {
                    Settings.UseTransientStoreForCurrentProcess();
                    Settings.Save("AccessKeysByPavise", false);
                    // A completed first registry mutation and a failed later one
                    // leave a recovery slot without the all-success marker.
                    Settings.SaveStr(slot, "=59\u001F=58");
                    Eq(true, AccessibilityKeysTweak.HasResidue());
                    Eq(false, AccessibilityKeysTweak.EnabledByPavise);
                    toggle.SetSilently(AccessibilityKeysTweak.HasResidue());
                    UiConfigCall(form, "SyncEnvironmentToggles");
                    Console.WriteLine("ACCESS_KEYS slot=" + slot + " residue=" + AccessibilityKeysTweak.HasResidue()
                        + " success_flag=" + AccessibilityKeysTweak.EnabledByPavise + " UI_checked=" + toggle.Checked);
                    Eq(true, toggle.Checked);
                    Eq("=59\u001F=58", Settings.LoadStr(slot, ""));
                }
                Settings.UseTransientStoreForCurrentProcess();
                Settings.Save("AccessKeysByPavise", true);
                UiConfigCall(form, "SyncEnvironmentToggles");
                Eq(true, toggle.Checked);
                Settings.Save("AccessKeysByPavise", false);
                UiConfigCall(form, "SyncEnvironmentToggles");
                Eq(false, toggle.Checked);
                Eq(false, toggle.IsHandleCreated);
            }
        }

        private static void UiConfigUnsupportedGraphicsRemainDisableable(string root)
        {
            string[] keys = { PolicyCatalog.KeyNvMaxPerf, PolicyCatalog.KeyNvLowLat,
                PolicyCatalog.KeyNvSmoothMotion, PolicyCatalog.KeyNvShaderCache,
                PolicyCatalog.KeyNvRebar, PolicyCatalog.KeyNvDlss,
                PolicyCatalog.KeyAmdAntiLag, PolicyCatalog.KeyAmdAfmf };
            int trapped = 0;
            foreach (string key in keys)
                using (var fixture = new UiConfigPowerYieldFixture(root, "graphics-unsupported-" + key))
                {
                    PolicyItem item = PolicyCatalog.ItemOf(key);
                    string stored = item.Kind == PolicyValueKind.Bool ? "1" : item.Choices[item.Choices.Length - 1];
                    Eq(true, fixture.Family.Mode.SetProfileOverride(fixture.Family.First.Id, key, stored));
                    TierPicker picker = fixture.BuildProfilePicker(key);
                    int expectedIndex = item.Kind == PolicyValueKind.Bool ? 2 : item.Choices.Length;
                    Console.WriteLine("GRAPHICS_PROFILE key=" + key + " stored=" + stored
                        + " UI_enabled=" + picker.Enabled + " UI_visible=" + picker.Visible
                        + " UI_index=" + picker.Index);
                    Eq(stored, fixture.Family.Current(fixture.Family.First.Id).Overrides[key]);
                    if (!picker.Enabled || !picker.Visible || picker.Index != expectedIndex) trapped++;
                    string before = File.ReadAllText(fixture.Family.LibraryFile);
                    const int attempted = 2;
                    picker.Index = attempted;
                    picker.IndexChanged(attempted);
                    Eq(before, File.ReadAllText(fixture.Family.LibraryFile));
                    Eq(expectedIndex, picker.Index);
                    picker.Index = 1; picker.IndexChanged(1);
                    string off = item.Kind == PolicyValueKind.Bool ? "0" : "off";
                    Eq(off, fixture.Family.Current(fixture.Family.First.Id).Overrides[key]);
                    Eq(1, picker.Index); Eq(false, picker.Enabled); Eq(true, picker.Visible);
                    Eq(false, (bool)UiConfigCall(fixture.Form, "ApplyCfgPolicyChoice", key, stored));
                    Eq(off, fixture.Family.Current(fixture.Family.First.Id).Overrides[key]);
                    Eq(false, picker.IsHandleCreated);
                }
            Eq(0, trapped);
        }

        private static void UiConfigUnsupportedGlobalGraphicsRemainDisableable(string root)
        {
            using (var fixture = new UiConfigPowerYieldFixture(root, "graphics-global-unsupported"))
            using (var page = new DBPanel { Size = new Size(996, 696) })
            {
                string[] fields = { "nvMaxPerf", "nvSmoothMotion", "nvShaderCacheMax", "nvRebarOn",
                    "amdAntiLag", "amdAfmf", "rsrOn", "gpuPowerMaxOn" };
                string[] toggles = { "swNvMax", "swNvSmooth", "swNvShader", "swNvRebar",
                    "swAmdAlag", "swAmdAfmf", "swAmdRsr", "swGpuPower" };
                foreach (string field in fields) FamilyPolicySetField(fixture.Family.Mode, field, true);
                FamilyPolicySetField(fixture.Family.Mode, "nvLowLatMode", "ultra");
                FamilyPolicySetField(fixture.Family.Mode, "nvDlssMode", "k");
                fixture.BuildGlobalGraphics(page);
                int trapped = 0;
                foreach (string name in toggles)
                {
                    var toggle = (Toggle)UiConfigGetField(fixture.Form, name);
                    Console.WriteLine("GRAPHICS_GLOBAL control=" + name + " checked=" + toggle.Checked + " enabled=" + toggle.Enabled);
                    Eq(true, toggle.Checked);
                    if (!toggle.Enabled) trapped++;
                    toggle.Checked = false;
                    Eq(false, toggle.Checked); Eq(false, toggle.Enabled);
                    toggle.Checked = true;
                    Eq(false, toggle.Checked); Eq(false, toggle.Enabled);
                    Eq(false, toggle.IsHandleCreated);
                }
                var lowLatency = (TierPicker)UiConfigGetField(fixture.Form, "nvllPicker");
                var dlss = (TierPicker)UiConfigGetField(fixture.Form, "dlssPicker");
                Eq(2, lowLatency.Index); Eq(3, dlss.Index);
                if (!lowLatency.Enabled) trapped++;
                if (!dlss.Enabled) trapped++;
                foreach (TierPicker picker in new[] { lowLatency, dlss })
                {
                    int old = picker.Index;
                    picker.Index = 1; picker.IndexChanged(1);
                    Eq(old, picker.Index);
                    picker.Index = 0; picker.IndexChanged(0);
                    Eq(0, picker.Index); Eq(false, picker.Enabled);
                    picker.Index = 1; picker.IndexChanged(1);
                    Eq(0, picker.Index); Eq(false, picker.Enabled);
                }
                foreach (string field in fields) Eq(false, (bool)FamilyPolicyGetField(fixture.Family.Mode, field));
                Eq("off", fixture.Family.Mode.NvLowLatMode); Eq("off", fixture.Family.Mode.NvDlssMode);
                Eq(false, page.IsHandleCreated);
                Eq(0, trapped);
            }
        }

        private static void UiConfigSupportedVendorProfiles(string root)
        {
            foreach (PolicyItem item in PolicyCatalog.All)
            {
                if (!(bool)UiConfigCallStatic("CfgVendorGraphicsItem", item.Key)) continue;
                using (var fixture = new UiConfigPowerYieldFixture(root, "graphics-supported-" + item.Key))
                {
                    bool supported = true;
                    fixture.Form.VendorGraphicsSupportForTest = delegate { return supported; };
                    TierPicker picker = fixture.BuildProfilePicker(item.Key);
                    Eq(true, picker.Enabled); Eq(true, picker.Visible);
                    string[] choices = item.Kind == PolicyValueKind.Bool ? new[] { "0", "1" } : item.Choices;
                    for (int i = 0; i < choices.Length; i++)
                    {
                        picker.Index = i + 1; picker.IndexChanged(i + 1);
                        Eq(choices[i], fixture.Family.Current(fixture.Family.First.Id).Overrides[item.Key]);
                        Eq(i + 1, picker.Index); Eq(true, picker.Enabled);
                    }
                    string on = choices[choices.Length - 1];
                    if (item.Kind == PolicyValueKind.Bool) Settings.Save(item.Key, true);
                    else Settings.SaveStr(item.Key, on);
                    picker.Index = 1; picker.IndexChanged(1);
                    picker.Index = 0; picker.IndexChanged(0);
                    Eq(false, fixture.Family.Current(fixture.Family.First.Id).Overrides.ContainsKey(item.Key));
                    Eq(on, PolicyResolver.Read(fixture.Family.Current(fixture.Family.First.Id), item.Key));
                    Eq(0, picker.Index); Eq(true, picker.Enabled);
                    Eq("0", fixture.Family.Current(fixture.Family.First.Id).Overrides[PolicyCatalog.KeyBoost]);
                    Eq("2", fixture.Family.Current(fixture.Family.First.Id).Overrides[PolicyCatalog.KeyPreset]);
                    Eq(true, fixture.Family.Mode.SetProfileOverride(fixture.Family.First.Id, item.Key, choices[0]));
                    fixture.RefreshProfile();
                    Eq(true, (bool)UiConfigCall(fixture.Form, "ApplyCfgClearAllOverrides"));
                    Eq(0, fixture.Family.Current(fixture.Family.First.Id).Overrides.Count);
                    fixture.RefreshProfile();
                    supported = false; fixture.RefreshProfile();
                    Eq(true, picker.Enabled); // Inherited On still has an Off exit.
                    picker.Index = 1; picker.IndexChanged(1);
                    Eq(choices[0], fixture.Family.Current(fixture.Family.First.Id).Overrides[item.Key]);
                    Eq(false, picker.Enabled);
                }
            }
        }

        private static void UiConfigCachedAmdSupportNeedsApi(string root)
        {
            using (var fixture = new UiConfigPowerYieldFixture(root, "graphics-amd-stale-support"))
            using (var page = new DBPanel { Size = new Size(996, 696) })
            {
                fixture.SetStatic(typeof(AdlxTweaks), "alagSup", 1);
                fixture.SetStatic(typeof(AdlxTweaks), "afmfSup", 1);
                fixture.SetStatic(typeof(AdlxTweaks), "rsrSup", 1);
                Eq(false, AdlxTweaks.Available);
                fixture.BuildGlobalGraphics(page);
                int enabled = 0;
                string[] names = { "swAmdAlag", "swAmdAfmf", "swAmdRsr" };
                foreach (string name in names)
                {
                    var toggle = (Toggle)UiConfigGetField(fixture.Form, name);
                    Console.WriteLine("AMD_STALE_SUPPORT control=" + name + " enabled=" + toggle.Enabled);
                    if (toggle.Enabled) enabled++;
                    // These two paths have no modal confirmation. RSR is not
                    // invoked until its API gate is known to reject the request.
                    if (name != "swAmdRsr") toggle.Checked = true;
                }
                Console.WriteLine("AMD_STALE_SUPPORT alag_on=" + fixture.Family.Mode.AmdAntiLag
                    + " afmf_on=" + fixture.Family.Mode.AmdAfmf);
                Eq(0, enabled);
                foreach (string name in names)
                {
                    var toggle = (Toggle)UiConfigGetField(fixture.Form, name);
                    toggle.Checked = true;
                    Eq(false, toggle.Checked); Eq(false, toggle.Enabled);
                }
                Eq(false, fixture.Family.Mode.AmdAntiLag); Eq(false, fixture.Family.Mode.AmdAfmf);
                Eq(false, fixture.Family.Mode.RsrUpscale); Eq(false, page.IsHandleCreated);
            }
        }

        private static void UiConfigGraphicsBindingContracts(string root)
        {
            using (var fixture = new UiConfigPowerYieldFixture(root, "graphics-bindings"))
            using (var toggle = new Toggle())
            {
                UiConfigSetField(fixture.Form, "graphicsSync", new List<Action>());
                bool value = false, supported = true, accept = true, loseSupport = false;
                int writes = 0, prompts = 0;
                UiConfigCall(fixture.Form, "BindGraphicsToggle", toggle,
                    new Func<bool>(delegate { return value; }),
                    new Action<bool>(delegate(bool next) { writes++; value = next; }),
                    new Func<bool>(delegate { return supported; }),
                    new Func<bool>(delegate { prompts++; if (loseSupport) supported = false; return accept; }));
                Eq(true, toggle.Enabled);
                toggle.Checked = true; Eq(true, value); Eq(1, writes); Eq(1, prompts);
                toggle.Checked = false; Eq(false, value); Eq(2, writes); Eq(1, prompts);
                accept = false;
                toggle.Checked = true; Eq(false, value); Eq(false, toggle.Checked); Eq(2, writes); Eq(2, prompts);
                accept = true; loseSupport = true;
                toggle.Checked = true; Eq(false, value); Eq(false, toggle.Enabled); Eq(2, writes); Eq(3, prompts);
                value = true;
                UiConfigCall(fixture.Form, "SyncGraphicsToggles");
                Eq(true, toggle.Checked); Eq(true, toggle.Enabled);
                toggle.Checked = false; Eq(false, value); Eq(false, toggle.Enabled); Eq(3, writes);
                toggle.Checked = true; Eq(false, value); Eq(false, toggle.Checked); Eq(3, prompts); Eq(3, writes);
                supported = true; loseSupport = false;
                UiConfigCall(fixture.Form, "SyncGraphicsToggles");
                toggle.Checked = true; Eq(true, value); Eq(4, writes);

                foreach (int count in new[] { 3, 4 })
                using (var picker = new TierPicker())
                {
                    picker.Labels = count == 3 ? new[] { "Off", "On", "Ultra" } : new[] { "Off", "Latest", "J", "K" };
                    int current = 0, changes = 0;
                    bool capability = true;
                    UiConfigCall(fixture.Form, "BindGraphicsPicker", picker,
                        new Func<int>(delegate { return current; }),
                        new Action<int>(delegate(int next) { changes++; current = next; }),
                        new Func<bool>(delegate { return capability; }));
                    for (int i = 1; i < count; i++)
                    {
                        picker.Index = i; picker.IndexChanged(i);
                        Eq(i, current); Eq(i, picker.Index); Eq(true, picker.Enabled);
                    }
                    int before = changes;
                    capability = false;
                    picker.Index = 1; picker.IndexChanged(1);
                    Eq(count - 1, current); Eq(count - 1, picker.Index); Eq(before, changes);
                    picker.IndexChanged(-1); picker.IndexChanged(count);
                    Eq(before, changes);
                    picker.Index = 0; picker.IndexChanged(0);
                    Eq(0, current); Eq(0, picker.Index); Eq(false, picker.Enabled);
                    picker.Index = 1; picker.IndexChanged(1);
                    Eq(0, current); Eq(0, picker.Index); Eq(before + 1, changes);
                    Eq(false, picker.IsHandleCreated);
                }
                Eq(false, toggle.IsHandleCreated);
            }
        }

        private static void UiConfigGraphicsInheritanceAndFailures(string root)
        {
            using (var fixture = new UiConfigPowerYieldFixture(root, "graphics-clear-capability-changed"))
            {
                bool supported = true;
                fixture.Form.VendorGraphicsSupportForTest = delegate { return supported; };
                Eq(true, fixture.Family.Mode.SetProfileOverride(fixture.Family.First.Id, PolicyCatalog.KeyNvMaxPerf, "0"));
                Eq(true, fixture.Family.Mode.SetProfileOverride(fixture.Family.First.Id, PolicyCatalog.KeyPowerYield, "0"));
                Settings.Save(PolicyCatalog.KeyNvMaxPerf, true);
                Settings.Save(PowerBudgetYieldRunner.EnabledKey, true);
                fixture.RefreshProfile();
                fixture.Form.PowerYieldConfirmationForTest = delegate { supported = false; return true; };
                string before = File.ReadAllText(fixture.Family.LibraryFile);
                Eq(false, (bool)UiConfigCall(fixture.Form, "ApplyCfgClearAllOverrides"));
                Eq(before, File.ReadAllText(fixture.Family.LibraryFile));
            }
            foreach (string key in new[] { PolicyCatalog.KeyNvMaxPerf, PolicyCatalog.KeyNvLowLat })
            using (var fixture = new UiConfigPowerYieldFixture(root, "graphics-inherit-" + key))
            {
                bool boolean = key == PolicyCatalog.KeyNvMaxPerf;
                string off = boolean ? "0" : "off", on = boolean ? "1" : "ultra";
                Eq(true, fixture.Family.Mode.SetProfileOverride(fixture.Family.First.Id, key, off));
                if (boolean) Settings.Save(key, true); else Settings.SaveStr(key, on);
                fixture.RefreshProfile();
                string before = File.ReadAllText(fixture.Family.LibraryFile);
                Eq(false, (bool)UiConfigCall(fixture.Form, "ApplyCfgPolicyChoice", key, null));
                Eq(false, (bool)UiConfigCall(fixture.Form, "ApplyCfgClearAllOverrides"));
                Eq(before, File.ReadAllText(fixture.Family.LibraryFile));
                if (boolean) Settings.Save(key, false); else Settings.SaveStr(key, off);
                Eq(true, (bool)UiConfigCall(fixture.Form, "ApplyCfgPolicyChoice", key, null));
                fixture.RefreshProfile();
                Eq(false, fixture.Family.Current(fixture.Family.First.Id).Overrides.ContainsKey(key));
                Eq(true, fixture.Family.Mode.SetProfileOverride(fixture.Family.First.Id, key, on));
                fixture.RefreshProfile();
                Eq(true, (bool)UiConfigCall(fixture.Form, "ApplyCfgClearAllOverrides"));
                Eq(0, fixture.Family.Current(fixture.Family.First.Id).Overrides.Count);
            }
            foreach (bool supported in new[] { false, true })
            using (var fixture = new UiConfigPowerYieldFixture(root, "graphics-save-failed-" + supported))
            {
                fixture.Form.VendorGraphicsSupportForTest = delegate { return supported; };
                Eq(true, fixture.Family.Mode.SetProfileOverride(fixture.Family.First.Id, PolicyCatalog.KeyNvLowLat, "ultra"));
                TierPicker picker = fixture.BuildProfilePicker(PolicyCatalog.KeyNvLowLat);
                string before = File.ReadAllText(fixture.Family.LibraryFile);
                using (var lease = new FileStream(fixture.Family.LibraryFile, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    picker.Index = 1; picker.IndexChanged(1);
                    Eq(true, fixture.Family.Mode.ProfileStoreSaveFailed);
                }
                Eq(before, File.ReadAllText(fixture.Family.LibraryFile));
                Eq(3, picker.Index);
            }
        }

        private static void UiConfigGraphicsPresentationDoesNotProbe(string root)
        {
            using (var fixture = new UiConfigPowerYieldFixture(root, "graphics-cached-presentation"))
            using (var toggle = new Toggle())
            {
                UiConfigSetField(fixture.Form, "graphicsSync", new List<Action>());
                bool value = false, cached = true, hardware = false;
                int probes = 0, writes = 0;
                UiConfigCall(fixture.Form, "BindGraphicsToggle", toggle,
                    new Func<bool>(delegate { return value; }),
                    new Action<bool>(delegate(bool next) { value = next; writes++; }),
                    new Func<bool>(delegate { probes++; cached = hardware; return cached; }), null,
                    new Func<bool>(delegate { return cached; }));
                for (int i = 0; i < 10; i++) UiConfigCall(fixture.Form, "SyncGraphicsToggles");
                Eq(0, probes); Eq(true, toggle.Enabled);
                toggle.Checked = true;
                Eq(1, probes); Eq(0, writes); Eq(false, toggle.Checked); Eq(false, toggle.Enabled);
                hardware = true;
                toggle.Checked = true;
                Eq(2, probes); Eq(1, writes); Eq(true, value);
                toggle.Checked = false;
                Eq(2, probes); Eq(2, writes); Eq(false, value); Eq(true, toggle.Enabled);
                Eq(false, toggle.IsHandleCreated);
            }
        }

        private static void UiConfigCoreFailedSave(string root)
        {
            using (var fixture = new UiConfigCoreFixture(root, "core-profile-save-failed"))
            {
                UiConfigSetField(fixture.Form, "cfgCorePending", CpuTopology.AllMask);
                string before = File.ReadAllText(fixture.Family.LibraryFile);
                using (var lease = new FileStream(fixture.Family.LibraryFile, FileMode.Open, FileAccess.Read, FileShare.Read))
                    UiConfigCall(fixture.Form, "ApplyCfgCoreMask");
                Eq(true, fixture.Family.Mode.ProfileStoreSaveFailed);
                Eq(before, File.ReadAllText(fixture.Family.LibraryFile));
                // Existing multi-key APIs may retain attempted values in RAM;
                // their save-failure gate, not a new transaction, stops application.
                Eq(null, FamilyPolicyGetField(fixture.Family.Mode, "worker"));
            }
        }

        private static void UiConfigVramGlobalFailedSave(string root)
        {
            FieldInfo stage = typeof(VramShield).GetField("stage", BindingFlags.Static | BindingFlags.NonPublic);
            object original = stage.GetValue(null);
            try
            {
                using (var fixture = new UiConfigPowerYieldFixture(root, "vram-global-save-failed"))
                using (var toggle = new Toggle())
                {
                    UiConfigSetField(fixture.Form, "swPolicyVramShield", toggle);
                    fixture.Form.VramShieldConfirmationForTest = delegate { return true; };
                    Settings.Save(VramShield.EnabledKey, false);
                    Settings.Save("VramShieldFuse", true);
                    stage.SetValue(null, ShieldStage.Fused);
                    Settings.SuspendWritesForReset();
                    toggle.SetSilently(true);
                    UiConfigCall(fixture.Form, "OnVramShieldToggle", true);
                    Console.WriteLine("VRAM_GLOBAL saved=" + Settings.Load(VramShield.EnabledKey, false)
                        + " RAM_on=" + fixture.Family.Mode.VramShieldOn + " stage=" + VramShield.Stage
                        + " UI_checked=" + toggle.Checked);
                    Eq(false, fixture.Family.Mode.VramShieldOn); Eq(false, toggle.Checked);
                    Eq(true, VramShield.Fused); Eq(ShieldStage.Fused, VramShield.Stage);
                }
            }
            finally { stage.SetValue(null, original); }
        }

        private static void UiConfigVramGlobalConsentAndFailedOff(string root)
        {
            Func<int, long, uint, uint, bool> oldRestore = VramShield.RestoreReservationForTest;
            VramShield.ResetRecoveryForTest();
            try
            {
                using (var fixture = new UiConfigPowerYieldFixture(root, "vram-global-consent"))
                using (var toggle = new Toggle())
                {
                    UiConfigSetField(fixture.Form, "swPolicyVramShield", toggle);
                    bool accept = false;
                    int prompts = 0;
                    fixture.Form.VramShieldConfirmationForTest = delegate { prompts++; return accept; };
                    Settings.Save("VramShieldFuse", true);
                    toggle.SetSilently(true);
                    UiConfigCall(fixture.Form, "OnVramShieldToggle", true);
                    Eq(false, toggle.Checked); Eq(false, fixture.Family.Mode.VramShieldOn); Eq(true, VramShield.Fused);
                    accept = true;
                    UiConfigCall(fixture.Form, "OnVramShieldToggle", true);
                    Eq(true, toggle.Checked); Eq(true, Settings.Load(VramShield.EnabledKey, false)); Eq(false, VramShield.Fused);
                    accept = false;
                    UiConfigCall(fixture.Form, "OnVramShieldToggle", true);
                    Eq(true, toggle.Checked); Eq(true, fixture.Family.Mode.VramShieldOn); Eq(3, prompts);
                    Settings.SaveStr("VramShieldSnap", "87001:42");
                    typeof(VramShield).GetField("stage", BindingFlags.Static | BindingFlags.NonPublic)
                        .SetValue(null, ShieldStage.Engaged);
                    typeof(VramShield).GetField("shieldPid", BindingFlags.Static | BindingFlags.NonPublic)
                        .SetValue(null, 87001);
                    typeof(VramShield).GetField("shieldCreation", BindingFlags.Static | BindingFlags.NonPublic)
                        .SetValue(null, 42L);
                    int released = 0;
                    VramShield.RestoreReservationForTest = delegate(int pid, long created, uint adapter, uint phys)
                    { Eq(87001, pid); Eq(42L, created); released++; return true; };
                    Settings.SuspendWritesForReset();
                    UiConfigCall(fixture.Form, "OnVramShieldToggle", false);
                    Eq(false, toggle.Checked); Eq(false, fixture.Family.Mode.VramShieldOn); Eq(1, released);
                    Eq(true, Settings.Load(VramShield.EnabledKey, false)); // Not a durable opt-out.
                    Eq("87001:42", Settings.LoadStr("VramShieldSnap", "")); // Failed receipt cleanup is retained.
                }
            }
            finally { VramShield.ResetRecoveryForTest(); VramShield.RestoreReservationForTest = oldRestore; }
        }

        private static object UiConfigCallStatic(string name, params object[] args)
        {
            MethodInfo method = typeof(PanelForm).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);
            if (method == null) throw new InvalidOperationException("Missing UI config static fixture method " + name);
            return method.Invoke(null, args);
        }

        private static void UiConfigSetField(PanelForm form, string name, object value)
        {
            FieldInfo field = typeof(PanelForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null) throw new InvalidOperationException("Missing UI config fixture field " + name);
            field.SetValue(form, value);
        }

        private static object UiConfigGetField(PanelForm form, string name)
        {
            return typeof(PanelForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form);
        }

        private static object UiConfigCall(PanelForm form, string name, params object[] args)
        {
            MethodInfo method = null;
            foreach (MethodInfo candidate in typeof(PanelForm).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic))
                if (candidate.Name == name && candidate.GetParameters().Length == args.Length)
                { method = candidate; break; }
            if (method == null) throw new InvalidOperationException("Missing UI config method " + name);
            return method.Invoke(form, args);
        }
    }
}
#endif
