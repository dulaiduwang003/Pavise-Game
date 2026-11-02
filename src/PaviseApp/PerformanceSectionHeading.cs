using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
	internal sealed class PerformanceSectionHeading : Label
	{
		internal PerformanceRestartScope RestartScope;

		internal int FeatureCount;

		internal int SelectedCount;

		internal int AttentionCount;

		private Rectangle countBounds;

		internal Rectangle TitleBounds { get; private set; }

		internal Rectangle ScopeBounds { get; private set; }

		internal string ScopeText
		{
			get
			{
				if (RestartScope == PerformanceRestartScope.SystemRestart)
				{
					return PerformancePolicy.Text("需重启电脑 · 独立手动", "Restart PC · Manual");
				}
				if (RestartScope == PerformanceRestartScope.ProcessRestart)
				{
					return PerformancePolicy.Text("需重开游戏", "Relaunch game");
				}
				return PerformancePolicy.Text("无需重启电脑", "No PC restart");
			}
		}

		private string CountText
		{
			get
			{
				return FeatureCount + PerformancePolicy.Text(" 项", (FeatureCount == 1) ? " item" : " items") + ((AttentionCount > 0) ? (" · " + AttentionCount + PerformancePolicy.Text(" 项需处理", " need attention")) : "");
			}
		}

		internal PerformanceSectionHeading()
		{
			SetStyle(ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer, true);
			base.TabStop = false;
			base.AccessibleRole = AccessibleRole.StaticText;
		}

		private int Arrange(int width)
		{
			int num = Theme.S(8);
			int num2 = Theme.S(6);
			int val = Theme.S(22);
			int num3 = TextRenderer.MeasureText(ScopeText, Theme.UI(8f, false), Size.Empty, TextFormatFlags.NoPadding).Width + Theme.S(20);
			int num4 = TextRenderer.MeasureText(CountText, Theme.UI(8.5f, false), Size.Empty, TextFormatFlags.NoPadding).Width + Theme.S(4);
			bool flag = width >= num3 + num4 + num * 2 + Theme.S(180);
			int num5 = Math.Max(1, flag ? (width - num3 - num4 - num * 2) : width);
			int num6 = Math.Max(val, TextRenderer.MeasureText(Text, Theme.UI(10f, true), new Size(num5, int.MaxValue), TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding).Height);
			TitleBounds = new Rectangle(0, num2, num5, num6);
			int num7 = (flag ? num2 : (TitleBounds.Bottom + Theme.S(6)));
			int num8 = (flag ? (width - num3) : 0);
			ScopeBounds = new Rectangle(num8, num7, Math.Min(width, num3), val);
			countBounds = new Rectangle(flag ? (num5 + num) : (ScopeBounds.Right + num), num7, Math.Max(0, flag ? num4 : (width - ScopeBounds.Right - num)), val);
			return Math.Max(TitleBounds.Bottom, ScopeBounds.Bottom) + Theme.S(8);
		}

		internal int MeasureHeight(int width)
		{
			return Arrange(Math.Max(1, width));
		}

		protected override void OnPaint(PaintEventArgs e)
		{
			Graphics graphics = e.Graphics;
			WorkspaceCanvasSurface.Paint(graphics, this, e.ClipRectangle);
			Arrange(base.Width);
			graphics.SmoothingMode = SmoothingMode.AntiAlias;
			TextRenderer.DrawText(graphics, Text, Theme.UI(10f, true), TitleBounds, Theme.Fg, TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
			Color color = ((RestartScope == PerformanceRestartScope.SystemRestart) ? Theme.Warning : Theme.Dim);
			using (GraphicsPath path = Theme.Rounded(ScopeBounds, Theme.S(5)))
			{
				using (SolidBrush brush = new SolidBrush(Col.Lerp(Theme.Bg, color, Theme.LightMode ? 0.08f : 0.12f)))
				{
					graphics.FillPath(brush, path);
				}
			}
			TextRenderer.DrawText(graphics, ScopeText, Theme.UI(8f, false), ScopeBounds, color, TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
			TextRenderer.DrawText(graphics, CountText, Theme.UI(8.5f, false), countBounds, (AttentionCount > 0) ? Theme.Warning : Theme.Faint, TextFormatFlags.NoPrefix | TextFormatFlags.Right | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
		}
	}
}
