using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace PaviseApp
{
    // RTSS 用标签图集显示中文，图集中只放固定标签。
    internal sealed class RtssLabelAtlas
    {
        private static readonly object gate = new object();
        private static readonly Dictionary<string, RtssLabelAtlas> cache = new Dictionary<string, RtssLabelAtlas>();
        private readonly Dictionary<string, Rectangle> sprites = new Dictionary<string, Rectangle>();
        internal string FileName { get; private set; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern uint GetShortPathName(string path, StringBuilder buffer, uint length);
#if PAVISE_SELFTEST
        internal static string TestDirectory = Path.Combine(Path.GetTempPath(), "Pavise-HudTests-" + System.Diagnostics.Process.GetCurrentProcess().Id);
#endif
        internal static RtssLabelAtlas Get(int fontPixels)
        {
            if (Lang.Cur == 1) return null;
            lock (gate)
            {
                string directory = string.IsNullOrEmpty(Paths.Data) ? null : Path.Combine(Paths.Data, "OverlayLabels");
#if PAVISE_SELFTEST
                directory = TestDirectory;
#endif
                if (directory == null) return null;
                string key = directory + "|" + Lang.Cur + "|" + fontPixels;
                RtssLabelAtlas atlas;
                if (cache.TryGetValue(key, out atlas)) return atlas;
                // 写入失败也缓存，避免目录只读时反复写盘。
                try { atlas = Create(directory, fontPixels); }
                catch (IOException) { atlas = null; }
                catch (UnauthorizedAccessException) { atlas = null; }
                catch (System.Runtime.InteropServices.ExternalException) { atlas = null; }
                cache.Add(key, atlas);
                return atlas;
            }
        }

        private static RtssLabelAtlas Create(string directory, int fontPixels)
        {
            var atlas = new RtssLabelAtlas();
            var labels = new List<string>();
            foreach (MonitoringMetricCell cell in MonitoringMetricPresentation.Cells(MonitoringMetricPresentation.Example, MonitoringMetrics.All))
            {
                string label = MonitoringMetricPresentation.ShortName(cell);
                if (!labels.Contains(label)) labels.Add(label);
            }
            foreach (bool gpu in new[] { false, true })
            {
                string label = MonitoringMetricPresentation.Name(MonitoringMetrics.FrameTime, gpu);
                if (!labels.Contains(label)) labels.Add(label);
            }
            float pixels = fontPixels * MonitoringOverlayGeometry.LabelScale / 100f;
            int height = MonitoringOverlayGeometry.LineHeight((int)Math.Ceiling(pixels));
            int width = 1;
            using (var font = new Font(MonitoringOverlayGeometry.FontFamily, pixels, MonitoringOverlayGeometry.HudFontStyle, GraphicsUnit.Pixel))
            using (var format = new StringFormat(StringFormat.GenericTypographic))
            {
                format.FormatFlags |= StringFormatFlags.NoWrap;
                format.LineAlignment = StringAlignment.Center;
                using (var measure = new Bitmap(1, 1))
                using (var graphics = Graphics.FromImage(measure))
                    for (int i = 0; i < labels.Count; i++)
                    {
                        int labelWidth = (int)Math.Ceiling(graphics.MeasureString(labels[i], font, int.MaxValue, format).Width) + 3;
                        atlas.sprites.Add(labels[i], new Rectangle(0, i * (height + 2), labelWidth, height));
                        width = Math.Max(width, labelWidth);
                    }
                // 图集按内容命名，避免升级后复用旧纹理。
                string identity = "v2|2|" + MonitoringOverlayGeometry.FontFamily + "|" + MonitoringOverlayGeometry.LabelScale + "|" + fontPixels + "|" + string.Join("|", labels.ToArray());
                string hash;
                using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(identity))).Replace("-", "").Substring(0, 24);
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, "labels-" + hash + ".png");
                if (!File.Exists(path))
                    using (var bitmap = new Bitmap(width * RtssOverlayFormat.FontRasterFactor, labels.Count * (height + 2) * RtssOverlayFormat.FontRasterFactor, PixelFormat.Format32bppArgb))
                    using (var graphics = Graphics.FromImage(bitmap))
                    {
                        graphics.Clear(Color.Transparent);
                        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                        graphics.ScaleTransform(RtssOverlayFormat.FontRasterFactor, RtssOverlayFormat.FontRasterFactor);
                        foreach (string label in labels)
                            graphics.DrawString(label, font, Brushes.White, atlas.sprites[label], format);
                        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                        try
                        {
                            bitmap.Save(temporary, ImageFormat.Png);
                            try { File.Move(temporary, path); }
                            catch (IOException) { if (!File.Exists(path)) throw; }
                        }
                        finally { if (File.Exists(temporary)) File.Delete(temporary); }
                    }
                var shortPath = new StringBuilder(32768);
                uint length = GetShortPathName(path, shortPath, (uint)shortPath.Capacity);
                if (length > 0 && length < shortPath.Capacity) path = shortPath.ToString();
                Encoding encoding = Encoding.GetEncoding((int)RtssTextEncoding.NativeCodePage);
                if (encoding.GetString(encoding.GetBytes(path)) != path || path.IndexOf('"') >= 0 || path.IndexOf('>') >= 0)
                    return null;
                atlas.FileName = path;
            }
            return atlas;
        }

        internal string Sprite(string label)
        {
            Rectangle bounds;
            if (!sprites.TryGetValue(label, out bounds)) return null;
            int factor = RtssOverlayFormat.FontRasterFactor;
            return string.Format(CultureInfo.InvariantCulture, "<I={0},{1},{2},{3},{4},{5}>",
                bounds.Width, bounds.Height, bounds.X * factor, bounds.Y * factor, bounds.Width * factor, bounds.Height * factor);
        }
    }
}
