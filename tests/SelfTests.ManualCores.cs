#if PAVISE_SELFTEST
using System;
using System.IO;
using System.Reflection;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        internal static void RunManualCoreTests(string output)
        {
            ulong all;
            ulong[] cores = AutoSmtCores(6, out all);
            string stamp = ManualCorePlan.Stamp(all, cores);
            var plan = new ManualCorePlan(0x14, true, stamp);
            Eq(plan.Encode(), ManualCorePlan.Parse(plan.Encode()).Encode());
            Eq(plan.Encode(), PolicyCatalog.Canonical(ManualCorePlan.Key, plan.Encode()));
            var hardPlan = new ManualCorePlan(0x14, true, stamp, true);
            Eq(true, ManualCorePlan.Parse(hardPlan.Encode()).HardAffinity);
            Eq(hardPlan.Encode(), PolicyCatalog.Canonical(ManualCorePlan.Key, hardPlan.Encode()));
            Eq(false, ManualCorePlan.Parse(plan.Encode()).HardAffinity);
            Eq(null, new ManualCorePlan(0x14, false, stamp, true).Validate(all, cores, 0, true, true));
            Eq(null, ManualCorePlan.Parse("1|FFF,3|garbage|1"));
            Eq(null, ManualCorePlan.Parse("1|FFF,3|0|1"));
            Eq(null, plan.Validate(all, cores, 0, true, true));
            Eq("schedule.manual.allocated", plan.Validate(all, cores, 0x20, true, true));
            Eq("schedule.manual.topology", plan.Validate(all, new ulong[] { all }, 0, true, true));
            Eq("schedule.manual.topology", plan.Validate(all, cores, 0, true, false));
            Eq("schedule.auto.unsupported", plan.Validate(all, cores, 0, false, true));
            Eq(null, new ManualCorePlan(4, false, stamp).Validate(all, cores, 0, true, true));
            Eq(null, new ManualCorePlan(4, true, stamp).Validate(all, cores, 0, true, true));
            Eq(null, new ManualCorePlan(1, true, stamp).Validate(all, cores, 0, true, true));
            Eq("schedule.manual.minimum", new ManualCorePlan(0, false, stamp).Validate(all, cores, 0, true, true));
            Eq("schedule.manual.minimum", new ManualCorePlan(1UL << 63, false, stamp).Validate(all, cores, 0, true, true));
            Eq("schedule.manual.allocated", new ManualCorePlan(4, true, stamp).Validate(all, cores, 8, true, true));
            Eq(null, new ManualCorePlan(4, false, stamp).Validate(all, cores, 8, true, true));
            Eq("game", AutoCores.ValidateRange(12, 4, all, cores, 0));
            Eq(null, AutoCores.ValidateRange(12, 4, all, cores, 0, true));
            ulong[] noSmt = { 1, 2, 4 };
            Eq(null, new ManualCorePlan(1, true, ManualCorePlan.Stamp(7, noSmt)).Validate(7, noSmt, 0, true, true));
            Eq(null, new ManualCorePlan(3, true, stamp).Validate(all, cores, 0, true, true));
            Eq("schedule.manual.allocated", plan.Validate(all, cores, 0xC00, true, true));
            Eq(true, GameMode.CoversManualIsolation(0xFF, 0x3C));
            Eq(false, GameMode.CoversManualIsolation(0x14, 0x3C));
            Eq("schedule.manual.spare", new ManualCorePlan(all & ~3UL, true, stamp).Validate(all, cores, 0, true, true));
            Eq(null, new ManualCorePlan(all, false, stamp).Validate(all, cores, 0, false, true));
            var governor = new AutoCoreGovernor(2, 2);
            governor.EngageManual(); Eq(true, governor.Exclusive); Eq(0, governor.Windows);
            governor.Block("denied"); governor.EngageManual(); Eq(false, governor.Exclusive);
            var singleGovernor = new AutoCoreGovernor(1, 1);
            singleGovernor.EngageManual(); Eq(true, singleGovernor.Exclusive); Eq(1, singleGovernor.Logical);

            var topology = CpuTopology.CaptureTopologyForTest();
            var oldAllocated = AutoCores.AllocatedForTest;
            var oldSupported = AutoCores.SupportedForTest;
            var oldStatus = AutoCores.Status;
            try
            {
                CpuTopology.InjectTopologyForTest(all, cores, new ulong[0], 0, 0, 0, 0, false, false);
                AutoCores.SupportedForTest = true;
                AutoCores.AllocatedForTest = delegate { return 0; };
                using (var f = new FamilyPolicyFixture(output, "manual-cores"))
                {
                    Eq(true, f.Mode.ClearProfileOverride(f.First.Id, PolicyCatalog.KeyPreset));
                    Settings.Save(AutomaticPolicy.VersionKey, true);
                    f.Mode.Preset = PerformancePreset.Competitive;
                    Eq(null, f.Mode.SaveManualCorePlan(f.First.Id, null, plan));
                    Eq(plan.Encode(), ManualCorePlan.Raw(f.Current(f.First.Id)));
                    Eq(null, ManualCorePlan.Raw(f.Current(f.Second.Id)));
                    var loaded = new GameProfileStore(f.DirectoryPath).LoadProfiles(false);
                    Eq(plan.Encode(), ManualCorePlan.Raw(FamilyPolicyFind(loaded, f.First.Id)));
                    Eq(true, f.Mode.SetProfileOverride(f.First.Id, PolicyCatalog.KeyAutoCores, "1"));
                    var frozen = PolicyResolver.For(f.Current(f.First.Id));
                    Eq(plan.Encode(), frozen.ManualCores.Encode());
                    FamilyPolicySetField(f.Mode, "sessionPolicy", frozen);
                    FamilyPolicySetField(f.Mode, "allMask", all);
                    FamilyPolicySetField(f.Mode, "activeDetection", new GameDetection { Profile = f.Current(f.First.Id),
                        RendererPid = 789012, RendererCreation = 12345, RendererName = "manual-test", RendererCandidateSelected = true });
                    f.Mode.RendererTestForeground = delegate { return 789012; };
                    foreach (PerformancePreset mode in new[] { PerformancePreset.Standard, PerformancePreset.Competitive,
                        PerformancePreset.Extreme, PerformancePreset.Handheld })
                    {
                        Eq(true, PresetValue.SupportsCoreScheduling(mode));
                        Settings.SaveStr(PolicyCatalog.KeyPreset, ((int)mode).ToString());
                        FamilyPolicySetField(f.Mode, "preset", mode);
                        PolicySnapshot modePolicy = PolicyResolver.For(f.Current(f.First.Id));
                        FamilyPolicySetField(f.Mode, "sessionPolicy", modePolicy);
                        FamilyPolicyInvoke(f.Mode, "BeginAutoCores", modePolicy, true);
                        object modePass = FamilyPolicyInvoke(f.Mode, "PrepareBoostPass");
                        Eq(true, AutoPassField<bool>(modePass, "Exclusive"));
                        Eq(0x14UL, AutoPassField<ulong>(modePass, "DesiredMask"));
                        Eq(true, AutoCores.Status.ManualIsolation);
                    }
                    Settings.SaveStr(PolicyCatalog.KeyPreset, "1");
                    FamilyPolicySetField(f.Mode, "preset", PerformancePreset.Competitive);
                    FamilyPolicySetField(f.Mode, "sessionPolicy", frozen);
                    FamilyPolicyInvoke(f.Mode, "BeginAutoCores", frozen, true);
                    object pass = FamilyPolicyInvoke(f.Mode, "PrepareBoostPass");
                    Eq(true, AutoPassField<bool>(pass, "Exclusive"));
                    Eq(0x14UL, AutoPassField<ulong>(pass, "DesiredMask"));
                    Eq(true, AutoCores.Status.Manual); Eq(true, AutoCores.Status.ManualIsolation);
                    Eq(0x3CUL, AutoCores.Status.Plan.Exclusive); Eq(0, AutoCores.Status.SampleCount);
                    FamilyPolicyInvoke(f.Mode, "GrowCoreCapacity", 64, 64.0);
                    Eq(0x14UL, (ulong)FamilyPolicyGetField(f.Mode, "coreGameMask"));
                    f.Mode.CoreEngageForTest = delegate { return false; };
                    Eq(false, (bool)FamilyPolicyInvoke(f.Mode, "EngageIsolation", 789012, pass));
                    Eq(false, f.Mode.ProbeCoreEngaged);

                    var pinOnly = new ManualCorePlan(0xC0, false, stamp);
                    Eq("schedule.manual.changed", f.Mode.SaveManualCorePlan(f.First.Id, null, pinOnly));
                    Eq(null, f.Mode.SaveManualCorePlan(f.First.Id, plan.Encode(), pinOnly));
                    Eq(plan.Encode(), frozen.ManualCores.Encode());
                    Eq(true, f.Mode.SetProfileOverride(f.First.Id, PolicyCatalog.KeyAutoCores, "0"));
                    pass = FamilyPolicyInvoke(f.Mode, "PrepareBoostPass");
                    Eq(false, AutoPassField<bool>(pass, "Exclusive"));
                    Eq(true, f.Mode.SetProfileOverride(f.First.Id, PolicyCatalog.KeyAutoCores, "1"));
                    pass = FamilyPolicyInvoke(f.Mode, "PrepareBoostPass");
                    Eq(0x14UL, AutoPassField<ulong>(pass, "DesiredMask"));

                    var next = PolicyResolver.For(f.Current(f.First.Id));
                    FamilyPolicySetField(f.Mode, "sessionPolicy", next);
                    FamilyPolicyInvoke(f.Mode, "BeginAutoCores", next, true);
                    pass = FamilyPolicyInvoke(f.Mode, "PrepareBoostPass");
                    Eq(0xC0UL, AutoPassField<ulong>(pass, "DesiredMask"));
                    f.Mode.CoreEngageForTest = delegate { throw new Exception("Pin-only must not start isolation"); };
                    Eq(true, (bool)FamilyPolicyInvoke(f.Mode, "EngageIsolation", 789012, pass));
                    FamilyPolicyInvoke(f.Mode, "PrepareBoostPass");
                    Eq(true, f.Mode.ProbeCoreEngaged);
                    FamilyPolicyInvoke(f.Mode, "StartBackgroundOnly");
                    Eq(false, f.Mode.ProbeCoreEngaged); Eq(false, f.Mode.ProbeGovernor.Exclusive);
                    Eq(null, f.Mode.SaveManualCorePlan(f.First.Id, pinOnly.Encode(), null));
                    Eq(null, ManualCorePlan.Raw(f.Current(f.First.Id)));
                    Eq(pinOnly.Encode(), next.ManualCores.Encode());

                    FamilyPolicySetField(f.Mode, "stopping", true);
                    Eq("schedule.manual.savefailed", f.Mode.SaveManualCorePlan(f.Second.Id, null, pinOnly));
                    Eq(null, ManualCorePlan.Raw(f.Current(f.Second.Id)));
                    FamilyPolicySetField(f.Mode, "stopping", false);
                    var store = (GameProfileStore)FamilyPolicyGetField(f.Mode, "profileStore");
                    byte[] previousFile = File.ReadAllBytes(f.LibraryFile);
                    store.BeforeReplaceForTest = delegate { throw new IOException("Injected manual save failure"); };
                    Eq("schedule.manual.savefailed", f.Mode.SaveManualCorePlan(f.Second.Id, null, pinOnly));
                    Eq(null, ManualCorePlan.Raw(f.Current(f.Second.Id)));
                    Eq(Convert.ToBase64String(previousFile), Convert.ToBase64String(File.ReadAllBytes(f.LibraryFile)));
                }
                ManualConcurrentSaveGate(output, plan);
                ManualRuntimeFailureStatus(output, plan);
                ManualCoreRecoveryGate(output, plan);
                ManualHardAffinityGate(output, hardPlan, plan, all);
                ManualCoreAutoSaveUi(output);
                ManualCoreShortcutsUi(output);
                ManualSingleCoreRuntime(output);
                using (var matrix = new CoreMatrix())
                {
                    matrix.LayoutFor(600);
                    matrix.Selected = 0;
                    matrix.ToggleSelection(0xC); Eq(0UL, matrix.Selected);
                    matrix.Editable = true; matrix.ToggleSelection(0xC); Eq(0xCUL, matrix.Selected);
                    matrix.DisabledMask = 0xC; matrix.ToggleSelection(0xC); Eq(0UL, matrix.Selected);
                    matrix.ToggleSelection(0xC); Eq(0UL, matrix.Selected);
                }
                Console.WriteLine("PASS manual cores: per-game persistence, frozen ranges, live disable, fixed masks, explicit isolation failures, manual CPU0 selection, topology/spare guards and editable matrix");
            }
            finally
            {
                CpuTopology.RestoreTopologyForTest(topology);
                AutoCores.AllocatedForTest = oldAllocated; AutoCores.SupportedForTest = oldSupported;
                AutoCores.Publish(oldStatus);
            }
        }

        private static void ManualSingleCoreRuntime(string output)
        {
            using (var f = new FamilyPolicyFixture(output, "manual-single-runtime"))
            {
                Eq(true, f.Mode.ClearProfileOverride(f.First.Id, PolicyCatalog.KeyPreset));
                Settings.Save(AutomaticPolicy.VersionKey, true);
                f.Mode.RendererTestForeground = delegate { return 789012; };
                foreach (ulong mask in new ulong[] { 1, 4 })
                foreach (bool isolation in new[] { false, true })
                foreach (PerformancePreset mode in new[] { PerformancePreset.Standard, PerformancePreset.Competitive,
                    PerformancePreset.Extreme, PerformancePreset.Handheld })
                {
                    f.Mode.Preset = mode;
                    var plan = new ManualCorePlan(mask, isolation, ManualCorePlan.CurrentStamp());
                    Eq(null, f.Mode.SaveManualCorePlan(f.First.Id, ManualCorePlan.Raw(f.Current(f.First.Id)), plan));
                    Eq(true, f.Mode.SetProfileOverride(f.First.Id, PolicyCatalog.KeyAutoCores, "1"));
                    var loaded = new GameProfileStore(f.DirectoryPath).LoadProfiles(false);
                    Eq(mask, PolicyResolver.For(FamilyPolicyFind(loaded, f.First.Id)).ManualCores.GameMask);
                    var profile = f.Current(f.First.Id);
                    var policy = PolicyResolver.For(profile);
                    FamilyPolicySetField(f.Mode, "sessionPolicy", policy);
                    FamilyPolicySetField(f.Mode, "activeDetection", new GameDetection { Profile = profile,
                        RendererPid = 789012, RendererCreation = 12345, RendererName = "single-test", RendererCandidateSelected = true });
                    FamilyPolicyInvoke(f.Mode, "BeginAutoCores", policy, true);
                    object pass = FamilyPolicyInvoke(f.Mode, "PrepareBoostPass");
                    if (!AutoPassField<bool>(pass, "Exclusive"))
                        throw new InvalidOperationException("Single CPU was not admitted: mask=" + mask + " isolation=" + isolation
                            + " mode=" + mode + " blocked=" + AutoCores.Status.Blocked);
                    Eq(mask, AutoPassField<ulong>(pass, "DesiredMask"));
                    Eq(1, AutoCores.Status.Plan.Logical); Eq(1, AutoCores.Status.Plan.Cores);
                    Eq(isolation ? AutoCores.WholeCores(mask, CpuTopology.PhysicalCoreMasks()) : mask,
                        AutoCores.Status.Plan.Exclusive);
                    FamilyPolicyInvoke(f.Mode, "GrowCoreCapacity", 64, 64.0);
                    Eq(mask, (ulong)FamilyPolicyGetField(f.Mode, "coreGameMask"));
                }
            }
            Console.WriteLine("PASS single logical CPU: saved profiles reload; all modes apply one CPU without expansion; isolation reserves SMT siblings");
        }

        private static void ManualCoreAutoSaveUi(string output)
        {
            using (var f = new FamilyPolicyFixture(output, "manual-autosave-ui"))
            using (var host = new DBPanel())
            {
                Eq(true, f.Mode.ClearProfileOverride(f.First.Id, PolicyCatalog.KeyPreset));
                Settings.Save(AutomaticPolicy.VersionKey, true);
                var form = (PanelForm)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(PanelForm));
                GC.SuppressFinalize(form);
                UiConfigSetField(form, "gameMode", f.Mode);
                UiConfigSetField(form, "coreScrollPanel", host);
                UiConfigSetField(form, "coreProfileId", f.First.Id);
                UiConfigCall(form, "BuildCoreEditor", 0, 0, 900);
                UiConfigCall(form, "SyncCoreEditor", f.Current(f.First.Id));
                var matrix = (CoreMatrix)UiConfigGetField(form, "coreEditMatrix");
                var enabled = (Toggle)UiConfigGetField(form, "coreEnableToggle");
                var isolation = (Toggle)UiConfigGetField(form, "coreIsolationToggle");
                var hard = (System.Windows.Forms.Label)UiConfigGetField(form, "coreEditorHardAffinity");
                var hardHelp = (System.Windows.Forms.Label)UiConfigGetField(form, "coreEditorHardHelp");
                Eq(0, host.Controls.Find("coreManualHardAffinity", true).Length);
                var result = (System.Windows.Forms.Label)UiConfigGetField(form, "coreEditorResult");
                Eq(false, enabled.Enabled);
                matrix.ToggleSelection(1);
                Eq(1UL, PolicyResolver.For(f.Current(f.First.Id)).ManualCores.GameMask);
                Eq(true, enabled.Enabled); Eq(false, enabled.Checked);
                Eq(Lang.T("schedule.manual.savedoff"), result.Text);
                UiConfigCall(form, "SyncCoreEditor", f.Current(f.First.Id));
                Eq(1UL, matrix.Selected);
                matrix.ToggleSelection(1);
                UiConfigCall(form, "SyncCoreEditor", f.Current(f.First.Id));
                Eq(0UL, matrix.Selected);
                Eq(1UL, PolicyResolver.For(f.Current(f.First.Id)).ManualCores.GameMask);
                Eq(Lang.F("schedule.manual.unsaved", Lang.T("schedule.manual.minimum")), result.Text);
                matrix.ToggleSelection(3);
                Eq(3UL, PolicyResolver.For(f.Current(f.First.Id)).ManualCores.GameMask);
                Eq(true, enabled.Enabled);
                Eq(false, enabled.Checked);
                Eq(Lang.T("schedule.manual.savedoff"), result.Text);
                enabled.Checked = true;
                Eq(true, PolicyResolver.For(f.Current(f.First.Id)).AutoCoresAllowed);
                Eq(Lang.T("schedule.manual.autosaved"), result.Text);
                isolation.Checked = true;
                Eq(true, PolicyResolver.For(f.Current(f.First.Id)).ManualCores.Isolation);
                Eq(Lang.T("schedule.manual.hard"), hard.Text);
                Eq(Lang.T("schedule.manual.hard.on"), hardHelp.Text);
                string saved = ManualCorePlan.Raw(f.Current(f.First.Id));
                var disk = new GameProfileStore(f.DirectoryPath).LoadProfiles(false);
                Eq(saved, ManualCorePlan.Raw(FamilyPolicyFind(disk, f.First.Id)));

                foreach (PerformancePreset mode in new[] { PerformancePreset.Standard, PerformancePreset.Competitive,
                    PerformancePreset.Extreme, PerformancePreset.Handheld })
                {
                    Settings.SaveStr(PolicyCatalog.KeyPreset, ((int)mode).ToString());
                    FamilyPolicySetField(f.Mode, "preset", mode);
                    UiConfigCall(form, "SyncCoreEditor", f.Current(f.First.Id));
                    Eq(true, isolation.Enabled); Eq(true, hard.Visible); Eq(true, hardHelp.Visible);
                    Eq(Lang.T("schedule.manual.autosaved"), result.Text);
                    Eq(saved, ManualCorePlan.Raw(f.Current(f.First.Id)));
                }

                matrix.ToggleSelection(3);
                Eq(saved, ManualCorePlan.Raw(f.Current(f.First.Id)));
                UiConfigCall(form, "SyncCoreEditor", f.Current(f.First.Id));
                Eq(3UL, PolicyResolver.For(f.Current(f.First.Id)).ManualCores.GameMask);
                Eq(false, PolicyResolver.For(f.Current(f.First.Id)).ManualCores.HardAffinity);
                Eq(0UL, matrix.Selected);
                matrix.ToggleSelection(3);
                saved = ManualCorePlan.Raw(f.Current(f.First.Id));
                int changes = f.Changes;
                UiConfigCall(form, "SyncCoreEditor", f.Current(f.First.Id));
                Eq(changes, f.Changes);

                FamilyPolicySetField(f.Mode, "stopping", true);
                matrix.ToggleSelection(0xC);
                Eq(saved, ManualCorePlan.Raw(f.Current(f.First.Id)));
                Eq(3UL, matrix.Selected);
                Eq(Lang.F("schedule.manual.autofailed", Lang.T("schedule.manual.savefailed")), result.Text);
                enabled.Checked = false;
                Eq(true, enabled.Checked);
                isolation.Checked = false;
                Eq(true, isolation.Checked);
                Eq(true, hard.Visible);
                FamilyPolicySetField(f.Mode, "stopping", false);
                matrix.ToggleSelection(0xC);
                Eq(0xFUL, PolicyResolver.For(f.Current(f.First.Id)).ManualCores.GameMask);

                var external = new ManualCorePlan(0x30, false, ManualCorePlan.CurrentStamp());
                Eq(null, f.Mode.SaveManualCorePlan(f.First.Id, ManualCorePlan.Raw(f.Current(f.First.Id)), external));
                matrix.ToggleSelection(0xC);
                Eq(external.Encode(), ManualCorePlan.Raw(f.Current(f.First.Id)));
                Eq(0x30UL, matrix.Selected);
                Eq(Lang.T("schedule.manual.changed"), result.Text);

                UiConfigSetField(form, "coreProfileId", f.Second.Id);
                UiConfigCall(form, "SyncCoreEditor", f.Current(f.Second.Id));
                Eq(0UL, matrix.Selected);
                Eq(false, enabled.Enabled);
                Eq(null, ManualCorePlan.Raw(f.Current(f.Second.Id)));
            }
            Console.WriteLine("PASS manual editor autosave: incomplete selection, disk persistence, live toggles, no refresh writes, failure rollback, concurrent edits and profile switching");
        }

        private static void ManualCoreShortcutsUi(string output)
        {
            var snapshot = CpuTopology.CaptureTopologyForTest();
            var oldAllocated = AutoCores.AllocatedForTest;
            try
            {
                Eq(0x30UL, PanelForm.CoreShortcutThreads(0x30, 0x33, new ulong[] { 0x11, 0x22 }, false));
                Eq(0x33UL, PanelForm.CoreShortcutThreads(0x30, 0x33, new ulong[] { 0x11, 0x22 }, true));
                Eq(0x31UL, PanelForm.CoreShortcutThreads(0x30, 0x31, new ulong[] { 0x11, 0x22 }, true));
                for (int shape = 0; shape < 3; shape++)
                {
                    ulong all;
                    ulong[] cores = AutoSmtCores(6, out all);
                    if (shape == 2) { all = 0xFF; cores = new ulong[] { 3, 0xC, 0x10, 0x20, 0x40, 0x80 }; }
                    CpuTopology.InjectTopologyForTest(all, cores, new ulong[0], shape == 2 ? 0xFUL : 0,
                        shape == 2 ? 0xF0UL : 0, shape == 1 ? 0x3FUL : 0, shape == 1 ? 0xFC0UL : 0, shape == 2, shape == 1);
                    CpuTopology.InjectCacheDomainsForTest(shape == 1 ? L3(0x3F, 0xFC0) : L3(all));
                    using (var f = new FamilyPolicyFixture(output, "manual-shortcuts-" + shape))
                    using (var host = new DBPanel())
                    {
                        var form = (PanelForm)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(PanelForm));
                        GC.SuppressFinalize(form);
                        UiConfigSetField(form, "gameMode", f.Mode);
                        UiConfigSetField(form, "coreScrollPanel", host);
                        UiConfigSetField(form, "coreProfileId", f.First.Id);
                        UiConfigCall(form, "BuildCoreEditor", 0, 0, 400);
                        UiConfigCall(form, "SyncCoreEditor", f.Current(f.First.Id));
                        var row = (System.Windows.Forms.Panel)UiConfigGetField(form, "coreQuickPanel");
                        var matrix = (CoreMatrix)UiConfigGetField(form, "coreEditMatrix");
                        var isolation = (Toggle)UiConfigGetField(form, "coreIsolationToggle");
                        Eq(shape == 1, row.Controls["coreQuick_ccd0"] != null);
                        Eq(shape == 2, row.Controls["coreQuick_perf"] != null);
                        ((PillButton)row.Controls["coreQuick_all"]).PerformClick();
                        Eq(all, PolicyResolver.For(f.Current(f.First.Id)).ManualCores.GameMask);
                        ((PillButton)row.Controls["coreQuick_noSmt"]).PerformClick();
                        Eq(shape == 2 ? 0xF5UL : 0x555UL, matrix.Selected);
                        ((PillButton)row.Controls["coreQuick_withSmt"]).PerformClick();
                        Eq(all, PolicyResolver.For(f.Current(f.First.Id)).ManualCores.GameMask);
                        if (shape == 1)
                        {
                            ((PillButton)row.Controls["coreQuick_ccd0"]).PerformClick();
                            ((PillButton)row.Controls["coreQuick_noSmt"]).PerformClick();
                            Eq(0x15UL, PolicyResolver.For(f.Current(f.First.Id)).ManualCores.GameMask);
                            ((PillButton)row.Controls["coreQuick_withSmt"]).PerformClick();
                            Eq(0x3FUL, matrix.Selected);
                            isolation.Checked = true;
                            string saved = ManualCorePlan.Raw(f.Current(f.First.Id));
                            ((PillButton)row.Controls["coreQuick_all"]).PerformClick();
                            Eq(saved, ManualCorePlan.Raw(f.Current(f.First.Id)));
                            isolation.Checked = false;
                        }
                        if (shape == 2)
                        {
                            ((PillButton)row.Controls["coreQuick_perf"]).PerformClick();
                            Eq(0xFUL, matrix.Selected);
                            ((PillButton)row.Controls["coreQuick_eff"]).PerformClick();
                            Eq(0xF0UL, matrix.Selected);
                        }
                        AutoCores.AllocatedForTest = delegate { return 3; };
                        UiConfigCall(form, "SyncCoreEditor", f.Current(f.First.Id));
                        ((PillButton)row.Controls["coreQuick_all"]).PerformClick();
                        Eq(all & ~3UL, PolicyResolver.For(f.Current(f.First.Id)).ManualCores.GameMask);
                        AutoCores.AllocatedForTest = delegate { return 0; };
                        foreach (System.Windows.Forms.Control button in row.Controls)
                        {
                            Eq(true, button.Right <= row.Width && button.Bottom <= row.Height);
                            foreach (System.Windows.Forms.Control other in row.Controls)
                                if (button != other) Eq(false, button.Bounds.IntersectsWith(other.Bounds));
                        }
                        Eq(true, matrix.Top > row.Bottom);
                    }
                }
            }
            finally { CpuTopology.RestoreTopologyForTest(snapshot); AutoCores.AllocatedForTest = oldAllocated; }
            Console.WriteLine("PASS core shortcuts: SMT grouping, CCD scope, hybrid ranges, autosave, reservation/isolation guards and wrapped layout");
        }

        private static void ManualHardAffinityGate(string output, ManualCorePlan hard, ManualCorePlan plain, ulong all)
        {
            string state = CoreIsolationClient.State;
            ulong activeMask = CoreIsolationClient.ActiveMask;
            try
            {
                using (var f = new FamilyPolicyFixture(output, "manual-hard-affinity"))
                {
                    Eq(null, f.Mode.SaveManualCorePlan(f.First.Id, null, plain));
                    Eq(null, f.Mode.SaveManualCorePlan(f.Second.Id, null, hard));
                    var stored = new GameProfileStore(f.DirectoryPath).LoadProfiles(false);
                    Eq(false, PolicyResolver.For(FamilyPolicyFind(stored, f.First.Id)).ManualCores.HardAffinity);
                    var frozen = PolicyResolver.For(FamilyPolicyFind(stored, f.Second.Id));
                    Eq(true, frozen.ManualCores.HardAffinity);
                    Eq(null, f.Mode.SaveManualCorePlan(f.Second.Id, hard.Encode(), plain));
                    Eq(true, frozen.ManualCores.HardAffinity);
                    FamilyPolicySetField(f.Mode, "allMask", all);
                    FamilyPolicySetField(f.Mode, "coreManual", hard);
                    AutoCorePlan runtime = hard.RuntimePlan();
                    FamilyPolicySetField(f.Mode, "corePlan", runtime);
                    FamilyPolicySetField(f.Mode, "corePhysical", CpuTopology.PhysicalCoreMasks());
                    FamilyPolicySetField(f.Mode, "coreEngaged", true);
                    CoreIsolationClient.State = "schedule.isolation.active";
                    CoreIsolationClient.ActiveMask = runtime.Exclusive;
                    Eq(all & ~runtime.Exclusive, (ulong)FamilyPolicyInvoke(f.Mode, "CoreBackgroundTarget"));
                    FamilyPolicySetField(f.Mode, "coreManual", plain);
                    Eq(all & ~runtime.Exclusive, (ulong)FamilyPolicyInvoke(f.Mode, "CoreBackgroundTarget"));
                    FamilyPolicySetField(f.Mode, "coreManual", hard);
                    CoreIsolationClient.ActiveMask = 0;
                    Eq(0UL, (ulong)FamilyPolicyInvoke(f.Mode, "CoreBackgroundTarget"));
                    CoreIsolationClient.ActiveMask = runtime.Exclusive;
                    CoreIsolationClient.State = "schedule.isolation.idle";
                    Eq(0UL, (ulong)FamilyPolicyInvoke(f.Mode, "CoreBackgroundTarget"));
                    CoreIsolationClient.State = "schedule.isolation.active";
                    FamilyPolicySetField(f.Mode, "corePinPending", true);
                    Eq(0UL, (ulong)FamilyPolicyInvoke(f.Mode, "CoreBackgroundTarget"));
                    FamilyPolicySetField(f.Mode, "corePinPending", false);
                    foreach (bool legacyFlag in new[] { false, true })
                    {
                        FamilyPolicySetField(f.Mode, "coreManual", new ManualCorePlan(hard.GameMask, false, hard.Topology, legacyFlag));
                        Eq(0UL, (ulong)FamilyPolicyInvoke(f.Mode, "CoreBackgroundTarget"));
                    }
                    FamilyPolicySetField(f.Mode, "coreManual", plain);
                    FamilyPolicySetField(f.Mode, "coreEngaged", false);
                    Eq(0UL, (ulong)FamilyPolicyInvoke(f.Mode, "CoreBackgroundTarget"));
                    FamilyPolicySetField(f.Mode, "coreEngaged", true);
                    FamilyPolicySetField(f.Mode, "coreObservationOnly", true);
                    Eq(0UL, (ulong)FamilyPolicyInvoke(f.Mode, "CoreBackgroundTarget"));
                    FamilyPolicySetField(f.Mode, "coreObservationOnly", false);
                    FamilyPolicySetField(f.Mode, "stopping", true);
                    Eq(0UL, (ulong)FamilyPolicyInvoke(f.Mode, "CoreBackgroundTarget"));
                    FamilyPolicySetField(f.Mode, "stopping", false);
                    Eq(all & ~runtime.Exclusive, (ulong)FamilyPolicyInvoke(f.Mode, "CoreBackgroundTarget"));
                }
            }
            finally { CoreIsolationClient.State = state; CoreIsolationClient.ActiveMask = activeMask; }
            Console.WriteLine("PASS automatic isolation hard affinity: both legacy storage formats, isolation-off, inactive, observation-only, pending, mismatch and stopping gates");
        }

        private static void ManualConcurrentSaveGate(string output, ManualCorePlan original)
        {
            using (var f = new FamilyPolicyFixture(output, "manual-concurrent-save"))
            using (var start = new System.Threading.ManualResetEvent(false))
            {
                Eq(null, f.Mode.SaveManualCorePlan(f.First.Id, null, original));
                var alternatives = new[] { new ManualCorePlan(0x30, false, original.Topology),
                    new ManualCorePlan(0xC0, false, original.Topology) };
                var results = new string[2];
                var errors = new Exception[2];
                var writers = new System.Threading.Thread[2];
                for (int i = 0; i < writers.Length; i++)
                {
                    int index = i;
                    writers[i] = new System.Threading.Thread(delegate()
                    {
                        try { start.WaitOne(); results[index] = f.Mode.SaveManualCorePlan(f.First.Id, original.Encode(), alternatives[index]); }
                        catch (Exception ex) { errors[index] = ex; }
                    });
                    writers[i].IsBackground = true; writers[i].Start();
                }
                start.Set();
                foreach (var writer in writers) Eq(true, writer.Join(5000));
                foreach (var error in errors) if (error != null) throw error;
                Eq(1, (results[0] == null ? 1 : 0) + (results[1] == null ? 1 : 0));
                int winner = results[0] == null ? 0 : 1;
                Eq("schedule.manual.changed", results[1 - winner]);
                Eq(alternatives[winner].Encode(), ManualCorePlan.Raw(f.Current(f.First.Id)));
                var disk = new GameProfileStore(f.DirectoryPath).LoadProfiles(false);
                Eq(alternatives[winner].Encode(), ManualCorePlan.Raw(FamilyPolicyFind(disk, f.First.Id)));
                Eq(false, PolicyResolver.For(f.Current(f.First.Id)).AutoCoresAllowed);
                Console.WriteLine("PASS concurrent manual editors: exactly one save commits; stale draft rejected; disk and memory agree; enable stays off");
            }
        }

        private static void ManualRuntimeFailureStatus(string output, ManualCorePlan plan)
        {
            using (var f = new FamilyPolicyFixture(output, "manual-invalid-runtime"))
            {
                var profile = f.First.Clone();
                profile.Overrides[PolicyCatalog.KeyAutoCores] = "1";
                foreach (var invalid in new[] { new ManualCorePlan(plan.GameMask, true, "FF,3,FC"),
                    new ManualCorePlan(1UL << 63, false, plan.Topology), plan })
                {
                    profile.Overrides[ManualCorePlan.Key] = invalid.Encode();
                    AutoCores.AllocatedForTest = delegate { return invalid == plan ? 0x20UL : 0; };
                    var policy = PolicyResolver.For(profile);
                    Eq(true, policy.AutoCoresAllowed);
                    FamilyPolicySetField(f.Mode, "sessionPolicy", policy);
                    FamilyPolicyInvoke(f.Mode, "BeginAutoCores", policy, true);
                    Eq(true, AutoCores.Status.Manual);
                    Eq(false, AutoCores.Status.Plan.Available);
                    Eq(invalid.RuntimePlan().Blocked, AutoCores.Status.Blocked);
                    Eq(null, f.Mode.ProbeGovernor);
                }
                AutoCores.AllocatedForTest = delegate { return 0; };
            }
        }

        private static void ManualCoreRecoveryGate(string output, ManualCorePlan plan)
        {
            using (var f = new FamilyPolicyFixture(output, "manual-recovery-gate"))
            {
                var profile = f.First.Clone();
                profile.Overrides[PolicyCatalog.KeyAutoCores] = "1";
                profile.Overrides[ManualCorePlan.Key] = plan.Encode();
                var policy = PolicyResolver.For(profile);
                Type pinType = typeof(GameMode).GetNestedType("HardPin", BindingFlags.NonPublic);
                object pin = Activator.CreateInstance(pinType, true);
                var pins = (System.Collections.IDictionary)FamilyPolicyGetField(f.Mode, "hardPins");
                pins.Add(789011, pin);
                try
                {
                    for (int i = 0; i < 3; i++)
                    {
                        FamilyPolicyInvoke(f.Mode, "BeginAutoCores", policy, true);
                        Eq(true, AutoCores.Status.Pending);
                        Eq("schedule.auto.pending", f.Mode.ProbeGovernor.Blocked);
                        f.Mode.ProbeGovernor.EngageManual();
                        Eq(false, f.Mode.ProbeGovernor.Exclusive);
                        Eq(false, (bool)FamilyPolicyInvoke(f.Mode, "ReleaseExclusive"));
                        FamilyPolicyInvoke(f.Mode, "PublishAutoCores", new object[] { null });
                        Eq(true, AutoCores.Status.Pending);
                        Eq(false, (bool)typeof(GameMode).GetMethod("UnboostGames", BindingFlags.Instance | BindingFlags.NonPublic,
                            null, Type.EmptyTypes, null).Invoke(f.Mode, null));
                    }
                }
                finally { pins.Clear(); }
                FamilyPolicyInvoke(f.Mode, "BeginAutoCores", policy, true);
                Eq(false, AutoCores.Status.Pending);
                Eq(null, f.Mode.ProbeGovernor.Blocked);
            }
        }
    }
}
#endif
