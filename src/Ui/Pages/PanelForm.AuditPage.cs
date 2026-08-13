// @author bdth 2074055628@qq.com
// 文件用途 构建系统体检页 手动触发检测 展示能力 实测数据 持久设置与结论

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace PaviseApp
{
    internal partial class PanelForm
    {
        private const int QuickAuditWindowMs = 3000;
        private const int PreciseAuditWindowMs = 30000;

        private DBPanel pageAudit;
        private DBPanel auditScroll;
        private AuditScanView auditScan;
        private PillButton btnAuditStart;
        private Label lblAuditStatus;
        private PillButton btnAuditQuick, btnAuditPrecise, btnAuditNv, btnAuditPoll;
        private int auditBusy;
        private string auditNvProbeText;
        private string auditPollText;
        private bool auditRendered;
        private Stopwatch auditClock;
        private int auditTotalMs;
        private readonly List<Control> auditEntering = new List<Control>();
        private float auditEnterPhase;

        private void BuildAuditPage()
        {
            int y = PageHeader(pageAudit, Lang.T("nav.audit"), Lang.T("audit.sub"), 2);

            btnAuditQuick = new PillButton(Lang.T("audit.rerun"), BtnKind.Primary);
            btnAuditQuick.SetBounds(Theme.S(ContentX), Theme.S(y), Theme.S(150), Theme.S(34));
            btnAuditQuick.Click += delegate { StartAudit(QuickAuditWindowMs); };
            pageAudit.Controls.Add(btnAuditQuick);

            btnAuditPrecise = new PillButton(Lang.T("audit.precise"), BtnKind.Normal);
            btnAuditPrecise.SetBounds(Theme.S(ContentX + 158), Theme.S(y), Theme.S(170), Theme.S(34));
            btnAuditPrecise.Click += delegate { StartAudit(PreciseAuditWindowMs); };
            pageAudit.Controls.Add(btnAuditPrecise);

            btnAuditNv = new PillButton(Lang.T("audit.nvprobe"), BtnKind.Normal);
            btnAuditNv.SetBounds(Theme.S(ContentX + 336), Theme.S(y), Theme.S(170), Theme.S(34));
            btnAuditNv.Click += delegate { StartNvProbe(); };
            pageAudit.Controls.Add(btnAuditNv);

            btnAuditPoll = new PillButton(Lang.T("audit.pollprobe"), BtnKind.Normal);
            btnAuditPoll.SetBounds(Theme.S(ContentX + 514), Theme.S(y), Theme.S(160), Theme.S(34));
            btnAuditPoll.Click += delegate { StartPollProbe(); };
            pageAudit.Controls.Add(btnAuditPoll);

            int statusOffset = 684;
            lblAuditStatus = CardLabel(pageAudit, "", ContentX + statusOffset, y + 8,
                ContentW - statusOffset, 20, 8.0f, false, Theme.Dim);
            y += 44;

            auditScroll = new DBPanel();
            auditScroll.SetBounds(Theme.S(20), Theme.S(y), Theme.S(PageW - 40), Theme.S(PageH - y - 8));
            auditScroll.BackColor = Theme.Bg;
            auditScroll.AutoScroll = true;
            auditScroll.Visible = false;
            Native.Dark(auditScroll);
            pageAudit.Controls.Add(auditScroll);

            auditScan = new AuditScanView();
            auditScan.SetBounds(Theme.S(ContentX), Theme.S(y + 6), Theme.S(ContentW), Theme.S(PageH - y - 20));
            pageAudit.Controls.Add(auditScan);

            btnAuditStart = new PillButton(Lang.T("audit.start"), BtnKind.Primary);
            btnAuditStart.Bg = Theme.Card;
            btnAuditStart.Click += delegate { StartAudit(QuickAuditWindowMs); };
            auditScan.Controls.Add(btnAuditStart);

            ShowAuditIdle();
        }

        private void ShowAuditIdle()
        {
            auditScan.SetIdle(Lang.T("audit.idle.title"), Lang.T("audit.idle.hint"));
            auditScan.Visible = true;
            Fx.Settle(auditScroll);
            auditScroll.Visible = false;
            Fx.SlideIn(auditScan);
            SetToolbarVisible(false);
            int bw = Theme.S(184), bh = Theme.S(38);
            btnAuditStart.SetBounds((auditScan.Width - bw) / 2,
                auditScan.ContentBottom + Theme.S(20), bw, bh);
            btnAuditStart.Visible = true;
            btnAuditStart.BringToFront();
        }

        private void SetToolbarVisible(bool visible)
        {
            bool wasShown = btnAuditQuick.Visible;
            btnAuditQuick.Visible = visible;
            btnAuditPrecise.Visible = visible;
            btnAuditNv.Visible = visible;
            btnAuditPoll.Visible = visible;
            if (!visible || wasShown) return;
            Fx.SlideIn(btnAuditQuick);
            Fx.SlideIn(btnAuditPrecise);
            Fx.SlideIn(btnAuditNv);
            Fx.SlideIn(btnAuditPoll);
        }

        private void StartAudit(int windowMs)
        {
            if (Interlocked.Exchange(ref auditBusy, 1) == 1) return;
            auditNvProbeText = null;
            auditPollText = null;
            SetAuditButtons(false);
            btnAuditStart.Visible = false;
            Fx.Settle(auditScroll);
            auditScroll.Visible = false;
            auditScan.Visible = true;
            Fx.SlideIn(auditScan);
            auditScan.BeginScan(Lang.T("audit.phase.capability"));
            lblAuditStatus.Text = "";
            BeginAuditProgress(windowMs);

            ThreadPool.QueueUserWorkItem(_ =>
            {
                AuditReport report = null;
                try { report = SystemAudit.Collect(windowMs); } catch { }
                Interlocked.Exchange(ref auditBusy, 0);
                try
                {
                    BeginInvoke((MethodInvoker)(() =>
                    {
                        EndAuditProgress();
                        SetAuditButtons(true);
                        if (report == null)
                        {
                            lblAuditStatus.Text = Lang.T("audit.failed");
                            if (!auditRendered) ShowAuditIdle();
                            return;
                        }
                        lblAuditStatus.Text = "";
                        RenderAudit(report);
                    }));
                }
                catch { }
            });
        }

        private void StartNvProbe()
        {
            if (!NvApi.Available)
            {
                lblAuditStatus.Text = Lang.T("audit.nv.unavailable");
                return;
            }
            if (Interlocked.Exchange(ref auditBusy, 1) == 1) return;
            SetAuditButtons(false);
            btnAuditStart.Visible = false;
            auditScroll.Visible = false;
            auditScan.Visible = true;
            auditScan.BeginScan(Lang.T("audit.nv.probing"));
            BeginAuditProgress(QuickAuditWindowMs);

            ThreadPool.QueueUserWorkItem(_ =>
            {
                string summary;
                try
                {
                    var results = NvDrsTweaks.ProbeWriteback();
                    if (results.Count == 0) summary = Lang.T("audit.nv.failed");
                    else
                    {
                        int ok = 0;
                        var parts = new List<string>();
                        foreach (var r in results)
                        {
                            if (r.Ok) ok++;
                            parts.Add(r.Key + "=" + r.Outcome);
                        }
                        summary = Lang.F("audit.nv.summary", ok, results.Count)
                            + " " + string.Join(" ", parts.ToArray()) + " ";
                    }
                }
                catch (Exception ex) { summary = Lang.T("audit.nv.failed") + " " + ex.Message; }
                auditNvProbeText = summary;
                AuditReport report = null;
                try { report = SystemAudit.Collect(QuickAuditWindowMs); } catch { }
                Interlocked.Exchange(ref auditBusy, 0);
                try
                {
                    BeginInvoke((MethodInvoker)(() =>
                    {
                        EndAuditProgress();
                        SetAuditButtons(true);
                        lblAuditStatus.Text = "";
                        if (report != null) RenderAudit(report);
                        else if (!auditRendered) ShowAuditIdle();
                    }));
                }
                catch { }
            });
        }

        private void BeginAuditProgress(int windowMs)
        {
            auditTotalMs = windowMs + Math.Max(600, Math.Min(3000, windowMs / 10)) + 900;
            auditClock = Stopwatch.StartNew();
            UiClock.Frame += OnAuditProgressFrame;
            UiClock.Wake();
        }

        private void EndAuditProgress()
        {
            UiClock.Frame -= OnAuditProgressFrame;
            auditClock = null;
            auditScan.ReportProgress(1f, Lang.T("audit.phase.done"));
        }

        private void OnAuditProgressFrame(object sender, EventArgs e)
        {
            Stopwatch clock = auditClock;
            if (clock == null || auditTotalMs <= 0) return;
            float ratio = (float)clock.ElapsedMilliseconds / auditTotalMs;
            if (ratio > 0.97f) ratio = 0.97f;
            auditScan.ReportProgress(ratio, AuditPhaseText(ratio));
            UiClock.Wake();
        }

        private static string AuditPhaseText(float ratio)
        {
            if (ratio < 0.12f) return Lang.T("audit.phase.capability");
            if (ratio < 0.72f) return Lang.T("audit.phase.measure");
            if (ratio < 0.92f) return Lang.T("audit.phase.persistent");
            return Lang.T("audit.phase.verdict");
        }

        private void SetAuditButtons(bool enabled)
        {
            btnAuditQuick.Enabled = enabled;
            btnAuditPrecise.Enabled = enabled;
            btnAuditNv.Enabled = enabled;
            btnAuditPoll.Enabled = enabled;
        }

        internal const int PollProbeMs = 4000;

        private void StartPollProbe()
        {
            if (Interlocked.Exchange(ref auditBusy, 1) == 1) return;

            var probe = new PollingRateProbe();
            if (!probe.Start())
            {
                probe.Dispose();
                Interlocked.Exchange(ref auditBusy, 0);
                lblAuditStatus.Text = Lang.T("audit.poll.nostart");
                return;
            }

            SetAuditButtons(false);
            btnAuditStart.Visible = false;
            Fx.Settle(auditScroll);
            auditScroll.Visible = false;
            auditScan.Visible = true;
            Fx.SlideIn(auditScan);
            auditScan.BeginScan(Lang.T("audit.poll.probing"));
            lblAuditStatus.Text = "";
            BeginAuditProgress(PollProbeMs);

            var timer = new System.Windows.Forms.Timer();
            timer.Interval = PollProbeMs;
            timer.Tick += delegate
            {
                timer.Stop();
                timer.Dispose();
                PollingResult result = null;
                try { result = probe.Stop(); }
                catch { }
                finally { probe.Dispose(); }
                auditPollText = PollResultText(result);
                FinishPollProbe();
            };
            timer.Start();
        }

        private void FinishPollProbe()
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                AuditReport report = null;
                try { report = SystemAudit.Collect(QuickAuditWindowMs); } catch { }
                Interlocked.Exchange(ref auditBusy, 0);
                try
                {
                    BeginInvoke((MethodInvoker)(() =>
                    {
                        EndAuditProgress();
                        SetAuditButtons(true);
                        lblAuditStatus.Text = "";
                        if (report != null) RenderAudit(report);
                        else if (!auditRendered) ShowAuditIdle();
                    }));
                }
                catch { }
            });
        }

        internal static string PollResultText(PollingResult result)
        {
            if (result == null) return Lang.F("audit.poll.fail", "读取异常");
            if (!result.Ok) return Lang.F("audit.poll.fail", result.Failure);

            string head = Lang.F("audit.poll.ok", result.Hz,
                result.MedianIntervalMs.ToString("0.00"), result.Samples);
            if (PollingRateProbe.IsWeak(result.Hz))
                return head + " " + Lang.F("audit.poll.weak", (1000.0 / result.Hz).ToString("0.#"));
            if (result.Hz >= PollingRateProbe.GoodHz) return head + " " + Lang.T("audit.poll.good");
            return head;
        }

        private void RenderAudit(AuditReport report)
        {
            auditScan.Stop();
            auditScan.Visible = false;
            btnAuditStart.Visible = false;
            auditScroll.Visible = true;
            SetToolbarVisible(true);

            auditScroll.SuspendLayout();
            auditScroll.AutoScrollPosition = Point.Empty;
            var stale = new Control[auditScroll.Controls.Count];
            auditScroll.Controls.CopyTo(stale, 0);
            auditScroll.Controls.Clear();
            foreach (Control c in stale) c.Dispose();
            auditEntering.Clear();

            int sy = 2;
            sy = RenderAuditGroup(Lang.T("audit.sec.capability"), report.Capability, sy);
            sy = RenderAuditGroup(Lang.T("audit.sec.machine"), report.Machine, sy);
            sy = RenderAuditGroup(Lang.T("audit.sec.persistent"), report.Persistent, sy);
            sy = RenderAuditGroup(Lang.T("audit.sec.verdicts"), report.Verdicts, sy);

            CardLabel(auditScroll, Lang.T("audit.footer"), 8, sy + 4, ScrollContentW - 16, 34, 7.6f, false, Theme.Faint);
            auditScroll.ResumeLayout();
            auditScroll.PerformLayout();
            auditScroll.AutoScrollPosition = Point.Empty;
            auditRendered = true;
            BeginAuditEnter();
        }

        private const int AuditEnterSlide = 22;

        private void BeginAuditEnter()
        {
            if (auditEntering.Count == 0) return;
            auditEnterPhase = 0f;
            for (int i = 0; i < auditEntering.Count; i++)
                auditEntering[i].Left = Theme.S(6) - Theme.S(AuditEnterSlide);
            UiClock.Frame += OnAuditEnterFrame;
            UiClock.Wake();
        }

        private void OnAuditEnterFrame(object sender, EventArgs e)
        {
            auditEnterPhase += 0.075f;
            bool moving = false;
            int baseX = Theme.S(6);
            for (int i = 0; i < auditEntering.Count; i++)
            {
                Control c = auditEntering[i];
                if (c.IsDisposed) continue;
                float local = auditEnterPhase - i * 0.055f;
                if (local <= 0f) { moving = true; continue; }
                float t = local > 1f ? 1f : local;
                float ease = 1f - (1f - t) * (1f - t) * (1f - t);
                c.Left = baseX - (int)Math.Round(Theme.S(AuditEnterSlide) * (1f - ease));
                if (t < 1f) moving = true;
            }
            if (moving) { UiClock.Wake(); return; }
            UiClock.Frame -= OnAuditEnterFrame;
            auditEntering.Clear();
        }

        private int RenderAuditGroup(string title, List<AuditRow> rows, int sy)
        {
            Section(auditScroll, title, 6, sy);
            sy += 26;
            foreach (AuditRow row in rows)
            {
                string note = row.Note ?? "";
                if (auditNvProbeText != null && row.Name == "NVIDIA 驱动接口")
                    note = Lang.T("audit.nv.result") + auditNvProbeText;
                if (auditPollText != null && row.Name == "键鼠链路")
                    note = auditPollText + "\r\n" + note;

                int noteHeight = MeasureNoteHeight(note);
                int rowHeight = Math.Max(64, 40 + noteHeight);
                var panel = MakeConsolePanel(auditScroll, 6, sy, ScrollContentW, rowHeight, false);
                CardLabel(panel, row.Name, 16, 10, 236, 20, 8.6f, true, Theme.Fg);
                CardLabel(panel, row.Value, 256, 10, ScrollContentW - 380, 20, 8.6f, true,
                    row.Warn ? Theme.Accent : Theme.Fg);
                CardLabel(panel, Lang.T("audit.evidence") + row.Evidence,
                    ScrollContentW - 118, 10, 104, 20, 7.4f, false, Theme.Faint);
                WrapLabel(panel, note, 16, 34, ScrollContentW - 32, noteHeight, 7.8f, Theme.Dim);
                auditEntering.Add(panel);
                sy += rowHeight + 8;
            }
            sy += 8;
            return sy;
        }

        private int MeasureNoteHeight(string note)
        {
            if (string.IsNullOrEmpty(note)) return 24;
            Font font = Theme.UI(7.8f, false);
            int widthPx = Theme.S(ScrollContentW - 32);
            Size measured = TextRenderer.MeasureText(note, font,
                new Size(widthPx, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
            int unscaled = (int)Math.Ceiling(measured.Height / Dpi.Scale);
            return Math.Max(24, unscaled + 4);
        }

        private void WrapLabel(Control parent, string text, int x, int y, int w, int h, float size, Color color)
        {
            var label = new Label();
            label.Text = text; label.ForeColor = color; label.BackColor = Color.Transparent;
            label.Font = Theme.UI(size, false); label.AutoEllipsis = false; label.AutoSize = false;
            label.UseCompatibleTextRendering = false;
            label.SetBounds(Theme.S(x), Theme.S(y), Theme.S(w), Theme.S(h));
            parent.Controls.Add(label);
        }
    }
}
