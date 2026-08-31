// @author bdth 2074055628@qq.com
// 文件用途 对局期间读取每个逻辑核的利用率差分 供同局平均负载记录使用
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace PaviseApp
{
    internal interface ICoreLoadSource : IDisposable
    {
        // 自上次读取或基线以来的平均值 null 表示没有有效区间
        Dictionary<int, double> Read();
    }

    // \Processor Information(*)\% Processor Time 是标准计数器 实例名是 组,核 格式
    //   单组机器 逻辑核号 = 实例里的核号 多组按组基址累加(前面各组的活动逻辑核数之和)
    //   "_Total" / "组,_Total" 是汇总行 一律跳过
    //   利用率是差分量 必须采两次 CollectQueryData 之间隔一小段 只有一次拿到的全是 0
    internal static class CoreLoadProbe
    {
        internal const int DefaultIntervalMs = 250;

        // 每观察一局只建一个常驻查询 Read 不睡眠也不建工作线程
        // 连续的计数器增量覆盖整个观测区间
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

        // 拿一次 per-core 利用率 键是全局逻辑核号 值是 0-100
        //   同步阻塞约 intervalMs 毫秒 拿不到就返回空字典 绝不抛
        //   保留给独立诊断测试 选核弹窗只读取已封存的同局记录
#if PAVISE_SELFTEST
        // 选核弹窗截图自测专用:注入一份确定的 per-core 负载 让弹窗铺满全负载区间(含 44% / 88%)
        //   供人工核对 ROG 发光观感 空则走真实 PDH 采集
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
                    // 同一实例只出现一次 但保险起见取最大 别被后来的 0 覆盖
                    double prev;
                    if (!result.TryGetValue(logical, out prev) || v > prev) result[logical] = v;
                }
                return result;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        // 把 组,核 实例名映射到全局逻辑核号 汇总行返回 -1
        //   groupBase[g] = 前面各组活动逻辑核数累加 单组时恒为 0
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
