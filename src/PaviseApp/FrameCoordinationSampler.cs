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
        private struct Property
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

            internal volatile bool Invalid;

            internal FrameCaptureIssue Fault;
            internal string FaultDetail;

            internal IntPtr FlagsName;

            internal IntPtr DxgiStreamName, D3d9StreamName;

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
            // 窗口边界可截断一次提交，每线程每端仅允许一次；真实丢事件仍使样本无效。
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
                    // 仅豁免紧贴关窗的未完成提交。
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
                var property = new Property { Name = (ulong)(dxgi ? DxgiStreamName : D3d9StreamName).ToInt64(), Index = uint.MaxValue };
                if (property.Name == 0) { Sample.PresentSourceReadFailures++; streamUnknown = true; return 0; }
                uint status = TdhGetProperty(ref record, 0u, IntPtr.Zero, 1u, ref property, 8u, streamBuffer);
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
                if (issue != FrameCaptureIssue.None) Sample.PresentSelectionReason = "采集未通过核验：" + issue;
                else if (first == 0 || second == 0)
                    Sample.PresentSelectionReason = SelectionFailure(Presents[0], "前半") + "；" + SelectionFailure(Presents[1], "后半");
                else if (first != second) Sample.PresentSelectionReason = "两半主导线程不同：" + first + "/" + second;
                else Sample.PresentSelectionReason = "两半主导线程一致：" + first + "；每半至少16次且占比至少70%";
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
                if (total == 0) return half + "没有成功提交";
                if (maximum < 16) return half + "单线程最多" + maximum + "次，少于16次";
                if (maximum < total * 0.7) return half + "最大份额" + (100.0 * maximum / total).ToString("F1", CultureInfo.InvariantCulture) + "%，少于70%";
                return half + "主导门槛已满足";
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

            // ETW 的扩展处理器索引为 16 位，只为实际出现的编号分配状态，不按最大编号预留数组。
            private readonly Dictionary<int, SchedulerCpuState> cpuStates = new Dictionary<int, SchedulerCpuState>();

            private bool CheckCpuBudget()
            {
                long creation;
                long exit;
                long kernel;
                long user;
                if (!Native.GetThreadTimes(new IntPtr(-2), out creation, out exit, out kernel, out user))
                {
                    Sample.ConsumerCpuMs = -1;
                    return true;
                }
                // ETW 消费者持续排空事件；自身耗时仅记账，超过参考水位也不弃样。
                Sample.ConsumerCpuMs = (double)(kernel + user) / 10000.0;
                return true;
            }

            private bool AdmitRelevantEvent()
            {
                if (RelevantCount < int.MaxValue) RelevantCount++;
                return true;
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
                    if (!Invalid)
                    {
                        if ((Count == 1 || Count % 4096 == 0) && !CheckCpuBudget())
                        {
                            return;
                        }
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
                            if (PresenterOnly)
                            {
                                return;
                            }
                            // 大处理器编号必须使用 ProcessorIndex，不能截断后归到另一颗 CPU。
                            int bufferContextMask = ProcessorIndex(ref r);
                            if (bufferContextMask < 0 || bufferContextMask > ushort.MaxValue)
                            {
                                if (Relation != null) Relation.Invalidate("处理器编号超过采样器支持范围");
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
                            if (timeStamp < cpu.Last)
                            {
                                cpu.Running = 0;
                                if (Relation != null) Relation.Invalidate("同一CPU事件时间倒序，状态重建弃用");
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
                                    if (Relation != null) Relation.Invalidate("CSwitch数据不足，状态重建弃用");
                                    break;
                                }
                                int int32 = Marshal.ReadInt32(r.UserData);
                                if (Relation != null)
                                    Relation.Switch(timeStamp, bufferContextMask, int32, Marshal.ReadInt32(r.UserData, 4), Marshal.ReadByte(r.UserData, 14));
                                bool isInSampleThreads = Sample.Threads.ContainsKey(int32);
                                cpu.Running = (isInSampleThreads ? int32 : 0);
                                break;
                            }
                            case 50:
                            {
                                if (Relation != null)
                                {
                                    if (r.UserDataLength < 8) Relation.Invalidate("ReadyThread数据不足，状态重建弃用");
                                    else Relation.Ready(timeStamp, Marshal.ReadInt32(r.UserData), Marshal.ReadByte(r.UserData, 6), bufferContextMask);
                                }
                                if (r.UserDataLength < 8 || ((uint)Marshal.ReadByte(r.UserData, 6) & (true ? 1u : 0u)) != 0)
                                {
                                    break;
                                }
                                int receiveValue2 = cpu.Running;
                                // 非零条目均由本次采样固定线程集中的 CSwitch 事件准入。
                                if (FixedThreadSet && receiveValue2 == 0) break;
                                int int322 = Marshal.ReadInt32(r.UserData);
                                if (receiveValue2 != int322 && Sample.Threads.ContainsKey(int322) && (FixedThreadSet || Sample.Threads.ContainsKey(receiveValue2)) && AdmitRelevantEvent())
                                {
                                    long key = ((long)int322 << 32) | (uint)receiveValue2;
                                    Dictionary<long, int> edges = Edges[receiveValue];
                                    int value;
                                    edges.TryGetValue(key, out value);
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
                            // DXGI 还会发送交换链创建等事件；只有已识别的 Present 边界才能影响来源核验。
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
                                    uint flagsStatus = TdhGetProperty(ref r, 0u, IntPtr.Zero, 1u, ref property2, 4u, flagBuffer);
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
                                            { source = new PresentStreamEvidence(); streams.Add(stream, source); }
                                            int submitted;
                                            source.Counts[receiveValue].TryGetValue(threadId, out submitted);
                                            source.Counts[receiveValue][threadId] = submitted + 1;
                                            source.Successes++;
                                        }
                                        Sample.PresentStreamCount = PresentStreamCount;
                                        Sample.PresentStreamsKnown = !streamUnknown && PresentStreamCount > 0;
                                    }
                                    if (Relation != null) Relation.SuccessfulPresent(threadId, timeStamp);
                                }
                                pendingStreams.Remove(threadId);
                            }
                            else if (id == (isDxgi ? 43 : 2))
                            {
                                // 显式 TEST 不提交画面；其结束事件不是缺少开始边界的未知提交。
                                if (pending.TryGetValue(threadId, out value2) && value2 == -(isDxgi ? 42 : 1))
                                { pending.Remove(threadId); pendingStreams.Remove(threadId); }
                                else if (Sample.LayoutObservation)
                                {
                                    // 仅豁免该线程紧贴开窗的首个孤立结束事件。
                                    if (!presentStarted.Contains(threadId) && timeStamp >= Begin && timeStamp - Begin <= PresentEdgeTicks && edgeStops.Add(threadId))
                                        Sample.EdgePresentStops++;
                                    else { Sample.UnmatchedPresentStops++; streamUnknown = true; }
                                }
                            }
                        }
                    }
                }
                catch (OutOfMemoryException)
                {
                    Fault = FrameCaptureIssue.CaptureFailed;
                    FaultDetail = "采集事件存储分配失败";
                    Invalid = true;
                }
                catch
                {
                    Fault = FrameCaptureIssue.DecodeFailed;
                    Invalid = true;
                }
            }
        }

        // 保留历史诊断参考值；固定采集窗口中的事件量和累计 CPU 不再充当失败条件。
        internal const int MaxRelevantEvents = 750000;

        internal const int MaxSystemEvents = 1000000;

        internal const int CpuCheckInterval = 4096;

        internal const double MaxCaptureCpuMs = 500.0;

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

        internal static int StablePartnerCount(Dictionary<long, int>[] edges, int presenter)
        {
            if (edges == null || edges.Length != 2 || edges[0] == null || edges[1] == null || presenter <= 0) return 0;
            int count = 0;
            foreach (var entry in edges[0])
            {
                int target = (int)(entry.Key >> 32), source = (int)entry.Key;
                if (target != presenter || source <= 0 || source == presenter || entry.Value < 3) continue;
                long reverse = ((long)source << 32) | (uint)presenter;
                int firstReverse, secondForward, secondReverse;
                if (edges[0].TryGetValue(reverse, out firstReverse) && firstReverse >= 3
                    && edges[1].TryGetValue(entry.Key, out secondForward) && secondForward >= 3
                    && edges[1].TryGetValue(reverse, out secondReverse) && secondReverse >= 3) count++;
            }
            return count;
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
        {
            double captureThreadCpuStart = CaptureThreadCpuMs();
            FrameCoordinationSample frameCoordinationSample = new FrameCoordinationSample();
            frameCoordinationSample.Pid = pid;
            frameCoordinationSample.Birth = birth;
            frameCoordinationSample.PresenterOnly = presenterOnly;
            // 来源读取取决于采集用途，须在消费者启动前冻结。
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
                        foreach (ProcessThread thread in process.Threads)
                        {
                            if (!alive()) return frameCoordinationSample2.Reject(FrameCaptureIssue.Canceled);
                            ReadInitialThreadIdentity(frameCoordinationSample2, thread.Id);
                        }
                    }
                }
                if (!alive()) return frameCoordinationSample2.Reject(FrameCaptureIssue.Canceled);
                processStateResult = NativeFrameThreads.ProcessState(pid, birth);
                if (processStateResult != 0)
                    return frameCoordinationSample2.Reject(processStateResult > 0 ? FrameCaptureIssue.TargetChanged : FrameCaptureIssue.TargetUnavailable);
                evidence2.FixedThreadSet = !presenterOnly;
                evidence2.FlagsName = Marshal.StringToHGlobalUni("Flags");
                evidence2.DxgiStreamName = Marshal.StringToHGlobalUni("pIDXGISwapChain");
                evidence2.D3d9StreamName = Marshal.StringToHGlobalUni("pSwapchain");
                evidence2.Begin = Stopwatch.GetTimestamp();
                evidence2.Half = evidence2.Begin + Stopwatch.Frequency * 8 / 5;
                evidence2.End = evidence2.Begin + Stopwatch.Frequency * 16 / 5;
                if (!presenterOnly)
                    evidence2.Relation = new ThreadRelationCollector(frameCoordinationSample2.Threads, evidence2.Begin,
                        evidence2.Half, evidence2.End, Stopwatch.Frequency);
                if (!frameCoordinationTrace.Start(pid, evidence2.Receive, presenterOnly))
                {
                    frameCoordinationSample2.TraceCode = frameCoordinationTrace.Error;
                    frameCoordinationSample2.TraceStage = frameCoordinationTrace.Stage;
                    frameCoordinationSample2.TraceFailureDetail = frameCoordinationTrace.FailureDetail;
                    return frameCoordinationSample2.Reject(FrameCaptureIssue.TraceUnavailable, frameCoordinationSample2.TraceFailureDetail);
                }
                while (Stopwatch.GetTimestamp() < evidence2.End && alive() && !evidence2.Invalid)
                {
                    Thread.Sleep(100);
                }
                bool stopped = frameCoordinationTrace.Stop();
                frameCoordinationSample2.CpuMs = frameCoordinationTrace.CpuMs;
                frameCoordinationSample2.TraceCode = frameCoordinationTrace.Error;
                frameCoordinationSample2.TraceStage = frameCoordinationTrace.Stage;
                if (frameCoordinationTrace.Error != 0) frameCoordinationSample2.TraceFailureDetail = frameCoordinationTrace.FailureDetail;
                frameCoordinationSample2.LostEvents = frameCoordinationTrace.Lost;
                frameCoordinationSample2.Events = evidence2.RelevantCount;
                frameCoordinationSample2.SystemEvents = evidence2.Count;
                frameCoordinationSample2.SchedulerSwitches = evidence2.SchedulerSwitches;
                FrameCaptureIssue frameCaptureIssue = CaptureIssue(alive(), NativeFrameThreads.ProcessState(pid, birth), stopped, frameCoordinationTrace.Drained, frameCoordinationTrace.Error, frameCoordinationTrace.Lost, evidence2.Fault, frameCoordinationSample2.CpuMs);
                int presentTidValue = frameCoordinationTrace.Drained ? Dominant(evidence2.Presents[0], 16, 0.7) : 0;
                int dominantResult = frameCoordinationTrace.Drained ? Dominant(evidence2.Presents[1], 16, 0.7) : 0;
                if (frameCoordinationTrace.Drained)
                {
                    evidence2.CompletePresentStreams();
                    evidence2.CompletePresentDiagnostics(presentTidValue, dominantResult, frameCaptureIssue);
                }
                var processingPacer = new CaptureProcessingPacer(CaptureThreadCpuMs,
                    delegate { return Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency; },
                    delegate { Thread.Sleep(1); frameCoordinationSample2.ProcessingYields++; });
                if (evidence2.Relation != null)
                    frameCoordinationSample2.Relation = !frameCoordinationTrace.Drained
                        ? ThreadRelationSnapshot.Unavailable(pid, birth, evidence2.Begin, evidence2.End, Stopwatch.Frequency,
                            frameCoordinationSample2.LostEvents, "事件消费者尚未退出，保留现场并弃用关系摘要")
                        : evidence2.Relation.Build(frameCoordinationSample2,
                            presentTidValue == dominantResult ? presentTidValue : 0, evidence2.Edges, frameCaptureIssue,
                            delegate(int tid, long created) { return VerifyRelationIdentity(frameCoordinationSample2, tid, created); }, workloadObservation,
                            delegate
                            {
                                if (!alive())
                                {
                                    frameCoordinationSample2.Reject(FrameCaptureIssue.Canceled);
                                    return false;
                                }
                                processingPacer.Continue();
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
                frameCoordinationSample2.StablePartnerCount = StablePartnerCount(evidence2.Edges, presentTidValue);
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
                if (frameCoordinationTrace.Drained && evidence2.FlagsName != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(evidence2.FlagsName);
                    if (evidence2.DxgiStreamName != IntPtr.Zero) Marshal.FreeHGlobal(evidence2.DxgiStreamName);
                    if (evidence2.D3d9StreamName != IntPtr.Zero) Marshal.FreeHGlobal(evidence2.D3d9StreamName);
                }
                CompleteCaptureCpu(frameCoordinationSample2, frameCoordinationTrace.CpuMs, captureThreadCpuStart, CaptureThreadCpuMs());
            }
        }

        private static double CaptureThreadCpuMs()
        {
            long creation, exit, kernel, user;
            return Native.GetThreadTimes(new IntPtr(-2), out creation, out exit, out kernel, out user)
                ? (kernel + user) / 10000.0 : double.NaN;
        }

        // CPU 统计涵盖事件消费、建图、身份核验和收尾；超过参考值仍处理同一完整样本。
        internal static void CompleteCaptureCpu(FrameCoordinationSample sample, double consumerCpu, double callerBefore, double callerAfter)
        {
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
            IntPtr handle = Native.OpenThread(2048, false, tid);
            if (handle == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                // 枚举后已销毁的线程不参与采集；其他打开失败仍计为身份未核验。
                if (error != 87) sample.RecordThreadIdentityFailure(tid, error, "打开线程");
                return;
            }
            try
            {
                long creation, exit, kernel, user;
                int owner = Native.QueryThreadOwnerPid(handle);
                if (owner <= 0) { sample.RecordThreadIdentityFailure(tid, 0, "线程归属未知"); return; }
                if (owner != sample.Pid) return;
                if (!Native.GetThreadTimes(handle, out creation, out exit, out kernel, out user))
                { sample.RecordThreadIdentityFailure(tid, Marshal.GetLastWin32Error(), "读取线程创建时间"); return; }
                if (exit != 0) return;
                if (creation <= 0) { sample.RecordThreadIdentityFailure(tid, 0, "线程创建时间未知"); return; }
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
