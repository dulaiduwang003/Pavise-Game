using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
	internal class PillButton : FxControl
	{
		public BtnKind Kind;

		public PillButton(string text)
		{
			Text = text;
			base.Height = Theme.S(34);
			Font = DsType.Font(DsText.Body);
			ForeColor = Theme.Fg;
			base.TabStop = true;
			base.AccessibleRole = AccessibleRole.PushButton;
		}

		public PillButton(string text, BtnKind kind)
			: this(text)
		{
			Kind = kind;
		}

		protected override void OnTextChanged(EventArgs e)
		{
			base.OnTextChanged(e);
			Invalidate();
		}

		public void PerformClick()
		{
			if (base.CanInteract)
			{
				OnClick(EventArgs.Empty);
			}
		}

		protected override bool IsInputKey(Keys keyData)
		{
			if (DsKeys.IsActivation(keyData))
			{
				return true;
			}
			return base.IsInputKey(keyData);
		}

		protected override void OnKeyDown(KeyEventArgs e)
		{
			base.OnKeyDown(e);
			if (DsKeys.IsPlainActivation(e) && base.CanInteract)
			{
				e.SuppressKeyPress = true;
				PerformClick();
			}
		}

		protected override void OnPaint(PaintEventArgs e)
		{
			Graphics graphics = e.Graphics;
			FillBg(graphics);
			graphics.SmoothingMode = SmoothingMode.AntiAlias;
			DsState dsState = base.State;
			Rectangle rectangle = new Rectangle(0, 0, base.Width - 1, base.Height - 1);
			int cut = Math.Max(Theme.S(6), base.Height / 4);
			using (GraphicsPath graphicsPath = Theme.TechPath(rectangle, cut))
			{
				DsPaint.ButtonFace(graphics, graphicsPath, rectangle, cut, base.Size, Kind, dsState, base.EffBg);
				PaintBusy(graphics, rectangle, graphicsPath);
			}
			TextRenderer.DrawText(graphics, Text, (Kind == BtnKind.Primary) ? DsType.Font(DsText.Body, true) : Font, base.ClientRectangle, DsPaint.ButtonInk(Kind, dsState), TextFormatFlags.EndEllipsis | TextFormatFlags.HorizontalCenter | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter);
			if (base.WantsFocusCue)
			{
				DsFocus.Draw(graphics, DsFocus.Inset(base.ClientRectangle, 5), (Kind == BtnKind.Primary) ? Theme.OnAccent : Theme.Fg, base.EffBg);
			}
		}
	}
}
