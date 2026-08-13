// @author bdth 2074055628@qq.com
// 文件用途 游戏期间阻止息屏与睡眠 纯会话级 进程退出自动失效

using System;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal static class DisplayAwake
    {
        private const uint EsContinuous = 0x80000000;
        private const uint EsDisplayRequired = 0x00000002;
        private const uint EsSystemRequired = 0x00000001;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint SetThreadExecutionState(uint flags);

        private static readonly object lk = new object();
        private static bool active;

        public static bool Activate()
        {
            lock (lk)
            {
                if (active) return true;
                uint previous = SetThreadExecutionState(EsContinuous | EsDisplayRequired | EsSystemRequired);
                if (previous == 0) return false;
                active = true;
                Logger.Log("息屏防护 游戏期间不熄屏 不睡眠");
                return true;
            }
        }

        public static bool Restore()
        {
            lock (lk)
            {
                if (!active) return true;
                uint previous = SetThreadExecutionState(EsContinuous);
                if (previous == 0) return false;
                active = false;
                Logger.Log("息屏防护已解除");
                return true;
            }
        }
    }
}
