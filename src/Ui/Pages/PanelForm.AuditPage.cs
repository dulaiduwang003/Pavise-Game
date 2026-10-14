// 作者：bdth 2074055628@qq.com
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace PaviseApp
{
    internal partial class PanelForm
    {
        private const int AuditWindowMs = GpuIrqCheck.CheckSeconds * 1000;

        private DBPanel pageAudit;
        private DBPanel auditScroll;
        private ScanView auditScan;
        private PillButton btnAuditStart;
        private Label lblAuditStatus;
        private PillButton btnAuditRun, btnAuditFixAll, btnAuditCancel;
        private int auditBusy;
        private int auditRunVersion;
        private bool auditRendered;
        private volatile bool auditCancelRequested;
        private AuditReport auditReport;
#if PAVISE_SELFTEST || PAVISE_PERFLAB
        internal Func<int, Func<bool>, Action<float, string>, AuditReport> AuditCollectForTest;
#endif

        private void BuildAuditPage()
        {
            int y = PageHeader(pageAudit, Lang.T("nav.audit"), Lang.T("audit.sub"), 2, false);

            btnAuditRun = new PillButton(Lang.F("audit.rerun", GpuIrqCheck.CheckSeconds), BtnKind.Primary);
            btnAuditRun.SetBounds(Theme.S(ContentX), Theme.S(y), Theme.S(150), Theme.S(34));
            btnAuditRun.Click += delegate { StartAudit(); };
            pageAudit.Controls.Add(btnAuditRun);

            int statusOffset = 158;
            int fixAllW = 150;
            lblAuditStatus = CardLabel(pageAudit, "", ContentX + statusOffset, y + 8,
                ContentW - statusOffset - fixAllW - 12, 20, 8.0f, false, Theme.Dim);

            btnAuditFixAll = new PillButton(Lang.T("audit.fixall.none"), BtnKind.Primary);
            btnAuditFixAll.SetBounds(Theme.S(ContentX + ContentW - fixAllW), Theme.S(y),
                Theme.S(fixAllW), Theme.S(34));
            btnAuditFixAll.Enabled = false;
            btnAuditFixAll.Click += delegate { OnAuditFixAllClick(); };
            pageAudit.Controls.Add(btnAuditFixAll);
            btnAuditCancel = new PillButton(Lang.T("dlg.cancel"), BtnKind.Normal);
            btnAuditCancel.Name = "auditCancel";
            btnAuditCancel.Bounds = btnAuditFixAll.Bounds;
            btnAuditCancel.Visible = false;
            btnAuditCancel.Click += delegate
            {
                auditCancelRequested = true;
                btnAuditCancel.Enabled = false;
                auditScan.ReportProgress(0f, Lang.T("irqcheck.dialog.stopping"));
            };
            pageAudit.Controls.Add(btnAuditCancel);
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
            btnAuditStart.Click += delegate { StartAudit(); };
            auditScan.Controls.Add(btnAuditStart);

            RestoreAuditPagePresentation();
        }

        private void RestoreAuditPagePresentation()
        {
            auditRendered = false;
            if (Volatile.Read(ref auditBusy) != 0) ShowAuditRunning();
            else if (auditReport != null) RenderAudit(auditReport);
            else ShowAuditIdle();
        }

        private void ShowAuditIdle()
        {
            auditScan.SetIdle(Lang.T("audit.idle.title"), Lang.F("audit.idle.hint", GpuIrqCheck.CheckSeconds));
            auditScan.Visible = true;
            auditScroll.Visible = false;
            SetToolbarVisible(false);
            int bw = Theme.S(184), bh = Theme.S(38);
            btnAuditStart.SetBounds((auditScan.Width - bw) / 2,
                auditScan.ContentBottom + Theme.S(20), bw, bh);
            btnAuditStart.Visible = true;
            btnAuditStart.BringToFront();
        }

        private void SetToolbarVisible(bool visible)
        {
            btnAuditRun.Visible = visible;
            btnAuditFixAll.Visible = visible;
            btnAuditCancel.Visible = false;
            if (visible) UpdateFixAllState();
        }

        private void StartAudit()
        {
            if (runtimeShutdownBusy || IsDisposed || Interlocked.CompareExchange(ref auditBusy, 1, 0) != 0) return;
            int version = Interlocked.Increment(ref auditRunVersion);
            auditCancelRequested = false;
            ShowAuditRunning();
            try
            {
                QueueUiStateWork(delegate
                {
                    AuditReport report = null;
                    try
                    {
                        Func<bool> cancel = delegate { return auditCancelRequested || IsDisposed || runtimeShutdownBusy
                            || gameMode != null && gameMode.IsActive; };
                        Action<float, string> progress = delegate(float ratio, string caption) { ReportAuditProgressForRun(version, ratio, caption); };
#if PAVISE_SELFTEST || PAVISE_PERFLAB
                        if (AuditCollectForTest == null) throw new InvalidOperationException("Audit collection was not mocked");
                        report = AuditCollectForTest(AuditWindowMs, cancel, progress);
#else
                        report = SystemAudit.Collect(AuditWindowMs, cancel, progress);
#endif
                    }
                    catch { }
                    try { PostUiStateResult(delegate { CompleteAudit(version, report); }); }
                    catch { if (version == Volatile.Read(ref auditRunVersion)) Interlocked.Exchange(ref auditBusy, 0); }
                });
            }
            catch { CompleteAudit(version, null); }
        }

        private void ShowAuditRunning()
        {
            SetAuditButtons(false);
            btnAuditFixAll.Visible = false;
            btnAuditCancel.Visible = true;
            btnAuditCancel.Enabled = !auditCancelRequested;
            btnAuditStart.Visible = false;
            auditScroll.Visible = false;
            auditScan.Visible = true;
            auditScan.BeginScan(auditCancelRequested ? Lang.T("irqcheck.dialog.stopping")
                : Lang.F("audit.phase.sampling", GpuIrqCheck.CheckSeconds));
            lblAuditStatus.Text = "";
        }

        private void CompleteAudit(int version, AuditReport report)
        {
            if (version != Volatile.Read(ref auditRunVersion)) return;
            Interlocked.Exchange(ref auditBusy, 0);
            if (IsDisposed || runtimeShutdownBusy || auditScan == null || auditScan.IsDisposed
                || btnAuditCancel == null || btnAuditCancel.IsDisposed) return;
            btnAuditCancel.Visible = false;
            auditScan.Stop();
            SetAuditButtons(true);
            if (report == null)
            {
                lblAuditStatus.Text = Lang.T(auditCancelRequested || gameMode != null && gameMode.IsActive ? "audit.cancelled" : "audit.failed");
                if (!auditRendered) ShowAuditIdle();
                else { auditScan.Visible = false; auditScroll.Visible = true; SetToolbarVisible(true); }
                return;
            }
            lblAuditStatus.Text = "";
            RenderAudit(report);
        }

        private void ReportAuditProgress(float ratio, string caption)
        {
            ReportAuditProgressForRun(Volatile.Read(ref auditRunVersion), ratio, caption);
        }

        private void ReportAuditProgressForRun(int version, float ratio, string caption)
        {
            try
            {
                PostUiStateResult(delegate
                {
                    if (IsDisposed || runtimeShutdownBusy || auditBusy == 0 || auditCancelRequested
                        || version != Volatile.Read(ref auditRunVersion) || auditScan == null || auditScan.IsDisposed) return;
                    auditScan.ReportProgress(ratio, caption);
                });
            }
            catch { }
        }

        private void SetAuditButtons(bool enabled)
        {
            btnAuditRun.Enabled = enabled;
            if (!enabled) { btnAuditFixAll.Enabled = false; btnAuditFixAll.Invalidate(); }
        }

        private void RenderAudit(AuditReport report)
        {
            auditReport = report;
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
        }

        private sealed class AuditFix
        {
            public Func<bool> CanFix;
            public Func<bool> CanRevert;
            public Action Fix;
            public Action Revert;
            public string ConfirmKey;
            public string FixLabel;
        }

        private Dictionary<string, AuditFix> BuildAuditFixes()
        {
            return new Dictionary<string, AuditFix>(StringComparer.Ordinal)
            {
                { "memcompress", new AuditFix
                {
                    CanFix = delegate { return auditReport != null && auditReport.MemoryCompressionCanDisable; },
                    CanRevert = delegate { return MemCompressTweak.OwnsState; },
                    Fix = delegate { ApplyAuditMemoryCompression(false); },
                    Revert = delegate { ApplyAuditMemoryCompression(true); },
                    FixLabel = "audit.mem.disable"
                } },
                { "gpuirq", new AuditFix
                {
                    CanFix = delegate { return auditReport != null && auditReport.GpuIrq != null
                        && (auditReport.GpuIrq.Outcome == GpuIrqOutcome.NeedsPlacement
                            || auditReport.GpuIrq.Outcome == GpuIrqOutcome.NeedsVerification)
                        && !GpuIrqCheck.AwaitingReboot(GpuIrqCheck.LoadState()) && !gameMode.IsActive; },
                    CanRevert = delegate { return false; },
                    Fix = delegate { OnIrqCheck(null, EventArgs.Empty); },
                    FixLabel = auditReport != null && auditReport.GpuIrq != null
                        && auditReport.GpuIrq.Outcome == GpuIrqOutcome.NeedsVerification ? "irqcheck.btn.verify" : "audit.irq.apply"
                } },
                { "gpuirqrestore", new AuditFix
                {
                    CanFix = delegate { return false; },
                    CanRevert = delegate { return GpuIrqCheck.HasResidue || IrqRelocate.HasResidue || IrqPriorityTweak.HasResidue; },
                    Revert = delegate { OnIrqRestore(null, EventArgs.Empty); }
                } },
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
                { "linkmetric", new AuditFix
                {
                    CanFix = delegate { return LinkMetricTweak.NeedsRepair(); },
                    CanRevert = delegate { return LinkMetricTweak.RepairedByPavise; },
                    Fix = delegate { LinkMetricTweak.Repair(); },
                    Revert = delegate { LinkMetricTweak.Restore(); }
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
            if (auditBusy != 0) return;
            if (!elevated)
            {
                PaviseDialog.Warn(this, App.DisplayName, Lang.T("env.performance.needadmin"));
                return;
            }
            bool canFix = fix.CanFix();
            bool revert = !canFix && fix.CanRevert();
            if (!canFix && !revert) { StartAudit(); return; }
            if (!revert && fix.ConfirmKey != null
                && !PaviseDialog.Confirm(this, App.DisplayName, Lang.T(fix.ConfirmKey), DlgKind.Warn))
                return;
            try
            {
                if (revert) fix.Revert(); else fix.Fix();
            }
            catch { }
            Logger.Log(revert ? Lang.T("log.panelformauditpage.1") : Lang.T("log.panelformauditpage.2"));
            StartAudit();
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
            if (auditBusy != 0) return;
            if (!elevated)
            {
                PaviseDialog.Warn(this, App.DisplayName, Lang.T("env.performance.needadmin"));
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
                    f.Fix();
                    done++;
                }
                catch { }
            }
            Logger.Log(done > 0
                ? Lang.T("log.panelformauditpage.3") + done + Lang.T("log.panelformauditpage.4")
                : Lang.T("log.panelformauditpage.5"));
            StartAudit();
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
                    Lang.F("audit.evidence.format", SystemAudit.EvidenceText(row.Evidence)), row.Warn,
                    hasButton, Lang.T(showFix ? (fix.FixLabel ?? "audit.fix") : "audit.fix.revert"), showFix,
                    onClick, ScrollContentW);
                card.SetBounds(Theme.S(6), Theme.S(sy), Theme.S(ScrollContentW), Theme.S(card.LogicalHeight));
                card.Name = "auditRow_" + (row.FixKey ?? "info");
                auditScroll.Controls.Add(card);
                sy += card.LogicalHeight + 8;
            }
            sy += 8;
            return sy;
        }

        private void ApplyAuditMemoryCompression(bool restore)
        {
            if (!restore && !MemCompressTweak.RamEligible())
            { PaviseDialog.Warn(this, App.DisplayName, Lang.T("audit.mem.ineligible")); return; }
            bool ok = restore ? MemCompressTweak.Restore() : MemCompressTweak.Enable(true);
            if (ok) PaviseDialog.Info(this, App.DisplayName, Lang.T(restore ? "log.memcompress.6" : "log.memcompress.4"));
            else PaviseDialog.Warn(this, App.DisplayName, Lang.T("audit.mem.failed"));
        }

    }
}
