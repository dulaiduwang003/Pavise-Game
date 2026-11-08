using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
	internal static class RogSurface
	{
		public static void Draw(Graphics g, Rectangle bounds, bool navigation)
		{
			if (bounds.Width <= 0 || bounds.Height <= 0)
			{
				return;
			}
			DsSurfaceProfile p = DsSurfaces.Profile(navigation);
			SmoothingMode smoothingMode = g.SmoothingMode;
			PixelOffsetMode pixelOffsetMode = g.PixelOffsetMode;
			using (Region region = g.Clip)
			{
				try
				{
					g.SetClip(bounds, CombineMode.Intersect);
					g.SmoothingMode = SmoothingMode.AntiAlias;
					g.PixelOffsetMode = PixelOffsetMode.HighQuality;
					DrawEtchedMesh(g, bounds, p);
					int widthRatio = bounds.Width / p.SweepDivisor;
					int thickness = Theme.S(p.BandThickness);
					DrawArmorBand(g, bounds, -widthRatio, bounds.Height * 14 / 100, bounds.Right + widthRatio, bounds.Height * 38 / 100, thickness, p.FaceAlpha, p.EdgeAlpha);
					DrawArmorBand(g, bounds, -widthRatio, bounds.Height * 43 / 100, bounds.Right + widthRatio, bounds.Height * 67 / 100, thickness, p.FaceAlpha, p.EdgeAlpha);
					DrawArmorBand(g, bounds, -widthRatio, bounds.Height * 72 / 100, bounds.Right + widthRatio, bounds.Height * 96 / 100, thickness, p.FaceAlpha, p.EdgeAlpha);
					DrawCircuitBus(g, bounds, p);
					DrawCornerCuts(g, bounds, p);
				}
				finally
				{
					g.SetClip(region, CombineMode.Replace);
					g.SmoothingMode = smoothingMode;
					g.PixelOffsetMode = pixelOffsetMode;
				}
			}
		}

		private static void DrawEtchedMesh(Graphics g, Rectangle r, DsSurfaceProfile p)
		{
			int sLimit = Math.Max(1, Theme.S(p.MeshStep));
			int heightOffset = r.Height + r.Width;
			using (Pen pen = new Pen(Col.Alpha(Theme.StrokeHi, p.MeshAlpha), DsSpace.Line(1)))
			{
				for (int i = r.Left - heightOffset; i < r.Right + heightOffset; i += sLimit)
				{
					g.DrawLine(pen, i, r.Top, i + r.Height, r.Bottom);
				}
			}
			int a = Math.Max(2, p.MeshAlpha / 2);
			using (Pen pen2 = new Pen(Col.Alpha(Theme.Accent, a), DsSpace.Line(1)))
			{
				for (int j = r.Left; j < r.Right + r.Height; j += sLimit * 4)
				{
					g.DrawLine(pen2, j, r.Top, j - r.Height, r.Bottom);
				}
			}
		}

		private static void DrawArmorBand(Graphics g, Rectangle clip, int x1, int y1, int x2, int y2, int thickness, int faceAlpha, int edgeAlpha)
		{
			double remainingX2 = x2 - x1;
			double remainingY2 = y2 - y1;
			double sqrtResult = Math.Sqrt(remainingX2 * remainingX2 + remainingY2 * remainingY2);
			if (sqrtResult < 1.0)
			{
				return;
			}
			int roundedValue = (int)Math.Round((0.0 - remainingY2) / sqrtResult * (double)thickness);
			int roundedValue2 = (int)Math.Round(remainingX2 / sqrtResult * (double)thickness);
			Point[] pointItems = new Point[4]
			{
				new Point(x1 + roundedValue, y1 + roundedValue2),
				new Point(x2 + roundedValue, y2 + roundedValue2),
				new Point(x2 - roundedValue, y2 - roundedValue2),
				new Point(x1 - roundedValue, y1 - roundedValue2)
			};
			Rectangle rect = new Rectangle(clip.Left, Math.Min(y1, y2) - thickness, Math.Max(1, clip.Width), Math.Max(1, Math.Abs(y2 - y1) + thickness * 2));
			using (LinearGradientBrush brush = new LinearGradientBrush(rect, Col.Alpha(Theme.Accent, Math.Max(1, faceAlpha / 3)), Col.Alpha(Theme.Accent, faceAlpha), LinearGradientMode.Horizontal))
			{
				g.FillPolygon(brush, pointItems);
			}
			using (Pen pen = new Pen(Col.Alpha(Theme.Accent, edgeAlpha), DsSpace.Line(1)))
			{
				using (Pen pen2 = new Pen(Col.Alpha(Theme.StrokeHi, Math.Max(4, edgeAlpha - 10)), DsSpace.Line(1)))
				{
					g.DrawLine(pen, pointItems[0], pointItems[1]);
					g.DrawLine(pen2, pointItems[3], pointItems[2]);
				}
			}
			using (Pen pen3 = new Pen(Col.Alpha(Theme.Accent, Math.Max(4, edgeAlpha / 2)), DsSpace.Line(1)))
			{
				int roundedValueRatio = roundedValue / 3;
				int roundedValue2Ratio = roundedValue2 / 3;
				g.DrawLine(pen3, x1 + roundedValueRatio, y1 + roundedValue2Ratio, x2 + roundedValueRatio, y2 + roundedValue2Ratio);
			}
		}

		private static void DrawCircuitBus(Graphics g, Rectangle r, DsSurfaceProfile p)
		{
			int left = r.Right - Theme.S(p.BusInset);
			int top = r.Top + Theme.S(p.BusTop);
			int height = r.Bottom - Theme.S(p.BusBottom);
			int edgeAlpha = p.EdgeAlpha;
			using (Pen pen = new Pen(Col.Alpha(Theme.StrokeHi, Math.Max(8, edgeAlpha - 7)), DsSpace.Line(1)))
			{
				using (Pen pen2 = new Pen(Col.Alpha(Theme.Accent, edgeAlpha), DsSpace.Line(1)))
				{
					g.DrawLine(pen, left, top, left, height);
					g.DrawLine(pen2, left + Theme.S(2), top + Theme.S(18), left + Theme.S(2), height - Theme.S(26));
					for (int i = 0; i < p.BusLevels.Length; i++)
					{
						int top2 = r.Top + r.Height * p.BusLevels[i] / 100;
						int scaledDimension = Theme.S(p.BusReach[i]);
						int width = left - scaledDimension;
						g.DrawLine(pen, left, top2, width + Theme.S(18), top2);
						g.DrawLine(pen2, width + Theme.S(18), top2, width, top2 - Theme.S(18));
						g.DrawLine(pen2, width, top2 - Theme.S(18), width - Theme.S(24), top2 - Theme.S(18));
						using (SolidBrush brush = new SolidBrush(Col.Alpha(Theme.Accent, edgeAlpha + 28)))
						{
							g.FillRectangle(brush, width - Theme.S(2), top2 - Theme.S(20), Theme.S(4), Theme.S(4));
							g.FillRectangle(brush, width - Theme.S(28), top2 - Theme.S(20), Theme.S(4), Theme.S(4));
						}
					}
				}
			}
			int sLimit = Math.Max(1, Theme.S(p.TickStep));
			using (Pen pen3 = new Pen(Col.Alpha(Theme.StrokeHi, Math.Max(6, edgeAlpha - 12)), DsSpace.Line(1)))
			{
				for (int j = top; j < height; j += sLimit)
				{
					g.DrawLine(pen3, left - Theme.S(8), j, left, j);
				}
			}
		}

		private static void DrawCornerCuts(Graphics g, Rectangle r, DsSurfaceProfile p)
		{
			using (Pen pen = new Pen(Col.Alpha(Theme.Accent, Math.Max(8, p.EdgeAlpha - 5)), DsSpace.Line(1)))
			{
				int scaledDimension = Theme.S(p.CornerCut);
				int scaledDimension2 = Theme.S(p.CornerReach);
				g.DrawLine(pen, r.Left + Theme.S(10), r.Top + scaledDimension, r.Left + scaledDimension2, r.Top + scaledDimension);
				g.DrawLine(pen, r.Left + scaledDimension2, r.Top + scaledDimension, r.Left + scaledDimension2 + scaledDimension, r.Top);
				g.DrawLine(pen, r.Right - Theme.S(10), r.Bottom - scaledDimension, r.Right - scaledDimension2, r.Bottom - scaledDimension);
				g.DrawLine(pen, r.Right - scaledDimension2, r.Bottom - scaledDimension, r.Right - scaledDimension2 - scaledDimension, r.Bottom);
			}
			if (p.CodeMinHeight == 0 || r.Height >= Theme.S(p.CodeMinHeight))
			{
				TextRenderer.DrawText(g, p.Code, DsType.Mono(DsMono.Surface), new Rectangle(r.Right - Theme.S(p.CodeRight), r.Bottom - Theme.S(24), Theme.S(p.CodeWidth), Theme.S(13)), Col.Alpha(Theme.Faint, DsSurfaces.CodeAlpha), TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.PreserveGraphicsTranslateTransform | TextFormatFlags.NoPadding);
			}
		}
	}
}
