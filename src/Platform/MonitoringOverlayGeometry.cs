using System;
using System.Collections.Generic;
using System.Drawing;

namespace PaviseApp
{
    internal sealed class MonitoringOverlayGroup
    {
        internal string Name;
        internal int[] Items;
        internal int Columns, Rows;
        internal Rectangle Bounds, HeaderBounds;
        internal int[] LabelWidths, ValueWidths, ColumnWidths, RowHeights;
        internal int NaturalWidth;
        internal bool HeroRow;
    }

    internal struct MonitoringOverlayFill
    {
        internal readonly Rectangle Bounds;
        internal readonly int Coverage;
        internal MonitoringOverlayFill(Rectangle bounds, int coverage)
        { Bounds = bounds; Coverage = coverage; }
    }

    // 预览和 RTSS 共用物理像素布局。
    internal sealed class MonitoringOverlayGeometry
    {
        internal static string FontFamily { get { return Lang.Cur == 1 ? "Bahnschrift SemiCondensed" : "Microsoft YaHei UI"; } }
        internal const int FontWeight = 700, HeadingScale = 65, LabelScale = 76, DefaultValueScale = 96, FpsScale = 160, BarFpsScale = 136;
        internal const int SurfaceRgb = 0x0A0C12;
        internal const FontStyle HudFontStyle = FontStyle.Bold;
        private sealed class FontMeasurements
        {
            internal readonly Dictionary<string, int> Labels = new Dictionary<string, int>();
            internal int DigitWidth;
            internal double AscentRatio, LineSpacingRatio;
        }
        private static readonly object measurementGate = new object();
        private static readonly Dictionary<int, FontMeasurements> measurements = new Dictionary<int, FontMeasurements>();
        private static FontMeasurements MeasureFont(int pixels)
        {
            lock (measurementGate)
            {
                FontMeasurements cached;
                int key = (Lang.Cur == 1 ? 1 : Lang.Cur == 2 ? 2 : 0) * 1000 + pixels;
                if (measurements.TryGetValue(key, out cached)) return cached;
                cached = new FontMeasurements();
                // 只缓存固定标签的字体尺寸。
                using (var bitmap = new Bitmap(1, 1))
                using (Graphics graphics = Graphics.FromImage(bitmap))
                using (var font = new Font(FontFamily, pixels, HudFontStyle, GraphicsUnit.Pixel))
                using (var format = new StringFormat(StringFormat.GenericTypographic))
                {
                    format.FormatFlags |= StringFormatFlags.NoWrap;
                    graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    using (FontFamily family = font.FontFamily)
                    {
                        double em = family.GetEmHeight(HudFontStyle);
                        cached.AscentRatio = family.GetCellAscent(HudFontStyle) / em;
                        cached.LineSpacingRatio = family.GetLineSpacing(HudFontStyle) / em;
                    }
                    cached.DigitWidth = (int)Math.Ceiling(graphics.MeasureString("0000000000", font, int.MaxValue, format).Width / 10);
                    var labels = new HashSet<string>();
                    foreach (MonitoringMetricCell cell in MonitoringMetricPresentation.Cells(MonitoringMetricPresentation.Example, MonitoringMetrics.All))
                    {
                        labels.Add(MonitoringMetricPresentation.ShortName(cell.Metric));
                        labels.Add(MonitoringMetricPresentation.EnglishShortName(cell.Metric));
                    }
                    labels.Add("PRESENT");
                    labels.Add(MonitoringMetricPresentation.Name(MonitoringMetrics.FrameTime));
                    foreach (string label in labels)
                        cached.Labels[label] = (int)Math.Ceiling(graphics.MeasureString(label, font, int.MaxValue, format).Width) + 3;
                }
                measurements.Add(key, cached);
                return cached;
            }
        }
        internal Rectangle Bounds;
        internal int FontPixels, ViewportWidth, ViewportHeight;
        internal int SpacingPercent = MonitoringPreferences.DefaultSpacingPercent;
        internal bool IsCentered { get; private set; }
        internal readonly List<MonitoringOverlayGroup> Groups = new List<MonitoringOverlayGroup>();
        internal readonly List<Rectangle> Separators = new List<Rectangle>();
        internal readonly List<MonitoringOverlayFill> SurfaceFragments = new List<MonitoringOverlayFill>();
        internal int SurfaceCutPixels { get; private set; }
        private Rectangle[] labels, values;
        private int[] scales;
        private bool[] stacked;
        internal int PaddingX { get { return Math.Max(10, FontPixels / 2); } }
        internal int PaddingY { get { return Math.Max(7, FontPixels / 3); } }
        private int Space(int pixels) { return Math.Max(1, (int)Math.Round(pixels * SpacingPercent / 100.0)); }
        internal int InlineGap { get { return Space(Math.Max(4, FontPixels / 6)); } }
        private int GroupGap { get { return Space(Math.Max(16, FontPixels)); } }
        private int FamilyGap { get { return Space(Math.Max(8, FontPixels / 3)); } }
        private int RowPadding { get { return Math.Max(0, (int)Math.Round(2 + (SpacingPercent - 100) * FontPixels / 400.0)); } }
        internal Rectangle LabelBounds(int index) { return index >= 0 && index < labels.Length ? labels[index] : Rectangle.Empty; }
        internal Rectangle ValueBounds(int index) { return index >= 0 && index < values.Length ? values[index] : Rectangle.Empty; }
        internal Rectangle BarLabelBounds(int index) { return LabelBounds(index); }
        internal Rectangle BarValueBounds(int index) { return ValueBounds(index); }
        internal int ValueScale(int index) { return index >= 0 && index < scales.Length ? scales[index] : DefaultValueScale; }
        internal bool IsStacked(int index) { return index >= 0 && index < stacked.Length && stacked[index]; }
        internal static int ScaledFont(int pixels, int scale) { return (int)Math.Ceiling(pixels * scale / 100.0); }
        internal static int Width(int value) { return value >= 320 && value <= 16384 ? value : 1920; }
        internal static int Height(int value) { return value >= 240 && value <= 16384 ? value : 1080; }
        internal static int CharacterWidth(int pixels) { return Math.Max(1, (int)Math.Ceiling(pixels * 0.6)); }
        internal static int LineHeight(int pixels) { return Math.Max(1, (int)Math.Ceiling(pixels * 1.35)); }
        internal static int CellCount(MonitoringMetrics metrics)
        { return MonitoringMetricPresentation.Cells(MonitoringMetricPresentation.Example, metrics).Count; }

        private MonitoringOverlayGroup MeasureGroup(string name, int[] items, int columns, List<MonitoringMetricCell> cells)
        {
            var group = new MonitoringOverlayGroup { Name = name, Items = items, Columns = columns,
                Rows = (items.Length + columns - 1) / columns, LabelWidths = new int[columns],
                ValueWidths = new int[columns], ColumnWidths = new int[columns] };
            if (!IsCentered && name == "FRAME")
                foreach (int index in items) if (cells[index].Metric == MonitoringMetrics.Fps) group.HeroRow = true;
            group.RowHeights = new int[group.Rows];
            int labelPixels = ScaledFont(FontPixels, LabelScale), labelHeight = LineHeight(labelPixels);
            FontMeasurements labelFont = MeasureFont(labelPixels);
            for (int i = 0; i < items.Length; i++)
            {
                int index = items[i], column = i % columns, row = i / columns;
                string label = MonitoringMetricPresentation.ShortName(cells[index].Metric);
                int labelWidth;
                if (!labelFont.Labels.TryGetValue(label, out labelWidth))
                    labelWidth = label.Length * CharacterWidth(labelPixels) + 3;
                if (cells[index].Metric == MonitoringMetrics.FrameTime)
                    labelWidth = Math.Max(labelWidth, labelFont.Labels[MonitoringMetricPresentation.Name(MonitoringMetrics.FrameTime)]);
                // 标签图集写入失败时保留英文显示。
                labelWidth = Math.Max(labelWidth, labelFont.Labels[MonitoringMetricPresentation.EnglishShortName(cells[index].Metric)]);
                if (cells[index].Metric == MonitoringMetrics.FrameTime)
                    labelWidth = Math.Max(labelWidth, labelFont.Labels["PRESENT"]);
                group.LabelWidths[column] = Math.Max(group.LabelWidths[column], labelWidth);
                int valuePixels = ScaledFont(FontPixels, scales[index]);
                group.ValueWidths[column] = Math.Max(group.ValueWidths[column],
                    cells[index].ValueCharacters * MeasureFont(valuePixels).DigitWidth + Math.Max(4, valuePixels / 2));
                int rowHeight = group.HeroRow && row == 0
                    ? labelHeight + InlineGap + LineHeight(valuePixels) + RowPadding
                    : Math.Max(labelHeight, LineHeight(valuePixels)) + RowPadding;
                group.RowHeights[row] = Math.Max(group.RowHeights[row], rowHeight);
            }
            for (int i = 0; i < items.Length; i++)
            {
                int column = i % columns, row = i / columns;
                int extent = group.HeroRow && row == 0
                    ? Math.Max(group.LabelWidths[column], group.ValueWidths[column])
                    : group.LabelWidths[column] + InlineGap + group.ValueWidths[column];
                group.ColumnWidths[column] = Math.Max(group.ColumnWidths[column], extent);
            }
            int width = PaddingX * 2;
            for (int column = 0; column < columns; column++)
            {
                if (column + 1 < columns) group.ColumnWidths[column] += GroupGap;
                width += group.ColumnWidths[column];
            }
            group.NaturalWidth = width;
            return group;
        }

        private int GroupHeight(MonitoringOverlayGroup group)
        {
            int height = 0;
            foreach (int row in group.RowHeights) height += row;
            return height;
        }

        private static readonly Dictionary<int, int> digitBaselines = new Dictionary<int, int>();
        private int DigitBaseline(int scale)
        {
            lock (measurementGate)
            {
                int key = Lang.Cur * 1000000 + FontPixels * 1000 + scale;
                int baseline;
                if (digitBaselines.TryGetValue(key, out baseline)) return baseline;
                int height = LineHeight(ScaledFont(FontPixels, scale));
                using (var bitmap = new Bitmap(height * 2, height))
                using (var graphics = Graphics.FromImage(bitmap))
                using (var font = new Font(FontFamily, FontPixels * scale / 100f, HudFontStyle, GraphicsUnit.Pixel))
                using (var format = new StringFormat(StringFormat.GenericTypographic))
                {
                    format.LineAlignment = StringAlignment.Center;
                    format.FormatFlags |= StringFormatFlags.NoWrap;
                    graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    graphics.DrawString("0", font, Brushes.White, new Rectangle(0, 0, bitmap.Width, height), format);
                    baseline = 0;
                    for (int y = 0; y < height; y++)
                        for (int x = 0; x < bitmap.Width; x++)
                            if (bitmap.GetPixel(x, y).A >= 128) baseline = y + 1;
                }
                digitBaselines.Add(key, baseline);
                return baseline;
            }
        }

        private double TextBaseline(int scale)
        {
            double pixels = FontPixels * scale / 100.0;
            FontMeasurements font = MeasureFont(ScaledFont(FontPixels, scale));
            // 按文字基线对齐，字体框底部并不等于基线。
            return (LineHeight(ScaledFont(FontPixels, scale)) - pixels * font.LineSpacingRatio) / 2
                + pixels * font.AscentRatio;
        }

        private void PlaceGroup(MonitoringOverlayGroup group, Rectangle bounds)
        {
            group.Bounds = bounds;
            group.HeaderBounds = Rectangle.Empty;
            int x = bounds.X + PaddingX;
            for (int column = 0; column < group.Columns; column++)
            {
                int top = bounds.Y;
                for (int row = 0; row < group.Rows; row++)
                {
                    int slot = row * group.Columns + column;
                    if (slot < group.Items.Length)
                    {
                        int index = group.Items[slot], rowHeight = group.RowHeights[row];
                        int labelHeight = LineHeight(ScaledFont(FontPixels, LabelScale));
                        int valueHeight = LineHeight(ScaledFont(FontPixels, scales[index]));
                        stacked[index] = group.HeroRow && row == 0;
                        if (stacked[index])
                        {
                            labels[index] = new Rectangle(x, top, group.LabelWidths[column], labelHeight);
                            int valueTop = top + labelHeight + InlineGap;
                            values[index] = new Rectangle(x, valueTop + 1 + DigitBaseline(FpsScale) - DigitBaseline(scales[index]),
                                group.ValueWidths[column], valueHeight);
                        }
                        else
                        {
                            int valueTop = top + (rowHeight - valueHeight) / 2;
                            int labelTop = IsCentered ? top + (rowHeight - labelHeight) / 2
                                : valueTop + (int)Math.Round(TextBaseline(scales[index]) - TextBaseline(LabelScale));
                            labels[index] = new Rectangle(x, labelTop, group.LabelWidths[column], labelHeight);
                            values[index] = new Rectangle(x + group.LabelWidths[column] + InlineGap,
                                valueTop, group.ValueWidths[column], valueHeight);
                        }
                    }
                    top += group.RowHeights[row];
                }
                x += group.ColumnWidths[column];
            }
        }

        private int AlignCornerColumns()
        {
            int columns = 1;
            foreach (var group in Groups) columns = Math.Max(columns, group.Columns);
            var labelWidths = new int[columns];
            var columnWidths = new int[columns];
            foreach (var group in Groups)
                for (int column = 0; column < group.Columns; column++)
                    labelWidths[column] = Math.Max(labelWidths[column], group.LabelWidths[column]);
            foreach (var group in Groups)
                if (group.Columns == columns)
                    for (int column = 0; column < columns; column++)
                    {
                        bool hasInlineRow = !group.HeroRow || group.Rows > 1;
                        int extent = hasInlineRow ? labelWidths[column] + InlineGap + group.ValueWidths[column]
                            : Math.Max(labelWidths[column], group.ValueWidths[column]);
                        columnWidths[column] = Math.Max(columnWidths[column], extent);
                    }
            int total = PaddingX * 2;
            for (int column = 0; column < columns; column++)
            {
                if (column + 1 < columns) columnWidths[column] += GroupGap;
                total += columnWidths[column];
            }
            foreach (var group in Groups)
            {
                if (group.Columns < columns)
                    total = Math.Max(total, PaddingX * 2 + (group.HeroRow && group.Rows == 1
                        ? Math.Max(labelWidths[0], group.ValueWidths[0])
                        : labelWidths[0] + InlineGap + group.ValueWidths[0]));
                for (int column = 0; column < group.Columns; column++)
                {
                    group.LabelWidths[column] = labelWidths[column];
                    group.ColumnWidths[column] = columnWidths[column];
                }
            }
            foreach (var group in Groups) group.NaturalWidth = total;
            return total;
        }

        private Size Arrange(List<MonitoringMetricCell> cells, int width, bool expandCorner = false)
        {
            Groups.Clear(); Separators.Clear(); Array.Clear(stacked, 0, stacked.Length);
            var names = new List<string>();
            var families = new List<int[]>();
            foreach (string name in new[] { "FRAME", "GPU", "CPU", "MEMORY" })
            {
                var members = new List<int>();
                for (int i = 0; i < cells.Count; i++)
                    if (MonitoringMetricPresentation.GroupName(cells[i].Metric) == name) members.Add(i);
                if (members.Count != 0) { names.Add(name); families.Add(members.ToArray()); }
            }
            if (families.Count == 0) return new Size(1, 1);
            int available = width - PaddingX * 2;
            if (!IsCentered)
            {
                // 公共网格过宽时整体切为单列。
                int maximum = expandCorner ? available : Math.Min(available, FontPixels * 30), widest = 0, y = PaddingY;
                for (int i = 0; i < families.Count; i++)
                {
                    int columns = names[i] == "MEMORY" ? 1 : Math.Min(2, families[i].Length);
                    Groups.Add(MeasureGroup(names[i], families[i], columns, cells));
                }
                widest = AlignCornerColumns();
                if (widest > maximum)
                {
                    Groups.Clear();
                    for (int i = 0; i < families.Count; i++)
                        Groups.Add(MeasureGroup(names[i], families[i], 1, cells));
                    widest = AlignCornerColumns();
                }
                for (int i = 0; i < Groups.Count; i++)
                {
                    var group = Groups[i];
                    PlaceGroup(group, new Rectangle(0, y, widest, GroupHeight(group)));
                    y = group.Bounds.Bottom;
                    if (i + 1 < Groups.Count) y += FamilyGap;
                }
                return new Size(widest, y + PaddingY);
            }

            int longest = 1;
            foreach (int[] family in families) longest = Math.Max(longest, family.Length);
            for (int rows = 1; rows <= longest; rows++)
            {
                Groups.Clear(); int natural = GroupGap * (families.Count - 1);
                for (int i = 0; i < families.Count; i++)
                {
                    var group = MeasureGroup(names[i], families[i], (families[i].Length + rows - 1) / rows, cells);
                    Groups.Add(group); natural += group.NaturalWidth;
                }
                if (natural <= available) break;
            }
            var bandStarts = new List<int>();
            var bandEnds = new List<int>();
            var bandWidths = new List<int>();
            var bandHeights = new List<int>();
            int first = 0, requiredWidth = 0;
            while (first < Groups.Count)
            {
                int last = first, natural = 0;
                while (last < Groups.Count)
                {
                    int next = Groups[last].NaturalWidth + (last > first ? GroupGap : 0);
                    if (last > first && natural + next > available) break;
                    natural += next; last++;
                }
                int rowCount = 0;
                for (int i = first; i < last; i++) rowCount = Math.Max(rowCount, Groups[i].Rows);
                var bandRows = new int[rowCount];
                for (int i = first; i < last; i++)
                    for (int row = 0; row < Groups[i].Rows; row++)
                        bandRows[row] = Math.Max(bandRows[row], Groups[i].RowHeights[row]);
                int height = 0;
                foreach (int rowHeight in bandRows) height += rowHeight;
                for (int i = first; i < last; i++)
                    for (int row = 0; row < Groups[i].Rows; row++) Groups[i].RowHeights[row] = bandRows[row];
                requiredWidth = Math.Max(requiredWidth, natural);
                bandStarts.Add(first); bandEnds.Add(last); bandWidths.Add(natural); bandHeights.Add(height);
                first = last;
            }
            int bandTop = PaddingY;
            for (int band = 0; band < bandStarts.Count; band++)
            {
                int x = (requiredWidth - bandWidths[band]) / 2;
                for (int i = bandStarts[band]; i < bandEnds[band]; i++)
                {
                    var group = Groups[i];
                    PlaceGroup(group, new Rectangle(x, bandTop, group.NaturalWidth, bandHeights[band]));
                    x = group.Bounds.Right + GroupGap;
                }
                bandTop += bandHeights[band];
                if (band + 1 < bandStarts.Count) bandTop += FamilyGap;
            }
            return new Size(requiredWidth, bandTop + PaddingY);
        }

        internal static int SurfaceAlpha(int opacity)
        { return (Math.Max(0, Math.Min(100, opacity)) * 255 + 50) / 100; }

        internal static int CoveredAlpha(int opacity, int coverage)
        { return (SurfaceAlpha(opacity) * Math.Max(0, Math.Min(255, coverage)) + 127) / 255; }

        private void BuildSurface()
        {
            int width = Bounds.Width, height = Bounds.Height;
            int cut = Math.Min(3, Math.Max(2, FontPixels / 12));
            cut = Math.Min(cut, Math.Min((width - 1) / 2, (height - 1) / 2));
            SurfaceCutPixels = cut;
            if (cut <= 0)
            {
                SurfaceFragments.Add(new MonitoringOverlayFill(new Rectangle(0, 0, width, height), 255));
                return;
            }
            SurfaceFragments.Add(new MonitoringOverlayFill(new Rectangle(0, cut, width, height - 2 * cut), 255));
            for (int row = 0; row < cut; row++)
            {
                SurfaceFragments.Add(new MonitoringOverlayFill(new Rectangle(0, row, width - cut + row, 1), 255));
                SurfaceFragments.Add(new MonitoringOverlayFill(new Rectangle(width - cut + row, row, 1, 1), 128));
                SurfaceFragments.Add(new MonitoringOverlayFill(new Rectangle(row + 1, height - cut + row, width - row - 1, 1), 255));
                SurfaceFragments.Add(new MonitoringOverlayFill(new Rectangle(row, height - cut + row, 1, 1), 128));
            }
        }

        internal static MonitoringOverlayGeometry Calculate(MonitoringLayout layout, MonitoringMetrics metrics,
            int viewportWidth, int viewportHeight, int offsetX = 0, int offsetY = 0, int scale = 100, MonitoringSnapshot snapshot = null, int spacing = 100)
        {
            var cells = MonitoringMetricPresentation.Cells(snapshot ?? MonitoringMetricPresentation.Example, metrics);
            int width = Width(viewportWidth), height = Height(viewportHeight);
            scale = Math.Max(MonitoringPreferences.MinimumScalePercent, Math.Min(MonitoringPreferences.MaximumScalePercent, scale));
            var geometry = new MonitoringOverlayGeometry { ViewportWidth = width, ViewportHeight = height,
                SpacingPercent = Math.Max(MonitoringPreferences.MinimumSpacingPercent, Math.Min(MonitoringPreferences.MaximumSpacingPercent, spacing)),
                IsCentered = layout == MonitoringLayout.TopBar,
                FontPixels = Math.Max(8, (int)Math.Round(RtssOverlayFormat.BaseFontPixels(height) * scale / 100.0)),
                labels = new Rectangle[cells.Count], values = new Rectangle[cells.Count], scales = new int[cells.Count], stacked = new bool[cells.Count] };
            for (int i = 0; i < cells.Count; i++) geometry.scales[i] = cells[i].Metric == MonitoringMetrics.Fps
                ? (geometry.IsCentered ? BarFpsScale : FpsScale) : DefaultValueScale;
            Size size;
            while (true)
            {
                size = geometry.Arrange(cells, width);
                if (!geometry.IsCentered && size.Height > height - 4)
                    size = geometry.Arrange(cells, width, true);
                if (geometry.FontPixels <= 8 || (size.Width <= width - 4 && size.Height <= height - 4)) break;
                geometry.FontPixels--;
            }
            int hudWidth = Math.Min(width - 4, size.Width);
            int hudHeight = Math.Min(height - 4, size.Height);
            int inset = Math.Max(4, geometry.FontPixels / 2);
            int x = geometry.IsCentered ? (width - hudWidth) / 2
                : layout == MonitoringLayout.TopRight ? width - hudWidth - inset : inset;
            int y = inset;
            if (!geometry.IsCentered) x += (int)Math.Round(Math.Max(-1000, Math.Min(1000, offsetX)) * width / 1000.0);
            y += (int)Math.Round(Math.Max(-1000, Math.Min(1000, offsetY)) * height / 1000.0);
            x = Math.Max(0, Math.Min(width - hudWidth, x));
            y = Math.Max(0, Math.Min(height - hudHeight, y));
            geometry.Bounds = new Rectangle(x, y, hudWidth, hudHeight);
            geometry.BuildSurface();
            return geometry;
        }

        internal static Point GetOffsets(MonitoringLayout layout, MonitoringMetrics metrics,
            int viewportWidth, int viewportHeight, int x, int y, int scale = 100, int spacing = 100)
        {
            var baseline = Calculate(layout, metrics, viewportWidth, viewportHeight, 0, 0, scale, spacing: spacing);
            return new Point(baseline.IsCentered ? 0 : EncodeOffset(x, baseline.Bounds.X, baseline.Bounds.Width, baseline.ViewportWidth),
                EncodeOffset(y, baseline.Bounds.Y, baseline.Bounds.Height, baseline.ViewportHeight));
        }

        private static int EncodeOffset(int position, int anchor, int extent, int viewport)
        {
            int maximum = viewport - extent;
            position = Math.Max(0, Math.Min(maximum, position));
            double offset = (position - anchor) * 1000.0 / viewport;
            // 边缘坐标向外取整，抵消千分比存储误差。
            if (position == 0) return (int)Math.Floor(offset);
            if (position == maximum) return (int)Math.Ceiling(offset);
            return (int)Math.Round(offset);
        }
    }
}
