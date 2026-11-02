#if PAVISE_SELFTEST && PAVISE_MANUAL_MODE_CHECKS
using System;
using System.IO;
using System.Linq;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;

namespace PaviseApp
{
    internal static partial class SelfTests
    { public static bool TryHandleRuntimeMode(string[] args) { throw new InvalidOperationException("Manual checks cannot start the product runtime"); } }

    internal static class ManualModeChecks
    {
        private static int checks;
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        private static void Check(bool value, string message)
        { if (!value) throw new InvalidOperationException(message); checks++; }
        private static T Field<T>(object owner, string name)
        { return (T)owner.GetType().GetField(name, Hidden).GetValue(owner); }
        private static void Call(object owner, string method)
        { owner.GetType().GetMethod(method, Hidden).Invoke(owner, null); }
        private static void Policies(string directory)
        {
            Paths.Data = directory;
            Settings.UseTransientStoreForCurrentProcess(); Logger.WritesEnabled = false;
            // Old automatic intent never becomes execution authority.
            Settings.Save("AutomaticPolicyV1", true);
            foreach (var item in PolicyCatalog.All) {
                if (PerformancePolicy.IsRetired(item.Key) || PerformancePolicy.IsEnvironment(item.Key)
                    || PerformancePolicy.IsGamePriorityOption(item.Key) || item.Key == PolicyCatalog.KeyPowerPlan) continue;
                foreach (string value in item.Kind == PolicyValueKind.Bool ? new[] { "0", "1" } : item.Choices ?? new[] { item.Fallback }) {
                    string expected = value;
                    Check(PerformancePolicy.Resolve(item.Key, key => expected) == expected, "Manual policy overridden: " + item.Key + "/" + expected);
                }
            }
            foreach (string key in new[] { PolicyCatalog.KeyBoost, PolicyCatalog.KeySuppress, PolicyCatalog.KeyGpuDemote, PolicyCatalog.KeyNvMaxPerf, PolicyCatalog.KeyNvShaderCache }) {
                Check(Settings.Save(key, true), "Preference save failed");
                Check(PolicyResolver.GlobalValue(key) == "1", "Legacy automatic flag suppressed manual switch: " + key);
                Check(Settings.Save(key, false), "Preference save failed");
                Check(PolicyResolver.GlobalValue(key) == "0", "Legacy result overrode manual Off: " + key);
            }
            foreach (string choice in new[] { ThreadBoostSelection.Off, ThreadBoostSelection.On }) {
                Check(ThreadBoostSelection.Save(choice), "Manual thread choice was not saved");
                Check(PolicyResolver.GlobalValue(PolicyCatalog.KeyThreadBoost) == choice, "Manual thread strategy was suppressed");
            }
            // 旧版本保存的两种策略都按开启读取。
            foreach (string legacy in new[] { ThreadBoostSelection.Candidate, ThreadBoostSelection.Frame })
                Check(ThreadBoostSelection.Save(legacy) && PolicyResolver.GlobalValue(PolicyCatalog.KeyThreadBoost) == ThreadBoostSelection.On,
                    "Legacy thread strategy did not read as on: " + legacy);
            Settings.Save(PerformanceActionCatalog.OpenGl, true);
            Check(PerformanceTuning.ActionSelected(PerformanceActionCatalog.OpenGl), "Manual OpenGL feature lost");
            Check(PerformanceTuning.LegacyActionSelected(PerformanceActionCatalog.OpenGl), "Manual OpenGL execution blocked by old intent");
            var profile = new GameProfile { Id = "manual", Name = "Manual fixture" };
            foreach (string key in new[] { PolicyCatalog.KeyGameRebar, PolicyCatalog.KeyGameFullscreenOptimizations, PolicyCatalog.KeySuppressFamily, PolicyCatalog.KeyAutoCores }) {
                Check(PolicyResolver.SetOverride(profile, key, "1"), "Per-game option removed: " + key);
                Check(PolicyResolver.For(profile).ValueOf(key) == "1", "Per-game option ignored: " + key);
            }
            Check(PerformancePolicy.SceneUsesWideBackgroundScope(PerformancePreset.Competitive), "Competitive background contract lost");
            Check(PerformancePolicy.SceneUsesAdaptiveEscalation(PerformancePreset.Standard), "Smart scene contract lost");
            var assembly = typeof(PanelForm).Assembly;
            foreach (string name in new[] { "CalibrationRuntime", "CalibrationCoverage", "CalibrationWorkspaceDialog", "CalibrationJourney", "CalibrationP3Report", "AutomaticPolicy",
                "LegacyCalibrationRecovery", "CalibrationSessionRecovery", "CalibrationRebootEntrypoint", "SpiCalibrationOwnership" })
                Check(assembly.GetType("PaviseApp." + name) == null, "Removed automatic code still compiled: " + name);
            foreach (string name in new[] { "PerformanceMonitoring", "MonitoringPreferences", "MonitoringFrameWindow", "MonitoringHotkey",
                "MonitoringGpuIdentity", "MonitoringNativeSensors", "MonitoringPreview", "MonitoringDesktopPreview", "MonitoringSelector",
                "RtssComponent", "RtssOverlayClient", "RtssStartup", "RtssTrayIcon", "RtssHardwareSensors", "RtssHardwareProvider" })
                Check(assembly.GetType("PaviseApp." + name) == null, "Retired monitoring code still compiled: " + name);
            foreach (string key in new[] { "PerfMonitorEnabledV1", "PerfMonitorLayoutV1", "PerfMonitorMetricsV1",
                "PerfMonitorIntervalMsV1", "PerfMonitorAppearanceV1", "PerfMonitorHotkeyV1" })
                Check(AppDataReset.IsUserPreference(key), "Retired monitoring preference cannot be reset: " + key);
            Check(!AppDataReset.IsUserPreference(Settings.GpuIdentityReceiptKey), "User-data reset discards legacy ETW recovery receipt");
        }
        private static void Ui(string directory)
        {
            PanelForm.SuppressUiWorkersForTest = true;
            // Previously enabled monitoring must not restore its UI or collectors after an upgrade.
            Settings.Save("PerfMonitorEnabledV1", true);
            Settings.SaveStr("PerfMonitorHotkeyV1", ((int)(Keys.Control | Keys.F10)).ToString());
            Directory.CreateDirectory(directory);
            var core = new SuppressionCore();
            var mode = new GameMode(directory, core);
            mode.InitializeAutomation();
            Check(!mode.Enabled, "UI fixture must not enable game processing");
            Check(!Settings.LoadCached("AutomaticPolicyV1", false), "Retired automatic intent was not migrated");
            using (var icon = IconArt.MakeIcon(24))
            using (var form = new PanelForm(new Tamer(core), mode, icon, false)) {
                form.ShowInTaskbar = false; form.Location = new Point(-32000, -32000); form.StartPosition = FormStartPosition.Manual;
                form.Show(); form.ShowPageForShot(PageId.Policy); Call(form, "StopPageReveal");
                Check(!form.UiActive && !mode.Enabled, "UI check started runtime workers");
                foreach (string name in new[] { "ControlModePicker", "OpenPerformanceEvidence", "CalibrationStart", "CalibrationJourney", "PerformanceLegacyRecovery", "PerformanceWorkspaceSummary" })
                    Check(form.Controls.Find(name, true).Length == 0, "Automatic UI entry survived: " + name);
                foreach (string key in new[] { PolicyCatalog.KeyBoost, PolicyCatalog.KeySuppress, PolicyCatalog.KeyGpuDemote,
                    PolicyCatalog.KeyThreadBoost, PolicyCatalog.KeyDwmBoost, PolicyCatalog.KeyAwake, PolicyCatalog.KeyPauseDl,
                    PerformanceActionCatalog.OpenGl, PerformanceActionCatalog.Mitigations, PerformanceActionCatalog.ShortNames })
                    Check(form.Controls.Find("manualPerformance_" + key, true).Length == 1, "Manual optimization control missing: " + key);
                Check(form.Controls.Find("manualPerformance_" + PerformanceActionCatalog.BackgroundFps, true).Length == 0, "Removed background rendering cap control survived");
                Check(form.Controls.Find("StrategyScenePicker", true).Length == 1, "Scene selector removed");
                Check(form.Controls.Find("StrategyPowerPicker", true).Length == 1, "Match power selector removed");
                form.ShowPageForShot(PageId.Settings); Call(form, "StopPageReveal");
                Check(Field<DBPanel[]>(form, "settingsTabPanels").Length == 3, "Retired monitoring settings tab survived");
                foreach (string name in new[] { "monitoringEnabled", "monitoringGpuAdapter", "monitoringMessage", "monitoringHotkey" })
                    Check(form.Controls.Find(name, true).Length == 0, "Retired monitoring control survived: " + name);
                foreach (int width in new[] { 360, 452 }) {
                    var flyout = Field<StrategyFlyout>(form, "modeFlyout"); flyout.FitViewport(new Size(Theme.S(width), Theme.S(800)));
                    foreach (Control child in flyout.Controls) Check(flyout.ClientRectangle.Contains(child.Bounds), "Strategy choice clipped");
                }
                Check(PanelForm.PerformanceRestartFor("Hags") == PerformanceRestartScope.SystemRestart, "System restart metadata changed");
                Check(PanelForm.PerformanceRestartFor(PolicyCatalog.KeyGameFullscreenOptimizations) == PerformanceRestartScope.ProcessRestart, "Per-game activation metadata changed");
                Check(PanelForm.PerformanceRestartFor(PolicyCatalog.KeyBoost) == PerformanceRestartScope.None, "Live manual option mislabeled");
            }
        }
        [STAThread]
        private static int Main(string[] args)
        {
            return PerformanceWorkspaceLayout.PrivateDesktop(delegate {
                Directory.CreateDirectory(args[0]);
                Settings.UseTransientStoreForCurrentProcess(); Logger.WritesEnabled = false;
                Dpi.Init(); Lang.Init(); Dpi.NoFit = true; Dpi.Scale = 1;
                Policies(args[0]); Ui(Path.Combine(args[0], "ui"));
                Console.WriteLine("PASS manual-only checks=" + checks + "; transient settings, private desktop, no product/hardware workload");
            });
        }
    }
}
#endif
