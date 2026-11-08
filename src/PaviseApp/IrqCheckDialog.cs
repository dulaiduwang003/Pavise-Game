using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Threading;
using System.Windows.Forms;

namespace PaviseApp
{
	internal sealed class IrqCheckDialog : Form
	{
		private const int DlgW = 540;

		private const int PadX = 26;

		private const int ScanTop = 78;

		private const int ScanH = 320;

		private const int BtnH = 34;

		private const int BottomPad = 22;

		private readonly ScanView scan;

		private readonly Label hint;

		private readonly PillButton cancel;

		private readonly Func<Func<bool>, Action<float>, GpuIrqResult> work;

		private volatile bool cancelRequested;

		private bool running;

		private int lastSeconds = -1;

		public GpuIrqResult Result { get; private set; }

		public IrqCheckDialog(Func<Func<bool>, Action<float>, GpuIrqResult> work)
		{
			this.work = work;
			base.FormBorderStyle = FormBorderStyle.None;
			base.StartPosition = FormStartPosition.CenterParent;
			base.MaximizeBox = false;
			base.MinimizeBox = false;
			base.ShowInTaskbar = false;
			BackColor = Theme.Bg;
			ForeColor = Theme.Fg;
			Font = Theme.UI(9.5f, false);
			DoubleBuffered = true;
			SetStyle(ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
			base.KeyPreview = true;
			int scaledDimension = Theme.S(540);
			int ctorValue = 410;
			int scaledDimension2 = Theme.S(ctorValue + 44 + 34 + 22 + 14);
			base.ClientSize = new Size(scaledDimension, scaledDimension2);
			scan = new ScanView();
			scan.Name = "irqCheckScan";
			scan.SetBounds(Theme.S(26), Theme.S(78), scaledDimension - Theme.S(26) * 2, Theme.S(320));
			base.Controls.Add(scan);
			hint = new Label();
			hint.Name = "irqCheckHint";
			hint.Text = Lang.F("irqcheck.dialog.hint", 120);
			hint.ForeColor = Theme.Warning;
			hint.BackColor = Color.Transparent;
			hint.Font = Theme.UI(9.5f, true);
			hint.TextAlign = ContentAlignment.MiddleCenter;
			hint.UseCompatibleTextRendering = false;
			hint.SetBounds(Theme.S(26), Theme.S(ctorValue), scaledDimension - Theme.S(26) * 2, Theme.S(40));
			base.Controls.Add(hint);
			cancel = new PillButton(Lang.T("dlg.cancel"), BtnKind.Normal);
			cancel.Name = "irqCheckCancel";
			cancel.Bg = Theme.Card;
			cancel.Size = new Size(Theme.S(108), Theme.S(34));
			cancel.Location = new Point(scaledDimension - Theme.S(26) - cancel.Width, scaledDimension2 - Theme.S(22) - cancel.Height);
			PillButton pillButton = cancel;
			EventHandler value = delegate
			{
				RequestCancel();
			};
			pillButton.Click += value;
			base.Controls.Add(cancel);
			base.KeyDown += delegate(object s, KeyEventArgs e)
			{
				if (e.KeyCode == Keys.Escape)
				{
					RequestCancel();
				}
			};
		}

		protected override void OnShown(EventArgs e)
		{
			base.OnShown(e);
			Fx.EnterForm(this);
			scan.BeginScan(Lang.F("irqcheck.dialog.caption", 120));
			running = true;
			Thread thread = new Thread(Run);
			thread.IsBackground = true;
			thread.Priority = ThreadPriority.BelowNormal;
			thread.Start();
		}

		private void Run()
		{
			GpuIrqResult result;
			try
			{
				result = work(() => cancelRequested, OnProgress);
			}
			catch (Exception ex)
			{
				result = new GpuIrqResult
				{
					Outcome = GpuIrqOutcome.Failed,
					Detail = ex.GetType().Name
				};
			}
			try
			{
				BeginInvoke((MethodInvoker)delegate
				{
					Finish(result);
				});
			}
			catch
			{
			}
		}

		private void OnProgress(float value)
		{
			int left = (int)Math.Ceiling(120f * (1f - value));
			if (left < 0)
			{
				left = 0;
			}
			try
			{
				BeginInvoke((MethodInvoker)delegate
				{
					if (!base.IsDisposed)
					{
						string irqcheckDialogStoppingText = (cancelRequested ? Lang.T("irqcheck.dialog.stopping") : ((value >= 1f) ? Lang.T("irqcheck.dialog.analyzing") : ((left != lastSeconds) ? Lang.F("irqcheck.dialog.caption", left) : null)));
						lastSeconds = left;
						scan.ReportProgress(value, irqcheckDialogStoppingText);
					}
				});
			}
			catch
			{
			}
		}

		private void Finish(GpuIrqResult result)
		{
			running = false;
			Result = result;
			scan.Stop();
			base.DialogResult = ((result == null || result.Outcome != GpuIrqOutcome.Cancelled) ? DialogResult.OK : DialogResult.Cancel);
			Close();
		}

		private void RequestCancel()
		{
			if (running && !cancelRequested)
			{
				cancelRequested = true;
				cancel.Enabled = false;
				scan.ReportProgress(0f, Lang.T("irqcheck.dialog.stopping"));
			}
		}

		protected override void OnFormClosing(FormClosingEventArgs e)
		{
			if (running)
			{
				RequestCancel();
				e.Cancel = true;
			}
			else
			{
				base.OnFormClosing(e);
			}
		}

		protected override void OnPaint(PaintEventArgs e)
		{
			Graphics graphics = e.Graphics;
			graphics.SmoothingMode = SmoothingMode.AntiAlias;
			graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
			Color accent = Theme.Accent;
			using (SolidBrush brush = new SolidBrush(Theme.Bg))
			{
				graphics.FillRectangle(brush, new Rectangle(0, 0, base.Width, base.Height));
			}
			using (SolidBrush brush2 = new SolidBrush(accent))
			{
				graphics.FillRectangle(brush2, 0, 0, base.Width, Theme.S(4));
			}
			int scaledDimension = Theme.S(9);
			int scaledDimension2 = Theme.S(17);
			int scaledDimension3 = Theme.S(16);
			int scaledDimension4 = Theme.S(92);
			int left = Theme.S(26);
			Point[] points = new Point[4]
			{
				new Point(left + scaledDimension, scaledDimension2),
				new Point(left + scaledDimension4 + scaledDimension, scaledDimension2),
				new Point(left + scaledDimension4 - scaledDimension, scaledDimension2 + scaledDimension3),
				new Point(left - scaledDimension, scaledDimension2 + scaledDimension3)
			};
			using (SolidBrush brush3 = new SolidBrush(Color.FromArgb(38, accent)))
			{
				graphics.FillPolygon(brush3, points);
			}
			using (Pen pen = new Pen(Color.FromArgb(120, accent), Math.Max(1f, Theme.S(1))))
			{
				graphics.DrawPolygon(pen, points);
			}
			using (SolidBrush brush4 = new SolidBrush(accent))
			{
				StringFormat stringFormat = new StringFormat();
				stringFormat.Alignment = StringAlignment.Center;
				stringFormat.LineAlignment = StringAlignment.Center;
				using (StringFormat format = stringFormat)
				{
					graphics.DrawString("IRQ // CHECK", Theme.Mono(6.4f), brush4, new RectangleF(left, scaledDimension2, scaledDimension4, scaledDimension3), format);
				}
			}
			using (SolidBrush brush5 = new SolidBrush(Theme.Fg))
			{
				graphics.DrawString(Lang.T("irqcheck.dialog.title"), Theme.UI(12.5f, true), brush5, left - Theme.S(2), Theme.S(43));
			}
			int top = base.ClientSize.Height - Theme.S(22) - Theme.S(34) - Theme.S(14);
			using (Pen pen2 = new Pen(Theme.Stroke))
			{
				graphics.DrawLine(pen2, left, top, base.Width - left, top);
			}
			using (Pen pen3 = new Pen(Theme.StrokeHi))
			{
				graphics.DrawRectangle(pen3, 0, 0, base.Width - 1, base.Height - 1);
			}
		}
	}
}
