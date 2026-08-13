// @author bdth 2074055628@qq.com
// 文件用途 用 Raw Input 实测鼠标回报率 这是外设段唯一能在本机测出来的量
// 依据 125Hz 到 1000Hz 是 8 毫秒到 1 毫秒 这一步是真实收益 1000Hz 再往上到 8000Hz 不足 1 毫秒
// 且 8K 会吃掉单核百分之二三 CPU 在 CPU 吃紧的游戏里反而换来卡顿 所以本探针只判到 1000Hz 为止
// 鼠标静止时不上报 测量必须在持续移动中进行 取间隔中位数 天然排除掉中途停顿

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PaviseApp
{
    internal sealed class PollingResult
    {
        public bool Ok;
        public int Samples;
        public double MedianIntervalMs;
        public int Hz;
        public string Failure;
    }

    internal sealed class PollingRateProbe : IDisposable
    {
        internal const int MinSamples = 40;
        internal const double IdleGapMs = 40.0;

        private readonly List<long> stamps = new List<long>();
        private readonly object lk = new object();
        private Sink sink;
        private long freq;

        public bool Start()
        {
            lock (lk)
            {
                if (sink != null) return true;
                if (!QueryPerformanceFrequency(out freq) || freq <= 0) return false;
                stamps.Clear();

                sink = new Sink(this);
                var dev = new RAWINPUTDEVICE
                {
                    usUsagePage = GenericDesktopPage,
                    usUsage = MouseUsage,
                    dwFlags = RIDEV_INPUTSINK,
                    hwndTarget = sink.Handle
                };
                if (!RegisterRawInputDevices(new[] { dev }, 1, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE))))
                {
                    sink.DestroyHandle();
                    sink = null;
                    return false;
                }
                return true;
            }
        }

        private void OnInput()
        {
            long now;
            if (!QueryPerformanceCounter(out now)) return;
            lock (lk) { stamps.Add(now); }
        }

        public PollingResult Stop()
        {
            lock (lk)
            {
                if (sink != null)
                {
                    var dev = new RAWINPUTDEVICE
                    {
                        usUsagePage = GenericDesktopPage,
                        usUsage = MouseUsage,
                        dwFlags = RIDEV_REMOVE,
                        hwndTarget = IntPtr.Zero
                    };
                    try { RegisterRawInputDevices(new[] { dev }, 1, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE))); }
                    catch { }
                    try { sink.DestroyHandle(); } catch { }
                    sink = null;
                }

                var gaps = new List<double>();
                for (int i = 1; i < stamps.Count; i++)
                {
                    double ms = (stamps[i] - stamps[i - 1]) * 1000.0 / freq;
                    if (ms <= 0 || ms > IdleGapMs) continue;
                    gaps.Add(ms);
                }
                return Summarize(gaps);
            }
        }

        internal static PollingResult Summarize(List<double> gaps)
        {
            var result = new PollingResult { Samples = gaps == null ? 0 : gaps.Count };
            if (gaps == null || gaps.Count < MinSamples)
            {
                result.Failure = "有效样本只有 " + result.Samples + " 个 测量期间要持续画圈移动鼠标";
                return result;
            }
            var sorted = new List<double>(gaps);
            sorted.Sort();
            double median = sorted.Count % 2 == 1
                ? sorted[sorted.Count / 2]
                : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2.0;
            if (median <= 0)
            {
                result.Failure = "间隔中位数异常";
                return result;
            }
            result.Ok = true;
            result.MedianIntervalMs = median;
            result.Hz = SnapHz(1000.0 / median);
            return result;
        }

        internal static readonly int[] Ladder = { 125, 250, 500, 1000, 2000, 4000, 8000 };

        internal static int SnapHz(double raw)
        {
            if (raw <= 0) return 0;
            int best = Ladder[0];
            double bestErr = double.MaxValue;
            foreach (int step in Ladder)
            {
                double err = Math.Abs(raw - step) / step;
                if (err < bestErr) { bestErr = err; best = step; }
            }
            return bestErr > 0.30 ? (int)Math.Round(raw) : best;
        }

        internal const int WeakHz = 125;
        internal const int GoodHz = 1000;

        internal static bool IsWeak(int hz) { return hz > 0 && hz < 500; }

        public void Dispose()
        {
            try { Stop(); } catch { }
        }

        private sealed class Sink : NativeWindow
        {
            private readonly PollingRateProbe owner;

            public Sink(PollingRateProbe owner)
            {
                this.owner = owner;
                CreateHandle(new CreateParams());
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WM_INPUT) owner.OnInput();
                base.WndProc(ref m);
            }
        }

        private const int WM_INPUT = 0x00FF;
        private const ushort GenericDesktopPage = 0x01;
        private const ushort MouseUsage = 0x02;
        private const uint RIDEV_REMOVE = 0x00000001;
        private const uint RIDEV_INPUTSINK = 0x00000100;

        [StructLayout(LayoutKind.Sequential)]
        private struct RAWINPUTDEVICE
        {
            public ushort usUsagePage;
            public ushort usUsage;
            public uint dwFlags;
            public IntPtr hwndTarget;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);
        [DllImport("kernel32.dll")] private static extern bool QueryPerformanceCounter(out long value);
        [DllImport("kernel32.dll")] private static extern bool QueryPerformanceFrequency(out long value);
    }
}
