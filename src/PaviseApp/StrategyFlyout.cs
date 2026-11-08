using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
	internal sealed class StrategyFlyout : RoundPanel, IMessageFilter
	{
		private sealed class StrategyToggle : Toggle
		{
			protected override void OnPaint(PaintEventArgs e)
			{
				Graphics graphics = e.Graphics;
				FillBg(graphics);
				if (Width < 2 || Height < 2) return;
				graphics.SmoothingMode = SmoothingMode.AntiAlias;
				Rectangle track = new Rectangle(0, (Height - Theme.S(22)) / 2, Width - 1, Theme.S(22));
				Color fill = Checked ? Col.Lerp(Theme.Accent2, Theme.Accent, 0.35f) : Theme.Inset;
				fill = Col.Lerp(fill, Theme.CardHover, State.HoverIfEnabled * 0.16f);
				using (GraphicsPath path = Theme.TechPath(track, Theme.S(5)))
				{
					using (var brush = new SolidBrush(fill)) graphics.FillPath(brush, path);
					using (var pen = new Pen(Checked ? Theme.Accent : Theme.StrokeHi)) graphics.DrawPath(pen, path);
					PaintBusy(graphics, track, path);
				}
				int thumbSize = Math.Min(Theme.S(14), track.Height - Theme.S(6));
				Rectangle thumb = new Rectangle(Checked ? track.Right - Theme.S(4) - thumbSize : track.Left + Theme.S(4), (Height - thumbSize) / 2, thumbSize, thumbSize);
				using (GraphicsPath path = Theme.TechPath(thumb, Theme.S(3)))
				using (var brush = new SolidBrush(Checked ? Theme.OnAccent : Theme.Dim)) graphics.FillPath(brush, path);
				if (!Enabled) DsPaint.Veil(graphics, ClientRectangle, Col.Alpha(Theme.Card, 105));
				if (WantsFocusCue) DsFocus.Draw(graphics, Rectangle.Intersect(Rectangle.Inflate(track, Theme.S(2), Theme.S(2)), ClientRectangle), Theme.Fg, EffBg);
			}
		}

		private sealed class PowerPlanSelect : TechSelect
		{
			protected override MenuBadge BadgeFor(int index)
			{
				return index == 0 ? new MenuBadge(Lang.T("plan.pick.pgcontrolled"), true) : base.BadgeFor(index);
			}
		}

		internal readonly TierPicker Scene;

		internal readonly TechSelect BackgroundGovernance;

		private readonly PerformanceInfoButton backgroundGovernanceInfo;

		internal readonly TechSelect Power;

		internal readonly Toggle LaptopPerformance;

		private readonly PerformanceInfoButton laptopPerformanceInfo;

		private bool laptopPerformanceAvailable;

		internal void SetLaptopPerformance(bool supported)
		{
			laptopPerformanceInfo.AccessibleDescription = LaptopPerformance.AccessibleDescription;
			if (laptopPerformanceAvailable == supported) return;
			laptopPerformanceAvailable = supported;
			LaptopPerformance.Visible = supported;
			laptopPerformanceInfo.Visible = supported;
			if (!supported)
			{
				PerformanceInformationTip.Cancel(laptopPerformanceInfo);
				AutoScrollPosition = Point.Empty;
			}
			ArrangeActions();
			Invalidate();
		}

		internal Control Trigger;

		internal Action Dismiss;

		private bool filtering;

		private bool arranging;

		private bool paintedLaptopPerformanceOn;

		private bool CompactRows
		{
			get
			{
				return base.ClientSize.Width < Theme.S(420);
			}
		}

		private int RowPitch
		{
			get
			{
				if (!CompactRows)
				{
					return 56;
				}
				return 80;
			}
		}

		private string LaptopPerformanceLabel { get { return DsLabels.Pick("厂商性能模式", "Vendor performance"); } }

		internal void RefreshBackgroundGovernanceInformation()
		{
			backgroundGovernanceInfo.AccessibleDescription = BackgroundGovernance.AccessibleDescription;
			PerformanceInformationTip.Refresh(backgroundGovernanceInfo, BackgroundGovernance.AccessibleDescription);
		}

		internal void FitViewport(Size available)
		{
			int width = Math.Max(1, Math.Min(Theme.S(452), available.Width));
			bool compact = width < Theme.S(420);
			int rows = laptopPerformanceAvailable ? 4 : 3;
			// Calculate layout without a stale scrollbar; reserve its width only when scrolling is required.
			for (int pass = 0; pass < 3; pass++)
			{
				int measuredHeight = Theme.S(64 + rows * (compact ? 80 : 56) + 16);
				int clientWidth = width - (measuredHeight > available.Height ? SystemInformation.VerticalScrollBarWidth : 0);
				compact = clientWidth < Theme.S(420);
			}
			int contentHeight = Theme.S(64 + rows * (compact ? 80 : 56) + 16);
			base.Width = width;
			base.Height = Math.Max(1, Math.Min(contentHeight, available.Height));
			AutoScroll = contentHeight > base.Height;
			base.AutoScrollMinSize = AutoScroll ? new Size(0, contentHeight) : Size.Empty;
			ArrangeActions();
		}

		private Rectangle ActionBounds(int row)
		{
			int left = Theme.S(CompactRows ? 24 : 220);
			return new Rectangle(left, Theme.S(64 + row * RowPitch + (CompactRows ? 30 : 10)),
				Math.Max(1, base.ClientSize.Width - left - Theme.S(24)), Theme.S(36));
		}

		private void PlaceAction(Control control, int row)
		{
			Rectangle bounds = ActionBounds(row);
			bounds.Offset(base.AutoScrollPosition);
			control.Bounds = bounds;
		}

		private void PlaceInformation(Control info, string label, int row)
		{
			int labelWidth = TextRenderer.MeasureText(label, Theme.UI(9.5f, false), Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width;
			int right = CompactRows ? base.ClientSize.Width - Theme.S(24) : ActionBounds(row).Left - Theme.S(8);
			int left = Math.Max(Theme.S(24), Math.Min(Theme.S(24) + labelWidth + Theme.S(6), right - Theme.S(20)));
			info.SetBounds(left + base.AutoScrollPosition.X,
				Theme.S(64 + row * RowPitch + (CompactRows ? 2 : 18)) + base.AutoScrollPosition.Y, Theme.S(20), Theme.S(20));
		}

		private void ArrangeActions()
		{
			if (arranging || Scene == null || Power == null || BackgroundGovernance == null)
			{
				return;
			}
			arranging = true;
			try
			{
				PlaceAction(Scene, 0);
				PlaceAction(BackgroundGovernance, 1);
				PlaceAction(Power, 2);
				if (backgroundGovernanceInfo != null) PlaceInformation(backgroundGovernanceInfo, Lang.T("background.governance.title"), 1);
				if (LaptopPerformance != null && laptopPerformanceInfo != null)
				{
					Rectangle bounds = ActionBounds(3);
					bounds.Offset(base.AutoScrollPosition);
					LaptopPerformance.SetBounds(bounds.Right - Theme.S(46), bounds.Top + Theme.S(3), Theme.S(46), Theme.S(30));
					PlaceInformation(laptopPerformanceInfo, LaptopPerformanceLabel, 3);
					if (paintedLaptopPerformanceOn != LaptopPerformance.Checked) Invalidate();
				}
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
			Fill = Theme.Card;
			Border = Col.Lerp(Theme.StrokeHi, Theme.Accent, 0.28f);
			BackColor = Theme.Card;
			Radius = Theme.S(24);
			AccentEdge = false;
			base.Size = new Size(Theme.S(452), Theme.S(248));
			Scene = new TierPicker
			{
				Name = "StrategyScenePicker",
				StrongSelection = true,
				PlainSegments = false,
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
				RoundedStyle = false,
				Bg = Theme.Card
			};
			BackgroundGovernance = new TechSelect
			{
				Name = "backgroundGovernanceMode",
				AccessibleName = Lang.T("background.governance.mode"),
				AccessibleDescription = Lang.T("background.governance.detail"),
				RoundedStyle = false,
				Bg = Theme.Card
			};
			BackgroundGovernance.SetItems(new[] { Lang.T("background.governance.off"), Lang.T("background.governance.normal"), Lang.T("background.governance.aggressive") });
			backgroundGovernanceInfo = new PerformanceInfoButton
			{
				Name = "StrategyBackgroundGovernanceInfo",
				AccessibleName = Lang.T("background.governance.title"),
				AccessibleDescription = BackgroundGovernance.AccessibleDescription,
				Bg = Theme.Card
			};
			LaptopPerformance = new StrategyToggle
			{
				Name = "StrategyLaptopPerformanceToggle",
				AccessibleName = DsLabels.Pick("笔记本厂商性能模式", "Laptop vendor performance mode"),
				Bg = Theme.Card,
				Visible = false
			};
			laptopPerformanceInfo = new PerformanceInfoButton
			{
				Name = "StrategyLaptopPerformanceInfo",
				AccessibleName = LaptopPerformance.AccessibleName,
				Bg = Theme.Card,
				Visible = false
			};
			PerformanceInformationTip.AddOwner();
			backgroundGovernanceInfo.MouseEnter += delegate { PerformanceInformationTip.Schedule(backgroundGovernanceInfo, BackgroundGovernance.AccessibleDescription); };
			backgroundGovernanceInfo.MouseLeave += delegate { PerformanceInformationTip.Cancel(backgroundGovernanceInfo); };
			backgroundGovernanceInfo.Click += delegate { PerformanceInformationTip.Show(backgroundGovernanceInfo, BackgroundGovernance.AccessibleDescription); };
			laptopPerformanceInfo.MouseEnter += delegate { PerformanceInformationTip.Schedule(laptopPerformanceInfo, LaptopPerformance.AccessibleDescription); };
			laptopPerformanceInfo.MouseLeave += delegate { PerformanceInformationTip.Cancel(laptopPerformanceInfo); };
			laptopPerformanceInfo.Click += delegate { PerformanceInformationTip.Show(laptopPerformanceInfo, LaptopPerformance.AccessibleDescription); };
			base.Controls.AddRange(new Control[] { Scene, BackgroundGovernance, backgroundGovernanceInfo, Power, LaptopPerformance, laptopPerformanceInfo });
			ArrangeActions();
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
			Fill = BackColor = Theme.Card;
			Border = Col.Lerp(Theme.StrokeHi, Theme.Accent, 0.28f);
			base.OnPaint(e);
			if (base.Width < Theme.S(36) || base.Height < Theme.S(72))
			{
				return;
			}
			Graphics graphics = e.Graphics;
			GraphicsState gstate = graphics.Save();
			graphics.TranslateTransform(base.AutoScrollPosition.X, base.AutoScrollPosition.Y);
			graphics.SmoothingMode = SmoothingMode.AntiAlias;
			DrawRogHeader(graphics);
			TextRenderer.DrawText(graphics, base.AccessibleName, Theme.UI(12.5f, true), new Rectangle(Theme.S(24), Theme.S(16), base.ClientSize.Width - Theme.S(48), Theme.S(28)), Theme.Fg, TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
			using (Pen pen = new Pen(Col.Alpha(Theme.Accent, Theme.LightMode ? 65 : 45), Math.Max(1f, Theme.S(1))))
				graphics.DrawLine(pen, Theme.S(24), Theme.S(56), base.ClientSize.Width - Theme.S(24), Theme.S(56));
			Scene.BackColor = Theme.Card;
			BackgroundGovernance.Bg = backgroundGovernanceInfo.Bg = Theme.Card;
			LaptopPerformance.Bg = laptopPerformanceInfo.Bg = Theme.Card;
			Power.Bg = Theme.Card;
			DrawRowLabel(graphics, 0, DsLabels.Pick("游戏场景", "Game scene"), null, Scene.Enabled);
			DrawRowLabel(graphics, 1, Lang.T("background.governance.title"), backgroundGovernanceInfo, BackgroundGovernance.Enabled);
			DrawRowLabel(graphics, 2, DsLabels.Pick("对局电源", "Match power"), null, Power.Enabled);
			if (laptopPerformanceAvailable)
			{
				DrawRowLabel(graphics, 3, LaptopPerformanceLabel, laptopPerformanceInfo, LaptopPerformance.Enabled);
				Rectangle stateBounds = ActionBounds(3);
				stateBounds.Width = Math.Max(1, stateBounds.Width - Theme.S(62));
				TextRenderer.DrawText(graphics, LaptopPerformance.Checked ? DsLabels.Pick("已开启", "Enabled") : DsLabels.Pick("已关闭", "Disabled"), Theme.UI(9.5f, false), stateBounds, LaptopPerformance.Checked ? Theme.Accent : Theme.Dim, TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
			}
			paintedLaptopPerformanceOn = LaptopPerformance.Checked;
			graphics.Restore(gstate);
			using (var pen = new Pen(Col.Alpha(Theme.Accent, 210), Math.Max(1f, Theme.S(1))))
			{
				graphics.DrawLine(pen, Theme.S(1), Theme.S(1), Theme.S(112), Theme.S(1));
				graphics.DrawLine(pen, Width - Theme.S(13), Theme.S(1), Width - Theme.S(1), Theme.S(13));
				graphics.DrawLine(pen, Width - Theme.S(88), Height - Theme.S(2), Width - Theme.S(24), Height - Theme.S(2));
			}
		}

		private void DrawRogHeader(Graphics graphics)
		{
			GraphicsState state = graphics.Save();
			using (GraphicsPath clip = Theme.TechPath(new Rectangle(0, 0, Width - 1, Math.Max(Height, AutoScrollMinSize.Height) - 1), Theme.S(12)))
			{
				graphics.SetClip(clip, CombineMode.Intersect);
				var header = new Rectangle(1, 1, Math.Max(1, ClientSize.Width - 2), Theme.S(55));
				using (var brush = new LinearGradientBrush(header, Col.Lerp(Theme.Card, Theme.Accent, Theme.LightMode ? 0.06f : 0.1f), Theme.Card, LinearGradientMode.Horizontal))
					graphics.FillRectangle(brush, header);
				using (var pen = new Pen(Col.Alpha(Theme.Accent, Theme.LightMode ? 22 : 16), Math.Max(1f, Theme.S(1))))
					for (int x = ClientSize.Width - Theme.S(128); x < ClientSize.Width + Theme.S(56); x += Theme.S(10))
						graphics.DrawLine(pen, x, Theme.S(56), x + Theme.S(56), 0);
			}
			graphics.Restore(state);
			using (var pen = new Pen(Theme.Accent, Math.Max(1f, Theme.S(2))))
			{
				graphics.DrawLine(pen, Theme.S(11), Theme.S(36), Theme.S(16), Theme.S(24));
				graphics.DrawLine(pen, Theme.S(16), Theme.S(36), Theme.S(21), Theme.S(24));
			}
			if (ClientSize.Width >= Theme.S(420))
				TextRenderer.DrawText(graphics, "PAVISE // STRATEGY", Theme.Mono(7f), new Rectangle(ClientSize.Width - Theme.S(160), Theme.S(20), Theme.S(136), Theme.S(20)), Theme.Dim, TextFormatFlags.Right | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
		}

		private void DrawRowLabel(Graphics g, int row, string label, Control info, bool enabled)
		{
			int right = CompactRows ? base.ClientSize.Width - Theme.S(24) : ActionBounds(row).Left - Theme.S(12);
			if (info != null) right = info.Left - base.AutoScrollPosition.X - Theme.S(4);
			Rectangle bounds = new Rectangle(Theme.S(24), Theme.S(64 + row * RowPitch + (CompactRows ? 0 : 10)), Math.Max(1, right - Theme.S(24)), Theme.S(CompactRows ? 24 : 36));
			TextRenderer.DrawText(g, label, Theme.UI(9.5f, false), bounds, enabled ? Theme.Fg : Theme.Faint, TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
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
				PerformanceInformationTip.Cancel(backgroundGovernanceInfo);
				PerformanceInformationTip.Cancel(laptopPerformanceInfo);
				if (BackgroundGovernance != null) BackgroundGovernance.CloseDropDown();
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
			ContextMenuStrip governanceDropDown = BackgroundGovernance.DropDown;
			if (control != null && (control == this || Contains(control) || control == Trigger || (dropDown != null && (control == dropDown || dropDown.Contains(control))) || (governanceDropDown != null && (control == governanceDropDown || governanceDropDown.Contains(control)))))
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
				PerformanceInformationTip.Cancel(backgroundGovernanceInfo);
				PerformanceInformationTip.Cancel(laptopPerformanceInfo);
				PerformanceInformationTip.ReleaseOwner();
			}
			base.Dispose(disposing);
		}
	}
}
