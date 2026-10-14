// 作者：bdth 2074055628@qq.com
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;

namespace PaviseApp
{
    internal sealed class GpuSampleDiagnostics
    {
        internal string FailureStage;
        internal uint FailureStatus;
        internal string ExceptionType;
        internal int CollectedRounds, ValidRounds, Instances, RejectedInstances;
        internal int TargetPid, TargetInstances, TargetRejectedInstances;
        internal uint TargetStatus;

        internal void Failure(string stage, uint status)
        {
            FailureStage = stage;
            FailureStatus = status;
        }

        public override string ToString()
        {
            return string.Format(CultureInfo.InvariantCulture,
                "lastFailure={0} code=0x{1:X8} collected={2} valid={3} instances={4} rejected={5}{6} targetInstances={7} targetRejected={8} targetCode=0x{9:X8}",
                FailureStage ?? "none", FailureStatus, CollectedRounds, ValidRounds,
                Instances, RejectedInstances, ExceptionType == null ? "" : " exception=" + ExceptionType,
                TargetInstances, TargetRejectedInstances, TargetStatus);
        }
    }

    internal sealed class RenderAdapter
    {
        public int LuidHigh;
        public uint LuidLow;
        public uint PhysIndex;
        public double Util;
        public bool Ambiguous;
    }

    internal static class GpuEvidence
    {
        internal const double MinElectUtilization = 10.0;

        internal const int BurstRounds = 2;
        internal const int BurstIntervalMs = 700;

        public static Dictionary<int, double> Sample3D(int rounds, int intervalMs, Func<bool> canceled)
        {
            return SampleCore(rounds, intervalMs, canceled, false, 0, 0, false, null);
        }

        internal static Dictionary<int, double> Sample3D(int rounds, int intervalMs, Func<bool> canceled,
            int targetPid, out GpuSampleDiagnostics diagnostics)
        {
            diagnostics = new GpuSampleDiagnostics { TargetPid = targetPid };
            return SampleCore(rounds, intervalMs, canceled, false, 0, 0, false, diagnostics);
        }

        // 持续负载取多轮最小值，避免瞬时尖峰触发压制。
        public static Dictionary<int, double> SampleAdapter3D(int luidHigh, uint luidLow,
            int rounds, int intervalMs, Func<bool> canceled)
        {
            return SampleCore(rounds, intervalMs, canceled, true, luidHigh, luidLow, true, null);
        }

        private static Dictionary<int, double> SampleCore(int rounds, int intervalMs, Func<bool> canceled,
            bool filterAdapter, int luidHigh, uint luidLow, bool sustained, GpuSampleDiagnostics diagnostics)
        {
            IntPtr query = IntPtr.Zero;
            try
            {
                uint status = PdhOpenQueryW(null, IntPtr.Zero, out query);
                if (status != 0 || query == IntPtr.Zero)
                {
                    if (diagnostics != null) diagnostics.Failure("PdhOpenQuery", status);
                    return null;
                }
                IntPtr counter;
                status = PdhAddEnglishCounterW(query, @"\GPU Engine(*engtype_3D)\Utilization Percentage",
                    IntPtr.Zero, out counter);
                if (status != 0)
                {
                    if (diagnostics != null) diagnostics.Failure("PdhAddEnglishCounter", status);
                    return null;
                }
                status = PdhCollectQueryData(query);
                if (status != 0)
                {
                    if (diagnostics != null) diagnostics.Failure("PdhCollectQueryData.prime", status);
                    return null;
                }
                Dictionary<int, double> best = null;
                int contributed = 0;
                for (int round = 0; round < rounds; round++)
                {
                    if (canceled != null && canceled()) break;
                    Thread.Sleep(intervalMs);
                    status = PdhCollectQueryData(query);
                    if (status != 0)
                    {
                        if (diagnostics != null) diagnostics.Failure("PdhCollectQueryData.sample", status);
                        continue;
                    }
                    if (diagnostics != null) diagnostics.CollectedRounds++;
                    Dictionary<int, double> current = ReadByPid(counter, filterAdapter, luidHigh, luidLow, diagnostics);
                    if (current == null) continue;
                    contributed++;
                    if (diagnostics != null) diagnostics.ValidRounds++;
                    if (best == null) { best = current; continue; }
                    if (sustained)
                    {
                        var kept = new Dictionary<int, double>();
                        foreach (KeyValuePair<int, double> kv in best)
                        {
                            double now;
                            if (current.TryGetValue(kv.Key, out now))
                                kept[kv.Key] = now < kv.Value ? now : kv.Value;
                        }
                        best = kept;
                        continue;
                    }
                    foreach (KeyValuePair<int, double> kv in current)
                    {
                        double prev;
                        if (!best.TryGetValue(kv.Key, out prev) || kv.Value > prev) best[kv.Key] = kv.Value;
                    }
                }
                // 持续负载判断至少需要两轮有效样本。
                if (sustained && contributed < 2) return null;
                return best;
            }
            catch (Exception error)
            {
                if (diagnostics != null) diagnostics.ExceptionType = error.GetType().Name;
                return null;
            }
            finally { if (query != IntPtr.Zero) { try { PdhCloseQuery(query); } catch { } } }
        }

        private static Dictionary<int, double> ReadByPid(IntPtr counter,
            bool filterAdapter, int luidHigh, uint luidLow, GpuSampleDiagnostics diagnostics)
        {
            uint bufferSize = 0;
            uint itemCount = 0;
            uint status = PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE,
                ref bufferSize, ref itemCount, IntPtr.Zero);
            if (status != PDH_MORE_DATA || bufferSize == 0)
            {
                if (diagnostics != null) diagnostics.Failure("PdhGetFormattedCounterArray.size", status);
                return null;
            }
            IntPtr buffer = Marshal.AllocHGlobal((int)bufferSize);
            try
            {
                status = PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE,
                    ref bufferSize, ref itemCount, buffer);
                if (status != 0)
                {
                    if (diagnostics != null) diagnostics.Failure("PdhGetFormattedCounterArray.data", status);
                    return null;
                }
                var result = new Dictionary<int, double>();
                int itemSize = Marshal.SizeOf(typeof(PdhFmtCounterValueItem));
                for (uint i = 0; i < itemCount; i++)
                {
                    var item = (PdhFmtCounterValueItem)Marshal.PtrToStructure(
                        new IntPtr(buffer.ToInt64() + (long)i * itemSize), typeof(PdhFmtCounterValueItem));
                    if (diagnostics == null && item.Value.CStatus > 1) continue;
                    string name = Marshal.PtrToStringUni(item.Name);
                    int pid = ParsePid(name);
                    if (diagnostics != null) diagnostics.Instances++;
                    if (diagnostics != null && pid > 0 && pid == diagnostics.TargetPid)
                    {
                        diagnostics.TargetInstances++;
                        if (item.Value.CStatus > 1)
                        {
                            diagnostics.TargetRejectedInstances++;
                            diagnostics.TargetStatus = item.Value.CStatus;
                        }
                    }
                    if (item.Value.CStatus > 1)
                    {
                        if (diagnostics != null)
                        {
                            diagnostics.RejectedInstances++;
                            diagnostics.Failure("counter.CStatus", item.Value.CStatus);
                        }
                        continue;
                    }
                    if (pid <= 0)
                    {
                        if (diagnostics != null) diagnostics.RejectedInstances++;
                        continue;
                    }
                    if (filterAdapter)
                    {
                        int hi; uint lo, phys;
                        if (!ParseAdapter(name, out hi, out lo, out phys)
                            || hi != luidHigh || lo != luidLow) continue;
                    }
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

        // 计数器实例中的 LUID 为高位、低位顺序，phys 为物理适配器索引。
        internal static bool ParseAdapter(string instanceName, out int luidHigh, out uint luidLow, out uint phys)
        {
            luidHigh = 0; luidLow = 0; phys = 0;
            if (string.IsNullOrEmpty(instanceName)) return false;
            string s = instanceName.ToLowerInvariant();
            int at = s.IndexOf("_luid_", StringComparison.Ordinal);
            if (at < 0) return false;
            int hiStart = at + 6;
            int hiEnd = s.IndexOf('_', hiStart);
            if (hiEnd <= hiStart) return false;
            int loEnd = s.IndexOf('_', hiEnd + 1);
            if (loEnd <= hiEnd + 1) return false;
            if (!TryParseHex(s.Substring(hiStart, hiEnd - hiStart), out luidLow)) return false;
            luidHigh = unchecked((int)luidLow);
            uint low;
            if (!TryParseHex(s.Substring(hiEnd + 1, loEnd - hiEnd - 1), out low)) return false;
            luidLow = low;
            int pat = s.IndexOf("_phys_", loEnd, StringComparison.Ordinal);
            if (pat < 0) return false;
            int pStart = pat + 6;
            int pEnd = s.IndexOf('_', pStart);
            if (pEnd <= pStart) return false;
            uint physIndex;
            if (!uint.TryParse(s.Substring(pStart, pEnd - pStart), out physIndex)) return false;
            phys = physIndex;
            return true;
        }

        private static bool TryParseHex(string token, out uint value)
        {
            value = 0;
            if (string.IsNullOrEmpty(token)) return false;
            string t = token.StartsWith("0x", StringComparison.Ordinal) ? token.Substring(2) : token;
            if (t.Length == 0 || t.Length > 8) return false;
            uint acc = 0;
            foreach (char c in t)
            {
                int d;
                if (c >= '0' && c <= '9') d = c - '0';
                else if (c >= 'a' && c <= 'f') d = c - 'a' + 10;
                else return false;
                acc = (acc << 4) | (uint)d;
            }
            value = acc;
            return true;
        }

        // 同一进程同时使用多张 GPU 时不猜测渲染归属。
        public static RenderAdapter ResolveRenderAdapter(int pid, int intervalMs)
        {
            if (pid <= 0) return null;
            IntPtr query = IntPtr.Zero;
            try
            {
                if (PdhOpenQueryW(null, IntPtr.Zero, out query) != 0 || query == IntPtr.Zero) return null;
                IntPtr counter;
                if (PdhAddEnglishCounterW(query, @"\GPU Engine(*engtype_3D)\Utilization Percentage",
                        IntPtr.Zero, out counter) != 0)
                    return null;
                if (PdhCollectQueryData(query) != 0) return null;
                Thread.Sleep(intervalMs);
                if (PdhCollectQueryData(query) != 0) return null;
                return PickAdapter(counter, pid);
            }
            catch { return null; }
            finally { if (query != IntPtr.Zero) { try { PdhCloseQuery(query); } catch { } } }
        }

        private static RenderAdapter PickAdapter(IntPtr counter, int pid, bool strict = false)
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
                var byLuid = new Dictionary<long, RenderAdapter>();
                int itemSize = Marshal.SizeOf(typeof(PdhFmtCounterValueItem));
                for (uint i = 0; i < itemCount; i++)
                {
                    var item = (PdhFmtCounterValueItem)Marshal.PtrToStructure(
                        new IntPtr(buffer.ToInt64() + (long)i * itemSize), typeof(PdhFmtCounterValueItem));
                    if (item.Value.CStatus > 1) continue;
                    string name = Marshal.PtrToStringUni(item.Name);
                    if (ParsePid(name) != pid) continue;
                    int hi; uint lo, phys;
                    if (!ParseAdapter(name, out hi, out lo, out phys)) continue;
                    double value = item.Value.DoubleValue;
                    if (double.IsNaN(value) || double.IsInfinity(value)) continue;
                    if (value < 0) value = 0;
                    long key = ((long)hi << 32) | lo;
                    RenderAdapter slot;
                    if (!byLuid.TryGetValue(key, out slot))
                    {
                        slot = new RenderAdapter { LuidHigh = hi, LuidLow = lo, PhysIndex = phys };
                        byLuid[key] = slot;
                    }
                    slot.Util += value;
                }
                if (byLuid.Count == 0) return null;
                RenderAdapter best = null, second = null;
                foreach (RenderAdapter a in byLuid.Values)
                {
                    if (best == null || a.Util > best.Util) { second = best; best = a; }
                    else if (second == null || a.Util > second.Util) second = a;
                }
                if (strict) return UniqueActiveAdapter(byLuid.Values);
                best.Ambiguous = second != null && second.Util >= MinElectUtilization;
                return best;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        internal static RenderAdapter UniqueActiveAdapter(IEnumerable<RenderAdapter> adapters)
        {
            RenderAdapter found = null;
            foreach (RenderAdapter adapter in adapters)
            {
                if (adapter == null || double.IsNaN(adapter.Util) || double.IsInfinity(adapter.Util) || adapter.Util <= 0) continue;
                if (found != null) return new RenderAdapter { Ambiguous = true };
                found = adapter;
            }
            return found;
        }

        // 对局内复用查询，以两次采集间隔计算占用率。
        internal sealed class GpuEngineSampler : IDisposable
        {
            private IntPtr query;
            private IntPtr counter;
            private bool primed;

            public bool IsOpen { get { return query != IntPtr.Zero; } }

            public bool Open() { return Open(0); }
            public bool Open(int targetPid)
            {
                Close();
                try
                {
                    if (PdhOpenQueryW(null, IntPtr.Zero, out query) != 0 || query == IntPtr.Zero)
                    {
                        query = IntPtr.Zero;
                        return false;
                    }
                    string instance = targetPid > 4 ? "pid_" + targetPid.ToString(CultureInfo.InvariantCulture) + "_*engtype_3D" : "*engtype_3D";
                    if (PdhAddEnglishCounterW(query, @"\GPU Engine(" + instance + @")\Utilization Percentage",
                            IntPtr.Zero, out counter) != 0)
                    {
                        Close();
                        return false;
                    }
                    primed = PdhCollectQueryData(query) == 0;
                    return true;
                }
                catch { Close(); return false; }
            }

            // 首次采集仅建立基线。
            public RenderAdapter Resolve(int pid) { return Resolve(pid, false); }
            public RenderAdapter Resolve(int pid, bool strict)
            {
                if (query == IntPtr.Zero || pid <= 0) return null;
                try
                {
                    if (PdhCollectQueryData(query) != 0) return null;
                    if (!primed) { primed = true; return null; }
                    return PickAdapter(counter, pid, strict);
                }
                catch { return null; }
            }

            public void Close()
            {
                if (query == IntPtr.Zero) return;
                try { PdhCloseQuery(query); } catch { }
                query = IntPtr.Zero;
                counter = IntPtr.Zero;
                primed = false;
            }

            public void Dispose() { Close(); }
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
