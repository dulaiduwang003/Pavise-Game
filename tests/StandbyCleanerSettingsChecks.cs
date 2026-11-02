#if PAVISE_SELFTEST && PAVISE_STANDBY_SETTINGS_CHECKS
using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

namespace PaviseApp
{
    // Actual settings controls on an isolated desktop; never starts GameMode.
    internal static class StandbyCleanerSettingsChecks
    {
        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private static int checks, layouts, fixtures;
        private static void Check(bool value, string message)
        { if (!value) throw new InvalidOperationException("FAIL: " + message); checks++; }
        private static T Field<T>(object target, string name)
        { return (T)(target is PanelForm ? typeof(PanelForm) : target.GetType()).GetField(name, Instance).GetValue(target); }
        private static object Call(PanelForm form, string name, params object[] args)
        { return typeof(PanelForm).GetMethod(name, Instance).Invoke(form, args); }
        private static void Refresh(PanelForm form) { Call(form, "RefreshStandbyCleanerPresentation"); }
        private static void Safe(PanelForm form, GameMode mode)
        {
            Check(!form.UiActive && !mode.Enabled, "fixture activated runtime");
            Check(Field<object>(mode, "worker") == null, "fixture started GameMode worker");
        }
        private static PanelForm Open(string output, bool admin, Action seed, out GameMode mode)
        {
            PanelForm form = PerformanceWorkspacePageChecks.CreateFixtureForTest(
                Path.Combine(output, "fixture-" + (++fixtures)), out mode, admin, seed);
            form.Location = new Point(-32000, -32000); form.StartPosition = FormStartPosition.Manual;
            form.StandbyCleanerConfirmationForTest = delegate { throw new InvalidOperationException("unexpected confirmation"); };
            form.StandbyCleanerInvalidOptionsForTest = delegate { throw new InvalidOperationException("unexpected invalid warning"); };
            form.StandbyCleanerSaveFailureForTest = delegate { throw new InvalidOperationException("unexpected save warning"); };
            form.StandbyCleanerOptionsEditorForTest = delegate { throw new InvalidOperationException("unexpected options editor"); };
            form.Show(); form.ShowPageForShot(PageId.Settings); Call(form, "StopPageReveal");
            Application.DoEvents(); Safe(form, mode); return form;
        }
        private static void PaintAndCheck(SettingCard card)
        {
            using (var bitmap = new Bitmap(card.Width, card.Height)) card.DrawToBitmap(bitmap, card.ClientRectangle);
            Check(card.DescLinesWanted > 0 && card.DescLinesShown >= card.DescLinesWanted, "settings explanation clipped");
            Check(!card.Collapsible && card.Expanded, "settings explanation can collapse");
            Check(card.ClientRectangle.Contains(card.Hosted.Bounds), "action host outside card");
            foreach (Control action in card.Hosted.Controls)
                Check(action.Visible && card.Hosted.ClientRectangle.Contains(action.Bounds), "settings action hidden or clipped");
        }
        private static void Layout(string output, int dpi, int language, bool light)
        {
            Dpi.NoFit = true; Dpi.Scale = dpi / 96f; Theme.DropFontCache(); Lang.Cur = language; Theme.SetLight(light);
            Console.WriteLine("standby settings layout " + dpi + "/" + language + "/" + light);
            GameMode mode;
            using (PanelForm form = Open(output, true, null, out mode)) {
                var card = Field<SettingCard>(form, "cardPolicyStandbyCleaner");
                var toggle = Field<Toggle>(form, "swPolicyStandbyCleaner");
                var app = Field<DBPanel[]>(form, "settingsTabPanels")[0];
                Check(app.Contains(card) && card.Contains(toggle), "standby missing from settings app tab");
                Check(!Field<DBPanel>(form, "pagePolicy").Contains(card), "standby remains in performance page");
                Check(PerformanceWorkspaceLayout.Descendants(app).Count(c => c.Name == "environmentStandbyCleaner") == 1,
                    "duplicate settings toggle");
                Check(PerformanceWorkspaceLayout.Descendants(app).Count(c => c.Name == "StandbyCleanerOptions") == 1,
                    "duplicate settings options");
                Check(Field<DBPanel>(form, "pagePolicy").Controls.Find("automationState_" + PolicyCatalog.KeyStandbyCleaner, true).Length == 0,
                    "standby advertised as automatic");
                ((ScrollableControl)card.Parent).ScrollControlIntoView(card); Application.DoEvents(); PaintAndCheck(card);
                // Actual Settings page lifecycle must refresh without visiting Performance.
                var hook = Field<Array>(form, "pageHooks").GetValue((int)PageId.Settings);
                Check(mode.TrySetStandbyCleanerEnabled(true), "transient seed enable failed");
                Field<Action>(hook, "OnTick")();
                Check(toggle.Checked, "settings tick did not refresh");
                Check(mode.TrySetStandbyCleanerEnabled(false), "transient seed disable failed");
                Field<Action<bool>>(hook, "OnActiveChanged")(true);
                Check(!toggle.Checked, "settings activation did not refresh");
                var tabs = Field<TechTabs>(form, "settingsTabs"); tabs.Index = 1;
                Check(mode.TrySetStandbyCleanerEnabled(true), "tab seed failed"); tabs.Index = 0;
                Check(toggle.Checked, "settings tab return did not refresh");
                Check(mode.TrySetStandbyCleanerEnabled(false), "tab reset failed"); Refresh(form); Safe(form, mode);
                if (dpi == 96) using (var bitmap = new Bitmap(card.Width, card.Height)) {
                    card.DrawToBitmap(bitmap, card.ClientRectangle);
                    bitmap.Save(Path.Combine(output, "standby-settings-" + language + "-" + light + ".png"));
                }
                layouts++;
            }
        }
        private static void Contracts(string output)
        {
            Dpi.NoFit = true; Dpi.Scale = 1; Theme.DropFontCache(); Lang.Cur = 0; Theme.SetLight(false);
            GameMode mode;
            using (PanelForm form = Open(output, true, null, out mode)) {
                var toggle = Field<Toggle>(form, "swPolicyStandbyCleaner"); int confirms = 0, editors = 0, warnings = 0;
                form.StandbyCleanerConfirmationForTest = delegate { confirms++; return false; };
                toggle.Checked = true;
                Check(confirms == 1 && !toggle.Checked && !mode.StandbyCleanerEnabled, "cancel enabled standby");
                form.StandbyCleanerConfirmationForTest = delegate { confirms++; return true; };
                toggle.Checked = true;
                Check(confirms == 2 && toggle.Checked && mode.StandbyCleanerEnabled
                    && Settings.Load(PolicyCatalog.KeyStandbyCleaner, false), "confirmed preference not saved");
                toggle.Checked = false;
                Check(!mode.StandbyCleanerEnabled && !Settings.Load(PolicyCatalog.KeyStandbyCleaner, true), "disable failed");
                string before = mode.StandbyCleaningOptions.Serialize();
                form.StandbyCleanerOptionsEditorForTest = delegate { editors++; return null; };
                Check(!(bool)Call(form, "EditStandbyCleanerOptions") && editors == 1
                    && mode.StandbyCleaningOptions.Serialize() == before, "cancel changed options");
                Check(!(bool)Call(form, "ApplyStandbyCleanerOptions", new StandbyCleanerOptions(-1, 1, 4000)), "invalid options accepted");
                var next = new StandbyCleanerOptions(2049, 4097, 1250);
                form.StandbyCleanerOptionsEditorForTest = delegate { editors++; return next; };
                Check((bool)Call(form, "EditStandbyCleanerOptions") && mode.StandbyCleaningOptions.Serialize() == next.Serialize()
                    && Settings.LoadStr(GameMode.StandbyCleanerOptionsKey, "") == next.Serialize()
                    && !mode.StandbyCleanerEnabled, "options save enabled or failed to persist");
                foreach (bool busy in new[] { false, true }) {
                    Settings.Save(CalibrationRuntime.RequestedKey, !busy);
                    var busyField = typeof(CalibrationRuntime).GetField("busy", Static); object savedBusy = busyField.GetValue(null);
                    int savedConfirms = confirms, savedEditors = editors;
                    try {
                        busyField.SetValue(null, busy); Refresh(form);
                        Check(!toggle.Enabled && !Field<SettingCard>(form, "cardPolicyStandbyCleaner").Hosted.Enabled,
                            "mode/busy gate lost after migration");
                        Call(form, "OnStandbyCleanerToggle", true);
                        Check(!(bool)Call(form, "EditStandbyCleanerOptions") && confirms == savedConfirms && editors == savedEditors
                            && !mode.StandbyCleanerEnabled, "mode/busy executed edit or confirm");
                    } finally { busyField.SetValue(null, savedBusy); Settings.Save(CalibrationRuntime.RequestedKey, false); Refresh(form); }
                }
                var suspend = typeof(Settings).GetField("writesSuspendedForReset", Static); object oldSuspend = suspend.GetValue(null);
                form.StandbyCleanerSaveFailureForTest = delegate { warnings++; };
                try {
                    suspend.SetValue(null, true); toggle.Checked = true;
                    Check(warnings == 1 && !toggle.Checked && !mode.StandbyCleanerEnabled, "failed save left enabled");
                    Check(!(bool)Call(form, "ApplyStandbyCleanerOptions", StandbyCleanerOptions.Conservative)
                        && mode.StandbyCleaningOptions.Serialize() == next.Serialize()
                        && Settings.LoadStr(GameMode.StandbyCleanerOptionsKey, "") == next.Serialize(), "failed options save published value");
                } finally { suspend.SetValue(null, oldSuspend); }
                Safe(form, mode);
            }
            using (PanelForm form = Open(output, true, delegate { Settings.SaveStr(GameMode.StandbyCleanerOptionsKey, "broken"); }, out mode)) {
                int invalid = 0; form.StandbyCleanerInvalidOptionsForTest = delegate { invalid++; };
                var toggle = Field<Toggle>(form, "swPolicyStandbyCleaner");
                Check(!mode.StandbyCleaningOptionsValid && Field<SettingCard>(form, "cardPolicyStandbyCleaner").Desc == Lang.T("standbycleaner.config.invalid"), "invalid state not shown");
                toggle.Checked = true;
                Check(invalid == 1 && !toggle.Checked && !mode.StandbyCleanerEnabled, "invalid config passed enable");
                Check((bool)Call(form, "ApplyStandbyCleanerOptions", StandbyCleanerOptions.Default)
                    && mode.StandbyCleaningOptionsValid && !mode.StandbyCleanerEnabled, "repair enabled standby");
                PaintAndCheck(Field<SettingCard>(form, "cardPolicyStandbyCleaner")); Safe(form, mode);
            }
            using (PanelForm form = Open(output, false, null, out mode)) {
                var toggle = Field<Toggle>(form, "swPolicyStandbyCleaner");
                Check(!toggle.Enabled, "nonadmin can enable"); toggle.Checked = true;
                Check(!toggle.Checked && !mode.StandbyCleanerEnabled, "nonadmin enable path passed");
                Check(Field<SettingCard>(form, "cardPolicyStandbyCleaner").Desc == Lang.T("gm.standbycleaner.needadmin"), "permission state not shown");
                PaintAndCheck(Field<SettingCard>(form, "cardPolicyStandbyCleaner")); Safe(form, mode);
            }
            using (PanelForm form = Open(output, false, delegate { Settings.Save(PolicyCatalog.KeyStandbyCleaner, true); }, out mode)) {
                var toggle = Field<Toggle>(form, "swPolicyStandbyCleaner");
                Check(toggle.Enabled && toggle.Checked, "nonadmin cannot disable saved opt-in"); toggle.Checked = false;
                Check(!mode.StandbyCleanerEnabled && !toggle.Enabled, "nonadmin opt-out failed"); Safe(form, mode);
            }
        }
        [STAThread] private static int Main(string[] args)
        {
            if (args.Contains("--negative-control")) { Console.Error.WriteLine("FAIL: intentional standby settings negative control"); return 3; }
            return PerformanceWorkspaceLayout.PrivateDesktop(delegate {
                string output = args[0]; Directory.CreateDirectory(output);
                foreach (int dpi in args.Contains("--single") ? new[] { 96 } : new[] { 96, 144, 192 })
                    foreach (int language in new[] { 0, 1 }) foreach (bool light in new[] { false, true }) Layout(output, dpi, language, light);
                Contracts(output);
                Console.WriteLine("PASS standby settings assertions=" + checks + " layouts=" + layouts + " fixtures=" + fixtures);
            });
        }
    }
}
#endif
