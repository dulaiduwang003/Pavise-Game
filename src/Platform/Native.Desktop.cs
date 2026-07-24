// @author bdth 2074055628@qq.com
// 文件用途 封装窗口 主题 DPI 和桌面计时器原生接口

using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AegisApp
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
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr hwnd, string sub, string list);
        [DllImport("uxtheme.dll", EntryPoint = "#135")]
        public static extern int SetPreferredAppMode(int mode);
        [DllImport("winmm.dll")]
        public static extern uint timeBeginPeriod(uint ms);
        [DllImport("winmm.dll")]
        public static extern uint timeEndPeriod(uint ms);

        public const int WM_NCLBUTTONDOWN = 0xA1;
        public const int HT_CAPTION = 0x2;

        public static void Dark(Control control)
        {
            EventHandler apply = delegate
            {
                try { SetWindowTheme(control.Handle, "DarkMode_Explorer", null); }
                catch { }
            };
            control.HandleCreated += apply;
            if (control.IsHandleCreated) apply(null, EventArgs.Empty);
        }

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
