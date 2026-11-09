using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
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

        [DllImport("shell32.dll")]
        private static extern void DragAcceptFiles(IntPtr hwnd, bool accept);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern uint DragQueryFileW(IntPtr hDrop, uint index, StringBuilder file, uint cch);

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
                IntPtr intPtr = FindWindow("Shell_TrayWnd", null);
                if (intPtr == IntPtr.Zero)
                {
                    return false;
                }
                return FindWindowEx(intPtr, IntPtr.Zero, "TrayNotifyWnd", null) != IntPtr.Zero;
            }
            catch
            {
                return false;
            }
        }

        public static void AllowTaskbarCreatedMessage()
        {
            try
            {
                uint registerWindowMessageResult = RegisterWindowMessage("TaskbarCreated");
                if (registerWindowMessageResult != 0)
                {
                    ChangeWindowMessageFilter(registerWindowMessageResult, 1u);
                }
            }
            catch
            {
            }
        }

        public static void EnableElevatedFileDrop(IntPtr hwnd)
        {
            try
            {
                ChangeWindowMessageFilter(563u, 1u);
                ChangeWindowMessageFilter(74u, 1u);
                ChangeWindowMessageFilter(73u, 1u);
                ChangeWindowMessageFilterEx(hwnd, 563u, 1u, IntPtr.Zero);
                ChangeWindowMessageFilterEx(hwnd, 74u, 1u, IntPtr.Zero);
                ChangeWindowMessageFilterEx(hwnd, 73u, 1u, IntPtr.Zero);
                DragAcceptFiles(hwnd, true);
            }
            catch
            {
            }
        }

        public static string[] ReadDroppedFiles(IntPtr hDrop)
        {
            try
            {
                uint dragQueryFileWResult = DragQueryFileW(hDrop, uint.MaxValue, null, 0u);
                List<string> list = new List<string>();
                StringBuilder stringBuilder = new StringBuilder(1024);
                for (uint iterationIndex = 0u; iterationIndex < dragQueryFileWResult; iterationIndex++)
                {
                    stringBuilder.Length = 0;
                    if (DragQueryFileW(hDrop, iterationIndex, stringBuilder, (uint)stringBuilder.Capacity) != 0)
                    {
                        list.Add(stringBuilder.ToString());
                    }
                }
                return list.ToArray();
            }
            catch
            {
                return new string[0];
            }
            finally
            {
                try
                {
                    DragFinish(hDrop);
                }
                catch
                {
                }
            }
        }

        public static void Dark(Control control)
        {
            EventHandler eventHandler = delegate
            {
                RefreshDark(control);
            };
            control.HandleCreated += eventHandler;
            if (control.IsHandleCreated)
            {
                eventHandler(null, EventArgs.Empty);
            }
        }

        internal static void RefreshDark(Control control)
        {
            if (control == null || control.IsDisposed || !control.IsHandleCreated)
            {
                return;
            }
            try
            {
                SetWindowTheme(control.Handle, Theme.LightMode ? "Explorer" : "DarkMode_Explorer", null);
            }
            catch
            {
            }
        }

        [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
        public static extern bool SystemParametersInfoGet(uint action, uint param, ref int value, uint winIni);

        [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
        public static extern bool SystemParametersInfoSet(uint action, uint param, IntPtr value, uint winIni);

        public static void RoundCorners(IntPtr hwnd)
        {
            try
            {
                int cornerPreference = 2;
                DwmSetWindowAttribute(hwnd, 33, ref cornerPreference, 4);
            }
            catch
            {
            }
        }

    }
}
