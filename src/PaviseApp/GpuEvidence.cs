using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;

namespace PaviseApp
{
    internal static class GpuEvidence
    {
        internal enum GpuEngineSampleState { Success, NoData, NativeFailure }

        internal sealed class GpuEngineSampleResult
        {
            internal readonly GpuEngineSampleState State;
            internal readonly RenderAdapter Adapter;
            internal readonly string Failure;
            internal GpuEngineSampleResult(GpuEngineSampleState state, RenderAdapter adapter = null, string failure = null)
            { State = state; Adapter = adapter; Failure = failure; }
        }

        internal const uint MaximumFormattedArrayBytes = 8u * 1024u * 1024u;

        private static GpuEngineSampleResult EngineFailure(string stage, uint status)
        { return new GpuEngineSampleResult(GpuEngineSampleState.NativeFailure, null, stage + " 0x" + status.ToString("X8", CultureInfo.InvariantCulture)); }

        internal sealed class GpuEngineSampler : IDisposable
        {
            private IntPtr query;

            private IntPtr counter;

            private bool primed;

            public bool IsOpen
            {
                get
                {
                    return query != IntPtr.Zero;
                }
            }

            public bool Open()
            {
                return Open(0);
            }

            public bool Open(int targetPid)
            { return OpenClassified(targetPid).State != GpuEngineSampleState.NativeFailure && IsOpen; }

            internal GpuEngineSampleResult OpenClassified(int targetPid)
            {
                Close();
                try
                {
                    uint status = PdhOpenQueryW(null, IntPtr.Zero, out query);
                    if (status != 0 || query == IntPtr.Zero)
                    {
                        Close();
                        return IsUnavailableObservation(status) ? new GpuEngineSampleResult(GpuEngineSampleState.NoData)
                            : EngineFailure("PdhOpenQuery", status);
                    }
                    string message = ((targetPid > 4) ? ("pid_" + targetPid.ToString(CultureInfo.InvariantCulture) + "_*engtype_3D") : "*engtype_3D");
                    status = PdhAddEnglishCounterW(query, "\\GPU Engine(" + message + ")\\Utilization Percentage", IntPtr.Zero, out counter);
                    if (status != 0 || counter == IntPtr.Zero)
                    {
                        Close();
                        return IsUnavailableObservation(status) ? new GpuEngineSampleResult(GpuEngineSampleState.NoData)
                            : EngineFailure("PdhAddEnglishCounter", status);
                    }
                    status = PdhCollectQueryData(query);
                    primed = status == 0;
                    if (primed) return new GpuEngineSampleResult(GpuEngineSampleState.Success);
                    if (IsUnavailableObservation(status)) return new GpuEngineSampleResult(GpuEngineSampleState.NoData);
                    Close();
                    return EngineFailure("PdhCollectQueryData.prime", status);
                }
                catch (Exception error)
                {
                    Close();
                    return new GpuEngineSampleResult(GpuEngineSampleState.NativeFailure, null, "GPU初始化 " + error.GetType().Name);
                }
            }

            public RenderAdapter Resolve(int pid)
            {
                return Resolve(pid, false);
            }

            public RenderAdapter Resolve(int pid, bool strict)
            { return ResolveClassified(pid, strict).Adapter; }

            internal GpuEngineSampleResult ResolveClassified(int pid, bool strict)
            {
                if (query == IntPtr.Zero || pid <= 0)
                {
                    return new GpuEngineSampleResult(GpuEngineSampleState.NoData);
                }
                try
                {
                    uint status = PdhCollectQueryData(query);
                    if (status != 0)
                    {
                        return IsUnavailableObservation(status) ? new GpuEngineSampleResult(GpuEngineSampleState.NoData)
                            : EngineFailure("PdhCollectQueryData.sample", status);
                    }
                    if (!primed)
                    {
                        primed = true;
                        return new GpuEngineSampleResult(GpuEngineSampleState.NoData);
                    }
                    return PickAdapterClassified(counter, pid, strict);
                }
                catch (Exception error)
                {
                    return new GpuEngineSampleResult(GpuEngineSampleState.NativeFailure, null, "GPU读取 " + error.GetType().Name);
                }
            }

            public void Close()
            {
                if (query != IntPtr.Zero)
                {
                    try
                    {
                        PdhCloseQuery(query);
                    }
                    catch
                    {
                    }
                    query = IntPtr.Zero;
                    counter = IntPtr.Zero;
                    primed = false;
                }
            }

            public void Dispose()
            {
                Close();
            }
        }

#pragma warning disable 0649
        private struct PdhFmtCounterValue
        {
            public uint CStatus;

            public double DoubleValue;
        }

        private struct PdhFmtCounterValueItem
        {
            public IntPtr Name;

            public PdhFmtCounterValue Value;
        }
#pragma warning restore 0649

        internal const double MinElectUtilization = 10.0;

        internal const int BurstRounds = 2;

        internal const int BurstIntervalMs = 700;

        private const uint PDH_FMT_DOUBLE = 512u;

        private const uint PDH_MORE_DATA = 2147485650u;

        private static bool IsUnavailableObservation(uint status)
        {
            // PDH_NO_DATA 也可能表示实例尚未出现；它与 PDH_CSTATUS_NO_INSTANCE 均不是 GPU 活动证据，也不算致命读取失败。

            return status == 0x800007D5u || status == 0x800007D1u;
        }

        public static Dictionary<int, double> Sample3D(int rounds, int intervalMs, Func<bool> canceled)
        {
            return SampleCore(rounds, intervalMs, canceled, false, 0, 0u, false, null);
        }

        internal static Dictionary<int, double> Sample3D(int rounds, int intervalMs, Func<bool> canceled, int targetPid, out GpuSampleDiagnostics diagnostics)
        {
            diagnostics = new GpuSampleDiagnostics
            {
                TargetPid = targetPid
            };
            return SampleCore(rounds, intervalMs, canceled, false, 0, 0u, false, diagnostics);
        }

        public static Dictionary<int, double> SampleAdapter3D(int luidHigh, uint luidLow, int rounds, int intervalMs, Func<bool> canceled)
        {
            return SampleCore(rounds, intervalMs, canceled, true, luidHigh, luidLow, true, null);
        }

        private static Dictionary<int, double> SampleCore(int rounds, int intervalMs, Func<bool> canceled, bool filterAdapter, int luidHigh, uint luidLow, bool sustained, GpuSampleDiagnostics diagnostics)
        {
            IntPtr query = IntPtr.Zero;
            try
            {
                if (canceled != null && canceled()) return null;
                uint pdhOpenQueryWResult = PdhOpenQueryW(null, IntPtr.Zero, out query);
                if (pdhOpenQueryWResult != 0 || query == IntPtr.Zero)
                {
                    if (diagnostics != null)
                    {
                        diagnostics.Failure("PdhOpenQuery", pdhOpenQueryWResult);
                    }
                    return null;
                }
                IntPtr counter;
                // 读取全部引擎再按类型筛选：部分驱动把 DX12 渲染队列记在 Graphics_1／Compute_* 上，只看 3D 引擎会把正在渲染的游戏读成 0%。
                pdhOpenQueryWResult = PdhAddEnglishCounterW(query, "\\GPU Engine(*)\\Utilization Percentage", IntPtr.Zero, out counter);
                if (pdhOpenQueryWResult != 0)
                {
                    if (diagnostics != null)
                    {
                        diagnostics.Failure("PdhAddEnglishCounter", pdhOpenQueryWResult);
                    }
                    return null;
                }
                pdhOpenQueryWResult = PdhCollectQueryData(query);
                if (IsUnavailableObservation(pdhOpenQueryWResult))
                {
                    if (diagnostics != null) diagnostics.Unavailable("PdhCollectQueryData.prime", pdhOpenQueryWResult);
                }
                else if (pdhOpenQueryWResult != 0)
                {
                    if (diagnostics != null)
                    {
                        diagnostics.Failure("PdhCollectQueryData.prime", pdhOpenQueryWResult);
                    }
                    return null;
                }
                Dictionary<int, double> dictionary = null;
                int processedCount = 0;
                bool failedRound = false;
                for (int i = 0; i < rounds; i++)
                {
                    if (canceled != null && canceled())
                    {
                        break;
                    }
                    Thread.Sleep(intervalMs);
                    if (canceled != null && canceled()) break;
                    pdhOpenQueryWResult = PdhCollectQueryData(query);
                    if (IsUnavailableObservation(pdhOpenQueryWResult))
                    {
                        if (diagnostics != null) diagnostics.Unavailable("PdhCollectQueryData.sample", pdhOpenQueryWResult);
                        if (dictionary == null || sustained) dictionary = new Dictionary<int, double>();
                        continue;
                    }
                    if (pdhOpenQueryWResult != 0)
                    {
                        failedRound = true;
                        if (diagnostics != null)
                        {
                            diagnostics.Failure("PdhCollectQueryData.sample", pdhOpenQueryWResult);
                        }
                        continue;
                    }
                    if (diagnostics != null)
                    {
                        diagnostics.CollectedRounds++;
                    }
                    Dictionary<int, double> dictionary2 = ReadByPid(counter, filterAdapter, luidHigh, luidLow, diagnostics);
                    if (dictionary2 == null)
                    {
                        failedRound = true;
                        continue;
                    }
                    processedCount++;
                    if (diagnostics != null)
                    {
                        diagnostics.ValidRounds++;
                    }
                    if (dictionary == null)
                    {
                        dictionary = dictionary2;
                        continue;
                    }
                    if (sustained)
                    {
                        Dictionary<int, double> dictionary3 = new Dictionary<int, double>();
                        foreach (KeyValuePair<int, double> item in dictionary)
                        {
                            double value;
                            if (dictionary2.TryGetValue(item.Key, out value))
                            {
                                dictionary3[item.Key] = ((value < item.Value) ? value : item.Value);
                            }
                        }
                        dictionary = dictionary3;
                        continue;
                    }
                    foreach (KeyValuePair<int, double> item2 in dictionary2)
                    {
                        double value2;
                        if (!dictionary.TryGetValue(item2.Key, out value2) || item2.Value > value2)
                        {
                            dictionary[item2.Key] = item2.Value;
                        }
                    }
                }
                if (sustained && processedCount < 2)
                {
                    return dictionary == null || failedRound ? null : new Dictionary<int, double>();
                }
                return dictionary;
            }
            catch (Exception ex)
            {
                if (diagnostics != null)
                {
                    diagnostics.ExceptionType = ex.GetType().Name;
                }
                return null;
            }
            finally
            {
                if (query != IntPtr.Zero)
                {
                    try
                    {
                        PdhCloseQuery(query);
                    }
                    catch
                    {
                    }
                }
            }
        }

        private static Dictionary<int, double> ReadByPid(IntPtr counter, bool filterAdapter, int luidHigh, uint luidLow, GpuSampleDiagnostics diagnostics)
        {
            uint bufferSize = 0u;
            uint itemCount = 0u;
            uint pdhGetFormattedCounterArrayWResult = PdhGetFormattedCounterArrayW(counter, 512u, ref bufferSize, ref itemCount, IntPtr.Zero);
            if (pdhGetFormattedCounterArrayWResult == 0 && itemCount == 0) return new Dictionary<int, double>();
            if (IsUnavailableObservation(pdhGetFormattedCounterArrayWResult))
            {
                if (diagnostics != null) diagnostics.Unavailable("PdhGetFormattedCounterArray.size", pdhGetFormattedCounterArrayWResult);
                return new Dictionary<int, double>();
            }
            if (pdhGetFormattedCounterArrayWResult != PDH_MORE_DATA)
            {
                if (diagnostics != null)
                {
                    diagnostics.Failure("PdhGetFormattedCounterArray.size", pdhGetFormattedCounterArrayWResult);
                }
                return null;
            }
            if (bufferSize == 0 || bufferSize > MaximumFormattedArrayBytes)
            {
                if (diagnostics != null) diagnostics.Failure("PdhGetFormattedCounterArray.invalid-size:" + bufferSize, pdhGetFormattedCounterArrayWResult);
                return null;
            }
            uint capacity = bufferSize;
            IntPtr intPtr = Marshal.AllocHGlobal((int)bufferSize);
            try
            {
                pdhGetFormattedCounterArrayWResult = PdhGetFormattedCounterArrayW(counter, 512u, ref bufferSize, ref itemCount, intPtr);
                if (IsUnavailableObservation(pdhGetFormattedCounterArrayWResult) || pdhGetFormattedCounterArrayWResult == PDH_MORE_DATA)
                {
                    if (diagnostics != null) diagnostics.Unavailable("PdhGetFormattedCounterArray.data", pdhGetFormattedCounterArrayWResult);
                    return new Dictionary<int, double>();
                }
                if (pdhGetFormattedCounterArrayWResult != 0)
                {
                    if (diagnostics != null)
                    {
                        diagnostics.Failure("PdhGetFormattedCounterArray.data", pdhGetFormattedCounterArrayWResult);
                    }
                    return null;
                }
                // 同一进程先按引擎类型累加（多适配器、多个同类引擎），再取最忙的类型，口径与任务管理器的进程 GPU 占用一致。
                Dictionary<int, Dictionary<string, double>> byEngine = new Dictionary<int, Dictionary<string, double>>();
                bool observed = itemCount == 0, failedInstance = false, failedValue = false;
                uint firstBadStatus = 0;
                int sizeOfResult = Marshal.SizeOf(typeof(PdhFmtCounterValueItem));
                if (bufferSize > capacity || itemCount > 0 && (bufferSize == 0 || (ulong)itemCount * (uint)sizeOfResult > bufferSize))
                {
                    if (diagnostics != null) diagnostics.Failure("PdhGetFormattedCounterArray.invalid-layout:bytes=" + bufferSize + ",count=" + itemCount, pdhGetFormattedCounterArrayWResult);
                    return null;
                }
                for (uint iterationIndex = 0u; iterationIndex < itemCount; iterationIndex++)
                {
                    PdhFmtCounterValueItem pdhFmtCounterValueItem = (PdhFmtCounterValueItem)Marshal.PtrToStructure(new IntPtr(intPtr.ToInt64() + iterationIndex * sizeOfResult), typeof(PdhFmtCounterValueItem));
                    string instanceName = Marshal.PtrToStringUni(pdhFmtCounterValueItem.Name);
                    string engineType = EngineType(instanceName);
                    // 视频编解码、复制、覆盖层等引擎不代表画面渲染，既不计入占用，也不影响本轮读取是否成功。
                    if (!IsRenderEngine(engineType))
                    {
                        continue;
                    }
                    if (IsUnavailableObservation(pdhFmtCounterValueItem.Value.CStatus))
                    {
                        if (diagnostics != null) diagnostics.Unavailable("counter.CStatus", pdhFmtCounterValueItem.Value.CStatus);
                        continue;
                    }
                    int pid = ParsePid(instanceName);
                    if (diagnostics != null)
                    {
                        diagnostics.Instances++;
                    }
                    if (diagnostics != null && pid > 0 && pid == diagnostics.TargetPid)
                    {
                        diagnostics.TargetInstances++;
                        if (pdhFmtCounterValueItem.Value.CStatus > 1)
                        {
                            diagnostics.TargetRejectedInstances++;
                            diagnostics.TargetStatus = pdhFmtCounterValueItem.Value.CStatus;
                        }
                    }
                    int luidHigh2;
                    uint luidLow2;
                    uint phys;
                    bool matchingAdapter = !filterAdapter || (ParseAdapter(instanceName, out luidHigh2, out luidLow2, out phys) && luidHigh2 == luidHigh && luidLow2 == luidLow);
                    if (pdhFmtCounterValueItem.Value.CStatus > 1)
                    {
                        if (pid > 0 && matchingAdapter)
                        { if (!failedInstance) firstBadStatus = pdhFmtCounterValueItem.Value.CStatus; failedInstance = true; }
                        if (diagnostics != null)
                        {
                            diagnostics.RejectedInstances++;
                        }
                    }
                    else if (pid <= 0)
                    {
                        if (diagnostics != null)
                        {
                            diagnostics.RejectedInstances++;
                        }
                    }
                    else if (matchingAdapter)
                    {
                        double doubleValue = pdhFmtCounterValueItem.Value.DoubleValue;
                        if (double.IsNaN(doubleValue) || double.IsInfinity(doubleValue))
                        { failedValue = true; continue; }
                        if (!(doubleValue < 0.0))
                        {
                            observed = true;
                            Dictionary<string, double> engines;
                            if (!byEngine.TryGetValue(pid, out engines))
                            {
                                engines = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                                byEngine[pid] = engines;
                            }
                            double value;
                            engines.TryGetValue(engineType, out value);
                            engines[engineType] = value + doubleValue;
                        }
                    }
                }
                if (failedInstance && !observed)
                {
                    if (diagnostics != null) diagnostics.Failure("counter.CStatus", firstBadStatus);
                    return null;
                }
                if (failedValue && !observed)
                {
                    if (diagnostics != null) diagnostics.Failure("counter.non-finite", 0);
                    return null;
                }
                Dictionary<int, double> dictionary = new Dictionary<int, double>();
                foreach (KeyValuePair<int, Dictionary<string, double>> process in byEngine)
                {
                    string busiestEngine = null;
                    double busiest = 0.0;
                    foreach (KeyValuePair<string, double> engine in process.Value)
                    {
                        if (busiestEngine == null || engine.Value > busiest)
                        {
                            busiestEngine = engine.Key;
                            busiest = engine.Value;
                        }
                    }
                    dictionary[process.Key] = busiest;
                    if (diagnostics != null && process.Key == diagnostics.TargetPid)
                    {
                        diagnostics.TargetEngine = busiestEngine;
                    }
                }
                return dictionary;
            }
            finally
            {
                Marshal.FreeHGlobal(intPtr);
            }
        }

        internal static string EngineType(string instanceName)
        {
            if (string.IsNullOrEmpty(instanceName))
            {
                return null;
            }
            int matchIndex = instanceName.IndexOf("_engtype_", StringComparison.OrdinalIgnoreCase);
            if (matchIndex < 0)
            {
                return null;
            }
            string engineType = instanceName.Substring(matchIndex + "_engtype_".Length);
            return engineType.Length == 0 ? null : engineType;
        }

        // 只认 3D、Graphics_*、Compute_*：这些是游戏提交画面与计算的队列；Cuda、视频、复制和安全引擎不算渲染证据。
        internal static bool IsRenderEngine(string engineType)
        {
            if (string.IsNullOrEmpty(engineType))
            {
                return false;
            }
            return string.Equals(engineType, "3D", StringComparison.OrdinalIgnoreCase)
                || engineType.StartsWith("Graphics", StringComparison.OrdinalIgnoreCase)
                || engineType.StartsWith("Compute", StringComparison.OrdinalIgnoreCase);
        }

        internal static int ParsePid(string instanceName)
        {
            if (string.IsNullOrEmpty(instanceName))
            {
                return 0;
            }
            if (!instanceName.StartsWith("pid_", StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }
            int matchIndex = instanceName.IndexOf('_', 4);
            if (matchIndex <= 4)
            {
                return 0;
            }
            int result;
            if (!int.TryParse(instanceName.Substring(4, matchIndex - 4), out result) || result <= 0)
            {
                return 0;
            }
            return result;
        }

        internal static bool ParseAdapter(string instanceName, out int luidHigh, out uint luidLow, out uint phys)
        {
            luidHigh = 0;
            luidLow = 0u;
            phys = 0u;
            if (string.IsNullOrEmpty(instanceName))
            {
                return false;
            }
            string toLowerInvariantResult = instanceName.ToLowerInvariant();
            int matchIndex = toLowerInvariantResult.IndexOf("_luid_", StringComparison.Ordinal);
            if (matchIndex < 0)
            {
                return false;
            }
            int matchIndexOffset = matchIndex + 6;
            int matchIndex2 = toLowerInvariantResult.IndexOf('_', matchIndexOffset);
            if (matchIndex2 <= matchIndexOffset)
            {
                return false;
            }
            int matchIndex3 = toLowerInvariantResult.IndexOf('_', matchIndex2 + 1);
            if (matchIndex3 <= matchIndex2 + 1)
            {
                return false;
            }
            if (!TryParseHex(toLowerInvariantResult.Substring(matchIndexOffset, matchIndex2 - matchIndexOffset), out luidLow))
            {
                return false;
            }
            luidHigh = (int)luidLow;
            uint value;
            if (!TryParseHex(toLowerInvariantResult.Substring(matchIndex2 + 1, matchIndex3 - matchIndex2 - 1), out value))
            {
                return false;
            }
            luidLow = value;
            int matchIndex4 = toLowerInvariantResult.IndexOf("_phys_", matchIndex3, StringComparison.Ordinal);
            if (matchIndex4 < 0)
            {
                return false;
            }
            int matchIndex4Offset = matchIndex4 + 6;
            int matchIndex5 = toLowerInvariantResult.IndexOf('_', matchIndex4Offset);
            if (matchIndex5 <= matchIndex4Offset)
            {
                return false;
            }
            uint result;
            if (!uint.TryParse(toLowerInvariantResult.Substring(matchIndex4Offset, matchIndex5 - matchIndex4Offset), out result))
            {
                return false;
            }
            phys = result;
            return true;
        }

        private static bool TryParseHex(string token, out uint value)
        {
            value = 0u;
            if (string.IsNullOrEmpty(token))
            {
                return false;
            }
            string substringResult = (token.StartsWith("0x", StringComparison.Ordinal) ? token.Substring(2) : token);
            if (substringResult.Length == 0 || substringResult.Length > 8)
            {
                return false;
            }
            uint numericValue = 0u;
            string message = substringResult;
            foreach (char c in message)
            {
                int remainingC;
                if (c >= '0' && c <= '9')
                {
                    remainingC = c - 48;
                }
                else
                {
                    if (c < 'a' || c > 'f')
                    {
                        return false;
                    }
                    remainingC = c - 97 + 10;
                }
                numericValue = (numericValue << 4) | (uint)remainingC;
            }
            value = numericValue;
            return true;
        }

        public static RenderAdapter ResolveRenderAdapter(int pid, int intervalMs)
        {
            if (pid <= 0)
            {
                return null;
            }
            IntPtr query = IntPtr.Zero;
            try
            {
                if (PdhOpenQueryW(null, IntPtr.Zero, out query) != 0 || query == IntPtr.Zero)
                {
                    return null;
                }
                IntPtr counter;
                if (PdhAddEnglishCounterW(query, "\\GPU Engine(*engtype_3D)\\Utilization Percentage", IntPtr.Zero, out counter) != 0)
                {
                    return null;
                }
                if (PdhCollectQueryData(query) != 0)
                {
                    return null;
                }
                Thread.Sleep(intervalMs);
                if (PdhCollectQueryData(query) != 0)
                {
                    return null;
                }
                return PickAdapter(counter, pid);
            }
            catch
            {
                return null;
            }
            finally
            {
                if (query != IntPtr.Zero)
                {
                    try
                    {
                        PdhCloseQuery(query);
                    }
                    catch
                    {
                    }
                }
            }
        }

        private static RenderAdapter PickAdapter(IntPtr counter, int pid, bool strict = false)
        { return PickAdapterClassified(counter, pid, strict).Adapter; }

        private static GpuEngineSampleResult PickAdapterClassified(IntPtr counter, int pid, bool strict)
        {
            // 实例表可能在两次调用间变化，只重取一次；分配和数组边界都以本次实际字节数核验。
            for (int attempt = 0; attempt < 2; attempt++)
            {
                uint bufferSize = 0u, itemCount = 0u;
                uint status = PdhGetFormattedCounterArrayW(counter, 512u, ref bufferSize, ref itemCount, IntPtr.Zero);
                if (IsUnavailableObservation(status) || status == 0 && itemCount == 0)
                    return new GpuEngineSampleResult(GpuEngineSampleState.NoData);
                if (status != PDH_MORE_DATA) return EngineFailure("PdhGetFormattedCounterArray.size", status);
                if (bufferSize == 0 || bufferSize > MaximumFormattedArrayBytes)
                    return new GpuEngineSampleResult(GpuEngineSampleState.NativeFailure, null, "GPU适配器数组长度无效：" + bufferSize);
                uint capacity = bufferSize;
                IntPtr intPtr = Marshal.AllocHGlobal((int)capacity);
                try
                {
                    status = PdhGetFormattedCounterArrayW(counter, 512u, ref bufferSize, ref itemCount, intPtr);
                    if (status == PDH_MORE_DATA) continue;
                    if (IsUnavailableObservation(status)) return new GpuEngineSampleResult(GpuEngineSampleState.NoData);
                    if (status != 0) return EngineFailure("PdhGetFormattedCounterArray.data", status);
                    int sizeOfResult = Marshal.SizeOf(typeof(PdhFmtCounterValueItem));
                    if (bufferSize > capacity || itemCount > 0 && (bufferSize == 0 || (ulong)itemCount * (uint)sizeOfResult > bufferSize))
                        return new GpuEngineSampleResult(GpuEngineSampleState.NativeFailure, null, "GPU适配器数组布局无效：bytes=" + bufferSize + ",count=" + itemCount);
                    if (itemCount == 0) return new GpuEngineSampleResult(GpuEngineSampleState.NoData);
                    Dictionary<long, RenderAdapter> dictionary = new Dictionary<long, RenderAdapter>();
                    bool failedInstance = false; uint firstBadStatus = 0;
                    bool failedValue = false;
                    for (uint iterationIndex = 0u; iterationIndex < itemCount; iterationIndex++)
                    {
                        PdhFmtCounterValueItem item = (PdhFmtCounterValueItem)Marshal.PtrToStructure(new IntPtr(intPtr.ToInt64() + (long)iterationIndex * sizeOfResult), typeof(PdhFmtCounterValueItem));
                        string instanceName = Marshal.PtrToStringUni(item.Name);
                        int luidHigh; uint luidLow, phys;
                        if (ParsePid(instanceName) != pid || !ParseAdapter(instanceName, out luidHigh, out luidLow, out phys)) continue;
                        if (IsUnavailableObservation(item.Value.CStatus)) continue;
                        if (item.Value.CStatus > 1)
                        { if (!failedInstance) firstBadStatus = item.Value.CStatus; failedInstance = true; continue; }
                        double utilValue = item.Value.DoubleValue;
                        if (double.IsNaN(utilValue) || double.IsInfinity(utilValue))
                        { failedValue = true; continue; }
                        if (utilValue < 0.0) utilValue = 0.0;
                        long key = ((long)luidHigh << 32) | luidLow;
                        RenderAdapter value;
                        if (!dictionary.TryGetValue(key, out value))
                        {
                            value = new RenderAdapter { LuidHigh = luidHigh, LuidLow = luidLow, PhysIndex = phys };
                            dictionary[key] = value;
                        }
                        value.Util += utilValue;
                        if (double.IsInfinity(value.Util)) return new GpuEngineSampleResult(GpuEngineSampleState.NativeFailure, null, "GPU适配器利用率合计溢出");
                    }
                    if (dictionary.Count == 0)
                    {
                        if (failedInstance) return EngineFailure("PdhGetFormattedCounterArray.item", firstBadStatus);
                        if (failedValue) return new GpuEngineSampleResult(GpuEngineSampleState.NativeFailure, null, "GPU适配器利用率不是有限值");
                        return new GpuEngineSampleResult(GpuEngineSampleState.NoData);
                    }
                    RenderAdapter first = null, second = null;
                    foreach (RenderAdapter value in dictionary.Values)
                    {
                        if (first == null || value.Util > first.Util) { second = first; first = value; }
                        else if (second == null || value.Util > second.Util) second = value;
                    }
                    RenderAdapter selected = strict ? UniqueActiveAdapter(dictionary.Values) : first;
                    if (!strict) selected.Ambiguous = second != null && second.Util >= 10.0;
                    return new GpuEngineSampleResult(selected != null && !selected.Ambiguous && selected.Util > 0
                        ? GpuEngineSampleState.Success : GpuEngineSampleState.NoData, selected);
                }
                finally { Marshal.FreeHGlobal(intPtr); }
            }
            return new GpuEngineSampleResult(GpuEngineSampleState.NoData);
        }

        internal static RenderAdapter UniqueActiveAdapter(IEnumerable<RenderAdapter> adapters)
        {
            RenderAdapter renderAdapter = null;
            foreach (RenderAdapter adapter in adapters)
            {
                if (adapter != null && !double.IsNaN(adapter.Util) && !double.IsInfinity(adapter.Util) && !(adapter.Util <= 0.0))
                {
                    if (renderAdapter != null)
                    {
                        RenderAdapter renderAdapter2 = new RenderAdapter();
                        renderAdapter2.Ambiguous = true;
                        return renderAdapter2;
                    }
                    renderAdapter = adapter;
                }
            }
            return renderAdapter;
        }

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern uint PdhOpenQueryW(string dataSource, IntPtr userData, out IntPtr query);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern uint PdhAddEnglishCounterW(IntPtr query, string counterPath, IntPtr userData, out IntPtr counter);

        [DllImport("pdh.dll")]
        private static extern uint PdhCollectQueryData(IntPtr query);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, ref uint itemCount, IntPtr buffer);

        [DllImport("pdh.dll")]
        private static extern uint PdhCloseQuery(IntPtr query);
    }
}
