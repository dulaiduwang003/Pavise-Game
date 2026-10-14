// 作者：bdth 2074055628@qq.com
using System;
using System.Drawing;
using System.Windows.Forms;

namespace PaviseApp
{
    internal partial class PanelForm
    {
        private LogStreamView logStream;
        private RoundPanel logWrap;
        private Label lblLogStreamHint;
        private Toggle swLogWrites;

        private void BuildLogPage()
        {
            pageLog.SetBounds(0, Theme.S(TopH), Theme.S(WinW), Theme.S(PageH));
            int contentWidth = ContentW + RailW;
            int y = PageHeader(pageLog, Lang.T("nav.log"), Lang.T("v20.log.sub"), 1, true, 0, contentWidth);

            var telemetry = MakeConsolePanel(pageLog, ContentX, y, contentWidth, 64, true);
            var liveDot = new StatusDot();
            liveDot.SetBounds(Theme.S(18), Theme.S(20), Theme.S(24), Theme.S(24));
            liveDot.Bg = Theme.Card; liveDot.FollowAccent = true; liveDot.Pulse = true;
            telemetry.Controls.Add(liveDot);
            CardLabel(telemetry, "PAVISE // SESSION LOG", 50, 9, 260, 18, 7.2f, true, Theme.Faint);
            CardLabel(telemetry, Lang.T("log.latest.session"), 50, 28, 260, 25, 10.5f, true, Theme.Fg);

            lblLogStreamHint = new Label { Name = "logSessionHint", ForeColor = Theme.Faint,
                BackColor = Color.Transparent, Font = Theme.UI(7.8f, false), TextAlign = ContentAlignment.MiddleRight };
            int hintLeft = Math.Max(310, contentWidth - 420);
            lblLogStreamHint.SetBounds(Theme.S(hintLeft), Theme.S(21), Theme.S(contentWidth - hintLeft - 18), Theme.S(24));
            telemetry.Controls.Add(lblLogStreamHint);

            int streamY = y + 76;
            int bottomY = PageH - 48;
            logWrap = new RoundPanel();
            logWrap.SetBounds(Theme.S(ContentX), Theme.S(streamY), Theme.S(contentWidth), Theme.S(bottomY - streamY - 10));
            logWrap.BackColor = Theme.Bg; logWrap.Fill = Theme.Inset; logWrap.Border = Theme.StrokeHi;
            logWrap.Radius = Theme.S(14); logWrap.AccentEdge = true; logWrap.Padding = new Padding(Theme.S(5));
            logStream = new LogStreamView();
            ConfigureLogView();
            logStream.Dock = DockStyle.Fill;
            Native.Dark(logStream);
            logWrap.Controls.Add(logStream);

            var refreshLog = new PillButton(Lang.T("v20.log.refresh")) { Name = "refreshSessionLog" };
            refreshLog.SetBounds(Theme.S(ContentX + 178), Theme.S(bottomY), Theme.S(96), Theme.S(36));
            refreshLog.Click += delegate { RefreshLog(true); };
            var clearLog = new PillButton(Lang.T("rep.clear.log"), BtnKind.Danger);
            clearLog.SetBounds(Theme.S(ContentX + 284), Theme.S(bottomY), Theme.S(112), Theme.S(36));
            clearLog.Click += delegate
            {
                if (!PaviseDialog.Confirm(this, App.DisplayName, Lang.T("rep.clear.ask"), DlgKind.Warn)) return;
                Logger.Clear();
                Logger.Log(Lang.T("log.panelformlogpage.1"));
                RefreshLog(true);
            };
            var diagnostics = new PillButton(Lang.T("workflow.diagnostic.copy"), BtnKind.Primary) { Name = "copyDiagnostics" };
            diagnostics.SetBounds(Theme.S(ContentX), Theme.S(bottomY), Theme.S(168), Theme.S(36));
            diagnostics.Click += delegate { CopyDiagnosticSummary(); };

            swLogWrites = MakeSwitch(Settings.Load(Logger.WritesEnabledKey, true), OnLogWritesToggle);
            swLogWrites.Location = new Point(Theme.S(ContentX + contentWidth) - swLogWrites.Width, Theme.S(bottomY + 6));
            var lblLogWrites = new Label { Text = Lang.T("set.logwrites"), ForeColor = Theme.Dim,
                BackColor = Theme.Bg, Font = Theme.UI(8.6f, false), TextAlign = ContentAlignment.MiddleRight };
            lblLogWrites.SetBounds(Theme.S(ContentX + contentWidth - 162) - swLogWrites.Width, Theme.S(bottomY), Theme.S(152), Theme.S(36));

            pageLog.Controls.AddRange(new Control[] { logWrap, diagnostics, refreshLog, clearLog, lblLogWrites, swLogWrites });
            RefreshLog(true);
        }

        private void ConfigureLogView()
        {
            if (logStream != null) logStream.Filter = 4;
        }

        private long diagnosticCopiedUntil;
#if PAVISE_SELFTEST
        internal Action<string> CopyDiagnosticForTest;
#endif
        private void CopyDiagnosticSummary()
        {
            try
            {
                string report = DiagnosticSummary.Build(gameMode, elevated, Logger.Tail(220));
#if PAVISE_SELFTEST
                if (CopyDiagnosticForTest != null) CopyDiagnosticForTest(report);
                else
#endif
                    Clipboard.SetText(report);
                diagnosticCopiedUntil = DateTime.UtcNow.AddSeconds(6).Ticks;
                RefreshLog(true);
            }
            catch { PaviseDialog.Warn(this, Lang.T("workflow.diagnostic.title"), Lang.T("workflow.diagnostic.failed")); }
        }

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

        private void RefreshLog() { RefreshLog(false); }
        private long logSeenVersion = -1;
        private LogStreamView logSeenStream;

        private void RefreshLog(bool force)
        {
            if (logStream == null) return;
            ConfigureLogView();
            SyncLogHint();
            // 日志关闭期间换局也需更新，不能继续显示旧局。
            logStream.SessionToken = gameMode.AutomationSession;
            long version = Logger.Version;
            if (!force && version == logSeenVersion && ReferenceEquals(logSeenStream, logStream)) return;
            logSeenVersion = version;
            logSeenStream = logStream;
            bool changed = logStream.SetText(Logger.Tail(2000));
            if (force && !changed) logStream.Invalidate();
        }

        private void SyncLogHint()
        {
            if (lblLogStreamHint == null) return;
            bool writing = Logger.WritesEnabled;
            lblLogStreamHint.Text = DateTime.UtcNow.Ticks < diagnosticCopiedUntil ? Lang.T("workflow.diagnostic.copied")
                : writing ? Lang.T("v20.log.hint") : Lang.T("v20.log.paused");
            lblLogStreamHint.ForeColor = writing ? Theme.Faint : LogStreamView.SeverityColor(LogEventSeverity.Warning);
        }
    }
}
