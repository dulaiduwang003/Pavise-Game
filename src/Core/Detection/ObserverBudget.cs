// @author bdth 2074055628@qq.com
// 文件用途 观察层自预算 量化帧时钟与中断归因自身的开销 超预算就降级
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal sealed class ObserverCost
    {
        public double SelfCpuShare;
        public double SelfCpuMs;
        public long EventsLost;
        public long RealTimeBuffersLost;
        public long BuffersWritten;
        public double WindowMs;
        public bool QueryOk;

        public bool Healthy { get { return QueryOk && EventsLost == 0 && RealTimeBuffersLost == 0; } }
    }

    internal sealed class ObserverBudget
    {
        internal const double CpuShareLimit = 0.003;

        private readonly string[] sessions;
        private long lastSelfCpuTicks;
        private long lastSampleTicks;
        private int strikes;

        public ObserverBudget(params string[] sessionNames)
        {
            sessions = sessionNames ?? new string[0];
        }

        public ObserverCost Last { get; private set; }

        public bool ShouldDegrade { get { return strikes >= 3; } }

        public void Reset()
        {
            lastSelfCpuTicks = 0;
            lastSampleTicks = 0;
            strikes = 0;
            Last = null;
        }

        public ObserverCost Sample()
        {
            var cost = new ObserverCost();
            long now = DateTime.UtcNow.Ticks;
            long cpu = SelfCpuTicks();

            if (lastSampleTicks > 0 && now > lastSampleTicks && cpu >= lastSelfCpuTicks)
            {
                long dWall = now - lastSampleTicks;
                long dCpu = cpu - lastSelfCpuTicks;
                int cores = Environment.ProcessorCount;
                cost.WindowMs = dWall / (double)TimeSpan.TicksPerMillisecond;
                cost.SelfCpuMs = dCpu / (double)TimeSpan.TicksPerMillisecond;
                if (cores > 0 && dWall > 0) cost.SelfCpuShare = dCpu / (double)dWall / cores;
            }
            lastSampleTicks = now;
            lastSelfCpuTicks = cpu;

            foreach (string name in sessions)
            {
                long lost, rtLost, written;
                if (!QuerySession(name, out lost, out rtLost, out written)) continue;
                cost.QueryOk = true;
                cost.EventsLost += lost;
                cost.RealTimeBuffersLost += rtLost;
                cost.BuffersWritten += written;
            }

            bool over = cost.SelfCpuShare > CpuShareLimit
                || cost.EventsLost > 0 || cost.RealTimeBuffersLost > 0;
            if (over) strikes++; else strikes = 0;

            Last = cost;
            return cost;
        }

        private static long SelfCpuTicks()
        {
            try
            {
                IntPtr h = GetCurrentProcess();
                long c, e, k, u;
                if (!GetProcessTimes(h, out c, out e, out k, out u)) return 0;
                return k + u;
            }
            catch { return 0; }
        }

        private static bool QuerySession(string name, out long eventsLost, out long rtBuffersLost, out long buffersWritten)
        {
            eventsLost = 0; rtBuffersLost = 0; buffersWritten = 0;
            if (string.IsNullOrEmpty(name)) return false;
            int nameBytes = (name.Length + 1) * 2;
            int size = Marshal.SizeOf(typeof(Native.EventTraceProperties)) + nameBytes + 16;
            IntPtr props = Marshal.AllocHGlobal(size);
            try
            {
                for (int i = 0; i < size; i++) Marshal.WriteByte(props, i, 0);
                var p = new Native.EventTraceProperties();
                p.WnodeBufferSize = (uint)size;
                p.LoggerNameOffset = (uint)Marshal.SizeOf(typeof(Native.EventTraceProperties));
                Marshal.StructureToPtr(p, props, false);
                if (Native.ControlTrace(0, name, props, EventTraceControlQuery) != 0) return false;
                var read = (Native.EventTraceProperties)Marshal.PtrToStructure(
                    props, typeof(Native.EventTraceProperties));
                eventsLost = read.EventsLost;
                rtBuffersLost = read.RealTimeBuffersLost;
                buffersWritten = read.BuffersWritten;
                return true;
            }
            catch { return false; }
            finally { Marshal.FreeHGlobal(props); }
        }

        private const uint EventTraceControlQuery = 0;

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessTimes(IntPtr handle,
            out long creation, out long exit, out long kernel, out long user);

        public static string Describe(ObserverCost c)
        {
            if (c == null) return Lang.T("t.observerbudget.1");
            if (!c.QueryOk) return Lang.T("t.observerbudget.2");
            if (c.EventsLost > 0 || c.RealTimeBuffersLost > 0)
                return Lang.T("t.observerbudget.3") + c.EventsLost
                    + Lang.T("t.observerbudget.4") + c.RealTimeBuffersLost + Lang.T("t.observerbudget.5");
            if (c.SelfCpuShare > CpuShareLimit)
                return Lang.T("t.observerbudget.6") + (c.SelfCpuShare * 100).ToString("F2")
                    + Lang.T("t.observerbudget.7") + (CpuShareLimit * 100).ToString("F2") + "%";
            return Lang.T("t.observerbudget.8") + (c.SelfCpuShare * 100).ToString("F3") + "%";
        }
    }
}
