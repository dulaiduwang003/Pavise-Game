using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Windows.Forms;

namespace PaviseApp
{
    internal static class MonitoringPreviewRenderer
    {
        private static void Text(Graphics graphics, string value, Font font, RectangleF bounds, Color color, StringAlignment alignment)
        {
            using (var brush = new SolidBrush(color))
            using (var edge = new SolidBrush(Color.FromArgb(240, 3, 6, 11)))
            using (var format = new StringFormat(StringFormat.GenericTypographic))
            {
                format.Alignment = alignment; format.LineAlignment = StringAlignment.Center;
                format.FormatFlags |= StringFormatFlags.NoWrap;
                format.Trimming = StringTrimming.None;
                for (int y = -1; y <= 1; y++)
                    for (int x = -1; x <= 1; x++)
                    {
                        if (x == 0 && y == 0) continue;
                        RectangleF shadow = bounds; shadow.Offset(x, y);
                        graphics.DrawString(value, font, edge, shadow, format);
                    }
                graphics.DrawString(value, font, brush, bounds, format);
            }
        }

        internal static RectangleF FirstValueBounds(MonitoringOverlayGeometry geometry, MonitoringLayout layout)
        { return ValueBounds(geometry, layout, 0); }

        internal static RectangleF LabelBounds(MonitoringOverlayGeometry geometry, MonitoringLayout layout, int index)
        { return geometry.LabelBounds(index); }

        internal static RectangleF ValueBounds(MonitoringOverlayGeometry geometry, MonitoringLayout layout, int index)
        { return geometry.ValueBounds(index); }

        internal static Color SurfaceColor(int opacity)
        {
            return Color.FromArgb(MonitoringOverlayGeometry.SurfaceAlpha(opacity), Color.FromArgb(MonitoringOverlayGeometry.SurfaceRgb));
        }

        internal static Bitmap Render(MonitoringOverlayGeometry geometry, MonitoringLayout layout, MonitoringMetrics metrics,
            int opacity, int accentRgb)
        { return RenderSnapshot(geometry, layout, metrics, opacity, accentRgb, MonitoringMetricPresentation.Example); }

        internal static Bitmap RenderSnapshot(MonitoringOverlayGeometry geometry, MonitoringLayout layout, MonitoringMetrics metrics,
            int opacity, int accentRgb, MonitoringSnapshot snapshot)
        {
            accentRgb &= 0xFFFFFF;
            var bitmap = new Bitmap(Math.Max(1, geometry.Bounds.Width), Math.Max(1, geometry.Bounds.Height), PixelFormat.Format32bppPArgb);
            try
            {
                using (Graphics graphics = Graphics.FromImage(bitmap))
                {
                    graphics.Clear(Color.Transparent);
                    graphics.CompositingMode = CompositingMode.SourceOver;
                    graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    graphics.SmoothingMode = SmoothingMode.None;
                    foreach (MonitoringOverlayFill fragment in geometry.SurfaceFragments)
                    {
                        int covered = MonitoringOverlayGeometry.CoveredAlpha(opacity, fragment.Coverage);
                        if (covered == 0) continue;
                        using (var fill = new SolidBrush(Color.FromArgb(covered, Color.FromArgb(MonitoringOverlayGeometry.SurfaceRgb))))
                            graphics.FillRectangle(fill, fragment.Bounds);
                    }
                    List<MonitoringMetricCell> shown = MonitoringMetricPresentation.Cells(snapshot, metrics, accentRgb);
                    float font = geometry.FontPixels;
                    using (var valueFont = new Font(MonitoringOverlayGeometry.FontFamily, font * MonitoringOverlayGeometry.DefaultValueScale / 100f, MonitoringOverlayGeometry.HudFontStyle, GraphicsUnit.Pixel))
                    using (var fpsFont = new Font(MonitoringOverlayGeometry.FontFamily, font * MonitoringOverlayGeometry.FpsScale / 100f, MonitoringOverlayGeometry.HudFontStyle, GraphicsUnit.Pixel))
                    using (var barFpsFont = new Font(MonitoringOverlayGeometry.FontFamily, font * MonitoringOverlayGeometry.BarFpsScale / 100f, MonitoringOverlayGeometry.HudFontStyle, GraphicsUnit.Pixel))
                    using (var labelFont = new Font(MonitoringOverlayGeometry.FontFamily, font * MonitoringOverlayGeometry.LabelScale / 100f, MonitoringOverlayGeometry.HudFontStyle, GraphicsUnit.Pixel))
                    {
                        for (int i = 0; i < shown.Count; i++)
                        {
                            Color readingColor = Color.FromArgb((shown[i].ColorRgb >> 16) & 255,
                                (shown[i].ColorRgb >> 8) & 255, shown[i].ColorRgb & 255);
                            Text(graphics, MonitoringMetricPresentation.ShortName(shown[i]), labelFont,
                                geometry.LabelBounds(i), Color.FromArgb(255, Color.FromArgb(MonitoringMetricPresentation.LabelColor(shown[i], accentRgb))), StringAlignment.Near);
                            Text(graphics, shown[i].Value, geometry.ValueScale(i) == MonitoringOverlayGeometry.FpsScale ? fpsFont
                                : geometry.ValueScale(i) == MonitoringOverlayGeometry.BarFpsScale ? barFpsFont : valueFont,
                                geometry.ValueBounds(i), readingColor, StringAlignment.Near);
                        }
                    }
                }
                return bitmap;
            }
            catch { bitmap.Dispose(); throw; }
        }
    }

    internal sealed class MonitoringPreview : Control
    {
        private MonitoringLayout layout;
        private MonitoringMetrics metrics;
        private int opacity = 62, offsetX, offsetY, scalePercent = 100, spacingPercent = 100;
        private int viewportWidth = 1920, viewportHeight = 1080;
        private bool dragging;
        private PointF dragGrab;
        private Rectangle viewportBounds;
        private int preferenceLanguage = -1, preferenceAccent;
        private bool preferenceLight;
        internal event Action<int, int> PositionChanging;
        internal event Action<int, int> PositionEdited;
        internal Rectangle OverlayBounds { get; private set; }
        internal Rectangle ViewportBounds { get { UpdateGeometry(); return viewportBounds; } }
        internal Rectangle FirstValueBounds { get; private set; }
        internal MonitoringLayout SelectedLayout { get { return layout; } }
        internal MonitoringMetrics SelectedMetrics { get { return metrics; } }
        internal int SelectedOpacity { get { return opacity; } }
        internal int SelectedOffsetX { get { return offsetX; } }
        internal int SelectedOffsetY { get { return offsetY; } }
        internal int SelectedScalePercent { get { return scalePercent; } }

        internal int SelectedSpacingPercent { get { return spacingPercent; } }

        internal MonitoringPreview()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            AccessibleRole = AccessibleRole.Graphic; TabStop = true;
        }

        internal void SetPreferences(MonitoringLayout nextLayout, MonitoringMetrics nextMetrics)
        { SetPreferences(nextLayout, nextMetrics, opacity, offsetX, offsetY, scalePercent, spacingPercent); }

        internal void SetPreferences(MonitoringLayout nextLayout, MonitoringMetrics nextMetrics, int nextOpacity, int nextOffsetX, int nextOffsetY, int scale = 100, int spacing = 100)
        {
            nextOpacity = Math.Max(0, Math.Min(100, nextOpacity));
            nextOffsetX = nextLayout == MonitoringLayout.TopBar ? 0 : Math.Max(-1000, Math.Min(1000, nextOffsetX));
            nextOffsetY = Math.Max(-1000, Math.Min(1000, nextOffsetY));
            scale = Math.Max(MonitoringPreferences.MinimumScalePercent, Math.Min(MonitoringPreferences.MaximumScalePercent, scale));
            spacing = Math.Max(MonitoringPreferences.MinimumSpacingPercent, Math.Min(MonitoringPreferences.MaximumSpacingPercent, spacing));
            int accent = Theme.ModeColor(Theme.CurrentMode).ToArgb();
            if (layout == nextLayout && metrics == nextMetrics && opacity == nextOpacity
                && offsetX == nextOffsetX && offsetY == nextOffsetY && scalePercent == scale && spacingPercent == spacing
                && preferenceLanguage == Lang.Cur && preferenceAccent == accent && preferenceLight == Theme.LightMode) return;
            preferenceLanguage = Lang.Cur; preferenceAccent = accent; preferenceLight = Theme.LightMode;
            layout = nextLayout; metrics = nextMetrics;
            opacity = Math.Max(0, Math.Min(100, nextOpacity));
            offsetX = layout == MonitoringLayout.TopBar ? 0 : Math.Max(-1000, Math.Min(1000, nextOffsetX));
            offsetY = Math.Max(-1000, Math.Min(1000, nextOffsetY));
            scalePercent = Math.Max(MonitoringPreferences.MinimumScalePercent, Math.Min(MonitoringPreferences.MaximumScalePercent, scale));
            spacingPercent = spacing;
            AccessibleName = AutomaticPolicy.Text("性能监测位置预览 · 示例数据", "Monitoring position preview · sample data");
            AccessibleDescription = LayoutName(layout) + (layout == MonitoringLayout.TopBar
                ? AutomaticPolicy.Text("；水平居中，垂直拖动或使用上下方向键调整位置。", "; horizontally centered; drag vertically or use the up and down arrow keys.")
                : AutomaticPolicy.Text("；拖动悬浮窗调整位置，方向键微调。", "; drag the HUD to position it, or use arrow keys."));
            UpdateGeometry(); Invalidate();
        }

        internal void SetViewport(int width, int height)
        {
            width = width >= 320 && width <= 16384 ? width : 1920;
            height = height >= 240 && height <= 16384 ? height : 1080;
            if (viewportWidth == width && viewportHeight == height) return;
            viewportWidth = width; viewportHeight = height;
            UpdateGeometry(); Invalidate();
        }

        internal static string LayoutName(MonitoringLayout value)
        {
            return value == MonitoringLayout.TopRight ? AutomaticPolicy.Text("右上角", "Top right")
                : value == MonitoringLayout.TopBar ? AutomaticPolicy.Text("顶部横条", "Top bar")
                : AutomaticPolicy.Text("左上角", "Top left");
        }

        private MonitoringOverlayGeometry UpdateGeometry()
        {
            Rectangle area = PositionCanvas;
            double scale = Math.Min(area.Width / (double)viewportWidth, area.Height / (double)viewportHeight);
            int viewWidth = Math.Max(1, (int)Math.Round(viewportWidth * scale)), viewHeight = Math.Max(1, (int)Math.Round(viewportHeight * scale));
            viewportBounds = new Rectangle(area.Left + (area.Width - viewWidth) / 2,
                area.Top + (area.Height - viewHeight) / 2, viewWidth, viewHeight);
            MonitoringOverlayGeometry geometry = MonitoringOverlayGeometry.Calculate(layout, metrics, viewportWidth, viewportHeight, offsetX, offsetY, scalePercent, spacing: spacingPercent);
            OverlayBounds = Project(geometry.Bounds);
            RectangleF value = MonitoringPreviewRenderer.FirstValueBounds(geometry, layout);
            value.Offset(geometry.Bounds.Left, geometry.Bounds.Top);
            FirstValueBounds = Project(Rectangle.Round(value));
            return geometry;
        }

        private Rectangle PositionCanvas
        {
            get
            {
                int top = Theme.S(24), bottom = Height - Theme.S(6);
                return new Rectangle(Theme.S(8), top, Math.Max(1, Width - Theme.S(16)), Math.Max(1, bottom - top));
            }
        }

        private Rectangle Project(Rectangle rectangle)
        {
            float scale = viewportBounds.Width / (float)viewportWidth;
            return new Rectangle(viewportBounds.Left + (int)Math.Round(rectangle.Left * scale),
                viewportBounds.Top + (int)Math.Round(rectangle.Top * scale),
                Math.Max(1, (int)Math.Round(rectangle.Width * scale)), Math.Max(1, (int)Math.Round(rectangle.Height * scale)));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics graphics = e.Graphics;
            MonitoringOverlayGeometry geometry = UpdateGeometry();
            graphics.Clear(Theme.Card);
            Color accent = Theme.ModeColor(Theme.CurrentMode);
            Color background = Theme.LightMode ? Color.FromArgb(220, 228, 237) : Color.FromArgb(15, 21, 30);
            Rectangle position = PositionCanvas;
            using (var fill = new LinearGradientBrush(position, background, Col.Lerp(background, accent, .06f), LinearGradientMode.ForwardDiagonal))
                graphics.FillRectangle(fill, position);
            using (var facet = new SolidBrush(Col.Alpha(accent, Theme.LightMode ? 10 : 7)))
                graphics.FillPolygon(facet, new[] { new Point(position.Left, position.Bottom),
                    new Point(position.Right, position.Top + position.Height / 3), new Point(position.Right, position.Bottom) });
            using (var fill = new LinearGradientBrush(viewportBounds,
                Color.FromArgb(181, 192, 202), Color.FromArgb(25, 34, 45), LinearGradientMode.Horizontal))
                graphics.FillRectangle(fill, viewportBounds);
            using (var scene = new SolidBrush(Color.FromArgb(22, 243, 247, 251)))
                graphics.FillPolygon(scene, new[] { new Point(viewportBounds.Left, viewportBounds.Bottom),
                    new Point(viewportBounds.Left + viewportBounds.Width / 3, viewportBounds.Top),
                    new Point(viewportBounds.Left + viewportBounds.Width * 2 / 3, viewportBounds.Top),
                    new Point(viewportBounds.Right, viewportBounds.Bottom) });
            using (var grid = new Pen(Col.Alpha(Theme.Faint, Theme.LightMode ? 18 : 13)))
            {
                int spacing = Math.Max(20, viewportBounds.Width / 6);
                for (int x = viewportBounds.Left + spacing; x < viewportBounds.Right; x += spacing)
                    graphics.DrawLine(grid, x, viewportBounds.Top, x, viewportBounds.Bottom);
                for (int y = viewportBounds.Top + spacing; y < viewportBounds.Bottom; y += spacing)
                    graphics.DrawLine(grid, viewportBounds.Left, y, viewportBounds.Right, y);
            }
            Rectangle label = new Rectangle(Theme.S(8), 0, Math.Max(1, Width - Theme.S(16)), Theme.S(20));
            TextRenderer.DrawText(graphics, AutomaticPolicy.Text("屏幕位置", "Screen position"), Theme.UI(8.5f, true), label, Theme.Fg,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(graphics, layout == MonitoringLayout.TopBar
                ? AutomaticPolicy.Text("示例 · 垂直拖动", "Sample · drag vertically")
                : AutomaticPolicy.Text("示例 · 拖动调整", "Sample · drag to position"), Theme.UI(8f, false), label, Theme.Dim,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            using (var border = new Pen(Theme.Stroke)) graphics.DrawRectangle(border, viewportBounds.Left, viewportBounds.Top, viewportBounds.Width - 1, viewportBounds.Height - 1);
            Rectangle resolution = new Rectangle(viewportBounds.Left + Theme.S(7), viewportBounds.Bottom - Theme.S(20),
                Math.Max(1, viewportBounds.Width - Theme.S(14)), Theme.S(16));
            TextRenderer.DrawText(graphics, viewportWidth + " × " + viewportHeight, Theme.Mono(7f), resolution, Col.Alpha(Theme.Dim, 105),
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic; graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            using (Bitmap bitmap = MonitoringPreviewRenderer.Render(geometry, layout, metrics, opacity, accent.ToArgb() & 0xFFFFFF))
                graphics.DrawImage(bitmap, OverlayBounds);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (!Enabled) return;
            MonitoringOverlayGeometry geometry = UpdateGeometry();
            if (e.Button != MouseButtons.Left || !OverlayBounds.Contains(e.Location)) return;
            float scale = viewportBounds.Width / (float)viewportWidth;
            dragGrab = new PointF((e.X - viewportBounds.Left) / scale - geometry.Bounds.Left,
                (e.Y - viewportBounds.Top) / scale - geometry.Bounds.Top);
            dragging = true; Capture = true;
            Cursor = layout == MonitoringLayout.TopBar ? Cursors.SizeNS : Cursors.SizeAll; Focus();
        }

        private void EditPosition(int x, int y, bool commit)
        {
            if (!Enabled) return;
            Point offset = MonitoringOverlayGeometry.GetOffsets(layout, metrics, viewportWidth, viewportHeight, x, y, scalePercent, spacingPercent);
            bool changed = offsetX != offset.X || offsetY != offset.Y;
            offsetX = offset.X; offsetY = offset.Y; UpdateGeometry(); Invalidate();
            Action<int, int> handler = commit ? PositionEdited : PositionChanging;
            if ((changed || commit) && handler != null) handler(offsetX, offsetY);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!Enabled) return;
            if (dragging)
            {
                float scale = viewportBounds.Width / (float)viewportWidth;
                EditPosition((int)Math.Round((e.X - viewportBounds.Left) / scale - dragGrab.X),
                    (int)Math.Round((e.Y - viewportBounds.Top) / scale - dragGrab.Y), false);
            }
            else Cursor = OverlayBounds.Contains(e.Location)
                ? layout == MonitoringLayout.TopBar ? Cursors.SizeNS : Cursors.SizeAll : Cursors.Default;
        }

        private void FinishDrag()
        {
            if (!Enabled) { CancelDrag(); return; }
            if (!dragging) return;
            dragging = false; Capture = false;
            Action<int, int> edited = PositionEdited; if (edited != null) edited(offsetX, offsetY);
        }
        private void CancelDrag()
        {
            dragging = false;
            if (Capture) Capture = false;
            Cursor = Cursors.Default;
        }
        protected override void OnEnabledChanged(EventArgs e)
        {
            if (!Enabled) CancelDrag();
            base.OnEnabledChanged(e); Invalidate();
        }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); if (e.Button == MouseButtons.Left) FinishDrag(); }
        protected override void OnMouseCaptureChanged(EventArgs e) { base.OnMouseCaptureChanged(e); if (!Capture) FinishDrag(); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (!Enabled) return;
            if (layout == MonitoringLayout.TopBar && (e.KeyCode == Keys.Left || e.KeyCode == Keys.Right))
            { e.Handled = e.SuppressKeyPress = true; return; }
            int amountX = Math.Max(1, (int)Math.Ceiling(viewportWidth / 1000.0));
            int amountY = Math.Max(1, (int)Math.Ceiling(viewportHeight / 1000.0));
            if (e.Shift) { amountX *= 10; amountY *= 10; }
            int dx = 0, dy = 0;
            if (e.KeyCode == Keys.Left) dx = -amountX; else if (e.KeyCode == Keys.Right) dx = amountX;
            else if (e.KeyCode == Keys.Up) dy = -amountY; else if (e.KeyCode == Keys.Down) dy = amountY;
            else return;
            Rectangle bounds = UpdateGeometry().Bounds;
            EditPosition(bounds.Left + dx, bounds.Top + dy, true); e.Handled = true; e.SuppressKeyPress = true;
        }
        protected override bool IsInputKey(Keys keyData)
        {
            Keys key = keyData & Keys.KeyCode;
            return key == Keys.Left || key == Keys.Right || key == Keys.Up || key == Keys.Down || base.IsInputKey(keyData);
        }
    }
}
