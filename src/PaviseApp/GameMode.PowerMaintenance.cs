using System;
using System.Threading;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private long nextPowerMaintenanceTicks;

        internal static bool PowerMaintenanceAdmitted(bool guard, bool game, bool plan,
            bool ending, bool stopping, bool profileFailed, bool gameGone, bool noSwitch)
        {
            return guard && game && plan && !ending && !stopping && !profileFailed && !gameGone && !noSwitch;
        }

        private void StepPowerPlanMaintenance()
        {
            if (!PowerMaintenanceAdmitted(enabled, active, planActive,
                powerSessionEnding, stopping || panicReq, ProfileStoreSaveFailed, gameGoneSinceTicks != 0, PowerPlan.NoSwitchSelected)) return;
            long now = DateTime.UtcNow.Ticks;
            if (now < Interlocked.Read(ref nextPowerMaintenanceTicks)) return;
            Interlocked.Exchange(ref nextPowerMaintenanceTicks, now + 15 * TimeSpan.TicksPerSecond);
            // 指定计划只核对活动 GUID；托管计划另核对自有参数。
            // 两者复用通知任务的会话核验、合并和故障隔离。
            Interlocked.Exchange(ref powerPlanNotificationPending, 1);
            QueuePowerPlanNotificationAudit();
        }
    }
}
