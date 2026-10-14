using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;

namespace PaviseApp
{
    internal sealed class FrameThreadReceipt
    {
        internal int Pid, Tid, Original;
        internal long ProcessBirth, ThreadBirth;
        internal string Encode() { return Pid + "|" + ProcessBirth + "|" + Tid + "|" + ThreadBirth + "|" + Original; }
    }

    internal interface IFrameThreads
    {
        int Read(FrameThreadReceipt identity, out int priority);
        bool Set(FrameThreadReceipt identity, int expected, int desired);
    }

    internal sealed partial class NativeFrameThreads : IFrameThreads, IFrameThreadMemory
    {
        internal static int ProcessState(int pid, long birth)
        {
            IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return Marshal.GetLastWin32Error() == 87 ? 1 : -1;
            try { return Native.QueryProcessIdentityState(h, birth); }
            finally { Native.CloseHandle(h); }
        }
        private static int ThreadState(IntPtr h, FrameThreadReceipt id, out int priority)
        {
            priority = 0;
            int owner = Native.QueryThreadOwnerPid(h);
            if (owner < 0) return -1;
            if (owner != id.Pid) return 1;
            long born, exited, kernel, user;
            if (!Native.GetThreadTimes(h, out born, out exited, out kernel, out user)) return -1;
            if (born != id.ThreadBirth || exited != 0) return 1;
            priority = Native.GetThreadPriority(h);
            return priority == Native.THREAD_PRIORITY_ERROR_RETURN ? -1 : 0;
        }
        public int Read(FrameThreadReceipt id, out int priority)
        {
            priority = 0; int state = ProcessState(id.Pid, id.ProcessBirth); if (state != 0) return state;
            IntPtr h = Native.OpenThread(Native.THREAD_QUERY_LIMITED_INFORMATION, false, id.Tid);
            if (h == IntPtr.Zero) return Marshal.GetLastWin32Error() == 87 ? 1 : -1;
            try { return ThreadState(h, id, out priority); } finally { Native.CloseHandle(h); }
        }
        public bool Set(FrameThreadReceipt id, int expected, int desired)
        {
            if (ProcessState(id.Pid, id.ProcessBirth) != 0) return false;
            IntPtr h = Native.OpenThread(Native.THREAD_QUERY_LIMITED_INFORMATION | Native.THREAD_SET_LIMITED_INFORMATION, false, id.Tid);
            if (h == IntPtr.Zero) return false;
            try { int current; return ThreadState(h, id, out current) == 0 && current == expected
                && Native.SetThreadPriority(h, desired) && Native.GetThreadPriority(h) == desired; }
            finally { Native.CloseHandle(h); }
        }
    }

    internal sealed class FrameCoordinationRetry
    {
        private int failures, lastTraceCode;
        private FrameCaptureIssue lastIssue;
        private long lastLogMs;
        private bool logged;
        internal int DelayMs { get; private set; }
        internal bool ShouldLog { get; private set; }
        internal void Observe(FrameCoordinationSample sample, long elapsedMs)
        {
            FrameCaptureIssue issue = sample == null ? FrameCaptureIssue.CaptureFailed : sample.Issue;
            int traceCode = sample == null ? -1 : sample.TraceCode;
            bool transient = sample != null && (sample.Valid || issue == FrameCaptureIssue.InsufficientEvidence
                || issue == FrameCaptureIssue.Canceled || issue == FrameCaptureIssue.TargetChanged);
            if (transient) { failures = 0; DelayMs = 30000; }
            else
            {
                if (failures < 5) ++failures;
                DelayMs = Math.Min(300000, 30000 << (failures - 1));
            }
            ShouldLog = !logged || issue != lastIssue || traceCode != lastTraceCode || elapsedMs - lastLogMs >= 300000;
            if (ShouldLog) { logged = true; lastIssue = issue; lastTraceCode = traceCode; lastLogMs = elapsedMs; }
        }
        internal void Reset()
        { failures = 0; logged = false; DelayMs = 0; ShouldLog = false; }
    }

    internal static partial class FrameCoordination
    {
        internal const string ActionKey = "FrameCoordination";
        internal const string JournalKey = "FrameCoordinationJournalV1";
        private static readonly object sync = new object(), mutation = new object();
        private static Thread worker;
        private static int generation, pid;
        private static long birth;
        private static bool closed;
        private static volatile int state;
        internal static int State { get { return state; } }
        internal static bool HasResidue
        {
            get
            {
                string receipt;
                return !Settings.TryLoadStr(JournalKey, out receipt) || receipt.Length > 0
                    || FrameMemoryProtection.HasResidue || FrameCoordinationTrace.HasResidue;
            }
        }
        private static IFrameThreads platform = new NativeFrameThreads();
        [ThreadStatic] private static string applyExecutionFailure;

        internal static void Ensure(int target, long created, string name, Func<bool> admitted)
        {
            if (target <= 0 || created <= 0 || admitted == null || !admitted()) return;
            lock (sync)
            {
                if (closed) return;
                if (worker != null && worker.IsAlive)
                {
                    if (pid != target || birth != created) Interlocked.Increment(ref generation);
                    return;
                }
                if (ActionFailureStore.IsBlocked(ActionKey, name)) { state = 4; return; }
                FrameCaptureSession session = CaptureSession(target, created);
                if (session != null && session.Stopped && !session.NoChangeNeeded
                    && ActionFailureStore.IsRetryPending(ActionKey, name))
                { session.Stopped = false; session.Failures = 0; session.Retry.Reset(); }
                if ((session == null || session.Stopped) && !HasResidue)
                {
                    state = session == null ? 3 : session.NoChangeNeeded ? 0 : 4; return;
                }
                pid = target; birth = created; int epoch = Interlocked.Increment(ref generation);
                worker = new Thread(delegate() { Run(target, created, name, epoch, admitted, session); });
                worker.IsBackground = true; worker.Name = "Pavise.FrameCoordination";
                worker.Priority = ThreadPriority.BelowNormal; worker.Start();
            }
        }
        private static bool Current(int epoch, Func<bool> admitted)
        { return !closed && Volatile.Read(ref generation) == epoch && admitted(); }
        private static bool Wait(int ms, Func<bool> alive)
        {
#if PAVISE_SELFTEST
            if (WaitForTest != null) return WaitForTest(ms, alive);
#endif
            for (int elapsed = 0; elapsed < ms; elapsed += 200) { if (!alive()) return false; Thread.Sleep(Math.Min(200, ms - elapsed)); }
            return alive();
        }
        internal static string WaitingLine(string name, FrameCoordinationSample sample)
        {
            string tag = Logger.WarnTag;
            if (sample != null && sample.Valid && sample.NoChangeNeeded) tag = Logger.InfoTag;
            if (sample != null && !sample.Valid)
            {
                if (sample.Issue == FrameCaptureIssue.Canceled || sample.Issue == FrameCaptureIssue.TargetChanged
                    || sample.Issue == FrameCaptureIssue.InsufficientEvidence) tag = Logger.InfoTag;
                else if (sample.Issue == FrameCaptureIssue.TraceStopFailed || sample.Issue == FrameCaptureIssue.DecodeFailed
                    || sample.Issue == FrameCaptureIssue.CaptureFailed) tag = Logger.FailTag;
            }
            string reason = sample == null ? AutomaticPolicy.Text("采样暂不可用", "Capture is unavailable")
                : sample.Valid ? sample.NoChangeNeeded
                    ? AutomaticPolicy.Text("目标线程优先级由游戏控制，无需重复调整", "Target thread priorities are game-managed; no change needed")
                    : AutomaticPolicy.Text("目标线程读取或优先级写入受限，保持原调度", "Thread access or priority writes are blocked; original scheduling retained")
                : sample.Detail;
            string metrics = sample == null ? "" : " [reason=" + sample.Issue + " traceCode=" + sample.TraceCode
                + " lostEvents=" + sample.LostEvents + " eventCount=" + sample.Events
                + " sampleCpuMs=" + sample.CpuMs.ToString("F1", CultureInfo.InvariantCulture)
                + " systemEvents=" + sample.SystemEvents + "]";
            return tag + "FRAME " + AutomaticPolicy.Text("线程协同待命：", "Coordination waiting: ") + name + " | " + reason + metrics;
        }
        private static void Run(int target, long created, string name, int epoch, Func<bool> admitted, FrameCaptureSession session)
        {
            Func<bool> alive = delegate { return Current(epoch, admitted); };
            var clock = Stopwatch.StartNew();
            try
            {
                while (alive())
                {
                    // 线程恢复受阻也必须停止内核采集。
                    bool clean = Restore(); clean &= FrameCoordinationTrace.Heal();
                    if (!clean) { state = 3; if (!Wait(30000, alive)) break; continue; }
                    if (session == null || session.Stopped) break;
                    if (CaptureProcessState(target, created) > 0) break;
                    state = 1;
                    bool previousMode = session.PresenterOnly;
                    FrameCoordinationSample sample = TakeCapture(target, created, alive, previousMode);
                    if (!alive())
                    {
                        if (sample != null && !sample.Valid && sample.Issue != FrameCaptureIssue.Canceled
                            && sample.Issue != FrameCaptureIssue.TargetChanged)
                        {
                            ObserveCapture(session, sample, false, clock.ElapsedMilliseconds);
                            if (sample.Issue == FrameCaptureIssue.TraceStopFailed || sample.Issue == FrameCaptureIssue.DecodeFailed
                                || sample.Issue == FrameCaptureIssue.CaptureFailed) Logger.Log(WaitingLine(name, sample));
                        }
                        break;
                    }
                    if (sample != null && (sample.Issue == FrameCaptureIssue.Canceled || sample.Issue == FrameCaptureIssue.TargetChanged)) break;
                    if (sample == null || sample.Issue == FrameCaptureIssue.CaptureFailed
                        || sample.Issue == FrameCaptureIssue.DecodeFailed || sample.Issue == FrameCaptureIssue.TraceStopFailed)
                        ActionFailureStore.Trip(ActionKey, name, sample == null ? "Thread capture returned no result" : sample.Detail);
                    bool applied = sample != null && sample.Valid && ApplyIsolated(sample, alive, name);
                    long elapsedMs = clock.ElapsedMilliseconds;
#if PAVISE_SELFTEST
                    if (ElapsedMsForTest != null) elapsedMs = ElapsedMsForTest();
#endif
                    ObserveCapture(session, sample, applied, elapsedMs);
                    if (!applied && ActionFailureStore.IsIsolated(ActionKey, name))
                    { session.Stopped = true; SaveCaptureSessions(); state = 4; break; }
                    if (!applied)
                    {
                        state = sample != null && sample.NoChangeNeeded ? 0 : 3;
                        if (session.Stopped)
                        {
                            string line = WaitingLine(name, sample);
                            if (!line.StartsWith(Logger.FailTag, StringComparison.Ordinal))
                                line = Logger.InfoTag + line.Substring(line.IndexOf("FRAME", StringComparison.Ordinal));
                            Logger.Log(line + " | " + AutomaticPolicy.Text("本进程不再重复采样，保留原调度", "Further capture stopped for this process; original scheduling retained"));
                            break;
                        }
                        if (!previousMode && session.PresenterOnly)
                        {
                            Logger.Info("FRAME " + name + " | " + AutomaticPolicy.Text("调度采样受限，改为仅识别画面提交线程", "Scheduling capture limited; switching to presenter-only capture"));
                            continue;
                        }
                        if (session.Retry.ShouldLog) Logger.Log(WaitingLine(name, sample) + " retryMs=" + session.Retry.DelayMs);
                        if (!Wait(session.Retry.DelayMs, alive)) break; continue;
                    }
                    if (session.Stopped) break;
                    state = 2;
                    Logger.Log(Logger.SuccessTag + "FRAME " + (Lang.Cur == 1 ? "Coordination active: " : "线程协同已执行：")
                        + name + " pid=" + target + " present=" + sample.PresentTid + " worker=" + sample.WorkerTid
                        + " sampleCpuMs=" + sample.CpuMs.ToString("F1", CultureInfo.InvariantCulture));
                    for (int i = 0; i < 60 && Wait(1000, alive); i++)
                        if (!LeaseHealthy()) break;
                }
            }
            catch (Exception error)
            {
                state = 3;
                if (session != null)
                {
                    ObserveCapture(session, new FrameCoordinationSample { Pid = target, Birth = created }
                        .Reject(FrameCaptureIssue.CaptureFailed), false, clock.ElapsedMilliseconds);
                    if (alive())
                    {
                        ActionFailureStore.Trip(ActionKey, name, "Thread capture execution failed: " + error.Message);
                        session.Stopped = true; SaveCaptureSessions();
                    }
                }
                Logger.Error("FRAME " + error.Message);
            }
            finally
            {
                Restore(); FrameCoordinationTrace.Heal();
                if (Volatile.Read(ref generation) == epoch) state = session != null && session.Stopped && !session.NoChangeNeeded ? 4 : 0;
            }
        }
        internal static bool Apply(FrameCoordinationSample sample, Func<bool> alive)
        {
            lock (mutation)
            {
                applyExecutionFailure = null;
                if (sample != null) sample.NoChangeNeeded = false;
                if (sample == null || !sample.Valid || sample.Pid <= 0 || sample.Birth <= 0 || sample.PresentTid <= 0
                    || !sample.Threads.ContainsKey(sample.PresentTid) || sample.Threads[sample.PresentTid] <= 0 || !alive() || !RestoreLocked()) return false;
                var receipts = new List<FrameThreadReceipt>();
                foreach (int tid in new[] { sample.PresentTid, sample.WorkerTid })
                {
                    if (tid == sample.WorkerTid && tid == sample.PresentTid && receipts.Count > 0) continue;
                    long threadBirth;
                    if (tid <= 0 || !sample.Threads.TryGetValue(tid, out threadBirth)) continue;
                    var id = new FrameThreadReceipt { Pid = sample.Pid, ProcessBirth = sample.Birth, Tid = tid, ThreadBirth = threadBirth };
                    int priority;
                    int readState = platform.Read(id, out priority);
                    if (readState != 0) { if (readState < 0) applyExecutionFailure = "Thread original state could not be read"; return false; }
                    // 不接管游戏原有 MMCSS、高优先级或空闲线程。
                    if (priority < -2 || priority >= 1) continue;
                    id.Original = priority; receipts.Add(id);
                }
                if (receipts.Count == 0) { sample.NoChangeNeeded = true; return false; }
                string line = Encode(receipts), stored;
                if (!alive()) return false;
                if (!Settings.SaveStrDurable(JournalKey, line) || !Settings.TryLoadStr(JournalKey, out stored) || stored != line)
                { applyExecutionFailure = "Thread recovery record could not be saved or verified"; return false; }
                foreach (FrameThreadReceipt id in receipts)
                {
                    if (!alive()) { RestoreLocked(); return false; }
                    if (!platform.Set(id, id.Original, 1))
                    {
                        int observed;
                        int observedState = platform.Read(id, out observed);
                        if (observedState <= 0 && (observedState < 0 || observed == id.Original || observed == 1))
                            applyExecutionFailure = "Thread priority apply or readback failed";
                        RestoreLocked(); return false;
                    }
                }
                if (!FrameMemoryProtection.Apply(receipts, platform as IFrameThreadMemory, alive))
                {
                    applyExecutionFailure = FrameMemoryProtection.ExecutionFailure;
                    if (!FrameMemoryProtection.Restore()) applyExecutionFailure = "Thread memory protection could not be restored";
                    RestoreLocked(); return false;
                }
                if (!alive()) { RestoreLocked(); return false; }
                return true;
            }
        }

        // 观测不足/取消不熔断；真正执行或确认失败即停止本游戏动作。
        internal static bool ApplyIsolated(FrameCoordinationSample sample, Func<bool> alive, string target)
        {
            if (sample == null || !sample.Valid || alive == null || !alive() || string.IsNullOrEmpty(target)) return false;
            if (ActionFailureStore.IsRetryPending(ActionKey, target) && !Restore()) return false;
            string reason;
            if (!ActionFailureStore.TryBeginApply(ActionKey, target, out reason)) return false;
            bool applied = false;
            string failure = null;
            try { applied = Apply(sample, alive); failure = applyExecutionFailure; }
            catch (Exception error) { failure = error.Message; }
            if (applied && ActionFailureStore.CompleteApply(ActionKey, target)) return true;
            if (applied) failure = "Thread action completion could not be saved";
            if (failure == null)
            {
                if (Restore()) ActionFailureStore.CancelApply(ActionKey, target);
                else ActionFailureStore.Trip(ActionKey, target, "Thread restoration is incomplete; new applications stopped");
            }
            else { ActionFailureStore.Trip(ActionKey, target, failure); Restore(); }
            return false;
        }
        private static string Encode(List<FrameThreadReceipt> receipts)
        { var rows = new List<string> { "v1" }; foreach (var id in receipts) rows.Add(id.Encode()); return string.Join(";", rows.ToArray()); }
        internal static bool Parse(string text, out List<FrameThreadReceipt> result)
        {
            result = new List<FrameThreadReceipt>(); if (text == "") return true;
            string[] rows = text.Split(';'); if (rows.Length < 2 || rows.Length > 3 || rows[0] != "v1") return false;
            var tids = new HashSet<int>();
            for (int i = 1; i < rows.Length; i++)
            {
                string[] p = rows[i].Split('|'); var id = new FrameThreadReceipt();
                if (p.Length != 5 || !int.TryParse(p[0], out id.Pid) || !long.TryParse(p[1], out id.ProcessBirth)
                    || !int.TryParse(p[2], out id.Tid) || !long.TryParse(p[3], out id.ThreadBirth) || !int.TryParse(p[4], out id.Original)
                    || id.Pid <= 0 || id.Tid <= 0 || id.ProcessBirth <= 0 || id.ThreadBirth <= 0 || id.Original < -2 || id.Original > 0 || !tids.Add(id.Tid)) return false;
                if (result.Count > 0 && (id.Pid != result[0].Pid || id.ProcessBirth != result[0].ProcessBirth)) return false;
                result.Add(id);
            }
            return true;
        }
        private static bool LeaseHealthy()
        {
            lock (mutation)
            {
                string raw; List<FrameThreadReceipt> receipts;
                if (!Settings.TryLoadStr(JournalKey, out raw) || raw.Length == 0 || !Parse(raw, out receipts)) return false;
                foreach (var id in receipts) { int value; if (platform.Read(id, out value) != 0 || value != 1) return false; }
                return FrameMemoryProtection.Healthy();
            }
        }
        internal static bool Restore() { lock (mutation) return RestoreLocked(); }
        private static bool RestoreLocked()
        {
            bool memoryClean = FrameMemoryProtection.Restore();
            string raw; List<FrameThreadReceipt> receipts;
            if (!Settings.TryLoadStr(JournalKey, out raw) || !Parse(raw, out receipts)) return false;
            if (raw.Length == 0) return memoryClean;
            bool clean = true;
            foreach (var id in receipts)
            {
                int current; int read = platform.Read(id, out current);
                if (read < 0) { clean = false; continue; }
                // 外部优先级变更后放弃所有权。
                if (read == 0 && current == 1 && !platform.Set(id, 1, id.Original)) clean = false;
            }
            string now;
            bool priorityClean = clean && Settings.TryLoadStr(JournalKey, out now) && now == raw
                && Settings.SaveStr(JournalKey, "") && Settings.TryLoadStr(JournalKey, out now) && now.Length == 0;
            return memoryClean & priorityClean;
        }
        internal static bool ReleaseExcept(int keepPid, long keepBirth)
        {
            lock (sync)
            {
                string raw; List<FrameThreadReceipt> rows = null;
                bool keep = keepPid > 0 && pid == keepPid && birth == keepBirth
                    && Settings.TryLoadStr(JournalKey, out raw) && Parse(raw, out rows);
                if (keep)
                {
                    foreach (var row in rows) if (row.Pid != keepPid || row.ProcessBirth != keepBirth) keep = false;
                    if (keep) return true;
                }
                Interlocked.Increment(ref generation); state = 0;
            }
            bool clean = Restore();
            clean &= FrameCoordinationTrace.Heal();
            return clean;
        }
        internal static bool Close(int timeout)
        {
            Thread current; lock (sync) { closed = true; Interlocked.Increment(ref generation); current = worker; }
            bool drained = current == null || !current.IsAlive || current != Thread.CurrentThread && current.Join(timeout);
            if (!drained) return false;
            bool clean = Restore();
            clean &= FrameCoordinationTrace.Heal();
            return clean;
        }
        internal static bool HealFromCrash() { return FrameCoordinationTrace.Heal() & Restore(); }
#if PAVISE_SELFTEST
        internal static void SetPlatformForTest(IFrameThreads replacement)
        { platform = replacement ?? new NativeFrameThreads(); FrameMemoryProtection.SetPlatformForTest(platform as IFrameThreadMemory); }
#endif
    }
}
