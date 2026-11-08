using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
	internal sealed class FamilySuppressionSwitch : CheckBox
	{
		public Color Surface = Theme.Card;

		public bool Gated;

		public string IconKey;

		public int DetailsWidth = 26;

		private readonly bool corePolicy;

		private Motion switchMotion;

		private Motion hoverMotion;

		private bool Locked
		{
			get
			{
				if (Gated)
				{
					return !base.Checked;
				}
				return false;
			}
		}

		protected override void OnHandleCreated(EventArgs e)
		{
			base.OnHandleCreated(e);
			switchMotion.Set(base.Checked ? 1f : 0f);
			UiClock.Frame += Animate;
		}

		protected override void OnHandleDestroyed(EventArgs e)
		{
			UiClock.Frame -= Animate;
			base.OnHandleDestroyed(e);
		}

		protected override void OnCheckedChanged(EventArgs e)
		{
			base.OnCheckedChanged(e);
			if (base.IsHandleCreated && base.Visible && !UiClock.Suspended && !UiClock.Frozen)
			{
				switchMotion.To(base.Checked ? 1f : 0f);
				UiClock.Wake();
			}
			else
			{
				switchMotion.Set(base.Checked ? 1f : 0f);
			}
			Invalidate();
		}

		private void Animate(object sender, EventArgs e)
		{
			if (base.Visible)
			{
				bool stepSucceeded = switchMotion.Step();
				bool stepSucceeded2 = hoverMotion.Step();
				if (stepSucceeded || stepSucceeded2)
				{
					Invalidate();
				}
			}
		}

		public FamilySuppressionSwitch()
			: this(false)
		{
		}

		public FamilySuppressionSwitch(bool isCorePolicy)
		{
			corePolicy = isCorePolicy;
			switchMotion.Speed = 0.28f;
			hoverMotion.Speed = 0.24f;
			base.AutoCheck = false;
			AutoSize = false;
			base.TabStop = true;
			Text = Lang.T(corePolicy ? "nav.corescheduling" : "lib.family.compact");
			Cursor = Cursors.Hand;
			base.AccessibleRole = AccessibleRole.CheckButton;
			SetStyle(ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
		}

		internal static int PreferredWidth(string text, bool isCorePolicy)
		{
			int width = TextRenderer.MeasureText(text, Theme.UI(8f, true), Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width;
			return width + Theme.S(32) + Theme.S(8) + Theme.S(44) + (isCorePolicy ? Theme.S(26) : 0);
		}

		public override Size GetPreferredSize(Size proposedSize)
		{
			return new Size(PreferredWidth(Text, corePolicy) + (corePolicy ? Theme.S(DetailsWidth - 26) : 0), Theme.S(32));
		}

		protected override bool IsInputKey(Keys keyData)
		{
			Keys keys = keyData & Keys.KeyCode;
			if (keys == Keys.Up || keys == Keys.Down || keys == Keys.Home || keys == Keys.End || keys == Keys.Prior || keys == Keys.Next)
			{
				return true;
			}
			return base.IsInputKey(keyData);
		}

		protected override void OnKeyDown(KeyEventArgs e)
		{
			if (e.KeyCode == Keys.Return)
			{
				e.Handled = true;
				e.SuppressKeyPress = true;
				OnClick(EventArgs.Empty);
			}
			base.OnKeyDown(e);
		}

		protected override void OnEnter(EventArgs e)
		{
			base.OnEnter(e);
			Invalidate();
		}

		protected override void OnLeave(EventArgs e)
		{
			base.OnLeave(e);
			Invalidate();
		}

		protected override void OnMouseEnter(EventArgs e)
		{
			base.OnMouseEnter(e);
			hoverMotion.To(1f);
			UiClock.Wake();
			Invalidate();
		}

		protected override void OnMouseLeave(EventArgs e)
		{
			base.OnMouseLeave(e);
			hoverMotion.To(0f);
			UiClock.Wake();
			Invalidate();
		}

		protected override void OnPaintBackground(PaintEventArgs e)
		{
			using (SolidBrush brush = new SolidBrush(Surface))
			{
				e.Graphics.FillRectangle(brush, base.ClientRectangle);
			}
		}

		protected override void OnPaint(PaintEventArgs e)
		{
			if (base.Width < 2 || base.Height < 2)
			{
				return;
			}
			Graphics graphics = e.Graphics;
			graphics.SmoothingMode = SmoothingMode.AntiAlias;
			float value = switchMotion.Value;
			Rectangle rectangle = new Rectangle(0, 0, base.Width - 1, base.Height - 1);
			Color color = Col.Lerp(Theme.Inset, Theme.Accent, (base.Checked ? 0.065f : 0.015f) + hoverMotion.Value * 0.05f);
			using (GraphicsPath path = Theme.TechPath(rectangle, Theme.S(6)))
			{
				using (SolidBrush brush = new SolidBrush(color))
				{
					graphics.FillPath(brush, path);
				}
				using (Pen pen = new Pen(base.Checked ? Col.Alpha(Theme.Accent, 118) : Col.Lerp(Theme.Stroke, Theme.StrokeHi, hoverMotion.Value)))
				{
					graphics.DrawPath(pen, path);
				}
			}
			int scaledDimension = (corePolicy ? Theme.S(DetailsWidth) : 0);
			Rectangle r = new Rectangle(base.Width - scaledDimension - Theme.S(44), (base.Height - Theme.S(16)) / 2, Theme.S(32), Theme.S(16));
			Rectangle rectangle2 = new Rectangle(Theme.S(10), (base.Height - Theme.S(14)) / 2, Theme.S(14), Theme.S(14));
			if (!string.IsNullOrEmpty(IconKey))
			{
				GameTuningIcons.Draw(graphics, rectangle2, IconKey, base.Checked ? Theme.Accent : Theme.Dim);
			}
			else
			{
				Glyphs.Draw(graphics, corePolicy ? "chip" : "tiles", rectangle2, base.Checked ? Theme.Accent : Theme.Dim);
			}
			int scaledDimension2 = Theme.S(32);
			int lowerBound = Math.Max(1, r.Left - scaledDimension2 - Theme.S(8));
			bool locked = Locked;
			TextRenderer.DrawText(graphics, Text, Theme.UI(8f, true), new Rectangle(scaledDimension2, 0, lowerBound, base.Height), base.Enabled ? Theme.Fg : Theme.Faint, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
			using (GraphicsPath path2 = Theme.TechPath(r, Theme.S(4)))
			{
				using (SolidBrush brush2 = new SolidBrush(Col.Lerp(Theme.TrackOff, Col.Lerp(Surface, Theme.Accent, 0.24f), value)))
				{
					graphics.FillPath(brush2, path2);
				}
				using (Pen pen2 = new Pen(base.Checked ? Theme.Accent : (locked ? Theme.Stroke : Theme.StrokeHi)))
				{
					graphics.DrawPath(pen2, path2);
				}
			}
			int scaledDimension3 = Theme.S(10);
			int scaledDimension4 = Theme.S(3);
			Rectangle r2 = new Rectangle(r.Left + scaledDimension4 + (int)((float)(r.Width - scaledDimension4 * 2 - scaledDimension3) * value), r.Top + scaledDimension4, scaledDimension3, scaledDimension3);
			using (GraphicsPath path3 = Theme.TechPath(r2, Theme.S(3)))
			{
				using (SolidBrush brush3 = new SolidBrush(Col.Lerp(Theme.Dim, Theme.Accent, value)))
				{
					graphics.FillPath(brush3, path3);
				}
			}
			if (Focused && ShowFocusCues)
			{
				ControlPaint.DrawFocusRectangle(graphics, Rectangle.Inflate(rectangle, -Theme.S(4), -Theme.S(4)), Theme.Accent, color);
			}
		}
	}
}
