using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace PaviseApp
{
	internal static class IconArt
	{
		private static readonly Color Ink = Color.FromArgb(24, 26, 32);

		private static readonly Color Rim = Color.FromArgb(40, 255, 255, 255);

		[DllImport("user32.dll")]
		private static extern bool DestroyIcon(IntPtr h);

		public static Icon MakeIcon(int px)
		{
			return MakeIcon(px, PerformancePreset.Standard, true);
		}

		public static Icon MakeIcon(int px, PerformancePreset mode, bool enabled)
		{
			using (Bitmap bitmap = Render(px, mode, enabled))
			{
				IntPtr hicon = bitmap.GetHicon();
				try
				{
					using (Icon icon = Icon.FromHandle(hicon))
					{
						return (Icon)icon.Clone();
					}
				}
				finally
				{
					DestroyIcon(hicon);
				}
			}
		}

		public static Icon MakeMultiIcon()
		{
			return MakeMultiIcon(PerformancePreset.Standard, true);
		}

		public static Icon MakeMultiIcon(PerformancePreset mode, bool enabled)
		{
			byte[] buffer = IcoWriter.Build(new int[9] { 16, 20, 24, 32, 40, 48, 64, 128, 256 }, mode, enabled);
			using (MemoryStream stream = new MemoryStream(buffer))
			{
				return new Icon(stream);
			}
		}

		public static Bitmap Render(int px)
		{
			return Render(px, PerformancePreset.Standard, true);
		}

		public static Bitmap Render(int px, PerformancePreset mode, bool enabled)
		{
			Bitmap bitmap = new Bitmap(px, px, PixelFormat.Format32bppArgb);
			using (Graphics graphics = Graphics.FromImage(bitmap))
			{
				graphics.SmoothingMode = SmoothingMode.AntiAlias;
				graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
				graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
				graphics.Clear(Color.Transparent);
				graphics.ScaleTransform((float)px / 100f, (float)px / 100f);
				Color color = Theme.ModeColor(mode);
				Color color2 = (enabled ? color : Col.Lerp(color, Color.FromArgb(105, 110, 121), 0.58f));
				Color color3 = (enabled ? Col.Alpha(color2, 105) : Rim);
				using (GraphicsPath path = Squircle(2.5f, 2.5f, 95f, 95f, 25f))
				{
					using (SolidBrush brush = new SolidBrush(Ink))
					{
						graphics.FillPath(brush, path);
					}
					using (Pen pen = new Pen(color3, 1.8f))
					{
						graphics.DrawPath(pen, path);
					}
				}
				using (SolidBrush brush2 = new SolidBrush(color2))
				{
					graphics.FillPolygon(brush2, BoltPts());
					return bitmap;
				}
			}
		}

		private static GraphicsPath Squircle(float x, float y, float w, float h, float r)
		{
			GraphicsPath graphicsPath = new GraphicsPath();
			float scaledR = r * 2f;
			graphicsPath.AddArc(x, y, scaledR, scaledR, 180f, 90f);
			graphicsPath.AddArc(x + w - scaledR, y, scaledR, scaledR, 270f, 90f);
			graphicsPath.AddArc(x + w - scaledR, y + h - scaledR, scaledR, scaledR, 0f, 90f);
			graphicsPath.AddArc(x, y + h - scaledR, scaledR, scaledR, 90f, 90f);
			graphicsPath.CloseFigure();
			return graphicsPath;
		}

		private static PointF[] BoltPts()
		{
			PointF[] pointFItems = new PointF[6]
			{
				new PointF(58f, 26f),
				new PointF(38f, 54f),
				new PointF(50f, 54f),
				new PointF(43f, 76f),
				new PointF(65f, 44f),
				new PointF(53f, 44f)
			};
			for (int i = 0; i < pointFItems.Length; i++)
			{
				pointFItems[i] = new PointF(50f + (pointFItems[i].X - 50f) * 1.36f, 50f + (pointFItems[i].Y - 50f) * 1.36f);
			}
			return pointFItems;
		}
	}
}
