using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace PaviseApp
{
    internal enum FrameCaptureIssue
    {
        None, InsufficientEvidence, Canceled, TargetChanged, TargetUnavailable,
        ThreadBudget, TraceUnavailable, TraceStopFailed, EventsLost,
        EventBudget, EdgeBudget, DecodeFailed, CpuBudget, CaptureFailed, SystemEventBudget
    }

    internal sealed class FrameCoordinationSample
    {
        internal int Pid, PresentTid, WorkerTid;
        internal long Birth;
        internal bool Valid, NoChangeNeeded, PresenterOnly;
        internal string Detail;
        internal double CpuMs;
        internal FrameCaptureIssue Issue;
        internal int TraceCode, Events, SystemEvents, SchedulerSwitches;
        internal uint LostEvents;
        internal Dictionary<int, long> Threads = new Dictionary<int, long>();
        internal FrameCoordinationSample Reject(FrameCaptureIssue issue, string extra = null)
        {
            Valid = false; Issue = issue;
            Detail = IssueText(issue) + (string.IsNullOrEmpty(extra) ? "" : " | " + extra);
            return this;
        }
        internal static string IssueText(FrameCaptureIssue issue)
        {
            switch (issue)
            {
                case FrameCaptureIssue.Canceled: return AutomaticPolicy.Text("前台或守护状态变化，本轮采样取消", "Focus or guard state changed; capture canceled");
                case FrameCaptureIssue.TargetChanged: return AutomaticPolicy.Text("游戏进程已退出或切换，本轮采样取消", "Game process exited or changed; capture canceled");
                case FrameCaptureIssue.TargetUnavailable: return AutomaticPolicy.Text("游戏进程身份暂时无法核验", "Game process identity could not be verified");
                case FrameCaptureIssue.ThreadBudget: return AutomaticPolicy.Text("游戏线程数量超过采样预算", "Game thread count exceeds the capture budget");
                case FrameCaptureIssue.TraceUnavailable: return AutomaticPolicy.Text("系统暂不允许线程事件采样", "Thread event capture is unavailable");
                case FrameCaptureIssue.TraceStopFailed: return AutomaticPolicy.Text("线程事件采样未正常结束", "Thread event capture did not stop cleanly");
                case FrameCaptureIssue.EventsLost: return AutomaticPolicy.Text("采样事件丢失，本轮样本弃用", "Capture events were lost; sample discarded");
                case FrameCaptureIssue.EventBudget: return AutomaticPolicy.Text("游戏相关事件数量超过采样预算，本轮样本弃用", "Game event count exceeds the capture budget; sample discarded");
                case FrameCaptureIssue.SystemEventBudget: return AutomaticPolicy.Text("系统事件数量超过采样预算，本轮样本弃用", "System event count exceeds the capture budget; sample discarded");
                case FrameCaptureIssue.EdgeBudget: return AutomaticPolicy.Text("线程关联数量超过采样预算，本轮样本弃用", "Thread dependency count exceeds the capture budget; sample discarded");
                case FrameCaptureIssue.DecodeFailed: return AutomaticPolicy.Text("线程事件解析异常，本轮样本弃用", "Thread event decoding failed; sample discarded");
                case FrameCaptureIssue.CpuBudget: return AutomaticPolicy.Text("采样 CPU 耗时超过预算，本轮样本弃用", "Capture CPU time exceeds the budget; sample discarded");
                case FrameCaptureIssue.CaptureFailed: return AutomaticPolicy.Text("线程采样发生异常", "Thread capture failed");
                default: return AutomaticPolicy.Text("暂未取得稳定线程证据", "Stable thread evidence is not available yet");
            }
        }
    }

    internal sealed class FrameCoordinationTrace
    {
        internal const string TraceKey = "FrameCoordinationTraceV1";
        private static readonly object ledgerGate = new object();
        private static FrameCoordinationTrace activeCapture;
        private readonly object memory = new object();
        private readonly object cleanup = new object();
        private IntPtr properties, loggerName;
        private ulong session, consumer = Native.InvalidProcessTraceHandle;
        private Native.EventRecordCallback callback;
        private Thread pump;
        private bool disposeRequested;
        private readonly string name = "Pavise.FrameCoordination." + Process.GetCurrentProcess().Id + "." + Guid.NewGuid().ToString("N");
        internal int Error;
        internal uint Lost;
        internal int PumpTid;
        internal double CpuMs;
        internal bool Drained { get { return pump == null || !pump.IsAlive; } }
        internal static bool HasResidue
        {
            get
            {
                lock (ledgerGate)
                {
                    string receipt;
                    return activeCapture != null || !Settings.TryLoadStr(TraceKey, out receipt) || receipt.Length > 0;
                }
            }
        }
        [DllImport("kernel32.dll")] private static extern ushort GetActiveProcessorGroupCount();
        [StructLayout(LayoutKind.Sequential)] private struct Filter { internal ulong Ptr; internal uint Size, Type; }
#if PAVISE_SELFTEST
        internal static Func<ulong, string, IntPtr, uint, int> ControlForTest;
        internal static Func<ulong, int> CloseForTest;
        internal static int StopWaitMsForTest = 3000;
        internal static FrameCoordinationTrace CleanupFixtureForTest(Thread runningPump = null)
        {
            var trace = new FrameCoordinationTrace();
            trace.properties = Props(trace.name, true); trace.session = 11; trace.consumer = 22; trace.pump = runningPump;
            Settings.SaveStr(TraceKey, trace.name);
            lock (ledgerGate) activeCapture = trace;
            return trace;
        }
#endif
        private static int Control(ulong handle, string traceName, IntPtr props, uint command)
        {
#if PAVISE_SELFTEST
            if (ControlForTest != null) return ControlForTest(handle, traceName, props, command);
#endif
            return Native.ControlTrace(handle, traceName, props, command);
        }
        private static int CloseConsumer(ulong handle)
        {
#if PAVISE_SELFTEST
            if (CloseForTest != null) return CloseForTest(handle);
#endif
            return Native.CloseTrace(handle);
        }

        private static IntPtr Props(string name, bool start)
        { return Props(name, start, false); }
        private static IntPtr Props(string name, bool start, bool presenterOnly)
        {
            int bytes = Marshal.SizeOf(typeof(Native.EventTraceProperties));
            byte[] text = System.Text.Encoding.Unicode.GetBytes(name + "\0");
            IntPtr p = Marshal.AllocHGlobal(bytes + text.Length);
            Marshal.Copy(new byte[bytes + text.Length], 0, p, bytes + text.Length);
            var value = new Native.EventTraceProperties { WnodeBufferSize = (uint)(bytes + text.Length),
                WnodeFlags = Native.WnodeFlagTracedGuid, WnodeClientContext = 1, WnodeGuid = Guid.NewGuid(),
                BufferSize = 64, MinimumBuffers = 8, MaximumBuffers = 32, FlushTimer = 1,
                LogFileMode = Native.EventTraceRealTimeMode | (presenterOnly ? 0u : 0x02000000u),
                EnableFlags = start && !presenterOnly ? 0x0812u : 0u,
                LoggerNameOffset = (uint)bytes };
            Marshal.StructureToPtr(value, p, false); Marshal.Copy(text, 0, IntPtr.Add(p, bytes), text.Length); return p;
        }
        internal bool Start(int pid, Native.EventRecordCallback receive)
        { return Start(pid, receive, false); }
        internal bool Start(int pid, Native.EventRecordCallback receive, bool presenterOnly)
        {
            if (!presenterOnly && GetActiveProcessorGroupCount() != 1) { Error = 50; return false; }
            lock (ledgerGate)
            {
                if (activeCapture != null) { Error = 170; return false; }
                string previous, written;
                if (!Settings.TryLoadStr(TraceKey, out previous) || previous.Length != 0
                    || !Settings.SaveStr(TraceKey, name) || !Settings.TryLoadStr(TraceKey, out written) || written != name) { Error = 5; return false; }
                activeCapture = this;
            }
            properties = Props(name, true, presenterOnly);
            Error = Native.StartTrace(out session, name, properties);
            if (Error != 0) { session = 0; Stop(); return false; }
            IntPtr pidPointer = Marshal.AllocHGlobal(4), filterPointer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Filter)));
            try
            {
                Marshal.WriteInt32(pidPointer, pid);
                Marshal.StructureToPtr(new Filter { Ptr = (ulong)pidPointer.ToInt64(), Size = 4, Type = 0x80000004 }, filterPointer, false);
                var parameters = new Native.EnableTraceParameters { Version = 2, EnableFilterDesc = filterPointer, FilterDescCount = 1 };
                Guid dxgi = FrameCoordinationSampler.Dxgi, d3d9 = FrameCoordinationSampler.D3d9;
                Error = Native.EnableTraceEx2(session, ref dxgi, 1, 5, 0x8000000000000002UL, 0, 0, ref parameters);
                if (Error == 0) Error = Native.EnableTraceEx2(session, ref d3d9, 1, 5, 0x8000000000000002UL, 0, 0, ref parameters);
            }
            finally { Marshal.FreeHGlobal(pidPointer); Marshal.FreeHGlobal(filterPointer); }
            if (Error != 0) { Stop(); return false; }
            callback = receive; loggerName = Marshal.StringToHGlobalUni(name);
            var log = new Native.EventTraceLogfile { LoggerName = loggerName,
                ProcessTraceMode = Native.ProcessTraceModeRealTime | Native.ProcessTraceModeEventRecord | 0x1000,
                EventRecordCallback = Marshal.GetFunctionPointerForDelegate(callback) };
            consumer = Native.OpenTrace(ref log);
            if (consumer == Native.InvalidProcessTraceHandle) { Error = Marshal.GetLastWin32Error(); Stop(); return false; }
            ulong opened = consumer;
            pump = new Thread(delegate() {
                PumpTid = Native.GetCurrentThreadId();
                try { int rc = Native.ProcessTrace(new[] { opened }, 1, IntPtr.Zero, IntPtr.Zero); if (rc != 0 && rc != 1223) Error = rc; }
                finally {
                    IntPtr thread = Native.OpenThread(Native.THREAD_QUERY_LIMITED_INFORMATION, false, PumpTid);
                    if (thread != IntPtr.Zero) { try { long b,e,k,u; if (Native.GetThreadTimes(thread,out b,out e,out k,out u)) CpuMs=(k+u)/10000.0; } finally { Native.CloseHandle(thread); } }
                    lock (memory) if (disposeRequested && session == 0 && consumer == Native.InvalidProcessTraceHandle) FreeMemory();
                }
            });
            pump.IsBackground = true; pump.Priority = ThreadPriority.BelowNormal; pump.Name = "Pavise.FrameEvents"; pump.Start(); return true;
        }
        internal bool Stop()
        { lock (cleanup) return StopLocked(); }
        private bool StopLocked()
        {
            int rc = 0;
            if (session != 0)
            {
                rc = Control(session, name, properties, Native.EventTraceControlStop);
                var value = (Native.EventTraceProperties)Marshal.PtrToStructure(properties, typeof(Native.EventTraceProperties));
                Lost = value.EventsLost + value.RealTimeBuffersLost;
                // 停止失败时保留会话句柄，后续清理继续重试。
                if (rc == 0 || rc == 4201 || rc == 234) session = 0;
            }
            if (consumer != Native.InvalidProcessTraceHandle)
            {
                int close = CloseConsumer(consumer);
                // 实时追踪返回关闭待完成仍需排空回调。
                if (close == 0 || close == 7007 || close == 6) consumer = Native.InvalidProcessTraceHandle;
                if (close != 0 && close != 7007) Error = close;
            }
            int waitMs = 3000;
#if PAVISE_SELFTEST
            waitMs = StopWaitMsForTest;
#endif
            bool joined = pump == null || !pump.IsAlive || pump.Join(waitMs);
            if (rc != 0 && rc != 4201) Error = rc;
            bool settled = session == 0 && consumer == Native.InvalidProcessTraceHandle && joined;
            bool cleared = false;
            if (settled)
                lock (ledgerGate)
                {
                    if (activeCapture == this) activeCapture = null;
                    cleared = ClearLedger(name);
                    if (!cleared) Error = 5;
                }
            lock (memory) { disposeRequested = true; if (settled) FreeMemory(); }
            return settled && cleared && Error == 0 && Lost == 0;
        }
        private static bool ClearLedger(string expected)
        {
            string current;
            if (!Settings.TryLoadStr(TraceKey, out current)) return false;
            if (current.Length == 0) return true;
            return current == expected && Settings.SaveStr(TraceKey, "")
                && Settings.TryLoadStr(TraceKey, out current) && current.Length == 0;
        }
        private void FreeMemory()
        {
            if (properties != IntPtr.Zero) { Marshal.FreeHGlobal(properties); properties = IntPtr.Zero; }
            if (loggerName != IntPtr.Zero) { Marshal.FreeHGlobal(loggerName); loggerName = IntPtr.Zero; }
            GC.KeepAlive(callback);
        }
        internal static bool Heal()
        {
            FrameCoordinationTrace pending;
            lock (ledgerGate) pending = activeCapture;
            if (pending != null)
            {
                // 持有 ledgerGate 时不能获取清理锁。
                if (!pending.Drained) return false;
                pending.Stop();
            }
            lock (ledgerGate)
            {
                if (activeCapture != null) return false;
                string name; if (!Settings.TryLoadStr(TraceKey, out name)) return false; if (name.Length == 0) return true;
                if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"\APavise\.FrameCoordination\.[0-9]+\.[0-9a-f]{32}\z")) return false;
                IntPtr p = Props(name, false);
                try { int rc = Control(0, name, p, 1); return (rc == 0 || rc == 4201 || rc == 234) && ClearLedger(name); }
                finally { Marshal.FreeHGlobal(p); }
            }
        }
    }

    internal static class FrameCoordinationSampler
    {
        internal const int MaxRelevantEvents = 750000, MaxSystemEvents = 1000000, CpuCheckInterval = 4096;
        internal const double MaxCaptureCpuMs = 500;
        internal static readonly Guid Dxgi = new Guid("ca11c036-0102-4a2d-a6ad-f03cfed5d3c9"), D3d9 = new Guid("783aca0a-790e-4d7f-8451-aa850511c6b9"), KernelThread = new Guid("3d6fa8d1-fe05-11d0-9dda-00c04fd7ba7c");
        [StructLayout(LayoutKind.Sequential)] private struct Property { internal ulong Name; internal uint Index, Reserved; }
        [DllImport("tdh.dll")] private static extern uint TdhGetProperty(ref Native.EventRecord record, uint contextCount, IntPtr context, uint count, ref Property property, uint bytes, byte[] data);
        internal sealed class Evidence
        {
            internal FrameCoordinationSample Sample;
            internal long Begin, End, Half;
            internal int Count, RelevantCount, SchedulerSwitches;
            internal bool PresenterOnly;
            internal volatile bool Invalid;
            internal FrameCaptureIssue Fault;
            internal IntPtr FlagsName;
            private readonly byte[] flagBuffer = new byte[4];
            private readonly Dictionary<int,int> pending = new Dictionary<int,int>();
            internal readonly Dictionary<int,int>[] Presents = { new Dictionary<int,int>(), new Dictionary<int,int>() };
            internal readonly Dictionary<long,int>[] Edges = { new Dictionary<long,int>(), new Dictionary<long,int>() };
            private readonly int[] running = new int[256];
            private readonly long[] last = new long[256];
#if PAVISE_SELFTEST
            internal Func<double> CpuMsForTest;
#endif
            private bool CheckCpuBudget()
            {
                double cpuMs;
#if PAVISE_SELFTEST
                if (CpuMsForTest != null) cpuMs = CpuMsForTest();
                else
#endif
                {
                    long b, e, k, u;
                    // 采样开销包含无关系统事件的解码时间。
                    if (!Native.GetThreadTimes(new IntPtr(-2), out b, out e, out k, out u))
                    { Fault = FrameCaptureIssue.CaptureFailed; Invalid = true; return false; }
                    cpuMs = (k + u) / 10000.0;
                }
                if (cpuMs <= MaxCaptureCpuMs) return true;
                Fault = FrameCaptureIssue.CpuBudget; Invalid = true; return false;
            }
            private bool AdmitRelevantEvent()
            {
                if (++RelevantCount <= MaxRelevantEvents) return true;
                Fault = FrameCaptureIssue.EventBudget; Invalid = true; return false;
            }
            internal void Receive(ref Native.EventRecord r)
            {
                try
                {
                    if (Invalid) return;
                    if (++Count > MaxSystemEvents)
                    { Fault = FrameCaptureIssue.SystemEventBudget; Invalid = true; return; }
                    if ((Count == 1 || Count % CpuCheckInterval == 0) && !CheckCpuBudget()) return;
                    long time = r.EventHeader.TimeStamp;
                    if (Invalid || time < Begin || time > End) return;
                    int phase = time < Half ? 0 : 1, tid = (int)r.EventHeader.ThreadId;
                    Guid provider = r.EventHeader.ProviderId;
                    if (provider == KernelThread)
                    {
                        if (PresenterOnly) return;
                        int cpu = (int)(r.BufferContext & 255), opcode = r.EventHeader.EventDescriptor.Opcode;
                        if (time < last[cpu]) { running[cpu] = 0; return; }
                        last[cpu] = time;
                        if (opcode == 36)
                        {
                            ++SchedulerSwitches;
                            if (r.UserDataLength < 24) { running[cpu] = 0; return; }
                            int next = Marshal.ReadInt32(r.UserData);
                            bool tracked = Sample.Threads.ContainsKey(next);
                            // 外部线程切换仍需清除发送者，避免生成过期依赖。
                            running[cpu] = tracked ? next : 0;
                            return;
                        }
                        if (opcode != 50 || r.UserDataLength < 8 || (Marshal.ReadByte(r.UserData,6) & 1) != 0) return;
                        int target = Marshal.ReadInt32(r.UserData), sender = running[cpu];
                        if (sender == target || !Sample.Threads.ContainsKey(target) || !Sample.Threads.ContainsKey(sender)) return;
                        if (!AdmitRelevantEvent()) return;
                        long edge = ((long)target << 32) | (uint)sender;
                        if (Edges[phase].Count >= 2048 && !Edges[phase].ContainsKey(edge)) { Fault = FrameCaptureIssue.EdgeBudget; Invalid = true; return; }
                        int count; Edges[phase].TryGetValue(edge, out count); Edges[phase][edge] = count+1; return;
                    }
                    if (r.EventHeader.ProcessId != Sample.Pid || !Sample.Threads.ContainsKey(tid)) return;
                    bool dxgi = provider == Dxgi, d3d = provider == D3d9;
                    if ((!dxgi && !d3d) || r.EventHeader.EventDescriptor.Version != 0) return;
                    int id = r.EventHeader.EventDescriptor.Id;
                    if (id != (dxgi ? 42 : 1) && id != (dxgi ? 43 : 2)) return;
                    if (!AdmitRelevantEvent()) return;
                    if (id == (dxgi ? 42 : 1))
                    {
                        if (dxgi)
                        {
                            var property = new Property { Name = (ulong)FlagsName.ToInt64(), Index = uint.MaxValue };
                            if (TdhGetProperty(ref r,0,IntPtr.Zero,1,ref property,4,flagBuffer) != 0 || (BitConverter.ToUInt32(flagBuffer,0)&1) != 0) { pending.Remove(tid); return; }
                        }
                        pending[tid] = id;
                    }
                    else if (id == (dxgi ? 43 : 2))
                    {
                        int start; if (!pending.TryGetValue(tid,out start) || start != (dxgi ? 42 : 1)) return;
                        pending.Remove(tid);
                        if (r.UserDataLength < 4 || Marshal.ReadInt32(r.UserData) != 0) return;
                        int count; Presents[phase].TryGetValue(tid,out count); Presents[phase][tid]=count+1;
                    }
                }
                catch { Fault = FrameCaptureIssue.DecodeFailed; Invalid = true; }
            }
        }
        internal static int Dominant(Dictionary<int,int> counts, int minimum, double share)
        {
            int total=0,best=0,max=0;
            foreach (var value in counts) { total+=value.Value; if(value.Value>max){max=value.Value;best=value.Key;} }
            return max >= minimum && max >= total * share ? best : 0;
        }
        private static Dictionary<int,int> ForPresenter(Dictionary<long,int> edges,int presenter)
        { var result=new Dictionary<int,int>(); foreach(var pair in edges) if((int)(pair.Key>>32)==presenter) result[(int)pair.Key]=pair.Value; return result; }
        internal static int SelectWorker(Evidence evidence,int presenter)
        {
            if (evidence.PresenterOnly) return 0;
            int a=Dominant(ForPresenter(evidence.Edges[0],presenter),12,.75),b=Dominant(ForPresenter(evidence.Edges[1],presenter),12,.75);
            return a!=0 && a==b ? a : 0;
        }
        internal static FrameCaptureIssue CaptureIssue(bool admitted, int identityState, bool stopped,
            bool drained, int traceCode, uint lost, FrameCaptureIssue fault, double cpuMs)
        {
            if (traceCode != 0 || !drained || (!stopped && lost == 0)) return FrameCaptureIssue.TraceStopFailed;
            if (lost != 0) return FrameCaptureIssue.EventsLost;
            if (fault == FrameCaptureIssue.DecodeFailed) return fault;
            if (!admitted)
                return fault == FrameCaptureIssue.SystemEventBudget || fault == FrameCaptureIssue.EventBudget
                    || fault == FrameCaptureIssue.EdgeBudget || fault == FrameCaptureIssue.CpuBudget ? fault : FrameCaptureIssue.Canceled;
            if (identityState > 0) return FrameCaptureIssue.TargetChanged;
            if (identityState < 0) return FrameCaptureIssue.TargetUnavailable;
            if (fault != FrameCaptureIssue.None) return fault;
            if (cpuMs > MaxCaptureCpuMs) return FrameCaptureIssue.CpuBudget;
            return FrameCaptureIssue.None;
        }
        internal static FrameCoordinationSample Capture(int pid,long birth,Func<bool> alive)
        { return Capture(pid, birth, alive, false); }
        internal static FrameCoordinationSample Capture(int pid,long birth,Func<bool> alive,bool presenterOnly)
        {
            var sample = new FrameCoordinationSample { Pid=pid,Birth=birth,PresenterOnly=presenterOnly };
            sample.Reject(FrameCaptureIssue.InsufficientEvidence);
            var evidence = new Evidence { Sample=sample,PresenterOnly=presenterOnly };
            var trace = new FrameCoordinationTrace();
            try
            {
                if (!alive()) return sample.Reject(FrameCaptureIssue.Canceled);
                int identity = NativeFrameThreads.ProcessState(pid,birth);
                if (identity != 0) return sample.Reject(identity > 0 ? FrameCaptureIssue.TargetChanged : FrameCaptureIssue.TargetUnavailable);
                using (Process process=Process.GetProcessById(pid))
                    foreach(ProcessThread t in process.Threads)
                    {
                        if(sample.Threads.Count>=512) return sample.Reject(FrameCaptureIssue.ThreadBudget);
                        IntPtr h=Native.OpenThread(Native.THREAD_QUERY_LIMITED_INFORMATION,false,t.Id);
                        if(h==IntPtr.Zero) continue;
                        try { long b,e,k,u; if(Native.QueryThreadOwnerPid(h)==pid && Native.GetThreadTimes(h,out b,out e,out k,out u) && e==0) sample.Threads[t.Id]=b; }
                        finally { Native.CloseHandle(h); }
                    }
                evidence.FlagsName=Marshal.StringToHGlobalUni("Flags");
                evidence.Begin=Stopwatch.GetTimestamp();evidence.Half=evidence.Begin+Stopwatch.Frequency*8/5;evidence.End=evidence.Begin+Stopwatch.Frequency*16/5;
                if (!trace.Start(pid,evidence.Receive,presenterOnly)) { sample.TraceCode = trace.Error; return sample.Reject(FrameCaptureIssue.TraceUnavailable); }
                while(Stopwatch.GetTimestamp()<evidence.End && alive() && !evidence.Invalid) Thread.Sleep(100);
                bool stopped=trace.Stop();sample.CpuMs=trace.CpuMs;
                sample.TraceCode=trace.Error; sample.LostEvents=trace.Lost;
                sample.Events=evidence.RelevantCount; sample.SystemEvents=evidence.Count; sample.SchedulerSwitches=evidence.SchedulerSwitches;
                FrameCaptureIssue issue = CaptureIssue(alive(), NativeFrameThreads.ProcessState(pid,birth),
                    stopped, trace.Drained, trace.Error, trace.Lost, evidence.Fault, sample.CpuMs);
                if (issue != FrameCaptureIssue.None) return sample.Reject(issue);
                int a=Dominant(evidence.Presents[0],16,.7), bWinner=Dominant(evidence.Presents[1],16,.7);
                if(a==0 || a!=bWinner) return sample;
                sample.PresentTid=a;
                sample.WorkerTid=SelectWorker(evidence,a);
                sample.Valid=true;sample.Issue=FrameCaptureIssue.None;
                sample.Detail=presenterOnly?"presenter confirmed; presenter-only capture":sample.WorkerTid==0?"presenter confirmed; no stable worker":"presenter and worker confirmed";
                return sample;
            }
            catch(ArgumentException error)
            {
                if (!alive() || NativeFrameThreads.ProcessState(pid,birth) > 0) return sample.Reject(FrameCaptureIssue.TargetChanged);
                return sample.Reject(FrameCaptureIssue.CaptureFailed, error.GetType().Name + ": " + error.Message);
            }
            catch(System.ComponentModel.Win32Exception error) { return sample.Reject(FrameCaptureIssue.TargetUnavailable, "code=" + error.NativeErrorCode); }
            catch(Exception error) { return sample.Reject(FrameCaptureIssue.CaptureFailed, error.GetType().Name + ": " + error.Message); }
            finally { evidence.Invalid=true; trace.Stop(); if(trace.Drained && evidence.FlagsName!=IntPtr.Zero) Marshal.FreeHGlobal(evidence.FlagsName); }
        }
    }
}
