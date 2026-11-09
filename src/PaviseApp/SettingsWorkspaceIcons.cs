using System.Drawing;
using System.Drawing.Drawing2D;

namespace PaviseApp
{
    internal static class SettingsWorkspaceIcons
    {
        internal static void Draw(Graphics graphics, Rectangle bounds, string key, Color ink)
        {
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                return;
            }
            GraphicsState gstate = graphics.Save();
            try
            {
                graphics.TranslateTransform(bounds.Left, bounds.Top);
                graphics.ScaleTransform((float)bounds.Width / 32f, (float)bounds.Height / 32f);
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (Pen pen = new Pen(ink, 1.8f))
                {
                    LineCap startCap = (pen.EndCap = LineCap.Round);
                    pen.StartCap = startCap;
                    pen.LineJoin = LineJoin.Round;
                    switch (key)
                    {
                    case "startup":
                        graphics.DrawArc(pen, 5, 5, 22, 22, -56, 292);
                        graphics.DrawLine(pen, 16, 2, 16, 15);
                        break;
                    case "hide":
                        graphics.DrawLines(pen, new PointF[4]
                        {
                            new PointF(5f, 20f),
                            new PointF(5f, 28f),
                            new PointF(27f, 28f),
                            new PointF(27f, 20f)
                        });
                        graphics.DrawLine(pen, 16, 3, 16, 21);
                        graphics.DrawLines(pen, new PointF[3]
                        {
                            new PointF(10f, 15f),
                            new PointF(16f, 21f),
                            new PointF(22f, 15f)
                        });
                        break;
                    case "clock":
                        graphics.DrawEllipse(pen, 3, 3, 26, 26);
                        graphics.DrawLine(pen, 16, 8, 16, 16);
                        graphics.DrawLine(pen, 16, 16, 22, 20);
                        break;
                    case "language":
                        graphics.DrawEllipse(pen, 3, 3, 26, 26);
                        graphics.DrawEllipse(pen, 10, 3, 12, 26);
                        graphics.DrawLine(pen, 3, 16, 29, 16);
                        graphics.DrawArc(pen, 4, 7, 24, 8, 0, 180);
                        graphics.DrawArc(pen, 4, 17, 24, 8, 180, 180);
                        break;
                    case "memory":
                    {
                        graphics.DrawRectangle(pen, 6, 8, 20, 16);
                        for (int i = 9; i <= 23; i += 5)
                        {
                            graphics.DrawLine(pen, i, 4, i, 8);
                            graphics.DrawLine(pen, i, 24, i, 28);
                        }
                        graphics.DrawLine(pen, 3, 12, 6, 12);
                        graphics.DrawLine(pen, 3, 20, 6, 20);
                        graphics.DrawLine(pen, 26, 12, 29, 12);
                        graphics.DrawLine(pen, 26, 20, 29, 20);
                        break;
                    }
                    case "maintenance":
                    case "repair":
                        graphics.DrawLines(pen, new PointF[4]
                        {
                            new PointF(23f, 3f),
                            new PointF(18f, 8f),
                            new PointF(23f, 13f),
                            new PointF(29f, 7f)
                        });
                        graphics.DrawArc(pen, 16, 2, 14, 14, 28, 257);
                        graphics.DrawLine(pen, 18, 14, 5, 27);
                        graphics.DrawLine(pen, 21, 17, 8, 30);
                        graphics.DrawLine(pen, 5, 27, 8, 30);
                        break;
                    case "appearance":
                        graphics.DrawRectangle(pen, 3, 4, 26, 24);
                        graphics.DrawLine(pen, 3, 10, 29, 10);
                        graphics.DrawLine(pen, 7, 7, 9, 7);
                        break;
                    case "monitoring":
                        graphics.DrawLines(pen, new PointF[3]
                        {
                            new PointF(4f, 3f),
                            new PointF(4f, 28f),
                            new PointF(29f, 28f)
                        });
                        graphics.DrawLine(pen, 10, 22, 10, 13);
                        graphics.DrawLine(pen, 17, 22, 17, 6);
                        graphics.DrawLine(pen, 24, 22, 24, 16);
                        break;
                    case "handheld":
                        graphics.DrawRectangle(pen, 3, 8, 26, 17);
                        graphics.DrawRectangle(pen, 10, 11, 12, 11);
                        graphics.DrawLine(pen, 6, 13, 6, 19);
                        graphics.DrawLine(pen, 4, 16, 8, 16);
                        graphics.DrawEllipse(pen, 25, 13, 1, 1);
                        graphics.DrawEllipse(pen, 25, 19, 1, 1);
                        break;
                    case "reset":
                        graphics.DrawArc(pen, 4, 4, 24, 24, 32, 294);
                        graphics.DrawLines(pen, new PointF[3]
                        {
                            new PointF(28f, 4f),
                            new PointF(28f, 12f),
                            new PointF(20f, 12f)
                        });
                        break;
                    case "uninstall":
                        graphics.DrawRectangle(pen, 7, 9, 18, 20);
                        graphics.DrawLine(pen, 4, 9, 28, 9);
                        graphics.DrawLine(pen, 12, 4, 20, 4);
                        graphics.DrawLine(pen, 13, 14, 13, 24);
                        graphics.DrawLine(pen, 19, 14, 19, 24);
                        break;
                    case "shader":
                        graphics.DrawLines(pen, new PointF[7]
                        {
                            new PointF(16f, 2f),
                            new PointF(29f, 10f),
                            new PointF(29f, 23f),
                            new PointF(16f, 31f),
                            new PointF(3f, 23f),
                            new PointF(3f, 10f),
                            new PointF(16f, 2f)
                        });
                        graphics.DrawLine(pen, 3, 10, 16, 18);
                        graphics.DrawLine(pen, 29, 10, 16, 18);
                        graphics.DrawLine(pen, 16, 18, 16, 31);
                        break;
                    case "logs":
                        graphics.DrawLines(pen, new PointF[6]
                        {
                            new PointF(6f, 2f),
                            new PointF(22f, 2f),
                            new PointF(28f, 8f),
                            new PointF(28f, 30f),
                            new PointF(6f, 30f),
                            new PointF(6f, 2f)
                        });
                        graphics.DrawLine(pen, 11, 12, 23, 12);
                        graphics.DrawLine(pen, 11, 18, 23, 18);
                        graphics.DrawLine(pen, 11, 24, 20, 24);
                        break;
                    default:
                        Glyphs.Draw(graphics, "gear", new Rectangle(1, 1, 30, 30), ink);
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
