using System.Drawing;
using System.Drawing.Drawing2D;

namespace PaviseApp
{
    internal static class GameTuningIcons
    {
        internal static void Draw(Graphics g, Rectangle bounds, string key, Color color)
        {
            if (key == "core")
            {
                Glyphs.Draw(g, "chip", bounds, color);
                return;
            }
            GraphicsState gstate = g.Save();
            try
            {
                g.TranslateTransform(bounds.X, bounds.Y);
                g.ScaleTransform((float)bounds.Width / 24f, (float)bounds.Height / 24f);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (Pen pen = new Pen(color, 1.55f))
                {
                    LineCap startCap = (pen.EndCap = LineCap.Round);
                    pen.StartCap = startCap;
                    pen.LineJoin = LineJoin.Round;
                    switch (key)
                    {
                    case "family":
                        g.DrawRectangle(pen, 9, 2, 6, 5);
                        g.DrawLines(pen, new PointF[4]
                        {
                            new PointF(12f, 7f),
                            new PointF(12f, 11f),
                            new PointF(4f, 11f),
                            new PointF(4f, 15f)
                        });
                        g.DrawLines(pen, new PointF[3]
                        {
                            new PointF(12f, 11f),
                            new PointF(20f, 11f),
                            new PointF(20f, 15f)
                        });
                        g.DrawRectangle(pen, 1, 15, 6, 5);
                        g.DrawRectangle(pen, 17, 15, 6, 5);
                        g.DrawLine(pen, 10, 18, 14, 18);
                        break;
                    case "rebar":
                        g.DrawRectangle(pen, 2, 3, 20, 10);
                        g.DrawRectangle(pen, 5, 6, 4, 4);
                        g.DrawRectangle(pen, 15, 6, 4, 4);
                        g.DrawLine(pen, 4, 19, 20, 19);
                        g.DrawLines(pen, new PointF[3]
                        {
                            new PointF(7f, 16f),
                            new PointF(4f, 19f),
                            new PointF(7f, 22f)
                        });
                        g.DrawLines(pen, new PointF[3]
                        {
                            new PointF(17f, 16f),
                            new PointF(20f, 19f),
                            new PointF(17f, 22f)
                        });
                        break;
                    case "fullscreen":
                        g.DrawRectangle(pen, 2, 3, 20, 18);
                        g.DrawLine(pen, 2, 7, 22, 7);
                        g.DrawLines(pen, new PointF[3]
                        {
                            new PointF(6f, 15f),
                            new PointF(6f, 11f),
                            new PointF(10f, 11f)
                        });
                        g.DrawLines(pen, new PointF[3]
                        {
                            new PointF(14f, 17f),
                            new PointF(18f, 17f),
                            new PointF(18f, 13f)
                        });
                        g.DrawLine(pen, 10, 17, 14, 11);
                        break;
                    case "threads":
                        g.DrawEllipse(pen, 1, 3, 3, 3);
                        g.DrawEllipse(pen, 1f, 10.5f, 3f, 3f);
                        g.DrawEllipse(pen, 1, 18, 3, 3);
                        g.DrawLines(pen, new PointF[3]
                        {
                            new PointF(5f, 4.5f),
                            new PointF(8f, 4.5f),
                            new PointF(11f, 10f)
                        });
                        g.DrawLine(pen, 5, 12, 11, 12);
                        g.DrawLines(pen, new PointF[3]
                        {
                            new PointF(5f, 19.5f),
                            new PointF(8f, 19.5f),
                            new PointF(11f, 14f)
                        });
                        g.DrawRectangle(pen, 12, 7, 9, 10);
                        g.DrawRectangle(pen, 15, 10, 3, 4);
                        g.DrawLine(pen, 15, 4, 15, 7);
                        g.DrawLine(pen, 18, 4, 18, 7);
                        g.DrawLine(pen, 15, 17, 15, 20);
                        g.DrawLine(pen, 18, 17, 18, 20);
                        break;
                    }
                }
            }
            finally
            {
                g.Restore(gstate);
            }
        }
    }
}
