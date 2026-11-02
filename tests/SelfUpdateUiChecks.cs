#if SELF_UPDATE_UI_FIXTURE
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace PaviseApp
{
    // This runner compiles the production dialog/gate with inert dependency adapters.
    // It never enters Program.Main, touches settings/tasks, performs HTTP, or displays on-screen windows.
    internal static class SelfUpdateUiChecks
    {
        private static int checks;
        private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks++; }
        private static void Pump(Func<bool> done)
        {
            Stopwatch watch = Stopwatch.StartNew();
            while (!done() && watch.ElapsedMilliseconds < 6000) { Application.DoEvents(); Thread.Sleep(8); }
            Check(done(), "Timed out waiting for the fixture UI worker");
        }
        private static Control Find(Control parent, string name) { return parent.Controls.Find(name, true)[0]; }
        private static void Click(Control parent, string name) { ((PillButton)Find(parent, name)).PerformClick(); }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateDesktop(string name, IntPtr device, IntPtr mode, uint flags, uint access, IntPtr security);
        [DllImport("user32.dll")] private static extern bool SetThreadDesktop(IntPtr desktop);
        [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);

        private static void CheckPositioning()
        {
            Size size = new Size(650, 478);
            Check(UpdateDialog.CenteredLocation(new Rectangle(300, 140, 1200, 800), size, new Rectangle(0, 0, 1920, 1040)) == new Point(575, 301),
                "Dialog geometry does not center on its owner");
            Check(UpdateDialog.CenteredLocation(new Rectangle(-1800, 80, 1200, 800), size, new Rectangle(-1920, 0, 1920, 1040)) == new Point(-1525, 241),
                "Dialog geometry lost a negative-coordinate secondary monitor");
            Check(UpdateDialog.CenteredLocation(new Rectangle(1800, 900, 800, 600), size, new Rectangle(0, 0, 1920, 1040)) == new Point(1270, 562),
                "A partly off-screen owner placed the dialog outside the working area");
            Check(UpdateDialog.CenteredLocation(new Rectangle(-600, -300, 800, 600), size, new Rectangle(0, 0, 1920, 1040)) == Point.Empty,
                "An off-screen owner left the dialog above or left of the monitor");
            foreach (float scale in new[] { 1f, 1.5f, 2f })
            using (Form owner = new Form { StartPosition = FormStartPosition.Manual, ShowInTaskbar = false })
            {
                Theme.Scale = scale;
                Rectangle area = Screen.PrimaryScreen.WorkingArea;
                owner.Size = new Size(Math.Min(1220, area.Width), Math.Min(760, area.Height));
                foreach (Point location in new[] { new Point(area.Left + 30, area.Top + 25), new Point(area.Right - owner.Width - 50, area.Bottom - owner.Height - 35) })
                {
                    owner.Location = location;
                    owner.Show();
                    using (UpdateDialog dialog = UpdateDialog.ShowFor(owner, new UpdateResult { Ok = true, Latest = "3.2.0.3", Newer = true }, null))
                    {
                        dialog.ShowInTaskbar = false;
                        Application.DoEvents();
                        Point expected = new Point(owner.Left + (owner.Width - dialog.Width) / 2, owner.Top + (owner.Height - dialog.Height) / 2);
                        expected.X = Math.Max(area.Left, Math.Min(expected.X, area.Right - dialog.Width));
                        expected.Y = Math.Max(area.Top, Math.Min(expected.Y, area.Bottom - dialog.Height));
                        Check(dialog.Owner == owner && dialog.Location == expected,
                            "Real modeless ShowFor did not center after owner relocation at scale=" + scale);
                        Console.WriteLine("CENTER scale=" + scale + " owner=" + owner.Bounds + " dialog=" + dialog.Bounds);
                        Rectangle previous = dialog.Bounds;
                        dialog.ShowManual("Fixture manual download failure details.\r\n" + new string('W', 900));
                        Application.DoEvents();
                        Check(dialog.Location == UpdateDialog.CenteredLocation(previous, dialog.Size, area),
                            "Resizing after failure lost the dialog's center at scale=" + scale);
                    }
                }
                using (UpdateDialog dialog = UpdateDialog.ShowStartupFailure(owner, "Fixture startup recovery"))
                {
                    dialog.ShowInTaskbar = false;
                    Application.DoEvents();
                    Check(dialog.Location == UpdateDialog.CenteredLocation(owner.Bounds, dialog.Size, area),
                        "Startup recovery dialog did not center on its owner at scale=" + scale);
                }
            }
            Theme.Scale = 1f;
        }
        private static UpdateDialog New(Form owner, Action<UpdateDialog, string, SelfUpdatePackage> install)
        {
            UpdateDialog dialog = new UpdateDialog(new UpdateResult { Ok = true, Latest = "3.2.0.3", Newer = true }, install);
            dialog.ShowInTaskbar = false;
            dialog.StartPosition = FormStartPosition.Manual;
            dialog.Location = new Point(-30000, -30000);
            dialog.Show(owner);
            Application.DoEvents();
            return dialog;
        }

        [STAThread]
        internal static int Main()
        {
            IntPtr desktop = CreateDesktop("PaviseUpdateUi-" + Guid.NewGuid().ToString("N"), IntPtr.Zero, IntPtr.Zero, 0, 0x01ff, IntPtr.Zero);
            if (desktop == IntPtr.Zero) return 2;
            int result = 1;
            try
            {
                Thread worker = new Thread(delegate() { result = Run(desktop); Console.Out.Flush(); Console.Error.Flush(); Environment.Exit(result); });
                worker.SetApartmentState(ApartmentState.STA);
                worker.Start(); worker.Join();
                return result;
            }
            finally { CloseDesktop(desktop); }
        }

        private static int Run(IntPtr desktop)
        {
            try
            {
                if (desktop == IntPtr.Zero || !SetThreadDesktop(desktop)) throw new Exception("Cannot isolate update UI tests on a private desktop");
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                CheckPositioning();
                SelfUpdateGate.End();
                Check(SelfUpdateGate.TryBegin() && !SelfUpdateGate.TryBegin(), "The update gate must reject overlapping owners");
                SelfUpdateGate.End();
                using (Form owner = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-30000, -30000), ShowInTaskbar = false })
                {
                    owner.Show();
                    SelfUpdateDownload.Mode = "block";
                    int installs = 0;
                    using (UpdateDialog dialog = New(owner, delegate { installs++; }))
                    {
                        Click(dialog, "UpdateStart");
                        Pump(delegate { return Find(dialog, "UpdateProgressTitle").Text.Contains("68%"); });
                        Check(SelfUpdateGate.IsBusy && !owner.Enabled, "Download disables the owner and claims the exit gate");
                        Check(!Find(dialog, "UpdateClose").Enabled, "Download disables the close button");
                        dialog.Close(); Application.DoEvents();
                        Check(!dialog.IsDisposed, "Alt+F4/close may not terminate an active update");
                        Click(dialog, "UpdateCancel");
                        Pump(delegate { return !SelfUpdateGate.IsBusy; });
                        Check(owner.Enabled && installs == 0, "Cancellation restores owner interaction and does not install");
                        Check(Find(dialog, "UpdateSummary").Text.Contains("取消"), "Cancellation reports that the original program is unchanged");
                    }
                    SelfUpdateDownload.Mode = "fail";
                    using (UpdateDialog dialog = New(owner, delegate { installs++; }))
                    {
                        Click(dialog, "UpdateStart");
                        Pump(delegate { return Find(dialog, "UpdateTitle").Text == "手动更新"; });
                        Check(!SelfUpdateGate.IsBusy && owner.Enabled, "Failure releases closing and owner controls");
                        Check(Find(dialog, "UpdateQuark").Visible && Find(dialog, "UpdateGitHub").Visible, "The same failed dialog exposes both manual download paths");
                        Check(Find(dialog, "UpdateQuark").Text.Contains("入口"), "The Quark entry must not pretend to be a direct share link");
                        Check(Find(dialog, "UpdateFailureReason").Text.Contains("Fixture network unavailable"), "The actual failure reason remains reviewable");
                    }
                    using (UpdateDialog dialog = New(owner, null))
                    {
                        dialog.ShowManual("Fixture orphaned update recovery");
                        Check(!Find(dialog, "UpdateStart").Visible, "Recovery without an installer callback must not offer automatic download or retry");
                        Check(Find(dialog, "UpdateQuark").Visible && Find(dialog, "UpdateGitHub").Visible, "Orphaned recovery retains both manual download choices");
                    }
                    SelfUpdateDownload.Mode = "success";
                    using (UpdateDialog dialog = New(owner, delegate { installs++; }))
                    {
                        int previous = installs;
                        Click(dialog, "UpdateStart");
                        // Allow the worker to finish and dispose its CTS without pumping its posted completion.
                        Thread.Sleep(200);
                        Click(dialog, "UpdateCancel");
                        Pump(delegate { return !SelfUpdateGate.IsBusy; });
                        Check(installs == previous, "Cancellation still wins after the worker finishes but before UI handoff");
                    }
                    SelfUpdateDownload.Mode = "success";
                    using (UpdateDialog dialog = New(owner, delegate(UpdateDialog d, string path, SelfUpdatePackage package) { installs++; }))
                    {
                        Click(dialog, "UpdateStart");
                        Pump(delegate { return dialog.IsInstalling; });
                        Check(SelfUpdateGate.IsBusy && !Find(dialog, "UpdateCancel").Enabled, "Installation locks closing and cancellation");
                        dialog.Close();
                        Check(!dialog.IsDisposed, "Installation blocks close requests");
                        dialog.ShowStoppedManual("Fixture safe shutdown incomplete");
                        Check(!SelfUpdateGate.IsBusy && Find(dialog, "UpdateTitle").Text == "手动更新", "Safe-shutdown failure becomes manual update");
                        Check(!Find(dialog, "UpdateStart").Visible, "A stopped runtime cannot retry installing in-process");
                        Check(Find(dialog, "UpdateCancel").Text == "退出 Pavise", "A stopped runtime offers exit instead of claiming it can continue");
                    }
                    foreach (int language in new[] { 0, 1 })
                    foreach (float scale in new[] { 1f, 1.5f, 2f })
                    {
                        Lang.Cur = language; Theme.Scale = scale;
                        using (UpdateDialog dialog = New(owner, delegate { installs++; }))
                        {
                            dialog.ShowManual(language == 0 ? "连接超时。部分网络环境可能无法访问更新服务。" : "Connection timed out. This network may not reach the update service.");
                            foreach (Control control in dialog.Controls)
                                if (control.Visible) Check(control.Right <= dialog.ClientSize.Width && control.Bottom <= dialog.ClientSize.Height, "Dialog content must fit at language=" + language + " scale=" + scale + " control=" + control.Name);
                            using (Bitmap image = new Bitmap(dialog.Width, dialog.Height)) dialog.DrawToBitmap(image, new Rectangle(0, 0, image.Width, image.Height));
                        }
                    }
                }
                Check(!SelfUpdateGate.IsBusy, "Every completed fixture releases the update gate");
                Console.WriteLine("PASS " + checks + " update UI checks; private_desktop=true; no product startup, network, task or settings access");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
    }

    internal sealed class SelfUpdatePackage { internal string Version = "3.2.0.3", Sha256, ManifestText; internal long Size; internal string[] DownloadUrls; }
    internal sealed class SelfUpdateProgress { internal string Stage; internal long Received, Total; }
    internal static class SelfUpdateDownload
    {
        internal static string Mode;
        internal static SelfUpdatePackage Check(CancellationToken token) { token.ThrowIfCancellationRequested(); if (Mode == "fail") throw new IOException("Fixture network unavailable"); return new SelfUpdatePackage(); }
        internal static string Download(SelfUpdatePackage package, string directory, Action<SelfUpdateProgress> progress, CancellationToken token)
        {
            progress(new SelfUpdateProgress { Stage = "Downloading", Received = 68, Total = 100 });
            if (Mode == "block") { while (true) { token.ThrowIfCancellationRequested(); Thread.Sleep(10); } }
            progress(new SelfUpdateProgress { Stage = "Verifying", Received = 100, Total = 100 });
            return Path.Combine(directory, "candidate.exe");
        }
    }
    internal static class SelfUpdateInstaller { internal static string CreateStagingDirectory() { return "fixture-only"; } internal static void CleanupStagingDirectory(string directory) { } }
    internal sealed class UpdateResult { internal bool Ok, Newer; internal string Latest; }
    internal static class UpdateChecker { internal static bool IsNewer(string remote, string local) { return new Version(remote) > new Version(local); } }
    internal static class App { internal const string Version = "3.2.0.2", ManualUpdateQuarkUrl = "https://pavise.club/changelog/#latest", ManualUpdateGithubUrl = "https://github.com/dulaiduwang003/Pavise-Game/releases/latest"; }
    internal static class Lang { internal static int Cur; }
    internal static class PerformancePolicy { internal static string Text(string zh, string en) { return Lang.Cur == 0 ? zh : en; } }
    internal static class Theme
    {
        internal static float Scale = 1f;
        internal static readonly Color Bg = Color.FromArgb(16, 18, 22), Fg = Color.White, Dim = Color.LightGray, Card = Color.FromArgb(28, 30, 34), Inset = Color.FromArgb(9, 11, 14), Warning = Color.Goldenrod, Stroke = Color.DimGray, Accent = Color.SeaGreen;
        internal static int S(int value) { return (int)Math.Round(value * Scale); }
        internal static Font UI(float size, bool bold) { return new Font("Segoe UI", size * Scale, bold ? FontStyle.Bold : FontStyle.Regular); }
    }
    internal enum BtnKind { Normal, Primary }
    internal sealed class PillButton : Button { internal BtnKind Kind; internal Color Bg; internal PillButton(string text, BtnKind kind) { Text = text; Kind = kind; } }
    internal static class Native { internal static void Dark(Control control) { } internal static void ReleaseCapture() { } internal static IntPtr SendMessage(IntPtr hwnd, int message, IntPtr w, IntPtr l) { return IntPtr.Zero; } }
}
#endif
