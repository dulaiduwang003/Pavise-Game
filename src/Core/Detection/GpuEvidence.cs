// @author bdth 2074055628@qq.com
// 文件用途 GPU 3D 引擎占用的突发采样 为渲染进程选举提供硬证据

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace PaviseApp
{
    internal static class GpuEvidence
    {
        internal const double MinElectUtilization = 10.0;
        internal const double MinAutoAddUtilization = 20.0;

        internal const int BurstRounds = 2;
        internal const int BurstIntervalMs = 700;

        public static Dictionary<int, double> Sample3D(int rounds, int intervalMs, Func<bool> canceled)
        {
            IntPtr query = IntPtr.Zero;
            try
            {
                if (PdhOpenQueryW(null, IntPtr.Zero, out query) != 0 || query == IntPtr.Zero) return null;
                IntPtr counter;
                if (PdhAddEnglishCounterW(query, @"\GPU Engine(*engtype_3D)\Utilization Percentage",
                        IntPtr.Zero, out counter) != 0)
                    return null;
                if (PdhCollectQueryData(query) != 0) return null;
                Dictionary<int, double> best = null;
                for (int round = 0; round < rounds; round++)
                {
                    if (canceled != null && canceled()) break;
                    Thread.Sleep(intervalMs);
                    if (PdhCollectQueryData(query) != 0) continue;
                    Dictionary<int, double> current = ReadByPid(counter);
                    if (current == null) continue;
                    if (best == null) { best = current; continue; }
                    foreach (KeyValuePair<int, double> kv in current)
                    {
                        double prev;
                        if (!best.TryGetValue(kv.Key, out prev) || kv.Value > prev) best[kv.Key] = kv.Value;
                    }
                }
                return best;
            }
            catch { return null; }
            finally { if (query != IntPtr.Zero) { try { PdhCloseQuery(query); } catch { } } }
        }

        private static Dictionary<int, double> ReadByPid(IntPtr counter)
        {
            uint bufferSize = 0;
            uint itemCount = 0;
            uint status = PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE,
                ref bufferSize, ref itemCount, IntPtr.Zero);
            if (status != PDH_MORE_DATA || bufferSize == 0) return null;
            IntPtr buffer = Marshal.AllocHGlobal((int)bufferSize);
            try
            {
                status = PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE,
                    ref bufferSize, ref itemCount, buffer);
                if (status != 0) return null;
                var result = new Dictionary<int, double>();
                int itemSize = Marshal.SizeOf(typeof(PdhFmtCounterValueItem));
                for (uint i = 0; i < itemCount; i++)
                {
                    var item = (PdhFmtCounterValueItem)Marshal.PtrToStructure(
                        new IntPtr(buffer.ToInt64() + (long)i * itemSize), typeof(PdhFmtCounterValueItem));
                    if (item.Value.CStatus > 1) continue;
                    string name = Marshal.PtrToStringUni(item.Name);
                    int pid = ParsePid(name);
                    if (pid <= 0) continue;
                    double value = item.Value.DoubleValue;
                    if (value < 0) continue;
                    double sum;
                    result.TryGetValue(pid, out sum);
                    result[pid] = sum + value;
                }
                return result;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        internal static int ParsePid(string instanceName)
        {
            if (string.IsNullOrEmpty(instanceName)) return 0;
            if (!instanceName.StartsWith("pid_", StringComparison.OrdinalIgnoreCase)) return 0;
            int end = instanceName.IndexOf('_', 4);
            if (end <= 4) return 0;
            int pid;
            return int.TryParse(instanceName.Substring(4, end - 4), out pid) && pid > 0 ? pid : 0;
        }

        private const uint PDH_FMT_DOUBLE = 0x00000200;
        private const uint PDH_MORE_DATA = 0x800007D2;

        [StructLayout(LayoutKind.Sequential)]
        private struct PdhFmtCounterValue
        {
            public uint CStatus;
            public double DoubleValue;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PdhFmtCounterValueItem
        {
            public IntPtr Name;
            public PdhFmtCounterValue Value;
        }

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern uint PdhOpenQueryW(string dataSource, IntPtr userData, out IntPtr query);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern uint PdhAddEnglishCounterW(IntPtr query, string counterPath, IntPtr userData, out IntPtr counter);
        [DllImport("pdh.dll")]
        private static extern uint PdhCollectQueryData(IntPtr query);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format,
            ref uint bufferSize, ref uint itemCount, IntPtr buffer);
        [DllImport("pdh.dll")]
        private static extern uint PdhCloseQuery(IntPtr query);
    }
}
