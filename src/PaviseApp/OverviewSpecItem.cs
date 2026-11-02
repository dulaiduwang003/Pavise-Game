using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
	internal sealed class OverviewSpecItem : Control
	{
		internal string Caption = "";

		internal string Value = "";

		internal string Detail = "";

		internal string IconKey = "";

		internal Rectangle IconBounds;

		internal Rectangle CaptionBounds;

		internal Rectangle ValueBounds;

		internal Rectangle DetailBounds;

		private bool compact;

		internal float ValuePointSize
		{
			get
			{
				if (!Compact)
				{
					return 10.5f;
				}
				return 10f;
			}
		}

		internal float DetailPointSize
		{
			get
			{
				if (!Compact)
				{
					return 8.5f;
				}
				return 8f;
			}
		}

		internal bool Compact
		{
			get
			{
				return compact;
			}
			set
			{
				if (compact != value)
				{
					compact = value;
					Arrange();
					Invalidate();
				}
			}
		}

		internal OverviewSpecItem()
		{
			SetStyle(ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
			BackColor = Theme.Bg;
			base.TabStop = false;
			base.AccessibleRole = AccessibleRole.StaticText;
		}

		internal void SetContent(string caption, string value, string detail, string iconKey)
		{
			caption = caption ?? "";
			value = value ?? "";
			detail = detail ?? "";
			iconKey = iconKey ?? "";
			if (!(Caption == caption) || !(Value == value) || !(Detail == detail) || !(IconKey == iconKey))
			{
				bool flag = Caption != caption || Value != value || Detail != detail;
				Caption = caption;
				Value = value;
				Detail = detail;
				IconKey = iconKey;
				base.AccessibleName = Caption;
				base.AccessibleDescription = Value + ((Detail.Length == 0) ? "" : ("\r\n" + Detail));
				if (flag)
				{
					Arrange();
				}
				Invalidate();
			}
		}

		internal int MeasureHeight(int width)
		{
			Rectangle icon;
			Rectangle caption;
			Rectangle value;
			Rectangle detail;
			return Geometry(width, out icon, out caption, out value, out detail);
		}

		private int Geometry(int width, out Rectangle icon, out Rectangle caption, out Rectangle value, out Rectangle detail)
		{
			width = Math.Max(1, width);
			int num = Theme.S(6);
			int num2 = Theme.S(Compact ? 20 : 26);
			int num3 = Theme.S(Compact ? 12 : 18);
			int num4 = Theme.S(Compact ? 44 : 58);
			int num5 = Math.Max(1, width - num4 - Theme.S(4));
			int num6 = OverviewHudDrawing.TextHeight(Caption, Theme.UI(Compact ? 8f : 8.5f, false), num5);
			int num7 = OverviewHudDrawing.TextHeight(Value, Theme.UI(ValuePointSize, true), num5);
			int num8 = ((Detail.Length != 0) ? OverviewHudDrawing.TextHeight(Detail, Theme.UI(DetailPointSize, false), num5) : 0);
			icon = new Rectangle(num3, num + Theme.S(2), num2, num2);
			caption = new Rectangle(num4, num, num5, num6);
			value = new Rectangle(num4, caption.Bottom + Theme.S(Compact ? 3 : 4), num5, num7);
			detail = new Rectangle(num4, value.Bottom + ((num8 != 0) ? Theme.S(4) : 0), num5, num8);
			return Math.Max(Theme.S(Compact ? 64 : 72), ((num8 == 0) ? value.Bottom : detail.Bottom) + Theme.S(8));
		}

		private void Arrange()
		{
			Geometry(base.Width, out IconBounds, out CaptionBounds, out ValueBounds, out DetailBounds);
		}

		protected override void OnSizeChanged(EventArgs e)
		{
			base.OnSizeChanged(e);
			Arrange();
		}

		protected override void OnPaintBackground(PaintEventArgs e)
		{
			WorkspaceCanvasSurface.Paint(e.Graphics, this, e.ClipRectangle);
		}

		protected override void OnPaint(PaintEventArgs e)
		{
			base.OnPaint(e);
			Graphics graphics = e.Graphics;
			GraphicsState gstate = graphics.Save();
			try
			{
				graphics.SmoothingMode = SmoothingMode.AntiAlias;
				using (Pen pen = new Pen(Theme.Accent, Math.Max(1f, Theme.S(2))))
				{
					graphics.DrawLine(pen, Theme.S(2), Theme.S(8), Theme.S(2), Theme.S(Compact ? 27 : 31));
				}
				OverviewHudDrawing.Icon(graphics, IconBounds, IconKey, Theme.Dim);
				OverviewHudDrawing.Text(graphics, Caption, Theme.UI(Compact ? 8f : 8.5f, false), CaptionBounds, Theme.Dim);
				OverviewHudDrawing.Text(graphics, Value, Theme.UI(ValuePointSize, true), ValueBounds, Theme.Fg);
				OverviewHudDrawing.Text(graphics, Detail, Theme.UI(DetailPointSize, false), DetailBounds, Theme.Dim);
			}
			finally
			{
				graphics.Restore(gstate);
			}
		}
	}
}
