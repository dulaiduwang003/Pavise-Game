using System;

namespace PaviseApp
{
    // 保持亮屏的 applied 和 restored 日志限频 每局开头清零 实例归 GameMode 所有
    // 自己不加锁 调用方都持有游戏模式的 sync
    internal sealed class DisplayAwakeLogThrottle
    {
        private long appliedLogTicks;
        private long restoredLogTicks;
        private bool finalRestoreLogged;
        private string recoveryState;

#if PAVISE_SELFTEST
        internal Func<long> NowForTest;
#endif

        internal bool FinalRestoreLogged
        {
            get { return finalRestoreLogged; }
        }

        internal void Reset()
        {
            appliedLogTicks = (restoredLogTicks = 0L);
            finalRestoreLogged = false;
            recoveryState = null;
        }

        // 失败后的第一次成功总要记 同一状态一分钟内只记一次 退局的最终恢复只记一次
        internal bool ShouldLog(string state, bool sessionEnding)
        {
            if (state == "failed")
            {
                recoveryState = "applied";
                return true;
            }
            if (state == "restore-failed")
            {
                recoveryState = "restored";
                return true;
            }
            if (state != "applied" && state != "restored")
            {
                return true;
            }
            bool endingRestored = state == "restored" && sessionEnding;
            bool sameRecoveryState = recoveryState == state;
            if (sameRecoveryState)
            {
                recoveryState = null;
            }
            if (endingRestored && finalRestoreLogged && !sameRecoveryState)
            {
                return false;
            }
            long ticks = DateTime.UtcNow.Ticks;
#if PAVISE_SELFTEST
            if (NowForTest != null) ticks = NowForTest();
#endif
            long lastLogTicks = ((state == "applied") ? appliedLogTicks : restoredLogTicks);
            if (!endingRestored && !sameRecoveryState && lastLogTicks != 0 && ticks >= lastLogTicks && ticks - lastLogTicks < 600000000)
            {
                return false;
            }
            if (state == "applied")
            {
                appliedLogTicks = ticks;
            }
            else
            {
                restoredLogTicks = ticks;
            }
            if (endingRestored)
            {
                finalRestoreLogged = true;
            }
            return true;
        }
    }
}
