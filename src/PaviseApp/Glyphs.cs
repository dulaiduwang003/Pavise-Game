using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace PaviseApp
{
	internal static class Glyphs
	{
		public static void Draw(Graphics g, string name, Rectangle b, Color c)
		{
			float widthRatio = (float)b.Width / 24f;
			SmoothingMode smoothingMode = g.SmoothingMode;
			g.SmoothingMode = SmoothingMode.AntiAlias;
			using (Pen pen = new Pen(c, Math.Max(1.4f, 1.9f * widthRatio)))
			{
				using (SolidBrush solidBrush = new SolidBrush(c))
				{
					pen.StartCap = LineCap.Round;
					pen.EndCap = LineCap.Round;
					pen.LineJoin = LineJoin.Round;
					float left = b.X;
					float top = b.Y;
					switch (name)
					{
					case "game":
					{
						PointF[] points3 = new PointF[6]
						{
							P(left, top, widthRatio, 13f, 2f),
							P(left, top, widthRatio, 6f, 13.5f),
							P(left, top, widthRatio, 11f, 13.5f),
							P(left, top, widthRatio, 10f, 22f),
							P(left, top, widthRatio, 17f, 10.5f),
							P(left, top, widthRatio, 12f, 10.5f)
						};
						g.FillPolygon(solidBrush, points3);
						break;
					}
					case "tiles":
					{
						float scaledWidthRatio = 7.4f * widthRatio;
						float scaledWidthRatio2 = 2.2f * widthRatio;
						for (int m = 0; m < 4; m++)
						{
							float x3 = left + 3.5f * widthRatio + (float)(m % 2) * (scaledWidthRatio + scaledWidthRatio2);
							float y3 = top + 3.5f * widthRatio + (float)(m / 2) * (scaledWidthRatio + scaledWidthRatio2);
							using (GraphicsPath path6 = Theme.Rounded(Rectangle.Round(new RectangleF(x3, y3, scaledWidthRatio, scaledWidthRatio)), (int)(2f * widthRatio)))
							{
								g.DrawPath(pen, path6);
							}
						}
						break;
					}
					case "gamepad":
					{
						RectangleF value3 = new RectangleF(left + 2.8f * widthRatio, top + 7.2f * widthRatio, 18.4f * widthRatio, 9.6f * widthRatio);
						using (GraphicsPath path4 = Theme.Rounded(Rectangle.Round(value3), (int)(4.4f * widthRatio)))
						{
							g.DrawPath(pen, path4);
						}
						using (Pen pen9 = new Pen(c, Math.Max(1.2f, 1.6f * widthRatio)))
						{
							pen9.StartCap = LineCap.Round;
							pen9.EndCap = LineCap.Round;
							g.DrawLine(pen9, P(left, top, widthRatio, 6.4f, 12f), P(left, top, widthRatio, 10.2f, 12f));
							g.DrawLine(pen9, P(left, top, widthRatio, 8.3f, 10.1f), P(left, top, widthRatio, 8.3f, 13.9f));
						}
						g.FillEllipse(solidBrush, left + 14.4f * widthRatio, top + 9.6f * widthRatio, 2.5f * widthRatio, 2.5f * widthRatio);
						g.FillEllipse(solidBrush, left + 16.9f * widthRatio, top + 12f * widthRatio, 2.5f * widthRatio, 2.5f * widthRatio);
						break;
					}
					case "lol":
					{
						using (Pen pen8 = new Pen(c, Math.Max(1.2f, 1.45f * widthRatio)))
						{
							g.DrawArc(pen8, left + 3.5f * widthRatio, top + 3.5f * widthRatio, 17f * widthRatio, 17f * widthRatio, -68f, 272f);
						}
						PointF[] points2 = new PointF[6]
						{
							P(left, top, widthRatio, 10.5f, 2.5f),
							P(left, top, widthRatio, 14.5f, 2.5f),
							P(left, top, widthRatio, 13.4f, 16.8f),
							P(left, top, widthRatio, 19.4f, 16.8f),
							P(left, top, widthRatio, 17.5f, 21.2f),
							P(left, top, widthRatio, 8.8f, 21.2f)
						};
						g.FillPolygon(solidBrush, points2);
						break;
					}
					case "shield":
						DrawShield(g, pen, left, top, widthRatio);
						break;
					case "acshield":
					{
						DrawShield(g, pen, left, top, widthRatio);
						using (Pen pen10 = new Pen(c, Math.Max(1.2f, 1.6f * widthRatio)))
						{
							pen10.StartCap = LineCap.Round;
							pen10.EndCap = LineCap.Round;
							g.DrawLine(pen10, P(left, top, widthRatio, 10.2f, 8.2f), P(left, top, widthRatio, 10.2f, 13.2f));
							g.DrawLine(pen10, P(left, top, widthRatio, 13.8f, 8.2f), P(left, top, widthRatio, 13.8f, 13.2f));
						}
						break;
					}
					case "white":
					{
						RectangleF value4 = new RectangleF(left + 3.5f * widthRatio, top + 3.5f * widthRatio, 17f * widthRatio, 17f * widthRatio);
						using (GraphicsPath path5 = Theme.Rounded(Rectangle.Round(value4), (int)(4f * widthRatio)))
						{
							g.DrawPath(pen, path5);
						}
						g.DrawLines(pen, new PointF[3]
						{
							P(left, top, widthRatio, 8.5f, 12f),
							P(left, top, widthRatio, 11f, 15f),
							P(left, top, widthRatio, 16f, 8.5f)
						});
						break;
					}
					case "log":
						g.DrawLine(pen, P(left, top, widthRatio, 5f, 7f).X, P(left, top, widthRatio, 5f, 7f).Y, P(left, top, widthRatio, 19f, 7f).X, P(left, top, widthRatio, 19f, 7f).Y);
						g.DrawLine(pen, P(left, top, widthRatio, 5f, 12f).X, P(left, top, widthRatio, 5f, 12f).Y, P(left, top, widthRatio, 19f, 12f).X, P(left, top, widthRatio, 19f, 12f).Y);
						g.DrawLine(pen, P(left, top, widthRatio, 5f, 17f).X, P(left, top, widthRatio, 5f, 17f).Y, P(left, top, widthRatio, 14f, 17f).X, P(left, top, widthRatio, 14f, 17f).Y);
						break;
					case "chart":
						g.DrawLine(pen, P(left, top, widthRatio, 4f, 19f), P(left, top, widthRatio, 4f, 5f));
						g.DrawLine(pen, P(left, top, widthRatio, 4f, 19f), P(left, top, widthRatio, 20f, 19f));
						g.DrawLines(pen, new PointF[4]
						{
							P(left, top, widthRatio, 6f, 15f),
							P(left, top, widthRatio, 10f, 11f),
							P(left, top, widthRatio, 13f, 13f),
							P(left, top, widthRatio, 19f, 6f)
						});
						break;
					case "pulse":
						g.DrawLines(pen, new PointF[6]
						{
							P(left, top, widthRatio, 3f, 14f),
							P(left, top, widthRatio, 7f, 14f),
							P(left, top, widthRatio, 9f, 6f),
							P(left, top, widthRatio, 12f, 19f),
							P(left, top, widthRatio, 15f, 14f),
							P(left, top, widthRatio, 21f, 14f)
						});
						break;
					case "settings":
						g.DrawLine(pen, P(left, top, widthRatio, 4f, 8f).X, P(left, top, widthRatio, 4f, 8f).Y, P(left, top, widthRatio, 20f, 8f).X, P(left, top, widthRatio, 20f, 8f).Y);
						g.DrawLine(pen, P(left, top, widthRatio, 4f, 16f).X, P(left, top, widthRatio, 4f, 16f).Y, P(left, top, widthRatio, 20f, 16f).X, P(left, top, widthRatio, 20f, 16f).Y);
						Knob(g, solidBrush, c, P(left, top, widthRatio, 9f, 8f), 2.6f * widthRatio);
						Knob(g, solidBrush, c, P(left, top, widthRatio, 15f, 16f), 2.6f * widthRatio);
						break;
					case "gear":
					{
						g.DrawEllipse(pen, left + 5.5f * widthRatio, top + 5.5f * widthRatio, 13f * widthRatio, 13f * widthRatio);
						g.DrawEllipse(pen, left + 9.5f * widthRatio, top + 9.5f * widthRatio, 5f * widthRatio, 5f * widthRatio);
						for (int l = 0; l < 8; l++)
						{
							double ratio = (double)l * Math.PI / 4.0;
							float x = left + (12f + (float)Math.Cos(ratio) * 8f) * widthRatio;
							float y = top + (12f + (float)Math.Sin(ratio) * 8f) * widthRatio;
							float x2 = left + (12f + (float)Math.Cos(ratio) * 10f) * widthRatio;
							float y2 = top + (12f + (float)Math.Sin(ratio) * 10f) * widthRatio;
							g.DrawLine(pen, x, y, x2, y2);
						}
						break;
					}
					case "gpu":
					{
						RectangleF value2 = new RectangleF(left + 2.5f * widthRatio, top + 6.5f * widthRatio, 19f * widthRatio, 11f * widthRatio);
						using (GraphicsPath path3 = Theme.Rounded(Rectangle.Round(value2), (int)(2.5f * widthRatio)))
						{
							g.DrawPath(pen, path3);
						}
						using (Pen pen6 = new Pen(c, Math.Max(1.2f, 1.5f * widthRatio)))
						{
							g.DrawEllipse(pen6, left + 5.2f * widthRatio, top + 8.7f * widthRatio, 6.6f * widthRatio, 6.6f * widthRatio);
						}
						using (Pen pen7 = new Pen(c, Math.Max(1f, 1.3f * widthRatio)))
						{
							for (int k = 0; k < 3; k++)
							{
								float top2 = top + (9.6f + (float)k * 2.4f) * widthRatio;
								g.DrawLine(pen7, left + 14.6f * widthRatio, top2, left + 18.6f * widthRatio, top2);
							}
						}
						break;
					}
					case "chip":
					{
						RectangleF value = new RectangleF(left + 6.5f * widthRatio, top + 6.5f * widthRatio, 11f * widthRatio, 11f * widthRatio);
						using (GraphicsPath path2 = Theme.Rounded(Rectangle.Round(value), (int)(2f * widthRatio)))
						{
							g.DrawPath(pen, path2);
						}
						g.FillRectangle(solidBrush, left + 10.6f * widthRatio, top + 10.6f * widthRatio, 2.8f * widthRatio, 2.8f * widthRatio);
						using (Pen pen5 = new Pen(c, Math.Max(1f, 1.4f * widthRatio)))
						{
							for (int j = 0; j < 3; j++)
							{
								float scaledWidthRatio3 = (9.4f + (float)j * 2.6f) * widthRatio;
								g.DrawLine(pen5, left + scaledWidthRatio3, top + 6.5f * widthRatio, left + scaledWidthRatio3, top + 3.4f * widthRatio);
								g.DrawLine(pen5, left + scaledWidthRatio3, top + 17.5f * widthRatio, left + scaledWidthRatio3, top + 20.6f * widthRatio);
								g.DrawLine(pen5, left + 6.5f * widthRatio, top + scaledWidthRatio3, left + 3.4f * widthRatio, top + scaledWidthRatio3);
								g.DrawLine(pen5, left + 17.5f * widthRatio, top + scaledWidthRatio3, left + 20.6f * widthRatio, top + scaledWidthRatio3);
							}
						}
						break;
					}
					case "info":
						g.DrawEllipse(pen, left + 3.5f * widthRatio, top + 3.5f * widthRatio, 17f * widthRatio, 17f * widthRatio);
						g.DrawLine(pen, P(left, top, widthRatio, 12f, 11.2f), P(left, top, widthRatio, 12f, 16.8f));
						g.FillEllipse(solidBrush, left + 10.6f * widthRatio, top + 6.2f * widthRatio, 2.8f * widthRatio, 2.8f * widthRatio);
						break;
					case "search":
						g.DrawEllipse(pen, left + 4.5f * widthRatio, top + 4.5f * widthRatio, 11.5f * widthRatio, 11.5f * widthRatio);
						g.DrawLine(pen, P(left, top, widthRatio, 14.8f, 14.8f), P(left, top, widthRatio, 20.2f, 20.2f));
						break;
					case "power":
					{
						g.DrawLine(pen, P(left, top, widthRatio, 9.2f, 2.8f), P(left, top, widthRatio, 9.2f, 6.8f));
						g.DrawLine(pen, P(left, top, widthRatio, 14.8f, 2.8f), P(left, top, widthRatio, 14.8f, 6.8f));
						PointF[] points = new PointF[6]
						{
							P(left, top, widthRatio, 6.4f, 6.8f),
							P(left, top, widthRatio, 17.6f, 6.8f),
							P(left, top, widthRatio, 17.6f, 11.6f),
							P(left, top, widthRatio, 15.2f, 14.6f),
							P(left, top, widthRatio, 8.8f, 14.6f),
							P(left, top, widthRatio, 6.4f, 11.6f)
						};
						g.DrawPolygon(pen, points);
						g.DrawLine(pen, P(left, top, widthRatio, 12f, 14.6f), P(left, top, widthRatio, 12f, 17.8f));
						g.DrawBezier(pen, P(left, top, widthRatio, 12f, 17.8f), P(left, top, widthRatio, 12f, 21.2f), P(left, top, widthRatio, 14.6f, 21.4f), P(left, top, widthRatio, 18.2f, 21f));
						break;
					}
					case "sun":
					{
						using (Pen pen4 = new Pen(c, Math.Max(1.1f, 1.55f * widthRatio)))
						{
							pen4.StartCap = LineCap.Round;
							pen4.EndCap = LineCap.Round;
							g.DrawEllipse(pen4, left + 7.6f * widthRatio, top + 7.6f * widthRatio, 8.8f * widthRatio, 8.8f * widthRatio);
							for (int i = 0; i < 8; i++)
							{
								double ratio2 = (double)i * Math.PI / 4.0;
								float cosResult = (float)Math.Cos(ratio2);
								float sinResult = (float)Math.Sin(ratio2);
								g.DrawLine(pen4, left + (12f + cosResult * 7.6f) * widthRatio, top + (12f + sinResult * 7.6f) * widthRatio, left + (12f + cosResult * 10.4f) * widthRatio, top + (12f + sinResult * 10.4f) * widthRatio);
							}
						}
						break;
					}
					case "moon":
					{
						using (Pen pen3 = new Pen(c, Math.Max(1.1f, 1.55f * widthRatio)))
						{
							using (GraphicsPath path = Crescent(left + 12f * widthRatio, top + 12f * widthRatio, 8.6f * widthRatio, 7.4f * widthRatio, -38f))
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
							graphicsPath.AddBezier(P(left, top, widthRatio, 12f, 20.5f), P(left, top, widthRatio, 3.5f, 14.5f), P(left, top, widthRatio, 2.5f, 6.5f), P(left, top, widthRatio, 7.5f, 4.5f));
							graphicsPath.AddBezier(P(left, top, widthRatio, 7.5f, 4.5f), P(left, top, widthRatio, 10.2f, 3.6f), P(left, top, widthRatio, 11.6f, 5.6f), P(left, top, widthRatio, 12f, 7f));
							graphicsPath.AddBezier(P(left, top, widthRatio, 12f, 7f), P(left, top, widthRatio, 12.4f, 5.6f), P(left, top, widthRatio, 13.8f, 3.6f), P(left, top, widthRatio, 16.5f, 4.5f));
							graphicsPath.AddBezier(P(left, top, widthRatio, 16.5f, 4.5f), P(left, top, widthRatio, 21.5f, 6.5f), P(left, top, widthRatio, 20.5f, 14.5f), P(left, top, widthRatio, 12f, 20.5f));
							graphicsPath.CloseFigure();
							g.FillPath(solidBrush, graphicsPath);
						}
						break;
					}
					case "globe":
					{
						g.DrawEllipse(pen, left + 3.5f * widthRatio, top + 3.5f * widthRatio, 17f * widthRatio, 17f * widthRatio);
						using (Pen pen2 = new Pen(c, Math.Max(1.1f, 1.55f * widthRatio)))
						{
							pen2.StartCap = LineCap.Round;
							pen2.EndCap = LineCap.Round;
							g.DrawEllipse(pen2, left + 8.4f * widthRatio, top + 3.5f * widthRatio, 7.2f * widthRatio, 17f * widthRatio);
							g.DrawLine(pen2, P(left, top, widthRatio, 3.5f, 12f), P(left, top, widthRatio, 20.5f, 12f));
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
			float pIRatio = (float)(Math.Acos((double)d / (2.0 * (double)r)) * 180.0 / Math.PI);
			double ratio = (double)tilt * Math.PI / 180.0;
			float cxOffset = cx + d * (float)Math.Cos(ratio);
			float cyOffset = cy + d * (float)Math.Sin(ratio);
			GraphicsPath graphicsPath = new GraphicsPath();
			graphicsPath.AddArc(cx - r, cy - r, r * 2f, r * 2f, tilt + pIRatio, 360f - 2f * pIRatio);
			graphicsPath.AddArc(cxOffset - r, cyOffset - r, r * 2f, r * 2f, 180f + tilt + pIRatio, -2f * pIRatio);
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
