#if PAVISE_SELFTEST && PAVISE_PERFORMANCE_ROG_CHECKS
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

namespace PaviseApp
{
    internal static partial class SelfTests
    { public static bool TryHandleRuntimeMode(string[] args) { throw new InvalidOperationException("ROG UI checks cannot start product runtime"); } }

    internal static class PerformanceRogWorkspaceChecks
    {
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        private static int checks, viewports;
        private static void Check(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); checks++; }
        private static T Field<T>(PanelForm form, string name) { return (T)typeof(PanelForm).GetField(name, Hidden).GetValue(form); }
        private static void Call(PanelForm form, string name) { typeof(PanelForm).GetMethod(name, Hidden).Invoke(form, null); }
        private static Control Named(PanelForm form, string name)
        { var found = form.Controls.Find(name, true); Check(found.Length == 1, "Missing or duplicate control: " + name); return found[0]; }
        private static PerformanceFeatureCard Feature(Control control)
        { while (control != null && !(control is PerformanceFeatureCard)) control = control.Parent; return control as PerformanceFeatureCard; }
        private static void Scope(PanelForm form, string name, PerformanceRestartScope expected)
        { var card = Feature(Named(form, name)); Check(card != null && card.RestartScope == expected, "Incorrect per-feature restart tag: " + name); }
        private static void VerifyTags(PerformanceFeatureGrid grid)
        {
            foreach (var card in PerformanceWorkspaceLayout.Cards(grid))
            {
                Check(card.ClientRectangle.Contains(card.RestartBounds), "Restart tag clips: " + card.Title);
                Check(!card.RestartBounds.IntersectsWith(card.StatusBounds) && !card.RestartBounds.IntersectsWith(card.DetailBounds), "Restart tag overlaps text: " + card.Title);
                int tagWidth = TextRenderer.MeasureText(card.EffectText, Theme.UI(7.6f, false), Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;
                Check(card.RestartBounds.Width >= tagWidth, "Restart tag truncates: " + card.Title);
                Check(card.Hosted == null || card.Hosted.Height >= Theme.S(28), "Action hit area is smaller than 28 logical pixels");
            }
        }
        private static void Run(string output, int dpi, int language, bool light)
        {
            Settings.UseTransientStoreForCurrentProcess(); Logger.WritesEnabled = false;
            PanelForm.SuppressUiWorkersForTest = true; Dpi.NoFit = true; Dpi.Scale = dpi / 96f; Theme.DropFontCache(); Lang.Cur = language; Theme.SetLight(light);
            string folder = Path.Combine(output, "rog-" + dpi + "-" + language + "-" + light); Directory.CreateDirectory(folder); Paths.Data = folder;
            var core = new SuppressionCore(); var mode = new GameMode(folder, core);
            using (var icon = IconArt.MakeIcon(24))
            using (var form = new PanelForm(new Tamer(core), mode, icon, false))
            {
                form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-32000, -32000); form.ShowInTaskbar = false;
                foreach (var privacy in PerformanceWorkspaceLayout.Descendants(form).OfType<PrivacyOptionsPanel>())
                {
                    privacy.InspectionVisibleForTest = delegate { return false; };
                    privacy.SetForTest = delegate { throw new InvalidOperationException("Layout cannot change system preferences"); };
                    privacy.RefreshForTest = delegate { throw new InvalidOperationException("Layout cannot inspect system preferences"); };
                    privacy.StateForTest = delegate { return new PrivacyResult { State = "restored", Supported = true, Detail = "UI-only diagnostic" }; };
                    privacy.SyncCached();
                }
                form.Show(); form.ShowPageForShot(PageId.Policy); Call(form, "StopPageReveal"); Call(form, "RefreshAutomationPage"); Application.DoEvents();
                Check(!form.UiActive && !mode.Enabled, "Fixture started product workers");
                var page = Field<DBPanel>(form, "pagePolicy");
                var panels = Field<DBPanel[]>(form, "performanceTabPanels");
                var tabs = Field<TechTabs>(form, "performanceTabs");
                var categories = Field<PerformanceCategoryBar>(form, "performanceCategories");
                Check(panels.Length == 5 && !tabs.Visible && !tabs.TabStop, "Five-category navigation contract changed");
                foreach (string key in new[] { LaboratoryPower.EppKey, LaboratoryGpu.ClockKey, LaboratoryGpu.PowerKey, LaboratoryAudio.Key })
                    Check(panels[4].Controls.Find("laboratory_" + key, true).Length == 1, "Lab feature missing or outside laboratory: " + key);
                Check(panels[4].Controls.Find("laboratoryCpuIdle", true).Length == 1, "CPU idle strategies did not share one picker");
                Check(panels[1].Controls.Find("privacyToggle" + PrivacyActionCatalog.BackgroundGlobal, true).Length == 1,
                    "UWP background control must be in the normal background category");
                Check(panels[4].Controls.Find("privacyToggle" + PrivacyActionCatalog.BackgroundGlobal, true).Length == 0,
                    "UWP background control was incorrectly placed in laboratory");

                Check(form.Controls.Find("PerformanceWorkspaceSummary", true).Length == 0, "Normal page retains a mode banner");
                Check(categories.Top <= Theme.S(24) && panels[0].Top <= Theme.S(90), "Repeated title/banner still reserves a high page header");
                Check(!PerformanceWorkspaceLayout.Descendants(page).OfType<PerformanceSectionHeading>().Any(), "Restart groups remain on the page");
                Check(!PerformanceWorkspaceLayout.Descendants(page).Any(c => c.Name.StartsWith("manualDestination_", StringComparison.Ordinal)), "Related-settings jump cards remain");
                foreach (string key in new[] { PolicyCatalog.KeyBoost, PolicyCatalog.KeySuppress, PolicyCatalog.KeyGpuDemote, PolicyCatalog.KeyThreadBoost,
                    PolicyCatalog.KeyDwmBoost, PolicyCatalog.KeyAwake, PolicyCatalog.KeyPauseDl, PerformanceActionCatalog.OpenGl,
                    PerformanceActionCatalog.Mitigations, PerformanceActionCatalog.ShortNames })
                    Named(form, "manualPerformance_" + key);
                foreach (string name in new[] { "environmentHags", "environmentAmdSam", "manualPerformance_GmMmcss", "environment" + PolicyCatalog.KeyTimerTick,
                    "environment" + PolicyCatalog.KeyGlobalTimer, "environment" + PerformanceActionCatalog.Quantum,
                    "environment" + PerformanceTuning.EnvironmentBundleKey, "environment" + PerformanceTuning.StorageGroupKey,
                    "privacyToggle" + PrivacyActionCatalog.NtfsMemory }) Scope(form, name, PerformanceRestartScope.SystemRestart);
                foreach (string name in new[] { "environmentWindowedOpt", "environmentVrrOpt", "environmentNvVrr", "manualPerformance_GpuPrefStageOn", "manualPerformance_AppGpuAutoV1" })
                    Scope(form, name, PerformanceRestartScope.ProcessRestart);
                Scope(form, "manualPerformance_" + PolicyCatalog.KeyBoost, PerformanceRestartScope.None);
                Check(Feature(Named(form, "manualPerformance_AppGpuAutoV1")).EffectText.Contains(language == 0 ? "应用" : "app"), "Background GPU preference incorrectly asks to restart the game");
                var values = PolicyCatalog.All.ToDictionary(item => item.Key, item => PolicyResolver.RawGlobalValue(item.Key));
                PerformanceWorkspaceLayout.CategoryKeyboard(categories, delegate { return tabs.Index; });
                int originalWidth = page.Width;
                for (int category = 0; category < panels.Length; category++)
                {
                    categories.Select(category, true); Application.DoEvents(); var grid = (PerformanceFeatureGrid)panels[category];
                    foreach (int width in new[] { 960, 560, 960 })
                    {
                        page.Width = Theme.S(width); Call(form, "LayoutPerformanceViewport"); grid.Reflow(); Application.DoEvents();
                        PerformanceWorkspaceLayout.Context = "ROG " + dpi + "/" + language + "/" + light + "/" + category + "/" + width;
                        PerformanceWorkspaceLayout.Validate(grid); VerifyTags(grid); PerformanceWorkspaceLayout.BottomReachable(grid);
                        PerformanceWorkspaceLayout.QuietOperation(grid, delegate { Call(form, "RefreshAutomationPage"); }, "unchanged ROG page refresh"); viewports++;
                    }
                    page.Width = originalWidth; Call(form, "LayoutPerformanceViewport"); grid.AutoScrollPosition = Point.Empty; grid.Reflow(); Application.DoEvents();
                    if (dpi == 96 && language == 0 && !light) PerformanceWorkspaceLayout.Save(form, Path.Combine(output, "performance-rog-category-" + category + ".png"));
                }
                foreach (var value in values) Check(value.Value == PolicyResolver.RawGlobalValue(value.Key), "Navigation changed a preference: " + value.Key);
                categories.Select(0, true); var awake = (Toggle)Named(form, "manualPerformance_" + PolicyCatalog.KeyAwake);
                bool previous = awake.Checked; awake.PerformClick();
                Check(awake.Checked != previous && PolicyResolver.RawGlobalValue(PolicyCatalog.KeyAwake) == (awake.Checked ? "1" : "0"), "Original manual preference save handler no longer runs");
                awake.PerformClick(); Check(awake.Checked == previous, "Original toggle value was not restored in transient settings");
                using (var grid = new PerformanceFeatureGrid { Width = Theme.S(1480), Height = Theme.S(400) })
                {
                    for (int i = 0; i < 7; i++) { var card = new PerformanceFeatureCard { Title = "Wide ROG " + i, Desc = "A complete description remains visible." }; card.Host(new Toggle()); grid.Controls.Add(card); }
                    grid.Complete(); Check(grid.Columns == 3 && grid.Controls[0].Top == grid.Controls[2].Top, "Wide viewport did not use three columns");
                    Check(grid.Controls[6].Width > grid.Controls[0].Width, "Last orphan should span the available row");
                }
            }
        }
        [STAThread] private static int Main(string[] args)
        {
            if (args.Length == 1 && args[0] == "--negative-control") { Console.WriteLine("FAIL: intentional performance ROG negative control"); return 3; }
            return PerformanceWorkspaceLayout.PrivateDesktop(delegate {
                Settings.UseTransientStoreForCurrentProcess(); Logger.ResetWriteBarrierForTest(); Logger.WritesEnabled = false; Dpi.Init(); Lang.Init();
                Directory.CreateDirectory(args[0]);
                bool single = args.Length > 1 && args[1] == "--single";
                foreach (int dpi in single ? new[] { 96 } : new[] { 96, 144, 192 })
                foreach (int language in single ? new[] { 0 } : new[] { 0, 1 }) foreach (bool light in single ? new[] { false } : new[] { false, true }) Run(args[0], dpi, language, light);
                Console.WriteLine("PASS performance ROG assertions=" + (checks + PerformanceWorkspaceLayout.Checks) + " viewports=" + viewports + " settings=transient workers=false native_operations=false private_desktop=true");
            });
        }
    }
}
#endif
