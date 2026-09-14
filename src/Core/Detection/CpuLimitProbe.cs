// @author bdth 2074055628@qq.com
// File purpose Samples the CPU performance limit counters once per second during a match, so hitting a power or thermal wall can be called out on the spot
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace PaviseApp
{
    internal sealed class CpuLimitProbe
    {
        private const string LimitCounter = @"\Processor Information(_Total)\% Performance Limit";
        private const string FlagsCounter = @"\Processor Information(_Total)\Performance Limit Flags";
        private const double LimitedBelow = 95.0;
        private const double WarnBelow = 80.0;
        private const int WarnHoldSeconds = 10;

        private Thread worker;
        private volatile bool running;
        private readonly object gate = new object();

        private int samples;
        private int limitedSeconds;
        private double minLimit = 100.0;
        private long flagsUnion;
        private long flagsAtMin;

        public void Start()
        {
            lock (gate)
            {
                if (running) return;
                running = true;
                samples = 0; limitedSeconds = 0; minLimit = 100.0; flagsUnion = 0; flagsAtMin = 0;
                worker = new Thread(Loop);
                worker.IsBackground = true;
                worker.Name = "Pavise.CpuLimit";
                worker.Priority = ThreadPriority.BelowNormal;
                worker.Start();
            }
        }

        public void Stop()
        {
            Thread t;
            lock (gate)
            {
                if (!running) return;
                running = false;
                t = worker; worker = null;
            }
            if (t != null) { try { t.Join(2500); } catch { } }
            if (samples <= 0) return;
            if (limitedSeconds > 0)
                Logger.Log(Lang.T("log.cpulimit.1") + limitedSeconds + Lang.T("log.cpulimit.2")
                    + samples + Lang.T("log.cpulimit.3") + minLimit.ToString("F0")
                    + Lang.T("log.cpulimit.4") + DescribeFlags(flagsAtMin)
                    + (flagsUnion != flagsAtMin ? Lang.T("log.cpulimit.5") + DescribeFlags(flagsUnion) : ""));
            else
                Logger.Log(Lang.T("log.cpulimit.6") + samples + Lang.T("log.cpulimit.7"));
        }

        // Windows Performance Limit Flags bit definitions: 0x1 thermal, 0x2 power, 0x4 domain dependency; other bits are shown raw in hex
        internal static string DescribeFlags(long flags)
        {
            string text = "0x" + flags.ToString("X");
            var names = new List<string>();
            if ((flags & 0x1) != 0) names.Add(Lang.T("t.cpulimit.thermal"));
            if ((flags & 0x2) != 0) names.Add(Lang.T("t.cpulimit.power"));
            if ((flags & 0x4) != 0) names.Add(Lang.T("t.cpulimit.domain"));
            return names.Count == 0 ? text : text + " " + string.Join(" ", names.ToArray());
        }

        private void Loop()
        {
            IntPtr query = IntPtr.Zero;
            try
            {
                if (PdhOpenQueryW(null, IntPtr.Zero, out query) != 0 || query == IntPtr.Zero) return;
                IntPtr cLimit, cFlags;
                if (PdhAddEnglishCounterW(query, LimitCounter, IntPtr.Zero, out cLimit) != 0) return;
                bool hasFlags = PdhAddEnglishCounterW(query, FlagsCounter, IntPtr.Zero, out cFlags) == 0;
                if (PdhCollectQueryData(query) != 0) return;
                int warnStreak = 0;
                bool warned = false;
                while (running)
                {
                    Thread.Sleep(1000);
                    if (!running) break;
                    if (PdhCollectQueryData(query) != 0) continue;
                    double limit;
                    if (!ReadDouble(cLimit, out limit)) continue;
                    long flags = 0;
                    if (hasFlags)
                    {
                        double f;
                        if (ReadDouble(cFlags, out f)) flags = (long)f;
                    }
                    samples++;
                    flagsUnion |= flags;
                    if (limit < LimitedBelow) limitedSeconds++;
                    if (limit < minLimit) { minLimit = limit; flagsAtMin = flags; }
                    if (limit < WarnBelow) warnStreak++; else warnStreak = 0;
                    if (!warned && warnStreak >= WarnHoldSeconds)
                    {
                        warned = true;
                        Logger.Log(Lang.T("log.cpulimit.8") + limit.ToString("F0")
                            + Lang.T("log.cpulimit.9") + DescribeFlags(flags)
                            + Lang.T("log.cpulimit.10"));
                    }
                }
            }
            catch { }
            finally { if (query != IntPtr.Zero) { try { PdhCloseQuery(query); } catch { } } }
        }

        private static bool ReadDouble(IntPtr counter, out double value)
        {
            value = 0;
            var fmt = new PdhFmtCounterValue();
            uint type;
            if (PdhGetFormattedCounterValue(counter, PDH_FMT_DOUBLE, out type, out fmt) != 0) return false;
            if (fmt.CStatus != 0) return false;
            value = fmt.DoubleValue;
            return true;
        }

        private const uint PDH_FMT_DOUBLE = 0x00000200;

        [StructLayout(LayoutKind.Sequential)]
        private struct PdhFmtCounterValue
        {
            public uint CStatus;
            public double DoubleValue;
        }

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern uint PdhOpenQueryW(string dataSource, IntPtr userData, out IntPtr query);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern uint PdhAddEnglishCounterW(IntPtr query, string counterPath, IntPtr userData, out IntPtr counter);
        [DllImport("pdh.dll")]
        private static extern uint PdhCollectQueryData(IntPtr query);
        [DllImport("pdh.dll")]
        private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out PdhFmtCounterValue value);
        [DllImport("pdh.dll")]
        private static extern uint PdhCloseQuery(IntPtr query);
    }
}
