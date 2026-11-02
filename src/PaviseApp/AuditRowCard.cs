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
			int num = logicalWidth - 16 - 16 - (hasButton ? 132 : 0);
			if (num < 40)
			{
				num = 40;
			}
			int num2 = TextRenderer.MeasureText("Ag", Theme.UI(8f, false)).Height;
			if (num2 <= 0)
			{
				num2 = Theme.S(15);
			}
			int num3 = ((note.Length != 0) ? TextRenderer.MeasureText(note, Theme.UI(8f, false), new Size(Theme.S(num), int.MaxValue), TextFormatFlags.WordBreak).Height : 0);
			int num4 = ((num2 <= 0) ? 1 : ((num3 + num2 - 1) / num2));
			if (num4 < 1)
			{
				num4 = 1;
			}
			if (num4 > 3)
			{
				num4 = 3;
			}
			int num5 = (int)Math.Ceiling((float)(num4 * num2) / Dpi.Scale);
			int val = 40 + num5 + 12;
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
			bool flag = hasButton && btnRect.Contains(e.Location);
			Cursor = (flag ? Cursors.Hand : Cursors.Default);
			if (flag != btnHover.Target > 0.5f)
			{
				btnHover.To(flag ? 1f : 0f);
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
			bool flag = btnDown;
			btnDown = false;
			btnPress.To(0f);
			UiClock.Wake();
			if (flag && e.Button == MouseButtons.Left && btnRect.Contains(e.Location) && onClick != null)
			{
				onClick();
			}
		}

		protected override bool StepAll()
		{
			bool flag = base.StepAll();
			bool flag2 = btnHover.Step();
			bool result = btnPress.Step();
			if (!flag && !flag2)
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
			float num = hover.Value;
			Rectangle r = new Rectangle(0, 0, base.Width - 1, base.Height - 1);
			using (GraphicsPath path = Theme.TechPath(r, Theme.S(9)))
			{
				using (SolidBrush brush = new SolidBrush(Backdrop.CardFill(this, Col.Lerp(Theme.Card, Theme.CardHover, num * 0.5f))))
				{
					graphics.FillPath(brush, path);
				}
				using (Pen pen = new Pen(Col.Lerp(Theme.Stroke, warn ? Col.Alpha(Theme.Accent, 150) : Theme.StrokeHi, warn ? (0.55f + num * 0.35f) : num)))
				{
					graphics.DrawPath(pen, path);
				}
			}
			using (Pen pen2 = new Pen(warn ? Theme.Accent : Col.Lerp(Theme.Stroke, Theme.Accent, num * 0.6f), Math.Max(1f, Theme.S(2))))
			{
				graphics.DrawLine(pen2, 0, Theme.S(12), 0, base.Height - Theme.S(13));
			}
			int num2 = ((evidence.Length > 0) ? Math.Min(base.Width / 3, TextRenderer.MeasureText(graphics, evidence, Theme.UI(7.4f, false), Size.Empty, TextFormatFlags.SingleLine).Width + Theme.S(8)) : 0);
			int num3 = (hasButton ? Theme.S(132) : 0);
			int num4 = base.Width - Theme.S(16) - Math.Max(num2, num3);
			int num5 = base.Width - Theme.S(16) - num3;
			int num6 = TextRenderer.MeasureText(graphics, name, Theme.UI(9.5f, true)).Width;
			Rectangle bounds = new Rectangle(Theme.S(16), Theme.S(11), Math.Min(num6 + Theme.S(4), num4 - Theme.S(16)), Theme.S(22));
			TextRenderer.DrawText(graphics, name, Theme.UI(9.5f, true), bounds, Theme.Fg, TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
			DrawValueBadge(graphics, bounds.Right + Theme.S(8), Theme.S(13), num4);
			if (num2 > 0)
			{
				TextRenderer.DrawText(bounds: new Rectangle(base.Width - Theme.S(16) - num2, Theme.S(12), num2, Theme.S(18)), dc: graphics, text: evidence, font: Theme.UI(7.4f, false), foreColor: Theme.Faint, flags: TextFormatFlags.EndEllipsis | TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
			}
			if (note.Length > 0)
			{
				int num7 = num5 - Theme.S(16);
				if (num7 < Theme.S(40))
				{
					num7 = Theme.S(40);
				}
				int num8 = TextRenderer.MeasureText(graphics, "Ag", Theme.UI(8f, false)).Height;
				int num9 = Theme.S(39);
				int val = Math.Max(num8, base.Height - num9 - Theme.S(10));
				if (num8 > 0)
				{
					val = Math.Min(val, num8 * 3);
				}
				TextRenderer.DrawText(bounds: new Rectangle(Theme.S(16), num9, num7, val), dc: graphics, text: note, font: Theme.UI(8f, false), foreColor: Theme.Dim, flags: TextFormatFlags.EndEllipsis | TextFormatFlags.WordBreak);
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
				int num = TextRenderer.MeasureText(g, value, font).Width;
				int num2 = num + Theme.S(16);
				if (x + num2 > rightLimit)
				{
					num2 = Math.Max(Theme.S(20), rightLimit - x);
				}
				Rectangle rectangle = new Rectangle(x, y, num2, Theme.S(18));
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
			int num = Theme.S(120);
			int num2 = Theme.S(32);
			int num3 = (base.Height - num2) / 2 + Theme.S(4);
			int num4 = Theme.S(36);
			if (evidence.Length > 0 && num3 < num4)
			{
				num3 = num4;
			}
			btnRect = new Rectangle(base.Width - Theme.S(16) - num, num3, num, num2);
			float num5 = btnHover.Value;
			int num6 = Math.Max(Theme.S(6), num2 / 4);
			using (GraphicsPath path = Theme.TechPath(btnRect, num6))
			{
				if (btnPrimary)
				{
					using (LinearGradientBrush brush = new LinearGradientBrush(btnRect, Col.Lerp(Theme.Accent, Color.White, num5 * 0.14f), Col.Lerp(Theme.Accent2, Color.White, num5 * 0.14f), LinearGradientMode.Horizontal))
					{
						g.FillPath(brush, path);
					}
					using (Pen pen = new Pen(Col.Alpha(Color.White, 80)))
					{
						g.DrawLine(pen, btnRect.X + Theme.S(2), btnRect.Y + Theme.S(1), btnRect.Right - num6 - Theme.S(1), btnRect.Y + Theme.S(1));
					}
				}
				else
				{
					Color color = Col.Lerp(Theme.CardHover, Color.White, 0.02f + num5 * 0.05f);
					Color color2 = Col.Lerp(Theme.Card, Color.Black, 0.05f);
					using (LinearGradientBrush brush2 = new LinearGradientBrush(btnRect, color, color2, LinearGradientMode.Vertical))
					{
						g.FillPath(brush2, path);
					}
					using (Pen pen2 = new Pen(Col.Lerp(Theme.Stroke, Theme.Accent, 0.3f + num5 * 0.5f)))
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
			Color foreColor = (btnPrimary ? Theme.OnAccent : Col.Lerp(Theme.Fg, Theme.Accent, num5 * 0.5f));
			TextRenderer.DrawText(g, btnText, Theme.UI(8.75f, btnPrimary), btnRect, foreColor, TextFormatFlags.EndEllipsis | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
		}
	}
}
