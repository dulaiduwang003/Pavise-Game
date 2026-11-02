using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
	internal sealed class PerformanceActionButton : PillButton
	{
		private sealed class ActionAccessibility : FxAccessibleObject
		{
			private readonly PerformanceActionButton button;

			public override string DefaultAction
			{
				get
				{
					return PerformancePolicy.Text("激活", "Activate");
				}
			}

			public override AccessibleStates State
			{
				get
				{
					AccessibleStates accessibleStates = base.State;
					if (button.CurrentCategory)
					{
						accessibleStates |= AccessibleStates.Selected;
					}
					return accessibleStates;
				}
			}

			internal ActionAccessibility(PerformanceActionButton owner)
				: base(owner)
			{
				button = owner;
			}

			public override void DoDefaultAction()
			{
				button.PerformClick();
			}
		}

		internal bool Quiet;

		internal bool CurrentCategory;

		internal bool IsCategoryTab;
		internal bool CompactCategory;
		internal float CategoryTextSize = 10f;

		internal int CategoryNumber;

		internal PerformanceActionButton(string text)
			: base(text)
		{
		}

		protected override bool IsInputKey(Keys keyData)
		{
			if (IsCategoryTab && (keyData & Keys.Modifiers) == 0 && DsKeys.IsHorizontalNavigation(keyData & Keys.KeyCode))
			{
				return true;
			}
			return base.IsInputKey(keyData);
		}

		protected override AccessibleObject CreateAccessibilityInstance()
		{
			return new ActionAccessibility(this);
		}

		protected override void OnPaintBackground(PaintEventArgs e)
		{
			if (IsCategoryTab) WorkspaceCanvasSurface.Paint(e.Graphics, this, e.ClipRectangle);
			else DsPaint.FillBackground(e.Graphics, this, base.Parent == null ? Theme.Bg : Theme.Card);
		}

		protected override void OnPaint(PaintEventArgs e)
		{
			Graphics graphics = e.Graphics;
			graphics.SmoothingMode = SmoothingMode.AntiAlias;
			if (IsCategoryTab)
			{
				PaintCategory(graphics);
				return;
			}
			Color color = (Quiet ? Theme.Card : ((Kind == BtnKind.Primary) ? Theme.Accent : Theme.Inset));
			if (base.Enabled && hover.Value > 0.01f)
			{
				color = Col.Lerp(color, Quiet ? Theme.CardHover : Theme.Accent, hover.Value * (Quiet ? 0.5f : 0.1f));
			}
			if (!base.Enabled)
			{
				color = Theme.Card;
			}
			DsPaint.TechFrame(graphics, new Rectangle(0, 0, Math.Max(1, base.Width - 1), Math.Max(1, base.Height - 1)), Theme.S(6), color, Quiet ? Theme.Card : ((Kind == BtnKind.Primary) ? Theme.Accent : Theme.Stroke));
			Color foreColor = ((!base.Enabled) ? Theme.Faint : (Quiet ? Theme.Dim : ((Kind == BtnKind.Primary) ? Theme.OnAccent : Theme.Fg)));
			Rectangle bounds = (Quiet ? new Rectangle(Theme.S(6), 0, base.Width - Theme.S(12), base.Height) : base.ClientRectangle);
			TextRenderer.DrawText(graphics, Text, Theme.UI(9f, !Quiet), bounds, foreColor, (TextFormatFlags)(((!Quiet) ? 1 : 0) | 4 | 0x20 | 0x8000 | 0x800 | 0x10000000));
			PaintBusy(graphics, base.ClientRectangle, null);
			if (base.WantsFocusCue)
			{
				ControlPaint.DrawFocusRectangle(graphics, Rectangle.Inflate(base.ClientRectangle, -Theme.S(3), -Theme.S(3)), foreColor, color);
			}
		}

		private void PaintCategory(Graphics g)
		{
			if (base.Width < 2 || base.Height < 2)
			{
				return;
			}
			Rectangle rect = new Rectangle(0, 0, base.Width, base.Height);
			Color color = (CurrentCategory ? Col.Lerp(Theme.Bg, Theme.Accent, Theme.LightMode ? 0.09f : 0.12f) : Col.Lerp(Theme.Bg, Theme.CardHover, hover.Value * 0.35f));
			using (SolidBrush brush = new SolidBrush(Backdrop.NavFill(this, color)))
			{
				g.FillRectangle(brush, rect);
			}
			if (CurrentCategory)
			{
				using (SolidBrush brush2 = new SolidBrush(Theme.Accent))
				{
					g.FillRectangle(brush2, 0, Theme.S(11), Theme.S(3), Math.Max(1, base.Height - Theme.S(22)));
					g.FillRectangle(brush2, Theme.S(14), base.Height - Theme.S(2), Math.Max(1, base.Width - Theme.S(28)), Theme.S(2));
				}
			}
			int textInset = Theme.S(CompactCategory ? 6 : 18);
			TextRenderer.DrawText(g, Text, Theme.UI(CategoryTextSize, CurrentCategory), new Rectangle(textInset, 0, Math.Max(1, base.Width - textInset * 2), base.Height), (!base.Enabled) ? Theme.Faint : (CurrentCategory ? Theme.Fg : Theme.Dim), TextFormatFlags.EndEllipsis | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
			PaintBusy(g, base.ClientRectangle, null);
			if (base.WantsFocusCue)
			{
				ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(base.ClientRectangle, -Theme.S(4), -Theme.S(4)), Theme.Accent, color);
			}
		}
	}
}
