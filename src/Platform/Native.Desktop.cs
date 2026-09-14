// @author bdth 2074055628@qq.com
// File purpose Wraps native window, theme, DPI and desktop timer interfaces
using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PaviseApp
{
    internal static partial class Native
    {
        [DllImport("user32.dll")]
        public static extern bool ReleaseCapture();
        [DllImport("user32.dll")]
        public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
        [DllImport("user32.dll")]
        public static extern uint GetDpiForSystem();
        [DllImport("user32.dll")]
        public static extern uint GetDpiForWindow(IntPtr hwnd);
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr hwnd, string sub, string list);
        [DllImport("uxtheme.dll", EntryPoint = "#135")]
        public static extern int SetPreferredAppMode(int mode);
        [DllImport("winmm.dll")]
        public static extern uint timeBeginPeriod(uint ms);

        // 0.5ms is only reachable via ntdll, winmm's minimum is 1ms, unit is 100ns
        [DllImport("ntdll.dll")]
        public static extern int NtSetTimerResolution(uint desired, bool set, out uint actual);
        [DllImport("winmm.dll")]
        public static extern uint timeEndPeriod(uint ms);

        public const int WM_DROPFILES = 0x0233;
        private const uint MSGFLT_ALLOW = 1;

        [DllImport("shell32.dll")]
        private static extern void DragAcceptFiles(IntPtr hwnd, bool accept);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern uint DragQueryFileW(IntPtr hDrop, uint index, System.Text.StringBuilder file, uint cch);
        [DllImport("shell32.dll")]
        private static extern void DragFinish(IntPtr hDrop);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool ChangeWindowMessageFilterEx(IntPtr hwnd, uint message, uint action, IntPtr changeInfo);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool ChangeWindowMessageFilter(uint message, uint action);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint RegisterWindowMessage(string name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string cls, string name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string name);

        public static bool NotificationAreaPresent()
        {
            try
            {
                IntPtr tray = FindWindow("Shell_TrayWnd", null);
                if (tray == IntPtr.Zero) return false;
                return FindWindowEx(tray, IntPtr.Zero, "TrayNotifyWnd", null) != IntPtr.Zero;
            }
            catch { return false; }
        }

        public static void AllowTaskbarCreatedMessage()
        {
            try
            {
                uint msg = RegisterWindowMessage("TaskbarCreated");
                if (msg != 0) ChangeWindowMessageFilter(msg, MSGFLT_ALLOW);
            }
            catch { }
        }

        public static void EnableElevatedFileDrop(IntPtr hwnd)
        {
            try
            {
                ChangeWindowMessageFilter(WM_DROPFILES, MSGFLT_ALLOW);
                ChangeWindowMessageFilter(0x004A, MSGFLT_ALLOW);
                ChangeWindowMessageFilter(0x0049, MSGFLT_ALLOW);
                ChangeWindowMessageFilterEx(hwnd, WM_DROPFILES, MSGFLT_ALLOW, IntPtr.Zero);
                ChangeWindowMessageFilterEx(hwnd, 0x004A, MSGFLT_ALLOW, IntPtr.Zero);
                ChangeWindowMessageFilterEx(hwnd, 0x0049, MSGFLT_ALLOW, IntPtr.Zero);
                DragAcceptFiles(hwnd, true);
            }
            catch { }
        }

        public static string[] ReadDroppedFiles(IntPtr hDrop)
        {
            try
            {
                uint count = DragQueryFileW(hDrop, 0xFFFFFFFF, null, 0);
                var files = new System.Collections.Generic.List<string>();
                var buffer = new System.Text.StringBuilder(1024);
                for (uint i = 0; i < count; i++)
                {
                    buffer.Length = 0;
                    if (DragQueryFileW(hDrop, i, buffer, (uint)buffer.Capacity) > 0)
                        files.Add(buffer.ToString());
                }
                return files.ToArray();
            }
            catch { return new string[0]; }
            finally { try { DragFinish(hDrop); } catch { } }
        }

        public const int WM_NCLBUTTONDOWN = 0xA1;
        public const int HT_CAPTION = 0x2;
        public const int WM_ENTERSIZEMOVE = 0x0231;
        public const int WM_EXITSIZEMOVE = 0x0232;

        public static void Dark(Control control)
        {
            EventHandler apply = delegate
            {
                try
                {
                    SetWindowTheme(control.Handle,
                        Theme.LightMode ? "Explorer" : "DarkMode_Explorer", null);
                }
                catch { }
            };
            control.HandleCreated += apply;
            if (control.IsHandleCreated) apply(null, EventArgs.Empty);
        }

        public const uint SPI_GETUIEFFECTS = 0x103E;
        public const uint SPI_SETUIEFFECTS = 0x103F;
        public const uint SPIF_SENDCHANGE = 0x0002;

        [DllImport("user32.dll", SetLastError = true, EntryPoint = "SystemParametersInfoW")]
        public static extern bool SystemParametersInfoGet(uint action, uint param, ref int value, uint winIni);
        [DllImport("user32.dll", SetLastError = true, EntryPoint = "SystemParametersInfoW")]
        public static extern bool SystemParametersInfoSet(uint action, uint param, IntPtr value, uint winIni);

        public static void RoundCorners(IntPtr hwnd)
        {
            try
            {
                int preference = 2;
                DwmSetWindowAttribute(hwnd, 33, ref preference, sizeof(int));
            }
            catch { }
        }
    }
}
