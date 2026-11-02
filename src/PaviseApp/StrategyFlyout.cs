using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
	internal sealed class StrategyFlyout : RoundPanel, IMessageFilter
	{
		private sealed class PowerPlanSelect : TechSelect
		{
			protected override MenuBadge BadgeFor(int index)
			{
				return index == 0 ? new MenuBadge(Lang.T("plan.pick.pgcontrolled"), true) : base.BadgeFor(index);
			}
		}

		internal readonly TierPicker Scene;

		internal readonly TechSelect Power;

		internal Control Trigger;

		internal Action Dismiss;

		private bool filtering;

		private bool arranging;

		private bool CompactRows
		{
			get
			{
				return base.ClientSize.Width < Theme.S(430);
			}
		}

		private int RowPitch
		{
			get
			{
				if (!CompactRows)
				{
					return 80;
				}
				return 128;
			}
		}

		internal void FitViewport(Size available)
		{
			int num = Math.Max(1, Math.Min(Theme.S(452), available.Width));
            base.Width = num;
            // Re-evaluate compact rows after the scrollbar changes the client width.
            for (int pass = 0; pass < 3; pass++)
            {
                int contentHeight = Theme.S(78 + (CompactRows ? 128 : 80) * 2 + 10);
                base.Height = Math.Max(1, Math.Min(contentHeight, available.Height));
                AutoScroll = contentHeight > base.Height;
                base.AutoScrollMinSize = AutoScroll ? new Size(0, contentHeight) : Size.Empty;
            }
			ArrangeActions();
		}

		private void ArrangeActions()
		{
			if (arranging || Scene == null || Power == null)
			{
				return;
			}
			arranging = true;
			try
			{
				int num = base.ClientSize.Width;
				int rowPitch = RowPitch;
				Point autoScrollPosition = base.AutoScrollPosition;
				int num2 = Theme.S(CompactRows ? 24 : 192);
				int num3 = (CompactRows ? 62 : 17);
				int num4 = Math.Max(1, num - num2 - Theme.S(32));
				Scene.SetBounds(num2, Theme.S(78 + num3) + autoScrollPosition.Y, num4, Theme.S(38));
				Power.SetBounds(num2, Theme.S(78 + rowPitch + num3) + autoScrollPosition.Y, num4, Theme.S(38));
			}
			finally
			{
				arranging = false;
			}
		}

		protected override void OnSizeChanged(EventArgs e)
		{
			base.OnSizeChanged(e);
			ArrangeActions();
		}

		internal StrategyFlyout()
		{
			base.Name = "StrategyFlyout";
			base.AccessibleName = DsLabels.Pick("策略设置", "Strategy settings");
			UseBackdrop = false;
			Fill = Theme.Bg;
			Border = Col.Lerp(Theme.StrokeHi, Theme.Accent, 0.3f);
			BackColor = Theme.Bg;
			Radius = Theme.S(36);
			AccentEdge = false;
			base.Size = new Size(Theme.S(452), Theme.S(248));
			Scene = new TierPicker
			{
				Name = "StrategyScenePicker",
				StrongSelection = true,
				AccessibleName = DsLabels.Pick("游戏场景", "Game scene"),
				Labels = new string[2]
				{
					ModeButton.ModeName(PerformancePreset.Standard),
					ModeButton.ModeName(PerformancePreset.Competitive)
				},
				Index = 0
			};
			Power = new PowerPlanSelect
			{
				Name = "StrategyPowerPicker",
				AccessibleName = Lang.T("power.name"),
				Bg = Theme.Card
			};
			Scene.SetBounds(Theme.S(192), Theme.S(95), Theme.S(228), Theme.S(38));
			Power.SetBounds(Theme.S(192), Theme.S(175), Theme.S(228), Theme.S(38));
			base.Controls.AddRange(new Control[] { Scene, Power });
			foreach (Control control in base.Controls)
			{
				control.EnabledChanged += delegate
				{
					Invalidate();
				};
			}
		}

		protected override void OnPaint(PaintEventArgs e)
		{
			Border = Col.Lerp(Theme.StrokeHi, Theme.Accent, 0.3f);
			base.OnPaint(e);
			if (base.Width < Theme.S(36) || base.Height < Theme.S(72))
			{
				return;
			}
			Graphics graphics = e.Graphics;
			GraphicsState gstate = graphics.Save();
			graphics.TranslateTransform(base.AutoScrollPosition.X, base.AutoScrollPosition.Y);
			graphics.SmoothingMode = SmoothingMode.AntiAlias;
			using (GraphicsPath path = Theme.TechPath(new Rectangle(0, 0, base.Width - 1, Math.Max(base.Height, base.AutoScrollMinSize.Height) - 1), Theme.S(18)))
			{
				GraphicsState gstate2 = graphics.Save();
				graphics.SetClip(path, CombineMode.Intersect);
				Rectangle rect = new Rectangle(0, 0, base.Width, Theme.S(72));
				using (LinearGradientBrush brush = new LinearGradientBrush(rect, Col.Lerp(Theme.Card, Theme.Accent, 0.12f), Theme.Bg, LinearGradientMode.Horizontal))
				{
					graphics.FillRectangle(brush, rect);
				}
				using (Pen pen = new Pen(Col.Alpha(Theme.Accent, Theme.LightMode ? 25 : 18), Math.Max(1f, Theme.S(1))))
				{
					for (int i = base.Width - Theme.S(150); i < base.Width + Theme.S(80); i += Theme.S(9))
					{
						graphics.DrawLine(pen, i, Theme.S(72), i + Theme.S(72), 0);
					}
				}
				graphics.Restore(gstate2);
			}
			TextRenderer.DrawText(graphics, base.AccessibleName, Theme.UI(12.5f, true), new Rectangle(Theme.S(24), Theme.S(16), base.Width - Theme.S(112), Theme.S(30)), Theme.Fg, TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
			TextRenderer.DrawText(graphics, "PAVISE // STRATEGY", Theme.Mono(7.2f), new Rectangle(Theme.S(25), Theme.S(46), base.Width - Theme.S(112), Theme.S(16)), Theme.Dim, TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
			Rectangle r = new Rectangle(base.Width - Theme.S(74), Theme.S(17), Theme.S(48), Theme.S(42));
			DsPaint.TechFrame(graphics, r, Theme.S(10), Col.Lerp(Theme.Card, Theme.Accent, 0.14f), Col.Alpha(Theme.Accent, 140));
			Glyphs.Draw(graphics, "game", new Rectangle(r.Left + Theme.S(12), r.Top + Theme.S(9), Theme.S(24), Theme.S(24)), Theme.Accent);
			DrawSection(graphics, 78, "01", DsLabels.Pick("游戏场景", "Game scene"), "SCENARIO", Scene.Enabled);
			DrawSection(graphics, 78 + RowPitch, "02", DsLabels.Pick("对局电源", "Match power"), "POWER", Power.Enabled);
			using (Pen pen2 = new Pen(Theme.Accent, Math.Max(1f, Theme.S(2))))
			{
				graphics.DrawLine(pen2, Theme.S(1), Theme.S(1), Theme.S(128), Theme.S(1));
				graphics.DrawLine(pen2, base.Width - Theme.S(19), Theme.S(1), base.Width - Theme.S(1), Theme.S(19));
				graphics.DrawLine(pen2, base.Width - Theme.S(104), base.Height - Theme.S(2), base.Width - Theme.S(24), base.Height - Theme.S(2));
			}
			graphics.Restore(gstate);
		}

		private void DrawSection(Graphics g, int top, string number, string title, string code, bool enabled)
		{
			Rectangle r = new Rectangle(Theme.S(16), Theme.S(top), base.ClientSize.Width - Theme.S(32), Theme.S(CompactRows ? 120 : 72));
			DsPaint.TechFrame(g, r, Theme.S(10), Theme.Card, Theme.Stroke);
			using (Pen pen = new Pen(enabled ? Col.Alpha(Theme.Accent, 180) : Theme.StrokeHi, Math.Max(1f, Theme.S(2))))
			{
				g.DrawLine(pen, r.Left, r.Top + Theme.S(17), r.Left, r.Bottom - Theme.S(17));
			}
			TextRenderer.DrawText(g, number, Theme.Mono(7.2f), new Rectangle(Theme.S(28), r.Top + Theme.S(15), Theme.S(22), Theme.S(20)), enabled ? Theme.Accent : Theme.Faint, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
			TextRenderer.DrawText(g, title, Theme.UI(9.5f, true), new Rectangle(Theme.S(56), r.Top + Theme.S(12), Theme.S(124), Theme.S(24)), enabled ? Theme.Fg : Theme.Faint, TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
			TextRenderer.DrawText(g, code, Theme.Mono(6.8f), new Rectangle(Theme.S(57), r.Top + Theme.S(38), Theme.S(122), Theme.S(16)), Theme.Faint, TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
		}

		protected override void OnVisibleChanged(EventArgs e)
		{
			base.OnVisibleChanged(e);
			if (base.Visible && !filtering)
			{
				Application.AddMessageFilter(this);
				filtering = true;
			}
			else if (!base.Visible)
			{
				StopFiltering();
				if (Power != null)
				{
					Power.CloseDropDown();
				}
			}
		}

		public bool PreFilterMessage(ref Message message)
		{
			if (!base.Visible || (message.Msg != 513 && message.Msg != 516 && message.Msg != 519 && message.Msg != 161))
			{
				return false;
			}
			Control control = Control.FromHandle(message.HWnd);
			ContextMenuStrip dropDown = Power.DropDown;
			if (control != null && (control == this || Contains(control) || control == Trigger || (dropDown != null && (control == dropDown || dropDown.Contains(control)))))
			{
				return false;
			}
			if (Dismiss != null)
			{
				Dismiss();
			}
			return false;
		}

		private void StopFiltering()
		{
			if (filtering)
			{
				Application.RemoveMessageFilter(this);
				filtering = false;
			}
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				StopFiltering();
			}
			base.Dispose(disposing);
		}
	}
}
