// @author bdth 2074055628@qq.com
// 文件用途 对局期间让 DWM 合成线程注册进 MMCSS 实时档 纯会话级 进程退出自动失效
using System;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal static class DwmBoost
    {
        // 无边框和窗口化的每一帧都过 DWM 合成 游戏自己吃满 CPU 时合成线程
        //   被抢占就是掉帧 这个调用让 DWM 和 csrss 拿到 MMCSS 保护
        //   注册随本进程的 DWM 连接存续 独占全屏时 DWM 自行反注册 无害
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
