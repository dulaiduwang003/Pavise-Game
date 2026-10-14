// 作者：bdth 2074055628@qq.com
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace PaviseApp
{
    internal partial class PanelForm : Form
    {
        private DBPanel pageIrq;
        private void MonoLabel(Control parent, string text, int x, int y, int w)
        {
            var label = new Label();
            label.Text = text; label.ForeColor = Theme.Faint; label.BackColor = Color.Transparent;
            label.Font = Theme.Mono(6.2f);
            label.SetBounds(Theme.S(x), Theme.S(y), Theme.S(w), Theme.S(14));
            parent.Controls.Add(label);
        }

        private static string IrqReasonText(string key)
        {
            return string.IsNullOrEmpty(key) ? "" : Lang.T(key);
        }

        internal static string IrqHintText(GpuIrqState state, bool awaiting, out Color color)
        {
            color = Theme.Dim;
            switch (state.Phase)
            {
                case GpuIrqPhase.Pending:
                    color = Theme.Warning;
                    return Lang.T(awaiting ? "irqcheck.hint.pending" : "irqcheck.hint.verify");
                case GpuIrqPhase.Applied:
                    color = Theme.Green;
                    return Lang.F("irqcheck.hint.applied", IrqRelocate.MaskText(state.Target));
                case GpuIrqPhase.Clear: color = Theme.Green; return Lang.T("irqcheck.hint.clear");
                case GpuIrqPhase.NoRegion: return Lang.T("irqcheck.hint.noregion");
                case GpuIrqPhase.RolledBack:
                    color = Theme.Danger;
                    return Lang.F("irqcheck.hint.rolledback", IrqReasonText(state.Reason));
                case GpuIrqPhase.Skipped: return Lang.F("irqcheck.hint.skipped", IrqReasonText(state.Reason));
                default: return Lang.T("irqcheck.hint.none");
            }
        }

        internal static string IrqBannerState(GpuIrqState state, bool awaiting, out Color color)
        {
            switch (state.Phase)
            {
                case GpuIrqPhase.Pending: color = Theme.Warning; return awaiting ? "RESTART PENDING" : "VERIFY PENDING";
                case GpuIrqPhase.Applied: color = Theme.Green; return "ROUTED";
                case GpuIrqPhase.Clear: color = Theme.Green; return "CLEAR";
                case GpuIrqPhase.NoRegion: color = Theme.Dim; return "NO GAME CORES";
                case GpuIrqPhase.RolledBack: color = Theme.Danger; return "ROLLED BACK";
                case GpuIrqPhase.Skipped: color = Theme.Dim; return "SKIPPED";
                default: color = Theme.Accent; return "READY";
            }
        }

        internal static string IrqStateText(GpuIrqState state, bool awaiting, out Color color)
        {
            color = Theme.Fg;
            switch (state.Phase)
            {
                case GpuIrqPhase.Pending:
                    color = Theme.Warning;
                    return Lang.T(awaiting ? "irqcheck.state.pending" : "irqcheck.state.verify");
                case GpuIrqPhase.Applied: color = Theme.Green; return Lang.T("irqcheck.state.applied");
                case GpuIrqPhase.Clear: color = Theme.Green; return Lang.T("irqcheck.state.clear");
                case GpuIrqPhase.NoRegion: color = Theme.Dim; return Lang.T("irqcheck.state.noregion");
                case GpuIrqPhase.RolledBack: color = Theme.Danger; return Lang.T("irqcheck.state.rolledback");
                case GpuIrqPhase.Skipped: color = Theme.Dim; return IrqReasonText(state.Reason);
                default: color = Theme.Dim; return Lang.T("irqcheck.state.none");
            }
        }

        internal static string IrqResultText(GpuIrqResult r, out DlgKind kind)
        {
            kind = DlgKind.Info;
            string seen = r.Gpu == null ? "—" : IrqRelocate.MaskText(r.Gpu.Seen);
            string text;
            switch (r.Outcome)
            {
                case GpuIrqOutcome.Failed: kind = DlgKind.Danger; text = Lang.F("irqcheck.out.failed", r.Detail); break;
                case GpuIrqOutcome.AwaitingReboot: kind = DlgKind.Warn; text = Lang.T("irqcheck.out.awaiting"); break;
                case GpuIrqOutcome.NoEvidence:
                    kind = DlgKind.Warn;
                    text = Lang.F("irqcheck.out.noevidence", r.Gpu == null ? 0 : r.Gpu.Events, GpuIrqCheck.MinEvents);
                    break;
                case GpuIrqOutcome.NoRegion: text = Lang.T("irqcheck.out.noregion"); break;
                case GpuIrqOutcome.Restored:
                    kind = DlgKind.Warn;
                    text = Lang.T("irqcheck.out.restored") + (r.Detail.Length > 0 ? " · " + r.Detail : "");
                    break;
                case GpuIrqOutcome.NoTarget: text = Lang.T("irqcheck.reason.notarget"); break;
                case GpuIrqOutcome.Clear:
                    kind = DlgKind.Success;
                    text = Lang.F("irqcheck.out.clear", seen, IrqRelocate.MaskText(r.Region));
                    break;
                case GpuIrqOutcome.Scheduled:
                    kind = DlgKind.Warn;
                    text = Lang.F("irqcheck.out.scheduled", seen, IrqRelocate.MaskText(r.Region), IrqRelocate.MaskText(r.Target));
                    break;
                case GpuIrqOutcome.Verified:
                    kind = DlgKind.Success;
                    text = Lang.F("irqcheck.out.verified", seen);
                    break;
                case GpuIrqOutcome.RolledBack: kind = DlgKind.Danger; text = Lang.F("irqcheck.out.rolledback", r.Detail); break;
                case GpuIrqOutcome.Skipped:
                    text = Lang.F("irqcheck.out.skipped", IrqReasonText(GpuIrqCheck.LoadState().Reason));
                    break;
                default: text = Lang.T("irqcheck.out.cancelled"); break;
            }
            if (r.Outcome == GpuIrqOutcome.Failed || r.Outcome == GpuIrqOutcome.AwaitingReboot
                || r.Outcome == GpuIrqOutcome.Cancelled) return text;
            if (r.Slow == null || r.Slow.Count == 0) return text + "\r\n\r\n" + Lang.T("irqcheck.out.noslow");
            var sb = new System.Text.StringBuilder(text);
            sb.Append("\r\n\r\n").Append(Lang.T("irqcheck.out.slow"));
            foreach (SlowDriver s in r.Slow)
                sb.Append("\r\n· ").Append(s.Device.Length > 0 ? s.Device + " (" + s.Driver + ")" : s.Driver)
                    .Append("  ").Append(Lang.F("irqcheck.slow.row", s.MaxUs.ToString("F0"), s.P99Us.ToString("F0")));
            return sb.ToString();
        }

        private void OnIrqCheck(object sender, EventArgs e)
        {
            if (!Native.IsElevated()) { PaviseDialog.Warn(this, Lang.T("nav.irq"), Lang.T("irqcheck.needadmin")); return; }
            if (gameMode.IsActive) { PaviseDialog.Warn(this, Lang.T("nav.irq"), Lang.T("irqcheck.ingame")); return; }
            if (GpuIrqCheck.AwaitingReboot(GpuIrqCheck.LoadState()))
            { PaviseDialog.Info(this, Lang.T("nav.irq"), Lang.T("irqcheck.out.awaiting")); return; }
            GpuIrqResult result;
            using (var dialog = new IrqCheckDialog(delegate(Func<bool> cancelled, Action<float> progress)
                {
                    return GpuIrqCheck.Run(delegate { return cancelled() || gameMode.IsActive; }, progress);
                }))
            {
                dialog.ShowDialog(this);
                result = dialog.Result;
            }
            if (result == null) return;
            DlgKind kind;
            string text = IrqResultText(result, out kind);
            if (kind == DlgKind.Success) PaviseDialog.Success(this, Lang.T("irqcheck.dialog.title"), text);
            else if (kind == DlgKind.Danger) PaviseDialog.Error(this, Lang.T("irqcheck.dialog.title"), text);
            else if (kind == DlgKind.Warn) PaviseDialog.Warn(this, Lang.T("irqcheck.dialog.title"), text);
            else PaviseDialog.Info(this, Lang.T("irqcheck.dialog.title"), text);
        }

        private void OnIrqRestore(object sender, EventArgs e)
        {
            if (!Native.IsElevated()) return;
            if (gameMode.IsActive) { PaviseDialog.Warn(this, Lang.T("nav.audit"), Lang.T("irqcheck.ingame")); return; }
            if (!PaviseDialog.Confirm(this, Lang.T("irqcheck.restore.btn"), Lang.T("irqcheck.restore.confirm"), DlgKind.Warn)) return;
            bool ok = true;
            try { if (GpuIrqCheck.HasResidue) ok &= GpuIrqCheck.RestoreAll(); } catch { ok = false; }
            try
            {
                if (IrqRelocate.HasResidue || IrqPriorityTweak.HasResidue)
                {
                    IrqRestoreResult legacy = IrqRelocate.RestoreDevices(null);
                    ok &= legacy != null && legacy.Success;
                }
            }
            catch { ok = false; }
            if (ok) PaviseDialog.Success(this, Lang.T("irqcheck.restore.btn"), Lang.T("irqcheck.restore.done"));
            else PaviseDialog.Warn(this, Lang.T("irqcheck.restore.btn"), Lang.T("irqcheck.restore.failed"));
        }
    }
}
