using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
	internal sealed class GameLibraryConfigurationButton : PillButton
	{
		private sealed class ConfigurationAccessibility : FxAccessibleObject
		{
			private readonly GameLibraryConfigurationButton owner;

			public override AccessibleStates State
			{
				get
				{
					return base.State | ((!owner.modalPresentation) ? (owner.expanded ? AccessibleStates.Expanded : AccessibleStates.Collapsed) : AccessibleStates.None);
				}
			}

			public override string DefaultAction
			{
				get
				{
					if (!owner.modalPresentation)
					{
						if (!owner.expanded)
						{
							return PerformancePolicy.Text("展开", "Expand");
						}
						return PerformancePolicy.Text("收起", "Collapse");
					}
					return PerformancePolicy.Text("打开深度调优", "Open advanced tuning");
				}
			}

			internal ConfigurationAccessibility(GameLibraryConfigurationButton button)
				: base(button)
			{
				owner = button;
			}

			public override void DoDefaultAction()
			{
				owner.PerformClick();
			}
		}

		private bool expanded;

		private bool modalPresentation;

		internal bool Attention;

		internal bool ModalPresentation
		{
			get
			{
				return modalPresentation;
			}
			set
			{
				if (modalPresentation != value)
				{
					modalPresentation = value;
					base.AccessibleDescription = (value ? PerformancePolicy.Text("在弹窗中打开此游戏的配置", "Open this game's settings in a dialog") : PerformancePolicy.Text("展开或收起此游戏的配置", "Expand or collapse this game's settings"));
					Invalidate();
				}
			}
		}

		internal bool Expanded
		{
			get
			{
				return expanded;
			}
			set
			{
				if (expanded != value)
				{
					expanded = value;
					Invalidate();
				}
			}
		}

		internal GameLibraryConfigurationButton()
			: base(PerformancePolicy.Text("深度调优", "Advanced tuning"))
		{
			base.AccessibleDescription = PerformancePolicy.Text("展开或收起此游戏的配置", "Expand or collapse this game's settings");
			Font = Theme.UI(8.2f, true);
			Cursor = Cursors.Hand;
		}

		protected override AccessibleObject CreateAccessibilityInstance()
		{
			return new ConfigurationAccessibility(this);
		}

		protected override void OnPaint(PaintEventArgs e)
		{
			if (base.Width < 3 || base.Height < 3)
			{
				return;
			}
			Graphics graphics = e.Graphics;
			graphics.SmoothingMode = SmoothingMode.AntiAlias;
			Rectangle rectangle = new Rectangle(0, 0, base.Width - 1, base.Height - 1);
			Color accent = Attention ? Theme.Warning : Theme.Accent;
			Color ink = Attention ? Theme.Warning : (expanded ? Theme.Fg : Theme.Dim);
			Color color = Col.Lerp(Theme.Inset, accent, Attention ? 0.14f : (expanded ? 0.13f : (0.035f + hover.Value * 0.08f)));
			using (GraphicsPath path = Theme.TechPath(rectangle, Theme.S(6)))
			{
				using (LinearGradientBrush brush = new LinearGradientBrush(rectangle, color, Theme.Card, LinearGradientMode.Horizontal))
				{
					graphics.FillPath(brush, path);
				}
				using (Pen pen = new Pen((expanded || Attention) ? Col.Alpha(accent, 125) : Theme.Stroke))
				{
					graphics.DrawPath(pen, path);
				}
			}
			using (SolidBrush brush2 = new SolidBrush(Col.Alpha(accent, (expanded || Attention) ? 255 : 135)))
			{
				graphics.FillPolygon(brush2, new Point[4]
				{
					new Point(0, Theme.S(8)),
					new Point(Theme.S(3), Theme.S(5)),
					new Point(Theme.S(3), base.Height - Theme.S(8)),
					new Point(0, base.Height - Theme.S(5))
				});
			}
			Glyphs.Draw(graphics, Attention ? "lol" : "settings", new Rectangle(Theme.S(13), (base.Height - Theme.S(15)) / 2, Theme.S(15), Theme.S(15)), Attention ? accent : (expanded ? Theme.Accent : Theme.Dim));
			TextRenderer.DrawText(graphics, Text, Font, new Rectangle(Theme.S(38), 0, Math.Max(1, base.Width - Theme.S(74)), base.Height), ink, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
			int remainingWidth = base.Width - Theme.S(19);
			int heightRatio = base.Height / 2;
			int onPaintValue = ((!expanded) ? 1 : (-1));
			using (Pen pen2 = new Pen(Attention ? accent : (expanded ? Theme.Accent : Theme.Dim), Math.Max(1f, Theme.S(1))))
			{
				if (modalPresentation)
				{
					graphics.DrawLines(pen2, new Point[3]
					{
						new Point(remainingWidth - Theme.S(2), heightRatio - Theme.S(4)),
						new Point(remainingWidth + Theme.S(2), heightRatio),
						new Point(remainingWidth - Theme.S(2), heightRatio + Theme.S(4))
					});
				}
				else
				{
					graphics.DrawLines(pen2, new Point[3]
					{
						new Point(remainingWidth - Theme.S(4), heightRatio - onPaintValue * Theme.S(2)),
						new Point(remainingWidth, heightRatio + onPaintValue * Theme.S(2)),
						new Point(remainingWidth + Theme.S(4), heightRatio - onPaintValue * Theme.S(2))
					});
				}
			}
			if (base.WantsFocusCue)
			{
				ControlPaint.DrawFocusRectangle(graphics, Rectangle.Inflate(rectangle, -Theme.S(4), -Theme.S(4)), Theme.Accent, color);
			}
		}
	}
}
