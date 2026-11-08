using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace PaviseApp
{
    internal sealed class UpdateDialog : Form
    {
        private static UpdateDialog current;
        private readonly Action<UpdateDialog, string, SelfUpdatePackage> install;
        private readonly Label caption, title, summary, versions, progressTitle, progressDetail, stages, notice, footer;
        private readonly TextBox reason;
        private readonly UpdateMeter progress;
        private readonly PillButton close, start, cancel, quark, github;
        private readonly Panel lockPanel;
        private CancellationTokenSource cancellation;
        private SelfUpdatePackage package;
        private string latestVersion;
        private string stagingDirectory;
        private bool busy, installing, allowClose, ownGate, ownerWasEnabled, stoppedRuntime;
        private int cancelRequested;
        private bool arranging;
        private int operation;
        private string state;

        internal static bool IsOpen { get { return current != null && !current.IsDisposed; } }
        internal bool IsInstalling { get { return installing; } }

        private static string T(string zh, string en) { return PerformancePolicy.Text(zh, en); }
        private static string T(string zh, string en, string ja) { return Lang.Cur == 2 ? ja : T(zh, en); }

        internal static UpdateDialog ShowFor(IWin32Window owner, UpdateResult latest, Action<UpdateDialog, string, SelfUpdatePackage> install)
        {
            if (IsOpen) { ActivateCurrent(); return current; }
            UpdateDialog dialog = new UpdateDialog(latest, install);
            current = dialog;
            if (owner == null) dialog.Show(); else dialog.Show(owner);
            return dialog;
        }

        internal static void ActivateCurrent()
        {
            if (!IsOpen) return;
            current.Post(delegate
            {
                if (current == null || current.IsDisposed) return;
                if (current.WindowState == FormWindowState.Minimized) current.WindowState = FormWindowState.Normal;
                current.Show(); current.BringToFront(); current.Activate();
            });
        }

        internal static UpdateDialog ShowStartupFailure(IWin32Window owner, string failure)
        {
            if (IsOpen) { current.ShowManual(failure); ActivateCurrent(); return current; }
            UpdateDialog dialog = new UpdateDialog(null, null);
            current = dialog;
            dialog.ShowManual(failure);
            if (owner == null) dialog.Show(); else dialog.Show(owner);
            return dialog;
        }

        internal UpdateDialog(UpdateResult latest, Action<UpdateDialog, string, SelfUpdatePackage> install)
        {
            this.install = install;
            latestVersion = latest != null && latest.Ok && UpdateChecker.IsNewer(latest.Latest, App.Version) ? latest.Latest : null;
            Name = "SelfUpdateDialog";
            Text = T("Pavise 软件更新", "Pavise software update");
            AccessibleName = Text;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.None;
            ShowInTaskbar = true;
            MaximizeBox = false;
            MinimizeBox = false;
            KeyPreview = true;
            BackColor = Theme.Bg;
            ForeColor = Theme.Fg;
            Font = Theme.UI(9f, false);
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer, true);

            caption = Label("PAVISE  //  " + T("软件更新", "SOFTWARE UPDATE"), "UpdateCaption", Theme.UI(9f, true), Theme.Dim);
            title = Label(T("软件更新", "Software update"), "UpdateTitle", Theme.UI(17f, true), Theme.Fg);
            summary = Label("", "UpdateSummary", Theme.UI(9f, false), Theme.Dim);
            versions = Label("", "UpdateVersions", Theme.UI(9f, true), Theme.Fg);
            progressTitle = Label("", "UpdateProgressTitle", Theme.UI(10f, true), Theme.Fg);
            progressDetail = Label("", "UpdateProgressDetail", Theme.UI(9f, false), Theme.Dim);
            stages = Label("", "UpdateStages", Theme.UI(8.5f, false), Theme.Dim);
            footer = Label(T("游戏库、白名单、个人设置与恢复记录将保留", "Your library, whitelist, settings and recovery records are retained"), "UpdateFooter", Theme.UI(8.5f, false), Theme.Dim);
            progress = new UpdateMeter { Name = "UpdateProgress", Minimum = 0, Maximum = 100, Value = 0, Style = ProgressBarStyle.Continuous, AccessibleName = T("更新进度", "Update progress") };
            reason = new TextBox { Name = "UpdateFailureReason", ReadOnly = true, Multiline = true, WordWrap = true, BorderStyle = BorderStyle.None, ScrollBars = ScrollBars.Vertical, BackColor = Theme.Card, ForeColor = Theme.Warning, Font = Theme.UI(9f, false), TabStop = false };
            lockPanel = new Panel { Name = "UpdateLockNotice", BackColor = Theme.Card };
            notice = Label("", "UpdateLockText", Theme.UI(9f, false), Theme.Warning);
            lockPanel.Controls.Add(notice);
            close = Button("×", "UpdateClose", BtnKind.Normal, delegate { Close(); });
            close.AccessibleName = T("关闭更新窗口", "Close update window");
            start = Button(T("立即更新", "Update now"), "UpdateStart", BtnKind.Primary, delegate { BeginOperation(state != "cancelled"); });
            cancel = Button(T("稍后更新", "Later"), "UpdateCancel", BtnKind.Normal, CancelOrClose);
            quark = Button(T("夸克下载入口", "Quark download entry"), "UpdateQuark", BtnKind.Primary, delegate { OpenManual(true); });
            github = Button(T("GitHub 下载", "GitHub download"), "UpdateGitHub", BtnKind.Normal, delegate { OpenManual(false); });
            Controls.AddRange(new Control[] { caption, title, summary, versions, progressTitle, progress, progressDetail, stages, reason, lockPanel, footer, close, start, cancel, quark, github });
            caption.MouseDown += Drag;
            MouseDown += Drag;
            Native.Dark(reason);
            SetState(latest != null && latest.Ok && !string.IsNullOrEmpty(latest.Latest) && UpdateChecker.IsNewer(latest.Latest, App.Version) ? "ready" : "initial");
            Rectangle area = Screen.FromPoint(Cursor.Position).WorkingArea;
            ClientSize = new Size(Math.Min(Theme.S(650), Math.Max(Theme.S(320), area.Width - Theme.S(32))), Theme.S(478));
            MinimumSize = new Size(Theme.S(320), Theme.S(420));
        }

        private Label Label(string text, string name, Font font, Color color)
        {
            return new Label { Name = name, Text = text, Font = font, ForeColor = color, BackColor = Color.Transparent, UseMnemonic = false, AutoSize = false };
        }

        private PillButton Button(string text, string name, BtnKind kind, Action action)
        {
            PillButton button = new PillButton(text, kind) { Name = name, Bg = Theme.Bg };
            button.Click += delegate { action(); };
            return button;
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            PositionOnOwner();
        }

        private void PositionOnOwner()
        {
            if (StartPosition != FormStartPosition.CenterParent) return;
            Form owner = Owner;
            Rectangle area = owner == null || owner.IsDisposed
                ? Screen.FromPoint(Cursor.Position).WorkingArea : Screen.FromControl(owner).WorkingArea;
            Rectangle anchor = owner == null || owner.IsDisposed || owner.WindowState == FormWindowState.Minimized
                ? area : owner.Bounds;
            Location = CenteredLocation(anchor, Size, area);
        }

        internal static Point CenteredLocation(Rectangle anchor, Size size, Rectangle workingArea)
        {
            int x = anchor.Left + (anchor.Width - size.Width) / 2;
            int y = anchor.Top + (anchor.Height - size.Height) / 2;
            return new Point(Math.Max(workingArea.Left, Math.Min(x, workingArea.Right - size.Width)),
                Math.Max(workingArea.Top, Math.Min(y, workingArea.Bottom - size.Height)));
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (state == "initial") BeginOperation(false);
            else cancel.Focus();
            // CenterParent is not applied by WinForms to a modeless Show(owner).
            PositionOnOwner();
        }

        internal void SetInstalling(string message)
        {
            Post(delegate
            {
                installing = true;
                SetState("installing");
                if (!string.IsNullOrEmpty(message)) summary.Text = message;
                Arrange();
            });
        }

        internal void ShowManual(string failure)
        {
            Post(delegate
            {
                if (cancellation != null) { try { cancellation.Cancel(); } catch (ObjectDisposedException) { } }
                installing = false;
                SetBusy(false);
                CleanupDownload();
                SetState("manual");
                string detail = (failure ?? "").Replace('\0', ' ').Trim();
                if (detail.Length > 1800) detail = detail.Substring(0, 1800);
                reason.Text = T("自动更新未完成。请选择夸克网盘或 GitHub 下载最新版。", "Automatic update did not complete. Download the latest version from Quark or GitHub.")
                    + (detail.Length == 0 ? "" : "\r\n\r\n" + detail);
                reason.AccessibleDescription = reason.Text;
                Arrange();
                close.Focus();
            });
        }

        internal void ShowStoppedManual(string failure)
        {
            Post(delegate
            {
                stoppedRuntime = true;
                ShowManual(failure);
                summary.Text = T("守护已停止。请退出 Pavise，下载新版后重新打开。", "The guard has stopped. Exit Pavise, download the latest version, then reopen it.");
                footer.Text = T("关闭此窗口将退出 Pavise，恢复记录会保留。", "Closing this window exits Pavise. Recovery records are retained.");
                Arrange();
            });
        }

        internal void CloseForHandoff()
        {
            Post(delegate { allowClose = true; Close(); });
        }

        private void SetBusy(bool value)
        {
            busy = value;
            if (!value && ownGate) { ownGate = false; SelfUpdateGate.End(); }
            close.Enabled = !value;
            close.AccessibleDescription = value ? T("更新进行中，暂时无法关闭软件", "Closing is unavailable while updating") : "";
            if (Owner != null && !Owner.IsDisposed)
            {
                if (value) { ownerWasEnabled = Owner.Enabled; Owner.Enabled = false; }
                else if (ownerWasEnabled) { Owner.Enabled = true; ownerWasEnabled = false; }
            }
        }

        private void BeginOperation(bool download)
        {
            if (busy || installing || stoppedRuntime || IsDisposed) return;
            if (!SelfUpdateGate.TryBegin()) { ActivateCurrent(); return; }
            ownGate = true;
            CancellationTokenSource cancelSource = new CancellationTokenSource();
            cancellation = cancelSource;
            CancellationToken token = cancelSource.Token;
            Interlocked.Exchange(ref cancelRequested, 0);
            int request = ++operation;
            package = null;
            latestVersion = null;
            SetBusy(true);
            SetState("checking");
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    SelfUpdatePackage found = SelfUpdateDownload.Check(token);
                    token.ThrowIfCancellationRequested();
                    int comparison = new Version(found.Version).CompareTo(new Version(App.Version));
                    if (comparison <= 0)
                    {
                        Post(delegate
                        {
                            if (!AcceptCompletion(request, token)) return;
                            latestVersion = found.Version;
                            SetBusy(false);
                            SetState(comparison < 0 ? "ahead" : "current");
                        });
                        return;
                    }
                    if (!download)
                    {
                        Post(delegate { if (AcceptCompletion(request, token)) { package = found; latestVersion = found.Version; SetBusy(false); SetState("ready"); } });
                        return;
                    }
                    stagingDirectory = SelfUpdateInstaller.CreateStagingDirectory();
                    Post(delegate { if (AcceptProgress(request, token)) { package = found; latestVersion = found.Version; SetState("downloading"); } });
                    string executable = SelfUpdateDownload.Download(found, stagingDirectory, delegate(SelfUpdateProgress p)
                    {
                        Post(delegate { if (AcceptProgress(request, token)) ApplyProgress(p); });
                    }, token);
                    token.ThrowIfCancellationRequested();
                    Post(delegate
                    {
                        if (!AcceptCompletion(request, token)) return;
                        if (install == null) { ShowManual(T("当前启动方式无法安装更新，请使用手动下载。", "This launch mode cannot install updates. Use a manual download.")); return; }
                        SetInstalling(T("正在安全停止守护并还原会话改动。请勿断电。", "Safely stopping the guard and restoring session changes. Keep the PC powered on."));
                        try { install(this, executable, found); }
                        catch (Exception ex) { ShowManual(ex.Message); }
                    });
                }
                catch (OperationCanceledException) { Post(delegate { FinishCancelled(request); }); }
                catch (Exception ex)
                {
                    string message = ex.Message;
                    Post(delegate
                    {
                        if (AcceptCompletion(request, token)) ShowManual(message);
                    });
                }
                finally { cancelSource.Dispose(); }
            });
        }

        private bool AcceptProgress(int request, CancellationToken token)
        {
            return operation == request && busy && !installing && Volatile.Read(ref cancelRequested) == 0 && !token.IsCancellationRequested;
        }

        private bool AcceptCompletion(int request, CancellationToken token)
        {
            if (operation != request || !busy || installing) return false;
            if (Volatile.Read(ref cancelRequested) != 0 || token.IsCancellationRequested)
            {
                FinishCancelled(request);
                return false;
            }
            return true;
        }

        private void FinishCancelled(int request)
        {
            if (operation != request || !busy || installing) return;
            CleanupDownload();
            package = null;
            latestVersion = null;
            SetBusy(false);
            SetState("cancelled");
        }

        private void CancelOrClose()
        {
            if (!busy) { Close(); return; }
            if (installing) return;
            Interlocked.Exchange(ref cancelRequested, 1);
            CancellationTokenSource source = cancellation;
            if (source != null) { try { source.Cancel(); } catch (ObjectDisposedException) { } }
            cancel.Enabled = false;
            cancel.Text = T("正在取消…", "Cancelling…");
        }

        private void CleanupDownload()
        {
            string directory = stagingDirectory;
            stagingDirectory = null;
            if (string.IsNullOrEmpty(directory)) return;
            try { SelfUpdateInstaller.CleanupStagingDirectory(directory); } catch { }
        }

        private void ApplyProgress(SelfUpdateProgress p)
        {
            if (p == null) return;
            bool verifying = string.Equals(p.Stage, "Verifying", StringComparison.OrdinalIgnoreCase);
            if (state != (verifying ? "verifying" : "downloading")) SetState(verifying ? "verifying" : "downloading");
            if (verifying)
            {
                progress.Style = ProgressBarStyle.Marquee;
                progressTitle.Text = T("正在校验文件完整性与发布来源", "Verifying file integrity and publisher");
                progressDetail.Text = T("下载完成不代表更新完成；验证后才会替换程序。", "Download complete; installation starts only after verification.");
            }
            else
            {
                int value = p.Total > 0 ? Math.Min(100, Math.Max(0, (int)(p.Received * 100.0 / p.Total))) : 0;
                progress.Style = p.Total > 0 ? ProgressBarStyle.Continuous : ProgressBarStyle.Marquee;
                progress.Value = value;
                progressTitle.Text = T("下载更新包", "Downloading update") + (p.Total > 0 ? "  " + value.ToString(CultureInfo.InvariantCulture) + "%" : "");
                progressDetail.Text = Bytes(p.Received) + (p.Total > 0 ? " / " + Bytes(p.Total) : "");
                progress.AccessibleDescription = progressTitle.Text + " " + progressDetail.Text;
            }
        }

        private static string Bytes(long bytes)
        {
            return (Math.Max(0, bytes) / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
        }

        private void SetState(string value)
        {
            state = value;
            bool manual = value == "manual";
            bool active = value == "checking" || value == "downloading" || value == "verifying" || value == "installing";
            Text = manual ? T("Pavise 手动更新", "Pavise manual update") : T("Pavise 软件更新", "Pavise software update");
            caption.Text = "PAVISE  //  " + (manual ? T("手动更新", "MANUAL UPDATE") : T("软件更新", "SOFTWARE UPDATE"));
            title.Text = manual ? T("手动更新", "Manual update") : value == "cancelled" ? T("更新已取消", "Update cancelled") : value == "ready" ? T("发现新版本", "Update available") : value == "ahead" ? T("无需更新", "No update needed", "更新は不要です") : value == "current" ? T("已是最新版本", "You're up to date") : value == "downloading" ? T("正在下载更新", "Downloading update") : value == "verifying" ? T("正在校验更新包", "Verifying update") : value == "installing" ? T("正在准备安装", "Preparing installation") : T("正在检查更新", "Checking for updates");
            summary.Text = manual ? T("自动更新失败后已切换为手动更新，请选择下方下载入口。", "Automatic update failed. Choose a manual download below.")
                : value == "cancelled" ? T("操作已取消，原程序未变更。可以重新检查更新或关闭窗口。", "Operation cancelled; the original program is unchanged. Check again or close this window.")
                : value == "ahead" ? T("当前版本高于线上版本，无需更新。", "Your version is newer than the online release. No update is needed.", "現在のバージョンは公開版より新しいため、更新は不要です。")
                : value == "current" ? T("当前运行的是 Pavise v", "You are running Pavise v") + App.Version
                : value == "ready" ? T("点击更新后自动下载、替换原 EXE 并启动新版；成功后删除旧版。", "Download, replace the original EXE and restart automatically; remove the old version after a successful start.")
                : value == "installing" ? T("正在安全停止运行，更新窗口将在程序退出后继续安装。", "Safely stopping Pavise. The updater will continue after the app exits.")
                : T("下载并校验完成后自动替换程序，然后重新启动。", "After download and verification, replace the program and restart.");
            versions.Text = T("当前 v", "Current v") + App.Version
                + (value == "ahead" && !string.IsNullOrEmpty(latestVersion)
                    ? "   " + T("线上 v", "Online v", "公開版 v") + latestVersion
                    : UpdateChecker.IsNewer(latestVersion, App.Version) ? "   →   v" + latestVersion : "");
            progress.Visible = progressTitle.Visible = progressDetail.Visible = stages.Visible = active;
            reason.Visible = manual;
            quark.Visible = github.Visible = manual || value == "ready";
            quark.Kind = manual ? BtnKind.Primary : BtnKind.Normal;
            lockPanel.Visible = active || value == "ready";
            notice.ForeColor = active ? Theme.Warning : Theme.Dim;
            notice.Text = active ? T("更新进行中，暂时无法关闭软件。\r\n主窗口、托盘退出与 Alt+F4 已锁定。", "Closing is unavailable while updating.\r\nThe main window, tray exit and Alt+F4 are locked.")
                : T("部分地区或网络环境可能无法自动更新。失败后本弹窗将切换为手动更新。", "Some regions or networks may not reach the update service. This dialog switches to manual downloads if it fails.");
            stages.Text = value == "installing" ? T("✓ 下载更新包    ✓ 校验文件    ● 准备替换    ○ 启动新版", "✓ Download    ✓ Verify    ● Prepare replacement    ○ Restart")
                : value == "verifying" ? T("✓ 下载更新包    ● 校验文件    ○ 替换程序    ○ 启动新版", "✓ Download    ● Verify    ○ Replace    ○ Restart")
                : T("● 下载更新包    ○ 校验文件    ○ 替换程序    ○ 启动新版", "● Download    ○ Verify    ○ Replace    ○ Restart");
            progress.Style = value == "downloading" ? ProgressBarStyle.Continuous : ProgressBarStyle.Marquee;
            if (value == "downloading") progress.Value = 0;
            progressTitle.Text = value == "checking" ? T("正在连接官方更新服务", "Connecting to the official update service") : value == "installing" ? T("正在等待安全退出回执", "Waiting for safe shutdown confirmation") : T("下载更新包", "Downloading update");
            progressDetail.Text = value == "checking" ? T("连接失败将显示夸克网盘与 GitHub 手动下载。", "If the connection fails, Quark and GitHub manual downloads will appear.") : value == "installing" ? T("此阶段无法取消。请勿断电。", "This stage cannot be cancelled. Keep the PC powered on.") : "";
            start.Visible = !active && value != "current" && value != "ahead" && !stoppedRuntime && install != null;
            start.Kind = manual ? BtnKind.Normal : BtnKind.Primary;
            start.Text = value == "cancelled" ? T("重新检查", "Check again") : manual ? T("重试自动更新", "Retry automatic update") : T("立即更新", "Update now");
            cancel.Visible = true;
            cancel.Enabled = !installing;
            cancel.Text = stoppedRuntime ? T("退出 Pavise", "Exit Pavise") : active ? T("取消更新", "Cancel update") : manual || value == "cancelled" ? T("关闭", "Close") : value == "current" || value == "ahead" ? T("完成", "Done", "完了") : T("稍后更新", "Later");
            close.Enabled = !busy;
            footer.Text = manual ? T("更新已停止，个人数据与恢复记录保留。", "Update stopped; personal data and recovery records retained.") : T("游戏库、白名单、个人设置与恢复记录将保留", "Your library, whitelist, settings and recovery records are retained");
            Arrange();
            Invalidate();
        }

        private void OpenManual(bool useQuark)
        {
            string url = useQuark ? App.ManualUpdateQuarkUrl : App.ManualUpdateGithubUrl;
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                if (useQuark) summary.Text = T("已打开官方下载页，请选择“夸克网盘”下载。", "Official download page opened. Select the Quark download there.");
                else summary.Text = T("已打开 GitHub 正式版本下载页。", "GitHub releases opened in your browser.");
            }
            catch
            {
                ShowManual(T("无法打开浏览器，请复制以下地址：", "Could not open a browser. Copy this address:") + "\r\n" + url);
            }
        }

        private void Post(Action action)
        {
            if (IsDisposed) return;
            try
            {
                if (InvokeRequired) BeginInvoke((MethodInvoker)delegate { if (!IsDisposed) action(); });
                else action();
            }
            catch (InvalidOperationException) { }
        }

        private static int Measure(string text, Font font, int width)
        {
            return TextRenderer.MeasureText(text, font, new Size(Math.Max(1, width), int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding).Height;
        }

        private void Arrange()
        {
            if (arranging || close == null || ClientSize.Width <= 0) return;
            arranging = true;
            try
            {
                int pad = Theme.S(26), inner = Math.Max(1, ClientSize.Width - pad * 2), y = Theme.S(66);
                caption.SetBounds(pad, Theme.S(17), Math.Max(1, inner - Theme.S(36)), Theme.S(22));
                close.SetBounds(ClientSize.Width - Theme.S(48), Theme.S(10), Theme.S(32), Theme.S(30));
                title.SetBounds(pad, y, inner, Theme.S(36)); y = title.Bottom + Theme.S(8);
                summary.SetBounds(pad, y, inner, Measure(summary.Text, summary.Font, inner)); y = summary.Bottom + Theme.S(17);
                versions.SetBounds(pad, y, inner, Theme.S(24)); y = versions.Bottom + Theme.S(18);
                if (progress.Visible)
                {
                    progressTitle.SetBounds(pad, y, inner, Theme.S(25));
                    progress.SetBounds(pad, progressTitle.Bottom + Theme.S(8), inner, Theme.S(12));
                    progressDetail.SetBounds(pad, progress.Bottom + Theme.S(9), inner, Theme.S(32));
                    stages.SetBounds(pad, progressDetail.Bottom + Theme.S(8), inner, Measure(stages.Text, stages.Font, inner));
                    y = stages.Bottom + Theme.S(17);
                }
                if (reason.Visible)
                {
                    int h = Math.Min(Theme.S(112), Math.Max(Theme.S(65), Measure(reason.Text, reason.Font, inner - Theme.S(20)) + Theme.S(12)));
                    reason.SetBounds(pad, y, inner, h); y = reason.Bottom + Theme.S(16);
                }
                if (quark.Visible)
                {
                    int width = (inner - Theme.S(12)) / 2;
                    quark.SetBounds(pad, y, width, Theme.S(44));
                    github.SetBounds(pad + width + Theme.S(12), y, width, Theme.S(44));
                    y = quark.Bottom + Theme.S(18);
                }
                if (lockPanel.Visible)
                {
                    int h = Measure(notice.Text, notice.Font, inner - Theme.S(24)) + Theme.S(22);
                    lockPanel.SetBounds(pad, y, inner, h);
                    notice.SetBounds(Theme.S(12), Theme.S(11), inner - Theme.S(24), h - Theme.S(22));
                    y = lockPanel.Bottom + Theme.S(16);
                }
                int buttonWidth = Theme.S(Lang.Cur == 0 ? 126 : 164), buttonHeight = Theme.S(36);
                int footerHeight = Measure(footer.Text, footer.Font, inner);
                footer.SetBounds(pad, Math.Max(y, ClientSize.Height - Theme.S(76) - footerHeight), inner, footerHeight);
                int buttonY = footer.Bottom + Theme.S(14);
                if (start.Visible)
                {
                    int w = Math.Min(buttonWidth, (inner - Theme.S(10)) / 2);
                    start.SetBounds(ClientSize.Width - pad - w, buttonY, w, buttonHeight);
                    cancel.SetBounds(start.Left - Theme.S(10) - w, buttonY, w, buttonHeight);
                }
                else cancel.SetBounds(ClientSize.Width - pad - buttonWidth, buttonY, buttonWidth, buttonHeight);
                int desired = buttonY + buttonHeight + Theme.S(22);
                if (ClientSize.Height != desired)
                {
                    Rectangle previous = Bounds;
                    bool keepCenter = Visible && StartPosition == FormStartPosition.CenterParent;
                    ClientSize = new Size(ClientSize.Width, desired);
                    if (keepCenter) Location = CenteredLocation(previous, Size, Screen.FromRectangle(previous).WorkingArea);
                }
            }
            finally { arranging = false; }
        }

        protected override void OnClientSizeChanged(EventArgs e) { base.OnClientSizeChanged(e); Arrange(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (Pen line = new Pen(Theme.Stroke)) e.Graphics.DrawRectangle(line, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
            using (SolidBrush accent = new SolidBrush(Theme.Accent)) e.Graphics.FillRectangle(accent, 1, 0, Theme.S(130), Theme.S(2));
            using (Pen line = new Pen(Theme.Stroke)) e.Graphics.DrawLine(line, 1, Theme.S(48), ClientSize.Width - 1, Theme.S(48));
        }

        private void Drag(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) { Native.ReleaseCapture(); Native.SendMessage(Handle, 161, (IntPtr)2, IntPtr.Zero); }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (busy && !allowClose && e.CloseReason != CloseReason.WindowsShutDown) { e.Cancel = true; return; }
            if (e.CloseReason == CloseReason.WindowsShutDown && cancellation != null) { try { cancellation.Cancel(); } catch (ObjectDisposedException) { } }
            base.OnFormClosing(e);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { if (!busy) Close(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (current == this) current = null;
                if (!allowClose) { try { if (cancellation != null) cancellation.Cancel(); } catch (ObjectDisposedException) { } SetBusy(false); }
            }
            base.Dispose(disposing);
        }

        private sealed class UpdateMeter : Control
        {
            private readonly System.Windows.Forms.Timer activity;
            private ProgressBarStyle style;
            private int value, offset = 25;
            internal int Minimum = 0, Maximum = 100;

            internal UpdateMeter()
            {
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
                AccessibleRole = AccessibleRole.ProgressBar;
                TabStop = false;
                activity = new System.Windows.Forms.Timer { Interval = 55 };
                activity.Tick += delegate { offset = (offset + 5) % 130; Invalidate(); };
            }

            internal int Value
            {
                get { return value; }
                set { this.value = Math.Max(Minimum, Math.Min(Maximum, value)); Invalidate(); }
            }

            internal ProgressBarStyle Style
            {
                get { return style; }
                set { style = value; SyncActivity(); Invalidate(); }
            }

            private void SyncActivity()
            {
                if (style == ProgressBarStyle.Marquee && Visible && IsHandleCreated) activity.Start();
                else activity.Stop();
            }

            protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); SyncActivity(); }
            protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); SyncActivity(); }

            protected override void OnPaint(PaintEventArgs e)
            {
                using (SolidBrush track = new SolidBrush(Theme.Inset)) e.Graphics.FillRectangle(track, ClientRectangle);
                using (SolidBrush fill = new SolidBrush(Theme.Accent))
                {
                    if (style == ProgressBarStyle.Marquee)
                    {
                        int width = Math.Max(1, Width / 4), left = (int)(Width * ((offset - 25) / 100.0));
                        e.Graphics.FillRectangle(fill, Math.Max(0, left), 0, Math.Max(0, Math.Min(Width, left + width) - Math.Max(0, left)), Height);
                    }
                    else e.Graphics.FillRectangle(fill, 0, 0, (int)Math.Round(Width * (value - Minimum) / (double)Math.Max(1, Maximum - Minimum)), Height);
                }
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) { activity.Stop(); activity.Dispose(); }
                base.Dispose(disposing);
            }
        }
    }
}
