#if PAVISE_SELFTEST && PAVISE_PERFORMANCE_PAGE_CHECKS
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        public static bool TryHandleRuntimeMode(string[] args) { throw new InvalidOperationException("UI fixture cannot start the runtime"); }
    }
    internal static class PerformanceWorkspacePageChecks
    {
        private static int checks, viewports;
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        private static void Check(bool ok, string detail) { if (!ok) throw new InvalidOperationException("FAIL: " + PerformanceWorkspaceLayout.Context + ": " + detail); checks++; }
        private static T Field<T>(PanelForm form, string name) { return (T)typeof(PanelForm).GetField(name, Hidden).GetValue(form); }
        private static object Call(PanelForm form, string name, params object[] args) { return typeof(PanelForm).GetMethod(name, Hidden).Invoke(form, args); }
        private static void Save(Control control, string path)
        {
            using (var bitmap = new Bitmap(control.Width, control.Height)) {
                control.DrawToBitmap(bitmap, control.ClientRectangle); bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
        }
        private static void ValidateCards(Control parent)
        {
            PerformanceWorkspaceLayout.Validate((PerformanceFeatureGrid)parent);
        }
        private static Control Named(PanelForm form, string name)
        {
            var found = form.Controls.Find(name, true); Check(found.Length == 1, "Missing/duplicate actual control: " + name); return found[0];
        }
        private static PerformanceRestartScope SectionScope(Control target)
        {
            for (Control branch = target; branch.Parent != null; branch = branch.Parent) {
                PerformanceSectionHeading current = null;
                foreach (Control sibling in branch.Parent.Controls) {
                    if (sibling == branch) { if (current != null) return current.RestartScope; break; }
                    var heading = sibling as PerformanceSectionHeading; if (heading != null) current = heading;
                }
            }
            throw new InvalidOperationException("FAIL: actual feature has no static restart section: " + target.Name);
        }
        private static void ValidateRestartSections(PanelForm form)
        {
            var targets = new Dictionary<string, Control>(StringComparer.Ordinal);
            foreach (var feature in CalibrationFeatureCatalog.All.Where(x => AutomaticPolicy.IsIndependentManual(x.Key))) {
                string key = feature.Key; Control control;
                if (key == PerformanceActionCatalog.CpuMitigations || key == PerformanceActionCatalog.LastAccess || key == PerformanceActionCatalog.AudioIdle || key == PerformanceTuning.EnvironmentVbsKey)
                    control = Field<SettingCard>(form, "cardPerformanceEnvironmentBundle");
                else if (PerformanceTuning.StorageMember(key)) control = Field<SettingCard>(form, "cardPerformanceEnvironmentStorage");
                else if (key == PerformanceActionCatalog.Quantum) control = Field<SettingCard>(form, "cardPerformanceEnvironmentQuantum");
                else if (key == PrivacyActionCatalog.NtfsMemory) control = Named(form, "privacyToggle" + key);
                else if (key == "GmMmcss") control = Named(form, "manualPerformance_" + key);
                else control = Named(form, "environment" + key);
                Check(control != null && SectionScope(control) == PerformanceRestartScope.SystemRestart, "Independent manual member is outside the OS-restart section: " + key);
                targets.Add(key, control);
            }
            Check(targets.Count == 14, "Restart section check did not cover all fourteen actual manual members");
            foreach (string name in new[] { "environmentWindowedOpt", "environmentVrrOpt", "environmentNvVrr" })
                Check(SectionScope(Named(form, name)) == PerformanceRestartScope.ProcessRestart, "Game-restart control is mislabeled as OS restart: " + name);
            Check(SectionScope(Named(form, "manualPerformance_" + PolicyCatalog.KeyBoost)) == PerformanceRestartScope.None, "Ordinary live priority control is in a restart section");
            foreach (var action in PrivacyActionCatalog.All) if (action.Key != PrivacyActionCatalog.NtfsMemory)
                Check(SectionScope(Named(form, "privacyToggle" + action.Key)) != PerformanceRestartScope.SystemRestart, "Nonreboot privacy action inherited the whole wrapper's OS-restart label: " + action.Key);
        }
        private static void ValidateConciseDiagnostics()
        {
            string tail = "\nRAW_DETAIL_71\r\n权限与恢复标记_92";
            Check(PanelForm.ConcisePerformanceDetail("Verbose static paragraph" + tail, "Short base") == "Short base" + tail,
                "Short description removed or normalized raw diagnostic bytes");
            string crlfTail = "\r\nRAW_FIRST_CRLF_41\r\nrestore/readback marker_62";
            Check(PanelForm.ConcisePerformanceDetail("Old base" + crlfTail, "Short base") == "Short base" + crlfTail,
                "Short description dropped the first CRLF separator byte");
            Check(PanelForm.CondenseStoragePerformanceDetail("Old base" + crlfTail, "restore-failed", new[] { "off", "off", "off" }).EndsWith(crlfTail, StringComparison.Ordinal),
                "Failure detail did not retain the exact first CRLF and diagnostic suffix");
            string ordinary = "Old base\nnormal member one\nnormal member two";
            foreach (string state in new[] { "off", "restored", "ready" }) {
                string[] normal = state == "ready" ? new[] { "ready", "applied", "configured" }
                    : state == "restored" ? new[] { "restored", "off", "" } : new[] { "", "off", "off" };
                Check(!PanelForm.CondenseStoragePerformanceDetail(ordinary, state, normal).Contains("normal member"), "Normal storage details still repeat the member footer");
            }
            foreach (string state in new[] { "unsupported", "pending-reboot", "restore-pending-reboot", "partial", "recovery-pending", "unverified", "failed", "unknown" })
                Check(PanelForm.CondenseStoragePerformanceDetail("Old base" + tail, state, new[] { "off", "off", "off" }).Contains(tail),
                    "Storage short copy dropped a non-normal diagnostic: " + state);
            foreach (string[] members in new[] { null, new[] { "off", "off" }, new[] { "ready", null, "ready" }, new[] { "ready", "pending-reboot", "ready" } })
                Check(PanelForm.CondenseStoragePerformanceDetail("Old base" + tail, "ready", members).Contains(tail), "Incomplete or mixed member evidence lost diagnostic text");
            var timer = typeof(PanelForm).GetMethod("GlobalTimerDescription", BindingFlags.NonPublic | BindingFlags.Static);
            Check(timer != null, "Timer explanation pure helper is missing");
            string readback = "READBACK_53\r\nresolution=0.50 ms; original diagnostic";
            foreach (string state in new[] { "ready", "timer-limited", "timer-unverified", "restore-pending-reboot", "failed", "unsupported" }) {
                string text = (string)timer.Invoke(null, new object[] { state, false, readback });
                Check(text.Contains(readback) && text.Contains(Lang.T("env.gtimer.state.release-pending")), "Short timer text lost actual readback or pending release: " + state);
            }
        }
        // Caller owns DPI/work-area setup. Settings are always transient; an
        // optional seed runs after reset and before the actual GameMode exists.
        internal static PanelForm CreateFixtureForTest(string folder, out GameMode mode, bool elevated = true, Action seedSettings = null)
        {
            Settings.UseTransientStoreForCurrentProcess(); Logger.WritesEnabled = false;
            if (seedSettings != null) seedSettings();
            Directory.CreateDirectory(folder); PanelForm.SuppressUiWorkersForTest = true;
            CalibrationRuntime.UseRootForTest(Path.Combine(folder, "calibration"));
            var core = new SuppressionCore(); mode = new GameMode(folder, core); var tamer = new Tamer(core);
            PanelForm form;
            using (var icon = IconArt.MakeIcon(24)) form = new FixtureForm(tamer, mode, icon, elevated);
            try {
                form.ShowInTaskbar = false;
                var privacyProviders = PerformanceWorkspaceLayout.Descendants(Field<DBPanel>(form, "pagePolicy")).OfType<PrivacyOptionsPanel>().ToArray();
                int expectedProviders = PrivacyActionCatalog.All.Select(x => PanelForm.PerformanceRestartFor(x.Key)).Distinct().Count();
                Check(privacyProviders.Length == expectedProviders && expectedProviders > 0, "Privacy provider count differs from the actual active catalog scopes");
                foreach (var retired in PrivacyActionCatalog.Retired)
                    Check(Field<DBPanel>(form, "pagePolicy").Controls.Find("privacyToggle" + retired.Key, true).Length == 0, "Retired privacy action regained a page entry: " + retired.Key);
                foreach (var privacy in privacyProviders) {
                    privacy.InspectionVisibleForTest = delegate { return false; };
                    privacy.SetForTest = delegate { throw new InvalidOperationException("Navigation must not change system settings"); };
                    privacy.RefreshForTest = delegate { throw new InvalidOperationException("Layout navigation must not query real settings"); };
                    privacy.StateForTest = delegate(PrivacyAction action, string target) { return new PrivacyResult { State = "restored", Supported = true, Detail = "Synthetic UI state" }; };
                }
                SetRecovery(new CalibrationRecoveryStatus(true, 0, 0, 0, "UI-only clear recovery fixture"));
                Call(form, "RefreshAutomationPage"); return form;
            } catch { form.Dispose(); throw; }
        }
        private static void Run(string output, int dpi, int language, bool light)
        {
            Console.WriteLine("UI fixture " + dpi + "/" + language + "/" + light);
            Settings.UseTransientStoreForCurrentProcess(); PrivacyControl.ResetForTest();
            EnvironmentEnhancementEligibility.SampleForTest = delegate { return new EnvironmentEnhancementEligibility.MemorySnapshot {
                Installed = 32 * EnvironmentEnhancementEligibility.GiB, Total = 31 * EnvironmentEnhancementEligibility.GiB,
                Available = 8 * EnvironmentEnhancementEligibility.GiB }; };
            CalibrationCoverage.BeginRun();
            Dpi.NoFit = true; Dpi.Scale = dpi / 96f; Theme.DropFontCache(); Lang.Cur = language; Theme.SetLight(light);
            PerformanceWorkspaceLayout.Context = "page fixture " + dpi + "/" + language + "/" + light;
            ValidateConciseDiagnostics();
            string folder = Path.Combine(output, "page-fixture-" + dpi + "-" + language + "-" + light);
            Directory.CreateDirectory(folder); GameMode mode;
            using (var form = CreateFixtureForTest(folder, out mode)) {
                form.Location = new Point(-32000, -32000); form.StartPosition = FormStartPosition.Manual;
                form.Show(); form.ShowPageForShot(PageId.Policy); Call(form, "StopPageReveal");
                Check(!form.UiActive && !mode.Enabled, "UI fixture started product workers or game mode");
                var panels = Field<DBPanel[]>(form, "performanceTabPanels");
                var bridge = Field<TechTabs>(form, "performanceTabs");
                var categories = Field<PerformanceCategoryBar>(form, "performanceCategories");
                Check(!bridge.Visible && !bridge.TabStop && categories.Visible, "Legacy tab presentation is still exposed");
                Check(form.Controls.Find("PerformanceRecoveryOrTest", true).Length == 1 && form.Controls.Find("OpenPerformanceEvidence", true).Length == 1, "Task actions duplicated or missing");
                var before = new Dictionary<string, string>();
                foreach (PolicyItem item in PolicyCatalog.All) before[item.Key] = PolicyResolver.RawGlobalValue(item.Key);
                Check(panels.Length == 4, "Real page is missing a performance category");
                ValidateRestartSections(form);
                foreach (Control categoryButton in categories.Controls) {
                    var rog = categoryButton as PerformanceActionButton;
                    Check(rog != null && rog.IsCategoryTab, "Performance category is not using the ROG tab presentation");
                }
                PerformanceWorkspaceLayout.CategoryKeyboard(categories, delegate { return bridge.Index; });
                for (int category = 0; category < panels.Length; category++) {
                    categories.Select(category, true); Application.DoEvents();
                    Check(bridge.Index == category && panels[category].Visible, "Category index is not synchronized");
                    for (int i = 0; i < panels.Length; i++) Check(panels[i].Visible == (i == category), "Category visibility mismatch");
                    var grid = (PerformanceFeatureGrid)panels[category]; grid.Reflow();
                    Control page = grid.Parent; int originalPageWidth = page.Width;
                    int totalCards = PerformanceWorkspaceLayout.Cards(grid).Count();
                    foreach (int width in new[] { 960, 560, 960 }) {
                        PerformanceWorkspaceLayout.Context = "page " + dpi + "/" + language + "/" + light + "/category=" + category + "/viewport=" + width;
                        Console.WriteLine(PerformanceWorkspaceLayout.Context);
                        // PanelForm fits a fixed logical root by changing scale.
                        // Keep this DPI fixed and exercise actual viewport sizes,
                        // without repairing the production height in the test.
                        page.Width = Theme.S(width);
                        grid.Reflow(); Application.DoEvents();
                        Check(grid.Columns == (width == 960 ? 2 : 1), "Actual page viewport did not cross the column threshold");
                        Check(PerformanceWorkspaceLayout.Cards(grid).Count() == totalCards, "Resizing changed the number of features");
                        // Settle a real page refresh before measuring its next
                        // unchanged periodic refresh, not only direct grid calls.
                        Call(form, "RefreshAutomationPage"); Application.DoEvents();
                        ValidateCards(grid); PerformanceWorkspaceLayout.BottomReachable(grid); viewports++;
                        PerformanceWorkspaceLayout.QuietOperation(grid, delegate { Call(form, "RefreshAutomationPage"); }, "unchanged page refresh");
                        if (dpi == 96 && language == 0 && light) {
                            Save(form, Path.Combine(output, "bottom-page-" + category + "-" + width + ".png"));
                            grid.AutoScrollPosition = Point.Empty; grid.Reflow(); Application.DoEvents();
                            Save(form, Path.Combine(output, "top-page-" + category + "-" + width + ".png"));
                        }
                    }
                    page.Width = originalPageWidth; grid.Reflow(); Application.DoEvents();
                }
                foreach (PolicyItem item in PolicyCatalog.All) Check(before[item.Key] == PolicyResolver.RawGlobalValue(item.Key), "Browsing or reflow changed a manual preference");
                var summary = Field<PerformanceWorkspaceSummary>(form, "performanceSummary");
                var unknownHags = PanelForm.DescribeHags(false, false, false, false, null);
                Check(!unknownHags.Editable && !unknownHags.Status.Contains(AutomaticPolicy.Text("不支持", "unsupported")), "Unavailable HAGS observation is mislabeled as unsupported hardware");
                var pendingHags = PanelForm.DescribeHags(false, false, false, true, 2);
                Check(pendingHags.Checked && !pendingHags.Editable && pendingHags.Status.Contains(AutomaticPolicy.Text("未核验", "unverified")), "An unverified saved HAGS request became proof of current support");
                var rebootHags = PanelForm.DescribeHags(true, true, false, true, 2);
                Check(rebootHags.Status.Contains(AutomaticPolicy.Text("待重启", "after restart")), "A configured HAGS request hides its current off state");
                var driftHags = PanelForm.DescribeHags(true, true, true, true, 1);
                Check(driftHags.Ink == Theme.Warning && driftHags.Status.Contains(AutomaticPolicy.Text("改为关闭", "changed to off")), "Changed HAGS configuration was presented as an unchanged on request");
                var externalHags = PanelForm.DescribeHags(true, true, true, false, 2);
                Check(externalHags.Checked && externalHags.External && externalHags.Editable, "Supported external HAGS On cannot be explicitly disabled");
                var offRequestHags = PanelForm.DescribeHags(true, true, true, false, 1);
                Check(!offRequestHags.Checked && offRequestHags.Editable && offRequestHags.Status.Contains(AutomaticPolicy.Text("请求关闭", "off requested")), "An Off request is presented as On while awaiting reboot");
                var unsupportedHags = PanelForm.DescribeHags(true, false, false, false, null);
                Check(unsupportedHags.Status.Contains(AutomaticPolicy.Text("不支持", "unsupported")), "Known unsupported hardware lost its distinct state");
                Check(!summary.ShowCoverage && summary.Height == Theme.S(56), "Daily header still exposes the full testing inventory");
                var statusAction = Field<PillButton>(form, "performanceStatusLink");
                Check(!statusAction.Visible && Field<PillButton>(form, "performanceEvidenceLink").Visible, "Daily management action is duplicated");
                int recoveryCalls = 0;
                form.PerformanceRecoveryForTest = delegate { recoveryCalls++; return "Synthetic recovery start refused"; };
                SetRecovery(new CalibrationRecoveryStatus(true, 2, 1, 0, "UI-only registered debt")); Call(form, "RefreshAutomationPage");
                Check(statusAction.Visible && statusAction.Enabled && !Field<PillButton>(form, "performanceEvidenceLink").Visible, "Recovery has no independent visible action");
                statusAction.PerformClick();
                Check(recoveryCalls == 1 && summary.ModeDetail == "Synthetic recovery start refused", "Recovery action did not report its explicit failure");
                typeof(CalibrationRuntime).GetField("recoveringOwned", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, true);
                Call(form, "RefreshAutomationPage");
                Check(!statusAction.Enabled && summary.ModeTitle == AutomaticPolicy.Text("正在恢复上次测试", "Recovering the previous test"), "Restore-only operation is mislabeled as a performance test");
                Check(summary.ModeDetail != "Synthetic recovery start refused", "Active recovery displays a stale start failure");
                statusAction.PerformClick(); Check(recoveryCalls == 1, "Repeated click started another recovery");
                if (dpi == 96 && language == 0 && light) Save(form, Path.Combine(output, "recovery-in-progress.png"));
                typeof(CalibrationRuntime).GetField("recoveringOwned", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, false);
                SetRecovery(CalibrationRecoveryStatus.Unknown); Call(form, "RefreshAutomationPage");
                Check(statusAction.Visible && summary.Attention && !Field<PillButton>(form, "performanceEvidenceLink").Visible, "Unverified recovery history is shown as clear");
                SetRecovery(new CalibrationRecoveryStatus(true, 0, 0, 0, "UI-only clear recovery fixture")); Call(form, "RefreshAutomationPage");
                Check(!statusAction.Visible && summary.ModeDetail != "Synthetic recovery start refused", "Confirmed recovery retains a stale failure");
                var snapshot = CalibrationCoverage.Snapshot();
                Check(summary.CoverageText.Contains(snapshot.MeasuredCount + "/" + snapshot.ExperimentCount), "Summary does not use current coverage");
                Check(summary.CoverageText.Contains(AutomaticPolicy.Text("本次测试", "Current test")), "Coverage scope is missing or implies a historical result was retested");
                foreach (CalibrationCoverageEntry entry in snapshot.Entries) if (entry.BackendImplemented && entry.State != CalibrationFeatureTestState.PreservedConstraint) {
                    CalibrationCoverage.Record(entry.Key, CalibrationFeatureTestState.WaitingRestart, "UI-only synthetic state");
                    Call(form, "RefreshAutomationPage");
                    Check(summary.CoverageText.Contains(AutomaticPolicy.Text("待重启 ", "Restart ") + "1"), "Coverage state did not refresh");
                    CalibrationCoverage.BeginRun(); Call(form, "RefreshAutomationPage"); break;
                }
                form.ShowPageForShot(PageId.Environment); Check(bridge.Index == 3, "System deep link lost its category");
                Call(form, "ShowQuantumProfileSetting");
                var quantum = Field<SettingCard>(form, "cardPerformanceEnvironmentQuantum");
                Check(quantum.Visible, "Deep link did not reveal the always-visible system feature");
                Check(quantum.Hosted.Focused, "Deep link did not focus the actual picker");
                Check(!mode.Enabled && !form.UiActive, "Page check activated workers");
                // Exercise an enabled native Forms resize timer at every DPI, then
                // require its teardown before the form receives WM_DESTROY.
                form.ClientSize = new Size(Theme.S(1340), Theme.S(840)); Call(form, "ScheduleFit");
                var fitTimer = Field<System.Windows.Forms.Timer>(form, "fitTimer");
                Check(fitTimer != null && fitTimer.Enabled, "Resize timer teardown fixture did not start");
                form.Hide();
            }
        }
        private static void SetRecovery(CalibrationRecoveryStatus status)
        { typeof(CalibrationRuntime).GetField("recoveryStatus", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, status); }
        private sealed class FixtureForm : PanelForm
        {
            internal FixtureForm(Tamer tamer, GameMode mode, Icon icon, bool elevated) : base(tamer, mode, icon, elevated) { }
            protected override void OnHandleDestroyed(EventArgs e) {
                Check(Field<System.Windows.Forms.Timer>(this, "fitTimer") == null, "Resize timer native window is destroyed inside parent WM_DESTROY");
                Check(Field<System.Windows.Forms.Timer>(this, "outroTimer") == null, "Outro timer outlives the window handle");
                var timer = Field<System.Windows.Forms.Timer>(this, "uiTimer");
                Check(timer == null || !timer.Enabled, "UI timer remains active during window destruction");
                base.OnHandleDestroyed(e);
            }
            protected override bool ShowWithoutActivation { get { return true; } }
            protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ExStyle |= 0x08000000; return cp; } }
        }
        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length == 1 && args[0] == "--negative-control") { Console.WriteLine("FAIL: intentional performance page negative control"); return 3; }
            return PerformanceWorkspaceLayout.PrivateDesktop(delegate {
                Settings.UseTransientStoreForCurrentProcess(); PanelForm.SuppressUiWorkersForTest = true;
                Logger.ResetWriteBarrierForTest(); Logger.WritesEnabled = false; Dpi.Init(); Lang.Init();
                PerformanceWorkspaceLayout.CheckRemovedInteractions();
                bool single = args.Length > 1 && args[1] == "--single";
                foreach (int dpi in single ? new[] { 96 } : new[] { 96, 144, 192 }) foreach (int language in single ? new[] { 0 } : new[] { 0, 1 }) foreach (bool light in single ? new[] { true } : new[] { false, true })
                    Run(args[0], dpi, language, light);
                Console.WriteLine("PASS product performance UI assertions=" + (checks + PerformanceWorkspaceLayout.Checks) + " viewports=" + viewports
                    + " nativeScrollCases=" + PerformanceWorkspaceLayout.NativeScrollCases + " quietReflowPasses=" + PerformanceWorkspaceLayout.QuietReflowPasses
                    + "; four categories, real Forms on private desktop, no collapse, all text visible, fourteen manual restart scopes, transient settings, workers suppressed");
            });
        }
    }
}
#endif
