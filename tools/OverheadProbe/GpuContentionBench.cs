// @author bdth 2074055628@qq.com
// 文件用途 GPU 争抢台架 受害者窗口测帧间隔 满载窗口做争抢 setgpu 用线上同款调用降级验证 GPU 让位收益

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

static class GpuContentionBench
{
    [DllImport("gdi32.dll")] static extern int D3DKMTSetProcessSchedulingPriorityClass(IntPtr h, int cls);
    [DllImport("gdi32.dll")] static extern int D3DKMTGetProcessSchedulingPriorityClass(IntPtr h, out int cls);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length == 0) return 2;
        if (args[0] == "setgpu")
        {
            int pid = int.Parse(args[1]), cls = int.Parse(args[2]);
            IntPtr h = OpenProcess(0x0200u | 0x1000u, false, pid);
            if (h == IntPtr.Zero) { File.WriteAllText(args[3], "OPEN_FAIL"); return 3; }
            try
            {
                int st = D3DKMTSetProcessSchedulingPriorityClass(h, cls);
                int now; D3DKMTGetProcessSchedulingPriorityClass(h, out now);
                File.WriteAllText(args[3], "status=0x" + st.ToString("X") + " readback=" + now);
                return st == 0 && now == cls ? 0 : 4;
            }
            finally { CloseHandle(h); }
        }
        var app = new Application();
        if (args[0] == "burn") { app.Run(Burner()); return 0; }
        if (args[0] == "victim")
        {
            int secs = int.Parse(args[1]);
            app.Run(Victim(secs, args[2]));
            return 0;
        }
        return 2;
    }

    static Window Burner()
    {
        var canvas = new Canvas { Background = Brushes.Black };
        var shapes = new List<Ellipse>();
        for (int i = 0; i < 8; i++)
        {
            var e = new Ellipse
            {
                Width = 420, Height = 420,
                Fill = new RadialGradientBrush(Colors.OrangeRed, Colors.DarkBlue),
                Effect = new BlurEffect { Radius = 90 }
            };
            canvas.Children.Add(e);
            shapes.Add(e);
        }
        var w = new Window
        {
            Title = "GpuBurner", Content = canvas, Width = 900, Height = 650,
            Left = 40, Top = 40, ShowActivated = false, Topmost = false
        };
        double t = 0;
        CompositionTarget.Rendering += delegate
        {
            t += 0.11;
            for (int i = 0; i < shapes.Count; i++)
            {
                Canvas.SetLeft(shapes[i], 220 + 200 * Math.Sin(t + i));
                Canvas.SetTop(shapes[i], 130 + 130 * Math.Cos(t * 1.3 + i));
                ((BlurEffect)shapes[i].Effect).Radius = 70 + 40 * Math.Abs(Math.Sin(t + i * 0.7));
            }
        };
        return w;
    }

    static Window Victim(int secs, string outPath)
    {
        var canvas = new Canvas { Background = Brushes.Black };
        var shapes = new List<Ellipse>();
        for (int i = 0; i < 6; i++)
        {
            var e = new Ellipse
            {
                Width = 180, Height = 180,
                Fill = new RadialGradientBrush(Colors.Lime, Colors.Purple),
                Effect = new BlurEffect { Radius = 25 }
            };
            canvas.Children.Add(e);
            shapes.Add(e);
        }
        var w = new Window
        {
            Title = "GpuVictim", Content = canvas, Width = 960, Height = 600,
            Left = 500, Top = 300, Topmost = true
        };
        var sw = new Stopwatch();
        var samples = new List<double>(20000);
        double warmupMs = 2000, t = 0;
        long lastTicks = 0;
        bool done = false;
        CompositionTarget.Rendering += delegate
        {
            if (done) return;
            if (!sw.IsRunning) { sw.Start(); lastTicks = sw.ElapsedTicks; return; }
            long now = sw.ElapsedTicks;
            double ms = (now - lastTicks) * 1000.0 / Stopwatch.Frequency;
            lastTicks = now;
            t += 0.09;
            for (int i = 0; i < shapes.Count; i++)
            {
                Canvas.SetLeft(shapes[i], 380 + 320 * Math.Sin(t + i * 1.1));
                Canvas.SetTop(shapes[i], 200 + 160 * Math.Cos(t * 1.4 + i));
            }
            if (sw.ElapsedMilliseconds < warmupMs) return;
            samples.Add(ms);
            if (sw.ElapsedMilliseconds >= warmupMs + secs * 1000)
            {
                done = true;
                File.WriteAllText(outPath, Summarize(samples), Encoding.UTF8);
                Application.Current.Shutdown();
            }
        };
        return w;
    }

    static string Summarize(List<double> s)
    {
        var sorted = new List<double>(s);
        sorted.Sort();
        int n = sorted.Count;
        double med = n > 0 ? sorted[n / 2] : 0;
        int worst = Math.Max(1, n / 100);
        double sum = 0;
        for (int i = n - worst; i < n; i++) sum += sorted[i];
        double low1 = worst > 0 ? sum / worst : 0;
        double p999 = n > 0 ? sorted[Math.Min(n - 1, (int)(n * 0.999))] : 0;
        return "frames=" + n + " median=" + med.ToString("F2")
            + " p99.9=" + p999.ToString("F2") + " low1pct=" + low1.ToString("F2");
    }
}
