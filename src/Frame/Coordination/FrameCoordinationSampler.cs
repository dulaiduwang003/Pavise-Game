using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;

namespace PaviseApp
{
    internal static class FrameCoordinationSampler
    {
        internal struct Property
        {
            internal ulong Name;

            internal uint Index;

            internal uint Reserved;
        }

        internal sealed class Evidence
        {
            internal FrameCoordinationSample Sample;

            internal long Begin;

            internal long End;

            internal long Half;

            internal int Count;

            internal int RelevantCount;

            internal int SchedulerSwitches;

            internal bool PresenterOnly;

            internal bool FixedThreadSet;
            internal bool WorkloadObservation;
            internal double CallerCpuBeforeEvents = double.NaN;

            internal volatile bool Invalid;

            internal FrameCaptureIssue Fault;
            internal string FaultDetail;
#if PAVISE_SELFTEST
            internal Func<double> CpuMsForTest;
#endif

            internal IntPtr FlagsName;

            internal IntPtr DxgiStreamName, D3d9StreamName;

            // 只在消费者退出后调用 释放回调用到的属性名 并告知原生缓冲消费者已经返回
            internal void ReleasePropertyNames()
            {
                IntPtr flags = Interlocked.Exchange(ref FlagsName, IntPtr.Zero);
                IntPtr dxgi = Interlocked.Exchange(ref DxgiStreamName, IntPtr.Zero);
                IntPtr d3d9 = Interlocked.Exchange(ref D3d9StreamName, IntPtr.Zero);
                if (flags != IntPtr.Zero) Marshal.FreeHGlobal(flags);
                if (dxgi != IntPtr.Zero) Marshal.FreeHGlobal(dxgi);
                if (d3d9 != IntPtr.Zero) Marshal.FreeHGlobal(d3d9);
                NativeEtwCapture native = NativeCapture;
                if (native != null) native.ConsumerReturned();
            }

            // 完整采集的原生缓冲 消费者返回且采集方用完后才释放
            internal NativeEtwCapture NativeCapture;

            internal void ReleaseNative()
            {
                NativeEtwCapture native = NativeCapture;
                if (native != null) native.Release();
            }

            internal bool NativeFaulted
            {
                get { NativeEtwCapture native = NativeCapture; return native != null && native.Fault != NativeEtw.FaultNone; }
            }

            // 完整采集时系统直接调用原生回调 只有测试替换了 ProcessTrace 或 Start 时才经这里把事件交给原生
            internal void ReceiveNative(ref Native.EventRecord r)
            {
                NativeEtwCapture native = NativeCapture;
                if (native != null) native.Deliver(ref r);
            }

            // 测试台架在首个事件前预置已送达数 完整采集时原生计数同步预置
            internal void SeedCountForTest(int count)
            {
                Count = count;
                NativeEtwCapture native = NativeCapture;
                if (native != null && !native.SeedCountForTest(count)) throw new InvalidOperationException("原生计数只能在首个事件前预置");
            }

            [UnmanagedFunctionPointer(CallingConvention.Winapi)]
            internal delegate uint NativePropertyReader(ref Native.EventRecord record, ref Property property, uint bytes, IntPtr data);

            private readonly byte[] nativePropertyBuffer = new byte[8];

            // 原生回调经这里调用 TDH 和托管处理用同一个读取入口 在消费线程上调用 异常不能穿过原生栈帧
            internal uint ReadNativeProperty(ref Native.EventRecord record, ref Property property, uint bytes, IntPtr data)
            {
                try
                {
                    if (bytes > nativePropertyBuffer.Length || data == IntPtr.Zero) return 87;
                    uint status = TdhGetProperty(ref record, 0u, IntPtr.Zero, 1u, ref property, bytes, nativePropertyBuffer);
                    Marshal.Copy(nativePropertyBuffer, 0, data, (int)bytes);
                    return status;
                }
                catch (OutOfMemoryException) { return ReaderOutOfMemory; }
                catch (Exception) { return ReaderFault; }
            }

            // 读取抛出异常时交给原生的读取码 回放读到它时按托管处理遇到同一异常的方式弃样
            // 系统 TDH 只返回 Win32 错误码 不会与这两个值重合
            internal const uint ReaderFault = 0xE0434352u, ReaderOutOfMemory = 0xE0434353u;

            private sealed class ReaderFaultException : Exception { }

            internal ThreadRelationCollector Relation;

            private readonly byte[] flagBuffer = new byte[4];

            private readonly byte[] streamBuffer = new byte[8];
            private readonly Dictionary<int, ulong> pendingStreams = new Dictionary<int, ulong>();
            private sealed class PresentStreamEvidence
            {
                internal readonly Dictionary<int, int>[] Counts = new Dictionary<int, int>[] { new Dictionary<int, int>(), new Dictionary<int, int>() };
                internal int Successes;
            }
            private readonly Dictionary<ulong, PresentStreamEvidence> dxgiPresentStreams = new Dictionary<ulong, PresentStreamEvidence>(),
                d3d9PresentStreams = new Dictionary<ulong, PresentStreamEvidence>();
            private bool streamUnknown;
            private int presentCounterKeys;
            // 窗口边界可截断一次提交 每线程每端仅允许一次 真实丢事件仍使样本无效
            internal const int PresentEdgeToleranceMs = 250;
            private readonly HashSet<int> presentStarted = new HashSet<int>(), edgeStops = new HashSet<int>();
            private readonly Dictionary<int, long> pendingAt = new Dictionary<int, long>();
            private static long PresentEdgeTicks { get { return Stopwatch.Frequency * PresentEdgeToleranceMs / 1000; } }

            private int PresentStreamCount { get { return dxgiPresentStreams.Count + d3d9PresentStreams.Count; } }

            internal void CompletePresentStreams()
            {
                int unclosed = 0, edge = 0;
                foreach (KeyValuePair<int, int> item in pending)
                {
                    if (item.Value <= 0) continue;
                    long startedAt;
                    // 仅豁免紧贴关窗的未完成提交
                    if (Sample.LayoutObservation && pendingAt.TryGetValue(item.Key, out startedAt) && End >= startedAt && End - startedAt <= PresentEdgeTicks) edge++;
                    else unclosed++;
                }
                Sample.UnclosedPresentStarts = unclosed;
                Sample.EdgePresentStarts = edge;
                if (Sample.LayoutObservation && unclosed > 0) streamUnknown = true;
                Sample.PresentStreamCount = PresentStreamCount;
                Sample.PresentStreamsKnown = !streamUnknown && PresentStreamCount > 0;
            }

            private ulong ReadPresentStream(ref Native.EventRecord record, bool dxgi)
            {
                uint status;
                if (replaying) status = ReplayProperty(8u, streamBuffer);
                else
                {
                    var property = new Property { Name = (ulong)(dxgi ? DxgiStreamName : D3d9StreamName).ToInt64(), Index = uint.MaxValue };
                    if (property.Name == 0) { Sample.PresentSourceReadFailures++; streamUnknown = true; return 0; }
                    status = TdhGetProperty(ref record, 0u, IntPtr.Zero, 1u, ref property, 8u, streamBuffer);
                }
                if (status != 0)
                {
                    Sample.PresentSourceReadFailures++;
                    RecordPropertyFailure(status, record.EventHeader.EventDescriptor.Version);
                    streamUnknown = true;
                    return 0;
                }
                ulong stream = BitConverter.ToUInt64(streamBuffer, 0);
                if (stream == 0)
                { Sample.PresentSourceReadFailures++; Sample.ZeroPresentSources++; streamUnknown = true; }
                else Sample.PresentSourceReadSuccesses++;
                return stream;
            }

            private void RecordPropertyFailure(uint status, int version)
            {
                if (Sample.FirstPropertyFailureVersion >= 0) return;
                Sample.FirstPropertyFailureCode = status;
                Sample.FirstPropertyFailureVersion = version;
            }

            internal void CompletePresentDiagnostics(int first, int second, FrameCaptureIssue issue)
            {
                Sample.PresentFirstHalf = DescribeCounts(Presents[0]);
                Sample.PresentSecondHalf = DescribeCounts(Presents[1]);
                var sources = new List<string>();
                DescribeSources(sources, dxgiPresentStreams, "DXGI");
                DescribeSources(sources, d3d9PresentStreams, "D3D9");
                Sample.PresentSources = sources.Count == 0 ? "无可核验来源" : string.Join(" | ", sources.ToArray());
                if (issue != FrameCaptureIssue.None) Sample.PresentSelectionReason = Logger.Text("采集未通过核验：", "capture failed verification: ") + issue;
                else if (first == 0 || second == 0)
                    Sample.PresentSelectionReason = SelectionFailure(Presents[0], Logger.Text("前半", "first half")) + Logger.Text("；", "; ") + SelectionFailure(Presents[1], Logger.Text("后半", "second half"));
                else if (first != second) Sample.PresentSelectionReason = Logger.Text("两半主导线程不同：", "dominant threads differ between halves: ") + first + "/" + second;
                else Sample.PresentSelectionReason = Logger.Text("两半主导线程一致：", "dominant thread consistent across halves: ") + first + Logger.Text("；每半至少16次且占比至少70%", "; each half has at least 16 presents and at least 70% share");
            }

            private static void DescribeSources(List<string> details, Dictionary<ulong, PresentStreamEvidence> streams, string provider)
            {
                foreach (var entry in streams)
                    details.Add(provider + ":0x" + entry.Key.ToString("X", CultureInfo.InvariantCulture) + " 成功=" + entry.Value.Successes
                        + " 前半{" + DescribeCounts(entry.Value.Counts[0]) + "} 后半{" + DescribeCounts(entry.Value.Counts[1]) + "}");
            }

            private static string DescribeCounts(Dictionary<int, int> counts)
            {
                var entries = new List<KeyValuePair<int, int>>(counts);
                entries.Sort(delegate(KeyValuePair<int, int> left, KeyValuePair<int, int> right) {
                    int order = right.Value.CompareTo(left.Value);
                    return order == 0 ? left.Key.CompareTo(right.Key) : order;
                });
                int total = 0;
                foreach (var entry in entries) total += entry.Value;
                var details = new List<string>();
                for (int index = 0; index < entries.Count && index < 8; index++)
                    details.Add("TID=" + entries[index].Key + " 次数=" + entries[index].Value + " 份额="
                        + (total == 0 ? "0.0" : (100.0 * entries[index].Value / total).ToString("F1", CultureInfo.InvariantCulture)) + "%");
                return "总数=" + total + " 线程数=" + entries.Count + " [" + string.Join(" | ", details.ToArray()) + "]"
                    + (entries.Count > 8 ? "；其余线程=" + (entries.Count - 8) : "");
            }

            private static string SelectionFailure(Dictionary<int, int> counts, string half)
            {
                int total = 0, maximum = 0;
                foreach (var entry in counts) { total += entry.Value; maximum = Math.Max(maximum, entry.Value); }
                if (total == 0) return half + Logger.Text("没有成功提交", ": no successful presents");
                if (maximum < 16) return half + Logger.Text("单线程最多", ": top thread had ") + maximum + Logger.Text("次，少于16次", " presents, fewer than 16");
                if (maximum < total * 0.7) return half + Logger.Text("最大份额", ": top share ") + (100.0 * maximum / total).ToString("F1", CultureInfo.InvariantCulture) + Logger.Text("%，少于70%", "%, below 70%");
                return half + Logger.Text("主导门槛已满足", ": dominance threshold met");
            }

            private readonly Dictionary<int, int> pending = new Dictionary<int, int>();

            internal readonly Dictionary<int, int>[] Presents = new Dictionary<int, int>[2]
            {
                new Dictionary<int, int>(),
                new Dictionary<int, int>()
            };

            internal readonly Dictionary<long, int>[] Edges = new Dictionary<long, int>[2]
            {
                new Dictionary<long, int>(),
                new Dictionary<long, int>()
            };

            private sealed class SchedulerCpuState
            {
                internal int Running;
                internal long Last;
            }

            // ETW 的扩展处理器索引为 16 位 只为实际出现的编号分配状态 不按最大编号预留数组
            private readonly Dictionary<int, SchedulerCpuState> cpuStates = new Dictionary<int, SchedulerCpuState>();

            private bool AdmitRelevantEvent()
            {
                if (RelevantCount < int.MaxValue) RelevantCount++;
                return true;
            }

            private void FailBudget(FrameCaptureIssue issue, string detail)
            { Fault = issue; FaultDetail = detail; Invalid = true; }

            private bool RelationWithinBudget()
            {
                if (Relation == null || !Relation.ResourceLimitReached) return true;
                FailBudget(FrameCaptureIssue.EventBudget, Relation.ResourceLimitDetail);
                return false;
            }

            internal void Receive(ref Native.EventRecord r)
            {
                try
                {
                    if (Invalid)
                    {
                        return;
                    }
                    if (Count < int.MaxValue) Count++;
                    if (Count > EmergencySystemEvents)
                    { FailBudget(FrameCaptureIssue.SystemEventBudget, Logger.Text("事件送达超过完整采集应急上限", "delivered events exceeded the full-capture emergency limit")); return; }
                    if ((Count & (CpuCheckInterval - 1)) == 0
                        && CpuEmergencyExceeded(
#if PAVISE_SELFTEST
                            CpuMsForTest != null ? CpuMsForTest() :
#endif
                            ConsumerThreadCpuMs(), CallerCpuBeforeEvents))
                    { FailBudget(FrameCaptureIssue.CpuBudget, Logger.Text("事件消费与窗前准备的累计 CPU 超过应急上限", "cumulative CPU for event consumption and pre-window preparation exceeded the emergency limit")); return; }
                    // 消费者退出后汇总完整 CPU 回调仅每 4096 个事件读取一次自身耗时作应急检查
                    Dispatch(ref r);
                }
                catch (OutOfMemoryException)
                {
                    Fault = FrameCaptureIssue.CaptureFailed;
                    FaultDetail = Logger.Text("采集事件存储分配失败", "capture event storage allocation failed");
                    Invalid = true;
                }
                catch
                {
                    Fault = FrameCaptureIssue.DecodeFailed;
                    Invalid = true;
                }
            }

            // 计数与应急检查之后的逐事件规则 原生前置处理的回放也从这里进入 两条路径共用这一份
            private void Dispatch(ref Native.EventRecord r)
            {
                    if (!Invalid)
                    {
                        long timeStamp = r.EventHeader.TimeStamp;
                        if (Invalid || timeStamp < Begin || timeStamp > End)
                        {
                            return;
                        }
                        int receiveValue = ((timeStamp >= Half) ? 1 : 0);
                        int threadId = (int)r.EventHeader.ThreadId;
                        Guid providerId = r.EventHeader.ProviderId;
                        if (providerId == KernelThread)
                        {
                            if (PresenterOnly || Sample.PresentIdentityOnly)
                            {
                                return;
                            }
                            // 大处理器编号必须使用 ProcessorIndex 不能截断后归到另一颗 CPU
                            int bufferContextMask = ProcessorIndex(ref r);
                            if (bufferContextMask < 0 || bufferContextMask > ushort.MaxValue)
                            {
                                if (Relation != null) Relation.Invalidate(Logger.Text("处理器编号超过采样器支持范围", "processor index exceeds the sampler's supported range"));
                                Fault = FrameCaptureIssue.DecodeFailed;
                                Invalid = true;
                                return;
                            }
                            SchedulerCpuState cpu;
                            if (!cpuStates.TryGetValue(bufferContextMask, out cpu))
                            {
                                cpu = new SchedulerCpuState();
                                cpuStates.Add(bufferContextMask, cpu);
                            }
                            int opcode = r.EventHeader.EventDescriptor.Opcode;
                            // 回放只含原生留下的事件 倒序由原生按这颗 CPU 的全部事件判定后标出
                            if (timeStamp < cpu.Last || replayKind == NativeEtw.KindOrderReversed)
                            {
                                cpu.Running = 0;
                                if (Relation != null) Relation.Invalidate(Logger.Text("同一CPU事件时间倒序，状态重建弃用", "events on the same CPU are out of time order; state reconstruction discarded"));
                                return;
                            }
                            cpu.Last = timeStamp;
                            switch (opcode)
                            {
                            case 36:
                            {
                                SchedulerSwitches++;
                                if (r.UserDataLength < 24)
                                {
                                    cpu.Running = 0;
                                    if (Relation != null) Relation.Invalidate(Logger.Text("CSwitch数据不足，状态重建弃用", "insufficient CSwitch data; state reconstruction discarded"));
                                    break;
                                }
                                int int32 = Marshal.ReadInt32(r.UserData);
                                // 回放缺少不涉及游戏线程的切换 切换链衔接已由原生看过全部切换并在断开处报告
                                if (Relation != null && replaying)
                                    Relation.ReplaySwitch(timeStamp, bufferContextMask, int32, Marshal.ReadInt32(r.UserData, 4), Marshal.ReadByte(r.UserData, 14));
                                else if (Relation != null)
                                    Relation.Switch(timeStamp, bufferContextMask, int32, Marshal.ReadInt32(r.UserData, 4), Marshal.ReadByte(r.UserData, 14));
                                if (!RelationWithinBudget()) return;
                                bool isInSampleThreads = Sample.Threads.ContainsKey(int32);
                                cpu.Running = (isInSampleThreads ? int32 : 0);
                                break;
                            }
                            case 50:
                            {
                                if (r.UserDataLength < 8)
                                {
                                    if (Relation != null) Relation.Invalidate(Logger.Text("ReadyThread数据不足，状态重建弃用", "insufficient ReadyThread data; state reconstruction discarded"));
                                    break;
                                }
                                int targetTid = Marshal.ReadInt32(r.UserData);
                                byte readyFlags = Marshal.ReadByte(r.UserData, 6);
                                if (Relation != null) Relation.Ready(timeStamp, targetTid, readyFlags, bufferContextMask);
                                if (!RelationWithinBudget()) return;
                                if ((readyFlags & 1) != 0)
                                {
                                    break;
                                }
                                int receiveValue2 = cpu.Running;
                                // 非零条目均由本次采样固定线程集中的 CSwitch 事件准入
                                if (FixedThreadSet && receiveValue2 == 0) break;
                                if (receiveValue2 != targetTid && Sample.Threads.ContainsKey(targetTid) && (FixedThreadSet || Sample.Threads.ContainsKey(receiveValue2)) && AdmitRelevantEvent())
                                {
                                    long key = ((long)targetTid << 32) | (uint)receiveValue2;
                                    Dictionary<long, int> edges = Edges[receiveValue];
                                    int value;
                                    if (!edges.TryGetValue(key, out value) && Edges[0].Count + Edges[1].Count >= EmergencyDependencyEdges)
                                    { FailBudget(FrameCaptureIssue.EdgeBudget, Logger.Text("线程关联超过完整采集应急上限", "thread relations exceeded the full-capture emergency limit")); return; }
                                    edges[key] = value == int.MaxValue ? value : value + 1;
                                }
                                break;
                            }
                            }
                        }
                        else
                        {
                            bool isDxgi = providerId == Dxgi;
                            bool isD3d9 = providerId == D3d9;
                            if (!isDxgi && !isD3d9 || r.EventHeader.ProcessId != Sample.Pid) return;
                            int id = r.EventHeader.EventDescriptor.Id;
                            // DXGI 还会发送交换链创建等事件 只有已识别的 Present 边界才能影响来源核验
                            if (id != (isDxgi ? 42 : 1) && id != (isDxgi ? 43 : 2)) return;
                            if (!PresenterOnly && !Sample.Threads.ContainsKey(threadId))
                            {
                                if (Sample.LayoutObservation) { Sample.UntrackedPresentEvents++; streamUnknown = true; }
                                return;
                            }
                            if (r.EventHeader.EventDescriptor.Version != 0)
                            {
                                if (Sample.LayoutObservation)
                                {
                                    Sample.UnsupportedPresentEvents++;
                                    if (Sample.FirstUnsupportedPresentVersion < 0) Sample.FirstUnsupportedPresentVersion = r.EventHeader.EventDescriptor.Version;
                                    streamUnknown = true;
                                }
                                return;
                            }
                            if (!AdmitRelevantEvent()) return;
                            int value2;
                            if (id == ((!isDxgi) ? 1 : 42))
                            {
                                if (isDxgi)
                                {
                                    Property property = default(Property);
                                    property.Name = (ulong)FlagsName.ToInt64();
                                    property.Index = uint.MaxValue;
                                    Property property2 = property;
                                    uint flagsStatus = replaying ? ReplayProperty(4u, flagBuffer)
                                        : TdhGetProperty(ref r, 0u, IntPtr.Zero, 1u, ref property2, 4u, flagBuffer);
                                    if (flagsStatus != 0 || (BitConverter.ToUInt32(flagBuffer, 0) & 1u) != 0)
                                    {
                                        if (Sample.LayoutObservation && flagsStatus != 0)
                                        {
                                            Sample.PresentFlagsReadFailures++;
                                            RecordPropertyFailure(flagsStatus, r.EventHeader.EventDescriptor.Version);
                                            streamUnknown = true;
                                        }
                                        pending.Remove(threadId);
                                        pendingStreams.Remove(threadId);
                                        if (flagsStatus == 0) pending[threadId] = -id;
                                        return;
                                    }
                                }
                                if (Sample.LayoutObservation && pending.TryGetValue(threadId, out value2) && value2 > 0)
                                { Sample.ReplacedPresentStarts++; streamUnknown = true; }
                                pending[threadId] = id;
                                if (Sample.LayoutObservation)
                                {
                                    pendingStreams[threadId] = ReadPresentStream(ref r, isDxgi);
                                    pendingAt[threadId] = timeStamp;
                                    presentStarted.Add(threadId);
                                }
                            }
                            else if (id == (isDxgi ? 43 : 2) && pending.TryGetValue(threadId, out value2) && value2 == ((!isDxgi) ? 1 : 42))
                            {
                                pending.Remove(threadId);
                                if (r.UserDataLength >= 4 && Marshal.ReadInt32(r.UserData) == 0)
                                {
                                    int value3;
                                    Presents[receiveValue].TryGetValue(threadId, out value3);
                                    Presents[receiveValue][threadId] = value3 + 1;
                                    if (Sample.LayoutObservation)
                                    {
                                        ulong stream;
                                        if (!pendingStreams.TryGetValue(threadId, out stream) || stream == 0) streamUnknown = true;
                                        else
                                        {
                                            Dictionary<ulong, PresentStreamEvidence> streams = isDxgi ? dxgiPresentStreams : d3d9PresentStreams;
                                            PresentStreamEvidence source;
                                            if (!streams.TryGetValue(stream, out source))
                                            {
                                                if (PresentStreamCount >= EmergencyPresentSources)
                                                {
                                                    Sample.PresentSourceCapacityEvents++;
                                                    FailBudget(FrameCaptureIssue.EventBudget, Logger.Text("成功呈现来源超过采集应急上限", "successful present sources exceeded the capture emergency limit"));
                                                    return;
                                                }
                                                source = new PresentStreamEvidence(); streams.Add(stream, source);
                                            }
                                            int submitted;
                                            if (!source.Counts[receiveValue].TryGetValue(threadId, out submitted))
                                            {
                                                if (presentCounterKeys >= EmergencyPresentCounterKeys)
                                                { FailBudget(FrameCaptureIssue.EdgeBudget, Logger.Text("呈现来源计数键超过采集应急上限", "present-source counter keys exceeded the capture emergency limit")); return; }
                                                presentCounterKeys++;
                                            }
                                            source.Counts[receiveValue][threadId] = submitted + 1;
                                            source.Successes++;
                                        }
                                        Sample.PresentStreamCount = PresentStreamCount;
                                        Sample.PresentStreamsKnown = !streamUnknown && PresentStreamCount > 0;
                                        // 第二个成功来源已足以否定单一来源 保留两源诊断后停止 不截取单源继续准入
                                        if (Sample.PresentIdentityOnly && PresentStreamCount > 1)
                                        {
                                            Fault = FrameCaptureIssue.InsufficientEvidence;
                                            FaultDetail = Logger.Text("仅呈现身份采集确认多个成功来源，本轮样本弃用", "present-identity-only capture confirmed multiple successful sources; this round's sample discarded");
                                            Invalid = true;
                                            return;
                                        }
                                    }
                                    if (Relation != null) Relation.SuccessfulPresent(threadId, timeStamp);
                                    if (!RelationWithinBudget()) return;
                                }
                                pendingStreams.Remove(threadId);
                            }
                            else if (id == (isDxgi ? 43 : 2))
                            {
                                // 显式 TEST 不提交画面 其结束事件不是缺少开始边界的未知提交
                                if (pending.TryGetValue(threadId, out value2) && value2 == -(isDxgi ? 42 : 1))
                                { pending.Remove(threadId); pendingStreams.Remove(threadId); }
                                else if (Sample.LayoutObservation)
                                {
                                    // 仅豁免该线程紧贴开窗的首个孤立结束事件
                                    if (!presentStarted.Contains(threadId) && timeStamp >= Begin && timeStamp - Begin <= PresentEdgeTicks && edgeStops.Add(threadId))
                                        Sample.EdgePresentStops++;
                                    else { Sample.UnmatchedPresentStops++; streamUnknown = true; }
                                }
                            }
                        }
                    }
            }

            private bool replaying;
            private byte replayKind;
            private NativeEtw.PresentRecord replayPresent;
            private const int ReplayDataBytes = 24;

            // 原生回调已在原始事件上按同样的条件读好属性 回放到读取位置时取回同一结果
            private uint ReplayProperty(uint bytes, byte[] data)
            {
                uint read = bytes == 4u ? 1u : 2u;
                if ((replayPresent.Properties & read) == 0) throw new InvalidOperationException(Logger.Text("原生前置处理没有读取这项画面提交属性", "native preprocessing did not read this present property"));
                uint status = bytes == 4u ? replayPresent.FlagsStatus : replayPresent.StreamStatus;
                if (status == ReaderOutOfMemory) throw new OutOfMemoryException();
                if (status == ReaderFault) throw new ReaderFaultException();
                ulong value = bytes == 4u ? replayPresent.FlagsValue : replayPresent.Stream;
                for (int index = 0; index < bytes; index++) data[index] = (byte)(value >> (8 * index));
                return status;
            }

            // 消费者返回后 按到达顺序把原生留下的事件交给 Dispatch 每条记录带着原生在该事件处的
            // 已送达数和切换数 回放中失效时停在同一个事件上 计数也停在那里 和逐事件处理一致
            internal void ReplayNative()
            {
                NativeEtwCapture native = NativeCapture;
                if (native == null || Invalid) return;
                IntPtr data = Marshal.AllocHGlobal(ReplayDataBytes);
                replaying = true;
                try
                {
                    NativeEtw.Stats stats;
                    bool complete = native.Read(out stats, delegate(NativeEtw.Record record, NativeEtw.PresentRecord present)
                    {
                        ReplayRecord(record, present, data);
                        return !Invalid;
                    });
                    if (!complete) { Fault = FrameCaptureIssue.DecodeFailed; FaultDetail = Logger.Text("原生前置处理的事件记录无法读取", "native preprocessing event records could not be read"); Invalid = true; return; }
                    if (Invalid) return;
                    Count = stats.Count;
                    SchedulerSwitches = stats.Switches;
                    switch (stats.Fault)
                    {
                    case NativeEtw.FaultNone:
                        break;
                    case NativeEtw.FaultSystemEvents:
                        FailBudget(FrameCaptureIssue.SystemEventBudget, Logger.Text("事件送达超过完整采集应急上限", "delivered events exceeded the full-capture emergency limit"));
                        break;
                    case NativeEtw.FaultCpu:
                        FailBudget(FrameCaptureIssue.CpuBudget, Logger.Text("事件消费与窗前准备的累计 CPU 超过应急上限", "cumulative CPU for event consumption and pre-window preparation exceeded the emergency limit"));
                        break;
                    case NativeEtw.FaultMemory:
                        Fault = FrameCaptureIssue.CaptureFailed; FaultDetail = Logger.Text("采集事件存储分配失败", "capture event storage allocation failed"); Invalid = true;
                        break;
                    case NativeEtw.FaultRecords:
                        FailBudget(FrameCaptureIssue.EventBudget, Logger.Text("原生前置处理留下的游戏相关记录超过完整采集应急上限", "game-related records left by native preprocessing exceeded the full-capture emergency limit"));
                        break;
                    case NativeEtw.FaultReader:
                        // 托管处理里 CPU 读取抛出异常时按解码失败弃样 不另写原因
                        Fault = FrameCaptureIssue.DecodeFailed; Invalid = true;
                        break;
                    default:
                        Fault = FrameCaptureIssue.DecodeFailed; FaultDetail = Logger.Text("原生前置处理报告了未知的停止原因", "native preprocessing reported an unknown stop reason"); Invalid = true;
                        break;
                    }
                }
                catch (OutOfMemoryException)
                {
                    Fault = FrameCaptureIssue.CaptureFailed;
                    FaultDetail = Logger.Text("采集事件存储分配失败", "capture event storage allocation failed");
                    Invalid = true;
                }
                catch (ReaderFaultException)
                {
                    // 和托管处理遇到读取异常时一样 解码失败 不改原因
                    Fault = FrameCaptureIssue.DecodeFailed;
                    Invalid = true;
                }
                catch (Exception ex)
                {
                    Fault = FrameCaptureIssue.DecodeFailed;
                    FaultDetail = Logger.Text("原生事件回放失败：", "native event replay failed: ") + ex.Message;
                    Invalid = true;
                }
                finally
                {
                    replaying = false;
                    replayKind = 0;
                    Marshal.FreeHGlobal(data);
                }
            }

            // 把一条原生记录还原成 Dispatch 需要的事件字段 内核事件统一按 16 位处理器索引给出 CPU
            private void ReplayRecord(NativeEtw.Record record, NativeEtw.PresentRecord present, IntPtr data)
            {
                for (int index = 0; index < ReplayDataBytes; index++) Marshal.WriteByte(data, index, 0);
                var r = new Native.EventRecord();
                r.EventHeader.TimeStamp = record.Time;
                r.UserData = data;
                Count = record.Count;
                SchedulerSwitches = record.Switches;
                replayKind = record.Kind;
                switch (record.Kind)
                {
                case NativeEtw.KindDiscontinuity:
                    if (Relation != null) Relation.BreakContinuity(record.Time);
                    return;
                case NativeEtw.KindPresent:
                    replayPresent = present;
                    r.EventHeader.ProviderId = present.IsDxgi != 0 ? Dxgi : D3d9;
                    r.EventHeader.ProcessId = (uint)Sample.Pid;
                    r.EventHeader.ThreadId = (uint)present.ThreadId;
                    r.EventHeader.EventDescriptor.Id = present.Id;
                    r.EventHeader.EventDescriptor.Version = present.Version;
                    r.UserDataLength = (ushort)present.UserDataLength;
                    Marshal.WriteInt32(data, present.Result);
                    Dispatch(ref r);
                    return;
                case NativeEtw.KindSwitch:
                case NativeEtw.KindSwitchShort:
                    // Dispatch 会把这次切换再计一次
                    SchedulerSwitches = record.Switches - 1;
                    r.EventHeader.EventDescriptor.Opcode = 36;
                    if (record.Kind == NativeEtw.KindSwitch)
                    {
                        r.UserDataLength = 24;
                        Marshal.WriteInt32(data, 0, record.A);
                        Marshal.WriteInt32(data, 4, record.B);
                        Marshal.WriteByte(data, 14, record.State);
                    }
                    break;
                case NativeEtw.KindReady:
                case NativeEtw.KindReadyShort:
                    r.EventHeader.EventDescriptor.Opcode = 50;
                    if (record.Kind == NativeEtw.KindReady)
                    {
                        SchedulerCpuState cpu;
                        int running = cpuStates.TryGetValue(record.Cpu, out cpu) ? cpu.Running : 0;
                        if (running != record.B) throw new InvalidOperationException(Logger.Text("原生前置处理与回放的运行线程不一致", "running thread differs between native preprocessing and replay"));
                        r.UserDataLength = 8;
                        Marshal.WriteInt32(data, 0, record.A);
                        Marshal.WriteByte(data, 6, record.State);
                    }
                    break;
                case NativeEtw.KindOrderReversed:
                    break;
                default:
                    throw new InvalidOperationException(Logger.Text("原生前置处理留下了未知记录类型 ", "native preprocessing left an unknown record kind ") + record.Kind);
                }
                r.EventHeader.ProviderId = KernelThread;
                r.EventHeader.Flags = 0x0200;
                r.BufferContext = record.Cpu;
                Dispatch(ref r);
            }
        }

        // 只作诊断参考值 应急停止用下方独立门限 这个软水位不作为失败条件
        internal const int MaxRelevantEvents = 750000;

        internal const int MaxSystemEvents = 1000000;

        internal const int CpuCheckInterval = 4096;

        internal const double MaxCaptureCpuMs = 500.0;

        // 根据合成高负载留出余量的应急保护 不是游戏帧时间目标 超过才弃用整窗 不发布截断证据
        // 系统事件上限按逻辑处理器数放大 每个 25 万 最少 400 万 最多 1000 万
        // 线程多的机器空闲时事件就多 32 线程空闲一个窗口就有约 200 万 固定 400 万会让普通负载也整窗作废
        internal static readonly int EmergencySystemEvents = SystemEventCap(Environment.ProcessorCount);
        internal const int EmergencyDependencyEdges = 262144;

        internal static int SystemEventCap(int processors)
        {
            long cap = 250000L * Math.Max(1, processors);
            return (int)Math.Max(4000000L, Math.Min(10000000L, cap));
        }
        internal const int EmergencyPresentCounterKeys = 262144;
        internal const int EmergencyPresentSources = 4096, EmergencyInitialThreads = 16384;
        internal const double EmergencyCaptureCpuMs = 2000.0;

        private static bool CpuEmergencyExceeded(double consumer, double caller)
        {
            bool consumerKnown = KnownCpu(consumer), callerKnown = KnownCpu(caller);
            // 任一已知分量已超界时不必等另一分量 未知读数不当作零展示 也不单独导致弃样
            return consumerKnown && consumer > EmergencyCaptureCpuMs || callerKnown && caller > EmergencyCaptureCpuMs
                || consumerKnown && callerKnown && consumer + caller > EmergencyCaptureCpuMs;
        }

        private static bool KnownCpu(double value)
        { return !double.IsNaN(value) && !double.IsInfinity(value) && value >= 0; }

        private static double CpuSince(double before, double after)
        { return KnownCpu(before) && KnownCpu(after) && after >= before ? after - before : double.NaN; }

        private static bool ResourceBudgetIssue(FrameCaptureIssue issue)
        {
            return issue == FrameCaptureIssue.EventBudget || issue == FrameCaptureIssue.SystemEventBudget
                || issue == FrameCaptureIssue.EdgeBudget || issue == FrameCaptureIssue.CpuBudget || issue == FrameCaptureIssue.ThreadBudget;
        }

        internal static readonly Guid Dxgi = new Guid("ca11c036-0102-4a2d-a6ad-f03cfed5d3c9");

        internal static readonly Guid D3d9 = new Guid("783aca0a-790e-4d7f-8451-aa850511c6b9");

        internal static readonly Guid KernelThread = new Guid("3d6fa8d1-fe05-11d0-9dda-00c04fd7ba7c");

        internal static int ProcessorIndex(ref Native.EventRecord record)
        { return (int)(record.BufferContext & ((record.EventHeader.Flags & 0x0200) != 0 ? 0xFFFFu : 0xFFu)); }

        [DllImport("tdh.dll")]
        private static extern uint TdhGetProperty(ref Native.EventRecord record, uint contextCount, IntPtr context, uint count, ref Property property, uint bytes, byte[] data);

        internal static int Dominant(Dictionary<int, int> counts, int minimum, double share)
        {
            int dominantValue = 0;
            int result = 0;
            int dominantValue2 = 0;
            foreach (KeyValuePair<int, int> count in counts)
            {
                dominantValue += count.Value;
                if (count.Value > dominantValue2)
                {
                    dominantValue2 = count.Value;
                    result = count.Key;
                }
            }
            if (dominantValue2 < minimum || !((double)dominantValue2 >= (double)dominantValue * share))
            {
                return 0;
            }
            return result;
        }

        private static int DominantWorker(Dictionary<long, int> edges, int presenter)
        {
            int total = 0, best = 0, maximum = 0;
            foreach (KeyValuePair<long, int> edge in edges)
            {
                if ((int)(edge.Key >> 32) == presenter)
                {
                    total += edge.Value;
                    if (edge.Value > maximum)
                    {
                        maximum = edge.Value;
                        best = (int)edge.Key;
                    }
                }
            }
            return maximum >= 12 && (double)maximum >= (double)total * 0.75 ? best : 0;
        }

        internal static int SelectWorker(Evidence evidence, int presenter)
        {
            if (evidence.PresenterOnly)
            {
                return 0;
            }
            int dominantWorkerResult = DominantWorker(evidence.Edges[0], presenter);
            int dominantWorkerResult2 = DominantWorker(evidence.Edges[1], presenter);
            if (dominantWorkerResult == 0 || dominantWorkerResult != dominantWorkerResult2)
            {
                return 0;
            }
            return dominantWorkerResult;
        }

        internal static FrameCaptureIssue CaptureIssue(bool admitted, int identityState, bool stopped, bool drained, int traceCode, uint lost, FrameCaptureIssue fault, double cpuMs)
        {
            if (traceCode != 0 || !drained || (!stopped && lost == 0))
            {
                return FrameCaptureIssue.TraceStopFailed;
            }
            if (lost != 0)
            {
                return FrameCaptureIssue.EventsLost;
            }
            if (fault == FrameCaptureIssue.DecodeFailed)
            {
                return fault;
            }
            if (!admitted)
            {
                if (fault != FrameCaptureIssue.SystemEventBudget && fault != FrameCaptureIssue.EventBudget && fault != FrameCaptureIssue.EdgeBudget && fault != FrameCaptureIssue.CpuBudget)
                {
                    return FrameCaptureIssue.Canceled;
                }
                return fault;
            }
            if (identityState > 0)
            {
                return FrameCaptureIssue.TargetChanged;
            }
            if (identityState < 0)
            {
                return FrameCaptureIssue.TargetUnavailable;
            }
            if (fault != 0)
            {
                return fault;
            }
            return FrameCaptureIssue.None;
        }

        internal static FrameCoordinationSample Capture(int pid, long birth, Func<bool> alive)
        {
            return Capture(pid, birth, alive, false);
        }

        private static bool AdmitPresenter(FrameCoordinationSample sample, int tid)
        {
            IntPtr intPtr = Native.OpenThread(2048, false, tid);
            if (intPtr == IntPtr.Zero)
            {
                return false;
            }
            try
            {
                long creation;
                long exit;
                long kernel;
                long user;
                if (Native.QueryThreadOwnerPid(intPtr) != sample.Pid || !Native.GetThreadTimes(intPtr, out creation, out exit, out kernel, out user) || exit != 0 || creation <= 0)
                {
                    return false;
                }
                sample.Threads[tid] = creation;
                return true;
            }
            finally
            {
                Native.CloseHandle(intPtr);
            }
        }

        internal static FrameCoordinationSample Capture(int pid, long birth, Func<bool> alive, bool presenterOnly)
        { return Capture(pid, birth, alive, presenterOnly, false); }

        internal static FrameCoordinationSample Capture(int pid, long birth, Func<bool> alive, bool presenterOnly, bool layoutObservation)
        { return Capture(pid, birth, alive, presenterOnly, layoutObservation, false); }

        internal static FrameCoordinationSample Capture(int pid, long birth, Func<bool> alive, bool presenterOnly, bool layoutObservation, bool workloadObservation)
        { return Capture(pid, birth, alive, presenterOnly, layoutObservation, workloadObservation, false); }

        internal static FrameCoordinationSample Capture(int pid, long birth, Func<bool> alive, bool presenterOnly, bool layoutObservation, bool workloadObservation, bool presentIdentityOnly)
        {
            double captureThreadCpuStart = CaptureThreadCpuMs();
            FrameCoordinationSample frameCoordinationSample = new FrameCoordinationSample();
            frameCoordinationSample.Pid = pid;
            frameCoordinationSample.Birth = birth;
            frameCoordinationSample.PresenterOnly = presenterOnly;
            frameCoordinationSample.PresentIdentityOnly = presentIdentityOnly;
            // 来源读取取决于采集用途 须在消费者启动前冻结
            frameCoordinationSample.LayoutObservation = layoutObservation;
            FrameCoordinationSample frameCoordinationSample2 = frameCoordinationSample;
            frameCoordinationSample2.Reject(FrameCaptureIssue.InsufficientEvidence);
            Evidence evidence = new Evidence();
            evidence.Sample = frameCoordinationSample2;
            evidence.PresenterOnly = presenterOnly;
            evidence.WorkloadObservation = workloadObservation;
            Evidence evidence2 = evidence;
            FrameCoordinationTrace frameCoordinationTrace = new FrameCoordinationTrace();
            try
            {
                if (presentIdentityOnly && (presenterOnly || !layoutObservation || workloadObservation))
                    return frameCoordinationSample2.Reject(FrameCaptureIssue.CaptureFailed, Logger.Text("仅呈现身份采集的用途组合无效", "invalid purpose combination for present-identity-only capture"));
                if (!alive())
                {
                    return frameCoordinationSample2.Reject(FrameCaptureIssue.Canceled);
                }
                int processStateResult = NativeFrameThreads.ProcessState(pid, birth);
                if (processStateResult != 0)
                {
                    return frameCoordinationSample2.Reject((processStateResult > 0) ? FrameCaptureIssue.TargetChanged : FrameCaptureIssue.TargetUnavailable);
                }
                if (!presenterOnly)
                {
                    using (Process process = Process.GetProcessById(pid))
                    {
                        ProcessThreadCollection threads = process.Threads;
                        if (threads.Count > EmergencyInitialThreads)
                            return frameCoordinationSample2.Reject(FrameCaptureIssue.ThreadBudget, Logger.Text("初始线程身份表超过采集应急上限", "initial thread identity table exceeded the capture emergency limit"));
                        foreach (ProcessThread thread in threads)
                        {
                            if (!alive()) return frameCoordinationSample2.Reject(FrameCaptureIssue.Canceled);
                            ReadInitialThreadIdentity(frameCoordinationSample2, thread.Id);
                            if (frameCoordinationSample2.Issue == FrameCaptureIssue.ThreadBudget) return frameCoordinationSample2;
                        }
                    }
                }
                if (frameCoordinationSample2.Issue == FrameCaptureIssue.ThreadBudget) return frameCoordinationSample2;
                if (!alive()) return frameCoordinationSample2.Reject(FrameCaptureIssue.Canceled);
                processStateResult = NativeFrameThreads.ProcessState(pid, birth);
                if (processStateResult != 0)
                    return frameCoordinationSample2.Reject(processStateResult > 0 ? FrameCaptureIssue.TargetChanged : FrameCaptureIssue.TargetUnavailable);
                evidence2.FixedThreadSet = !presenterOnly;
                evidence2.FlagsName = Marshal.StringToHGlobalUni("Flags");
                evidence2.DxgiStreamName = Marshal.StringToHGlobalUni("pIDXGISwapChain");
                evidence2.D3d9StreamName = Marshal.StringToHGlobalUni("pSwapchain");
                frameCoordinationTrace.OwnDrainedCleanup(evidence2.ReleasePropertyNames);
                evidence2.Begin = Stopwatch.GetTimestamp();
                evidence2.Half = evidence2.Begin + Stopwatch.Frequency * 8 / 5;
                evidence2.End = evidence2.Begin + Stopwatch.Frequency * 16 / 5;
                // 关系摘要只供统一调度的布局观察使用 线程协同只认呈现与配合线程 不建关系图
                // 也就不会因建图的资源上限弃掉可用样本
                if (layoutObservation && !presenterOnly && !presentIdentityOnly)
                    evidence2.Relation = new ThreadRelationCollector(frameCoordinationSample2.Threads, evidence2.Begin,
                        evidence2.Half, evidence2.End, Stopwatch.Frequency);
                evidence2.CallerCpuBeforeEvents = CpuSince(captureThreadCpuStart, CaptureThreadCpuMs());
                if (CpuEmergencyExceeded(double.NaN, evidence2.CallerCpuBeforeEvents))
                    return frameCoordinationSample2.Reject(FrameCaptureIssue.CpuBudget, Logger.Text("采集准备的 CPU 已超过应急上限", "capture preparation CPU exceeded the emergency limit"));
                // 完整采集的逐事件处理只走原生前置处理 加载或创建失败时本轮直接弃用 不换回 C# 逐事件处理
                bool fullCapture = !presenterOnly && !presentIdentityOnly;
                if (fullCapture)
                {
                    string nativeFailure;
                    evidence2.NativeCapture = NativeEtwCapture.Create(pid, evidence2.Begin, evidence2.End, frameCoordinationSample2.Threads.Keys,
                        layoutObservation, evidence2.CallerCpuBeforeEvents, ConsumerThreadCpuMs,
                        new Evidence.NativePropertyReader(evidence2.ReadNativeProperty), out nativeFailure);
                    if (evidence2.NativeCapture == null)
                        return frameCoordinationSample2.Reject(FrameCaptureIssue.CaptureFailed, nativeFailure);
                    if (!frameCoordinationTrace.UseNativeConsumer(evidence2.NativeCapture.Callback, evidence2.NativeCapture.Context))
                        return frameCoordinationSample2.Reject(FrameCaptureIssue.CaptureFailed, Logger.Text("原生事件处理无法接入采集会话", "native event processing could not attach to the capture session"));
                }
                if (!(fullCapture ? frameCoordinationTrace.Start(pid, evidence2.ReceiveNative, false)
                    : frameCoordinationTrace.Start(pid, evidence2.Receive, presenterOnly || presentIdentityOnly)))
                {
                    frameCoordinationSample2.TraceCode = frameCoordinationTrace.Error;
                    frameCoordinationSample2.TraceStage = frameCoordinationTrace.Stage;
                    frameCoordinationSample2.TraceFailureDetail = frameCoordinationTrace.FailureDetail;
                    return frameCoordinationSample2.Reject(FrameCaptureIssue.TraceUnavailable, frameCoordinationSample2.TraceFailureDetail);
                }
                while (Stopwatch.GetTimestamp() < evidence2.End && alive() && !evidence2.Invalid && !evidence2.NativeFaulted)
                {
                    Thread.Sleep(100);
                }
                bool stopped = frameCoordinationTrace.Stop();
                // 回放只能在消费者返回之后读原生缓冲 没返回时下面按未排空处理 不发布任何结论
                if (evidence2.NativeCapture != null && frameCoordinationTrace.Drained)
                {
                    evidence2.NativeCapture.ConsumerReturned();
                    evidence2.ReplayNative();
                }
                frameCoordinationSample2.CpuMs = frameCoordinationTrace.CpuMs;
                frameCoordinationSample2.TraceCode = frameCoordinationTrace.Error;
                frameCoordinationSample2.TraceStage = frameCoordinationTrace.Stage;
                if (frameCoordinationTrace.Error != 0) frameCoordinationSample2.TraceFailureDetail = frameCoordinationTrace.FailureDetail;
                frameCoordinationSample2.LostEvents = frameCoordinationTrace.Lost;
                frameCoordinationSample2.Events = evidence2.RelevantCount;
                frameCoordinationSample2.SystemEvents = evidence2.Count;
                frameCoordinationSample2.SchedulerSwitches = evidence2.SchedulerSwitches;
                FrameCaptureIssue frameCaptureIssue = CaptureIssue(alive(), NativeFrameThreads.ProcessState(pid, birth), stopped, frameCoordinationTrace.Drained, frameCoordinationTrace.Error, frameCoordinationTrace.Lost, evidence2.Fault, frameCoordinationSample2.CpuMs);
                if (frameCaptureIssue == FrameCaptureIssue.None && CpuEmergencyExceeded(frameCoordinationTrace.CpuMs,
                    CpuSince(captureThreadCpuStart, CaptureThreadCpuMs())))
                    frameCaptureIssue = FrameCaptureIssue.CpuBudget;
                int presentTidValue = frameCoordinationTrace.Drained ? Dominant(evidence2.Presents[0], 16, 0.7) : 0;
                int dominantResult = frameCoordinationTrace.Drained ? Dominant(evidence2.Presents[1], 16, 0.7) : 0;
                if (frameCoordinationTrace.Drained)
                {
                    evidence2.CompletePresentStreams();
                    if (!ResourceBudgetIssue(frameCaptureIssue))
                        evidence2.CompletePresentDiagnostics(presentTidValue, dominantResult, frameCaptureIssue);
                    else frameCoordinationSample2.PresentSelectionReason = Logger.Text("完整采集触发应急保护：", "full capture triggered emergency protection: ") + frameCaptureIssue;
                }
                var processingPacer = new CaptureProcessingPacer(delegate
                    {
                        double current = CaptureThreadCpuMs();
                        if ((frameCaptureIssue == FrameCaptureIssue.None || frameCaptureIssue == FrameCaptureIssue.CpuBudget)
                            && CpuEmergencyExceeded(frameCoordinationTrace.CpuMs, CpuSince(captureThreadCpuStart, current)))
                            frameCoordinationSample2.Reject(FrameCaptureIssue.CpuBudget, Logger.Text("采集及摘要处理的累计 CPU 超过应急上限", "cumulative CPU for capture and summary processing exceeded the emergency limit"));
                        return current;
                    },
                    delegate { return Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency; },
                    delegate { Thread.Sleep(1); frameCoordinationSample2.ProcessingYields++; });
                if (evidence2.Relation != null)
                    frameCoordinationSample2.Relation = !frameCoordinationTrace.Drained
                        ? ThreadRelationSnapshot.Unavailable(pid, birth, evidence2.Begin, evidence2.End, Stopwatch.Frequency,
                            frameCoordinationSample2.LostEvents, Logger.Text("事件消费者尚未退出，保留现场并弃用关系摘要", "event consumer has not exited; state retained and relation summary discarded"))
                        : evidence2.Relation.Build(frameCoordinationSample2,
                            presentTidValue == dominantResult ? presentTidValue : 0, evidence2.Edges,
                            frameCoordinationSample2.Issue == FrameCaptureIssue.CpuBudget ? FrameCaptureIssue.CpuBudget : frameCaptureIssue,
                            delegate(int tid, long created) { return VerifyRelationIdentity(frameCoordinationSample2, tid, created); }, workloadObservation,
                            delegate
                            {
                                if (!alive())
                                {
                                    frameCoordinationSample2.Reject(FrameCaptureIssue.Canceled);
                                    return false;
                                }
                                processingPacer.Continue();
                                if (frameCoordinationSample2.Issue == FrameCaptureIssue.CpuBudget) return false;
                                if (alive()) return true;
                                frameCoordinationSample2.Reject(FrameCaptureIssue.Canceled);
                                return false;
                            });
                if (frameCoordinationSample2.Issue != FrameCaptureIssue.None
                    && frameCoordinationSample2.Issue != FrameCaptureIssue.InsufficientEvidence)
                    return frameCoordinationSample2;
                if (frameCaptureIssue != 0)
                {
                    return frameCoordinationSample2.Reject(frameCaptureIssue, evidence2.FaultDetail);
                }
                if (presentTidValue == 0 || presentTidValue != dominantResult)
                {
                    return frameCoordinationSample2;
                }
                if (presenterOnly && !AdmitPresenter(frameCoordinationSample2, presentTidValue))
                {
                    return frameCoordinationSample2;
                }
                frameCoordinationSample2.PresentTid = presentTidValue;
                frameCoordinationSample2.WorkerTid = SelectWorker(evidence2, presentTidValue);
                frameCoordinationSample2.Valid = true;
                frameCoordinationSample2.Issue = FrameCaptureIssue.None;
                long presentCreation;
                frameCoordinationSample2.PresentValid = layoutObservation && frameCoordinationSample2.PresentStreamsKnown
                    && frameCoordinationSample2.PresentStreamCount == 1
                    && frameCoordinationSample2.Threads.TryGetValue(presentTidValue, out presentCreation)
                    && VerifyRelationIdentity(frameCoordinationSample2, presentTidValue, presentCreation);
                frameCoordinationSample2.Detail = (presenterOnly ? "presenter confirmed; presenter-only capture" : ((frameCoordinationSample2.WorkerTid == 0) ? "presenter confirmed; no stable worker" : "presenter and worker confirmed"));
                if (!alive()) return frameCoordinationSample2.Reject(FrameCaptureIssue.Canceled);
                processStateResult = NativeFrameThreads.ProcessState(pid, birth);
                if (processStateResult != 0)
                    return frameCoordinationSample2.Reject(processStateResult > 0 ? FrameCaptureIssue.TargetChanged : FrameCaptureIssue.TargetUnavailable);
                return frameCoordinationSample2;
            }
            catch (ArgumentException ex)
            {
                if (!alive() || NativeFrameThreads.ProcessState(pid, birth) > 0)
                {
                    return frameCoordinationSample2.Reject(FrameCaptureIssue.TargetChanged);
                }
                return frameCoordinationSample2.Reject(FrameCaptureIssue.CaptureFailed, ex.GetType().Name + ": " + ex.Message);
            }
            catch (Win32Exception ex2)
            {
                return frameCoordinationSample2.Reject(FrameCaptureIssue.TargetUnavailable, "code=" + ex2.NativeErrorCode);
            }
            catch (Exception ex3)
            {
                return frameCoordinationSample2.Reject(FrameCaptureIssue.CaptureFailed, ex3.GetType().Name + ": " + ex3.Message);
            }
            finally
            {
                evidence2.Invalid = true;
                if (evidence2.NativeCapture != null) evidence2.NativeCapture.Stop();
                frameCoordinationTrace.Stop();
                frameCoordinationSample2.TraceCode = frameCoordinationTrace.Error;
                frameCoordinationSample2.TraceStage = frameCoordinationTrace.Stage;
                if (frameCoordinationTrace.Error != 0)
                {
                    frameCoordinationSample2.TraceFailureDetail = frameCoordinationTrace.FailureDetail;
                    if (frameCoordinationSample2.Issue == FrameCaptureIssue.TraceUnavailable || frameCoordinationSample2.Issue == FrameCaptureIssue.TraceStopFailed)
                        frameCoordinationSample2.Reject(frameCoordinationSample2.Issue, frameCoordinationSample2.TraceFailureDetail);
                    else if (frameCoordinationSample2.Valid)
                        frameCoordinationSample2.Reject(FrameCaptureIssue.TraceStopFailed, frameCoordinationSample2.TraceFailureDetail);
                }
                if (frameCoordinationTrace.Drained) evidence2.ReleasePropertyNames();
                evidence2.ReleaseNative();
                CompleteCaptureCpu(frameCoordinationSample2, frameCoordinationTrace.CpuMs, captureThreadCpuStart, CaptureThreadCpuMs());
            }
        }

        private static double CaptureThreadCpuMs()
        {
            long creation, exit, kernel, user;
            return Native.GetThreadTimes(new IntPtr(-2), out creation, out exit, out kernel, out user)
                ? (kernel + user) / 10000.0 : double.NaN;
        }

        // 消费者为本次新线程 累计 CPU 可直接作为本窗分量 只在固定事件检查点调用
        private static double ConsumerThreadCpuMs()
        { return CaptureThreadCpuMs(); }

        // CPU 统计涵盖事件消费 建图 身份核验和收尾 超出参考值不弃样 超过应急上限才整窗拒绝
        internal static void CompleteCaptureCpu(FrameCoordinationSample sample, double consumerCpu, double callerBefore, double callerAfter)
        {
            if ((sample.Valid || sample.Issue == FrameCaptureIssue.None || sample.Issue == FrameCaptureIssue.InsufficientEvidence)
                && CpuEmergencyExceeded(consumerCpu, CpuSince(callerBefore, callerAfter)))
                sample.Reject(FrameCaptureIssue.CpuBudget, Logger.Text("采集及收尾的累计 CPU 超过应急上限", "cumulative CPU for capture and finalization exceeded the emergency limit"));
            if (double.IsNaN(consumerCpu) || double.IsInfinity(consumerCpu) || consumerCpu < 0
                || double.IsNaN(callerBefore) || double.IsInfinity(callerBefore) || callerBefore < 0
                || double.IsNaN(callerAfter) || double.IsInfinity(callerAfter) || callerAfter < callerBefore)
            {
                sample.ConsumerCpuMs = sample.CallerCpuMs = sample.CpuMs = -1;
                return;
            }
            sample.ConsumerCpuMs = consumerCpu;
            sample.CallerCpuMs = callerAfter - callerBefore;
            sample.CpuMs = sample.ConsumerCpuMs + sample.CallerCpuMs;
        }

        internal static void ReadInitialThreadIdentity(FrameCoordinationSample sample, int tid)
        {
            if (sample.Threads.Count >= EmergencyInitialThreads && !sample.Threads.ContainsKey(tid))
            { sample.Reject(FrameCaptureIssue.ThreadBudget, Logger.Text("初始线程身份表超过采集应急上限", "initial thread identity table exceeded the capture emergency limit")); return; }
            IntPtr handle = Native.OpenThread(2048, false, tid);
            if (handle == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                // 枚举后已销毁的线程不参与采集 其他打开失败仍计为身份未核验
                if (error != 87) sample.RecordThreadIdentityFailure(tid, error, Logger.Text("打开线程", "open thread"));
                return;
            }
            try
            {
                long creation, exit, kernel, user;
                int owner = Native.QueryThreadOwnerPid(handle);
                if (owner <= 0) { sample.RecordThreadIdentityFailure(tid, 0, Logger.Text("线程归属未知", "thread owner unknown")); return; }
                if (owner != sample.Pid) return;
                if (!Native.GetThreadTimes(handle, out creation, out exit, out kernel, out user))
                { sample.RecordThreadIdentityFailure(tid, Marshal.GetLastWin32Error(), Logger.Text("读取线程创建时间", "read thread creation time")); return; }
                if (exit != 0) return;
                if (creation <= 0) { sample.RecordThreadIdentityFailure(tid, 0, Logger.Text("线程创建时间未知", "thread creation time unknown")); return; }
                sample.Threads[tid] = creation;
                sample.RecordThreadCpuBefore(tid, creation, kernel, user, Stopwatch.GetTimestamp());
            }
            finally { Native.CloseHandle(handle); }
        }

        private static bool VerifyRelationIdentity(FrameCoordinationSample sample, int tid, long expectedCreation)
        {
            IntPtr handle = Native.OpenThread(2048, false, tid);
            if (handle == IntPtr.Zero) return false;
            try
            {
                long creation, exit, kernel, user;
                if (Native.QueryThreadOwnerPid(handle) != sample.Pid) return false;
                long readBefore = Stopwatch.GetTimestamp();
                if (!Native.GetThreadTimes(handle, out creation, out exit, out kernel, out user)
                    || creation != expectedCreation || creation <= 0) return false;
                sample.RecordThreadCpuAfter(tid, creation, kernel, user, readBefore);
                return exit == 0;
            }
            finally { Native.CloseHandle(handle); }
        }
    }
}
