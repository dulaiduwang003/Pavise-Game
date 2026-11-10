using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
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

            internal volatile bool Invalid;

            internal FrameCaptureIssue Fault;

            internal IntPtr FlagsName;

            private readonly byte[] flagBuffer = new byte[4];

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

            private readonly int[] running = new int[256];

            private readonly long[] last = new long[256];

            private bool CheckCpuBudget()
            {
                long creation;
                long exit;
                long kernel;
                long user;
                if (!Native.GetThreadTimes(new IntPtr(-2), out creation, out exit, out kernel, out user))
                {
                    Fault = FrameCaptureIssue.CaptureFailed;
                    Invalid = true;
                    return false;
                }
                double ratio = (double)(kernel + user) / 10000.0;
                if (ratio <= 500.0)
                {
                    return true;
                }
                Fault = FrameCaptureIssue.CpuBudget;
                Invalid = true;
                return false;
            }

            private bool AdmitRelevantEvent()
            {
                if (++RelevantCount <= 750000)
                {
                    return true;
                }
                Fault = FrameCaptureIssue.EventBudget;
                Invalid = true;
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
                    if (++Count > 1000000)
                    {
                        Fault = FrameCaptureIssue.SystemEventBudget;
                        Invalid = true;
                    }
                    else
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
                        int sampleHalf = ((timeStamp >= Half) ? 1 : 0);
                        int threadId = (int)r.EventHeader.ThreadId;
                        Guid providerId = r.EventHeader.ProviderId;
                        if (providerId == KernelThread)
                        {
                            if (PresenterOnly)
                            {
                                return;
                            }
                            int bufferContextMask = (int)(r.BufferContext & 0xFF);
                            int opcode = r.EventHeader.EventDescriptor.Opcode;
                            if (timeStamp < last[bufferContextMask])
                            {
                                running[bufferContextMask] = 0;
                                return;
                            }
                            last[bufferContextMask] = timeStamp;
                            switch (opcode)
                            {
                            case 36:
                            {
                                SchedulerSwitches++;
                                if (r.UserDataLength < 24)
                                {
                                    running[bufferContextMask] = 0;
                                    break;
                                }
                                int runningTid = Marshal.ReadInt32(r.UserData);
                                bool isInSampleThreads = Sample.Threads.ContainsKey(runningTid);
                                running[bufferContextMask] = (isInSampleThreads ? runningTid : 0);
                                break;
                            }
                            case 50:
                            {
                                if (r.UserDataLength < 8 || ((uint)Marshal.ReadByte(r.UserData, 6) & ((1u))) != 0)
                                {
                                    break;
                                }
                                int previousRunningTid = running[bufferContextMask];
                                // 非零条目均由本次采样固定线程集中的 CSwitch 事件准入。
                                if (FixedThreadSet && previousRunningTid == 0) break;
                                int readyTid = Marshal.ReadInt32(r.UserData);
                                if (previousRunningTid != readyTid && Sample.Threads.ContainsKey(readyTid) && (FixedThreadSet || Sample.Threads.ContainsKey(previousRunningTid)) && AdmitRelevantEvent())
                                {
                                    long key = ((long)readyTid << 32) | (uint)previousRunningTid;
                                    Dictionary<long, int> edges = Edges[sampleHalf];
                                    int edge;
                                    bool known = edges.TryGetValue(key, out edge);
                                    if (edges.Count >= 2048 && !known)
                                    {
                                        Fault = FrameCaptureIssue.EdgeBudget;
                                        Invalid = true;
                                    }
                                    else
                                    {
                                        edges[key] = edge + 1;
                                    }
                                }
                                break;
                            }
                            }
                        }
                        else
                        {
                            if (r.EventHeader.ProcessId != Sample.Pid || (!PresenterOnly && !Sample.Threads.ContainsKey(threadId)))
                            {
                                return;
                            }
                            bool isDxgi = providerId == Dxgi;
                            bool isD3d9 = providerId == D3d9;
                            if ((!isDxgi && !isD3d9) || r.EventHeader.EventDescriptor.Version != 0)
                            {
                                return;
                            }
                            int id = r.EventHeader.EventDescriptor.Id;
                            if ((id != ((!isDxgi) ? 1 : 42) && id != (isDxgi ? 43 : 2)) || !AdmitRelevantEvent())
                            {
                                return;
                            }
                            int currentPending;
                            if (id == ((!isDxgi) ? 1 : 42))
                            {
                                if (isDxgi)
                                {
                                    Property currentProperty = default(Property);
                                    currentProperty.Name = (ulong)FlagsName.ToInt64();
                                    currentProperty.Index = uint.MaxValue;
                                    if (TdhGetProperty(ref r, 0u, IntPtr.Zero, 1u, ref currentProperty, 4u, flagBuffer) != 0 || (BitConverter.ToUInt32(flagBuffer, 0) & ((1u))) != 0)
                                    {
                                        pending.Remove(threadId);
                                        return;
                                    }
                                }
                                pending[threadId] = id;
                            }
                            else if (id == (isDxgi ? 43 : 2) && pending.TryGetValue(threadId, out currentPending) && currentPending == ((!isDxgi) ? 1 : 42))
                            {
                                pending.Remove(threadId);
                                if (r.UserDataLength >= 4 && Marshal.ReadInt32(r.UserData) == 0)
                                {
                                    int Present;
                                    Presents[sampleHalf].TryGetValue(threadId, out Present);
                                    Presents[sampleHalf][threadId] = Present + 1;
                                }
                            }
                        }
                    }
                }
                catch
                {
                    Fault = FrameCaptureIssue.DecodeFailed;
                    Invalid = true;
                }
            }
        }

        internal const int MaxRelevantEvents = 750000;

        internal const int MaxSystemEvents = 1000000;

        internal const int CpuCheckInterval = 4096;

        internal const double MaxCaptureCpuMs = 500.0;

        internal static readonly Guid Dxgi = new Guid("ca11c036-0102-4a2d-a6ad-f03cfed5d3c9");

        internal static readonly Guid D3d9 = new Guid("783aca0a-790e-4d7f-8451-aa850511c6b9");

        internal static readonly Guid KernelThread = new Guid("3d6fa8d1-fe05-11d0-9dda-00c04fd7ba7c");

        [DllImport("tdh.dll")]
        private static extern uint TdhGetProperty(ref Native.EventRecord record, uint contextCount, IntPtr context, uint count, ref Property property, uint bytes, byte[] data);

        internal static int Dominant(Dictionary<int, int> counts, int minimum, double share)
        {
            int totalSamples = 0;
            int result = 0;
            int dominantSamples = 0;
            foreach (KeyValuePair<int, int> count in counts)
            {
                totalSamples += count.Value;
                if (count.Value > dominantSamples)
                {
                    dominantSamples = count.Value;
                    result = count.Key;
                }
            }
            if (dominantSamples < minimum || !((double)dominantSamples >= (double)totalSamples * share))
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
            if (cpuMs > 500.0)
            {
                return FrameCaptureIssue.CpuBudget;
            }
            return FrameCaptureIssue.None;
        }

        internal static FrameCoordinationSample Capture(int pid, long birth, Func<bool> alive)
        {
            return Capture(pid, birth, alive, false);
        }

        private static bool AdmitPresenter(FrameCoordinationSample sample, int tid)
        {
            IntPtr threadHandle = Native.OpenThread(2048, false, tid);
            if (threadHandle == IntPtr.Zero)
            {
                return false;
            }
            try
            {
                long creation;
                long exit;
                long kernel;
                long user;
                if (Native.QueryThreadOwnerPid(threadHandle) != sample.Pid || !Native.GetThreadTimes(threadHandle, out creation, out exit, out kernel, out user) || exit != 0 || creation <= 0)
                {
                    return false;
                }
                sample.Threads[tid] = creation;
                return true;
            }
            finally
            {
                Native.CloseHandle(threadHandle);
            }
        }

        internal static FrameCoordinationSample Capture(int pid, long birth, Func<bool> alive, bool presenterOnly)
        {
            FrameCoordinationSample currentFrameCoordinationSample = new FrameCoordinationSample();
            currentFrameCoordinationSample.Pid = pid;
            currentFrameCoordinationSample.Birth = birth;
            currentFrameCoordinationSample.PresenterOnly = presenterOnly;
            currentFrameCoordinationSample.Reject(FrameCaptureIssue.InsufficientEvidence);
            Evidence currentEvidence = new Evidence();
            currentEvidence.Sample = currentFrameCoordinationSample;
            currentEvidence.PresenterOnly = presenterOnly;
            FrameCoordinationTrace frameCoordinationTrace = new FrameCoordinationTrace();
            try
            {
                if (!alive())
                {
                    return currentFrameCoordinationSample.Reject(FrameCaptureIssue.Canceled);
                }
                int processStateResult = NativeFrameThreads.ProcessState(pid, birth);
                if (processStateResult != 0)
                {
                    return currentFrameCoordinationSample.Reject((processStateResult > 0) ? FrameCaptureIssue.TargetChanged : FrameCaptureIssue.TargetUnavailable);
                }
                if (!presenterOnly)
                {
                    using (Process process = Process.GetProcessById(pid))
                    {
                        foreach (ProcessThread thread in process.Threads)
                        {
                            if (currentFrameCoordinationSample.Threads.Count >= 512)
                            {
                                return currentFrameCoordinationSample.Reject(FrameCaptureIssue.ThreadBudget);
                            }
                            IntPtr threadHandle = Native.OpenThread(2048, false, thread.Id);
                            if (threadHandle == IntPtr.Zero)
                            {
                                continue;
                            }
                            try
                            {
                                long creation;
                                long exit;
                                long kernel;
                                long user;
                                if (Native.QueryThreadOwnerPid(threadHandle) == pid && Native.GetThreadTimes(threadHandle, out creation, out exit, out kernel, out user) && exit == 0)
                                {
                                    currentFrameCoordinationSample.Threads[thread.Id] = creation;
                                }
                            }
                            finally
                            {
                                Native.CloseHandle(threadHandle);
                            }
                        }
                    }
                }
                currentEvidence.FixedThreadSet = !presenterOnly;
                currentEvidence.FlagsName = Marshal.StringToHGlobalUni("Flags");
                currentEvidence.Begin = Stopwatch.GetTimestamp();
                currentEvidence.Half = currentEvidence.Begin + Stopwatch.Frequency * 8 / 5;
                currentEvidence.End = currentEvidence.Begin + Stopwatch.Frequency * 16 / 5;
                if (!frameCoordinationTrace.Start(pid, currentEvidence.Receive, presenterOnly))
                {
                    currentFrameCoordinationSample.TraceCode = frameCoordinationTrace.Error;
                    return currentFrameCoordinationSample.Reject(FrameCaptureIssue.TraceUnavailable);
                }
                while (Stopwatch.GetTimestamp() < currentEvidence.End && alive() && !currentEvidence.Invalid)
                {
                    Thread.Sleep(100);
                }
                bool stopped = frameCoordinationTrace.Stop();
                currentFrameCoordinationSample.CpuMs = frameCoordinationTrace.CpuMs;
                currentFrameCoordinationSample.TraceCode = frameCoordinationTrace.Error;
                currentFrameCoordinationSample.LostEvents = frameCoordinationTrace.Lost;
                currentFrameCoordinationSample.Events = currentEvidence.RelevantCount;
                currentFrameCoordinationSample.SystemEvents = currentEvidence.Count;
                currentFrameCoordinationSample.SchedulerSwitches = currentEvidence.SchedulerSwitches;
                FrameCaptureIssue frameCaptureIssue = CaptureIssue(alive(), NativeFrameThreads.ProcessState(pid, birth), stopped, frameCoordinationTrace.Drained, frameCoordinationTrace.Error, frameCoordinationTrace.Lost, currentEvidence.Fault, currentFrameCoordinationSample.CpuMs);
                if (frameCaptureIssue != 0)
                {
                    return currentFrameCoordinationSample.Reject(frameCaptureIssue);
                }
                int presentTidValue = Dominant(currentEvidence.Presents[0], 16, 0.7);
                int dominantResult = Dominant(currentEvidence.Presents[1], 16, 0.7);
                if (presentTidValue == 0 || presentTidValue != dominantResult)
                {
                    return currentFrameCoordinationSample;
                }
                if (presenterOnly && !AdmitPresenter(currentFrameCoordinationSample, presentTidValue))
                {
                    return currentFrameCoordinationSample;
                }
                currentFrameCoordinationSample.PresentTid = presentTidValue;
                currentFrameCoordinationSample.WorkerTid = SelectWorker(currentEvidence, presentTidValue);
                currentFrameCoordinationSample.Valid = true;
                currentFrameCoordinationSample.Issue = FrameCaptureIssue.None;
                currentFrameCoordinationSample.Detail = (presenterOnly ? "presenter confirmed; presenter-only capture" : ((currentFrameCoordinationSample.WorkerTid == 0) ? "presenter confirmed; no stable worker" : "presenter and worker confirmed"));
                return currentFrameCoordinationSample;
            }
            catch (ArgumentException ex)
            {
                if (!alive() || NativeFrameThreads.ProcessState(pid, birth) > 0)
                {
                    return currentFrameCoordinationSample.Reject(FrameCaptureIssue.TargetChanged);
                }
                return currentFrameCoordinationSample.Reject(FrameCaptureIssue.CaptureFailed, ex.GetType().Name + ": " + ex.Message);
            }
            catch (Win32Exception ex2)
            {
                return currentFrameCoordinationSample.Reject(FrameCaptureIssue.TargetUnavailable, "code=" + ex2.NativeErrorCode);
            }
            catch (Exception ex3)
            {
                return currentFrameCoordinationSample.Reject(FrameCaptureIssue.CaptureFailed, ex3.GetType().Name + ": " + ex3.Message);
            }
            finally
            {
                currentEvidence.Invalid = true;
                frameCoordinationTrace.Stop();
                if (frameCoordinationTrace.Drained && currentEvidence.FlagsName != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(currentEvidence.FlagsName);
                }
            }
        }
    }
}
