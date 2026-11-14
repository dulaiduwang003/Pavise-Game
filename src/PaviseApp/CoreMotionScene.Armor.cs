using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace PaviseApp
{
    internal sealed partial class CoreMotionScene
    {
        private void Ribbon(PointF[] points, Color color, float width)
        {
            if (points.Length < 2) return;
#if PAVISE_SELFTEST
            if (PanelGeometryForTest) return;
#endif
            pen.Color = Color.FromArgb(color.A / 8, color); pen.Width = width + 9;
            g.DrawCurve(pen, points, .45f);
            pen.Color = Color.FromArgb(color.A / 3, color); pen.Width = width + 3;
            g.DrawCurve(pen, points, .45f);
            pen.Color = color; pen.Width = width;
            g.DrawCurve(pen, points, .45f);
        }
        private void Surface(PointF[] points, Color bright, Color shade)
        {
#if PAVISE_SELFTEST
            if (PanelGeometryForTest) { brush.Color = Color.White; g.FillPolygon(brush, points); return; }
#endif
            using (var path = new GraphicsPath())
            {
                path.AddPolygon(points);
                RectangleF area = path.GetBounds();
                if (area.Width < .01f || area.Height < .01f) return;
                using (var fill = new LinearGradientBrush(area, bright, shade, 65f)) g.FillPath(fill, path);
            }
            Polygon(A(high, light ? 100 : 115), .85f, points);
        }
        private void CurveFill(PointF[] points, Color color)
        {
            using (var path = new GraphicsPath())
            {
                path.AddClosedCurve(points, .5f);
#if PAVISE_SELFTEST
                if (PanelGeometryForTest) color = Color.White;
#endif
                brush.Color = color; g.FillPath(brush, path);
            }
        }
        private void Orb(double x, double y, double radius, Color color, double strength)
        {
#if PAVISE_SELFTEST
            if (PanelGeometryForTest) { Dot(x, y, radius, Color.White); return; }
#endif
            Halo(x, y, radius * 2.5, color, .38 * strength);
            using (var path = new GraphicsPath())
            {
                path.AddEllipse((float)(x - radius), (float)(y - radius), (float)(radius * 2), (float)(radius * 2));
                using (var fill = new PathGradientBrush(path))
                {
                    fill.CenterPoint = P(x - radius * .28, y - radius * .32);
                    fill.CenterColor = Col.Lerp(color, Color.White, .87f);
                    fill.SurroundColors = new[] { Col.Lerp(color, Theme.Inset, .64f) };
                    g.FillPath(fill, path);
                }
            }
        }
        private static PointF SpacePoint(double x, double y, double z, double yaw, double tilt)
        {
            double dx = x * Math.Cos(yaw) + z * Math.Sin(yaw);
            double dz = z * Math.Cos(yaw) - x * Math.Sin(yaw);
            double dy = y * Math.Cos(tilt) - dz * Math.Sin(tilt);
            dz = y * Math.Sin(tilt) + dz * Math.Cos(tilt);
            double perspective = 460 / (460 + dz);
            return P(dx * perspective, dy * perspective);
        }
    }
}
