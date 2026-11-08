using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace PaviseApp
{
	internal static class AntiCheatFeatureIcons
	{
		internal static string[] IconKeys
		{
			get
			{
				return new string[6] { "ace", "eac", "battleye", "master", "roster", "records" };
			}
		}

		internal static bool Supports(string key)
		{
			string normalizeResult = Normalize(key);
			switch (normalizeResult)
			{
			default:
				return normalizeResult == "records";
			case "ace":
			case "eac":
			case "battleye":
			case "master":
			case "roster":
				return true;
			}
		}

		internal static void Draw(Graphics g, Rectangle bounds, string key, Color ink)
		{
			if (g == null || bounds.Width <= 0 || bounds.Height <= 0)
			{
				return;
			}
			GraphicsState gstate = g.Save();
			try
			{
				g.SetClip(bounds, CombineMode.Intersect);
				float minRatio = (float)Math.Min(bounds.Width, bounds.Height) / 32f;
				g.TranslateTransform((float)bounds.Left + ((float)bounds.Width - 32f * minRatio) / 2f, (float)bounds.Top + ((float)bounds.Height - 32f * minRatio) / 2f);
				g.ScaleTransform(minRatio, minRatio);
				g.SmoothingMode = SmoothingMode.AntiAlias;
				g.PixelOffsetMode = PixelOffsetMode.HighQuality;
				using (Pen pen2 = new Pen(ink, 1.65f))
				{
					using (Pen pen = new Pen(Color.FromArgb(ink.A * 3 / 5, ink), 1.15f))
					{
						LineCap lineCap2 = (pen.EndCap = LineCap.Round);
						LineCap lineCap4 = (pen.StartCap = lineCap2);
						LineCap startCap = (pen2.EndCap = lineCap4);
						pen2.StartCap = startCap;
						LineJoin lineJoin2 = (pen.LineJoin = LineJoin.Round);
						pen2.LineJoin = lineJoin2;
						switch (Normalize(key))
						{
						case "ace":
						case "master":
							Lines(g, pen2, 16f, 3f, 27f, 7f, 25f, 21f, 21f, 26f, 16f, 29f, 11f, 26f, 7f, 21f, 5f, 7f, 16f, 3f);
							g.DrawLine(pen2, 11, 21, 16, 10);
							g.DrawLine(pen2, 18, 21, 23, 10);
							break;
						case "eac":
							Eye(g, pen2, 3f, 9f, 26f, 14f);
							g.DrawEllipse(pen2, 11.5f, 11.5f, 9f, 9f);
							g.DrawLine(pen2, 4, 16, 28, 16);
							Lines(g, pen, 5f, 9f, 5f, 5f, 11f, 5f);
							Lines(g, pen, 21f, 27f, 27f, 27f, 27f, 23f);
							break;
						case "battleye":
						{
							for (int i = 0; i < 4; i++)
							{
								g.DrawArc(pen2, 6, 6, 20, 20, 25 + i * 90, 40);
							}
							g.DrawLine(pen2, 2, 16, 7, 16);
							g.DrawLine(pen2, 25, 16, 30, 16);
							g.DrawLine(pen2, 16, 2, 16, 7);
							g.DrawLine(pen2, 16, 25, 16, 30);
							Eye(g, pen2, 8f, 11.5f, 16f, 9f);
							g.DrawEllipse(pen2, 14, 14, 4, 4);
							break;
						}
						case "roster":
							Cut(g, pen2, 4f, 4f, 24f, 24f, 3f);
							Lines(g, pen2, 8f, 11f, 10f, 13f, 13f, 9f);
							Lines(g, pen2, 8f, 21f, 10f, 23f, 13f, 19f);
							g.DrawLine(pen2, 17, 11, 23, 11);
							g.DrawLine(pen, 17, 14, 21, 14);
							g.DrawLine(pen2, 17, 21, 23, 21);
							g.DrawLine(pen, 17, 24, 21, 24);
							break;
						case "records":
							Lines(g, pen, 9f, 3f, 24f, 3f, 28f, 7f, 28f, 23f);
							Lines(g, pen2, 6f, 7f, 21f, 7f, 25f, 11f, 25f, 26f);
							Cut(g, pen2, 3f, 11f, 18f, 18f, 2f);
							g.DrawLine(pen2, 7, 17, 17, 17);
							g.DrawLine(pen2, 7, 22, 17, 22);
							g.DrawLine(pen, 7, 26, 13, 26);
							break;
						default:
							Lines(g, pen2, 10f, 4f, 22f, 4f, 29f, 16f, 22f, 28f, 10f, 28f, 3f, 16f, 10f, 4f);
							g.DrawLine(pen2, 16, 10, 16, 18);
							g.DrawEllipse(pen2, 15.4f, 22f, 1.2f, 1.2f);
							break;
						}
					}
				}
			}
			finally
			{
				g.Restore(gstate);
			}
		}

		private static string Normalize(string key)
		{
			if (string.IsNullOrEmpty(key))
			{
				return "";
			}
			string toLowerInvariantResult = key.Trim().ToLowerInvariant();
			if (!(toLowerInvariantResult == "be") && !(toLowerInvariantResult == "battleeye"))
			{
				return toLowerInvariantResult;
			}
			return "battleye";
		}

		private static void Eye(Graphics g, Pen pen, float x, float y, float width, float height)
		{
			float yOffset = y + height / 2f;
			using (GraphicsPath graphicsPath = new GraphicsPath())
			{
				graphicsPath.AddBezier(x, yOffset, x + width * 0.27f, y - height * 0.15f, x + width * 0.73f, y - height * 0.15f, x + width, yOffset);
				graphicsPath.AddBezier(x + width, yOffset, x + width * 0.73f, y + height * 1.15f, x + width * 0.27f, y + height * 1.15f, x, yOffset);
				graphicsPath.CloseFigure();
				g.DrawPath(pen, graphicsPath);
			}
		}

		private static void Cut(Graphics g, Pen pen, float x, float y, float width, float height, float cut)
		{
			Lines(g, pen, x + cut, y, x + width - cut, y, x + width, y + cut, x + width, y + height - cut, x + width - cut, y + height, x + cut, y + height, x, y + height - cut, x, y + cut, x + cut, y);
		}

		private static void Lines(Graphics g, Pen pen, params float[] coordinates)
		{
			PointF[] pointFItems = new PointF[coordinates.Length / 2];
			for (int i = 0; i < pointFItems.Length; i++)
			{
				pointFItems[i] = new PointF(coordinates[i * 2], coordinates[i * 2 + 1]);
			}
			if (pointFItems.Length > 1)
			{
				g.DrawLines(pen, pointFItems);
			}
		}
	}
}
