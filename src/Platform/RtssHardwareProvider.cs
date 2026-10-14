using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace PaviseApp
{
    internal sealed class RtssHardwareProviderIdentity
    {
        internal int Pid;
        internal long Creation;
        internal string Path;
        internal IntPtr Window;
    }

    internal sealed class RtssHardwareProviderBackend
    {
        internal Func<RtssComponentInfo> Detect;
        internal Func<string, bool> Exists;
        internal Func<RtssHardwareProviderIdentity> Find;
        internal Func<ProcessStartInfo, RtssHardwareProviderIdentity> Start;
        internal Func<RtssHardwareProviderIdentity, bool> MatchesProcess;
        internal Func<RtssHardwareProviderIdentity, bool> HasExited;
        internal Func<RtssHardwareProviderIdentity, bool> StopOwned;
        internal Func<RtssHardwareProviderIdentity, bool> MatchesWindow;
        internal Func<IntPtr, int, bool> Ping;
        internal Action<IntPtr> RequestExit;
        internal Action<int> Wait;
        internal Func<long> Milliseconds;
    }

    internal sealed class RtssHardwareProvider : IDisposable
    {
        internal const string WindowTitle = "LHMDataProviderConnectWnd";
        // 硬件更新可能占用窗口线程，过短探测会误判离线。
        internal const int ResponseTimeoutMilliseconds = 100, RetryMilliseconds = 30000;
        internal const int ResponseGraceMilliseconds = 2000, PingCacheMilliseconds = 250;
        internal const int ReadFailureMilliseconds = 30000, ExitGraceMilliseconds = 5000;
        internal const int DisposeGraceMilliseconds = 200;
        private readonly object gate = new object();
        private readonly RtssHardwareProviderBackend backend;
        private bool attempted, disposed;
        private long lastAttempt;
        private string executable;
        private RtssHardwareProviderIdentity current, owned, lastResponsiveIdentity, observedSource;
        private long sourceGeneration;
        private long lastSuccessfulPing;
        private bool failedRead, recovering, recoveryAttempted;
        private long firstFailedRead, recoveryStarted, lastRecoveryAttempt;
        internal bool UsingRecentHeartbeat { get; private set; }
        internal long SourceGeneration { get { lock (gate) return sourceGeneration; } }

        internal RtssHardwareProvider() : this(CreateBackend()) { }
        internal RtssHardwareProvider(RtssHardwareProviderBackend operations) { backend = operations; }

        internal bool EnsureRunning()
        {
            lock (gate)
            {
                if (disposed) return false;
                try
                {
                    if (current != null)
                    {
                        if (backend.MatchesProcess(current)) return true;
                        // 无法确认退出时保留旧进程身份，避免重复启动。
                        if (backend.HasExited == null || !backend.HasExited(current)) return false;
                        if (SameIdentity(owned, current)) owned = null;
                        current = null;
                        ResetSource();
                    }
                    current = FindVerified();
                    if (current != null) return true;
                    long now = Milliseconds();
                    if (!attempted || now - lastAttempt >= RetryMilliseconds)
                    {
                        attempted = true;
                        lastAttempt = now;
                        RtssComponentInfo component = backend.Detect();
                        if (component == null || !component.Installed || component.InstallationInProgress
                            || component.UninstallationInProgress || !RtssComponent.SupportsFontOverrideVersion(component.Version)) return false;
                        string directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(component.Path));
                        executable = System.IO.Path.GetFullPath(System.IO.Path.Combine(directory,
                            "Plugins", "Client", "LHMDataProvider", "LHMDataProvider.exe"));
                        if (!backend.Exists(executable)) { executable = null; return false; }
                        current = FindVerified();
                        if (current != null) return true;
                        var start = new ProcessStartInfo(executable, "-i") {
                            WorkingDirectory = System.IO.Path.GetDirectoryName(executable),
                            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
                        };
                        RtssHardwareProviderIdentity started = backend.Start(start);
                        if (!ValidIdentity(started) || !backend.MatchesProcess(started)) return false;
                        owned = CopyIdentity(started);
                        current = CopyIdentity(started);
                        ClearReadFailure();
                        return true;
                    }
                    // 启动失败和异常退出均使用冷却重试。
                    return false;
                }
                catch { return false; }
            }
        }

        internal bool IsResponsive()
        {
            lock (gate)
            {
                UsingRecentHeartbeat = false;
                if (disposed || executable == null) { ClearHeartbeat(); return false; }
                try
                {
                    RtssHardwareProviderIdentity window = FindVerified();
                    if (window == null || !backend.MatchesWindow(window)) { ClearHeartbeat(); return false; }
                    current = window;
                    long now = Milliseconds();
                    if (!SameWindowIdentity(lastResponsiveIdentity, window) || now < lastSuccessfulPing)
                        ClearHeartbeat();
                    if (lastResponsiveIdentity != null && now - lastSuccessfulPing < PingCacheMilliseconds)
                    {
                        if (backend.MatchesWindow(window)) return true;
                        ClearHeartbeat(); return false;
                    }
                    bool responsive = backend.Ping(window.Window, ResponseTimeoutMilliseconds);
                    if (!backend.MatchesWindow(window)) { ClearHeartbeat(); return false; }
                    long completed = Milliseconds();
                    if (completed < now || (lastResponsiveIdentity != null && completed < lastSuccessfulPing))
                        ClearHeartbeat();
                    if (responsive)
                    {
                        lastResponsiveIdentity = CopyIdentity(window);
                        lastSuccessfulPing = completed;
                        return true;
                    }
                    // 失败不会延长心跳宽限期。
                    UsingRecentHeartbeat = SameWindowIdentity(lastResponsiveIdentity, window)
                        && completed >= lastSuccessfulPing
                        && completed - lastSuccessfulPing < ResponseGraceMilliseconds;
                    return UsingRecentHeartbeat;
                }
                catch { ClearHeartbeat(); return false; }
            }
        }

        internal void ReportReadResult(bool fresh)
        {
            lock (gate)
            {
                if (disposed) return;
                try
                {
                    if (fresh) { ClearReadFailure(); return; }
                    long now = Milliseconds();
                    if (!failedRead || now < firstFailedRead || (recovering && now < recoveryStarted)
                        || (recoveryAttempted && now < lastRecoveryAttempt))
                    {
                        ClearReadFailure(); failedRead = true; firstFailedRead = now;
                        return;
                    }
                    if (now - firstFailedRead < ReadFailureMilliseconds) return;
                    // 所有权包含创建时间，借用或被替换的采集器不能关闭。
                    if (!SameIdentity(owned, current) || !ValidIdentity(owned)
                        || !backend.MatchesProcess(owned)) return;
                    if (recovering)
                    {
                        if (now - recoveryStarted < ExitGraceMilliseconds) return;
                        recovering = false;
                        recoveryAttempted = true; lastRecoveryAttempt = now;
                        if (backend.StopOwned != null && backend.StopOwned(CopyIdentity(owned)))
                            ResetSource();
                        return;
                    }
                    if (recoveryAttempted && now - lastRecoveryAttempt < RetryMilliseconds) return;
                    recovering = true; recoveryStarted = now;
                    RtssHardwareProviderIdentity window = backend.Find();
                    if (ValidIdentity(window) && SameIdentity(owned, window)
                        && window.Window != IntPtr.Zero && backend.MatchesWindow(window))
                        backend.RequestExit(window.Window);
                }
                catch
                {
                    recovering = false;
                    recoveryAttempted = true;
                    try { lastRecoveryAttempt = Milliseconds(); } catch { }
                }
            }
        }
        private void ClearReadFailure()
        {
            failedRead = recovering = recoveryAttempted = false;
            firstFailedRead = recoveryStarted = lastRecoveryAttempt = 0;
        }
        private void ResetSource()
        {
            observedSource = null;
            sourceGeneration = unchecked(sourceGeneration + 1);
            ClearHeartbeat(); ClearReadFailure();
        }
        private long Milliseconds()
        {
            return backend.Milliseconds == null ? (long)(Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency)
                : backend.Milliseconds();
        }
        private void ClearHeartbeat()
        { lastResponsiveIdentity = null; lastSuccessfulPing = 0; UsingRecentHeartbeat = false; }
        private static bool SameWindowIdentity(RtssHardwareProviderIdentity left, RtssHardwareProviderIdentity right)
        { return SameIdentity(left, right) && left.Window == right.Window; }
        private static RtssHardwareProviderIdentity CopyIdentity(RtssHardwareProviderIdentity identity)
        {
            return new RtssHardwareProviderIdentity {
                Pid = identity.Pid, Creation = identity.Creation, Path = identity.Path, Window = identity.Window
            };
        }
        private void ObserveSource(RtssHardwareProviderIdentity identity)
        {
            if (SameWindowIdentity(observedSource, identity)) return;
            observedSource = CopyIdentity(identity);
            sourceGeneration = unchecked(sourceGeneration + 1);
            ClearHeartbeat(); ClearReadFailure();
        }
        private bool ValidIdentity(RtssHardwareProviderIdentity identity)
        {
            return executable != null && identity != null && identity.Pid > 4 && identity.Creation > 0
                && !String.IsNullOrWhiteSpace(identity.Path)
                && String.Equals(System.IO.Path.GetFullPath(identity.Path), executable, StringComparison.OrdinalIgnoreCase);
        }
        private RtssHardwareProviderIdentity FindVerified()
        {
            if (executable == null) return null;
            RtssHardwareProviderIdentity identity = backend.Find();
            if (!ValidIdentity(identity) || identity.Window == IntPtr.Zero || !backend.MatchesWindow(identity)) return null;
            ObserveSource(identity);
            return identity;
        }
        private static bool SameIdentity(RtssHardwareProviderIdentity left, RtssHardwareProviderIdentity right)
        { return left != null && right != null && left.Pid == right.Pid && left.Creation == right.Creation
            && String.Equals(left.Path, right.Path, StringComparison.OrdinalIgnoreCase); }

        private bool OwnedProcessMatches()
        { return SameIdentity(owned, current) && ValidIdentity(owned) && backend.MatchesProcess(owned); }
        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                try
                {
                    if (OwnedProcessMatches())
                    {
                        RtssHardwareProviderIdentity window = null;
                        try { window = FindVerified(); } catch { }
                        var grace = Stopwatch.StartNew();
                        for (int i = 0; window == null && i < 5 && grace.ElapsedMilliseconds < 50; i++)
                        {
                            try { backend.Wait(10); } catch { break; }
                            if (!OwnedProcessMatches()) break;
                            try { window = FindVerified(); } catch { }
                        }
                        bool verifiedWindow = false;
                        try { verifiedWindow = SameIdentity(owned, window) && backend.MatchesWindow(window)
                            && OwnedProcessMatches(); } catch { }
                        if (verifiedWindow)
                        {
                            try { backend.RequestExit(window.Window); } catch { }
                        }
                        if (OwnedProcessMatches())
                        {
                            bool responsive = false;
                            if (verifiedWindow)
                            {
                                try { responsive = backend.MatchesWindow(window)
                                    && backend.Ping(window.Window, ResponseTimeoutMilliseconds)
                                    && backend.MatchesWindow(window); } catch { }
                            }
                            if (responsive)
                            {
                                // 还能响应消息不代表已退出，关闭监测后不能遗留自有采集器。
                                var exiting = Stopwatch.StartNew();
                                for (int i = 0; i < DisposeGraceMilliseconds / 10
                                    && exiting.ElapsedMilliseconds < DisposeGraceMilliseconds && OwnedProcessMatches(); i++)
                                {
                                    try { backend.Wait(10); } catch { break; }
                                }
                            }
                            if (OwnedProcessMatches() && backend.StopOwned != null)
                            {
                                try { backend.StopOwned(CopyIdentity(owned)); } catch { }
                            }
                        }
                    }
                }
                catch { }
                current = owned = null;
                ResetSource();
            }
        }

        private static RtssHardwareProviderBackend CreateBackend()
        {
            return new RtssHardwareProviderBackend {
                Detect = RtssComponent.Detect, Exists = File.Exists, Find = FindNative,
                Start = StartNative, MatchesProcess = MatchesNativeProcess, HasExited = HasExitedNative,
                StopOwned = StopOwnedNative, MatchesWindow = MatchesNativeWindow,
                Ping = PingNative, RequestExit = delegate(IntPtr window) { PostMessageW(window, 0x0012, IntPtr.Zero, IntPtr.Zero); },
                Wait = Thread.Sleep
            };
        }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowW(string name, string title);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out int pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr window, StringBuilder text, int maximum);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessageTimeoutW(IntPtr window,
            uint message, IntPtr wparam, IntPtr lparam, uint flags, uint milliseconds, out UIntPtr result);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool PostMessageW(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder path, ref int length);
        [DllImport("kernel32.dll")] private static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(IntPtr process, uint code);

        private static RtssHardwareProviderIdentity ReadIdentity(int pid, IntPtr window)
        {
            if (pid <= 4) return null;
            IntPtr handle = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (handle == IntPtr.Zero) return null;
            try { return ReadIdentity(handle, pid, window); }
            finally { Native.CloseHandle(handle); }
        }
        private static RtssHardwareProviderIdentity ReadIdentity(IntPtr handle, int pid, IntPtr window)
        {
            try
            {
                long creation, exit, kernel, user;
                var image = new StringBuilder(1024); int size = image.Capacity;
                if (!GetProcessTimes(handle, out creation, out exit, out kernel, out user) || creation <= 0 || exit != 0
                    || !QueryFullProcessImageNameW(handle, 0, image, ref size)
                    || Native.QueryProcessIdentityState(handle, creation) != 0) return null;
                return new RtssHardwareProviderIdentity { Pid = pid, Creation = creation,
                    Path = System.IO.Path.GetFullPath(image.ToString()), Window = window };
            }
            catch { return null; }
        }
        private static RtssHardwareProviderIdentity FindNative()
        {
            IntPtr window = FindWindowW(null, WindowTitle); int pid;
            if (window == IntPtr.Zero || GetWindowThreadProcessId(window, out pid) == 0) return null;
            RtssHardwareProviderIdentity identity = ReadIdentity(pid, window);
            return identity != null && MatchesNativeWindow(identity) ? identity : null;
        }
        private static RtssHardwareProviderIdentity StartNative(ProcessStartInfo start)
        {
            using (Process process = Process.Start(start))
            { return process == null ? null : ReadIdentity(process.Id, IntPtr.Zero); }
        }
        private static bool MatchesNativeProcess(RtssHardwareProviderIdentity identity)
        { return identity != null && SameIdentity(identity, ReadIdentity(identity.Pid, identity.Window)); }
        private static bool HasExitedNative(RtssHardwareProviderIdentity identity)
        {
            if (identity == null || identity.Pid <= 4 || identity.Creation <= 0) return false;
            IntPtr handle = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, identity.Pid);
            if (handle == IntPtr.Zero) return Native.LastOpenProcessFailureWasNoSuchProcess();
            try { return Native.QueryProcessIdentityState(handle, identity.Creation) == 1; }
            finally { Native.CloseHandle(handle); }
        }
        private static bool StopOwnedNative(RtssHardwareProviderIdentity identity)
        {
            if (identity == null || identity.Pid <= 4 || identity.Creation <= 0
                || String.IsNullOrWhiteSpace(identity.Path)) return false;
            // 通过同一进程句柄校验并停止，避免误杀复用 PID 的进程。
            IntPtr handle = Native.OpenProcess(0x100000 | 0x0001 | Native.PROCESS_QUERY_LIMITED_INFORMATION,
                false, identity.Pid);
            if (handle == IntPtr.Zero) return false;
            try
            {
                RtssHardwareProviderIdentity actual = ReadIdentity(handle, identity.Pid, identity.Window);
                return SameIdentity(identity, actual) && Native.WaitForSingleObject(handle, 0) == 258
                    && Native.QueryProcessIdentityState(handle, identity.Creation) == 0
                    && TerminateProcess(handle, 1);
            }
            catch { return false; }
            finally { Native.CloseHandle(handle); }
        }
        private static bool MatchesNativeWindow(RtssHardwareProviderIdentity identity)
        {
            if (identity == null || identity.Window == IntPtr.Zero) return false;
            int pid;
            var title = new StringBuilder(128);
            return GetWindowThreadProcessId(identity.Window, out pid) != 0 && pid == identity.Pid
                && GetWindowTextW(identity.Window, title, title.Capacity) > 0 && title.ToString() == WindowTitle
                && MatchesNativeProcess(identity) && GetWindowThreadProcessId(identity.Window, out pid) != 0 && pid == identity.Pid;
        }
        private static bool PingNative(IntPtr window, int milliseconds)
        {
            UIntPtr result;
            return SendMessageTimeoutW(window, 0, IntPtr.Zero, IntPtr.Zero, 2 | 32,
                (uint)Math.Max(1, Math.Min(ResponseTimeoutMilliseconds, milliseconds)), out result) != IntPtr.Zero;
        }
    }
}
