// 作者：bdth 2074055628@qq.com
using System;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal static class DwmBoost
    {
        // MMCSS 请求成功不等于帧延迟降低。
        [DllImport("dwmapi.dll")]
        private static extern int DwmEnableMMCSS(bool enable);

        private static int Request(bool enabled)
        {
#if PAVISE_SELFTEST
            if (RequestForTest == null) throw new InvalidOperationException("Unmocked DWM request");
            return RequestForTest(enabled);
#else
            return DwmEnableMMCSS(enabled);
#endif
        }
#if PAVISE_SELFTEST
        internal static Func<bool, int> RequestForTest;
#endif
        private static readonly object lk = new object();
        private static bool active;

        public static bool Activate()
        {
            lock (lk)
            {
                if (active) return true;
                if (Request(true) != 0) return false;
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
                if (Request(false) != 0) return false;
                active = false;
                Logger.Log(Lang.T("log.dwmboost.2"));
                return true;
            }
        }
    }
}
