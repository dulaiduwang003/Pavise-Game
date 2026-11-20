using System;
using System.Collections.Generic;
using System.Threading;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private sealed class BackgroundCoreReservation
        {
            internal readonly CriticalRuntimeSource Source;
            internal readonly CriticalThreadTargetLease Owner;
            internal readonly CriticalThreadHardAffinitySession Session;
            internal readonly HardThreadPlan Plan;
            internal readonly ulong PhysicalMask;

            internal BackgroundCoreReservation(CriticalRuntimeSource source, CriticalThreadTargetLease owner,
                CriticalThreadHardAffinitySession session, HardThreadPlan plan, ulong physicalMask)
            { Source = source; Owner = owner; Session = session; Plan = plan; PhysicalMask = physicalMask; }
        }

        private BackgroundCoreReservation backgroundCoreReservation;
        private ulong reconciledBackgroundReservation;
        private HashSet<int> backgroundCoreProtected = new HashSet<int>();
        private int backgroundCoreProtectionEpoch = -1;

        // 只有线程会话建立 身份复核且首次范围读回成功后才发布 候选窗口不占用后台核心
        // 也不反过来用后台先占的范围改变游戏选核 撤权只结束预留 恢复债仍归原控制器
        private void PublishBackgroundCoreReservation(CriticalRuntimeSource source)
        {
            if (!Monitor.IsEntered(criticalProcessGate)) throw new InvalidOperationException(Logger.Text("缺少原始线程放置门禁，不能发布后台核心预留", "original thread placement gate not held; cannot publish the background core reservation"));
            HardThreadPlan plan = criticalPlan;
            CriticalThreadTargetLease owner = criticalOriginalTarget;
            if (plan == null || owner == null || owner.Revoked || criticalSession == null
                || plan.Target.SessionId != owner.SessionId || plan.Target.ProcessId != source.Pid
                || plan.Target.ProcessCreation != source.Birth) return;
            ulong chosen = 0;
            foreach (HardThreadChoice choice in plan.Choices) chosen |= choice.Mask;
            ulong physical = AutoCores.WholeCores(chosen, CpuTopology.PhysicalCoreMasks());
            if (physical == 0 || (chosen & ~physical) != 0) return;
            Volatile.Write(ref backgroundCoreReservation, new BackgroundCoreReservation(source, owner, criticalSession, plan, physical));
        }

        // 自动模式的软放置没有硬绑定会话 只记来源和关键角色所在的整颗物理核 提示全部失效或退局时撤回
        private sealed class SoftCoreReservation
        {
            internal readonly CriticalRuntimeSource Source;
            internal readonly ulong PhysicalMask;
            internal SoftCoreReservation(CriticalRuntimeSource source, ulong physicalMask) { Source = source; PhysicalMask = physicalMask; }
        }

        private SoftCoreReservation softCoreReservation;

        // chosen 是仍有效的提示指向的逻辑 CPU 预留它们所在的整颗物理核
        private void PublishSoftCoreReservation(CriticalRuntimeSource source, ulong chosen)
        {
            ulong physical = 0;
            foreach (ulong core in CpuTopology.PhysicalCoreMasks()) if ((core & chosen) != 0) physical |= core;
            Volatile.Write(ref softCoreReservation, physical == 0 ? null : new SoftCoreReservation(source, physical));
        }

        private void ClearSoftCoreReservation() { Volatile.Write(ref softCoreReservation, null); }

        private ulong GameBackgroundReservedCores()
        {
            BackgroundCoreReservation reservation = Volatile.Read(ref backgroundCoreReservation);
            if (reservation == null)
            {
                SoftCoreReservation soft = Volatile.Read(ref softCoreReservation);
                return soft != null && ReservationSourceCurrent(soft.Source) ? soft.PhysicalMask : 0;
            }
            if (reservation.Owner.Revoked
                || !object.ReferenceEquals(reservation.Owner, criticalOriginalTarget)
                || !object.ReferenceEquals(reservation.Session, criticalSession)
                || !object.ReferenceEquals(reservation.Plan, criticalPlan)
                || reservation.Session.ThreadHasExited
                || reservation.Session.Status == HardThreadStatus.Stopped
                || reservation.Session.Status == HardThreadStatus.RecoveryPending
                || reservation.Session.Status == HardThreadStatus.Unsupported) return 0;
            return ReservationSourceCurrent(reservation.Source) ? reservation.PhysicalMask : 0;
        }

        // 预留只对发布它的那一局 那个渲染进程和那条规则有效
        private bool ReservationSourceCurrent(CriticalRuntimeSource source)
        {
            lock (sync)
            {
                GameDetection detection = activeDetection;
                GameProfile profile = FindProfileLocked(source.Profile);
                if (!active || !enabled || stopping || panicReq || smartSwitchedOut || ProfileStoreSaveFailed
                    || detection == null || detection.Profile == null || detection.Profile.Id != source.Profile
                    || !detection.RendererCandidateSelected || detection.RendererSafetyOnly || detection.RequiresGpuConfirm
                    || detection.RendererPid != source.Pid || detection.RendererCreation != source.Birth
                    || !string.Equals(detection.RendererPath, source.Path, StringComparison.OrdinalIgnoreCase)
                    || profile == null || EffectiveGameSchedulingRaw(profile) != source.RuleText
                    || gameSessionStarts != source.Serial || Interlocked.Read(ref criticalIntentEpoch) != source.Epoch)
                    return false;
                return true;
            }
        }

        // 会话循环在游戏首次接管之后调用 仅预留发生变化时补齐同轮后台协调
        // 后端仍沿用同一目标的失败与恢复账本 预留变化不提供新的尝试次数
        private void ReconcileBackgroundCoreReservation(ProcessSnapshot snapshot, HashSet<int> gamePids, bool switchedOut)
        {
            ulong reserved = GameBackgroundReservedCores();
            if (reserved == reconciledBackgroundReservation) return;
            reconciledBackgroundReservation = reserved;
            int epoch = backgroundCoreProtectionEpoch;
            if (EffSuppress && !switchedOut && FamilyPolicyEpochCurrent(epoch))
                RunBackgroundPolicy(epoch, delegate
                {
                    using (SuccessLogBatch.Begin("background-core-coordination session=" + AutomationSession))
                        SyncCoreBackgroundImpl(backgroundCoreProtected, GameSessionDetector.ForegroundPid());
                });
            Logger.Info(Logger.Format("后台核心协调：已接管游戏关键物理核=0x{0}；后台仅在原允许范围内避让，余核不足时撤回本方收核，保留其他后台策略及恢复归属", "Background core coordination: game critical physical cores taken over=0x{0}; background only yields inside its original allowed range, our narrowing is withdrawn when spare cores run short, other background policies and restore ownership kept", reserved.ToString("X")));
        }
    }
}
