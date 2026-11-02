#if PAVISE_SELFTEST && PAVISE_WEGAME_CARD_UI
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace PaviseApp
{
    internal static class WeGameCardUiChecks
    {
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        private static int assertions;
        private static int renders;

        private static void Check(bool value, string message)
        {
            assertions++;
            if (!value) throw new InvalidOperationException(message);
        }

        private static T Field<T>(object instance, string name)
        {
            return (T)instance.GetType().GetField(name, Hidden).GetValue(instance);
        }

        private static void Set(object instance, string name, object value)
        {
            instance.GetType().GetField(name, Hidden).SetValue(instance, value);
        }

        private static void Refresh(WeGameCardPanel panel)
        {
            typeof(WeGameCardPanel).GetMethod("RefreshState", Hidden).Invoke(panel, null);
            panel.PerformLayout();
        }

        private static int Subscriptions(WeGameShellService service)
        {
            Action changed = Field<Action>(service, "Changed");
            return changed == null ? 0 : changed.GetInvocationList().Length;
        }

        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length != 1 || !string.Equals(Path.GetFullPath(args[0]).TrimEnd('\\'),
                AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return 2;
            Settings.UseTransientStoreForCurrentProcess();
            Logger.WritesEnabled = false;
            Lang.Init();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                foreach (int dpi in new[] { 96, 144, 192 })
                foreach (int language in new[] { 0, 1 })
                foreach (int width in new[] { 280, 400, 720 })
                    CheckCard(args[0], dpi, language, width);
                Console.WriteLine("PASS WeGame card UI assertions=" + assertions + " renders=" + renders
                    + " settings=transient service_started=false confirmation_controls=0 native_process_actions=false");
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }

        private static void CheckCard(string output, int dpi, int language, int logicalWidth)
        {
            Dpi.Scale = dpi / 96f;
            Theme.DropFontCache();
            Theme.SetLight(false);
            Lang.Cur = language;
            string scenario = "dpi" + dpi + "-lang" + language + "-w" + logicalWidth;
            GameProfile profile = new GameProfile
            {
                Id = "wegame-ui-fixture", Name = "UI fixture", Root = @"C:\WeGameApps\Fixture",
                ExecutablePath = @"C:\WeGameApps\Fixture\Game.exe"
            };
            using (WeGameShellService service = new WeGameShellService())
            using (OffscreenHost host = new OffscreenHost())
            {
                Check(Subscriptions(service) == 0, scenario + ": unexpected original subscriber");
                WeGameCardPanel panel = new WeGameCardPanel(service, profile);
                host.Controls.Add(panel);
                int width = Theme.S(logicalWidth);
                panel.SetBounds(0, 0, width, panel.PreferredHeightForWidth(width));
                host.ClientSize = panel.Size;
                host.Show();
                Application.DoEvents();
                Refresh(panel);
                System.Windows.Forms.Timer timer = Field<System.Windows.Forms.Timer>(panel, "tick");
                Check(panel.Visible && timer.Enabled, scenario + ": visible polling did not start");
                Check(Subscriptions(service) == 1, scenario + ": missing service subscription");
                Check(panel.Controls.Count == 0 && !panel.TabStop, scenario + ": automatic status card exposes an action control");
                Check(panel.AccessibleName == (language == 0 ? "WeGame 自动脱壳" : "Automatic WeGame cleanup"), scenario + ": automatic title mismatch");
                Check(panel.AccessibleDescription.Contains(language == 0 ? "已观测的渲染进程" : "observed renderer"), scenario + ": missing observed-renderer instruction");
                Check(!panel.AccessibleDescription.Contains(language == 0 ? "确认" : "confirm"), scenario + ": retired confirmation instruction remains");
                CheckLayout(panel, scenario);
                Render(panel, Path.Combine(output, scenario + "-waiting.png"));

                // Only seed display fields. The service never starts, and no
                // process inspection or cleanup entry point is invoked.
                Set(service, "profile", profile.Clone());
                Set(service, "rendererPid", 1234567);
                Set(service, "rendererCreation", 987654321L);
                Set(service, "generation", 42);
                Set(service, "cleaning", true);
                Set(service, "status", language == 0 ? "正在退出 WeGame 界面" : "Closing the WeGame interface");
                Refresh(panel);
                Check(Field<WeGameShellSnapshot>(panel, "last").Busy, scenario + ": executing state missing");
                Check(Field<WeGameShellSnapshot>(panel, "last").RendererPid == 1234567, scenario + ": wrong displayed session identity");
                Check(Field<WeGameShellSnapshot>(panel, "last").Generation == 42, scenario + ": wrong displayed session generation");
                CheckLayout(panel, scenario + ": busy");
                Render(panel, Path.Combine(output, scenario + "-busy.png"));

                Set(service, "cleaning", false);
                Set(service, "detached", true);
                Set(service, "status", language == 0 ? "WeGame 界面已关闭，登录与运行组件保留" : "WeGame interface closed; login and runtime components are kept");
                Refresh(panel);
                Check(Field<WeGameShellSnapshot>(panel, "last").Detached && !Field<WeGameShellSnapshot>(panel, "last").Busy, scenario + ": completion state mismatch");
                CheckLayout(panel, scenario + ": detached");
                Render(panel, Path.Combine(output, scenario + "-detached.png"));

                Set(service, "detached", false);
                Set(service, "error", language == 0 ? "WeGame 脱壳失败，本局停止" : "WeGame cleanup failed; stopped for this game session");
                Refresh(panel);
                Check(panel.AccessibleDescription.Contains(language == 0 ? "本局停止" : "stopped for this game session"), scenario + ": error omitted from accessible description");
                CheckLayout(panel, scenario + ": error");
                Render(panel, Path.Combine(output, scenario + "-error.png"));

                // Exercise the event marshal without starting the worker or
                // invoking any process inspection/cleanup entry point.
                Exception callbackError = null;
                Thread callback = new Thread(delegate()
                {
                    try { typeof(WeGameShellService).GetMethod("RaiseChanged", Hidden).Invoke(service, null); }
                    catch (Exception error) { callbackError = error; }
                });
                callback.Start();
                Check(callback.Join(2000), scenario + ": callback thread did not finish");
                Application.DoEvents();
                Check(callbackError == null, scenario + ": event callback failed");
                Check(Field<int>(panel, "refreshQueued") == 0, scenario + ": refresh remained queued");
                Check(Field<Thread>(service, "worker") == null && !Field<bool>(service, "running"), scenario + ": service worker unexpectedly started");

                host.Hide();
                Application.DoEvents();
                Check(!panel.Visible && !timer.Enabled, scenario + ": hidden card keeps polling: visible=" + panel.Visible + ", timer=" + timer.Enabled);
                host.Show();
                Application.DoEvents();
                Check(panel.Visible && timer.Enabled, scenario + ": shown card did not resume polling");
                panel.Dispose();
                Check(Subscriptions(service) == 0, scenario + ": disposed card retained subscriber");
                typeof(WeGameShellService).GetMethod("RaiseChanged", Hidden).Invoke(service, null);
                Application.DoEvents();
                Check(panel.IsDisposed, scenario + ": card not disposed");
                Check(Field<Thread>(service, "worker") == null, scenario + ": dispose started service worker");
                host.Hide();
            }
        }

        private static Rectangle Bounds(WeGameCardPanel panel, string method)
        {
            return (Rectangle)typeof(WeGameCardPanel).GetMethod(method, Hidden).Invoke(panel, new object[] { panel.Width });
        }

        private static void CheckLayout(WeGameCardPanel panel, string scenario)
        {
            Check(panel.Controls.Count == 0, scenario + ": automatic status card exposes controls");
            Check(panel.Height == panel.PreferredHeightForWidth(panel.Width), scenario + ": preferred height mismatch");
            Rectangle titleBounds = Bounds(panel, "TitleBoundsForWidth");
            Rectangle descriptionBounds = Bounds(panel, "DescriptionBoundsForWidth");
            Rectangle statusBounds = Bounds(panel, "StatusBoundsForWidth");
            Check(panel.ClientRectangle.Contains(titleBounds) && panel.ClientRectangle.Contains(descriptionBounds) && panel.ClientRectangle.Contains(statusBounds), scenario + ": text outside card");
            Check(titleBounds.Bottom < descriptionBounds.Top && descriptionBounds.Bottom < statusBounds.Top, scenario + ": text regions overlap");
            string title = (string)typeof(WeGameCardPanel).GetMethod("TitleText", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
            string description = (string)typeof(WeGameCardPanel).GetMethod("DescriptionText", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
            Size titleSize = TextRenderer.MeasureText(title, Theme.UI(8.6f, true), Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            Check(titleSize.Width <= titleBounds.Width && titleSize.Height <= titleBounds.Height, scenario + ": title clipped");
            int descriptionHeight = TextRenderer.MeasureText(description, Theme.UI(8.2f, false), new Size(descriptionBounds.Width, int.MaxValue), TextFormatFlags.NoPadding | TextFormatFlags.WordBreak).Height;
            Check(descriptionHeight <= descriptionBounds.Height, scenario + ": instructions clipped");
            WeGameShellSnapshot snapshot = Field<WeGameShellSnapshot>(panel, "last");
            string status = string.IsNullOrEmpty(snapshot.Error) ? snapshot.Status : snapshot.Error;
            int statusHeight = TextRenderer.MeasureText(status ?? "", Theme.UI(7.8f, false), new Size(statusBounds.Width, int.MaxValue), TextFormatFlags.NoPadding | TextFormatFlags.WordBreak).Height;
            Check(statusHeight <= statusBounds.Height, scenario + ": status clipped");
        }

        private static void Render(Control control, string path)
        {
            using (Bitmap bitmap = new Bitmap(control.Width, control.Height))
            {
                control.DrawToBitmap(bitmap, control.ClientRectangle);
                bitmap.Save(path, ImageFormat.Png);
            }
            renders++;
        }

        private sealed class OffscreenHost : Form
        {
            internal OffscreenHost()
            {
                AutoScaleMode = AutoScaleMode.None;
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.Manual;
                Location = new Point(-32000, -32000);
            }
            protected override bool ShowWithoutActivation { get { return true; } }
            protected override CreateParams CreateParams
            {
                get
                {
                    CreateParams parameters = base.CreateParams;
                    parameters.ExStyle |= 0x08000080; // NOACTIVATE | TOOLWINDOW
                    return parameters;
                }
            }
        }
    }
}
#endif
