using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PaviseApp
{
	internal sealed class CoreIdentityStrip : Control
	{
		private string gameName = "";

		private string cpuLine = "";

		private Bitmap originalIcon;

		internal CoreIdentityStrip()
		{
			Tag = "backdrop-card";
			SetStyle(ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
			base.Size = new Size(Theme.S(800), Theme.S(88));
			BackColor = Theme.Card;
			base.AccessibleRole = AccessibleRole.Grouping;
			base.TabStop = false;
		}

		internal void SetIdentity(string name, string cpu, Bitmap icon)
		{
			name = name ?? "";
			cpu = cpu ?? "";
			if (!string.Equals(gameName, name, StringComparison.Ordinal) || !string.Equals(cpuLine, cpu, StringComparison.Ordinal) || !object.ReferenceEquals(originalIcon, icon))
			{
				gameName = name;
				cpuLine = cpu;
				originalIcon = icon;
				base.AccessibleName = gameName;
				base.AccessibleDescription = cpuLine;
				Invalidate();
			}
		}

		protected override void OnPaintBackground(PaintEventArgs e)
		{
			CoreWorkspaceSurface.PaintCoreFrame(e.Graphics, base.ClientRectangle, this);
		}

		protected override void OnPaint(PaintEventArgs e)
		{
			base.OnPaint(e);
			Graphics graphics = e.Graphics;
			GraphicsState gstate = graphics.Save();
			try
			{
				graphics.SmoothingMode = SmoothingMode.AntiAlias;
				bool flag = base.Height < Theme.S(72);
				int num = Theme.S(flag ? 36 : 48);
				Rectangle rectangle = new Rectangle(Theme.S(20), (base.Height - num) / 2, num, num);
				using (GraphicsPath path = Theme.TechPath(Rectangle.Inflate(rectangle, Theme.S(3), Theme.S(3)), Theme.S(6)))
				{
					using (SolidBrush brush = new SolidBrush(Theme.Inset))
					{
						graphics.FillPath(brush, path);
					}
					using (Pen pen = new Pen(Col.Lerp(Theme.Card, Theme.StrokeHi, 0.58f)))
					{
						graphics.DrawPath(pen, path);
					}
				}
				if (!DrawOriginalIcon(graphics, rectangle))
				{
					Glyphs.Draw(graphics, "game", Rectangle.Inflate(rectangle, -Theme.S(8), -Theme.S(8)), Theme.Dim);
				}
				int num2 = Theme.S(flag ? 72 : 84);
				int num3 = Math.Max(0, base.Width - num2 - Theme.S(220));
				int num4 = Math.Max(Theme.S(4), (base.Height - Theme.S(flag ? 44 : 52)) / 2);
				TextFormatFlags flags = TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;
				TextRenderer.DrawText(graphics, gameName, Theme.UI(flag ? 10.5f : 12f, true), new Rectangle(num2, num4, num3, Theme.S(flag ? 24 : 28)), Theme.Fg, flags);
				TextRenderer.DrawText(graphics, cpuLine, Theme.UI(8.5f, false), new Rectangle(num2, num4 + Theme.S(flag ? 25 : 31), num3, Theme.S(flag ? 17 : 21)), Theme.Dim, flags);
			}
			finally
			{
				graphics.Restore(gstate);
			}
		}

		private bool DrawOriginalIcon(Graphics graphics, Rectangle bounds)
		{
			if (originalIcon == null)
			{
				return false;
			}
			try
			{
				int num = originalIcon.Width;
				int num2 = originalIcon.Height;
				if (num < 1 || num2 < 1)
				{
					return false;
				}
				float num3 = Math.Min((float)bounds.Width / (float)num, (float)bounds.Height / (float)num2);
				int num4 = Math.Max(1, (int)Math.Round((float)num * num3));
				int num5 = Math.Max(1, (int)Math.Round((float)num2 * num3));
				Rectangle rect = new Rectangle(bounds.X + (bounds.Width - num4) / 2, bounds.Y + (bounds.Height - num5) / 2, num4, num5);
				graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
				graphics.DrawImage(originalIcon, rect);
				return true;
			}
			catch (ArgumentException)
			{
				return false;
			}
			catch (ExternalException)
			{
				return false;
			}
		}
	}
}
