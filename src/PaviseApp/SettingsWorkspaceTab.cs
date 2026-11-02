using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
	internal sealed class SettingsWorkspaceTab : PillButton
	{
		private sealed class TabAccessibility : FxAccessibleObject
		{
			private readonly SettingsWorkspaceTab owner;

			public override string DefaultAction
			{
				get
				{
					return PerformancePolicy.Text("切换分类", "Switch category");
				}
			}

			public override AccessibleStates State
			{
				get
				{
					return base.State | (owner.Selected ? AccessibleStates.Selected : AccessibleStates.None);
				}
			}

			internal TabAccessibility(SettingsWorkspaceTab button)
				: base(button)
			{
				owner = button;
			}

			public override void DoDefaultAction()
			{
				owner.PerformClick();
			}
		}

		internal string IconKey = "";

		internal bool Selected;

		internal Action<Keys> NavigationRequested;

		internal Font LabelFont
		{
			get
			{
				return Theme.UI(10.5f, true);
			}
		}

		internal Rectangle LabelBounds
		{
			get
			{
				return new Rectangle(Theme.S(44), 0, Math.Max(1, base.Width - Theme.S(58)), base.Height);
			}
		}

		internal SettingsWorkspaceTab(string text)
			: base(text)
		{
			base.AccessibleRole = AccessibleRole.PageTab;
		}

		protected override bool IsInputKey(Keys keyData)
		{
			if ((keyData & Keys.Modifiers) == 0 && DsKeys.IsHorizontalNavigation(keyData & Keys.KeyCode))
			{
				return true;
			}
			return base.IsInputKey(keyData);
		}

		protected override void OnKeyDown(KeyEventArgs e)
		{
			base.OnKeyDown(e);
			if (!e.Handled && e.Modifiers == Keys.None && DsKeys.IsHorizontalNavigation(e.KeyCode))
			{
				e.Handled = true;
				e.SuppressKeyPress = true;
				if (base.CanInteract && NavigationRequested != null)
				{
					NavigationRequested(e.KeyCode);
				}
			}
		}

		protected override AccessibleObject CreateAccessibilityInstance()
		{
			return new TabAccessibility(this);
		}

		protected override void OnPaint(PaintEventArgs e)
		{
			Graphics graphics = e.Graphics;
			if (Backdrop.AppliesTo(this)) Backdrop.Paint(graphics, this, base.ClientRectangle);
			else graphics.Clear(base.Parent == null ? Theme.Card : base.Parent.BackColor);
			graphics.SmoothingMode = SmoothingMode.AntiAlias;
			Rectangle rectangle = new Rectangle(1, 1, base.Width - 3, base.Height - 3);
			float value = hover.Value;
			using (GraphicsPath path = Theme.TechPath(rectangle, Theme.S(11)))
			{
				using (SolidBrush brush = new SolidBrush(Backdrop.CardFill(this, Selected ? Col.Lerp(Theme.Card, Theme.Accent, 0.2f) : Col.Lerp(Theme.Card, Theme.CardHover, value))))
				{
					using (Pen pen = new Pen(Selected ? Theme.Accent : Col.Lerp(Theme.Stroke, Theme.StrokeHi, value)))
					{
						graphics.FillPath(brush, path);
						graphics.DrawPath(pen, path);
					}
				}
			}
			int num = Theme.S(20);
			int num2 = Theme.S(14);
			SettingsWorkspaceIcons.Draw(graphics, new Rectangle(num2, (base.Height - num) / 2, num, num), IconKey, Selected ? Theme.Accent : Theme.Dim);
			TextRenderer.DrawText(graphics, Text, LabelFont, LabelBounds, Selected ? Theme.Fg : Theme.Dim, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
			if (base.WantsFocusCue)
			{
				DsFocus.Draw(graphics, Rectangle.Inflate(rectangle, -Theme.S(4), -Theme.S(4)), Theme.Fg, Theme.Card);
			}
		}
	}
}
