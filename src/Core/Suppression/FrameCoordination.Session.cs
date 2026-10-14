using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

namespace PaviseApp
{
    internal sealed class FrameCaptureSession
    {
        internal readonly int Pid;
        internal readonly long Birth;
        internal readonly FrameCoordinationRetry Retry = new FrameCoordinationRetry();
        internal bool PresenterOnly, Stopped, NoChangeNeeded;
        internal int Failures;
        internal FrameCaptureSession(int pid, long birth) { Pid = pid; Birth = birth; }
    }

    internal static partial class FrameCoordination
    {
        internal const string CaptureSessionKey = "FrameCoordinationCaptureSessionsV1";
        internal const int MaxCaptureFailures = 3, MaxCaptureSessions = 64;
        private static readonly object captureSessionGate = new object();
        private static readonly Dictionary<int, FrameCaptureSession> captureSessions = new Dictionary<int, FrameCaptureSession>();
        private static bool captureSessionsLoaded, captureSessionStoreFailed;

        // 采样预算按进程生命周期保留，焦点切换不重置。
        private static FrameCaptureSession CaptureSession(int target, long created)
        {
            lock (captureSessionGate)
            {
                if (!LoadCaptureSessions()) return null;
                FrameCaptureSession session;
                if (captureSessions.TryGetValue(target, out session) && session.Birth == created) return session;
                if (session != null)
                {
                    if (CaptureProcessState(target, created) != 0) return null;
                    captureSessions.Remove(target);
                }
                if (captureSessions.Count >= MaxCaptureSessions)
                {
                    var ended = new List<int>();
                    foreach (var entry in captureSessions)
                        if (CaptureProcessState(entry.Key, entry.Value.Birth) > 0) ended.Add(entry.Key);
                    foreach (int endedPid in ended) captureSessions.Remove(endedPid);
                    if (captureSessions.Count >= MaxCaptureSessions) return null;
                }
                session = new FrameCaptureSession(target, created);
                captureSessions[target] = session;
                return session;
            }
        }
        private static bool LoadCaptureSessions()
        {
            if (captureSessionsLoaded) return !captureSessionStoreFailed;
            captureSessionsLoaded = true;
            string text;
            if (!Settings.TryLoadStr(CaptureSessionKey, out text)) return CaptureSessionLoadFailed();
            if (text.Length == 0) return true;
            string[] rows = text.Split(';');
            if (rows[0] != "v1" || rows.Length > MaxCaptureSessions + 1) return CaptureSessionLoadFailed();
            for (int i = 1; i < rows.Length; i++)
            {
                string[] fields = rows[i].Split('|');
                int target, failures, presenterOnly, stopped, noChange;
                long created;
                if (fields.Length != 6 || !int.TryParse(fields[0], out target) || target <= 0
                    || !long.TryParse(fields[1], out created) || created <= 0
                    || !int.TryParse(fields[2], out presenterOnly) || (presenterOnly != 0 && presenterOnly != 1)
                    || !int.TryParse(fields[3], out failures) || failures < 0 || failures > MaxCaptureFailures
                    || !int.TryParse(fields[4], out stopped) || (stopped != 0 && stopped != 1)
                    || !int.TryParse(fields[5], out noChange) || (noChange != 0 && noChange != 1)
                    || (failures == MaxCaptureFailures && stopped == 0) || (noChange != 0 && stopped == 0)
                    || captureSessions.ContainsKey(target)) return CaptureSessionLoadFailed();
                captureSessions[target] = new FrameCaptureSession(target, created) {
                    PresenterOnly = presenterOnly != 0, Failures = failures,
                    Stopped = stopped != 0, NoChangeNeeded = noChange != 0 };
            }
            return true;
        }
        private static bool CaptureSessionLoadFailed()
        {
            captureSessions.Clear(); captureSessionStoreFailed = true;
            Logger.Warn("FRAME " + AutomaticPolicy.Text("采样状态无法读取，保留系统调度", "Capture state is unreadable; system scheduling retained"));
            return false;
        }
        private static void SaveCaptureSessions()
        {
            var rows = new List<string> { "v1" };
            foreach (FrameCaptureSession session in captureSessions.Values)
                if (session.PresenterOnly || session.Failures != 0 || session.Stopped)
                    rows.Add(session.Pid.ToString(CultureInfo.InvariantCulture) + "|"
                        + session.Birth.ToString(CultureInfo.InvariantCulture) + "|" + (session.PresenterOnly ? "1" : "0")
                        + "|" + session.Failures.ToString(CultureInfo.InvariantCulture) + "|" + (session.Stopped ? "1" : "0")
                        + "|" + (session.NoChangeNeeded ? "1" : "0"));
            string text = rows.Count == 1 ? "" : string.Join(";", rows.ToArray()), written;
            if (Settings.SaveStr(CaptureSessionKey, text) && Settings.TryLoadStr(CaptureSessionKey, out written) && written == text) return;
            captureSessionStoreFailed = true;
            foreach (FrameCaptureSession session in captureSessions.Values) session.Stopped = true;
        }
        private static bool CapturePressure(FrameCaptureIssue issue)
        {
            return issue == FrameCaptureIssue.SystemEventBudget || issue == FrameCaptureIssue.EventBudget
                || issue == FrameCaptureIssue.EdgeBudget || issue == FrameCaptureIssue.CpuBudget
                || issue == FrameCaptureIssue.EventsLost || issue == FrameCaptureIssue.TraceUnavailable;
        }
        private static void ObserveCapture(FrameCaptureSession session, FrameCoordinationSample sample, bool applied, long elapsedMs)
        {
            lock (captureSessionGate)
            {
                bool previousMode = session.PresenterOnly, previousStop = session.Stopped, previousNoChange = session.NoChangeNeeded;
                int previousFailures = session.Failures;
                if (sample != null && sample.Valid && (applied || sample.NoChangeNeeded))
                {
                    session.Failures = 0; session.Retry.Reset();
                    if (sample.NoChangeNeeded) { session.NoChangeNeeded = true; session.Stopped = true; }
                }
                else
                {
                    session.Retry.Observe(sample, elapsedMs);
                    FrameCaptureIssue issue = sample == null ? FrameCaptureIssue.CaptureFailed : sample.Issue;
                    if (issue == FrameCaptureIssue.Canceled || issue == FrameCaptureIssue.TargetChanged) return;
                    if (session.Failures < MaxCaptureFailures) ++session.Failures;
                    if (!session.PresenterOnly && CapturePressure(issue)) session.PresenterOnly = true;
                    if (session.Failures >= MaxCaptureFailures) session.Stopped = true;
                }
                if (previousMode != session.PresenterOnly || previousStop != session.Stopped
                    || previousNoChange != session.NoChangeNeeded || previousFailures != session.Failures) SaveCaptureSessions();
            }
        }
        private static int CaptureProcessState(int target, long created)
        {
#if PAVISE_SELFTEST
            if (ProcessStateForTest != null) return ProcessStateForTest(target, created);
#endif
            return NativeFrameThreads.ProcessState(target, created);
        }
        private static FrameCoordinationSample TakeCapture(int target, long created, Func<bool> alive, bool presenterOnly)
        {
#if PAVISE_SELFTEST
            if (CaptureForTest != null) return CaptureForTest(target, created, alive, presenterOnly);
#endif
            return FrameCoordinationSampler.Capture(target, created, alive, presenterOnly);
        }
#if PAVISE_SELFTEST
        internal static Func<int, long, Func<bool>, bool, FrameCoordinationSample> CaptureForTest;
        internal static Func<int, Func<bool>, bool> WaitForTest;
        internal static Func<long> ElapsedMsForTest;
        internal static Func<int, long, int> ProcessStateForTest;
        internal static bool DrainWorkerForTest(int timeout)
        {
            Thread current; lock (sync) current = worker;
            return current == null || !current.IsAlive || current != Thread.CurrentThread && current.Join(timeout);
        }
        internal static void ResetWorkerForTest()
        {
            if (!DrainWorkerForTest(1000)) throw new InvalidOperationException("Capture test worker has not stopped");
            lock (sync) { closed = false; worker = null; pid = 0; birth = 0; state = 0; ++generation; }
            ResetCaptureSessionsForTest();
            CaptureForTest = null; WaitForTest = null; ElapsedMsForTest = null; ProcessStateForTest = null;
        }
        internal static void ResetCaptureSessionsForTest()
        { lock (captureSessionGate) { captureSessions.Clear(); captureSessionsLoaded = captureSessionStoreFailed = false; } }
        internal static FrameCaptureSession CaptureSessionForTest(int target, long created) { return CaptureSession(target, created); }
#endif
    }
}
