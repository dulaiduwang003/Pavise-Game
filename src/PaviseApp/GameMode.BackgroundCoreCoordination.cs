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

        // 只有守护建立、身份复核且首次范围读回成功后发布。候选窗口不占用后台核心，
        // 也不反过来用后台先占的范围改变游戏选核。撤权只结束预留，恢复债仍归原控制器。
        private void PublishBackgroundCoreReservation(CriticalRuntimeSource source)
        {
            if (!Monitor.IsEntered(criticalProcessGate)) throw new InvalidOperationException("缺少原始线程放置门禁，不能发布后台核心预留");
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

        private ulong GameBackgroundReservedCores()
        {
            BackgroundCoreReservation reservation = Volatile.Read(ref backgroundCoreReservation);
            if (reservation == null || reservation.Owner.Revoked
                || !object.ReferenceEquals(reservation.Owner, criticalOriginalTarget)
                || !object.ReferenceEquals(reservation.Session, criticalSession)
                || !object.ReferenceEquals(reservation.Plan, criticalPlan)
                || reservation.Session.ThreadHasExited
                || reservation.Session.Status == HardThreadStatus.Stopped
                || reservation.Session.Status == HardThreadStatus.RecoveryPending
                || reservation.Session.Status == HardThreadStatus.Unsupported) return 0;
            lock (sync)
            {
                CriticalRuntimeSource source = reservation.Source;
                GameDetection detection = activeDetection;
                GameProfile profile = FindProfileLocked(source.Profile);
                if (!active || !enabled || stopping || panicReq || smartSwitchedOut || ProfileStoreSaveFailed
                    || detection == null || detection.Profile == null || detection.Profile.Id != source.Profile
                    || !detection.RendererCandidateSelected || detection.RendererSafetyOnly || detection.RequiresGpuConfirm
                    || detection.RendererPid != source.Pid || detection.RendererCreation != source.Birth
                    || !string.Equals(detection.RendererPath, source.Path, StringComparison.OrdinalIgnoreCase)
                    || profile == null || EffectiveGameSchedulingRaw(profile) != source.RuleText
                    || gameSessionStarts != source.Serial || Interlocked.Read(ref criticalIntentEpoch) != source.Epoch)
                    return 0;
                return reservation.PhysicalMask;
            }
        }

        // 会话循环在游戏首次接管之后调用；仅预留发生变化时补齐同轮后台协调。
        // 后端仍沿用同一目标的失败与恢复账本，预留变化不提供新的尝试次数。
        private void ReconcileBackgroundCoreReservation(ProcessSnapshot snapshot, HashSet<int> gamePids, bool switchedOut)
        {
            ulong reserved = GameBackgroundReservedCores();
            if (reserved == reconciledBackgroundReservation) return;
            reconciledBackgroundReservation = reserved;
            windowsBackgroundNext = 0;
            StepWindowsBackground(snapshot, gamePids, switchedOut);
            int epoch = backgroundCoreProtectionEpoch;
            if (EffSuppress && !switchedOut && FamilyPolicyEpochCurrent(epoch))
                RunBackgroundPolicy(epoch, delegate { SyncCoreBackgroundImpl(backgroundCoreProtected, GameSessionDetector.ForegroundPid()); });
            Logger.Info("后台核心协调：已接管游戏关键物理核=0x" + reserved.ToString("X")
                + "；后台仅在原允许范围内避让，余核不足时撤回本方收核，保留其他后台策略及恢复归属");
        }
    }
}
