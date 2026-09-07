// UI/policy wiring only. Driver and registry doubles are shared with the existing suites.
// No PanelForm constructor, Show/ShowDialog, screenshots or application runtime is used.
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
        private static int graphicsUiChecks;

        internal static int RunGraphicsInputUiRegressionTests()
        {
            string root = Path.Combine(Path.GetTempPath(), "PaviseGraphicsUi-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string oldLog = Logger.LogPath;
            int oldLanguage = Lang.Cur;
            float oldScale = Dpi.Scale;
            Action<string>[] cases =
            {
                GraphicsIntelGlobalConsent,
                GraphicsIntelProfileConsent,
                GraphicsIntelInheritanceAndClear,
                GraphicsIntelDefaultsAndLiveOverrides,
                GraphicsIntelAdmissionBoundaries,
                GraphicsIntelRestoreGenerations,
                GraphicsIntelFailedPersistence,
                GraphicsIntelExplicitReenableClearsFuse,
                GraphicsApplicationDialogLayout,
                GraphicsVendorCardLayout
            };
            graphicsUiChecks = 0;
            try
            {
                foreach (Action<string> test in cases)
                {
                    Settings.UseTransientStoreForCurrentProcess();
                    Logger.ResetWriteBarrierForTest(); Logger.LogPath = Path.Combine(root, "decisions.log");
                    Lang.Cur = 0; Dpi.Scale = 1f; Theme.DropFontCache();
                    test(root);
                    Console.WriteLine("PASS " + test.Method.Name);
                }
                Console.WriteLine("PASS graphics-input-ui assertions=" + graphicsUiChecks
                    + " driver=mocked registry=mocked windows_shown=false snapshots=0");
                return cases.Length;
            }
            finally
            {
                Lang.Cur = oldLanguage; Dpi.Scale = oldScale; Theme.DropFontCache();
                Logger.ResetWriteBarrierForTest(); Logger.LogPath = oldLog;
                Settings.UseTransientStoreForCurrentProcess();
                string actual = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
                string temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
                if (actual.StartsWith(temporary + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    && Path.GetFileName(actual).StartsWith("PaviseGraphicsUi-", StringComparison.Ordinal)
                    && Directory.Exists(actual)) Directory.Delete(actual, true);
            }
        }

        private static void GraphicsUiCheck(bool condition, string detail)
        {
            graphicsUiChecks++;
            if (!condition) throw new InvalidOperationException("Graphics UI regression: " + detail);
        }

        private sealed class GraphicsIntelFixture : IDisposable
        {
            internal readonly IntelFakeLedger Ledger = new IntelFakeLedger();
            internal readonly IntelFakeControl Api;
            internal readonly FamilyPolicyFixture Family;
            private readonly IntelLowLatencyEngine previous;
            internal GameMode Mode { get { return Family.Mode; } }
            internal GraphicsIntelFixture(string root, string name)
            {
                Api = new IntelFakeControl(Ledger);
                previous = IntelGraphicsTweaks.ReplaceEngineForTest(new IntelLowLatencyEngine(Api, Ledger));
                Family = new FamilyPolicyFixture(root, name);
            }
            internal void Ready()
            {
                Mode.ClearProfileOverrides("first");
                GameProfile profile = Family.Current("first");
                FamilyPolicySetField(Mode, "sessionPolicy", PolicyResolver.For(profile));
                FamilyPolicySetField(Mode, "activeDetection", new GameDetection { Profile = profile });
                FamilyPolicySetField(Mode, "enabled", true);
                FamilyPolicySetField(Mode, "active", true);
            }
            internal PanelForm Ui()
            {
                var form = (PanelForm)FormatterServices.GetUninitializedObject(typeof(PanelForm));
                GC.SuppressFinalize(form);
                ResetFlowCpuIdleUiSet(form, "gameMode", Mode);
                ResetFlowCpuIdleUiSet(form, "elevated", true);
                ResetFlowCpuIdleUiSet(form, "cfgProfileId", "first");
                ResetFlowCpuIdleUiSet(form, "stackBase", new Dictionary<Control, Dictionary<Control, int>>());
                SyncProfile(form);
                return form;
            }
            internal void SyncProfile(PanelForm form)
            { ResetFlowCpuIdleUiSet(form, "cfgProfile", Family.Current("first")); }
            internal Func<bool> Capture() { return Mode.CaptureIntelGraphicsAdmissionForTest(); }
            public void Dispose()
            {
                IntelGraphicsTweaks.ReplaceEngineForTest(previous);
                GraphicsUiCheck(Api.Writes == 0 && Ledger.Value.Length == 0,
                    "UI/policy-only checks issued a driver setting or created a driver receipt");
                GraphicsUiCheck(FamilyPolicyGetField(Mode, "worker") == null,
                    "UI/policy-only checks started the application worker");
                Family.Dispose();
            }
        }

        private static T GraphicsUiField<T>(object instance, string name)
        {
            FieldInfo field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null) throw new InvalidOperationException("Missing UI fixture field " + name);
            return (T)field.GetValue(instance);
        }

        private static object GraphicsUiCall(object instance, string name, params object[] args)
        {
            MethodInfo method = instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
            if (method == null) throw new InvalidOperationException("Missing UI fixture method " + name);
            return method.Invoke(instance, args);
        }

        private static void GraphicsIntelGlobalConsent(string root)
        {
            using (var f = new GraphicsIntelFixture(root, "global-confirm"))
            using (var panel = new Panel())
            {
                PanelForm form = f.Ui();
                bool accept = false; int prompts = 0;
                form.IntelLowLatencyConfirmationForTest = delegate
                {
                    prompts++;
                    GraphicsUiCheck(!f.Mode.IntelLowLatency && !Settings.Load(PolicyCatalog.KeyIntelLowLatency, false),
                        "global Intel choice was saved before its warning");
                    return accept;
                };
                GraphicsUiCall(form, "BuildIntelGraphicsPage", panel);
                Toggle toggle = GraphicsUiField<Toggle>(form, "swIntelLowLatency");
                string library = File.ReadAllText(f.Family.LibraryFile);
                toggle.Checked = true;
                GraphicsUiCheck(prompts == 1 && !toggle.Checked && !f.Mode.IntelLowLatency
                    && !Settings.Load(PolicyCatalog.KeyIntelLowLatency, false)
                    && File.ReadAllText(f.Family.LibraryFile) == library, "canceling the Intel global warning changed settings");
                accept = true; toggle.Checked = true;
                GraphicsUiCheck(prompts == 2 && toggle.Checked && f.Mode.IntelLowLatency
                    && Settings.Load(PolicyCatalog.KeyIntelLowLatency, false), "accepted global Intel choice was not saved");
                toggle.Checked = false;
                GraphicsUiCheck(prompts == 2 && !f.Mode.IntelLowLatency, "turning Intel off unnecessarily asked to enable it");

                f.Mode.IntelLowLatency = true;
                f.Api.Adapters[0].LowLatencySupported = false;
                GraphicsUiCall(form, "SyncIntelGraphicsToggles");
                GraphicsUiCheck(toggle.Enabled && toggle.Checked, "unsupported hardware could not turn off a stored Intel opt-in");
                toggle.Checked = false;
                GraphicsUiCheck(!toggle.Enabled && !f.Mode.IntelLowLatency && prompts == 2,
                    "unsupported hardware did not allow disabling the stored option");
                toggle.Checked = true;
                GraphicsUiCheck(!toggle.Checked && !f.Mode.IntelLowLatency && prompts == 2,
                    "unsupported activation bypassed capability checking");
                GraphicsUiCheck(!panel.IsHandleCreated, "global consent test created a native control tree");
            }
        }

        private static void GraphicsIntelProfileConsent(string root)
        {
            using (var f = new GraphicsIntelFixture(root, "profile-confirm"))
            {
                PanelForm form = f.Ui();
                bool accept = false; int prompts = 0;
                form.IntelLowLatencyConfirmationForTest = delegate { prompts++; return accept; };
                string before = File.ReadAllText(f.Family.LibraryFile);
                GraphicsUiCheck(!(bool)GraphicsUiCall(form, "ApplyCfgPolicyChoice", PolicyCatalog.KeyIntelLowLatency, "1")
                    && prompts == 1 && File.ReadAllText(f.Family.LibraryFile) == before
                    && !f.Family.Current("first").Overrides.ContainsKey(PolicyCatalog.KeyIntelLowLatency),
                    "canceling per-game Intel confirmation saved an override");
                accept = true;
                GraphicsUiCheck((bool)GraphicsUiCall(form, "ApplyCfgPolicyChoice", PolicyCatalog.KeyIntelLowLatency, "1")
                    && prompts == 2 && f.Family.Current("first").Overrides[PolicyCatalog.KeyIntelLowLatency] == "1",
                    "accepted per-game Intel choice was not saved");
                f.SyncProfile(form); f.Api.Adapters[0].LowLatencySupported = false;
                GraphicsUiCheck((bool)GraphicsUiCall(form, "ApplyCfgPolicyChoice", PolicyCatalog.KeyIntelLowLatency, "0")
                    && prompts == 2 && f.Family.Current("first").Overrides[PolicyCatalog.KeyIntelLowLatency] == "0",
                    "unsupported per-game Intel opt-in could not be disabled");
                f.SyncProfile(form); before = File.ReadAllText(f.Family.LibraryFile);
                GraphicsUiCheck(!(bool)GraphicsUiCall(form, "ApplyCfgPolicyChoice", PolicyCatalog.KeyIntelLowLatency, "1")
                    && prompts == 2 && File.ReadAllText(f.Family.LibraryFile) == before,
                    "unsupported per-game Intel activation was saved");
            }
        }

        private static void GraphicsIntelInheritanceAndClear(string root)
        {
            using (var f = new GraphicsIntelFixture(root, "inherit-confirm"))
            {
                f.Mode.IntelLowLatency = true;
                f.Mode.SetProfileOverride("first", PolicyCatalog.KeyIntelLowLatency, "0");
                PanelForm form = f.Ui();
                bool accept = false; int prompts = 0;
                form.IntelLowLatencyConfirmationForTest = delegate { prompts++; return accept; };
                string before = File.ReadAllText(f.Family.LibraryFile);
                GraphicsUiCheck(PanelForm.CfgIntelInheritanceNeedsConfirmation(f.Family.Current("first")),
                    "explicit off-to-inherited-on was not recognized as enabling Intel");
                GraphicsUiCheck(!(bool)GraphicsUiCall(form, "ApplyCfgPolicyChoice", PolicyCatalog.KeyIntelLowLatency, null)
                    && prompts == 1 && File.ReadAllText(f.Family.LibraryFile) == before,
                    "canceling Intel inheritance removed the explicit off");
                accept = true;
                GraphicsUiCheck((bool)GraphicsUiCall(form, "ApplyCfgPolicyChoice", PolicyCatalog.KeyIntelLowLatency, null)
                    && prompts == 2 && !f.Family.Current("first").Overrides.ContainsKey(PolicyCatalog.KeyIntelLowLatency),
                    "confirmed Intel inheritance did not remove the off override");
                f.Mode.SetProfileOverride("first", PolicyCatalog.KeyIntelLowLatency, "0"); f.SyncProfile(form);
                before = File.ReadAllText(f.Family.LibraryFile); accept = false;
                int count = f.Family.Current("first").Overrides.Count;
                GraphicsUiCheck(!(bool)GraphicsUiCall(form, "ApplyCfgClearAllOverrides") && prompts == 3
                    && f.Family.Current("first").Overrides.Count == count && File.ReadAllText(f.Family.LibraryFile) == before,
                    "Clear All bypassed the Intel warning or partially removed other overrides");
                accept = true;
                GraphicsUiCheck((bool)GraphicsUiCall(form, "ApplyCfgClearAllOverrides") && prompts == 4
                    && f.Family.Current("first").Overrides.Count == 0, "fully confirmed Clear All did not restore inheritance");
            }
        }

        private static void GraphicsIntelDefaultsAndLiveOverrides(string root)
        {
            using (var f = new GraphicsIntelFixture(root, "live-policy"))
            {
                f.Ready();
                GraphicsUiCheck(!f.Mode.IntelLowLatency && !PolicyResolver.Global().IntelLowLatency && !f.Capture()(),
                    "a new installation opted into Intel low latency");
                PolicySnapshot snapshot = (PolicySnapshot)FamilyPolicyGetField(f.Mode, "sessionPolicy");
                GraphicsUiCheck(f.Family.Current("first").Overrides.Count == 0 && snapshot.IsGlobal,
                    "live override fixture did not start with an empty snapshot");
                GraphicsUiCheck(f.Mode.SetProfileOverride("first", PolicyCatalog.KeyIntelLowLatency, "1")
                    && !snapshot.IntelLowLatency && f.Capture()(), "live first override was ignored in favor of the old snapshot");
                Func<bool> old = f.Capture();
                GraphicsUiCheck(f.Mode.SetProfileOverride("first", PolicyCatalog.KeyIntelLowLatency, "0")
                    && !old() && !f.Capture()(), "live off did not cancel prior Intel admission");
                GraphicsUiCheck(f.Mode.SetProfileOverride("first", PolicyCatalog.KeyIntelLowLatency, "1")
                    && !old() && f.Capture()(), "off/on revived an earlier Intel request or failed to admit the new choice");
                GraphicsUiCheck(f.Mode.ClearProfileOverride("first", PolicyCatalog.KeyIntelLowLatency) && !f.Capture()(),
                    "clearing per-game on did not inherit global off");
                f.Mode.IntelLowLatency = true;
                GraphicsUiCheck(f.Capture()(), "global on was hidden behind a captured global-off snapshot");
                f.Mode.SetProfileOverride("first", PolicyCatalog.KeyIntelLowLatency, "0"); old = f.Capture();
                GraphicsUiCheck(!old() && f.Mode.ClearProfileOverrides("first") > 0 && !old() && f.Capture()(),
                    "bulk reset did not invalidate the old off request and inherit current global on");
            }
        }

        private static void GraphicsIntelAdmissionBoundaries(string root)
        {
            using (var f = new GraphicsIntelFixture(root, "admission"))
            {
                f.Ready(); f.Mode.IntelLowLatency = true;
                Func<bool> initial = f.Capture();
                GraphicsUiCheck(initial(), "valid active Intel policy was not admitted");
                foreach (string field in new[] { "active", "enabled", "stopping", "panicReq", "stickyGraceOnly",
                    "gameGoneSinceTicks", "profileSaveFailureSignaled" })
                {
                    object original = FamilyPolicyGetField(f.Mode, field);
                    object changed = field == "gameGoneSinceTicks" ? (object)1L
                        : field == "profileSaveFailureSignaled" ? (object)1 : (object)(field != "active" && field != "enabled");
                    FamilyPolicySetField(f.Mode, field, changed);
                    GraphicsUiCheck(!initial() && !f.Capture()(), "unsafe session state admitted an Intel change: " + field);
                    FamilyPolicySetField(f.Mode, field, original);
                }
                object target = FamilyPolicyGetField(f.Mode, "activeDetection");
                FamilyPolicySetField(f.Mode, "activeDetection", null);
                GraphicsUiCheck(!initial() && !f.Capture()(), "no verified game admitted an Intel change");
                FamilyPolicySetField(f.Mode, "activeDetection", new GameDetection { Profile = f.Family.Current("second") });
                GraphicsUiCheck(!initial() && !f.Capture()(), "a different active game reused the old Intel session policy");
                FamilyPolicySetField(f.Mode, "activeDetection", target);
                f.Mode.IntelLowLatency = false; f.Mode.IntelLowLatency = true;
                GraphicsUiCheck(!initial() && f.Capture()(), "global off/on revived an older Intel callback");
            }
        }

        private static void GraphicsIntelRestoreGenerations(string root)
        {
            using (var f = new GraphicsIntelFixture(root, "restore-generation"))
            {
                f.Ready(); f.Mode.IntelLowLatency = true;
                Func<bool> old = f.Capture();
                FamilyPolicyInvoke(f.Mode, "BeginIntelGraphicsRestore");
                GraphicsUiCheck(!old() && !f.Capture()(), "Panic's early restore boundary left Intel admission open");
                FamilyPolicyInvoke(f.Mode, "BeginIntelGraphicsRestore");
                FamilyPolicyInvoke(f.Mode, "EndIntelGraphicsRestore");
                GraphicsUiCheck(!f.Capture()(), "one nested restore end reopened Intel admission too early");
                FamilyPolicyInvoke(f.Mode, "EndIntelGraphicsRestore");
                GraphicsUiCheck(!old() && f.Capture()(), "completed restore revived the previous Intel generation");
                GraphicsUiCheck((int)FamilyPolicyGetField(f.Mode, "intelGraphicsRestorePending") == 0,
                    "nested restore boundary leaked its pending count");
            }
        }

        private static void GraphicsIntelFailedPersistence(string root)
        {
            using (var f = new GraphicsIntelFixture(root, "global-save-failure"))
            {
                f.Ready();
                Settings.SuspendWritesForReset();
                f.Mode.IntelLowLatency = true;
                GraphicsUiCheck(!f.Mode.IntelLowLatency && !f.Capture()()
                    && !Settings.Load(PolicyCatalog.KeyIntelLowLatency, false),
                    "failed global save still published Intel consent");
            }
            using (var f = new GraphicsIntelFixture(root, "profile-save-failure"))
            {
                f.Ready(); f.Mode.IntelLowLatency = true;
                Func<bool> old = f.Capture();
                string before = File.ReadAllText(f.Family.LibraryFile);
                using (var lease = new FileStream(f.Family.LibraryFile, FileMode.Open, FileAccess.Read, FileShare.Read))
                    GraphicsUiCheck(!f.Mode.SetProfileOverride("first", PolicyCatalog.KeyIntelLowLatency, "0"),
                        "locked profile save was reported successful");
                GraphicsUiCheck(!f.Mode.ProfileStoreSaveFailed && !old() && f.Capture()()
                    && File.ReadAllText(f.Family.LibraryFile) == before,
                    "busy profile commit did not cancel stale work and preserve the previous policy");
                GraphicsUiCheck(f.Mode.SetProfileOverride("first", PolicyCatalog.KeyIntelLowLatency, "0")
                    && !f.Capture()(), "unlocked profile save did not apply the requested Intel policy");
            }
        }

        private static void GraphicsIntelExplicitReenableClearsFuse(string root)
        {
            using (var f = new GraphicsIntelFixture(root, "explicit-reenable"))
            {
                f.Ready(); f.Mode.SetProfileOverride("first", PolicyCatalog.KeyIntelLowLatency, "1");
                var fused = (HashSet<string>)FamilyPolicyGetField(f.Mode, "envFused");
                fused.Add("intelll"); Settings.Save("EnvFuse_intelll", true);
                ((Dictionary<string, int>)FamilyPolicyGetField(f.Mode, "envFailures"))["intelll"] = 2;
                ((Dictionary<string, long>)FamilyPolicyGetField(f.Mode, "envNextAttempt"))["intelll"] = long.MaxValue;
                GraphicsUiCheck(!f.Capture()(), "fused Intel policy admitted a new change");
                GraphicsUiCheck(f.Mode.SetProfileOverride("first", PolicyCatalog.KeyIntelLowLatency, "1")
                    && !fused.Contains("intelll") && !Settings.Load("EnvFuse_intelll", false)
                    && !((Dictionary<string, int>)FamilyPolicyGetField(f.Mode, "envFailures")).ContainsKey("intelll")
                    && !((Dictionary<string, long>)FamilyPolicyGetField(f.Mode, "envNextAttempt")).ContainsKey("intelll")
                    && f.Capture()(), "explicit per-game re-enable left the previous failure fuse active");
            }
        }

        private static void GraphicsApplicationDialogLayout(string root)
        {
            foreach (int language in new[] { 0, 1 })
            foreach (float scale in new[] { 1f, 1.5f, 2f })
            {
                Lang.Cur = language; Dpi.Scale = scale; Theme.DropFontCache();
                using (var f = new AppGpuFixture())
                {
                    f.Own(null);
                    int writes = f.Control.Writes;
                    using (var dialog = new AppGpuPreferencesDialog(f.Manager))
                    {
                        int shown = 0;
                        dialog.Shown += delegate { shown++; };
                        foreach (Size size in new[] { new Size(Theme.S(760), Theme.S(590)), new Size(320, 300) })
                        {
                            dialog.ClientSize = size;
                            GraphicsUiCall(dialog, "LayoutContent");
                            Label heading = GraphicsUiField<Label>(dialog, "heading");
                            Label note = GraphicsUiField<Label>(dialog, "note");
                            Label notice = GraphicsUiField<Label>(dialog, "notice");
                            Label status = GraphicsUiField<Label>(dialog, "status");
                            RoundPanel wrap = GraphicsUiField<RoundPanel>(dialog, "wrap");
                            PillButton browse = GraphicsUiField<PillButton>(dialog, "browse");
                            PillButton restore = GraphicsUiField<PillButton>(dialog, "restore");
                            PillButton close = GraphicsUiField<PillButton>(dialog, "close");
                            GraphicsUiCheck(dialog.Text == Lang.T("set.apppref") && heading.Text == dialog.Text
                                && notice.Text == Lang.T("apppref.nextlaunch"), "dialog title/next-launch notice was not localized");
                            GraphicsUiCheck(note.Bottom <= notice.Top && notice.Bottom <= browse.Top
                                && browse.Bottom < wrap.Top && wrap.Height > 0 && wrap.Bottom < status.Top
                                && status.Bottom <= restore.Top && close.Top == restore.Top,
                                "dialog text/list/footer overlap: lang=" + language + " scale=" + scale + " size=" + size);
                            GraphicsUiCheck(TextRenderer.MeasureText(note.Text, note.Font,
                                new Size(note.Width, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height <= note.Height
                                && TextRenderer.MeasureText(notice.Text, notice.Font,
                                new Size(notice.Width, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height <= notice.Height,
                                "dialog scope/next-launch text was clipped");
                            GraphicsUiCheck(dialog.AutoScroll && (size.Width > 320
                                || dialog.AutoScrollMinSize.Width >= Theme.S(620) && dialog.AutoScrollMinSize.Height >= Theme.S(460)),
                                "small-work-area dialog did not expose a reachable scrolling canvas");
                            GraphicsUiCheck(!dialog.Visible && shown == 0 && f.Control.Writes == writes,
                                "layout validation displayed a window or changed a preference");
                        }
                        ListBox list = GraphicsUiField<TechListBox>(dialog, "list");
                        list.SelectedIndex = 0;
                        f.Control.Hardware = false; GraphicsUiCall(dialog, "Reload", new object[] { null });
                        GraphicsUiCheck(!GraphicsUiField<PillButton>(dialog, "browse").Enabled
                            && !GraphicsUiField<PillButton>(dialog, "running").Enabled
                            && GraphicsUiField<PillButton>(dialog, "remove").Enabled
                            && GraphicsUiField<PillButton>(dialog, "restore").Enabled
                            && !GraphicsUiField<PillButton>(dialog, "forget").Enabled,
                            "unsupported hardware disabled existing recovery or allowed new preferences");
                        f.Control.Set(AppGpuPath, "GpuPreference=2;User=keep;");
                        GraphicsUiCall(dialog, "Reload", new object[] { null });
                        GraphicsUiCheck(GraphicsUiField<PillButton>(dialog, "forget").Enabled
                            && AppGpuPreferencesDialog.StateText((AppGpuPreferenceEntry)list.SelectedItem) == Lang.T("apppref.state.external"),
                            "external preference state did not expose the safe record-only action");
                        GraphicsUiField<PillButton>(dialog, "close").PerformClick();
                        GraphicsUiCheck(shown == 0 && f.Control.Writes == writes && f.Manager.HasResidue,
                            "closing the dialog undid the persistent preference list");
                    }
                }
            }
        }

        private static void GraphicsVendorCardLayout(string root)
        {
            foreach (int language in new[] { 0, 1 })
            foreach (float scale in new[] { 1f, 1.5f, 2f })
            {
                Lang.Cur = language; Dpi.Scale = scale; Theme.DropFontCache();
                using (var f = new GraphicsIntelFixture(root, "cards"))
                using (var app = new AppGpuFixture())
                using (var common = new Panel { Size = new Size(Theme.S(956), Theme.S(530)), AutoScroll = true })
                using (var intel = new Panel { Size = common.Size, AutoScroll = true })
                {
                    PanelForm form = f.Ui();
                    form.IntelLowLatencyConfirmationForTest = delegate { throw new InvalidOperationException("Layout requested consent"); };
                    GraphicsUiCall(form, "BuildCommonGraphicsPage", common);
                    GraphicsUiCall(form, "BuildIntelGraphicsPage", intel);
                    // 公共页三张卡 Intel 页第二张是 Endurance Gaming
                    GraphicsUiCheck(common.Controls.Count == 3 && intel.Controls.Count == 2,
                        "common/Intel pages have missing or duplicate cards");
                    SettingCard appCard = (SettingCard)common.Controls[1];
                    SettingCard autoCard = (SettingCard)common.Controls[2];
                    SettingCard intelCard = (SettingCard)intel.Controls[0];
                    SettingCard enduranceCard = (SettingCard)intel.Controls[1];
                    GraphicsUiCheck(enduranceCard.Title == Lang.T("set.intel.endurance"),
                        "the Endurance Gaming card lost localization");
                    GraphicsUiCheck(appCard.Title == Lang.T("set.apppref") && appCard.Desc == Lang.T("set.apppref.n")
                        && appCard.Expanded && appCard.HasStatus && intelCard.Title == Lang.T("set.intel.lowlatency"),
                        "vendor cards lost localization or hid the next-launch scope by default");
                    GraphicsUiCheck(autoCard.Title == Lang.T("set.autogpu") && autoCard.Desc == Lang.T("set.autogpu.n")
                        && autoCard.HasStatus, "the auto GPU preference card lost localization or its next-launch scope");
                    intelCard.Expanded = true;
                    autoCard.Expanded = true;
                    foreach (SettingCard card in new[] { appCard, autoCard, intelCard })
                    {
                        Control host = card.Controls[0];
                        int textWidth = card.Width - Theme.S(84) - host.Width - Theme.S(14)
                            - (card.Collapsible ? Theme.S(20) : 0);
                        int available = card.Height - Theme.S(11) - Theme.S(22) - Theme.S(1)
                            - (card.HasStatus ? Theme.S(SettingCard.StatusLineH) : 0) - Theme.S(9);
                        int line = Math.Max(1, TextRenderer.MeasureText("Ag", Theme.UI(8.5f, false)).Height);
                        int needed = TextRenderer.MeasureText(card.Desc, Theme.UI(8.5f, false),
                            new Size(textWidth, int.MaxValue), TextFormatFlags.WordBreak).Height;
                        GraphicsUiCheck(card.ClientRectangle.Contains(host.Bounds) && textWidth > 0
                            && available / line * line >= needed,
                            "expanded vendor-card consent/scope text or control did not fit: lang=" + language + " scale=" + scale);
                    }
                    GraphicsUiCheck(!common.IsHandleCreated && !intel.IsHandleCreated && app.Control.Writes == 0,
                        "card construction started a window or changed an application preference");
                }
            }
        }
    }
}
#endif
