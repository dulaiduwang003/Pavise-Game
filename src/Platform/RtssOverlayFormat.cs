using System;
using System.Drawing;
using System.Globalization;
using System.Text;

namespace PaviseApp
{
    internal static class RtssOverlayFormat
    {
        internal const int FontRasterFactor = 2;
        internal static int TextScale(int scale, bool fontOverride) { return fontOverride ? scale / FontRasterFactor : scale; }
        internal static string Format(MonitoringSnapshot snapshot, MonitoringLayout layout, MonitoringMetrics metrics,
            bool fontOverride = false, int viewportWidth = 1920, int viewportHeight = 1080, int accentRgb = 0xAE70FF,
            int bgOpacity = 62, int offsetX = 0, int offsetY = 0, int scale = 100, int spacing = 100)
        {
            // 单次发布使用同一语言，避免测量与绘制混用。
            lock (Lang.ChangeGate)
                return FormatCurrentLanguage(snapshot, layout, metrics, fontOverride, viewportWidth, viewportHeight,
                    accentRgb, bgOpacity, offsetX, offsetY, scale, spacing);
        }

        private static string FormatCurrentLanguage(MonitoringSnapshot snapshot, MonitoringLayout layout, MonitoringMetrics metrics,
            bool fontOverride, int viewportWidth, int viewportHeight, int accentRgb,
            int bgOpacity, int offsetX, int offsetY, int scale, int spacing)
        {
            if (snapshot == null || snapshot.Pid <= 4 || !MonitoringPreferences.ValidMetrics(metrics)) return "";
            accentRgb &= 0xFFFFFF;
            var cells = MonitoringMetricPresentation.Cells(snapshot, metrics, accentRgb);
            var geometry = MonitoringOverlayGeometry.Calculate(layout, metrics, viewportWidth, viewportHeight, offsetX, offsetY, scale, snapshot, spacing);
            int alpha = MonitoringOverlayGeometry.SurfaceAlpha(bgOpacity);
            var result = new StringBuilder(3072);
            RtssLabelAtlas atlas = RtssLabelAtlas.Get(geometry.FontPixels);
            if (atlas != null) result.Append("<LI=\"").Append(atlas.FileName).Append("\">");
            if (fontOverride) result.Append("<FNT=").Append(MonitoringOverlayGeometry.FontFamily).Append(",-")
                .Append(geometry.FontPixels * FontRasterFactor).Append(',').Append(MonitoringOverlayGeometry.FontWeight).Append(",1>");
            Layer(result, 0, geometry.Bounds.X, geometry.Bounds.Y);
            result.Append("<E=").Append(geometry.Bounds.Width).Append(',').Append(geometry.Bounds.Height).Append("> ");
            int layer = 1;
            if (alpha > 0)
                foreach (MonitoringOverlayFill fragment in geometry.SurfaceFragments)
                {
                    Layer(result, layer++, geometry.Bounds.X + fragment.Bounds.X, geometry.Bounds.Y + fragment.Bounds.Y);
                    int covered = MonitoringOverlayGeometry.CoveredAlpha(bgOpacity, fragment.Coverage);
                    Fill(result, covered << 24 | MonitoringOverlayGeometry.SurfaceRgb, fragment.Bounds.Width, fragment.Bounds.Height);
                }
            for (int i = 0; i < cells.Count; i++)
            {
                string sprite = atlas == null ? null : atlas.Sprite(MonitoringMetricPresentation.ShortName(cells[i]));
                Text(result, layer++, geometry.LabelBounds(i), geometry.Bounds,
                    sprite ?? MonitoringMetricPresentation.EnglishShortName(cells[i]), MonitoringMetricPresentation.LabelColor(cells[i], accentRgb),
                    sprite == null ? TextScale(MonitoringOverlayGeometry.LabelScale, fontOverride) : 100);
                Text(result, layer++, geometry.ValueBounds(i), geometry.Bounds,
                    cells[i].Value, cells[i].ColorRgb, TextScale(geometry.ValueScale(i), fontOverride));
            }
            return result.ToString();
        }

        private static void Text(StringBuilder result, int layer, Rectangle bounds, Rectangle surface,
            string value, int color, int scale)
        {
            Layer(result, layer, surface.X + bounds.X, surface.Y + bounds.Y);
            result.Append("<E=").Append(bounds.Width).Append(',').Append(bounds.Height).Append('>');
            result.Append("<C=").Append(color.ToString("X6", CultureInfo.InvariantCulture)).Append('>');
            if (scale != 100) result.Append("<S=").Append(scale).Append('>');
            result.Append(value);
            if (scale != 100) result.Append("<S>");
            result.Append("<C>");
        }

        private static void Fill(StringBuilder result, int color, int width = 0, int height = 0)
        {
            // RTSS 尺寸为零时会自动填满文字范围。
            result.Append("<C=").Append(color.ToString("X8", CultureInfo.InvariantCulture)).Append("><B=")
                .Append(width).Append(',').Append(height).Append(">\b<C>");
        }

        private static void Layer(StringBuilder result, int id, int x, int y)
        {
            result.Append("<P0><M=").Append(x).Append(',').Append(y).Append(',').Append(-x).Append(',').Append(-y)
                .Append("><L").Append(id).Append('>');
        }

        internal static int BaseFontPixels(int height)
        {
            height = height >= 240 && height <= 16384 ? height : 1080;
            return Math.Max(20, Math.Min(36, (int)Math.Round(24.0 * height / 1080)));
        }
    }
}
