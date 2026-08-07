// @author bdth 2074055628@qq.com
// 文件用途 冒充全屏游戏并统计帧间隔分布 用于验证 Pavise 是否偷帧

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace PaviseFrameProbe
{
    internal sealed class ProbeForm : Form
    {
        private readonly List<double> frames = new List<double>(200000);
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly double targetMs;
        private readonly double runSeconds;
        private readonly string outPath;
        private readonly string label;
        private double lastTick;
        private double workBallast;
        private bool finished;

        [DllImport("winmm.dll")]
        private static extern uint timeBeginPeriod(uint ms);
        [DllImport("winmm.dll")]
        private static extern uint timeEndPeriod(uint ms);
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")]
        private static extern bool BringWindowToTop(IntPtr h);
        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint from, uint to, bool attach);
        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        public ProbeForm(double fps, double seconds, string outFile, string tag)
        {
            targetMs = 1000.0 / fps;
            runSeconds = seconds;
            outPath = outFile;
            label = tag;

            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Maximized;
            TopMost = true;
            BackColor = Color.Black;
            ShowInTaskbar = true;
            Text = "Pavise Frame Probe";
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                | ControlStyles.Opaque, true);
            Bounds = Screen.PrimaryScreen.Bounds;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            ForceForeground();
            timeBeginPeriod(1);
            var worker = new Thread(RenderLoop);
            worker.IsBackground = true;
            worker.Priority = ThreadPriority.Highest;
            worker.Start();
        }

        private void ForceForeground()
        {
            try
            {
                IntPtr self = Handle;
                IntPtr fore = GetForegroundWindow();
                uint foreThread = 0;
                if (fore != IntPtr.Zero)
                {
                    uint pid;
                    foreThread = GetWindowThreadProcessId(fore, out pid);
                }
                uint ownThread = GetCurrentThreadId();
                bool attached = foreThread != 0 && foreThread != ownThread
                    && AttachThreadInput(foreThread, ownThread, true);
                ShowWindow(self, 5);
                BringWindowToTop(self);
                SetForegroundWindow(self);
                Activate();
                Focus();
                if (attached) AttachThreadInput(foreThread, ownThread, false);
            }
            catch { }
        }

        private void RenderLoop()
        {
            var spin = new SpinWait();
            lastTick = clock.Elapsed.TotalMilliseconds;
            double nextDeadline = lastTick + targetMs;
            while (clock.Elapsed.TotalSeconds < runSeconds)
            {
                SimulateFrameWork();

                while (true)
                {
                    double now = clock.Elapsed.TotalMilliseconds;
                    double remaining = nextDeadline - now;
                    if (remaining <= 0) break;
                    if (remaining > 2.0) Thread.Sleep(1);
                    else Thread.SpinWait(120);
                }

                double tick = clock.Elapsed.TotalMilliseconds;
                frames.Add(tick - lastTick);
                lastTick = tick;
                nextDeadline += targetMs;
                if (nextDeadline < tick) nextDeadline = tick + targetMs;
            }
            finished = true;
            try { BeginInvoke((MethodInvoker)Finish); } catch { Finish(); }
        }

        private void SimulateFrameWork()
        {
            double acc = workBallast;
            for (int i = 0; i < 24000; i++) acc += Math.Sqrt(i + 1.0) * 1.0000001;
            workBallast = acc > 1e18 ? 0 : acc;
        }

        private void Finish()
        {
            timeEndPeriod(1);
            var report = Summarize();
            Console.WriteLine(report);
            if (outPath != null)
            {
                try { File.WriteAllText(outPath, report, Encoding.UTF8); } catch { }
            }
            try { Close(); } catch { }
            Application.Exit();
        }

        private string Summarize()
        {
            var sorted = new List<double>(frames);
            if (sorted.Count > 20) sorted.RemoveRange(0, 20);
            sorted.Sort();
            var sb = new StringBuilder();
            sb.AppendLine("pavise-frame-probe-v1");
            sb.AppendLine("label=" + label);
            sb.AppendLine("target_ms=" + targetMs.ToString("F3", CultureInfo.InvariantCulture));
            sb.AppendLine("frames=" + sorted.Count);
            if (sorted.Count == 0) return sb.ToString();
            double sum = 0;
            foreach (double v in sorted) sum += v;
            double mean = sum / sorted.Count;
            double varsum = 0;
            foreach (double v in sorted) varsum += (v - mean) * (v - mean);
            sb.AppendLine("mean_ms=" + mean.ToString("F4", CultureInfo.InvariantCulture));
            sb.AppendLine("stdev_ms=" + Math.Sqrt(varsum / sorted.Count).ToString("F4", CultureInfo.InvariantCulture));
            sb.AppendLine("p50_ms=" + Pct(sorted, 50).ToString("F4", CultureInfo.InvariantCulture));
            sb.AppendLine("p90_ms=" + Pct(sorted, 90).ToString("F4", CultureInfo.InvariantCulture));
            sb.AppendLine("p99_ms=" + Pct(sorted, 99).ToString("F4", CultureInfo.InvariantCulture));
            sb.AppendLine("p99_9_ms=" + Pct(sorted, 99.9).ToString("F4", CultureInfo.InvariantCulture));
            sb.AppendLine("max_ms=" + sorted[sorted.Count - 1].ToString("F4", CultureInfo.InvariantCulture));
            int over15 = 0, over20 = 0, over30 = 0;
            foreach (double v in sorted)
            {
                if (v > targetMs * 1.5) over15++;
                if (v > targetMs * 2.0) over20++;
                if (v > targetMs * 3.0) over30++;
            }
            sb.AppendLine("stutter_1_5x=" + over15);
            sb.AppendLine("stutter_2x=" + over20);
            sb.AppendLine("stutter_3x=" + over30);
            sb.AppendLine("stutter_1_5x_per_min="
                + (over15 / (runSeconds / 60.0)).ToString("F2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        private static double Pct(List<double> sorted, double p)
        {
            if (sorted.Count == 0) return 0;
            int idx = (int)Math.Round(p / 100.0 * (sorted.Count - 1));
            if (idx < 0) idx = 0;
            if (idx >= sorted.Count) idx = sorted.Count - 1;
            return sorted[idx];
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Color.Black);
            using (var b = new SolidBrush(Color.FromArgb(0, 200, 120)))
                e.Graphics.FillRectangle(b, 40, 40, 400, 60);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!finished && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; return; }
            base.OnFormClosing(e);
        }
    }

    internal static class Program
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        private static void MakeDpiAware()
        {
            try { if (SetProcessDpiAwarenessContext((IntPtr)(-4))) return; }
            catch { }
            try { SetProcessDPIAware(); }
            catch { }
        }

        [STAThread]
        private static int Main(string[] args)
        {
            MakeDpiAware();
            double fps = 144;
            double seconds = 60;
            string outPath = null;
            string label = "frame";
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--fps" && i + 1 < args.Length)
                    fps = double.Parse(args[++i], CultureInfo.InvariantCulture);
                else if (args[i] == "--seconds" && i + 1 < args.Length)
                    seconds = double.Parse(args[++i], CultureInfo.InvariantCulture);
                else if (args[i] == "--out" && i + 1 < args.Length)
                    outPath = args[++i];
                else if (args[i] == "--label" && i + 1 < args.Length)
                    label = args[++i];
            }
            Application.EnableVisualStyles();
            Application.Run(new ProbeForm(fps, seconds, outPath, label));
            return 0;
        }
    }
}
