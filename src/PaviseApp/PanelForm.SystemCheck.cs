using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace PaviseApp
{
	internal partial class PanelForm : Form
	{
		private void RestoreAuditPagePresentation()
		{
			auditRendered = false;
			if (Volatile.Read(ref auditBusy) != 0)
			{
				ShowAuditRunning();
			}
			else if (auditReport != null)
			{
				RenderAudit(auditReport);
			}
			else
			{
				ShowAuditIdle();
			}
		}
		private void CompleteAuditRefresh(int version, AuditReport report)
		{
			if (version != Volatile.Read(ref auditRunVersion))
			{
				return;
			}
			Interlocked.Exchange(ref auditBusy, 0);
			if (!base.IsDisposed && !runtimeShutdownBusy && auditScroll != null && !auditScroll.IsDisposed && lblAuditStatus != null && !lblAuditStatus.IsDisposed)
			{
				SetAuditButtons(true);
				if (report == null)
				{
					lblAuditStatus.Text = PerformancePolicy.Text("检查结果刷新失败，可点“重新体检”", "The results could not be refreshed; run the checkup again");
					UpdateFixAllState();
				}
				else
				{
					lblAuditStatus.Text = "";
					RenderAudit(report, true);
				}
			}
		}
		private void CompleteAudit(int version, AuditReport report)
		{
			if (version != Volatile.Read(ref auditRunVersion))
			{
				return;
			}
			Interlocked.Exchange(ref auditBusy, 0);
			if (base.IsDisposed || runtimeShutdownBusy || auditScan == null || auditScan.IsDisposed || btnAuditCancel == null || btnAuditCancel.IsDisposed)
			{
				return;
			}
			btnAuditCancel.Visible = false;
			auditScan.Stop();
			SetAuditButtons(true);
			if (report == null)
			{
				lblAuditStatus.Text = Lang.T((auditCancelRequested || (gameMode != null && gameMode.IsActive)) ? "audit.cancelled" : "audit.failed");
				if (!auditRendered)
				{
					ShowAuditIdle();
					return;
				}
				auditScan.Visible = false;
				auditScroll.Visible = true;
				SetToolbarVisible(true);
			}
			else
			{
				lblAuditStatus.Text = "";
				RenderAudit(report);
			}
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
					if (!base.IsDisposed && !runtimeShutdownBusy && auditBusy != 0 && !auditCancelRequested && version == Volatile.Read(ref auditRunVersion) && auditScan != null && !auditScan.IsDisposed)
					{
						auditScan.ReportProgress(ratio, caption);
					}
				});
			}
			catch
			{
			}
		}
		private void SetAuditButtons(bool enabled)
		{
			btnAuditRun.Enabled = enabled;
			if (!enabled)
			{
				btnAuditFixAll.Enabled = false;
				btnAuditFixAll.Invalidate();
			}
		}
		private void RenderAudit(AuditReport report)
		{
			RenderAudit(report, false);
		}
		private void RenderAudit(AuditReport report, bool keepScroll)
		{
			Point autoScrollPosition = auditScroll.AutoScrollPosition;
			auditReport = report;
			auditScan.Stop();
			auditScan.Visible = false;
			btnAuditStart.Visible = false;
			auditScroll.Visible = true;
			SetToolbarVisible(true);
			auditScroll.SuspendLayout();
			auditScroll.AutoScrollPosition = Point.Empty;
			Control[] controlItems = new Control[auditScroll.Controls.Count];
			auditScroll.Controls.CopyTo(controlItems, 0);
			auditScroll.Controls.Clear();
			Control[] controlItems2 = controlItems;
			foreach (Control control in controlItems2)
			{
				control.Dispose();
			}
			int sy = 2;
			sy = RenderAuditGroup(Lang.T("audit.sec.capability"), report.Capability, sy);
			sy = RenderAuditGroup(Lang.T("audit.sec.machine"), report.Machine, sy);
			sy = RenderAuditGroup(Lang.T("audit.sec.persistent"), report.Persistent, sy);
			sy = RenderAuditGroup(Lang.T("audit.sec.verdicts"), report.Verdicts, sy);
			CardLabel(auditScroll, Lang.T("audit.footer"), 8, sy + 4, AuditContentWidth - 16, 34, 7.6f, false, Theme.Faint);
			auditScroll.ResumeLayout();
			auditScroll.PerformLayout();
			LayoutAuditViewport();
			auditScroll.AutoScrollPosition = (keepScroll ? new Point(0, -autoScrollPosition.Y) : Point.Empty);
			auditRendered = true;
		}
		private int RenderAuditGroup(string title, List<AuditRow> rows, int sy)
		{
			Section(auditScroll, title, 6, sy);
			sy += 26;
			Dictionary<string, AuditFix> dictionary = BuildAuditFixes();
			foreach (AuditRow row in rows)
			{
				AuditFix value = null;
				bool renderAuditGroupReady = false;
				bool renderAuditGroupReady2 = false;
				bool renderAuditGroupReady3 = false;
				if (row.FixKey != null && dictionary.TryGetValue(row.FixKey, out value))
				{
					try
					{
						renderAuditGroupReady = value.CanFix();
						renderAuditGroupReady2 = !renderAuditGroupReady && value.CanRevert();
						renderAuditGroupReady3 = !renderAuditGroupReady && !renderAuditGroupReady2 && value.Open != null;
					}
					catch
					{
					}
				}
				bool matchesRenderAuditGroupReady3 = renderAuditGroupReady || renderAuditGroupReady2 || renderAuditGroupReady3;
				AuditFix boundFix = value;
				Action onClick = (matchesRenderAuditGroupReady3 ? ((Action)delegate
				{
					OnAuditFixClick(boundFix);
				}) : null);
				string btnText = (renderAuditGroupReady3 ? ((value.OpenText == null) ? "" : value.OpenText()) : Lang.T(renderAuditGroupReady ? (value.FixLabel ?? "audit.fix") : "audit.fix.revert"));
				AuditRowCard auditRowCard = new AuditRowCard(row.Name, row.Value, row.Note ?? "", Lang.F("audit.evidence.format", SystemAudit.EvidenceText(row.Evidence)), row.Warn, matchesRenderAuditGroupReady3, btnText, renderAuditGroupReady, onClick, AuditContentWidth);
				auditRowCard.SetBounds(Theme.S(6), Theme.S(sy), Theme.S(AuditContentWidth), Theme.S(auditRowCard.LogicalHeight));
				auditRowCard.Name = "auditRow_" + (row.FixKey ?? "info");
				auditScroll.Controls.Add(auditRowCard);
				sy += auditRowCard.LogicalHeight + 8;
			}
			sy += 8;
			return sy;
		}
		private void ApplyAuditMemoryCompression(bool restore)
		{
			if (restore ? MemCompressTweak.Restore() : MemCompressTweak.EnableCompression())
			{
				PaviseDialog.Info(this, "PAVISE AUTO", Lang.T(restore ? "log.memcompress.6" : "audit.mem.enabled"));
			}
			else
			{
				PaviseDialog.Warn(this, "PAVISE AUTO", Lang.T("audit.mem.failed"));
			}
		}
		private static string IrqReasonText(string key)
		{
			if (!string.IsNullOrEmpty(key))
			{
				return Lang.T(key);
			}
			return "";
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
			case GpuIrqPhase.Clear:
				color = Theme.Green;
				return Lang.T("irqcheck.hint.clear");
			case GpuIrqPhase.NoRegion:
				return Lang.T("irqcheck.hint.noregion");
			case GpuIrqPhase.RolledBack:
				color = Theme.Danger;
				return Lang.F("irqcheck.hint.rolledback", IrqReasonText(state.Reason));
			case GpuIrqPhase.Skipped:
				return Lang.F("irqcheck.hint.skipped", IrqReasonText(state.Reason));
			default:
				return Lang.T("irqcheck.hint.none");
			}
		}
		internal static string IrqBannerState(GpuIrqState state, bool awaiting, out Color color)
		{
			switch (state.Phase)
			{
			case GpuIrqPhase.Pending:
				color = Theme.Warning;
				if (!awaiting)
				{
					return "VERIFY PENDING";
				}
				return "RESTART PENDING";
			case GpuIrqPhase.Applied:
				color = Theme.Green;
				return "ROUTED";
			case GpuIrqPhase.Clear:
				color = Theme.Green;
				return "CLEAR";
			case GpuIrqPhase.NoRegion:
				color = Theme.Dim;
				return "NO GAME CORES";
			case GpuIrqPhase.RolledBack:
				color = Theme.Danger;
				return "ROLLED BACK";
			case GpuIrqPhase.Skipped:
				color = Theme.Dim;
				return "SKIPPED";
			default:
				color = Theme.Accent;
				return "READY";
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
			case GpuIrqPhase.Applied:
				color = Theme.Green;
				return Lang.T("irqcheck.state.applied");
			case GpuIrqPhase.Clear:
				color = Theme.Green;
				return Lang.T("irqcheck.state.clear");
			case GpuIrqPhase.NoRegion:
				color = Theme.Dim;
				return Lang.T("irqcheck.state.noregion");
			case GpuIrqPhase.RolledBack:
				color = Theme.Danger;
				return Lang.T("irqcheck.state.rolledback");
			case GpuIrqPhase.Skipped:
				color = Theme.Dim;
				return IrqReasonText(state.Reason);
			default:
				color = Theme.Dim;
				return Lang.T("irqcheck.state.none");
			}
		}
		internal static string IrqResultText(GpuIrqResult r, out DlgKind kind)
		{
			kind = DlgKind.Info;
			string maskTextResult = ((r.Gpu == null) ? "—" : IrqRelocate.MaskText(r.Gpu.Seen));
			string irqcheckOutFailedText;
			switch (r.Outcome)
			{
			case GpuIrqOutcome.Failed:
				kind = DlgKind.Danger;
				irqcheckOutFailedText = Lang.F("irqcheck.out.failed", r.Detail);
				break;
			case GpuIrqOutcome.AwaitingReboot:
				kind = DlgKind.Warn;
				irqcheckOutFailedText = Lang.T("irqcheck.out.awaiting");
				break;
			case GpuIrqOutcome.NoEvidence:
				kind = DlgKind.Warn;
				irqcheckOutFailedText = Lang.F("irqcheck.out.noevidence", (r.Gpu == null) ? 0 : r.Gpu.Events, 300L);
				break;
			case GpuIrqOutcome.NoRegion:
				irqcheckOutFailedText = Lang.T("irqcheck.out.noregion");
				break;
			case GpuIrqOutcome.Restored:
				kind = DlgKind.Warn;
				irqcheckOutFailedText = Lang.T("irqcheck.out.restored") + ((r.Detail.Length > 0) ? (" · " + r.Detail) : "");
				break;
			case GpuIrqOutcome.NoTarget:
				irqcheckOutFailedText = Lang.T("irqcheck.reason.notarget");
				break;
			case GpuIrqOutcome.Clear:
				kind = DlgKind.Success;
				irqcheckOutFailedText = Lang.F("irqcheck.out.clear", maskTextResult, IrqRelocate.MaskText(r.Region));
				break;
			case GpuIrqOutcome.Scheduled:
				kind = DlgKind.Warn;
				irqcheckOutFailedText = Lang.F("irqcheck.out.scheduled", maskTextResult, IrqRelocate.MaskText(r.Region), IrqRelocate.MaskText(r.Target));
				break;
			case GpuIrqOutcome.Verified:
				kind = DlgKind.Success;
				irqcheckOutFailedText = Lang.F("irqcheck.out.verified", maskTextResult);
				break;
			case GpuIrqOutcome.RolledBack:
				kind = DlgKind.Danger;
				irqcheckOutFailedText = Lang.F("irqcheck.out.rolledback", r.Detail);
				break;
			case GpuIrqOutcome.Skipped:
				irqcheckOutFailedText = Lang.F("irqcheck.out.skipped", IrqReasonText(GpuIrqCheck.LoadState().Reason));
				break;
			default:
				irqcheckOutFailedText = Lang.T("irqcheck.out.cancelled");
				break;
			}
			if (r.Outcome == GpuIrqOutcome.Failed || r.Outcome == GpuIrqOutcome.AwaitingReboot || r.Outcome == GpuIrqOutcome.Cancelled)
			{
				return irqcheckOutFailedText;
			}
			if (r.Slow == null || r.Slow.Count == 0)
			{
				return irqcheckOutFailedText + "\r\n\r\n" + Lang.T("irqcheck.out.noslow");
			}
			StringBuilder stringBuilder = new StringBuilder(irqcheckOutFailedText);
			stringBuilder.Append("\r\n\r\n").Append(Lang.T("irqcheck.out.slow"));
			foreach (SlowDriver item in r.Slow)
			{
				stringBuilder.Append("\r\n· ").Append((item.Device.Length > 0) ? (item.Device + " (" + item.Driver + ")") : item.Driver).Append("  ")
					.Append(Lang.F("irqcheck.slow.row", item.MaxUs.ToString("F0"), item.P99Us.ToString("F0")));
			}
			return stringBuilder.ToString();
		}
		private GpuIrqResult RunIrqCheck()
		{
			if (!Native.IsElevated())
			{
				PaviseDialog.Warn(this, Lang.T("nav.irq"), Lang.T("irqcheck.needadmin"));
				return null;
			}
			if (gameMode.IsActive)
			{
				PaviseDialog.Warn(this, Lang.T("nav.irq"), Lang.T("irqcheck.ingame"));
				return null;
			}
			if (GpuIrqCheck.AwaitingReboot(GpuIrqCheck.LoadState()))
			{
				PaviseDialog.Info(this, Lang.T("nav.irq"), Lang.T("irqcheck.out.awaiting"));
				return null;
			}
			GpuIrqResult result;
			using (IrqCheckDialog irqCheckDialog = new IrqCheckDialog((Func<bool> cancelled, Action<float> progress) => GpuIrqCheck.Run(() => cancelled() || gameMode.IsActive, progress)))
			{
				irqCheckDialog.ShowDialog(this);
				result = irqCheckDialog.Result;
			}
			if (result == null)
			{
				return null;
			}
			DlgKind kind;
			string body = IrqResultText(result, out kind);
			switch (kind)
			{
			case DlgKind.Success:
				PaviseDialog.Success(this, Lang.T("irqcheck.dialog.title"), body);
				break;
			case DlgKind.Danger:
				PaviseDialog.Error(this, Lang.T("irqcheck.dialog.title"), body);
				break;
			case DlgKind.Warn:
				PaviseDialog.Warn(this, Lang.T("irqcheck.dialog.title"), body);
				break;
			default:
				PaviseDialog.Info(this, Lang.T("irqcheck.dialog.title"), body);
				break;
			}
			return result;
		}
	}
}
