using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
    internal static class WorkspaceCanvasSurface
    {
        internal static void Paint(Graphics graphics, Control control, Rectangle clip)
        {
            Paint(graphics, control, clip, Theme.Accent);
        }

        internal static void Paint(Graphics graphics, Control control, Rectangle clip, Color accent)
        {
            if (Backdrop.AppliesTo(control))
            {
                Backdrop.Paint(graphics, control, clip);
                return;
            }
            using (SolidBrush brush = new SolidBrush(Theme.Bg))
            {
                graphics.FillRectangle(brush, clip);
            }
            Form form = control.FindForm();
            Size size = ((form == null) ? control.ClientSize : form.ClientSize);
            if (size.Width < 1 || size.Height < 1)
            {
                return;
            }
            Point empty = Point.Empty;
            Control currentControl = control;
            while (currentControl != null && !(currentControl is Form))
            {
                empty.Offset(currentControl.Left, currentControl.Top);
                currentControl = currentControl.Parent;
            }
            GraphicsState gstate = graphics.Save();
            try
            {
                graphics.TranslateTransform(-empty.X, -empty.Y);
                graphics.ScaleTransform((float)size.Width / 1600f, (float)size.Height / 1000f);
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                DrawScene(graphics, accent);
            }
            finally
            {
                graphics.Restore(gstate);
            }
        }

        private static void DrawScene(Graphics graphics, Color accent)
        {
            Color steel = (Theme.LightMode ? Color.FromArgb(211, 218, 229) : Color.FromArgb(18, 20, 25));
            DrawPanel(graphics, steel, new PointF[4]
            {
                new PointF(0f, 78f),
                new PointF(445f, 78f),
                new PointF(233f, 290f),
                new PointF(0f, 290f)
            });
            DrawPanel(graphics, steel, new PointF[6]
            {
                new PointF(0f, 304f),
                new PointF(167f, 304f),
                new PointF(474f, 611f),
                new PointF(374f, 611f),
                new PointF(130f, 367f),
                new PointF(0f, 367f)
            });
            DrawPanel(graphics, steel, new PointF[5]
            {
                new PointF(0f, 654f),
                new PointF(177f, 654f),
                new PointF(434f, 911f),
                new PointF(345f, 1000f),
                new PointF(0f, 655f)
            });
            DrawPanel(graphics, steel, new PointF[4]
            {
                new PointF(1020f, 78f),
                new PointF(1207f, 78f),
                new PointF(1507f, 378f),
                new PointF(1414f, 378f)
            });
            DrawPanel(graphics, steel, new PointF[3]
            {
                new PointF(1600f, 220f),
                new PointF(1500f, 220f),
                new PointF(1600f, 320f)
            });
            DrawPanel(graphics, steel, new PointF[6]
            {
                new PointF(1600f, 499f),
                new PointF(1385f, 499f),
                new PointF(1062f, 822f),
                new PointF(1176f, 822f),
                new PointF(1436f, 562f),
                new PointF(1600f, 562f)
            });
            DrawPanel(graphics, steel, new PointF[5]
            {
                new PointF(1600f, 650f),
                new PointF(1482f, 650f),
                new PointF(1250f, 882f),
                new PointF(1350f, 982f),
                new PointF(1600f, 732f)
            });
            using (Pen pen = new Pen(Col.Alpha(accent, Theme.LightMode ? 44 : 35), 0.8f))
            {
                graphics.DrawLines(pen, new PointF[4]
                {
                    new PointF(0f, 256f),
                    new PointF(73f, 210f),
                    new PointF(111f, 210f),
                    new PointF(240f, 81f)
                });
                graphics.DrawLines(pen, new PointF[4]
                {
                    new PointF(0f, 402f),
                    new PointF(88f, 402f),
                    new PointF(245f, 559f),
                    new PointF(361f, 559f)
                });
                graphics.DrawLines(pen, new PointF[3]
                {
                    new PointF(1600f, 305f),
                    new PointF(1398f, 305f),
                    new PointF(1173f, 80f)
                });
                graphics.DrawLines(pen, new PointF[3]
                {
                    new PointF(1600f, 500f),
                    new PointF(1384f, 500f),
                    new PointF(1194f, 690f)
                });
                graphics.DrawLines(pen, new PointF[3]
                {
                    new PointF(1600f, 740f),
                    new PointF(1485f, 740f),
                    new PointF(1358f, 867f)
                });
            }
            using (Pen currentPen = new Pen(Col.Alpha(accent, Theme.LightMode ? 150 : 185), 1.2f))
            {
                graphics.DrawLine(currentPen, 258, 257, 310, 205);
                graphics.DrawLine(currentPen, 1284, 162, 1327, 205);
                graphics.DrawLine(currentPen, 1353, 531, 1384, 500);
                graphics.DrawLine(currentPen, 207, 680, 259, 732);
            }
        }

        private static void DrawPanel(Graphics graphics, Color steel, PointF[] points)
        {
            using (GraphicsPath graphicsPath = new GraphicsPath())
            {
                graphicsPath.AddPolygon(points);
                using (LinearGradientBrush brush = new LinearGradientBrush(new Rectangle(0, 80, 1600, 920), Col.Alpha(steel, Theme.LightMode ? 75 : 155), Col.Alpha(Theme.Bg, 8), 48f))
                {
                    graphics.FillPath(brush, graphicsPath);
                }
                using (Pen pen = new Pen(Col.Alpha(Theme.StrokeHi, Theme.LightMode ? 36 : 25), 0.7f))
                {
                    graphics.DrawPath(pen, graphicsPath);
                }
            }
        }
    }
}
