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
        private ScanView auditScan;
        private PillButton btnAuditStart;
        private Label lblAuditStatus;
        private PillButton btnAuditQuick, btnAuditPrecise, btnAuditFixAll;
        private int auditBusy;
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

            int statusOffset = 336;
            int fixAllW = 150;
            lblAuditStatus = CardLabel(pageAudit, "", ContentX + statusOffset, y + 8,
                ContentW - statusOffset - fixAllW - 12, 20, 8.0f, false, Theme.Dim);

            btnAuditFixAll = new PillButton(Lang.T("audit.fixall.none"), BtnKind.Primary);
            btnAuditFixAll.SetBounds(Theme.S(ContentX + ContentW - fixAllW), Theme.S(y),
                Theme.S(fixAllW), Theme.S(34));
            btnAuditFixAll.Enabled = false;
            btnAuditFixAll.Click += delegate { OnAuditFixAllClick(); };
            pageAudit.Controls.Add(btnAuditFixAll);
            y += 44;

            auditScroll = new DBPanel();
            auditScroll.SetBounds(Theme.S(20), Theme.S(y), Theme.S(PageW - 40), Theme.S(PageH - y - 8));
            auditScroll.BackColor = Theme.Bg;
            auditScroll.AutoScroll = true;
            auditScroll.Visible = false;
            Native.Dark(auditScroll);
            pageAudit.Controls.Add(auditScroll);

            auditScan = new ScanView();
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
            btnAuditFixAll.Visible = visible;
            if (visible) UpdateFixAllState();
            if (!visible || wasShown) return;
            Fx.SlideIn(btnAuditQuick);
            Fx.SlideIn(btnAuditPrecise);
            Fx.SlideIn(btnAuditFixAll);
        }

        private void StartAudit(int windowMs)
        {
            if (Interlocked.Exchange(ref auditBusy, 1) == 1) return;
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
            if (!enabled) { btnAuditFixAll.Enabled = false; btnAuditFixAll.Invalidate(); }
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

        private sealed class AuditFix
        {
            public Func<bool> CanFix;
            public Func<bool> CanRevert;
            public Action Fix;
            public Action Revert;
            public string ConfirmKey;
        }

        private static Dictionary<string, AuditFix> BuildAuditFixes()
        {
            return new Dictionary<string, AuditFix>(StringComparer.Ordinal)
            {
                { "msi", new AuditFix
                {
                    CanFix = delegate { return MsiModeTweak.Disabled().Count > 0; },
                    CanRevert = delegate { return MsiModeTweak.EnabledByPavise; },
                    Fix = delegate { MsiModeTweak.Enable(); },
                    Revert = delegate { MsiModeTweak.Restore(); },
                    ConfirmKey = "audit.msi.confirm"
                } },
                { "quantum", new AuditFix
                {
                    CanFix = delegate { return QuantumTweak.NeedsRepair(); },
                    CanRevert = delegate { return QuantumTweak.RepairedByPavise; },
                    Fix = delegate { QuantumTweak.Repair(); },
                    Revert = delegate { QuantumTweak.Restore(); }
                } },
                { "net", new AuditFix
                {
                    CanFix = delegate { return NetTweak.NeedsRepair(); },
                    CanRevert = delegate { return NetTweak.RepairedByPavise; },
                    Fix = delegate { NetTweak.Repair(); },
                    Revert = delegate { NetTweak.Restore(); }
                } },
                { "inputq", new AuditFix
                {
                    CanFix = delegate { return InputMythTweak.NeedsRepair(); },
                    CanRevert = delegate { return InputMythTweak.HasResidue(); },
                    Fix = delegate { InputMythTweak.Repair(); },
                    Revert = delegate { InputMythTweak.Restore(); }
                } },
                { "fth", new AuditFix
                {
                    CanFix = delegate { return FthTweak.NeedsRepair(); },
                    CanRevert = delegate { return FthTweak.RepairedByPavise; },
                    Fix = delegate { FthTweak.Repair(); },
                    Revert = delegate { FthTweak.Restore(); },
                    ConfirmKey = "audit.fth.confirm"
                } },
            };
        }

        private void OnAuditFixClick(AuditFix fix)
        {
            if (!elevated)
            {
                PaviseDialog.Warn(this, App.DisplayName, Lang.T("vbs.needadmin"));
                return;
            }
            bool revert = !fix.CanFix() && fix.CanRevert();
            if (!revert && fix.ConfirmKey != null
                && !PaviseDialog.Confirm(this, App.DisplayName, Lang.T(fix.ConfirmKey), DlgKind.Warn))
                return;
            try
            {
                IrqMutationBoundary.Run(delegate
                {
                    if (revert) fix.Revert(); else fix.Fix();
                });
            }
            catch { }
            Logger.Log(revert ? Lang.T("log.panelformauditpage.1") : Lang.T("log.panelformauditpage.2"));
            StartAudit(QuickAuditWindowMs);
        }

        private void UpdateFixAllState()
        {
            int n = 0;
            foreach (AuditFix f in BuildAuditFixes().Values)
            {
                try { if (f.CanFix()) n++; } catch { }
            }
            btnAuditFixAll.Enabled = n > 0;
            btnAuditFixAll.Text = n > 0 ? Lang.F("audit.fixall", n) : Lang.T("audit.fixall.none");
            btnAuditFixAll.Invalidate();
        }

        private void OnAuditFixAllClick()
        {
            if (!elevated)
            {
                PaviseDialog.Warn(this, App.DisplayName, Lang.T("vbs.needadmin"));
                return;
            }
            int done = 0;
            foreach (AuditFix f in BuildAuditFixes().Values)
            {
                try
                {
                    if (!f.CanFix()) continue;
                    if (f.ConfirmKey != null
                        && !PaviseDialog.Confirm(this, App.DisplayName, Lang.T(f.ConfirmKey), DlgKind.Warn))
                        continue;
                    IrqMutationBoundary.Run(f.Fix);
                    done++;
                }
                catch { }
            }
            Logger.Log(done > 0
                ? Lang.T("log.panelformauditpage.3") + done + Lang.T("log.panelformauditpage.4")
                : Lang.T("log.panelformauditpage.5"));
            StartAudit(QuickAuditWindowMs);
        }

        private int RenderAuditGroup(string title, List<AuditRow> rows, int sy)
        {
            Section(auditScroll, title, 6, sy);
            sy += 26;
            Dictionary<string, AuditFix> fixes = BuildAuditFixes();
            foreach (AuditRow row in rows)
            {
                AuditFix fix = null;
                bool showFix = false, showRevert = false;
                if (row.FixKey != null && fixes.TryGetValue(row.FixKey, out fix))
                {
                    try { showFix = fix.CanFix(); showRevert = !showFix && fix.CanRevert(); }
                    catch { }
                }
                bool hasButton = showFix || showRevert;
                AuditFix boundFix = fix;
                Action onClick = hasButton ? delegate { OnAuditFixClick(boundFix); } : (Action)null;

                var card = new AuditRowCard(row.Name, row.Value, row.Note ?? "",
                    Lang.T("audit.evidence") + row.Evidence, row.Warn,
                    hasButton, Lang.T(showFix ? "audit.fix" : "audit.fix.revert"), showFix,
                    onClick, ScrollContentW);
                card.SetBounds(Theme.S(6), Theme.S(sy), Theme.S(ScrollContentW), Theme.S(card.LogicalHeight));
                auditScroll.Controls.Add(card);
                auditEntering.Add(card);
                sy += card.LogicalHeight + 8;
            }
            sy += 8;
            return sy;
        }
    }
}
