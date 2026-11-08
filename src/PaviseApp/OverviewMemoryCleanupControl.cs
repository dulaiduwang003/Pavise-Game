using System;
using System.Drawing;
using System.Windows.Forms;

namespace PaviseApp
{
	internal sealed class OverviewMemoryCleanupControl : Control
	{
		internal readonly MemoryCleanupButton CleanButton;

		private readonly ToolTip tips = new ToolTip();

		private string status;
		private bool gameProtected;
		internal void SetGamePolicy(bool protect)
		{
			if (gameProtected == protect) return;
			gameProtected = protect;
			CleanButton.AccessibleDescription = protect ? ManualMemoryText.GameHint : ManualMemoryText.Hint;
			tips.SetToolTip(CleanButton, CleanButton.AccessibleDescription);
			if (!CleanButton.Busy)
			{
				CleanButton.Text = protect ? ManualMemoryText.GameButton : ManualMemoryText.Button;
				SetStatus(protect ? ManualMemoryText.GameHint : ManualMemoryText.Hint);
			}
		}

		internal Rectangle StatusBounds;

		internal string StatusText
		{
			get
			{
				return status;
			}
		}

		internal event EventHandler GeometryChanged;

		internal event EventHandler CleanRequested;

		internal OverviewMemoryCleanupControl(bool elevated)
		{
			SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
			base.Tag = "workspace-canvas";
			BackColor = Theme.Bg;
			base.TabStop = false;
			CleanButton = new MemoryCleanupButton
			{
				Name = "overviewManualMemoryClean",
				Tag = "workspace-canvas",
				AccessibleName = ManualMemoryText.Title,
				AccessibleDescription = ManualMemoryText.Hint,
				Enabled = elevated
			};
			MemoryCleanupButton cleanButton = CleanButton;
			EventHandler value = delegate
			{
				if (this.CleanRequested != null)
				{
					this.CleanRequested(this, EventArgs.Empty);
				}
			};
			cleanButton.Click += value;
			base.Controls.Add(CleanButton);
			tips.SetToolTip(CleanButton, ManualMemoryText.Hint);
			SetStatus(elevated ? ManualMemoryText.Hint : ManualMemoryText.Permission);
		}

		internal int MeasureHeight(int width)
		{
			int textHeightResult = OverviewHudDrawing.TextHeight(status, Theme.UI(8f, false), Math.Max(1, width - Theme.S(2)));
			return Theme.S(40) + textHeightResult;
		}

		private void SetStatus(string value)
		{
			int measureHeightResult = MeasureHeight(base.Width);
			status = value ?? "";
			base.AccessibleDescription = status;
			Arrange();
			Invalidate();
			if (measureHeightResult != MeasureHeight(base.Width) && this.GeometryChanged != null)
			{
				this.GeometryChanged(this, EventArgs.Empty);
			}
		}

		private void Arrange()
		{
			CleanButton.SetBounds(0, 0, Math.Max(1, base.Width - Theme.S(2)), Theme.S(32));
			StatusBounds = new Rectangle(0, Theme.S(38), Math.Max(1, base.Width - Theme.S(2)), OverviewHudDrawing.TextHeight(status, Theme.UI(8f, false), Math.Max(1, base.Width - Theme.S(2))));
		}

		internal void ShowProgress(int number, ManualMemoryStep step)
		{
			CleanButton.Busy = true;
			if (step == ManualMemoryStep.PaviseMemory) SetGamePolicy(true);
			CleanButton.Text = ManualMemoryText.T("清理中…", "Cleaning…", "解放中…");
			SetStatus(number + "/" + (step == ManualMemoryStep.PaviseMemory ? 1 : ManualMemoryCleaner.OrderedSteps.Length) + " " + ManualMemoryText.Stage(step));
		}

		internal void ShowReport(ManualMemoryReport report)
		{
			CleanButton.Busy = false;
			SetGamePolicy(report.GameProtected);
			CleanButton.Text = gameProtected ? ManualMemoryText.GameButton : ManualMemoryText.Button;
			SetStatus(ManualMemoryText.Summary(report));
		}

		internal void ShowError(string message)
		{
			CleanButton.Busy = false;
			CleanButton.Text = gameProtected ? ManualMemoryText.GameButton : ManualMemoryText.Button;
			SetStatus(message);
		}

		protected override void OnSizeChanged(EventArgs e)
		{
			base.OnSizeChanged(e);
			if (CleanButton != null)
			{
				Arrange();
			}
		}

		protected override void OnPaintBackground(PaintEventArgs e)
		{
			WorkspaceCanvasSurface.Paint(e.Graphics, this, e.ClipRectangle);
		}

		protected override void OnPaint(PaintEventArgs e)
		{
			base.OnPaint(e);
			OverviewHudDrawing.Text(e.Graphics, status, Theme.UI(8f, false), StatusBounds, Theme.Dim);
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				tips.Dispose();
			}
			base.Dispose(disposing);
		}
	}
}
