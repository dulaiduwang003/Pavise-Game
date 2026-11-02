using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace PaviseApp
{
    internal static class CoreAnimationVisualChecks
    {
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly MethodInfo Render = typeof(PaviseCore).GetMethod("RenderFrame", Hidden);
        private static int checks;
        private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks++; }
        private static void Frame(PaviseCore core, Bitmap bitmap, double time)
        { using (Graphics g = Graphics.FromImage(bitmap)) Render.Invoke(core, new object[] { g, time }); }
        private static long Hash(Bitmap b)
        {
            long h = 17;
            unchecked { for (int y = 0; y < b.Height; y += 3) for (int x = 0; x < b.Width; x += 3) h = h * 31 + b.GetPixel(x, y).ToArgb(); }
            return h;
        }
        private static void CheckOrbitContinuity()
        {
            MethodInfo orbit = typeof(PaviseCore).GetMethod("DrawOrbitFlow", BindingFlags.Static | BindingFlags.NonPublic);
            foreach (double boundary in new[] { 180.0, 360.0 })
            using (var before = new Bitmap(250, 160))
            using (var after = new Bitmap(250, 160))
            {
                for (int frame = 0; frame < 2; frame++)
                using (Graphics g = Graphics.FromImage(frame == 0 ? before : after))
                {
                    g.Clear(Color.Black); g.TranslateTransform(125, 80);
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    double at = boundary + (frame == 0 ? -.001 : .001);
                    foreach (bool front in new[] { false, true })
                        orbit.Invoke(null, new object[] { g, 112f, 64f, at, Color.Lime, 1f, front });
                }
                long difference = 0;
                for (int y = 0; y < before.Height; y++)
                for (int x = 0; x < before.Width; x++)
                {
                    Color a = before.GetPixel(x, y), b = after.GetPixel(x, y);
                    difference += Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);
                }
                Check(difference / (double)(before.Width * before.Height * 3) < .05,
                    "Orbit pops at the depth/loop boundary: " + boundary);
            }
        }
        private static void CheckEyeMotion()
        {
            MethodInfo gaze = typeof(PaviseCore).GetMethod("CompetitiveGaze", BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo opening = typeof(PaviseCore).GetMethod("CompetitiveEyeOpen", BindingFlags.Static | BindingFlags.NonPublic);
            PointF previous = PointF.Empty;
            float previousOpen = 1;
            for (int frame = 0; frame <= 1200; frame++)
            {
                double time = frame / 120.0;
                PointF at = (PointF)gaze.Invoke(null, new object[] { time });
                float open = (float)opening.Invoke(null, new object[] { time });
                Check(Math.Abs(at.X) <= 12 && Math.Abs(at.Y) <= 5, "Pupil leaves its safe travel area");
                Check(open > .04f && open <= 1, "Blink makes a singular or inverted transform");
                Check(Math.Abs(at.X - previous.X) < .5f && Math.Abs(at.Y - previous.Y) < .5f,
                    "Gaze jumps at a hold or loop boundary");
                Check(Math.Abs(open - previousOpen) < .14f, "Eyelids jump between frames");
                previous = at; previousOpen = open;
            }
            foreach (double time in new[] { 3.14, 8.44 })
                Check((float)opening.Invoke(null, new object[] { time }) < .1f, "Eye never closes during blink");
            foreach (double time in new[] { 0.0, 3.5, 8.8, 9.8 })
                Check((float)opening.Invoke(null, new object[] { time }) > .95f, "Eye does not reopen");
        }
        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                Settings.UseTransientStoreForCurrentProcess();
                Dpi.Init(); Lang.Init(); Lang.Cur = 0;
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                string output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
                PerformancePreset[] modes = { PerformancePreset.Standard, PerformancePreset.Competitive, PerformancePreset.Handheld, PerformancePreset.Extreme };
                string[] names = { "smart", "competitive", "handheld", "extreme" };
                CheckOrbitContinuity();
                CheckEyeMotion();
                for (int d = 0; d < 3; d++)
                foreach (bool light in new[] { false, true })
                foreach (int language in new[] { 0, 1 })
                {
                    int dpi = new[] { 96, 144, 192 }[d];
                    Dpi.Update(dpi); Theme.DropFontCache(); Theme.SetLight(light); Lang.Cur = language;
                    using (var core = new PaviseCore())
                    using (var bitmap = new Bitmap(Theme.S(370), Theme.S(376), PixelFormat.Format32bppPArgb))
                    {
                        core.Size = bitmap.Size;
                        long last = 0;
                        for (int m = 0; m < modes.Length; m++)
                        {
                            Theme.SetMode(modes[m], false); core.SetState(modes[m], true, false);
                            Frame(core, bitmap, 1.25); long first = Hash(bitmap);
                            object layers = typeof(PaviseCore).GetField("staticLayers", Hidden).GetValue(core);
                            Check(layers != null && (layers.GetType().GetField("Hub", Hidden | BindingFlags.Public).GetValue(layers) != null)
                                == (modes[m] != PerformancePreset.Standard), "Smart uses a dynamic crystal instead of a cached hub");
                            Check(first != last, "Mode switch retained previous geometry"); last = first;
                            Frame(core, bitmap, 1.25); Check(Hash(bitmap) == first, "Cached frame not deterministic");
                            long previous = first;
                            for (int frame = 1; frame <= 4; frame++)
                            {
                                Frame(core, bitmap, 1.25 + frame / 60.0);
                                long current = Hash(bitmap);
                                Check(current != previous, "Adjacent animation frames stall: " + names[m]);
                                previous = current;
                            }
                            Frame(core, bitmap, 2.25); long moving = Hash(bitmap);
                            Check(moving != first, "Mode does not animate");
                            bitmap.Save(Path.Combine(output, names[m] + "-" + dpi + "-" + language + (light ? "-light" : "-dark") + ".png"));
                            core.SetState(modes[m], true, true); Frame(core, bitmap, 2.25);
                            Check(Hash(bitmap) != moving, "Active state did not update");
                            core.SetState(modes[m], false, false); Frame(core, bitmap, 1.25);
                            Check(Hash(bitmap) != first, "Offline state did not update");
                        }
                    }
                }
                Dpi.Update(144); Theme.DropFontCache(); Theme.SetLight(false); Lang.Cur = 0;
                for (int m = 0; m < modes.Length; m++)
                {
                    Theme.SetMode(modes[m], false);
                    using (var core = new PaviseCore())
                    using (var bitmap = new Bitmap(Theme.S(370), Theme.S(376), PixelFormat.Format32bppPArgb))
                    {
                        core.Size = bitmap.Size; core.SetState(modes[m], true, false);
                        string frames = Path.Combine(output, names[m]); Directory.CreateDirectory(frames);
                        int count = modes[m] == PerformancePreset.Competitive || modes[m] == PerformancePreset.Standard ? 600 : 180;
                        for (int i = 0; i < count; i++)
                        { Frame(core, bitmap, i / 60.0); bitmap.Save(Path.Combine(frames, i.ToString("D3") + ".png")); }
                        var watch = Stopwatch.StartNew();
                        for (int i = 0; i < 240; i++) Frame(core, bitmap, i / 60.0);
                        watch.Stop(); Console.WriteLine(names[m] + " warmed frame average ms=" + (watch.Elapsed.TotalMilliseconds / 240).ToString("F3"));
                    }
                }
                for (int mask = 0; mask < 128; mask++)
                {
                    bool expected = (mask & 63) == 63 && (mask & 64) == 0;
                    Check(PaviseCore.ShouldAnimate((mask & 1) != 0, (mask & 2) != 0, (mask & 4) != 0,
                        (mask & 8) != 0, (mask & 16) != 0 ? FormWindowState.Normal : FormWindowState.Minimized,
                        (mask & 64) != 0, (mask & 32) != 0) == expected, "Animation gating changed");
                }
                Console.WriteLine("PASS core animation checks=" + checks + " windows_shown=0 settings=transient");
                return 0;
            }
            catch (Exception e) { Console.Error.WriteLine(e); return 1; }
        }
    }
}
