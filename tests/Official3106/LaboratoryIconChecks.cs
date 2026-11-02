using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace PaviseApp
{
    // This runner references only the production vector painter and System.Drawing.
    // It never instantiates a form, loads settings or starts a product worker.
    internal static class LaboratoryIconChecks
    {
        private static readonly string[] Keys = {
            "LabEppYieldV1", "LabGpuClockMaxV1", "LabGpuPowerMaxV1",
            "LabAudioLowBufferV1", "LabCpuIdleStrategyV1", "LabFileFilterV1", "LabClockRepairV1", "LabAudioEffectsV1"
        };
        private static int checks;
        private static void Check(bool ok, string why)
        {
            checks++;
            if (!ok) throw new InvalidOperationException(why);
        }
        private static Bitmap Render(string key, int size, Color ink)
        {
            var bitmap = new Bitmap(size + 16, size + 16, PixelFormat.Format32bppArgb);
            using (Graphics graphics = Graphics.FromImage(bitmap))
                PerformanceFeatureIcons.Draw(graphics, new Rectangle(8, 8, size, size), key, ink);
            return bitmap;
        }
        private static HashSet<int> Pixels(Bitmap bitmap)
        {
            var visible = new HashSet<int>();
            for (int y = 0; y < bitmap.Height; y++)
                for (int x = 0; x < bitmap.Width; x++)
                    if (bitmap.GetPixel(x, y).A >= 32) visible.Add(y * bitmap.Width + x);
            return visible;
        }
        private static void CheckDrawingState()
        {
            using (var bitmap = new Bitmap(80, 80))
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.TranslateTransform(3f, 5f);
                graphics.SetClip(new Rectangle(2, 4, 63, 61));
                graphics.SmoothingMode = SmoothingMode.HighSpeed;
                graphics.PixelOffsetMode = PixelOffsetMode.Half;
                SmoothingMode smoothing = graphics.SmoothingMode;
                PixelOffsetMode offset = graphics.PixelOffsetMode;
                float[] before = graphics.Transform.Elements;
                RectangleF clip = graphics.ClipBounds;
                PerformanceFeatureIcons.Draw(graphics, new Rectangle(8, 8, 48, 32), Keys[0], Color.Crimson);
                float[] after = graphics.Transform.Elements;
                for (int i = 0; i < before.Length; i++) Check(before[i] == after[i], "Caller transform changed");
                Check(graphics.ClipBounds == clip, "Caller clipping changed");
                Check(graphics.SmoothingMode == smoothing, "Caller smoothing changed");
                Check(graphics.PixelOffsetMode == offset, "Caller pixel offset changed");
            }
            PerformanceFeatureIcons.Draw(null, Rectangle.Empty, Keys[0], Color.Red);
        }
        private static void WriteAtlas(string output)
        {
            const int width = 1440, row = 100;
            using (var bitmap = new Bitmap(width, row * 2))
            using (Graphics graphics = Graphics.FromImage(bitmap))
            using (var label = new Font("Segoe UI", 9f))
            {
                Color[] backgrounds = { Color.FromArgb(16, 18, 22), Color.FromArgb(244, 245, 247) };
                Color[] inks = { Color.FromArgb(255, 45, 83), Color.FromArgb(192, 16, 53) };
                for (int theme = 0; theme < 2; theme++)
                {
                    using (var brush = new SolidBrush(backgrounds[theme])) graphics.FillRectangle(brush, 0, theme * row, width, row);
                    for (int i = 0; i < Keys.Length; i++)
                    {
                        PerformanceFeatureIcons.Draw(graphics, new Rectangle(i * 180 + 16, theme * row + 12, 48, 48), Keys[i], inks[theme]);
                        using (var brush = new SolidBrush(theme == 0 ? Color.WhiteSmoke : Color.Black))
                            graphics.DrawString(Keys[i], label, brush, i * 180 + 8, theme * row + 72);
                    }
                }
                bitmap.Save(Path.Combine(output, "laboratory-icons.png"), ImageFormat.Png);
            }
        }
        private static int Main(string[] args)
        {
            try
            {
                string output = args.Length == 0 ? "." : args[0];
                Directory.CreateDirectory(output);
                foreach (string key in Keys)
                {
                    Check(PerformanceFeatureIcons.Supports(key), "Unregistered laboratory icon " + key);
                    Check(PerformanceFeatureIcons.Supports(key.ToLowerInvariant()), "Icon key is case sensitive " + key);
                }
                Color[] colors = { Color.FromArgb(255, 45, 83), Color.FromArgb(192, 16, 53), Color.FromArgb(128, 94, 184, 255) };
                foreach (int size in new[] { 24, 32, 48, 64, 96 })
                    foreach (Color ink in colors)
                    {
                        var shapes = new List<HashSet<int>>();
                        foreach (string key in Keys)
                            using (Bitmap bitmap = Render(key, size, ink))
                            {
                                HashSet<int> visible = Pixels(bitmap);
                                Check(visible.Count > size * 2, "Icon vanished at " + size + ": " + key);
                                for (int y = 0; y < bitmap.Height; y++)
                                    for (int x = 0; x < bitmap.Width; x++)
                                    {
                                        Color pixel = bitmap.GetPixel(x, y);
                                        if (x < 8 || x >= size + 8 || y < 8 || y >= size + 8)
                                            Check(pixel.A == 0, "Icon escapes its bounds: " + key);
                                        if (pixel.A >= 64)
                                            Check(Math.Abs(pixel.R - ink.R) <= 8 && Math.Abs(pixel.G - ink.G) <= 8 && Math.Abs(pixel.B - ink.B) <= 8,
                                                "Icon ignores caller theme ink: " + key + " expected=" + ink + " actual=" + pixel);
                                    }
                                shapes.Add(visible);
                            }
                        for (int i = 0; i < shapes.Count; i++)
                            for (int j = i + 1; j < shapes.Count; j++)
                            {
                                var union = new HashSet<int>(shapes[i]); union.UnionWith(shapes[j]);
                                var difference = new HashSet<int>(shapes[i]); difference.SymmetricExceptWith(shapes[j]);
                                Check(difference.Count > union.Count / 5, "Laboratory icons look identical at " + size + ": " + Keys[i] + "/" + Keys[j]);
                            }
                        using (Bitmap fallback = Render("unknown laboratory setting", size, ink))
                        {
                            HashSet<int> fallbackShape = Pixels(fallback);
                            foreach (HashSet<int> shape in shapes) Check(!shape.SetEquals(fallbackShape), "Laboratory icon uses fallback");
                        }
                    }
                CheckDrawingState(); WriteAtlas(output);
                Console.WriteLine("PASS laboratory vector icons assertions=" + checks + " designs=8 sizes=5 inks=3 windows=0");
                return 0;
            }
            catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
        }
    }
}
