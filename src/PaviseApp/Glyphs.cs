using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace PaviseApp
{
	internal static class Glyphs
	{
		public static void Draw(Graphics g, string name, Rectangle b, Color c)
		{
			float num = (float)b.Width / 24f;
			SmoothingMode smoothingMode = g.SmoothingMode;
			g.SmoothingMode = SmoothingMode.AntiAlias;
			using (Pen pen = new Pen(c, Math.Max(1.4f, 1.9f * num)))
			{
				using (SolidBrush solidBrush = new SolidBrush(c))
				{
					pen.StartCap = LineCap.Round;
					pen.EndCap = LineCap.Round;
					pen.LineJoin = LineJoin.Round;
					float num2 = b.X;
					float num3 = b.Y;
					switch (name)
					{
					case "game":
					{
						PointF[] points3 = new PointF[6]
						{
							P(num2, num3, num, 13f, 2f),
							P(num2, num3, num, 6f, 13.5f),
							P(num2, num3, num, 11f, 13.5f),
							P(num2, num3, num, 10f, 22f),
							P(num2, num3, num, 17f, 10.5f),
							P(num2, num3, num, 12f, 10.5f)
						};
						g.FillPolygon(solidBrush, points3);
						break;
					}
					case "tiles":
					{
						float num10 = 7.4f * num;
						float num11 = 2.2f * num;
						for (int m = 0; m < 4; m++)
						{
							float x3 = num2 + 3.5f * num + (float)(m % 2) * (num10 + num11);
							float y3 = num3 + 3.5f * num + (float)(m / 2) * (num10 + num11);
							using (GraphicsPath path6 = Theme.Rounded(Rectangle.Round(new RectangleF(x3, y3, num10, num10)), (int)(2f * num)))
							{
								g.DrawPath(pen, path6);
							}
						}
						break;
					}
					case "gamepad":
					{
						RectangleF value3 = new RectangleF(num2 + 2.8f * num, num3 + 7.2f * num, 18.4f * num, 9.6f * num);
						using (GraphicsPath path4 = Theme.Rounded(Rectangle.Round(value3), (int)(4.4f * num)))
						{
							g.DrawPath(pen, path4);
						}
						using (Pen pen9 = new Pen(c, Math.Max(1.2f, 1.6f * num)))
						{
							pen9.StartCap = LineCap.Round;
							pen9.EndCap = LineCap.Round;
							g.DrawLine(pen9, P(num2, num3, num, 6.4f, 12f), P(num2, num3, num, 10.2f, 12f));
							g.DrawLine(pen9, P(num2, num3, num, 8.3f, 10.1f), P(num2, num3, num, 8.3f, 13.9f));
						}
						g.FillEllipse(solidBrush, num2 + 14.4f * num, num3 + 9.6f * num, 2.5f * num, 2.5f * num);
						g.FillEllipse(solidBrush, num2 + 16.9f * num, num3 + 12f * num, 2.5f * num, 2.5f * num);
						break;
					}
					case "lol":
					{
						using (Pen pen8 = new Pen(c, Math.Max(1.2f, 1.45f * num)))
						{
							g.DrawArc(pen8, num2 + 3.5f * num, num3 + 3.5f * num, 17f * num, 17f * num, -68f, 272f);
						}
						PointF[] points2 = new PointF[6]
						{
							P(num2, num3, num, 10.5f, 2.5f),
							P(num2, num3, num, 14.5f, 2.5f),
							P(num2, num3, num, 13.4f, 16.8f),
							P(num2, num3, num, 19.4f, 16.8f),
							P(num2, num3, num, 17.5f, 21.2f),
							P(num2, num3, num, 8.8f, 21.2f)
						};
						g.FillPolygon(solidBrush, points2);
						break;
					}
					case "shield":
						DrawShield(g, pen, num2, num3, num);
						break;
					case "acshield":
					{
						DrawShield(g, pen, num2, num3, num);
						using (Pen pen10 = new Pen(c, Math.Max(1.2f, 1.6f * num)))
						{
							pen10.StartCap = LineCap.Round;
							pen10.EndCap = LineCap.Round;
							g.DrawLine(pen10, P(num2, num3, num, 10.2f, 8.2f), P(num2, num3, num, 10.2f, 13.2f));
							g.DrawLine(pen10, P(num2, num3, num, 13.8f, 8.2f), P(num2, num3, num, 13.8f, 13.2f));
						}
						break;
					}
					case "white":
					{
						RectangleF value4 = new RectangleF(num2 + 3.5f * num, num3 + 3.5f * num, 17f * num, 17f * num);
						using (GraphicsPath path5 = Theme.Rounded(Rectangle.Round(value4), (int)(4f * num)))
						{
							g.DrawPath(pen, path5);
						}
						g.DrawLines(pen, new PointF[3]
						{
							P(num2, num3, num, 8.5f, 12f),
							P(num2, num3, num, 11f, 15f),
							P(num2, num3, num, 16f, 8.5f)
						});
						break;
					}
					case "log":
						g.DrawLine(pen, P(num2, num3, num, 5f, 7f).X, P(num2, num3, num, 5f, 7f).Y, P(num2, num3, num, 19f, 7f).X, P(num2, num3, num, 19f, 7f).Y);
						g.DrawLine(pen, P(num2, num3, num, 5f, 12f).X, P(num2, num3, num, 5f, 12f).Y, P(num2, num3, num, 19f, 12f).X, P(num2, num3, num, 19f, 12f).Y);
						g.DrawLine(pen, P(num2, num3, num, 5f, 17f).X, P(num2, num3, num, 5f, 17f).Y, P(num2, num3, num, 14f, 17f).X, P(num2, num3, num, 14f, 17f).Y);
						break;
					case "chart":
						g.DrawLine(pen, P(num2, num3, num, 4f, 19f), P(num2, num3, num, 4f, 5f));
						g.DrawLine(pen, P(num2, num3, num, 4f, 19f), P(num2, num3, num, 20f, 19f));
						g.DrawLines(pen, new PointF[4]
						{
							P(num2, num3, num, 6f, 15f),
							P(num2, num3, num, 10f, 11f),
							P(num2, num3, num, 13f, 13f),
							P(num2, num3, num, 19f, 6f)
						});
						break;
					case "pulse":
						g.DrawLines(pen, new PointF[6]
						{
							P(num2, num3, num, 3f, 14f),
							P(num2, num3, num, 7f, 14f),
							P(num2, num3, num, 9f, 6f),
							P(num2, num3, num, 12f, 19f),
							P(num2, num3, num, 15f, 14f),
							P(num2, num3, num, 21f, 14f)
						});
						break;
					case "settings":
						g.DrawLine(pen, P(num2, num3, num, 4f, 8f).X, P(num2, num3, num, 4f, 8f).Y, P(num2, num3, num, 20f, 8f).X, P(num2, num3, num, 20f, 8f).Y);
						g.DrawLine(pen, P(num2, num3, num, 4f, 16f).X, P(num2, num3, num, 4f, 16f).Y, P(num2, num3, num, 20f, 16f).X, P(num2, num3, num, 20f, 16f).Y);
						Knob(g, solidBrush, c, P(num2, num3, num, 9f, 8f), 2.6f * num);
						Knob(g, solidBrush, c, P(num2, num3, num, 15f, 16f), 2.6f * num);
						break;
					case "gear":
					{
						g.DrawEllipse(pen, num2 + 5.5f * num, num3 + 5.5f * num, 13f * num, 13f * num);
						g.DrawEllipse(pen, num2 + 9.5f * num, num3 + 9.5f * num, 5f * num, 5f * num);
						for (int l = 0; l < 8; l++)
						{
							double num9 = (double)l * Math.PI / 4.0;
							float x = num2 + (12f + (float)Math.Cos(num9) * 8f) * num;
							float y = num3 + (12f + (float)Math.Sin(num9) * 8f) * num;
							float x2 = num2 + (12f + (float)Math.Cos(num9) * 10f) * num;
							float y2 = num3 + (12f + (float)Math.Sin(num9) * 10f) * num;
							g.DrawLine(pen, x, y, x2, y2);
						}
						break;
					}
					case "gpu":
					{
						RectangleF value2 = new RectangleF(num2 + 2.5f * num, num3 + 6.5f * num, 19f * num, 11f * num);
						using (GraphicsPath path3 = Theme.Rounded(Rectangle.Round(value2), (int)(2.5f * num)))
						{
							g.DrawPath(pen, path3);
						}
						using (Pen pen6 = new Pen(c, Math.Max(1.2f, 1.5f * num)))
						{
							g.DrawEllipse(pen6, num2 + 5.2f * num, num3 + 8.7f * num, 6.6f * num, 6.6f * num);
						}
						using (Pen pen7 = new Pen(c, Math.Max(1f, 1.3f * num)))
						{
							for (int k = 0; k < 3; k++)
							{
								float num8 = num3 + (9.6f + (float)k * 2.4f) * num;
								g.DrawLine(pen7, num2 + 14.6f * num, num8, num2 + 18.6f * num, num8);
							}
						}
						break;
					}
					case "chip":
					{
						RectangleF value = new RectangleF(num2 + 6.5f * num, num3 + 6.5f * num, 11f * num, 11f * num);
						using (GraphicsPath path2 = Theme.Rounded(Rectangle.Round(value), (int)(2f * num)))
						{
							g.DrawPath(pen, path2);
						}
						g.FillRectangle(solidBrush, num2 + 10.6f * num, num3 + 10.6f * num, 2.8f * num, 2.8f * num);
						using (Pen pen5 = new Pen(c, Math.Max(1f, 1.4f * num)))
						{
							for (int j = 0; j < 3; j++)
							{
								float num7 = (9.4f + (float)j * 2.6f) * num;
								g.DrawLine(pen5, num2 + num7, num3 + 6.5f * num, num2 + num7, num3 + 3.4f * num);
								g.DrawLine(pen5, num2 + num7, num3 + 17.5f * num, num2 + num7, num3 + 20.6f * num);
								g.DrawLine(pen5, num2 + 6.5f * num, num3 + num7, num2 + 3.4f * num, num3 + num7);
								g.DrawLine(pen5, num2 + 17.5f * num, num3 + num7, num2 + 20.6f * num, num3 + num7);
							}
						}
						break;
					}
					case "info":
						g.DrawEllipse(pen, num2 + 3.5f * num, num3 + 3.5f * num, 17f * num, 17f * num);
						g.DrawLine(pen, P(num2, num3, num, 12f, 11.2f), P(num2, num3, num, 12f, 16.8f));
						g.FillEllipse(solidBrush, num2 + 10.6f * num, num3 + 6.2f * num, 2.8f * num, 2.8f * num);
						break;
					case "search":
						g.DrawEllipse(pen, num2 + 4.5f * num, num3 + 4.5f * num, 11.5f * num, 11.5f * num);
						g.DrawLine(pen, P(num2, num3, num, 14.8f, 14.8f), P(num2, num3, num, 20.2f, 20.2f));
						break;
					case "power":
					{
						g.DrawLine(pen, P(num2, num3, num, 9.2f, 2.8f), P(num2, num3, num, 9.2f, 6.8f));
						g.DrawLine(pen, P(num2, num3, num, 14.8f, 2.8f), P(num2, num3, num, 14.8f, 6.8f));
						PointF[] points = new PointF[6]
						{
							P(num2, num3, num, 6.4f, 6.8f),
							P(num2, num3, num, 17.6f, 6.8f),
							P(num2, num3, num, 17.6f, 11.6f),
							P(num2, num3, num, 15.2f, 14.6f),
							P(num2, num3, num, 8.8f, 14.6f),
							P(num2, num3, num, 6.4f, 11.6f)
						};
						g.DrawPolygon(pen, points);
						g.DrawLine(pen, P(num2, num3, num, 12f, 14.6f), P(num2, num3, num, 12f, 17.8f));
						g.DrawBezier(pen, P(num2, num3, num, 12f, 17.8f), P(num2, num3, num, 12f, 21.2f), P(num2, num3, num, 14.6f, 21.4f), P(num2, num3, num, 18.2f, 21f));
						break;
					}
					case "sun":
					{
						using (Pen pen4 = new Pen(c, Math.Max(1.1f, 1.55f * num)))
						{
							pen4.StartCap = LineCap.Round;
							pen4.EndCap = LineCap.Round;
							g.DrawEllipse(pen4, num2 + 7.6f * num, num3 + 7.6f * num, 8.8f * num, 8.8f * num);
							for (int i = 0; i < 8; i++)
							{
								double num4 = (double)i * Math.PI / 4.0;
								float num5 = (float)Math.Cos(num4);
								float num6 = (float)Math.Sin(num4);
								g.DrawLine(pen4, num2 + (12f + num5 * 7.6f) * num, num3 + (12f + num6 * 7.6f) * num, num2 + (12f + num5 * 10.4f) * num, num3 + (12f + num6 * 10.4f) * num);
							}
						}
						break;
					}
					case "moon":
					{
						using (Pen pen3 = new Pen(c, Math.Max(1.1f, 1.55f * num)))
						{
							using (GraphicsPath path = Crescent(num2 + 12f * num, num3 + 12f * num, 8.6f * num, 7.4f * num, -38f))
							{
								pen3.LineJoin = LineJoin.Round;
								g.DrawPath(pen3, path);
							}
						}
						break;
					}
					case "heart":
					{
						using (GraphicsPath graphicsPath = new GraphicsPath())
						{
							graphicsPath.AddBezier(P(num2, num3, num, 12f, 20.5f), P(num2, num3, num, 3.5f, 14.5f), P(num2, num3, num, 2.5f, 6.5f), P(num2, num3, num, 7.5f, 4.5f));
							graphicsPath.AddBezier(P(num2, num3, num, 7.5f, 4.5f), P(num2, num3, num, 10.2f, 3.6f), P(num2, num3, num, 11.6f, 5.6f), P(num2, num3, num, 12f, 7f));
							graphicsPath.AddBezier(P(num2, num3, num, 12f, 7f), P(num2, num3, num, 12.4f, 5.6f), P(num2, num3, num, 13.8f, 3.6f), P(num2, num3, num, 16.5f, 4.5f));
							graphicsPath.AddBezier(P(num2, num3, num, 16.5f, 4.5f), P(num2, num3, num, 21.5f, 6.5f), P(num2, num3, num, 20.5f, 14.5f), P(num2, num3, num, 12f, 20.5f));
							graphicsPath.CloseFigure();
							g.FillPath(solidBrush, graphicsPath);
						}
						break;
					}
					case "globe":
					{
						g.DrawEllipse(pen, num2 + 3.5f * num, num3 + 3.5f * num, 17f * num, 17f * num);
						using (Pen pen2 = new Pen(c, Math.Max(1.1f, 1.55f * num)))
						{
							pen2.StartCap = LineCap.Round;
							pen2.EndCap = LineCap.Round;
							g.DrawEllipse(pen2, num2 + 8.4f * num, num3 + 3.5f * num, 7.2f * num, 17f * num);
							g.DrawLine(pen2, P(num2, num3, num, 3.5f, 12f), P(num2, num3, num, 20.5f, 12f));
						}
						break;
					}
					}
				}
			}
			g.SmoothingMode = smoothingMode;
		}

		private static void DrawShield(Graphics g, Pen pen, float x, float y, float u)
		{
			using (GraphicsPath graphicsPath = new GraphicsPath())
			{
				graphicsPath.AddLines(new PointF[6]
				{
					P(x, y, u, 12f, 2.5f),
					P(x, y, u, 20f, 5.5f),
					P(x, y, u, 20f, 12f),
					P(x, y, u, 12f, 21.5f),
					P(x, y, u, 4f, 12f),
					P(x, y, u, 4f, 5.5f)
				});
				graphicsPath.CloseFigure();
				g.DrawPath(pen, graphicsPath);
			}
		}

		private static GraphicsPath Crescent(float cx, float cy, float r, float d, float tilt)
		{
			if (d > r * 1.94f)
			{
				d = r * 1.94f;
			}
			if (d < r * 0.2f)
			{
				d = r * 0.2f;
			}
			float num = (float)(Math.Acos((double)d / (2.0 * (double)r)) * 180.0 / Math.PI);
			double num2 = (double)tilt * Math.PI / 180.0;
			float num3 = cx + d * (float)Math.Cos(num2);
			float num4 = cy + d * (float)Math.Sin(num2);
			GraphicsPath graphicsPath = new GraphicsPath();
			graphicsPath.AddArc(cx - r, cy - r, r * 2f, r * 2f, tilt + num, 360f - 2f * num);
			graphicsPath.AddArc(num3 - r, num4 - r, r * 2f, r * 2f, 180f + tilt + num, -2f * num);
			graphicsPath.CloseFigure();
			RectangleF bounds = graphicsPath.GetBounds();
			using (Matrix matrix = new Matrix())
			{
				matrix.Translate(cx - (bounds.Left + bounds.Right) / 2f, cy - (bounds.Top + bounds.Bottom) / 2f);
				graphicsPath.Transform(matrix);
				return graphicsPath;
			}
		}

		private static PointF P(float ox, float oy, float u, float px, float py)
		{
			return new PointF(ox + px * u, oy + py * u);
		}

		private static void Knob(Graphics g, SolidBrush fill, Color c, PointF center, float r)
		{
			using (SolidBrush brush = new SolidBrush(Theme.Nav))
			{
				g.FillEllipse(brush, center.X - r, center.Y - r, r * 2f, r * 2f);
			}
			using (Pen pen = new Pen(c, Math.Max(1.4f, r * 0.7f)))
			{
				g.DrawEllipse(pen, center.X - r, center.Y - r, r * 2f, r * 2f);
			}
		}
	}
}
