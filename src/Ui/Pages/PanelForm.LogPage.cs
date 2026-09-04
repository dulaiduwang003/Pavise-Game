// @author bdth 2074055628@qq.com
// 文件用途 构建结构化日志事件流并提供筛选 打开 刷新与清空
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace PaviseApp
{
    internal partial class PanelForm
    {
        private LogStreamView logStream;
        private RoundPanel logWrap;
        private TechTabs logFilterTabs;
        private Label lblLogTotal, lblLogWarnings, lblLogErrors, lblLogLatest;
        private Label lblLogStreamHint;
        private Toggle swLogWrites;

        private void BuildLogPage()
        {
            int y = PageHeader(pageLog, Lang.T("nav.log"), Lang.T("v20.log.sub"), 1);

            var telemetry = MakeConsolePanel(pageLog, ContentX, y, ContentW, 70, true);
            var liveDot = new StatusDot();
            liveDot.SetBounds(Theme.S(18), Theme.S(22), Theme.S(24), Theme.S(24));
            liveDot.Bg = Theme.Card; liveDot.FollowAccent = true; liveDot.Pulse = true;
            telemetry.Controls.Add(liveDot);
            CardLabel(telemetry, "PAVISE // EVENT BUS", 50, 11, 260, 20, 7.2f, true, Theme.Faint);
            CardLabel(telemetry, Lang.T("v20.log.live"), 50, 30, 260, 25, 10.5f, true, Theme.Fg);

            int statX = ContentW - 520;
            lblLogTotal = MakeLogStat(telemetry, Lang.T("v20.log.events"), statX, 0);
            lblLogWarnings = MakeLogStat(telemetry, Lang.T("v20.log.warnings"), statX + 126, 1);
            lblLogErrors = MakeLogStat(telemetry, Lang.T("v20.log.errors"), statX + 252, 2);
            lblLogLatest = MakeLogStat(telemetry, Lang.T("v20.log.latest"), statX + 378, 3);

            int filterY = y + 82;
            logFilterTabs = new TechTabs();
            logFilterTabs.SetBounds(Theme.S(ContentX), Theme.S(filterY), Theme.S(410), Theme.S(42));
            logFilterTabs.SetTabs(new[] { Lang.T("v20.log.all"), Lang.T("v20.log.alerts"), Lang.T("v20.log.failures") },
                new[] { "", "", "" });
            logFilterTabs.IndexChanged = delegate(int index)
            {
                if (logStream != null) logStream.Filter = index;
            };
            pageLog.Controls.Add(logFilterTabs);

            lblLogStreamHint = new Label();
            lblLogStreamHint.Text = Lang.T("v20.log.hint");
            lblLogStreamHint.ForeColor = Theme.Faint; lblLogStreamHint.BackColor = Theme.Bg;
            lblLogStreamHint.Font = Theme.UI(7.8f, false); lblLogStreamHint.TextAlign = ContentAlignment.MiddleRight;
            lblLogStreamHint.SetBounds(Theme.S(ContentX + 430), Theme.S(filterY), Theme.S(ContentW - 430), Theme.S(42));
            pageLog.Controls.Add(lblLogStreamHint);

            int streamY = filterY + 52;
            int bottomY = PageH - 48;
            logWrap = new RoundPanel();
            logWrap.SetBounds(Theme.S(ContentX), Theme.S(streamY), Theme.S(ContentW), Theme.S(bottomY - streamY - 10));
            logWrap.BackColor = Theme.Bg; logWrap.Fill = Theme.Inset; logWrap.Border = Theme.StrokeHi;
            logWrap.Radius = Theme.S(14); logWrap.AccentEdge = true; logWrap.Padding = new Padding(Theme.S(5));
            logStream = new LogStreamView();
            logStream.Dock = DockStyle.Fill;
            Native.Dark(logStream);
            logWrap.Controls.Add(logStream);

            var openLog = new PillButton(Lang.T("btn.openlog"));
            openLog.SetBounds(Theme.S(ContentX), Theme.S(bottomY), Theme.S(178), Theme.S(36));
            openLog.Click += delegate { OpenTextFile(Logger.LogPath); };
            var refreshLog = new PillButton(Lang.T("v20.log.refresh"));
            refreshLog.SetBounds(Theme.S(ContentX + 190), Theme.S(bottomY), Theme.S(132), Theme.S(36));
            refreshLog.Click += delegate { RefreshLog(true); };
            var clearLog = new PillButton(Lang.T("rep.clear.log"), BtnKind.Danger);
            clearLog.SetBounds(Theme.S(ContentX + 334), Theme.S(bottomY), Theme.S(142), Theme.S(36));
            clearLog.Click += delegate
            {
                if (!PaviseDialog.Confirm(this, App.DisplayName, Lang.T("rep.clear.ask"), DlgKind.Warn)) return;
                Logger.Clear();
                Logger.Log(Lang.T("log.panelformlogpage.1"));
                RefreshLog(true);
            };

            swLogWrites = MakeSwitch(Settings.Load(Logger.WritesEnabledKey, true), OnLogWritesToggle);
            swLogWrites.Location = new Point(Theme.S(ContentX + ContentW) - swLogWrites.Width,
                Theme.S(bottomY + 6));
            var lblLogWrites = new Label();
            lblLogWrites.Text = Lang.T("set.logwrites");
            lblLogWrites.ForeColor = Theme.Dim; lblLogWrites.BackColor = Theme.Bg;
            lblLogWrites.Font = Theme.UI(8.6f, false); lblLogWrites.TextAlign = ContentAlignment.MiddleRight;
            lblLogWrites.SetBounds(Theme.S(ContentX + ContentW - 260) - swLogWrites.Width, Theme.S(bottomY),
                Theme.S(250), Theme.S(36));

            pageLog.Controls.AddRange(new Control[] { logWrap, openLog, refreshLog, clearLog, lblLogWrites, swLogWrites });
            RefreshLog(true);
        }

        // 关闭前先把这条落盘 否则日志的最后一行会停在无关的动作上 看不出是被主动关的
        //   开启则先放开写入再记 顺序反了这两条都会丢
        private void OnLogWritesToggle(object s, EventArgs e)
        {
            if (IsDisposed || swLogWrites == null || swLogWrites.IsDisposed) return;
            bool on = swLogWrites.Checked;
            if (!on) Logger.Log(Lang.T("log.logwrites.off"));
            Logger.WritesEnabled = on;
            Settings.Save(Logger.WritesEnabledKey, on);
            if (on) Logger.Log(Lang.T("log.logwrites.on"));
            swLogWrites.SetSilently(Settings.Load(Logger.WritesEnabledKey, true));
            RefreshLog(true);
        }

        private Label MakeLogStat(Control parent, string title, int x, int channel)
        {
            CardLabel(parent, title, x, 10, 112, 18, 7.1f, false, Theme.Faint);
            Label value = CardLabel(parent, "0", x, 29, 112, 26, channel == 2 ? 11f : 10f, true,
                channel == 2 ? Theme.Danger : channel == 1 ? LogStreamView.SeverityColor(LogEventSeverity.Warning) : Theme.Fg);
            return value;
        }

        private void OpenTextFile(string path)
        {
            try { if (!File.Exists(path)) File.WriteAllText(path, "", System.Text.Encoding.UTF8); using (Process.Start(System.IO.Path.Combine(Environment.SystemDirectory, "notepad.exe"), path)) { } }
            catch { }
        }

        private void RefreshLog()
        {
            RefreshLog(false);
        }

        private long logSeenVersion = -1;
        private LogStreamView logSeenStream;

        private void RefreshLog(bool force)
        {
            if (logStream == null) return;
            // 记录关掉时刷新只会看到一份不动的旧日志 不说明白会被当成卡死
            if (lblLogStreamHint != null)
            {
                bool writing = Logger.WritesEnabled;
                lblLogStreamHint.Text = writing ? Lang.T("v20.log.hint") : Lang.T("v20.log.paused");
                lblLogStreamHint.ForeColor = writing ? Theme.Faint
                    : LogStreamView.SeverityColor(LogEventSeverity.Warning);
            }
            // 没有新写入就不读文件 Tail 和 Sweep 线程的 Log 抢的是同一把锁
            long version = Logger.Version;
            if (!force && version == logSeenVersion && ReferenceEquals(logSeenStream, logStream)) return;
            logSeenVersion = version;
            logSeenStream = logStream;
            string text = Logger.Tail(220);
            bool changed = logStream.SetText(text);
            if (force && !changed) logStream.Invalidate();
            if (lblLogTotal != null) lblLogTotal.Text = logStream.TotalCount.ToString();
            if (lblLogWarnings != null) lblLogWarnings.Text = logStream.WarningCount.ToString();
            if (lblLogErrors != null) lblLogErrors.Text = logStream.ErrorCount.ToString();
            if (lblLogLatest != null) lblLogLatest.Text = logStream.LatestTime;
            if (logFilterTabs != null) logFilterTabs.SetHot(new[] { false,
                logStream.WarningCount + logStream.ErrorCount > 0, logStream.ErrorCount > 0 });
        }
    }
}
