using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
	internal sealed class AuditRowCard : FxControl
	{
		private const int PadL = 16;

		private const int PadR = 16;

		private const int BtnW = 120;

		private const int BtnH = 32;

		private const int RightCol = 132;

		private const int NoteMaxLines = 3;

		private const float NameSize = 9.5f;

		private const float NoteSize = 8f;

		private const float MetaSize = 7.4f;

		private readonly string name;

		private readonly string value;

		private readonly string note;

		private readonly string evidence;

		private readonly bool warn;

		private readonly bool hasButton;

		private readonly string btnText;

		private readonly bool btnPrimary;

		private readonly Action onClick;

		private Rectangle btnRect;

		private Motion btnHover;

		private Motion btnPress;

		private bool btnDown;

		public int LogicalHeight { get; private set; }

		public AuditRowCard(string name, string value, string note, string evidence, bool warn, bool hasButton, string btnText, bool btnPrimary, Action onClick, int logicalWidth)
		{
			this.name = name ?? "";
			this.value = value ?? "";
			this.note = note ?? "";
			this.evidence = evidence ?? "";
			this.warn = warn;
			this.hasButton = hasButton;
			this.btnText = btnText ?? "";
			this.btnPrimary = btnPrimary;
			this.onClick = onClick;
			Bg = Theme.Bg;
			Cursor = Cursors.Default;
			btnHover.Speed = 0.3f;
			btnPress.Speed = 0.42f;
			LogicalHeight = ComputeHeight(logicalWidth);
		}

		private int ComputeHeight(int logicalWidth)
		{
			int heightValue = logicalWidth - 16 - 16 - (hasButton ? 132 : 0);
			if (heightValue < 40)
			{
				heightValue = 40;
			}
			int height = TextRenderer.MeasureText("Ag", Theme.UI(8f, false)).Height;
			if (height <= 0)
			{
				height = Theme.S(15);
			}
			int height2 = ((note.Length != 0) ? TextRenderer.MeasureText(note, Theme.UI(8f, false), new Size(Theme.S(heightValue), int.MaxValue), TextFormatFlags.WordBreak).Height : 0);
			int heightRatio = ((height <= 0) ? 1 : ((height2 + height - 1) / height));
			if (heightRatio < 1)
			{
				heightRatio = 1;
			}
			if (heightRatio > 3)
			{
				heightRatio = 3;
			}
			int roundedValue = (int)Math.Ceiling((float)(heightRatio * height) / Dpi.Scale);
			int val = 40 + roundedValue + 12;
			int val2 = (hasButton ? 78 : 66);
			return Math.Max(val2, val);
		}

		internal void SetLogicalWidth(int width)
		{
			if (base.Width != Theme.S(width) || base.Height != Theme.S(LogicalHeight))
			{
				LogicalHeight = ComputeHeight(width);
				base.Size = new Size(Theme.S(width), Theme.S(LogicalHeight));
			}
		}

		protected override void OnMouseMove(MouseEventArgs e)
		{
			base.OnMouseMove(e);
			bool matchesHasButton = hasButton && btnRect.Contains(e.Location);
			Cursor = (matchesHasButton ? Cursors.Hand : Cursors.Default);
			if (matchesHasButton != btnHover.Target > 0.5f)
			{
				btnHover.To(matchesHasButton ? 1f : 0f);
				UiClock.Wake();
			}
		}

		protected override void OnMouseLeave(EventArgs e)
		{
			base.OnMouseLeave(e);
			btnDown = false;
			btnPress.To(0f);
			btnHover.To(0f);
			UiClock.Wake();
		}

		protected override void OnMouseDown(MouseEventArgs e)
		{
			base.OnMouseDown(e);
			if (hasButton && e.Button == MouseButtons.Left && btnRect.Contains(e.Location))
			{
				btnDown = true;
				btnPress.Set(1f);
				UiClock.Wake();
				Invalidate();
			}
		}

		protected override void OnMouseUp(MouseEventArgs e)
		{
			base.OnMouseUp(e);
			bool btnDownSnapshot = btnDown;
			btnDown = false;
			btnPress.To(0f);
			UiClock.Wake();
			if (btnDownSnapshot && e.Button == MouseButtons.Left && btnRect.Contains(e.Location) && onClick != null)
			{
				onClick();
			}
		}

		protected override bool StepAll()
		{
			bool stepAllSucceeded = base.StepAll();
			bool stepSucceeded = btnHover.Step();
			bool result = btnPress.Step();
			if (!stepAllSucceeded && !stepSucceeded)
			{
				return result;
			}
			return true;
		}

		protected override void OnPaint(PaintEventArgs e)
		{
			Graphics graphics = e.Graphics;
			FillBg(graphics);
			graphics.SmoothingMode = SmoothingMode.AntiAlias;
			float value2 = hover.Value;
			Rectangle r = new Rectangle(0, 0, base.Width - 1, base.Height - 1);
			using (GraphicsPath path = Theme.TechPath(r, Theme.S(9)))
			{
				using (SolidBrush brush = new SolidBrush(Backdrop.CardFill(this, Col.Lerp(Theme.Card, Theme.CardHover, value2 * 0.5f))))
				{
					graphics.FillPath(brush, path);
				}
				using (Pen pen = new Pen(Col.Lerp(Theme.Stroke, warn ? Col.Alpha(Theme.Accent, 150) : Theme.StrokeHi, warn ? (0.55f + value2 * 0.35f) : value2)))
				{
					graphics.DrawPath(pen, path);
				}
			}
			using (Pen pen2 = new Pen(warn ? Theme.Accent : Col.Lerp(Theme.Stroke, Theme.Accent, value2 * 0.6f), Math.Max(1f, Theme.S(2))))
			{
				graphics.DrawLine(pen2, 0, Theme.S(12), 0, base.Height - Theme.S(13));
			}
			int upperBound = ((evidence.Length > 0) ? Math.Min(base.Width / 3, TextRenderer.MeasureText(graphics, evidence, Theme.UI(7.4f, false), Size.Empty, TextFormatFlags.SingleLine).Width + Theme.S(8)) : 0);
			int scaledDimension = (hasButton ? Theme.S(132) : 0);
			int remainingMax = base.Width - Theme.S(16) - Math.Max(upperBound, scaledDimension);
			int remainingScaledDimension = base.Width - Theme.S(16) - scaledDimension;
			int width = TextRenderer.MeasureText(graphics, name, Theme.UI(9.5f, true)).Width;
			Rectangle bounds = new Rectangle(Theme.S(16), Theme.S(11), Math.Min(width + Theme.S(4), remainingMax - Theme.S(16)), Theme.S(22));
			TextRenderer.DrawText(graphics, name, Theme.UI(9.5f, true), bounds, Theme.Fg, TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
			DrawValueBadge(graphics, bounds.Right + Theme.S(8), Theme.S(13), remainingMax);
			if (upperBound > 0)
			{
				TextRenderer.DrawText(bounds: new Rectangle(base.Width - Theme.S(16) - upperBound, Theme.S(12), upperBound, Theme.S(18)), dc: graphics, text: evidence, font: Theme.UI(7.4f, false), foreColor: Theme.Faint, flags: TextFormatFlags.EndEllipsis | TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
			}
			if (note.Length > 0)
			{
				int remainingRemainingScaledDimension = remainingScaledDimension - Theme.S(16);
				if (remainingRemainingScaledDimension < Theme.S(40))
				{
					remainingRemainingScaledDimension = Theme.S(40);
				}
				int height = TextRenderer.MeasureText(graphics, "Ag", Theme.UI(8f, false)).Height;
				int scaledDimension2 = Theme.S(39);
				int val = Math.Max(height, base.Height - scaledDimension2 - Theme.S(10));
				if (height > 0)
				{
					val = Math.Min(val, height * 3);
				}
				TextRenderer.DrawText(bounds: new Rectangle(Theme.S(16), scaledDimension2, remainingRemainingScaledDimension, val), dc: graphics, text: note, font: Theme.UI(8f, false), foreColor: Theme.Dim, flags: TextFormatFlags.EndEllipsis | TextFormatFlags.WordBreak);
			}
			if (hasButton)
			{
				DrawButton(graphics);
			}
		}

		private void DrawValueBadge(Graphics g, int x, int y, int rightLimit)
		{
			if (value.Length == 0)
			{
				return;
			}
			Font font = Theme.UI(8f, true);
			if (warn)
			{
				int width = TextRenderer.MeasureText(g, value, font).Width;
				int widthOffset = width + Theme.S(16);
				if (x + widthOffset > rightLimit)
				{
					widthOffset = Math.Max(Theme.S(20), rightLimit - x);
				}
				Rectangle rectangle = new Rectangle(x, y, widthOffset, Theme.S(18));
				using (GraphicsPath path = Theme.TechPath(rectangle, Theme.S(4)))
				{
					using (SolidBrush brush = new SolidBrush(Col.Alpha(Theme.Accent, 235)))
					{
						g.FillPath(brush, path);
					}
					using (Pen pen = new Pen(Theme.Accent))
					{
						g.DrawPath(pen, path);
					}
				}
				TextRenderer.DrawText(g, value, font, rectangle, Theme.OnAccent, TextFormatFlags.EndEllipsis | TextFormatFlags.HorizontalCenter | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter);
			}
			else
			{
				TextRenderer.DrawText(bounds: new Rectangle(x, y - Theme.S(1), rightLimit - x, Theme.S(20)), dc: g, text: value, font: font, foreColor: Theme.Green, flags: TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
			}
		}

		private void DrawButton(Graphics g)
		{
			int scaledDimension = Theme.S(120);
			int scaledDimension2 = Theme.S(32);
			int sOffset = (base.Height - scaledDimension2) / 2 + Theme.S(4);
			int scaledDimension3 = Theme.S(36);
			if (evidence.Length > 0 && sOffset < scaledDimension3)
			{
				sOffset = scaledDimension3;
			}
			btnRect = new Rectangle(base.Width - Theme.S(16) - scaledDimension, sOffset, scaledDimension, scaledDimension2);
			float value2 = btnHover.Value;
			int sLimit = Math.Max(Theme.S(6), scaledDimension2 / 4);
			using (GraphicsPath path = Theme.TechPath(btnRect, sLimit))
			{
				if (btnPrimary)
				{
					using (LinearGradientBrush brush = new LinearGradientBrush(btnRect, Col.Lerp(Theme.Accent, Color.White, value2 * 0.14f), Col.Lerp(Theme.Accent2, Color.White, value2 * 0.14f), LinearGradientMode.Horizontal))
					{
						g.FillPath(brush, path);
					}
					using (Pen pen = new Pen(Col.Alpha(Color.White, 80)))
					{
						g.DrawLine(pen, btnRect.X + Theme.S(2), btnRect.Y + Theme.S(1), btnRect.Right - sLimit - Theme.S(1), btnRect.Y + Theme.S(1));
					}
				}
				else
				{
					Color color = Col.Lerp(Theme.CardHover, Color.White, 0.02f + value2 * 0.05f);
					Color color2 = Col.Lerp(Theme.Card, Color.Black, 0.05f);
					using (LinearGradientBrush brush2 = new LinearGradientBrush(btnRect, color, color2, LinearGradientMode.Vertical))
					{
						g.FillPath(brush2, path);
					}
					using (Pen pen2 = new Pen(Col.Lerp(Theme.Stroke, Theme.Accent, 0.3f + value2 * 0.5f)))
					{
						g.DrawPath(pen2, path);
					}
				}
				if (btnPress.Value > 0.01f)
				{
					using (SolidBrush brush3 = new SolidBrush(Col.Alpha(Color.Black, (int)(50f * btnPress.Value))))
					{
						g.FillPath(brush3, path);
					}
				}
			}
			Color foreColor = (btnPrimary ? Theme.OnAccent : Col.Lerp(Theme.Fg, Theme.Accent, value2 * 0.5f));
			TextRenderer.DrawText(g, btnText, Theme.UI(8.75f, btnPrimary), btnRect, foreColor, TextFormatFlags.EndEllipsis | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
		}
	}
}
