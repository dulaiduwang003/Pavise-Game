using System;
using System.Threading;

namespace PaviseApp
{
    // 电源计划守护只通过这个接口读游戏模式的会话状态和请它做事 不直接碰 GameMode 的字段
    internal interface IPowerMaintenanceHost
    {
        // 就是游戏模式自己的 sync 和电源应用锁 每次现读 和首次应用 退局恢复锁的是同一把
        object Sync { get; }
        object PowerApplyGate { get; }
        bool GuardEnabled { get; }
        bool GameActive { get; }
        bool PowerPlanActive { get; }
        bool PowerSessionEnding { get; }
        // 正在停止或者收到了紧急恢复请求
        bool ShuttingDown { get; }
        bool ProfileStoreReady { get; }
        bool GameGone { get; }
        int PowerSessionGeneration { get; }
        int LastPowerPolicyKey { get; }
        long NextPowerAuditTicks { get; }
        // 上次应用失败后 核对窗口重新打开之前不响应通知
        int PlanFailStreak { get; }
        bool PowerAttemptInProgress { get; }
        bool PowerSessionCurrent(int generation);
        bool PowerPlanStopped(int policyKey);
        // 首次应用和守护核对共用一个占用标记 同一时刻只有一条在跑
        bool TryClaimPowerApply();
        void ReleasePowerApply();
        bool AutomaticWorkAllowed(string key);
        void RecordAutomaticWorkFailure(string key, string reason);
        bool BeginPowerAttempt(int policyKey, out string reason);
        void CompletePowerAttempt(bool success, string reason);
        bool RunPowerPlanAction(Func<bool> action, out bool skipped, out string reason);
        void RollbackPowerPlan(string reason);
        void OnPowerPlanApplied(bool planOk, string reason, int generation);
    }

    // 对局持有电源计划期间的守护 系统通知和十五秒例行核对合并成同一个后台核对 实例归 GameMode 所有
    internal sealed class PowerMaintenance
    {
        private readonly IPowerMaintenanceHost host;
        // 核对在跑时到达的通知先记下 收尾时补跑一次
        private int notificationPending;
        private long nextMaintenanceTicks;
        // 后台线程随主循环启动 未持有电源计划时 会话门禁忽略核对通知
        private PowerSchemeGuard schemeGuard;

        internal PowerMaintenance(IPowerMaintenanceHost host)
        {
            if (host == null) throw new ArgumentNullException("host");
            this.host = host;
        }

        // 还有排着队的通知时 电源状态不能报已核验
        internal bool NotificationPending
        {
            get { return Volatile.Read(ref notificationPending) != 0; }
        }

        internal static bool Admitted(bool guard, bool game, bool plan,
            bool ending, bool stopping, bool profileFailed, bool gameGone, bool noSwitch)
        {
            return guard && game && plan && !ending && !stopping && !profileFailed && !gameGone && !noSwitch;
        }

        internal void StartSchemeGuard()
        {
            if (schemeGuard == null) schemeGuard = PowerSchemeGuard.Start(NotifySchemeChanged);
        }

        internal void Step()
        {
            if (!Admitted(host.GuardEnabled, host.GameActive, host.PowerPlanActive,
                host.PowerSessionEnding, host.ShuttingDown, !host.ProfileStoreReady, host.GameGone, PowerPlan.NoSwitchSelected)) return;
            long now = DateTime.UtcNow.Ticks;
            if (now < Interlocked.Read(ref nextMaintenanceTicks)) return;
            Interlocked.Exchange(ref nextMaintenanceTicks, now + 15 * TimeSpan.TicksPerSecond);
            // 指定计划核对活动 GUID 托管计划还核对自有参数
            Interlocked.Exchange(ref notificationPending, 1);
            QueueAudit();
        }

        internal void NotifySchemeChanged()
        {
            bool sessionCurrent;
            lock (host.Sync)
            {
                sessionCurrent = host.PowerSessionCurrent(host.PowerSessionGeneration);
            }
            if (sessionCurrent)
            {
                Interlocked.Exchange(ref notificationPending, 1);
                QueueAudit();
            }
        }

        // 会话失效时丢掉还没排上的通知 已经在跑的核对由会话门禁拦下
        internal void DropNotification()
        {
            Interlocked.Exchange(ref notificationPending, 0);
        }

        // 应用或核对收尾时补跑期间到达的通知
        internal void QueueAuditIfPending()
        {
            if (Interlocked.CompareExchange(ref notificationPending, 0, 0) != 0)
            {
                QueueAudit();
            }
        }

        internal void QueueAudit()
        {
            if (!host.AutomaticWorkAllowed("power-queue")) return;
            if (!host.TryClaimPowerApply())
            {
                return;
            }
            int keyShot;
            int genShot;
            lock (host.Sync)
            {
                keyShot = host.LastPowerPolicyKey;
                genShot = host.PowerSessionGeneration;
                if (!host.PowerSessionCurrent(genShot) || keyShot < 0 || (keyShot & 2) == 0 || host.PowerPlanStopped(keyShot) || (host.PlanFailStreak > 0 && DateTime.UtcNow.Ticks < host.NextPowerAuditTicks))
                {
                    Interlocked.Exchange(ref notificationPending, 0);
                    host.ReleasePowerApply();
                    return;
                }
            }
            Interlocked.Exchange(ref notificationPending, 0);
            bool auditQueued = false;
            try
            {
                auditQueued = ThreadPool.QueueUserWorkItem(delegate
                {
                    RunAudit(genShot, keyShot);
                });
            }
            catch
            {
            }
            if (!auditQueued)
            {
                host.RecordAutomaticWorkFailure("power-queue", "notification-queue-rejected");
                Interlocked.Exchange(ref notificationPending, 1);
                host.ReleasePowerApply();
            }
        }

        private bool AuditStillCurrent(int genShot, int keyShot)
        {
            if (AuditSessionCurrent(genShot, keyShot))
            {
                return !host.PowerPlanStopped(keyShot);
            }
            return false;
        }

        private bool AuditSessionCurrent(int genShot, int keyShot)
        {
            lock (host.Sync)
            {
                return host.PowerSessionCurrent(genShot) && host.LastPowerPolicyKey == keyShot;
            }
        }

        // 核对和写回都在电源应用门禁内 和首次应用 厂商性能档切换 退局恢复串行
        internal void RunAudit(int genShot, int keyShot)
        {
            try
            {
                lock (host.PowerApplyGate)
                {
                    string reason;
                    if (!AuditStillCurrent(genShot, keyShot) || PowerPlan.EnforceSettled((keyShot & 1) != 0) || !host.BeginPowerAttempt(keyShot, out reason))
                    {
                        return;
                    }
                    bool skipped;
                    bool runPowerPlanActionSucceeded = host.RunPowerPlanAction(delegate
                    {
                        if (!AuditSessionCurrent(genShot, keyShot))
                        {
                            throw new OperationCanceledException();
                        }
                        return PowerPlan.Enforce((keyShot & 1) != 0);
                    }, out skipped, out reason);
                    if (skipped)
                    {
                        host.RollbackPowerPlan(PerformancePolicy.Text("电源计划应用已跳过", "Power-plan apply was skipped"));
                        host.CompletePowerAttempt(false, "stage=apply-skipped");
                    }
                    else if (!AuditSessionCurrent(genShot, keyShot))
                    {
                        if (!runPowerPlanActionSucceeded)
                        {
                            ActionFailureStore.Trip("PowerPlanOn", "system", reason);
                        }
                        host.RollbackPowerPlan(runPowerPlanActionSucceeded ? "stage=session-cancelled" : reason + " phase=session-cancelled");
                        host.CompletePowerAttempt(false, "stage=session-cancelled");
                    }
                    else
                    {
                        host.OnPowerPlanApplied(runPowerPlanActionSucceeded, reason, genShot);
                    }
                }
            }
            catch
            {
            }
            finally
            {
                lock (host.PowerApplyGate)
                {
                    if (host.PowerAttemptInProgress)
                    {
                        host.RollbackPowerPlan("stage=attempt-interrupted");
                        host.CompletePowerAttempt(false, "stage=attempt-interrupted");
                    }
                }
                host.ReleasePowerApply();
                QueueAuditIfPending();
            }
        }
    }
}
