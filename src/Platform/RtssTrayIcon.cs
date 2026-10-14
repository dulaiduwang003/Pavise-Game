using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace PaviseApp
{
    internal sealed class RtssTrayIconTarget
    {
        internal int Pid;
        internal long Birth;
        internal IntPtr Window;
        internal string Path;
    }

    internal interface IRtssTrayIconBackend
    {
        long Clock { get; }
        long ShellIdentity { get; }
        IEnumerable<RtssTrayIconTarget> Find(string path);
        bool Matches(RtssTrayIconTarget target);
        bool HasIcon(RtssTrayIconTarget target);
        bool SetHidden(RtssTrayIconTarget target, bool hidden);
        IEnumerable<RtssTrayIconTarget> FindWindows(RtssTrayIconTarget owner);
        bool HideWindow(RtssTrayIconTarget target);
        void WatchWindows(RtssTrayIconTarget owner);
        void StopWatchingWindows();
    }

    internal static class RtssTrayIcon
    {
        private static readonly IRtssTrayIconBackend backend = new RtssTrayIconNativeBackend();
        private static readonly RtssTrayIconLifecycle lifecycle = new RtssTrayIconLifecycle(
            new RtssTrayIconController(backend), backend,
            delegate(Action work) { return ThreadPool.QueueUserWorkItem(delegate { work(); }); });
        internal static bool Hide(RtssComponentInfo component)
        {
#if PAVISE_SELFTEST
            return false;
#else
            return lifecycle.Hide(component);
#endif
        }
        internal static bool Restore()
        {
#if PAVISE_SELFTEST
            return true;
#else
            return lifecycle.Restore();
#endif
        }
        internal static void Maintain()
        {
#if !PAVISE_SELFTEST
            lifecycle.Maintain();
#endif
        }
        internal static void Forget() { lifecycle.Forget(); }
    }

    internal sealed class RtssTrayIconLifecycle
    {
        private readonly object gate = new object();
        private readonly RtssTrayIconController controller;
        private readonly IRtssTrayIconBackend backend;
        private readonly Func<Action, bool> queue;
        private RtssComponentInfo component;
        private bool active;
        private int generation, failedRetries;
        private long shell, due;
        private object queued;
        internal RtssTrayIconLifecycle(RtssTrayIconController controller, IRtssTrayIconBackend backend, Func<Action, bool> queue)
        { this.controller = controller; this.backend = backend; this.queue = queue; }

        internal bool Hide(RtssComponentInfo value)
        {
            if (value == null || !value.Installed || value.InstallationInProgress || value.UninstallationInProgress
                || RtssComponent.NormalizeCandidate(value.Path).Length == 0) return false;
            lock (gate)
            {
                if (!value.InstalledByPavise)
                {
                    if (active || component != null) Restore();
                    return false;
                }
                generation++; queued = null; active = true; failedRetries = 0;
                component = new RtssComponentInfo { Installed = true, InstalledByPavise = true, Path = value.Path, Version = value.Version };
                try
                {
                    bool hidden = controller.Hide(component);
                    shell = backend.ShellIdentity;
                    due = backend.Clock + (hidden ? 30000 : 2000);
                    return hidden;
                }
                catch { ScheduleFailure(); return false; }
            }
        }

        internal void Maintain()
        {
            lock (gate)
            {
                if (!active || component == null || queued != null) return;
                try
                {
                    long now = backend.Clock, currentShell = backend.ShellIdentity;
                    bool changed = currentShell != shell;
                    if (!changed && now < due) return;
                    if (changed) { shell = currentShell; failedRetries = 0; }
                    int admittedGeneration = generation;
                    object token = new object(); queued = token;
                    if (!queue(delegate { MaintainWorker(admittedGeneration, token); }))
                    { queued = null; due = now + 30000; }
                }
                catch { queued = null; ScheduleFailure(); }
            }
        }

        private void MaintainWorker(int admittedGeneration, object token)
        {
            lock (gate)
            {
                try
                {
                    if (!active || generation != admittedGeneration || !Object.ReferenceEquals(queued, token)) return;
                    // 隐藏与恢复共用锁，避免退出后延迟回调再次隐藏。
                    bool hidden = controller.Hide(component);
                    shell = backend.ShellIdentity;
                    if (hidden) { failedRetries = 0; due = backend.Clock + 30000; }
                    else { failedRetries = Math.Min(failedRetries + 1, 5); due = backend.Clock + (failedRetries < 5 ? 2000 : 30000); }
                }
                catch { ScheduleFailure(); }
                finally { if (Object.ReferenceEquals(queued, token)) queued = null; }
            }
        }

        private void ScheduleFailure()
        {
            failedRetries = 5;
            try { due = backend.Clock + 30000; }
            catch { due = 0; }
        }

        internal bool Restore()
        {
            lock (gate)
            {
                active = false; generation++; queued = null; component = null;
                return controller.Restore();
            }
        }
        internal void Forget()
        {
            lock (gate)
            {
                active = false; generation++; queued = null; component = null;
                controller.Forget();
            }
        }
    }

    internal sealed class RtssTrayIconController
    {
        private sealed class OwnedIcon
        {
            internal RtssTrayIconTarget Target;
            internal long Shell, Applied;
        }
        private readonly object gate = new object();
        private readonly IRtssTrayIconBackend backend;
        private readonly List<OwnedIcon> owned = new List<OwnedIcon>();
        private string path = "";
        internal RtssTrayIconController(IRtssTrayIconBackend backend) { this.backend = backend; }

        internal bool Hide(RtssComponentInfo component)
        {
            if (component == null || !component.Installed || !component.InstalledByPavise
                || component.InstallationInProgress || component.UninstallationInProgress) return false;
            string expected = RtssComponent.NormalizeCandidate(component.Path);
            if (expected.Length == 0) return false;
            lock (gate)
            {
                try
                {
                    if (!String.Equals(path, expected, StringComparison.OrdinalIgnoreCase))
                    {
                        backend.StopWatchingWindows();
                        owned.Clear();
                        path = expected;
                    }
                    for (int i = owned.Count - 1; i >= 0; i--)
                        if (!backend.Matches(owned[i].Target)) owned.RemoveAt(i);
                    long shell = backend.ShellIdentity, now = backend.Clock;
                    bool found = false, hasOwner = false, allHidden = shell != 0;
                    foreach (RtssTrayIconTarget target in backend.Find(expected))
                    {
                        if (!ValidTarget(target, expected) || !backend.Matches(target)) continue;
                        hasOwner = true;
                        // 用窗口显示事件处理新窗口，定时检查仅作兜底。
                        backend.WatchWindows(target);
                        foreach (RtssTrayIconTarget window in backend.FindWindows(target))
                            if (ValidTarget(window, expected) && window.Pid == target.Pid && window.Birth == target.Birth
                                && backend.Matches(window) && !backend.HideWindow(window)) allHidden = false;
                        if (shell == 0) continue;
                        OwnedIcon current = null;
                        foreach (OwnedIcon item in owned)
                            if (SameTarget(item.Target, target)) { current = item; break; }
                        if (current != null && current.Shell == shell && now >= current.Applied && now - current.Applied < 30000)
                        { found = true; continue; }
                        if (!backend.HasIcon(target)) { allHidden = false; continue; }
                        found = true;
                        if (!backend.SetHidden(target, true)) { allHidden = false; continue; }
                        if (current == null)
                        {
                            current = new OwnedIcon { Target = new RtssTrayIconTarget {
                                Pid = target.Pid, Birth = target.Birth, Window = target.Window, Path = expected } };
                            owned.Add(current);
                        }
                        current.Shell = shell; current.Applied = now;
                    }
                    if (!hasOwner) backend.StopWatchingWindows();
                    return found && allHidden;
                }
                catch { return false; }
            }
        }

        private static bool ValidTarget(RtssTrayIconTarget target, string expected)
        {
            return target != null && target.Pid > 4 && target.Birth > 0 && target.Window != IntPtr.Zero
                && String.Equals(expected, RtssComponent.NormalizeCandidate(target.Path), StringComparison.OrdinalIgnoreCase);
        }
        private static bool SameTarget(RtssTrayIconTarget left, RtssTrayIconTarget right)
        { return left.Pid == right.Pid && left.Birth == right.Birth && left.Window == right.Window; }

        internal bool Restore()
        { lock (gate) { return RestoreLocked(); } }
        private bool RestoreLocked()
        {
            backend.StopWatchingWindows();
            bool restored = true;
            for (int i = owned.Count - 1; i >= 0; i--)
                try
                {
                    // PID 或 HWND 被复用后不再操作原图标。
                    if (!backend.Matches(owned[i].Target) || backend.SetHidden(owned[i].Target, false))
                        owned.RemoveAt(i);
                    else restored = false;
                }
                catch { restored = false; }
            if (owned.Count == 0) path = "";
            return restored;
        }
        internal void Forget()
        { lock (gate) { backend.StopWatchingWindows(); owned.Clear(); path = ""; } }

        internal static bool ValidIconRect(int hresult, int left, int top, int right, int bottom)
        {
            // Windows 11 对关闭的溢出区返回 S_FALSE，仍表示查询成功。
            return hresult >= 0 && right > left && bottom > top;
        }
    }

    internal sealed class RtssTrayIconNativeBackend : IRtssTrayIconBackend
    {
        private delegate bool EnumWindow(IntPtr window, IntPtr parameter);
        private delegate void WinEvent(IntPtr hook, uint eventId, IntPtr window, int objectId, int childId, uint thread, uint time);
        [StructLayout(LayoutKind.Sequential)] private struct Message
        {
            internal IntPtr Window;
            internal uint Id;
            internal UIntPtr WParam;
            internal IntPtr LParam;
            internal uint Time;
            internal int X, Y;
            internal uint Private;
        }
        [StructLayout(LayoutKind.Sequential)] private struct Rect { internal int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct IconIdentifier
        { internal uint Size; internal IntPtr Window; internal uint Id; internal Guid Guid; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct IconData
        {
            internal uint Size;
            internal IntPtr Window;
            internal uint Id, Flags, Callback;
            internal IntPtr Icon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string Tip;
            internal uint State, StateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] internal string Info;
            internal uint Version;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] internal string Title;
            internal uint InfoFlags;
            internal Guid Guid;
            internal IntPtr Balloon;
        }
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindow callback, IntPtr parameter);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr window, int command);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
        [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint minimum, uint maximum, IntPtr module, WinEvent callback, uint pid, uint thread, uint flags);
        [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
        [DllImport("user32.dll")] private static extern int GetMessageW(out Message message, IntPtr window, uint minimum, uint maximum);
        [DllImport("user32.dll")] private static extern bool PeekMessageW(out Message message, IntPtr window, uint minimum, uint maximum, uint flags);
        [DllImport("user32.dll")] private static extern bool PostThreadMessageW(uint thread, uint message, UIntPtr wparam, IntPtr lparam);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out int pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr window, StringBuilder text, int maximum);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr window, StringBuilder text, int maximum);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowW(string name, string title);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder path, ref int length);
        [DllImport("kernel32.dll")] private static extern bool GetProcessTimes(IntPtr process, out long birth, out long exit, out long kernel, out long user);
        [DllImport("shell32.dll")] private static extern int Shell_NotifyIconGetRect(ref IconIdentifier identifier, out Rect rectangle);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIconW(uint message, ref IconData data);

        public long Clock { get { return unchecked((long)Native.GetTickCount64()); } }
        public long ShellIdentity
        {
            get
            {
                IntPtr window = FindWindowW("Shell_TrayWnd", null);
                int pid;
                if (window == IntPtr.Zero || GetWindowThreadProcessId(window, out pid) == 0 || pid <= 0) return 0;
                return ((long)pid << 32) | unchecked((uint)window.ToInt64());
            }
        }
        private static bool IsMainWindow(IntPtr window)
        {
            var text = new StringBuilder(256);
            if (GetWindowTextW(window, text, text.Capacity) <= 0 || text.ToString() != "RivaTunerStatisticsServer") return false;
            text.Length = 0;
            if (GetClassNameW(window, text, text.Capacity) <= 0) return false;
            string[] parts = text.ToString().Split(':');
            return parts.Length >= 3 && parts[0] == "Afx" && parts[2] == "20";
        }
        private static bool TryIdentity(IntPtr window, string expected, out RtssTrayIconTarget target)
        {
            target = null;
            int pid;
            if (GetWindowThreadProcessId(window, out pid) == 0 || pid <= 4) return false;
            IntPtr process = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (process == IntPtr.Zero) return false;
            try
            {
                var image = new StringBuilder(32768); int size = image.Capacity;
                long birth, exit, kernel, user;
                if (!QueryFullProcessImageNameW(process, 0, image, ref size)
                    || !String.Equals(System.IO.Path.GetFullPath(image.ToString()), expected, StringComparison.OrdinalIgnoreCase)
                    || !GetProcessTimes(process, out birth, out exit, out kernel, out user) || birth <= 0 || exit != 0) return false;
                int currentPid;
                if (GetWindowThreadProcessId(window, out currentPid) == 0 || currentPid != pid) return false;
                target = new RtssTrayIconTarget { Pid = pid, Birth = birth, Window = window, Path = expected };
                return true;
            }
            catch { return false; }
            finally { Native.CloseHandle(process); }
        }
        public IEnumerable<RtssTrayIconTarget> Find(string path)
        {
            var matches = new List<RtssTrayIconTarget>();
            EnumWindows(delegate(IntPtr window, IntPtr parameter) {
                RtssTrayIconTarget target;
                if (IsMainWindow(window) && TryIdentity(window, path, out target)) matches.Add(target);
                return true;
            }, IntPtr.Zero);
            return matches;
        }
        public bool Matches(RtssTrayIconTarget target)
        {
            if (target == null || target.Window == IntPtr.Zero) return false;
            RtssTrayIconTarget current;
            return TryIdentity(target.Window, target.Path, out current) && current.Pid == target.Pid && current.Birth == target.Birth;
        }
        public bool HasIcon(RtssTrayIconTarget target)
        {
            if (!Matches(target) || !IsMainWindow(target.Window)) return false;
            var identifier = new IconIdentifier { Size = (uint)Marshal.SizeOf(typeof(IconIdentifier)),
                Window = target.Window, Id = unchecked((uint)target.Window.ToInt64()) };
            Rect rectangle;
            int result = Shell_NotifyIconGetRect(ref identifier, out rectangle);
            return RtssTrayIconController.ValidIconRect(result, rectangle.Left, rectangle.Top, rectangle.Right, rectangle.Bottom);
        }
        public bool SetHidden(RtssTrayIconTarget target, bool hidden)
        {
            if (!Matches(target) || !IsMainWindow(target.Window)) return false;
            // RTSS 7.3.7 的图标标识为窗口句柄，只改隐藏状态。
            var data = new IconData { Size = (uint)Marshal.SizeOf(typeof(IconData)), Window = target.Window,
                Id = unchecked((uint)target.Window.ToInt64()), Flags = 8, StateMask = 1, State = hidden ? 1u : 0u,
                Tip = "", Info = "", Title = "" };
            return Shell_NotifyIconW(1, ref data);
        }

        public IEnumerable<RtssTrayIconTarget> FindWindows(RtssTrayIconTarget owner)
        {
            var windows = new List<RtssTrayIconTarget>();
            if (!Matches(owner)) return windows;
            EnumWindows(delegate(IntPtr window, IntPtr parameter) {
                int pid;
                RtssTrayIconTarget target;
                if (IsWindowVisible(window) && GetWindowThreadProcessId(window, out pid) != 0 && pid == owner.Pid
                    && TryIdentity(window, owner.Path, out target) && target.Birth == owner.Birth) windows.Add(target);
                return true;
            }, IntPtr.Zero);
            return windows;
        }

        public bool HideWindow(RtssTrayIconTarget target)
        {
            // 只隐藏 RTSS 自身窗口，不能操作注入游戏的窗口。
            if (!Matches(target) || GetAncestor(target.Window, 2) != target.Window) return false;
            return !IsWindowVisible(target.Window) || ShowWindowAsync(target.Window, 0);
        }

        private readonly object watchersGate = new object();
        private readonly List<WindowWatch> watchers = new List<WindowWatch>();

        public void WatchWindows(RtssTrayIconTarget owner)
        {
            if (!Matches(owner) || !IsMainWindow(owner.Window)) return;
            lock (watchersGate)
            {
                for (int i = watchers.Count - 1; i >= 0; i--)
                {
                    WindowWatch watch = watchers[i];
                    if (!watch.Alive || !Matches(watch.Owner))
                    { watch.Stop(); watchers.RemoveAt(i); }
                    else if (watch.Owner.Pid == owner.Pid && watch.Owner.Birth == owner.Birth
                        && watch.Owner.Window == owner.Window) return;
                }
                var added = new WindowWatch(this, owner);
                watchers.Add(added);
                added.Start();
            }
        }

        public void StopWatchingWindows()
        {
            lock (watchersGate)
            {
                foreach (WindowWatch watch in watchers) watch.Stop();
                watchers.Clear();
            }
        }

        // WinEvent 回调线程必须维持消息循环。
        private sealed class WindowWatch
        {
            private readonly RtssTrayIconNativeBackend backend;
            internal readonly RtssTrayIconTarget Owner;
            private readonly object gate = new object();
            private readonly Thread thread;
            private readonly WinEvent callback;
            private bool stopped;
            private uint threadId;
            internal WindowWatch(RtssTrayIconNativeBackend backend, RtssTrayIconTarget owner)
            {
                this.backend = backend;
                Owner = new RtssTrayIconTarget { Pid = owner.Pid, Birth = owner.Birth, Window = owner.Window, Path = owner.Path };
                callback = OnShow;
                thread = new Thread(Run) { IsBackground = true, Name = "Pavise RTSS window events" };
            }
            internal bool Alive { get { lock (gate) { return !stopped && thread.IsAlive; } } }
            internal void Start() { thread.Start(); }
            internal void Stop()
            {
                lock (gate)
                {
                    stopped = true;
                    if (threadId != 0) PostThreadMessageW(threadId, 0x0012, UIntPtr.Zero, IntPtr.Zero);
                }
            }
            private void Run()
            {
                IntPtr hook = IntPtr.Zero;
                GCHandle callbackRoot = default(GCHandle);
                try
                {
                    Message message;
                    PeekMessageW(out message, IntPtr.Zero, 0, 0, 0); // 先创建消息队列，确保停止消息可达。
                    lock (gate)
                    {
                        if (stopped || !backend.Matches(Owner)) return;
                        threadId = GetCurrentThreadId();
                        callbackRoot = GCHandle.Alloc(callback);
                        hook = SetWinEventHook(0x8002, 0x8002, IntPtr.Zero, callback, unchecked((uint)Owner.Pid), 0, 0);
                        if (hook == IntPtr.Zero) return;
                        foreach (RtssTrayIconTarget window in backend.FindWindows(Owner)) backend.HideWindow(window);
                    }
                    while (GetMessageW(out message, IntPtr.Zero, 0, 0) > 0) { }
                }
                catch {   }
                finally
                {
                    if (hook != IntPtr.Zero) UnhookWinEvent(hook);
                    if (callbackRoot.IsAllocated) callbackRoot.Free();
                    lock (gate) { stopped = true; threadId = 0; }
                }
            }
            private void OnShow(IntPtr hook, uint eventId, IntPtr window, int objectId, int childId, uint eventThread, uint time)
            {
                if (eventId != 0x8002 || objectId != 0 || childId != 0 || window == IntPtr.Zero) return;
                lock (gate)
                {
                    if (stopped) return;
                    try
                    {
                        RtssTrayIconTarget target;
                        if (backend.Matches(Owner) && TryIdentity(window, Owner.Path, out target)
                            && target.Pid == Owner.Pid && target.Birth == Owner.Birth) backend.HideWindow(target);
                    }
                    catch {   }
                }
            }
        }
    }
}
