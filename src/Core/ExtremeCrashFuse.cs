// @author bdth 2074055628@qq.com
// 文件用途 极限崩溃保险丝 解锁后系统多次非正常重启就自动回锁 按账本还原环境项
using System;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;

namespace PaviseApp
{
    // 机器用蓝屏投票 不该等用户手快关程序
    //   只数系统日志里三种非正常关机 Kernel-Power 41 BugCheck 1001 EventLog 6008 都晚于解锁时刻
    //   解锁流程自己那次 shutdown /r 是正常重启 不会落这三种事件
    //   门槛两次 一次可能是拔电或别的巧合 两次就够了 回锁只回滚账本上的 用户自己开的一概不碰
    internal static class ExtremeCrashFuse
    {
        internal const int TripCount = 2;

        internal static bool ShouldTrip(int crashes)
        {
            return crashes >= TripCount;
        }

        internal static string BuildQuery(DateTime sinceUtc)
        {
            string stamp = sinceUtc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
            return "*[System[((EventID=41 and Provider[@Name='Microsoft-Windows-Kernel-Power'])"
                + " or (EventID=1001 and Provider[@Name='Microsoft-Windows-WER-SystemErrorReporting'])"
                + " or (EventID=6008 and Provider[@Name='EventLog']))"
                + " and TimeCreated[@SystemTime>='" + stamp + "']]]";
        }

        // 读不到日志按零算 保险丝宁可不跳 也不能因为日志服务没起来就把人家的解锁回掉
        internal static int CountCrashesSince(long unlockTicksUtc)
        {
            if (unlockTicksUtc <= 0) return 0;
            try
            {
                var query = new EventLogQuery("System", PathType.LogName, BuildQuery(new DateTime(unlockTicksUtc, DateTimeKind.Utc)));
                int count = 0;
                using (var reader = new EventLogReader(query))
                {
                    EventRecord record;
                    while ((record = reader.ReadEvent()) != null)
                    {
                        using (record) count++;
                        if (count >= TripCount) break;
                    }
                }
                return count;
            }
            catch { return 0; }
        }

        public static bool CheckAtStartup(out int crashes)
        {
            crashes = 0;
            if (!ExtremeMode.Unlocked) return false;
            crashes = CountCrashesSince(ExtremeMode.UnlockTicksUtc);
            return ShouldTrip(crashes);
        }
    }
}
