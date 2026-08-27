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

            var streamHint = new Label();
            streamHint.Text = Lang.T("v20.log.hint");
            streamHint.ForeColor = Theme.Faint; streamHint.BackColor = Theme.Bg;
            streamHint.Font = Theme.UI(7.8f, false); streamHint.TextAlign = ContentAlignment.MiddleRight;
            streamHint.SetBounds(Theme.S(ContentX + 430), Theme.S(filterY), Theme.S(ContentW - 430), Theme.S(42));
            pageLog.Controls.Add(streamHint);

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

            pageLog.Controls.AddRange(new Control[] { logWrap, openLog, refreshLog, clearLog });
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

        private void RefreshLog(bool force)
        {
            if (logStream == null) return;
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
