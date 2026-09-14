// @author bdth 2074055628@qq.com
// File purpose Reads per-logical-core utilization deltas during a match, feeds the same-match average load record
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace PaviseApp
{
    internal interface ICoreLoadSource : IDisposable
    {
        // Average since the last read or the baseline, null means no valid interval
        Dictionary<int, double> Read();
    }

    // \Processor Information(*)\% Processor Time is a standard counter, instance names are in group,core format
    //   single-group machine: logical core id = core id in the instance, multi-group adds the group base, the sum of active logical cores of preceding groups
    //   _Total / group,_Total are summary rows, always skipped
    //   utilization is a delta, needs two CollectQueryData calls a short interval apart, a single one yields all zeros
    internal static class CoreLoadProbe
    {
        internal const int DefaultIntervalMs = 250;

        // One resident query per observed match, Read neither sleeps nor spawns a worker thread
        // consecutive counter deltas cover the whole observation interval
        internal static ICoreLoadSource OpenSource()
        {
            var source = new CounterSource();
            if (source.Open()) return source;
            source.Dispose();
            return null;
        }

        private sealed class CounterSource : ICoreLoadSource
        {
            private IntPtr query, counter;
            private bool baseline;
            internal bool Open()
            {
                try
                {
                    if (PdhOpenQueryW(null, IntPtr.Zero, out query) != 0 || query == IntPtr.Zero) return false;
                    if (PdhAddEnglishCounterW(query, @"\Processor Information(*)\% Processor Time",
                        IntPtr.Zero, out counter) != 0) return false;
                    baseline = PdhCollectQueryData(query) == 0;
                    return baseline;
                }
                catch { return false; }
            }
            public Dictionary<int, double> Read()
            {
                if (query == IntPtr.Zero) return null;
                try
                {
                    if (PdhCollectQueryData(query) != 0) { baseline = false; return null; }
                    bool valid = baseline;
                    baseline = true;
                    return valid ? ReadByCore(counter) : null;
                }
                catch { baseline = false; return null; }
            }
            public void Dispose()
            {
                IntPtr old = query;
                query = IntPtr.Zero;
                baseline = false;
                if (old != IntPtr.Zero) try { PdhCloseQuery(old); } catch { }
            }
        }

        // Grab per-core utilization once, key is the global logical core id, value is 0-100
        //   blocks synchronously for about intervalMs ms, returns an empty dictionary on failure, never throws
        //   reserved for standalone diagnostic tests, the core selection dialog only reads the sealed same-match record
#if PAVISE_SELFTEST
        // Core selection dialog screenshot self-test only, injects a deterministic per-core load so the dialog spans the full load range incl. 44% / 88%
        //   for manual checking of the ROG glow look, empty means real PDH sampling
        internal static Dictionary<int, double> Override;
#endif

        public static Dictionary<int, double> Sample(int intervalMs)
        {
#if PAVISE_SELFTEST
            if (Override != null) return new Dictionary<int, double>(Override);
#endif
            if (intervalMs < 50) intervalMs = DefaultIntervalMs;
            IntPtr query = IntPtr.Zero;
            try
            {
                if (PdhOpenQueryW(null, IntPtr.Zero, out query) != 0 || query == IntPtr.Zero)
                    return Empty();
                IntPtr counter;
                if (PdhAddEnglishCounterW(query, @"\Processor Information(*)\% Processor Time",
                        IntPtr.Zero, out counter) != 0)
                    return Empty();
                if (PdhCollectQueryData(query) != 0) return Empty();
                Thread.Sleep(intervalMs);
                if (PdhCollectQueryData(query) != 0) return Empty();
                Dictionary<int, double> map = ReadByCore(counter);
                return map ?? Empty();
            }
            catch { return Empty(); }
            finally { if (query != IntPtr.Zero) { try { PdhCloseQuery(query); } catch { } } }
        }

        private static Dictionary<int, double> Empty()
        {
            return new Dictionary<int, double>();
        }

        private static Dictionary<int, double> ReadByCore(IntPtr counter)
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
                int[] groupBase = GroupBaseOffsets();
                var result = new Dictionary<int, double>();
                int itemSize = Marshal.SizeOf(typeof(PdhFmtCounterValueItem));
                for (uint i = 0; i < itemCount; i++)
                {
                    var item = (PdhFmtCounterValueItem)Marshal.PtrToStructure(
                        new IntPtr(buffer.ToInt64() + (long)i * itemSize), typeof(PdhFmtCounterValueItem));
                    if (item.Value.CStatus > 1) continue;
                    string name = Marshal.PtrToStringUni(item.Name);
                    int logical = ParseLogical(name, groupBase);
                    if (logical < 0) continue;
                    double v = item.Value.DoubleValue;
                    if (double.IsNaN(v) || double.IsInfinity(v)) continue;
                    if (v < 0) v = 0; else if (v > 100) v = 100;
                    // Each instance appears only once, but take the max to be safe, do not let a later 0 overwrite
                    double prev;
                    if (!result.TryGetValue(logical, out prev) || v > prev) result[logical] = v;
                }
                return result;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        // Map a group,core instance name to the global logical core id, summary rows return -1
        //   groupBase[g] = cumulative active logical cores of preceding groups, always 0 with a single group
        internal static int ParseLogical(string instanceName, int[] groupBase)
        {
            if (string.IsNullOrEmpty(instanceName)) return -1;
            if (instanceName.IndexOf("_Total", StringComparison.OrdinalIgnoreCase) >= 0) return -1;
            int comma = instanceName.IndexOf(',');
            if (comma <= 0 || comma >= instanceName.Length - 1) return -1;
            int group, core;
            if (!int.TryParse(instanceName.Substring(0, comma).Trim(), out group)) return -1;
            if (!int.TryParse(instanceName.Substring(comma + 1).Trim(), out core)) return -1;
            if (group < 0 || core < 0) return -1;
            int baseOff = group >= 0 && group < groupBase.Length ? groupBase[group] : group * 64;
            long logical = (long)baseOff + core;
            return logical >= 0 && logical < 64 ? (int)logical : -1;
        }

        private static int[] GroupBaseOffsets()
        {
            try
            {
                ushort groups = GetActiveProcessorGroupCount();
                if (groups <= 1) return new int[] { 0 };
                var baseOff = new int[groups];
                int acc = 0;
                for (ushort g = 0; g < groups; g++)
                {
                    baseOff[g] = acc;
                    acc += (int)GetActiveProcessorCount(g);
                }
                return baseOff;
            }
            catch { return new int[] { 0 }; }
        }

        [DllImport("kernel32.dll")]
        private static extern ushort GetActiveProcessorGroupCount();
        [DllImport("kernel32.dll")]
        private static extern uint GetActiveProcessorCount(ushort groupNumber);

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
