// @author bdth 2074055628@qq.com
// File purpose Register the DWM composition thread into the MMCSS realtime class during a match; session-only, expires when the process exits
using System;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal static class DwmBoost
    {
        // Every frame of borderless and windowed goes through DWM composition; when the game saturates the CPU the composition thread
        //   getting preempted means dropped frames; this call gives DWM and csrss MMCSS protection
        //   Registration lives as long as this process's DWM connection; DWM deregisters itself in exclusive fullscreen, harmless
        [DllImport("dwmapi.dll")]
        private static extern int DwmEnableMMCSS(bool enable);

        private static readonly object lk = new object();
        private static bool active;

        public static bool Activate()
        {
            lock (lk)
            {
                if (active) return true;
                if (DwmEnableMMCSS(true) != 0) return false;
                active = true;
                Logger.Log(Lang.T("log.dwmboost.1"));
                return true;
            }
        }

        public static bool Restore()
        {
            lock (lk)
            {
                if (!active) return true;
                if (DwmEnableMMCSS(false) != 0) return false;
                active = false;
                Logger.Log(Lang.T("log.dwmboost.2"));
                return true;
            }
        }
    }
}
