#if PAVISE_FAMILY_WARNING_BENCH && PAVISE_SELFTEST
using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace PaviseApp
{
    internal static class FamilyWarningUiChecks
    {
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        private static int checks;
        private static void Check(bool value, string message)
        { if (!value) throw new Exception(message); checks++; }

        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length != 1 || Path.GetFullPath(args[0]).TrimEnd('\\')
                != AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\')) return 2;
            Settings.UseTransientStoreForCurrentProcess();
            PanelForm.SuppressUiWorkersForTest = true;
            Paths.Data = Path.Combine(args[0], "fixture");
            Directory.CreateDirectory(Paths.Data);
            Logger.LogPath = Path.Combine(args[0], "fixture.log");
            Dpi.Init(); Lang.Init();
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                CheckWarningLayout(args[0]);
                int layoutChecks = checks;
                Lang.Cur = 0;
                foreach (string scenario in new[] { "pending", "observed", "lol-observed" })
                    RunScenario(args[0], scenario);
                Console.WriteLine("PASS family warning assertions=" + checks + " layout assertions=" + layoutChecks
                    + " settings=transient runtime_started=false");
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }

        private static void CheckWarningLayout(string output)
        {
            float scaleBefore = Dpi.Scale;
            foreach (float scale in new[] { 1f, 1.25f, 1.5f, 2f, 3f })
            foreach (int language in new[] { 0, 1 })
            foreach (bool light in new[] { false, true })
            {
                Dpi.Scale = scale; Theme.DropFontCache(); Lang.Cur = language; Theme.SetLight(light);
                using (var warning = new FamilySuppressionDialog("Example", @"D:\Example\Game.exe"))
                {
                    string body = Lang.T("lib.family.warning.body");
                    Check(body.Contains("League of Legends.exe") && body.Contains("Steam.exe")
                        && body.Contains(language == 0 ? "每次开启" : "every time"), "Incomplete risk wording");
                    Check(warning.Text.Contains(language == 0 ? "不建议开启" : "not recommended"), "Missing warning title");
                    Check(!body.Contains(language == 0 ? "暂未确认" : "has not confirmed"), "Incorrect renderer status claim");
                    foreach (Control control in warning.Controls)
                    {
                        Check(warning.ClientRectangle.Contains(control.Bounds), "Warning control outside dialog");
                        if (control is Label && (control.Text == body || control.Text == warning.Text))
                        {
                            int needed = TextRenderer.MeasureText(control.Text, control.Font,
                                new Size(control.Width, int.MaxValue), TextFormatFlags.WordBreak).Height;
                            Check(needed <= control.Height, "Warning text clipped: " + control.Text);
                        }
                    }
                    Check(warning.AcceptButton == warning.CancelButton
                        && warning.AcceptButton.DialogResult == DialogResult.Cancel, "Default action must keep off");
                    Check(warning.EnableAnyway.DialogResult == DialogResult.OK, "Missing explicit opt-in");
                    if (scale == 1f)
                    {
                        warning.StartPosition = FormStartPosition.Manual;
                        warning.Location = new Point(-20000, -20000); warning.Show();
                        using (var bitmap = new Bitmap(warning.Width, warning.Height))
                        {
                            warning.DrawToBitmap(bitmap, warning.ClientRectangle);
                            bitmap.Save(Path.Combine(output, "warning-" + language + "-" + light + ".png"));
                        }
                        warning.Hide();
                    }
                }
            }
            Dpi.Scale = scaleBefore; Theme.DropFontCache();
        }

        private static void RunScenario(string output, string scenario)
        {
            string directory = Path.Combine(output, scenario);
            Directory.CreateDirectory(directory);
            string exe = Path.Combine(directory, "Example.exe");
            File.Copy(Application.ExecutablePath, exe);
            var core = new SuppressionCore();
            var tamer = new Tamer(core);
            var mode = new GameMode(directory, core);
            mode.AddGameExecutable(scenario == "lol-observed" ? "英雄联盟" : "Example", exe);
            var profile = mode.GetProfiles()[0];
            bool observed = scenario != "pending";
            if (observed)
            {
                var store = (RendererObservationStore)typeof(GameMode).GetField("rendererObservations", Hidden).GetValue(mode);
                Check(store.RecordActivity(profile.Id, exe, RendererObservationEvidence.Gpu3D,
                    GpuEvidence.MinElectUtilization, RendererFileStamp.Read(exe)), "Cannot seed renderer observation");
            }
            Check(mode.HasRendererObservation(profile) == observed, "Incorrect observation fixture");
            using (var icon = IconArt.MakeIcon(24))
            using (var form = new PanelForm(tamer, mode, icon, true))
            {
                form.ShowInTaskbar = false; form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-20000, -20000); form.Show();
                Check(!form.UiActive, "UI workers unexpectedly started");
                Toggle(form, mode, true, false); // Cancel must keep protection
                Toggle(form, mode, true, true);
                Toggle(form, mode, false, false); // Turning off must not prompt
                Toggle(form, mode, true, true); // Prior acceptance must not skip confirmation
                Toggle(form, mode, false, false);
                Toggle(form, mode, true, false); // Cancellation still works after earlier acceptance
                Check(mode.HasRendererObservation(mode.GetProfiles()[0]) == observed, "Toggle changed renderer badge");
                Check(typeof(Tamer).GetField("worker", Hidden).GetValue(tamer) == null,
                    "Background worker unexpectedly started");
                form.Hide();
            }
        }

        private static void Toggle(PanelForm form, GameMode mode, bool expectPrompt, bool accept)
        {
            int prompts = 0;
            GameProfile before = mode.GetProfiles()[0];
            using (var timer = new Timer { Interval = 25 })
            {
                timer.Tick += delegate
                {
                    FamilySuppressionDialog warning = null;
                    foreach (Form open in Application.OpenForms)
                        if (open is FamilySuppressionDialog) { warning = (FamilySuppressionDialog)open; break; }
                    if (warning == null) return;
                    prompts++;
                    warning.Location = new Point(-20000, -20000);
                    if (accept) warning.EnableAnyway.PerformClick();
                    else ((Button)warning.CancelButton).PerformClick();
                };
                timer.Start();
                typeof(PanelForm).GetMethod("ToggleGameFamilySuppression", Hidden).Invoke(form,
                    new object[] { new GameLibraryItem(before, false, mode.HasRendererObservation(before)) });
                timer.Stop();
            }
            Check(prompts == (expectPrompt ? 1 : 0), "Wrong confirmation count");
            bool expected = expectPrompt ? accept : false;
            Check(mode.GetProfiles()[0].SuppressFamilyBackground == expected, "Incorrect saved family policy");
            Check(new GameProfileStore(Path.GetDirectoryName(before.ExecutablePath)).LoadProfiles()[0]
                .SuppressFamilyBackground == expected, "Saved library differs from displayed policy");
        }
    }

    internal static partial class SelfTests
    {
        public static bool TryHandleRuntimeMode(string[] args)
        { throw new InvalidOperationException("UI bench cannot start the application runtime"); }
    }
}
#endif
