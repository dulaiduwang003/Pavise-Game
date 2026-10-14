// 作者：bdth 2074055628@qq.com
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal sealed class ForegroundGameWindowSnapshot
    {
        internal int Pid;
        internal bool FullscreenLike;
    }

    internal static partial class GameSessionDetector
    {
        internal static bool TryCaptureProcessIdentity(
            int pid, int ownerSession,
            out GameProcessSnapshot identity)
        {
            identity = null;
            if (pid <= 0 || ownerSession < 0) return false;
            IntPtr h = Native.OpenProcess(
                Native.PROCESS_QUERY_LIMITED_INFORMATION
                    | Native.SYNCHRONIZE,
                false, pid);
            if (h == IntPtr.Zero) return false;
            try
            {
                long creation;
                long exit;
                long kernel;
                long user;
                if (!GetProcessTimes(
                        h, out creation, out exit,
                        out kernel, out user)
                    || creation <= 0)
                    return false;
                string path = Native.ImagePath(h);
                string name = ImageNameFromVerifiedPath(path);
                int session;
                if (string.IsNullOrEmpty(name)
                    || !Native.TryGetLiveProcessSessionId(
                        h, pid, out session)
                    || session != ownerSession)
                    return false;
                identity = new GameProcessSnapshot
                {
                    Pid = pid,
                    ParentPid = Native.ParentProcessId(h),
                    Creation = creation,
                    Name = name,
                    Path = path
                };
                return true;
            }
            finally { Native.CloseHandle(h); }
        }

        internal static string ImageNameFromVerifiedPath(
            string imagePath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(imagePath))
                    return null;
                string leaf = Path.GetFileName(imagePath.Trim());
                if (string.IsNullOrWhiteSpace(leaf))
                    return null;
                string name = Path.GetFileNameWithoutExtension(leaf);
                return string.IsNullOrWhiteSpace(name)
                    ? null : name.Trim();
            }
            catch { return null; }
        }

        private static void CaptureWindowEvidence(
            IList<GameProcessSnapshot> snapshot)
        {
            if (snapshot == null || snapshot.Count == 0)
                return;
            int foreground = ForegroundPid();
            bool foregroundFullscreen = foreground > 0
                && ForegroundWindowFullscreenLike(foreground);
            HashSet<int> visible = VisibleWindowPids(false);
            foreach (GameProcessSnapshot identity in snapshot)
            {
                if (identity == null || identity.Pid <= 0
                    || identity.Creation <= 0)
                    continue;
                bool foregroundClaim =
                    identity.Pid == foreground;
                bool visibleClaim =
                    visible.Contains(identity.Pid);
                if (!foregroundClaim && !visibleClaim)
                    continue;

                if (!IsLiveProcessCreation(
                        identity.Pid, identity.Creation))
                    continue;
                identity.Foreground = foregroundClaim;
                identity.Visible = visibleClaim;
                identity.FullscreenLike =
                    foregroundClaim && foregroundFullscreen;
            }
        }

        private static bool IsLiveProcessCreation(
            int pid, long expectedCreation)
        {
            if (pid <= 0 || expectedCreation <= 0)
                return false;
            IntPtr handle = Native.OpenProcess(
                Native.PROCESS_QUERY_LIMITED_INFORMATION
                    | Native.SYNCHRONIZE,
                false, pid);
            if (handle == IntPtr.Zero) return false;
            try
            {
                long creation;
                long exit;
                long kernel;
                long user;
                int session;
                return GetProcessTimes(
                        handle, out creation, out exit,
                        out kernel, out user)
                    && creation == expectedCreation
                    && Native.TryGetLiveProcessSessionId(
                        handle, pid, out session);
            }
            finally { Native.CloseHandle(handle); }
        }

#if PAVISE_SELFTEST
        internal static bool HasUserFacingWindow(Process p)
        {
            try
            {
                IntPtr h = p.MainWindowHandle;
                return h != IntPtr.Zero && IsWindowVisible(h);
            }
            catch { return false; }
        }
#endif

        internal static bool TryForegroundFullscreen(out int pid)
        {
            pid = 0;
            try
            {
                IntPtr window = GetForegroundWindow();
                if (window == IntPtr.Zero) return false;
                uint owner;
                GetWindowThreadProcessId(window, out owner);
                if (owner == 0 || owner > int.MaxValue) return false;
                pid = (int)owner;
                if (!IsWindowVisible(window) || IsIconic(window)) return false;
                NativeRect rect;
                if (!GetWindowRect(window, out rect)) return false;
                return IsFullscreenLikeWindow(window, rect);
            }
            catch { return false; }
        }

        // 证据发布前确认前台窗口及其所有者未变化。
        internal static bool TryForegroundGameWindow(out ForegroundGameWindowSnapshot snapshot)
        {
            snapshot = null;
            try
            {
                IntPtr window = GetForegroundWindow();
                if (window == IntPtr.Zero || !IsWindowVisible(window) || IsIconic(window)
                    || (GetWindowLong(window, GwlExStyle) & WsExToolWindow) != 0)
                    return false;
                uint owner;
                if (GetWindowThreadProcessId(window, out owner) == 0
                    || owner == 0 || owner > int.MaxValue) return false;
                int cloaked;
                if (DwmGetWindowAttribute(window, DwmwaCloaked, out cloaked, sizeof(int)) == 0
                    && cloaked != 0) return false;
                NativeRect rect;
                NativeRect client;
                if (!GetWindowRect(window, out rect) || !RectHasArea(rect)
                    || !TryGetScreenClientRect(window, out client)) return false;
                bool fullscreen = CaptureFullscreenEvidence(window, rect, client);
                uint ownerNow;
                if (GetForegroundWindow() != window
                    || GetWindowThreadProcessId(window, out ownerNow) == 0
                    || ownerNow != owner) return false;
                snapshot = new ForegroundGameWindowSnapshot
                {
                    Pid = (int)owner,
                    FullscreenLike = fullscreen
                };
                return true;
            }
            catch { return false; }
        }

        private static bool ForegroundWindowFullscreenLike(int pid)
        {
            int owner;
            return TryForegroundFullscreen(out owner) && owner == pid;
        }

        internal static bool IsFullscreenLikeWindow(IntPtr window, NativeRect rect)
        {
            // 窗口范围放宽仅用于自动入库，仍需独立 GPU 证据。
            try
            {
                int style = GetWindowLong(window, GwlStyle);
                if ((style & WsCaption) == WsCaption) return false;
                IntPtr monitor = MonitorFromWindow(window, MonitorDefaultToNearest);
                if (monitor == IntPtr.Zero) return false;
                var info = new MonitorInfo();
                info.Size = Marshal.SizeOf(typeof(MonitorInfo));
                return GetMonitorInfo(monitor, ref info) && RectCoversMonitor(rect, info.Monitor);
            }
            catch { return false; }
        }

        private static bool CaptureFullscreenEvidence(IntPtr window, NativeRect rect, NativeRect client)
        {
            try
            {
                int style = GetWindowLong(window, GwlStyle);
                IntPtr monitor = MonitorFromWindow(window, MonitorDefaultToNearest);
                if (monitor == IntPtr.Zero) return false;
                var info = new MonitorInfo();
                info.Size = Marshal.SizeOf(typeof(MonitorInfo));
                if (!GetMonitorInfo(monitor, ref info)) return false;
                bool hasCaption = (style & WsCaption) == WsCaption;
                return IsFullscreenLikeGeometry(hasCaption,
                    rect, client, info.Monitor, info.Work);
            }
            catch { return false; }
        }

        private static bool TryGetScreenClientRect(IntPtr window, out NativeRect rect)
        {
            rect = new NativeRect();
            try
            {
                NativeRect client;
                if (!GetClientRect(window, out client) || !RectHasArea(client)) return false;
                var topLeft = new NativePoint { X = client.Left, Y = client.Top };
                var bottomRight = new NativePoint { X = client.Right, Y = client.Bottom };
                if (!ClientToScreen(window, ref topLeft) || !ClientToScreen(window, ref bottomRight))
                    return false;
                rect = new NativeRect
                {
                    Left = topLeft.X, Top = topLeft.Y,
                    Right = bottomRight.X, Bottom = bottomRight.Y
                };
                return RectHasArea(rect);
            }
            catch { return false; }
        }

        // 全屏按客户区覆盖全部显示器边缘判断，不能只看标题栏标志。
        internal static bool IsFullscreenLikeGeometry(bool hasCaption, NativeRect windowRect,
            NativeRect clientRect, NativeRect monitor, NativeRect work)
        {
            if (!RectHasArea(windowRect) || !RectHasArea(clientRect) || !RectHasArea(monitor))
                return false;
            if (hasCaption)
                return RectCoversMonitor(clientRect, monitor) && RectEdgesMatch(clientRect, monitor);
            if (RectCoversMonitor(windowRect, monitor)) return true;
            return RectHasArea(work) && work.Left >= monitor.Left && work.Top >= monitor.Top
                && work.Right <= monitor.Right && work.Bottom <= monitor.Bottom
                && RectEdgesMatch(windowRect, work);
        }

        private static bool RectHasArea(NativeRect rect)
        {
            return rect.Right > rect.Left && rect.Bottom > rect.Top;
        }

        private static bool RectEdgesMatch(NativeRect rect, NativeRect bounds)
        {
            const int tolerance = 4;
            return Math.Abs((long)rect.Left - bounds.Left) <= tolerance
                && Math.Abs((long)rect.Top - bounds.Top) <= tolerance
                && Math.Abs((long)rect.Right - bounds.Right) <= tolerance
                && Math.Abs((long)rect.Bottom - bounds.Bottom) <= tolerance;
        }

        internal static bool RectCoversMonitor(NativeRect rect, NativeRect monitor)
        {
            if (!RectHasArea(rect) || !RectHasArea(monitor)) return false;
            double monitorArea = ((long)monitor.Right - monitor.Left)
                * (double)((long)monitor.Bottom - monitor.Top);
            int left = Math.Max(rect.Left, monitor.Left);
            int top = Math.Max(rect.Top, monitor.Top);
            int right = Math.Min(rect.Right, monitor.Right);
            int bottom = Math.Min(rect.Bottom, monitor.Bottom);
            double covered = right > left && bottom > top
                ? ((long)right - left) * (double)((long)bottom - top) : 0;
            return covered * 100 >= monitorArea * FullscreenCoveragePercent;
        }

        private static readonly object visibleCacheSync = new object();
        private static HashSet<int> visibleCacheAll, visibleCacheShown;
        private static long visibleCacheAllTicks, visibleCacheShownTicks;
        private const long VisibleCacheTtlTicks = 1500 * TimeSpan.TicksPerMillisecond;

        internal static HashSet<int> VisibleWindowPids(bool includeMinimized)
        {
            long now = DateTime.UtcNow.Ticks;
            lock (visibleCacheSync)
            {
                HashSet<int> cached = includeMinimized ? visibleCacheAll : visibleCacheShown;
                long stamp = includeMinimized ? visibleCacheAllTicks : visibleCacheShownTicks;
                if (cached != null && now >= stamp && now - stamp < VisibleCacheTtlTicks) return cached;
            }
            bool succeeded;
            HashSet<int> fresh = VisibleWindowPids(includeMinimized, out succeeded);
            if (!succeeded) return fresh;
            lock (visibleCacheSync)
            {
                if (includeMinimized) { visibleCacheAll = fresh; visibleCacheAllTicks = now; }
                else { visibleCacheShown = fresh; visibleCacheShownTicks = now; }
            }
            return fresh;
        }

        internal static HashSet<int> VisibleWindowPids(bool includeMinimized, out bool succeeded)
        {
            var result = new HashSet<int>();
            succeeded = false;
            try
            {
                succeeded = EnumWindows(delegate(IntPtr window, IntPtr state)
                {
                    try
                    {
                        if (!IsWindowVisible(window)
                            || GetWindow(window, GwOwner) != IntPtr.Zero
                            || (GetWindowLong(window, GwlExStyle) & WsExToolWindow) != 0
                            || !includeMinimized && IsIconic(window))
                            return true;
                        int cloaked;
                        if (DwmGetWindowAttribute(
                                window, DwmwaCloaked, out cloaked, sizeof(int)) == 0
                            && cloaked != 0)
                            return true;
                        NativeRect rect;
                        if (!GetWindowRect(window, out rect)
                            || rect.Right <= rect.Left || rect.Bottom <= rect.Top)
                            return true;
                        uint pid;
                        GetWindowThreadProcessId(window, out pid);
                        if (pid > 0 && pid <= int.MaxValue) result.Add((int)pid);
                    }
                    catch { }
                    return true;
                }, IntPtr.Zero);
            }
            catch { }
            return result;
        }

        internal static int ForegroundPid()
        {
            try
            {
                uint pid;
                GetWindowThreadProcessId(GetForegroundWindow(), out pid);
                return (int)pid;
            }
            catch { return -1; }
        }

        private delegate bool EnumWindowsCallback(IntPtr window, IntPtr state);
        [StructLayout(LayoutKind.Sequential)]
        internal struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MonitorInfo
        {
            public int Size;
            public NativeRect Monitor;
            public NativeRect Work;
            public uint Flags;
        }

        private const uint GwOwner = 4;
        private const int GwlStyle = -16;
        private const int GwlExStyle = -20;
        private const int WsExToolWindow = 0x80;
        private const int WsCaption = 0x00C00000;
        private const uint DwmwaCloaked = 14;
        private const uint MonitorDefaultToNearest = 2;
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool EnumWindows(
            EnumWindowsCallback callback, IntPtr state);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(
            IntPtr window, uint command);
        [DllImport("user32.dll")] private static extern int GetWindowLong(
            IntPtr window, int index);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(
            IntPtr window, out NativeRect rect);
        [DllImport("user32.dll")] private static extern bool GetClientRect(
            IntPtr window, out NativeRect rect);
        [DllImport("user32.dll")] private static extern bool ClientToScreen(
            IntPtr window, ref NativePoint point);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(
            IntPtr hwnd, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessTimes(
            IntPtr process, out long creation, out long exit,
            out long kernel, out long user);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(
            IntPtr window, uint attribute, out int value, int size);
    }
}
