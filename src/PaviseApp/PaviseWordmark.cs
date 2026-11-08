using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace PaviseApp
{
	internal static class PaviseWordmark
	{
		internal static GraphicsPath Create(Rectangle bounds)
		{
			GraphicsPath graphicsPath = new GraphicsPath(FillMode.Alternate);
			Add(graphicsPath, 0f, 0f, 0f, 16f, 0f, 20f, 4f, 20f, 10f, 15f, 15f, 5f, 15f, 5f, 11f, 14f, 11f, 16f, 9f, 16f, 6f, 14f, 4f, 5f, 4f, 5f, 9f, 0f, 11f);
			Add(graphicsPath, 0f, 0f, 14f, 5f, 12f, 5f, 24f, 0f, 24f);
			Add(graphicsPath, 23f, 0f, 24f, 8f, 0f, 15f, 0f, 22f, 24f, 17f, 24f, 15f, 18f, 7f, 18f, 5f, 24f);
			Add(graphicsPath, 23f, 11.5f, 5f, 8.3f, 14f, 14f, 14f);
			Add(graphicsPath, 48f, 0f, 0f, 5f, 0f, 12f, 17f, 18f, 0f, 23f, 0f, 14.5f, 24f, 9.5f, 24f);
			Add(graphicsPath, 74f, 0f, 0f, 11f, 0f, 11f, 4f, 7.5f, 4f, 7.5f, 10f, 3.5f, 12f, 3.5f, 4f, 0f, 4f);
			Add(graphicsPath, 74f, 3.5f, 15f, 7.5f, 13f, 7.5f, 20f, 11f, 20f, 11f, 24f, 0f, 24f, 0f, 20f, 3.5f, 20f);
			Add(graphicsPath, 88f, 20f, 0f, 4f, 0f, 0f, 4f, 0f, 11f, 4f, 15f, 15f, 15f, 15f, 20f, 0f, 20f, 0f, 24f, 16f, 24f, 20f, 20f, 20f, 13f, 16f, 9f, 5f, 9f, 5f, 4f, 20f, 4f);
			Add(graphicsPath, 111f, 3f, 0f, 20f, 0f, 18f, 4f, 0f, 4f);
			Add(graphicsPath, 111f, 2f, 10f, 17f, 10f, 15f, 14f, 0f, 14f);
			Add(graphicsPath, 111f, 0f, 20f, 20f, 20f, 18f, 24f, 0f, 24f);
			float minLimit = Math.Max(0.001f, Math.Min((float)Math.Min(Theme.S(21), bounds.Height) / 24f, (float)Math.Max(1, bounds.Width - 1) / 136f));
			using (Matrix matrix = new Matrix(minLimit, 0f, -0.2f * minLimit, minLimit, (float)bounds.Left + 4.8f * minLimit, (float)bounds.Top + ((float)bounds.Height - 24f * minLimit) / 2f))
			{
				graphicsPath.Transform(matrix);
				return graphicsPath;
			}
		}

		private static void Add(GraphicsPath path, float left, params float[] coordinates)
		{
			PointF[] pointFItems = new PointF[coordinates.Length / 2];
			for (int i = 0; i < pointFItems.Length; i++)
			{
				pointFItems[i] = new PointF(left + coordinates[i * 2], coordinates[i * 2 + 1]);
			}
			path.AddPolygon(pointFItems);
		}
	}
}
