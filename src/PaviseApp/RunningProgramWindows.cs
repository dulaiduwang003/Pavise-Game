using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    // 选择器按需发现进程，不依赖游戏渲染窗口的准入条件。
    internal static class RunningProgramWindows
    {
        private delegate bool WindowCallback(IntPtr window, IntPtr state);

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect { public int Left, Top, Right, Bottom; }

        internal static HashSet<int> Capture(out bool succeeded)
        {
            var result = new HashSet<int>();
            succeeded = false;
            try
            {
                succeeded = EnumWindows(delegate(IntPtr window, IntPtr state)
                {
                    try
                    {
                        Rect bounds;
                        bool hasBounds = GetWindowRect(window, out bounds)
                            && bounds.Right > bounds.Left && bounds.Bottom > bounds.Top;
                        if (IsApplicationWindow(IsWindowVisible(window), GetWindow(window, 4) != IntPtr.Zero,
                            unchecked((uint)GetWindowLong(window, -16)),
                            unchecked((uint)GetWindowLong(window, -20)), hasBounds))
                        {
                            uint pid;
                            GetWindowThreadProcessId(window, out pid);
                            if (pid > 0 && pid <= int.MaxValue) result.Add((int)pid);
                        }
                    }
                    catch { }
                    return true;
                }, IntPtr.Zero);
            }
            catch { }
            return result;
        }

        internal static bool IsApplicationWindow(bool visible, bool owned, uint style, uint extendedStyle, bool hasBounds)
        {
            const uint child = 0x40000000, caption = 0x00c00000, appWindow = 0x00040000, toolWindow = 0x00000080;
            if (!hasBounds || (style & child) != 0) return false;
            // 托盘及虚拟桌面应用的隐藏或最小化主窗口仍然有效。
            if ((extendedStyle & appWindow) != 0) return true;
            return !owned && (extendedStyle & toolWindow) == 0
                && (visible || (style & caption) == caption);
        }

        [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, IntPtr state);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr window, int index);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect bounds);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    }
}
