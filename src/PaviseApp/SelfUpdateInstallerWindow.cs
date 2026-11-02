using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace PaviseApp
{
    internal sealed class SelfUpdateInstallerWindow : Form
    {
        private readonly Label heading, detail, status, warning;
        private readonly ProgressBar progress;
        private readonly FlowLayoutPanel buttons;
        private bool busy = true;

        internal SelfUpdateInstallerWindow()
        {
            Text = "PAVISE // " + T("软件更新", "Software update");
            Font = Theme.UI(9f, false); BackColor = Theme.Card; ForeColor = Theme.Fg;
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = true; ControlBox = false;
            StartPosition = FormStartPosition.CenterScreen; ClientSize = new Size(Theme.S(600), Theme.S(350));
#if PAVISE_SELFTEST && PAVISE_UPDATE_INSTALLER_CHECKS
            if (SelfUpdateInstaller.FixtureRoot != null) { StartPosition = FormStartPosition.Manual; Location = new Point(-32000, -32000); ShowInTaskbar = false; }
#endif
            heading = MakeLabel(T("正在准备更新", "Preparing update"), 24, 24, 552, 36); heading.Font = Theme.UI(16f, true);
            detail = MakeLabel(T("请保持供电，更新窗口将继续显示安装进度。", "Keep your PC powered on. This window will show the installation progress."), 24, 70, 552, 48);
            progress = new ProgressBar { Minimum = 0, Maximum = 100, Value = 0, Style = ProgressBarStyle.Continuous };
            progress.SetBounds(Theme.S(24), Theme.S(136), Theme.S(552), Theme.S(12)); Controls.Add(progress);
            status = MakeLabel(T("等待安全退出回执", "Waiting for safe-shutdown confirmation"), 24, 162, 552, 42);
            warning = MakeLabel(T("更新进行中，暂时无法关闭或取消。新版启动成功后会自动删除旧版。", "Closing and cancellation are unavailable during installation. The old version is removed after the new one starts successfully."), 24, 214, 552, 55); warning.ForeColor = Theme.Warning;
            buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, WrapContents = true, BackColor = Theme.Card };
            buttons.SetBounds(Theme.S(24), Theme.S(282), Theme.S(552), Theme.S(46)); Controls.Add(buttons);
        }

        private Label MakeLabel(string text, int x, int y, int w, int h)
        {
            Label label = new Label { Text = text, ForeColor = Theme.Fg, BackColor = Theme.Card, AutoSize = false };
            label.SetBounds(Theme.S(x), Theme.S(y), Theme.S(w), Theme.S(h)); Controls.Add(label); return label;
        }
        private static string T(string zh, string en) { return Lang.Cur == 0 ? zh : en; }
        private void Post(Action action)
        {
            try { if (!IsDisposed && IsHandleCreated) BeginInvoke((MethodInvoker)delegate { if (!IsDisposed) action(); }); } catch { }
        }
        internal void Progress(int percent, string message)
        {
            Post(delegate { progress.Value = Math.Max(0, Math.Min(100, percent)); status.Text = message; heading.Text = T("正在安装更新", "Installing update"); });
        }
        internal void Manual(string reason)
        {
            Post(delegate
            {
                busy = false; ControlBox = true;
                Text = "PAVISE // " + T("手动更新", "Manual update");
                heading.Text = T("手动更新", "Manual update"); detail.Text = T("自动更新未完成，请通过备用入口下载最新版。", "Automatic update did not complete. Download the latest release using an alternate link.");
                progress.Visible = false; status.SetBounds(Theme.S(24), Theme.S(130), Theme.S(552), Theme.S(112)); status.Text = reason;
                warning.Visible = false; buttons.Controls.Clear();
                AddButton(T("关闭", "Close"), delegate { Close(); });
                AddButton("GitHub", delegate { Open(App.ManualUpdateGithubUrl); });
                AddButton(T("夸克下载入口", "Quark download"), delegate { Open(App.ManualUpdateQuarkUrl); });
#if PAVISE_SELFTEST && PAVISE_UPDATE_INSTALLER_CHECKS
                if (SelfUpdateInstaller.FixtureRoot != null)
                {
                    Timer timer = new Timer { Interval = 700 }; timer.Tick += delegate { timer.Stop(); timer.Dispose(); if (!IsDisposed) Close(); }; timer.Start();
                }
#endif
            });
        }
        internal void Complete()
        {
            Post(delegate
            {
                busy = false; ControlBox = true; progress.Value = 100;
                heading.Text = T("更新完成", "Update complete");
                detail.Text = T("新版已正常启动，原位置的程序已替换。", "The new version has started successfully and the original executable has been replaced.");
                status.Text = T("旧版程序已删除，更新器临时文件将在窗口关闭后清理。", "The previous executable was removed. Updater temporary files will be cleaned after this window closes.");
                warning.Text = T("游戏库、白名单及个人设置已保留。", "Your game library, whitelist and personal settings were retained."); warning.ForeColor = Theme.Green;
                AddButton(T("继续使用", "Continue"), delegate { Close(); });
                Timer timer = new Timer { Interval = 1600 }; timer.Tick += delegate { timer.Stop(); timer.Dispose(); if (!IsDisposed) Close(); }; timer.Start();
            });
        }
        internal void FinishSilently() { Post(delegate { busy = false; Close(); }); }
        private void AddButton(string text, Action click)
        {
            PillButton button = new PillButton(text); button.Width = Theme.S(145); button.Height = Theme.S(34); button.Click += delegate { click(); }; buttons.Controls.Add(button);
        }
        private void Open(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { status.Text = T("无法打开浏览器，请手动访问：", "Could not open your browser. Visit: ") + url; }
        }
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (busy && e.CloseReason != CloseReason.WindowsShutDown) e.Cancel = true;
            base.OnFormClosing(e);
        }
    }
}
