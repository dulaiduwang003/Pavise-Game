using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
	internal sealed class DeviceSpecBar : FxControl
	{
		private static readonly string[] Keys = new string[3] { "v16.spec.cpu", "v16.spec.gpu", "v16.spec.mem" };

		private static readonly string[] Icons = new string[3] { "chip", "gpu", "settings" };

		private string[] values = new string[3] { " ", " ", " " };

		public DeviceSpecBar()
		{
			SetStyle(ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
			base.TabStop = false;
			Cursor = Cursors.Default;
		}

		public void SetValues(string[] next)
		{
			if (next != null && next.Length >= 3)
			{
				values = next;
				Invalidate();
			}
		}

		protected override bool StepAll()
		{
			return false;
		}

		protected override void OnPaint(PaintEventArgs e)
		{
			Graphics graphics = e.Graphics;
			graphics.SmoothingMode = SmoothingMode.AntiAlias;
			Color accent = Theme.Accent;
			Rectangle rectangle = new Rectangle(0, 0, base.Width, base.Height);
			if (Backdrop.AppliesTo(this))
			{
				Backdrop.Paint(graphics, this, rectangle);
			}
			using (SolidBrush brush = new SolidBrush(Backdrop.CardFill(this, Theme.Card)))
			{
				graphics.FillRectangle(brush, rectangle);
			}
			int scaledDimension = Theme.S(14);
			int[] sliceEdgesResult = SliceEdges();
			for (int i = 0; i < 3; i++)
			{
				int onPaintValue = sliceEdgesResult[i];
				if (i % 2 == 1)
				{
					using (SolidBrush brush2 = new SolidBrush(Color.FromArgb(16, 255, 255, 255)))
					{
						graphics.FillPolygon(brush2, Cell(onPaintValue, sliceEdgesResult[i + 1] - onPaintValue, scaledDimension, i));
					}
				}
				if (i > 0)
				{
					using (Pen pen = new Pen(Color.FromArgb(70, accent), Math.Max(1f, Theme.S(1))))
					{
						graphics.DrawLine(pen, onPaintValue + scaledDimension, 0, onPaintValue - scaledDimension, base.Height);
					}
				}
			}
			using (Pen pen2 = new Pen(Color.FromArgb(150, accent), Math.Max(1f, Theme.S(2))))
			{
				graphics.DrawLine(pen2, 0, 0, base.Width, 0);
			}
			using (Pen pen3 = new Pen(Theme.StrokeHi))
			{
				graphics.DrawRectangle(pen3, 0, 0, base.Width - 1, base.Height - 1);
			}
			Font font = Theme.UI(7.4f, true);
			Font font2 = Theme.UI(9.6f, true);
			for (int j = 0; j < 3; j++)
			{
				int onPaintValue2 = sliceEdgesResult[j];
				int remainingOnPaintValue2 = sliceEdgesResult[j + 1] - onPaintValue2;
				int scaledDimension2 = Theme.S(16);
				Glyphs_Draw(box: new Rectangle(onPaintValue2 + scaledDimension2, Theme.S(13), Theme.S(15), Theme.S(15)), g: graphics, name: Icons[j], color: Color.FromArgb(210, accent));
				using (SolidBrush brush3 = new SolidBrush(Theme.Faint))
				{
					graphics.DrawString(Lang.T(Keys[j]), font, brush3, onPaintValue2 + scaledDimension2 + Theme.S(20), Theme.S(14));
				}
				using (SolidBrush brush4 = new SolidBrush(Theme.Fg))
				{
					RectangleF layoutRectangle = new RectangleF(onPaintValue2 + scaledDimension2, Theme.S(34), remainingOnPaintValue2 - scaledDimension2 * 2 + Theme.S(6), Theme.S(20));
					StringFormat stringFormat = new StringFormat();
					stringFormat.Trimming = StringTrimming.EllipsisCharacter;
					stringFormat.FormatFlags = StringFormatFlags.NoWrap;
					using (StringFormat format = stringFormat)
					{
						graphics.DrawString(values[j], font2, brush4, layoutRectangle, format);
					}
				}
			}
		}

		private int[] SliceEdges()
		{
			return new int[4]
			{
				0,
				base.Width * 38 / 100,
				base.Width * 80 / 100,
				base.Width
			};
		}

		private static void Glyphs_Draw(Graphics g, string name, Rectangle box, Color color)
		{
			try
			{
				Glyphs.Draw(g, name, box, color);
			}
			catch
			{
			}
		}

		private Point[] Cell(int x, int slice, int skew, int index)
		{
			return new Point[4]
			{
				new Point(x + skew, 0),
				new Point(x + slice + skew, 0),
				new Point(x + slice - skew, base.Height),
				new Point(x - skew, base.Height)
			};
		}
	}
}
