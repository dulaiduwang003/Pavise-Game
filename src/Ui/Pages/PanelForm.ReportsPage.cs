// @author bdth 2074055628@qq.com
// 文件用途 构建报告页 会话摘要卡与运行日志双视图

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace PaviseApp
{
    internal partial class PanelForm
    {
        private TextBox tbReports;

        private void BuildReportsPage()
        {
            int y = PageHeader(pageReports, Lang.T("nav.reports"), Lang.T("v14.reports.sub"), 2);

            var swEvidence = MakeSwitch(Settings.Load("EvidenceMode", false), null);
            swEvidence.CheckedChanged += delegate
            {
                if (!swEvidence.Checked) { Settings.Save("EvidenceMode", false); return; }
                if (MessageBox.Show(this, Lang.T("ev.toggle.warn"), "Pavise",
                        MessageBoxButtons.OKCancel, MessageBoxIcon.Warning,
                        MessageBoxDefaultButton.Button2) != DialogResult.OK)
                {
                    swEvidence.SetSilently(false);
                    return;
                }
                Settings.Save("EvidenceMode", true);
            };
            var evCard = new SettingCard();
            evCard.SetBounds(Theme.S(ContentX), Theme.S(y), Theme.S(ContentW), Theme.S(84));
            evCard.Title = Lang.T("ev.toggle");
            evCard.Desc = Lang.T("ev.toggle.sub");
            evCard.Host(swEvidence);
            pageReports.Controls.Add(evCard);
            y += 92;

            tabReportsCards = new PillButton(Lang.T("rep.tab.cards"), BtnKind.Primary);
            tabReportsCards.Bg = Theme.Bg;
            tabReportsCards.SetBounds(Theme.S(ContentX), Theme.S(y), Theme.S(126), Theme.S(30));
            tabReportsCards.Click += delegate { SetReportsView(0); };
            tabReportsLog = new PillButton(Lang.T("rep.tab.log"));
            tabReportsLog.Bg = Theme.Bg;
            tabReportsLog.SetBounds(Theme.S(ContentX + 134), Theme.S(y), Theme.S(126), Theme.S(30));
            tabReportsLog.Click += delegate { SetReportsView(1); };
            y += 38;

            reportsCardsPanel = new DBPanel();
            reportsCardsPanel.SetBounds(Theme.S(ContentX), Theme.S(y), Theme.S(ContentW), Theme.S(PageH - y - 58));
            reportsCardsPanel.BackColor = Theme.Bg;
            reportsCardsPanel.AutoScroll = true;
            Native.Dark(reportsCardsPanel);

            reportsLogWrap = new RoundPanel();
            reportsLogWrap.SetBounds(Theme.S(ContentX), Theme.S(y), Theme.S(ContentW), Theme.S(PageH - y - 58));
            reportsLogWrap.BackColor = Theme.Bg; reportsLogWrap.Fill = Theme.Inset; reportsLogWrap.Border = Theme.Stroke;
            reportsLogWrap.Radius = Theme.S(14); reportsLogWrap.Padding = new Padding(Theme.S(14));
            tbReports = new TextBox(); tbReports.Multiline = true; tbReports.ReadOnly = true; tbReports.ScrollBars = ScrollBars.Both; tbReports.WordWrap = false;
            tbReports.BackColor = Theme.Inset; tbReports.ForeColor = Theme.Fg; tbReports.BorderStyle = BorderStyle.None; tbReports.Font = Theme.Mono(8.75f); tbReports.Dock = DockStyle.Fill;
            Native.Dark(tbReports); reportsLogWrap.Controls.Add(tbReports);
            reportsLogWrap.Visible = false;

            var openReport = new PillButton(Lang.T("v14.open.report")); openReport.SetBounds(Theme.S(ContentX), Theme.S(PageH - 48), Theme.S(190), Theme.S(36));
            openReport.Click += delegate { OpenTextFile(Path.Combine(Paths.Data, SessionReportStore.FileName)); };
            var openLog = new PillButton(Lang.T("btn.openlog")); openLog.SetBounds(Theme.S(ContentX + 202), Theme.S(PageH - 48), Theme.S(190), Theme.S(36));
            openLog.Click += delegate { OpenTextFile(Logger.LogPath); };
            btnClearLog = new PillButton(Lang.T("rep.clear.log"), BtnKind.Danger);
            btnClearLog.SetBounds(Theme.S(ContentX + 404), Theme.S(PageH - 48), Theme.S(150), Theme.S(36));
            btnClearLog.Visible = false;
            btnClearLog.Click += delegate
            {
                if (MessageBox.Show(this, Lang.T("rep.clear.ask"), "Pavise",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                Logger.Clear();
                Logger.Log("运行日志已手动清除");
                RefreshReports();
            };
            btnClearReports = new PillButton(Lang.T("rep.clear.cards"), BtnKind.Danger);
            btnClearReports.SetBounds(Theme.S(ContentX + 404), Theme.S(PageH - 48), Theme.S(150), Theme.S(36));
            btnClearReports.Click += delegate
            {
                if (MessageBox.Show(this, Lang.T("rep.clear.cards.ask"), "Pavise",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                SessionReportStore.ClearAll(Paths.Data);
                EvidenceStore.ClearAll(Paths.Data);
                reportsCardsSig = null;
                RefreshReports();
            };
            pageReports.Controls.AddRange(new Control[] {
                tabReportsCards, tabReportsLog, reportsCardsPanel, reportsLogWrap,
                openReport, openLog, btnClearLog, btnClearReports });
            RefreshReports();
        }

        private PillButton tabReportsCards, tabReportsLog, btnClearLog, btnClearReports;
        private DBPanel reportsCardsPanel;
        private RoundPanel reportsLogWrap;
        private int reportsViewMode;
        private string reportsCardsSig;

        private void SetReportsView(int mode)
        {
            if (reportsViewMode == mode) return;
            reportsViewMode = mode;
            tabReportsCards.Kind = mode == 0 ? BtnKind.Primary : BtnKind.Normal;
            tabReportsLog.Kind = mode == 1 ? BtnKind.Primary : BtnKind.Normal;
            tabReportsCards.Invalidate();
            tabReportsLog.Invalidate();
            reportsCardsPanel.Visible = mode == 0;
            reportsLogWrap.Visible = mode == 1;
            btnClearLog.Visible = mode == 1;
            btnClearReports.Visible = mode == 0;
            reportsCardsSig = null;
            RefreshReports();
        }

        private void OpenTextFile(string path)
        {
            try { if (!File.Exists(path)) File.WriteAllText(path, "", System.Text.Encoding.UTF8); using (Process.Start(System.IO.Path.Combine(Environment.SystemDirectory, "notepad.exe"), path)) { } }
            catch { }
        }

        private void RefreshReports()
        {
            if (reportsCardsPanel == null || tbReports == null) return;
            if (reportsViewMode == 1)
            {
                string logText = SessionReportStore.TailOfFile(Logger.LogPath, 220);
                if (logText.Length == 0) logText = Lang.T("rep.log.none");
                if (tbReports.Text != logText)
                {
                    tbReports.Text = logText;
                    try { tbReports.SelectionStart = tbReports.TextLength; tbReports.ScrollToCaret(); } catch { }
                }
                return;
            }
            string reportsRaw = SessionReportStore.ReadTail(Paths.Data, 60);
            string evidenceRaw = EvidenceStore.ReadTail(Paths.Data, 80);
            string sig = reportsRaw + "\n#\n" + evidenceRaw;
            if (sig == reportsCardsSig) return;
            reportsCardsSig = sig;
            RebuildSessionCards(reportsRaw, evidenceRaw);
        }

        private void RebuildSessionCards(string reportsRaw, string evidenceRaw)
        {
            while (reportsCardsPanel.Controls.Count > 0) reportsCardsPanel.Controls[0].Dispose();
            List<SessionSummary> sessions = SessionSummaries.Parse(reportsRaw, evidenceRaw, 30);
            if (sessions.Count == 0)
            {
                var empty = new Label
                {
                    Text = Lang.T("rep.cards.none"), ForeColor = Theme.Dim, BackColor = Theme.Bg,
                    Font = Theme.UI(9f, false), AutoEllipsis = true
                };
                empty.UseCompatibleTextRendering = false;
                empty.SetBounds(Theme.S(8), Theme.S(16), Theme.S(ContentW - 20), Theme.S(22));
                reportsCardsPanel.Controls.Add(empty);
                return;
            }
            int cardW = ContentW - 24;
            for (int i = 0; i < sessions.Count; i++)
            {
                var card = new SessionCardView();
                card.SetBounds(Theme.S(2), Theme.S(2 + i * 94), Theme.S(cardW), Theme.S(86));
                card.Bind(sessions[i]);
                card.DeleteRequested = OnDeleteSessionCard;
                reportsCardsPanel.Controls.Add(card);
            }
        }

        private void OnDeleteSessionCard(SessionSummary session)
        {
            SessionReportStore.DeleteSession(Paths.Data, session.Time, session.Game);
            EvidenceStore.DeleteNear(Paths.Data, session.Stamp, session.Game);
            reportsCardsSig = null;
            RefreshReports();
        }

    }
}
