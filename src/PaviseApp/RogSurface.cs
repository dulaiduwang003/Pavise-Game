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
					int num = bounds.Width / p.SweepDivisor;
					int thickness = Theme.S(p.BandThickness);
					DrawArmorBand(g, bounds, -num, bounds.Height * 14 / 100, bounds.Right + num, bounds.Height * 38 / 100, thickness, p.FaceAlpha, p.EdgeAlpha);
					DrawArmorBand(g, bounds, -num, bounds.Height * 43 / 100, bounds.Right + num, bounds.Height * 67 / 100, thickness, p.FaceAlpha, p.EdgeAlpha);
					DrawArmorBand(g, bounds, -num, bounds.Height * 72 / 100, bounds.Right + num, bounds.Height * 96 / 100, thickness, p.FaceAlpha, p.EdgeAlpha);
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
			int num = Math.Max(1, Theme.S(p.MeshStep));
			int num2 = r.Height + r.Width;
			using (Pen pen = new Pen(Col.Alpha(Theme.StrokeHi, p.MeshAlpha), DsSpace.Line(1)))
			{
				for (int i = r.Left - num2; i < r.Right + num2; i += num)
				{
					g.DrawLine(pen, i, r.Top, i + r.Height, r.Bottom);
				}
			}
			int a = Math.Max(2, p.MeshAlpha / 2);
			using (Pen pen2 = new Pen(Col.Alpha(Theme.Accent, a), DsSpace.Line(1)))
			{
				for (int j = r.Left; j < r.Right + r.Height; j += num * 4)
				{
					g.DrawLine(pen2, j, r.Top, j - r.Height, r.Bottom);
				}
			}
		}

		private static void DrawArmorBand(Graphics g, Rectangle clip, int x1, int y1, int x2, int y2, int thickness, int faceAlpha, int edgeAlpha)
		{
			double num = x2 - x1;
			double num2 = y2 - y1;
			double num3 = Math.Sqrt(num * num + num2 * num2);
			if (num3 < 1.0)
			{
				return;
			}
			int num4 = (int)Math.Round((0.0 - num2) / num3 * (double)thickness);
			int num5 = (int)Math.Round(num / num3 * (double)thickness);
			Point[] array = new Point[4]
			{
				new Point(x1 + num4, y1 + num5),
				new Point(x2 + num4, y2 + num5),
				new Point(x2 - num4, y2 - num5),
				new Point(x1 - num4, y1 - num5)
			};
			Rectangle rect = new Rectangle(clip.Left, Math.Min(y1, y2) - thickness, Math.Max(1, clip.Width), Math.Max(1, Math.Abs(y2 - y1) + thickness * 2));
			using (LinearGradientBrush brush = new LinearGradientBrush(rect, Col.Alpha(Theme.Accent, Math.Max(1, faceAlpha / 3)), Col.Alpha(Theme.Accent, faceAlpha), LinearGradientMode.Horizontal))
			{
				g.FillPolygon(brush, array);
			}
			using (Pen pen = new Pen(Col.Alpha(Theme.Accent, edgeAlpha), DsSpace.Line(1)))
			{
				using (Pen pen2 = new Pen(Col.Alpha(Theme.StrokeHi, Math.Max(4, edgeAlpha - 10)), DsSpace.Line(1)))
				{
					g.DrawLine(pen, array[0], array[1]);
					g.DrawLine(pen2, array[3], array[2]);
				}
			}
			using (Pen pen3 = new Pen(Col.Alpha(Theme.Accent, Math.Max(4, edgeAlpha / 2)), DsSpace.Line(1)))
			{
				int num6 = num4 / 3;
				int num7 = num5 / 3;
				g.DrawLine(pen3, x1 + num6, y1 + num7, x2 + num6, y2 + num7);
			}
		}

		private static void DrawCircuitBus(Graphics g, Rectangle r, DsSurfaceProfile p)
		{
			int num = r.Right - Theme.S(p.BusInset);
			int num2 = r.Top + Theme.S(p.BusTop);
			int num3 = r.Bottom - Theme.S(p.BusBottom);
			int edgeAlpha = p.EdgeAlpha;
			using (Pen pen = new Pen(Col.Alpha(Theme.StrokeHi, Math.Max(8, edgeAlpha - 7)), DsSpace.Line(1)))
			{
				using (Pen pen2 = new Pen(Col.Alpha(Theme.Accent, edgeAlpha), DsSpace.Line(1)))
				{
					g.DrawLine(pen, num, num2, num, num3);
					g.DrawLine(pen2, num + Theme.S(2), num2 + Theme.S(18), num + Theme.S(2), num3 - Theme.S(26));
					for (int i = 0; i < p.BusLevels.Length; i++)
					{
						int num4 = r.Top + r.Height * p.BusLevels[i] / 100;
						int num5 = Theme.S(p.BusReach[i]);
						int num6 = num - num5;
						g.DrawLine(pen, num, num4, num6 + Theme.S(18), num4);
						g.DrawLine(pen2, num6 + Theme.S(18), num4, num6, num4 - Theme.S(18));
						g.DrawLine(pen2, num6, num4 - Theme.S(18), num6 - Theme.S(24), num4 - Theme.S(18));
						using (SolidBrush brush = new SolidBrush(Col.Alpha(Theme.Accent, edgeAlpha + 28)))
						{
							g.FillRectangle(brush, num6 - Theme.S(2), num4 - Theme.S(20), Theme.S(4), Theme.S(4));
							g.FillRectangle(brush, num6 - Theme.S(28), num4 - Theme.S(20), Theme.S(4), Theme.S(4));
						}
					}
				}
			}
			int num7 = Math.Max(1, Theme.S(p.TickStep));
			using (Pen pen3 = new Pen(Col.Alpha(Theme.StrokeHi, Math.Max(6, edgeAlpha - 12)), DsSpace.Line(1)))
			{
				for (int j = num2; j < num3; j += num7)
				{
					g.DrawLine(pen3, num - Theme.S(8), j, num, j);
				}
			}
		}

		private static void DrawCornerCuts(Graphics g, Rectangle r, DsSurfaceProfile p)
		{
			using (Pen pen = new Pen(Col.Alpha(Theme.Accent, Math.Max(8, p.EdgeAlpha - 5)), DsSpace.Line(1)))
			{
				int num = Theme.S(p.CornerCut);
				int num2 = Theme.S(p.CornerReach);
				g.DrawLine(pen, r.Left + Theme.S(10), r.Top + num, r.Left + num2, r.Top + num);
				g.DrawLine(pen, r.Left + num2, r.Top + num, r.Left + num2 + num, r.Top);
				g.DrawLine(pen, r.Right - Theme.S(10), r.Bottom - num, r.Right - num2, r.Bottom - num);
				g.DrawLine(pen, r.Right - num2, r.Bottom - num, r.Right - num2 - num, r.Bottom);
			}
			if (p.CodeMinHeight == 0 || r.Height >= Theme.S(p.CodeMinHeight))
			{
				TextRenderer.DrawText(g, p.Code, DsType.Mono(DsMono.Surface), new Rectangle(r.Right - Theme.S(p.CodeRight), r.Bottom - Theme.S(24), Theme.S(p.CodeWidth), Theme.S(13)), Col.Alpha(Theme.Faint, DsSurfaces.CodeAlpha), TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.PreserveGraphicsTranslateTransform | TextFormatFlags.NoPadding);
			}
		}
	}
}
