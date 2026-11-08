using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
	internal static class OverviewHudDrawing
	{
		internal const TextFormatFlags TextFlags = TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding;

		internal static int TextHeight(string text, Font font, int width)
		{
			return TextRenderer.MeasureText(string.IsNullOrEmpty(text) ? " " : text, font, new Size(Math.Max(1, width), int.MaxValue), TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding).Height;
		}

		internal static void Text(Graphics graphics, string text, Font font, Rectangle bounds, Color ink)
		{
			if (!string.IsNullOrEmpty(text) && bounds.Width > 0 && bounds.Height > 0)
			{
				TextRenderer.DrawText(graphics, text, font, bounds, ink, TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
			}
		}

		internal static void Icon(Graphics graphics, Rectangle bounds, string key, Color ink)
		{
			if (bounds.Width < 1 || bounds.Height < 1)
			{
				return;
			}
			GraphicsState gstate = graphics.Save();
			try
			{
				graphics.TranslateTransform(bounds.Left, bounds.Top);
				graphics.ScaleTransform((float)bounds.Width / 32f, (float)bounds.Height / 32f);
				Pen pen = new Pen(ink, 1.3f);
				pen.LineJoin = LineJoin.Round;
				pen.StartCap = LineCap.Round;
				pen.EndCap = LineCap.Round;
				using (Pen pen2 = pen)
				{
					switch ((key ?? "").ToLowerInvariant())
					{
					case "cpu":
					case "chip":
					{
						graphics.DrawRectangle(pen2, 7, 7, 18, 18);
						graphics.DrawRectangle(pen2, 11, 11, 10, 10);
						for (int k = 10; k <= 22; k += 4)
						{
							graphics.DrawLine(pen2, k, 3, k, 7);
							graphics.DrawLine(pen2, k, 25, k, 29);
							graphics.DrawLine(pen2, 3, k, 7, k);
							graphics.DrawLine(pen2, 25, k, 29, k);
						}
						break;
					}
					case "gpu":
					{
						graphics.DrawRectangle(pen2, 3, 7, 26, 18);
						graphics.DrawLine(pen2, 1, 6, 1, 28);
						for (int l = 0; l < 2; l++)
						{
							int iconValue = 10 + l * 12;
							graphics.DrawEllipse(pen2, iconValue - 5, 11, 10, 10);
							graphics.DrawLine(pen2, iconValue - 3, 13, iconValue + 3, 19);
							graphics.DrawLine(pen2, iconValue - 3, 19, iconValue + 3, 13);
						}
						graphics.DrawLine(pen2, 7, 28, 15, 28);
						break;
					}
					case "ram":
					case "memory":
					case "mem":
					{
						graphics.DrawRectangle(pen2, 2, 9, 28, 15);
						for (int i = 5; i <= 23; i += 9)
						{
							graphics.DrawRectangle(pen2, i, 12, 5, 6);
						}
						for (int j = 5; j <= 27; j += 4)
						{
							graphics.DrawLine(pen2, j, 24, j, 27);
						}
						break;
					}
					case "display":
					case "monitor":
					case "screen":
						graphics.DrawRectangle(pen2, 3, 4, 26, 20);
						graphics.DrawLine(pen2, 16, 24, 16, 29);
						graphics.DrawLine(pen2, 10, 29, 22, 29);
						break;
					default:
						graphics.DrawRectangle(pen2, 6, 6, 20, 20);
						break;
					}
				}
			}
			finally
			{
				graphics.Restore(gstate);
			}
		}
	}
}
