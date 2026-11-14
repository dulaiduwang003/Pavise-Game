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

        // 后台线程随主循环启动；未持有电源计划时，会话门禁忽略核对通知。
        private PowerSchemeGuard powerSchemeGuard;

        private void StartPowerSchemeGuard()
        {
            if (powerSchemeGuard == null) powerSchemeGuard = PowerSchemeGuard.Start(NotifyPowerSchemeChanged);
        }

        private void StepPowerPlanMaintenance()
        {
            if (!PowerMaintenanceAdmitted(enabled, active, planActive,
                powerSessionEnding, stopping || panicReq, ProfileStoreSaveFailed, gameGoneSinceTicks != 0, PowerPlan.NoSwitchSelected)) return;
            long now = DateTime.UtcNow.Ticks;
            if (now < Interlocked.Read(ref nextPowerMaintenanceTicks)) return;
            Interlocked.Exchange(ref nextPowerMaintenanceTicks, now + 15 * TimeSpan.TicksPerSecond);
            // 指定计划核对活动 GUID；托管计划还核对自有参数。
            Interlocked.Exchange(ref powerPlanNotificationPending, 1);
            QueuePowerPlanNotificationAudit();
        }
    }
}
