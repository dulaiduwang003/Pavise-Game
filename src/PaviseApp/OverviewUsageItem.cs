using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
	internal sealed class OverviewUsageItem : Control
	{
		private OverviewMemoryCleanupControl memoryAction;

		internal string Caption = "";

		internal string Value = "--";

		internal string Detail = "";

		internal double? Percent;

		internal Rectangle CaptionBounds;

		internal Rectangle ValueBounds;

		internal Rectangle DetailBounds;

		internal Rectangle GaugeBounds;

		private bool compact;

		private float valuePointSize = 15f;

		internal float ValuePointSize
		{
			get
			{
				return valuePointSize;
			}
		}

		internal bool Compact
		{
			get
			{
				return compact;
			}
			set
			{
				if (compact != value)
				{
					compact = value;
					Arrange();
					Invalidate();
				}
			}
		}

		internal void AttachMemoryAction(OverviewMemoryCleanupControl action)
		{
			memoryAction = action;
			base.Controls.Add(action);
			action.GeometryChanged += delegate
			{
				Arrange();
			};
			Arrange();
		}

		internal OverviewUsageItem()
		{
			SetStyle(ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
			BackColor = Theme.Bg;
			base.TabStop = false;
			base.AccessibleRole = AccessibleRole.StaticText;
		}

		internal void SetReading(string caption, string value, string detail, double? percent)
		{
			caption = caption ?? "";
			detail = detail ?? "";
			if (percent.HasValue && (double.IsNaN(percent.Value) || double.IsInfinity(percent.Value)))
			{
				percent = null;
			}
			if (percent.HasValue)
			{
				percent = Math.Max(0.0, Math.Min(100.0, percent.Value));
			}
			value = (percent.HasValue ? (value ?? "--") : "--");
			SetContent(caption, value, detail, percent);
		}

		internal void SetStatus(string caption, string status)
		{
			SetContent(caption ?? "", status ?? "--", "", null);
		}

		private void SetContent(string caption, string value, string detail, double? percent)
		{
			if (!(Caption == caption) || !(Value == value) || !(Detail == detail) || Percent != percent)
			{
				bool anyConditionMet = Caption != caption || Value != value || Detail != detail;
				Caption = caption;
				Value = value;
				Detail = detail;
				Percent = percent;
				base.AccessibleName = Caption;
				base.AccessibleDescription = Value + ((Detail.Length == 0) ? "" : ("\r\n" + Detail));
				if (anyConditionMet)
				{
					Arrange();
				}
				Invalidate();
			}
		}

		internal int MeasureHeight(int width)
		{
			Rectangle caption;
			Rectangle value;
			Rectangle detail;
			Rectangle gauge;
			float pointSize;
			return Geometry(width, out caption, out value, out detail, out gauge, out pointSize);
		}

		private int Geometry(int width, out Rectangle caption, out Rectangle value, out Rectangle detail, out Rectangle gauge, out float pointSize)
		{
			width = Math.Max(1, width);
			int scaledDimension = Theme.S(2);
			int lowerBound = Math.Max(1, width - scaledDimension * 2);
			Font font = Theme.UI(Compact ? 8.5f : 9f, false);
			Font font2 = Theme.UI(Compact ? 9f : 9.5f, false);
			int scaledDimension2 = Theme.S(4);
			int upperBound = ((Detail.Length != 0) ? Math.Min(lowerBound / 3, TextRenderer.MeasureText(Detail, font2, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width + Theme.S(4)) : 0);
			int lowerBound2 = Math.Max(1, lowerBound - ((upperBound != 0) ? (upperBound + Theme.S(8)) : 0));
			pointSize = (Compact ? 14f : 15f);
			while (pointSize > 13.5f && TextRenderer.MeasureText(Value, Theme.UI(pointSize, true), Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width > lowerBound2)
			{
				pointSize -= 0.5f;
			}
			Font font3 = Theme.UI(pointSize, true);
			int textHeightResult = OverviewHudDrawing.TextHeight(Caption, font, lowerBound);
			int textHeightResult2 = OverviewHudDrawing.TextHeight(Value, font3, lowerBound2);
			int textHeightResult3 = ((Detail.Length != 0) ? OverviewHudDrawing.TextHeight(Detail, font2, Math.Max(1, upperBound)) : 0);
			int textHeightResult4 = OverviewHudDrawing.TextHeight("0", font3, lowerBound2);
			caption = new Rectangle(scaledDimension, scaledDimension2, lowerBound, textHeightResult);
			value = new Rectangle(scaledDimension, caption.Bottom + Theme.S(Compact ? 4 : 6), lowerBound2, textHeightResult2);
			detail = new Rectangle(width - scaledDimension - upperBound, value.Top + Math.Max(0, textHeightResult4 - textHeightResult3 - Theme.S(3)), upperBound, textHeightResult3);
			gauge = new Rectangle(scaledDimension, Math.Max(value.Bottom, detail.Bottom) + Theme.S(Compact ? 10 : 13), lowerBound, Math.Max(1, Theme.S(3)));
			return Math.Max(Theme.S(Compact ? 88 : 96), gauge.Bottom + Theme.S(21) + ((memoryAction != null) ? (memoryAction.MeasureHeight(lowerBound) + Theme.S(4)) : 0));
		}

		private void Arrange()
		{
			Geometry(base.Width, out CaptionBounds, out ValueBounds, out DetailBounds, out GaugeBounds, out valuePointSize);
			if (memoryAction != null)
			{
				memoryAction.SetBounds(Theme.S(2), GaugeBounds.Bottom + Theme.S(21), Math.Max(1, base.Width - Theme.S(4)), memoryAction.MeasureHeight(Math.Max(1, base.Width - Theme.S(4))));
			}
		}

		protected override void OnSizeChanged(EventArgs e)
		{
			base.OnSizeChanged(e);
			Arrange();
		}

		protected override void OnPaintBackground(PaintEventArgs e)
		{
			WorkspaceCanvasSurface.Paint(e.Graphics, this, e.ClipRectangle);
		}

		protected override void OnPaint(PaintEventArgs e)
		{
			base.OnPaint(e);
			Graphics graphics = e.Graphics;
			OverviewHudDrawing.Text(graphics, Caption, Theme.UI(Compact ? 8.5f : 9f, false), CaptionBounds, Theme.Dim);
			OverviewHudDrawing.Text(graphics, Value, Theme.UI(valuePointSize, true), ValueBounds, Theme.Fg);
			if (DetailBounds.Width > 0 && DetailBounds.Height > 0)
			{
				TextRenderer.DrawText(graphics, Detail, Theme.UI(Compact ? 9f : 9.5f, false), DetailBounds, Theme.Dim, TextFormatFlags.NoPrefix | TextFormatFlags.Right | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
			}
			GraphicsState gstate = graphics.Save();
			try
			{
				graphics.SmoothingMode = SmoothingMode.AntiAlias;
				using (GraphicsPath path = Theme.Rounded(GaugeBounds, GaugeBounds.Height / 2))
				{
					using (SolidBrush brush = new SolidBrush(Col.Lerp(Theme.Bg, Theme.TrackOff, Theme.LightMode ? 0.85f : 0.65f)))
					{
						graphics.FillPath(brush, path);
					}
				}
				if (Percent.HasValue && Percent.Value > 0.0 && GaugeBounds.Width > 0)
				{
					float leftOffset = (float)GaugeBounds.Left + (float)(GaugeBounds.Width - 1) * (float)(Percent.Value / 100.0);
					Pen pen = new Pen(Theme.Accent, GaugeBounds.Height);
					pen.StartCap = LineCap.Round;
					pen.EndCap = LineCap.Round;
					using (Pen pen2 = pen)
					{
						graphics.DrawLine(pen2, (float)GaugeBounds.Left + (float)GaugeBounds.Height / 2f, (float)GaugeBounds.Top + (float)GaugeBounds.Height / 2f, Math.Max((float)GaugeBounds.Left + (float)GaugeBounds.Height / 2f, leftOffset), (float)GaugeBounds.Top + (float)GaugeBounds.Height / 2f);
					}
					float width = Math.Max(2f, Theme.S(5));
					using (SolidBrush brush2 = new SolidBrush(Theme.LightMode ? Theme.Accent : Theme.Fg))
					{
						graphics.FillEllipse(brush2, leftOffset - width / 2f, (float)GaugeBounds.Top + (float)GaugeBounds.Height / 2f - width / 2f, width, width);
					}
				}
				using (Pen pen3 = new Pen(Col.Alpha(Theme.Faint, Theme.LightMode ? 110 : 75), Math.Max(1, Theme.S(1))))
				{
					for (int i = 0; i <= 10; i++)
					{
						float left = (float)GaugeBounds.Left + (float)((GaugeBounds.Width - 1) * i) / 10f;
						float top = GaugeBounds.Bottom + Theme.S(8);
						graphics.DrawLine(pen3, left, top, left, top + (float)Theme.S((i % 5 == 0) ? 5 : 3));
					}
				}
			}
			finally
			{
				graphics.Restore(gstate);
			}
		}
	}
}
