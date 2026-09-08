// 文件用途 无界面的配置回归 不构造 PanelForm 不建原生窗口
// 不改进程亲和性 不写真实注册表
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
                    UiConfigPowerYieldFreqProxy, UiConfigPowerYieldSteering,
                    UiConfigPowerYieldInheritanceAndClear, UiConfigGuardedFailedSaves,
                    UiConfigAccessibilityPartialBackupRemainsVisible,
                    UiConfigUnsupportedGraphicsRemainDisableable,
                    UiConfigUnsupportedGlobalGraphicsRemainDisableable,
                    UiConfigCachedAmdSupportNeedsApi,
                    UiConfigSupportedVendorProfiles, UiConfigGraphicsBindingContracts,
                    UiConfigGraphicsPresentationDoesNotProbe,
                    UiConfigGraphicsInheritanceAndFailures, UiConfigCoreFailedSave,
                    UiConfigVramGlobalFailedSave, UiConfigVramGlobalConsentAndFailedOff,
                    UiConfigLaneFollowsPresetChanges, UiConfigLaneInitialProfileAndLowCoreSupport,
                    UiConfigGlobalLaneRecoversAfterHandheld };
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

        private static void UiConfigLaneFollowsPresetChanges(string root)
        {
            CpuTopology.TopologySnapshot topology = CpuTopology.CaptureTopologyForTest();
            try
            {
                CpuTopology.InjectTopologyForTest(63, new ulong[] { 1, 2, 4, 8, 16, 32 }, new ulong[] { 63 },
                    0, 0, 0, 0, false, false);
                foreach (bool startHandheld in new[] { false, true })
                    using (var f = new UiConfigPowerYieldFixture(root, "lane-preset-" + startHandheld))
                    {
                        f.Family.Mode.SetProfileOverride(f.Family.First.Id, PolicyCatalog.KeyPreset, startHandheld ? "4" : "1");
                        f.Family.Mode.SetProfileOverride(f.Family.First.Id, PolicyCatalog.KeyRenderLane, "1");
                        f.RefreshProfile();
                        TierPicker picker = f.BuildProfilePicker(PolicyCatalog.KeyRenderLane);
                        var card = (SettingCard)picker.Parent;
                        for (int i = 0; i < 3; i++)
                        {
                            bool handheld = i % 2 == 0 ? startHandheld : !startHandheld;
                            f.Family.Mode.SetProfileOverride(f.Family.First.Id, PolicyCatalog.KeyPreset, handheld ? "4" : "1");
                            f.RefreshProfile();
                            Eq(!handheld, picker.Enabled); Eq(!handheld, picker.Visible);
                            Eq(handheld ? 1 : 2, picker.Index);
                            Eq(Lang.T(handheld ? "gm.lane.unsupported" : "gm.lane.sub"), card.Desc);
                            Eq("1", f.Family.Current(f.Family.First.Id).Overrides[PolicyCatalog.KeyRenderLane]);
                        }
                    }
            }
            finally { CpuTopology.RestoreTopologyForTest(topology); }
        }

        private static void UiConfigLaneInitialProfileAndLowCoreSupport(string root)
        {
            CpuTopology.TopologySnapshot topology = CpuTopology.CaptureTopologyForTest();
            try
            {
                foreach (bool lowCore in new[] { false, true })
                    using (var f = new UiConfigPowerYieldFixture(root, "lane-initial-" + lowCore))
                    {
                        CpuTopology.InjectTopologyForTest(lowCore ? 15UL : 63UL,
                            lowCore ? new ulong[] { 1, 2, 4, 8 } : new ulong[] { 1, 2, 4, 8, 16, 32 },
                            new ulong[0], 0, 0, 0, 0, false, false);
                        f.Family.Mode.SetProfileOverride(f.Family.First.Id, PolicyCatalog.KeyPreset, lowCore ? "1" : "4");
                        // 打开页面先 RefreshCfgProfile 再建行 此处不提前调用 SyncCfgRows
                        Eq(true, (bool)UiConfigCall(f.Form, "RefreshCfgProfile"));
                        object[] args = { f.ProfilePanel, 0, PolicyCatalog.ItemOf(PolicyCatalog.KeyRenderLane) };
                        UiConfigCall(f.Form, "AddCfgPickerRow", args);
                        var card = (SettingCard)f.ProfilePanel.Controls[0];
                        TierPicker picker = null;
                        foreach (Control child in card.Controls) if (child is TierPicker) picker = (TierPicker)child;
                        Eq(false, picker.Enabled); Eq(false, picker.Visible);
                        Eq(Lang.T("gm.lane.unsupported"), card.Desc);
                        f.Family.Mode.SetProfileOverride(f.Family.First.Id, PolicyCatalog.KeyPreset, "1");
                        f.RefreshProfile();
                        Eq(!lowCore, picker.Enabled);
                    }
            }
            finally { CpuTopology.RestoreTopologyForTest(topology); }
        }

        private static void UiConfigGlobalLaneRecoversAfterHandheld(string root)
        {
            CpuTopology.TopologySnapshot topology = CpuTopology.CaptureTopologyForTest();
            try
            {
                CpuTopology.InjectTopologyForTest(63, new ulong[] { 1, 2, 4, 8, 16, 32 }, new ulong[] { 63 },
                    0, 0, 0, 0, false, false);
                using (var f = new UiConfigPowerYieldFixture(root, "lane-global"))
                using (var toggle = new Toggle())
                using (var card = new SettingCard())
                {
                    UiConfigSetField(f.Form, "swPolicyLane", toggle);
                    UiConfigSetField(f.Form, "cardPolicyLane", card);
                    foreach (bool savedOn in new[] { true, false })
                    {
                        f.Family.Mode.RenderLaneOn = savedOn;
                        foreach (PerformancePreset mode in new[] { PerformancePreset.Competitive,
                            PerformancePreset.Handheld, PerformancePreset.Competitive, PerformancePreset.Extreme,
                            PerformancePreset.Handheld, PerformancePreset.Competitive })
                        {
                            FamilyPolicySetField(f.Family.Mode, "preset", mode);
                            // 模拟通用开关同步先读回用户值 随后的策略同步必须覆盖成实际显示值
                            toggle.SetSilently(savedOn);
                            UiConfigCall(f.Form, "SyncPolicyLane");
                            bool supported = mode != PerformancePreset.Handheld;
                            bool forced = mode == PerformancePreset.Extreme;
                            Eq(supported && (forced || savedOn), toggle.Checked);
                            Eq(supported && !forced, toggle.Enabled);
                            Eq(Lang.T(supported ? "gm.lane.sub" : "gm.lane.unsupported"), card.Desc);
                            Eq(savedOn, f.Family.Mode.RenderLaneOn);
                        }
                    }
                }
            }
            finally { CpuTopology.RestoreTopologyForTest(topology); }
        }

        private sealed class UiConfigCoreFixture : IDisposable
        {
            internal readonly FamilyPolicyFixture Family;
            internal readonly CoreSchedulingPanel Editor;
            internal readonly DBPanel Panel = new DBPanel();
            private readonly CpuTopology.TopologySnapshot topology = CpuTopology.CaptureTopologyForTest();
            private readonly Dictionary<FieldInfo, object> originals = new Dictionary<FieldInfo, object>();
            private readonly ulong oldStrict = CpuTopology.StrictBoostMask, oldThrottle = CpuTopology.ThrottleMask;

            internal UiConfigCoreFixture(string root, string name)
            {
                Settings.UseTransientStoreForCurrentProcess();
                foreach (string field in new[] { "backgroundIds", "partitionGameIds", "customSet", "squeezeCache" })
                {
                    FieldInfo info = typeof(CpuTopology).GetField(field, BindingFlags.Static | BindingFlags.NonPublic);
                    originals.Add(info, info.GetValue(null));
                }
                CpuTopology.InjectTopologyForTest(0xF, new ulong[] { 1, 2, 4, 8 }, new ulong[] { 3, 12 }, 0, 0, 0, 0, false, false);
                CpuTopology.StrictBoostMask = 3; CpuTopology.ThrottleMask = 12;
                typeof(CpuTopology).GetField("backgroundIds", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, new uint[] { 2, 3 });
                typeof(CpuTopology).GetField("partitionGameIds", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, new uint[] { 0, 1 });
                Family = new FamilyPolicyFixture(root, name);
                FamilyPolicySetField(Family.Mode, "gameMask", 0xFUL);
                FamilyPolicySetField(Family.Mode, "allMask", 0xFUL);
                FamilyPolicySetField(Family.Mode, "strictMask", 0x3UL);
                Family.Mode.CorePartitionEnabled = true;
                // 保存下一局方案，不创建工作线程，不读写任何真实进程亲和性。
                FamilyPolicySetField(Family.Mode, "active", true);
                Editor = new CoreSchedulingPanel(900, null,
                    delegate(CoreSchedulingPlan p, string global, string token, bool follow)
                    { return Family.Mode.SaveCoreScheduling(p, global, null, false, null); }, delegate { return true; });
                Panel.Controls.Add(Editor);
            }

            internal CoreSchedulingPanel ProfileEditor()
            {
                var editor = new CoreSchedulingPanel(900, delegate { return Family.Current(Family.First.Id); },
                    delegate(CoreSchedulingPlan p, string global, string token, bool follow)
                    { return Family.Mode.SaveCoreScheduling(p, global, Family.First.Id, follow, token); }, delegate { return true; });
                Panel.Controls.Add(editor);
                return editor;
            }

            public void Dispose()
            {
                Eq(null, FamilyPolicyGetField(Family.Mode, "worker"));
                Panel.Dispose(); Family.Dispose();
                CpuTopology.RestoreTopologyForTest(topology);
                CpuTopology.StrictBoostMask = oldStrict; CpuTopology.ThrottleMask = oldThrottle;
                foreach (KeyValuePair<FieldInfo, object> old in originals) old.Key.SetValue(null, old.Value);
            }
        }

        private static void UiConfigManualAllOverridesGlobalPartition(string root)
        {
            using (var f = new UiConfigCoreFixture(root, "manual-all"))
            {
                var editor = f.ProfileEditor(); editor.FollowToggle.Checked = false; editor.SelectMask(15); editor.SaveDraft();
                var snapshot = PolicyResolver.For(f.Family.Current(f.Family.First.Id));
                Eq(false, snapshot.StrictCores); Eq(15UL, snapshot.CoreMask); Eq(true, snapshot.ManualPlacement);
                Eq("0", f.Family.Current(f.Family.First.Id).Overrides[PolicyCatalog.KeyBoost]);
            }
        }

        private static void UiConfigGlobalManualAllIsDirty(string root)
        {
            using (var f = new UiConfigCoreFixture(root, "global-manual-dirty"))
            {
                Eq(3UL, f.Editor.Draft.GameMask);
                f.Editor.SelectMask(15); Eq(true, f.Editor.SaveButton.Enabled);
            }
        }

        private static void UiConfigGlobalManualAllClearsPartition(string root)
        {
            using (var f = new UiConfigCoreFixture(root, "global-manual-apply"))
            {
                f.Editor.SelectMask(15); f.Editor.SaveDraft();
                Eq(false, PolicyResolver.Global().StrictCores);
                Eq(15UL, CoreScheduling.LoadGlobal().GameMask);
                Eq(false, f.Editor.SaveButton.Enabled);
                Eq(null, FamilyPolicyGetField(f.Family.Mode, "worker"));
            }
        }

        private static void UiConfigProfilePartitionToManualAllIsDirty(string root)
        {
            using (var f = new UiConfigCoreFixture(root, "profile-partition-to-all"))
            {
                Eq(true, f.Family.Mode.SetProfileCorePlacement(f.Family.First.Id, "", "1", null));
                var editor = f.ProfileEditor(); editor.SelectMask(15);
                Eq(true, editor.SaveButton.Enabled); editor.SaveDraft();
                Eq(false, PolicyResolver.For(f.Family.Current(f.Family.First.Id)).StrictCores);
                Eq(15UL, PolicyResolver.For(f.Family.Current(f.Family.First.Id)).CoreMask);
            }
        }

        private static void UiConfigCorePartialFollowAndTopology(string root)
        {
            using (var f = new UiConfigCoreFixture(root, "profile-core-transitions"))
            {
                var editor = f.ProfileEditor(); editor.FollowToggle.Checked = false; editor.SelectMask(12); editor.SaveDraft();
                GameProfile profile = f.Family.Current(f.Family.First.Id);
                Eq(12UL, PolicyResolver.For(profile).CoreMask);
                Eq("0", profile.Overrides[PolicyCatalog.KeyBoost]); Eq("2", profile.Overrides[PolicyCatalog.KeyPreset]);
                editor.FollowToggle.Checked = true; editor.SaveDraft();
                profile = f.Family.Current(f.Family.First.Id);
                Eq(false, profile.Overrides.ContainsKey(CoreScheduling.Key));
                Eq(true, PolicyResolver.For(profile).StrictCores);
                editor.FollowToggle.Checked = false; editor.SelectMask(15); editor.SaveDraft();
                profile = f.Family.Current(f.Family.First.Id);
                CpuTopology.AllMask = 255;
                FamilyPolicySetField(f.Family.Mode, "allMask", 255UL);
                object[] args = { PolicyResolver.For(profile), false };
                Eq(255UL, (ulong)FamilyPolicyInvoke(f.Family.Mode, "EffectiveGameMask", args));
                Eq(15UL, CoreScheduling.ForProfile(profile, CoreScheduling.LoadGlobal()).GameMask);
            }
        }

        private static void UiConfigGlobalPartialAndAllPreset(string root)
        {
            using (var f = new UiConfigCoreFixture(root, "global-core-transitions"))
            {
                f.Editor.SelectMask(12); f.Editor.SaveDraft(); Eq(12UL, CoreScheduling.LoadGlobal().GameMask);
                f.Editor.SelectMask(15); f.Editor.SaveDraft(); Eq(15UL, CoreScheduling.LoadGlobal().GameMask);
                Eq(false, PolicyResolver.Global().StrictCores);
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
                // 先把探测缓存填上 不去开系统电源和 EMI 句柄
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
            YieldAction action = UiConfigPowerYieldSampleWindow(state, 0,
                PowerBudgetYield.ObserveTicks, 95, 30, 50, -1);
            Eq(YieldAction.Engage, action);
            action = UiConfigPowerYieldSampleWindow(state, PowerBudgetYield.ObserveTicks,
                PowerBudgetYield.VerifyTicks, 95, 30, 50, -1);
            Eq(YieldAction.Revert, action);
            Eq(true, PowerBudgetYield.Fused);
            Eq(true, PowerBudgetYieldRunner.EnabledSetting);
        }

        private static YieldAction UiConfigPowerYieldSampleWindow(PowerBudgetYield state,
            long start, long span, double gpu, double cpu, double watts, double frequency)
        {
            // 熔断依赖完整采样证据，沿生产的 2 秒节奏；15 秒验收在第 16 秒读数完成。
            long interval = PowerBudgetYieldRunner.SampleIntervalMs * TimeSpan.TicksPerMillisecond;
            YieldAction action = YieldAction.None;
            for (long elapsed = interval; elapsed < span + interval; elapsed += interval)
                action = state.Advance(start + elapsed, gpu, cpu, watts, frequency);
            return action;
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

        // 方向盘 验收通过后持续盯瓶颈 瓶颈回移就还预算 再吃满可重让 封顶三次
        private static void UiConfigPowerYieldSteering(string root)
        {
            // 前序用例会留下熔断 自己清干净 不依赖数组顺序
            PowerBudgetYield.ClearFuse();
            var state = new PowerBudgetYield();
            state.Begin(0, true);
            long t = 0;
            YieldAction action = YieldAction.None;

            Action<double, double, double, long> window = delegate(double gpu, double cpu, double watts, long span)
            {
                for (int s = 1; s <= PowerBudgetYield.MinSamples; s++)
                    action = state.Advance(t + span * s / PowerBudgetYield.MinSamples, gpu, cpu, watts);
                t += span;
            };

            for (int round = 1; round <= PowerBudgetYield.MaxReengage; round++)
            {
                window(95, 30, 50, PowerBudgetYield.ObserveTicks);
                Eq(YieldAction.Engage, action);
                window(95, 30, 45, PowerBudgetYield.VerifyTicks);
                Eq(YieldAction.Keep, action);
                Eq(YieldStage.Held, state.Stage);
                // GPU 仍吃满 按兵不动
                window(95, 30, 45, PowerBudgetYield.HoldWindowTicks);
                Eq(YieldAction.None, action);
                Eq(YieldStage.Held, state.Stage);
                // 瓶颈移回 CPU 侧 还预算 第一轮走 GPU 掉载触发 其余走 CPU 吃紧触发
                if (round == 1) window(60, 40, 45, PowerBudgetYield.HoldWindowTicks);
                else window(95, 85, 45, PowerBudgetYield.HoldWindowTicks);
                Eq(YieldAction.Release, action);
                Eq(round < PowerBudgetYield.MaxReengage
                    ? YieldStage.Observing : YieldStage.Skipped, state.Stage);
            }
            // 名额用完 之后不再有任何动作 也不熔断
            window(95, 30, 45, PowerBudgetYield.ObserveTicks);
            Eq(YieldAction.None, action);
            Eq(YieldStage.Skipped, state.Stage);
            Eq(false, PowerBudgetYield.Fused);
        }

        // 频率代理的降级验证 熔断分账 与瓦数路径互不牵连
        private static void UiConfigPowerYieldFreqProxy(string root)
        {
            // 前序用例会留下熔断 自己清干净 不依赖数组顺序
            PowerBudgetYield.ClearFuse();
            // 频率真降了且 GPU 稳住 → 保持 不熔断
            var state = new PowerBudgetYield();
            state.Begin(0, true, true);
            YieldAction action = UiConfigPowerYieldSampleWindow(state, 0,
                PowerBudgetYield.ObserveTicks, 95, 30, -1, 150);
            Eq(YieldAction.Engage, action);
            action = UiConfigPowerYieldSampleWindow(state, PowerBudgetYield.ObserveTicks,
                PowerBudgetYield.VerifyTicks, 95, 32, -1, 138);
            Eq(YieldAction.Keep, action);
            Eq(YieldVerdict.Kept, state.Verdict);
            Eq(false, PowerBudgetYield.Fused);
            Eq(false, PowerBudgetYield.FreqFused);

            // 频率纹丝不动 = EPP 死杠杆 → 熔断 但记在代理账上 瓦数账不背锅
            state = new PowerBudgetYield();
            state.Begin(0, true, true);
            action = UiConfigPowerYieldSampleWindow(state, 0,
                PowerBudgetYield.ObserveTicks, 95, 30, -1, 150);
            Eq(YieldAction.Engage, action);
            action = UiConfigPowerYieldSampleWindow(state, PowerBudgetYield.ObserveTicks,
                PowerBudgetYield.VerifyTicks, 95, 31, -1, 150);
            Eq(YieldAction.Revert, action);
            Eq(YieldVerdict.NoGain, state.Verdict);
            Eq(false, PowerBudgetYield.Fused);
            Eq(true, PowerBudgetYield.FreqFused);
            PowerBudgetYield.ClearFuse();
            Eq(false, PowerBudgetYield.FreqFused);

            // 负载漂移超过判定窗 → 退回但不熔断 那是场景变了 不是机器的错
            state = new PowerBudgetYield();
            state.Begin(0, true, true);
            action = UiConfigPowerYieldSampleWindow(state, 0,
                PowerBudgetYield.ObserveTicks, 95, 30, -1, 150);
            Eq(YieldAction.Engage, action);
            action = UiConfigPowerYieldSampleWindow(state, PowerBudgetYield.ObserveTicks,
                PowerBudgetYield.VerifyTicks, 95, 55, -1, 120);
            Eq(YieldAction.Revert, action);
            Eq(YieldVerdict.Inconclusive, state.Verdict);
            Eq(false, PowerBudgetYield.FreqFused);

            // GPU 被拖下水 → 熔断 频率降了也不算数
            state = new PowerBudgetYield();
            state.Begin(0, true, true);
            action = UiConfigPowerYieldSampleWindow(state, 0,
                PowerBudgetYield.ObserveTicks, 95, 30, -1, 150);
            Eq(YieldAction.Engage, action);
            action = UiConfigPowerYieldSampleWindow(state, PowerBudgetYield.ObserveTicks,
                PowerBudgetYield.VerifyTicks, 88, 31, -1, 130);
            Eq(YieldAction.Revert, action);
            Eq(YieldVerdict.GpuHarm, state.Verdict);
            Eq(true, PowerBudgetYield.FreqFused);
            PowerBudgetYield.ClearFuse();

            // UI 无瓦数但有频率代理 → 开关可用 文案是降级说明 代理熔断后换熔断文案
            using (var fixture = new UiConfigPowerYieldFixture(root, "power-yield-proxy"))
            {
                Settings.Save(PowerBudgetYieldRunner.EnabledKey, false);
                fixture.Capabilities(true, false, true);
                PowerBudgetYieldRunner.FreqProxyForTest = true;
                try
                {
                    fixture.Refresh();
                    Eq(true, fixture.Toggle.Enabled);
                    Eq(Lang.T("gm.poweryield.proxysub"), fixture.Card.Desc);
                    Settings.Save("PowerYieldFreqFuse", true);
                    fixture.Refresh();
                    Eq(Lang.T("gm.poweryield.fused"), fixture.Card.Desc);
                    PowerBudgetYield.ClearFuse();
                    // 代理探针也没有时 回到统一的不可用文案
                    PowerBudgetYieldRunner.FreqProxyForTest = false;
                    fixture.Refresh();
                    Eq(false, fixture.Toggle.Enabled);
                    Eq(true, fixture.Card.Desc.StartsWith(Lang.T("gm.poweryield.nowatt")));
                }
                finally { PowerBudgetYieldRunner.FreqProxyForTest = false; }
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
                Eq(false, fixture.Family.Mode.ProfileStoreSaveFailed);
                Eq(before, File.ReadAllText(fixture.Family.LibraryFile)); Eq(true, PowerBudgetYield.Fused);
                Eq(false, fixture.Family.Current(fixture.Family.First.Id).Overrides.ContainsKey(PowerBudgetYieldRunner.EnabledKey));
                Eq(true, fixture.Pick("1"));
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
                Eq(false, fixture.Family.Mode.ProfileStoreSaveFailed);
                Eq(before, File.ReadAllText(fixture.Family.LibraryFile)); Eq(true, VramShield.Fused);
                Eq(false, fixture.Family.Current(fixture.Family.First.Id).Overrides.ContainsKey(PolicyCatalog.KeyVramShield));
                Eq(true, (bool)UiConfigCall(fixture.Form, "ApplyCfgPolicyChoice", PolicyCatalog.KeyVramShield, "1"));
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
                    // 第一次注册表改动完成了 后面那次失败
                    // 会留下一个没有全成功标记的恢复槽
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
                    // 这两条路径没有模态确认
                    // 在确定 API 门会拒绝之前 不会去调 RSR
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
                    Eq(false, fixture.Family.Mode.ProfileStoreSaveFailed);
                }
                Eq(before, File.ReadAllText(fixture.Family.LibraryFile));
                Eq(3, picker.Index);
                Eq("ultra", fixture.Family.Current(fixture.Family.First.Id).Overrides[PolicyCatalog.KeyNvLowLat]);
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
            using (var f = new UiConfigCoreFixture(root, "core-profile-save-failed"))
            {
                var editor = f.ProfileEditor(); editor.FollowToggle.Checked = false; editor.SelectMask(15);
                string before = File.ReadAllText(f.Family.LibraryFile);
                using (var lease = new FileStream(f.Family.LibraryFile, FileMode.Open, FileAccess.Read, FileShare.Read))
                    editor.SaveDraft();
                Eq(false, f.Family.Mode.ProfileStoreSaveFailed);
                Eq(before, File.ReadAllText(f.Family.LibraryFile));
                Eq(false, f.Family.Current(f.Family.First.Id).Overrides.ContainsKey(CoreScheduling.Key));
                Eq(true, editor.SaveButton.Enabled);
                Eq(null, FamilyPolicyGetField(f.Family.Mode, "worker"));
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
