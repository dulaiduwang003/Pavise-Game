// @author bdth 2074055628@qq.com
// 文件用途 用与 Pavise 相同的窗口证据逻辑诊断为何测试游戏未被选举

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace PaviseDetectDiag
{
    internal static class Program
    {
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr h, out NativeRect r);
        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr h, uint cmd);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr h, int index);
        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr param);
        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int val, int size);
        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
        [DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
        [DllImport("user32.dll")]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextW(IntPtr h, StringBuilder text, int max);

        private delegate bool EnumWindowsProc(IntPtr window, IntPtr param);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MonitorInfo
        {
            public int Size;
            public NativeRect Monitor;
            public NativeRect Work;
            public uint Flags;
        }

        private static int Main(string[] args)
        {
            try { SetProcessDpiAwarenessContext((IntPtr)(-4)); } catch { }
            string wanted = args.Length > 0 ? args[0] : "FrameBench";

            IntPtr fore = GetForegroundWindow();
            uint forePid;
            GetWindowThreadProcessId(fore, out forePid);
            Console.WriteLine("foreground hwnd=0x" + fore.ToInt64().ToString("X")
                + " pid=" + forePid + " name=" + NameOf((int)forePid));
            NativeRect fr;
            if (GetWindowRect(fore, out fr))
            {
                Console.WriteLine("  rect = " + Fmt(fr) + "  (" + (fr.Right - fr.Left)
                    + "x" + (fr.Bottom - fr.Top) + ")");
                IntPtr mon = MonitorFromWindow(fore, 2);
                var mi = new MonitorInfo();
                mi.Size = Marshal.SizeOf(typeof(MonitorInfo));
                if (GetMonitorInfo(mon, ref mi))
                {
                    Console.WriteLine("  monitor = " + Fmt(mi.Monitor) + "  ("
                        + (mi.Monitor.Right - mi.Monitor.Left) + "x"
                        + (mi.Monitor.Bottom - mi.Monitor.Top) + ")");
                    long wa = (long)(fr.Right - fr.Left) * (fr.Bottom - fr.Top);
                    long ma = (long)(mi.Monitor.Right - mi.Monitor.Left)
                        * (mi.Monitor.Bottom - mi.Monitor.Top);
                    double pct = ma > 0 ? wa * 100.0 / ma : 0;
                    Console.WriteLine("  coverage = " + pct.ToString("F1")
                        + "%   (fullscreen needs >= 97%)");
                }
            }

            Console.WriteLine();
            Console.WriteLine("--- windows owned by '" + wanted + "' ---");
            var pids = new HashSet<int>();
            foreach (Process p in Process.GetProcessesByName(wanted))
            {
                pids.Add(p.Id);
                Console.WriteLine("process pid=" + p.Id);
                p.Dispose();
            }
            if (pids.Count == 0) Console.WriteLine("(no such process running)");

            EnumWindows(delegate(IntPtr window, IntPtr state)
            {
                uint pid;
                GetWindowThreadProcessId(window, out pid);
                if (!pids.Contains((int)pid)) return true;
                var title = new StringBuilder(256);
                GetWindowTextW(window, title, title.Capacity);
                bool visible = IsWindowVisible(window);
                bool owned = GetWindow(window, 4) != IntPtr.Zero;
                bool tool = (GetWindowLong(window, -20) & 0x80) != 0;
                bool iconic = IsIconic(window);
                int cloaked = 0;
                DwmGetWindowAttribute(window, 14, out cloaked, sizeof(int));
                NativeRect r;
                bool gotRect = GetWindowRect(window, out r);
                bool passesVisiblePidFilter = visible && !owned && !tool && !iconic
                    && cloaked == 0 && gotRect && r.Right > r.Left && r.Bottom > r.Top;
                Console.WriteLine("  hwnd=0x" + window.ToInt64().ToString("X")
                    + " title='" + title + "'"
                    + " visible=" + visible + " owned=" + owned + " tool=" + tool
                    + " iconic=" + iconic + " cloaked=" + cloaked
                    + " rect=" + (gotRect ? Fmt(r) : "?")
                    + "  -> countsAsVisible=" + passesVisiblePidFilter
                    + " isForeground=" + (window == fore));
                return true;
            }, IntPtr.Zero);

            return 0;
        }

        private static string Fmt(NativeRect r)
        {
            return "(" + r.Left + "," + r.Top + ")-(" + r.Right + "," + r.Bottom + ")";
        }

        private static string NameOf(int pid)
        {
            try { using (Process p = Process.GetProcessById(pid)) return p.ProcessName; }
            catch { return "?"; }
        }
    }
}
