using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace PaviseApp
{
    // 反作弊分支由原 Job 守护独占前向写入。线程原值先落盘，才能改变会连带影响线程的进程范围。
    internal static class SGuardAffinityOperations
    {
        internal static bool ForwardAllowed(GameAffinityJobRecord record)
        {
            if (!record.OwnThreadAffinity) return true;
            try { using (EventWaitHandle signal = EventWaitHandle.OpenExisting(record.Name + ".ForwardRevoked")) return !signal.WaitOne(0); }
            catch { return false; }
        }
        internal static bool Apply(GameAffinityJobRecord record, IntPtr job, IntPtr process, bool overrideThreads, out string reason)
        {
            reason = null;
            if (!record.OwnThreadAffinity) return WindowsBackgroundJobNative.Apply(record, job, process, overrideThreads, out reason);
            if (record.Resources != null) { reason = "sguard-resource-owner-conflict"; return false; }
            if (record.StrictThreadAdmission)
            {
                ulong current;
                if (!Native.TryQueryAffinity(process, out current)
                    || !GameThreadAffinityAdmission.Check(process, record.Pid, record.Birth, current, out reason))
                { if (reason == null) reason = GameThreadAffinityAdmission.RefusalPrefix + "process-mask-unreadable"; return false; }
            }
            if (!SGuardThreadAffinityJournal.Prepare(record, process, out reason)) return false;
            if (!SGuardThreadAffinityJournal.MarkIndirectWrite(record, process, out reason)) return false;
            if (!ForwardAllowed(record)) { reason = "sguard-forward-revoked"; return false; }
            if (!GameAffinityJobNative.Apply(job, process, record.Birth, record.Desired, true, delegate { return ForwardAllowed(record); }, out reason))
            {
                // Job 已赋值时，进程仍可能保留更窄的外部范围；只在身份和本方 Job 均可核验时纠正。
                if (reason != "process-affinity-mismatch")
                {
                    // 反作弊拒绝 Job 接管时改由进程范围承担限制；赋值前写入的 Job 限制没有成员，先清掉再继续。
                    if (!JobAssignmentRefused(record, reason) || !GameAffinityJobNative.ClearForGoneTarget(job, out reason)) return false;
                }
            }
            if (!SetProcessRange(record, process, out reason)
                || !SGuardThreadAffinityJournal.Apply(record, process, out reason)) return false;
            return Verify(record, job, process, out reason);
        }

        // SGuard64 的句柄拿不到 Job 赋值所需的权限，但允许设置进程范围。线程原值仍先由同一持久日志记录。
        private static bool JobAssignmentRefused(GameAffinityJobRecord record, string reason)
        { return !record.StrictThreadAdmission && reason == "assign-job:5"; }

        // 是否由进程范围直接承担限制只看目标是不是本方 Job 的成员，不另存标记：赋值一旦成功，目标无法自行脱离。
        internal static bool DirectRange(GameAffinityJobRecord record, IntPtr job, IntPtr process)
        {
            bool member;
            return record.OwnThreadAffinity && !record.StrictThreadAdmission && record.Resources == null
                && job != IntPtr.Zero && GameAffinityJobNative.Alive(process)
                && GameAffinityJobNative.IsMember(process, job, out member) && !member;
        }

        private static bool VerifyDirectRange(GameAffinityJobRecord record, IntPtr job, IntPtr process, out string reason)
        {
            if (!GameAffinityJobNative.Matches(process, record.Birth)) { reason = "target-identity"; return false; }
            // 没有成员的 Job 不能残留范围限制。
            if (!GameAffinityJobNative.VerifyNeutral(job, out reason)) return false;
            ulong mask;
            if (!Native.TryQueryAffinity(process, out mask) || mask != record.Desired) { reason = "process-affinity-mismatch"; return false; }
            return true;
        }

        internal static bool Maintain(GameAffinityJobRecord record, IntPtr job, IntPtr process, out string reason)
        {
            if (!record.OwnThreadAffinity || record.Resources != null || record.Phase != "armed")
            { reason = "sguard-maintenance-not-owned"; return false; }
            bool direct = DirectRange(record, job, process);
            // 进程和 Job 未漂移时只维护已接管线程；新线程预算耗尽不能剥夺旧字段的纠偏资格。
            if (direct ? VerifyDirectRange(record, job, process, out reason) : WindowsBackgroundJobNative.Verify(record, job, process, out reason))
                return SGuardThreadAffinityJournal.Maintain(record, process, out reason) && Verify(record, job, process, out reason);
            if (!SGuardThreadAffinityJournal.PrepareMaintenance(record, process, out reason)) return false;
            if (!SGuardThreadAffinityJournal.MarkIndirectWrite(record, process, out reason)) return false;
            if (!ForwardAllowed(record)) { reason = "sguard-forward-revoked"; return false; }
            // 已被拒绝 Job 接管的目标只纠正进程范围，不在每个维护周期重复发起 Job 赋值。
            if (!direct && !GameAffinityJobNative.Apply(job, process, record.Birth, record.Desired, true, delegate { return ForwardAllowed(record); }, out reason) && reason != "process-affinity-mismatch") return false;
            if (!SetProcessRange(record, process, out reason)
                || !SGuardThreadAffinityJournal.Maintain(record, process, out reason)) return false;
            return Verify(record, job, process, out reason);
        }

        private static bool SetProcessRange(GameAffinityJobRecord record, IntPtr process, out string reason)
        {
            reason = null;
            if (!ForwardAllowed(record)) { reason = "sguard-forward-revoked"; return false; }
            ulong current;
            if (!GameAffinityJobNative.Matches(process, record.Birth) || !Native.TryQueryAffinity(process, out current))
            { reason = "sguard-process-read-unconfirmed"; return false; }
            if ((record.Desired & ~record.Original) != 0) { reason = "sguard-original-range-conflict"; return false; }
            if (current != record.Desired)
            {
                if (!ForwardAllowed(record)) { reason = "sguard-forward-revoked"; return false; }
                if (!Native.SetProcessAffinityMask(process, (UIntPtr)record.Desired))
                { reason = "sguard-set-process-affinity:" + Marshal.GetLastWin32Error(); return false; }
            }
            if (!GameAffinityJobNative.Matches(process, record.Birth) || !Native.TryQueryAffinity(process, out current) || current != record.Desired)
            { reason = "sguard-process-readback-drift"; return false; }
            return true;
        }

        internal static bool Verify(GameAffinityJobRecord record, IntPtr job, IntPtr process, out string reason)
        {
            if (DirectRange(record, job, process))
                return VerifyDirectRange(record, job, process, out reason) && SGuardThreadAffinityJournal.Verify(record, process, out reason);
            return WindowsBackgroundJobNative.Verify(record, job, process, out reason)
                && (!record.OwnThreadAffinity || SGuardThreadAffinityJournal.Verify(record, process, out reason));
        }

        // 只写回仍等于本方目标的进程范围；已是原值或之后出现的其他范围都保留。
        private static bool RestoreProcessRange(GameAffinityJobRecord record, IntPtr process, string unconfirmed, out string reason)
        {
            reason = null;
            ulong mask;
            if (!GameAffinityJobNative.Matches(process, record.Birth) || !Native.TryQueryAffinity(process, out mask))
            { reason = "restore-mask-unreadable"; return false; }
            if (mask != record.Desired || mask == record.Original) return true;
            if (!Native.SetProcessAffinityMask(process, (UIntPtr)record.Original)
                || !Native.TryQueryAffinity(process, out mask) || mask != record.Original)
            { reason = unconfirmed; return false; }
            return true;
        }

        // Job 名称随最后一个句柄失效后的遗留恢复。从未进入 Job 的目标可由进程范围直接写回；
        // 仍受旧 Job 约束的目标写回会被系统拒绝，沿用原结论，等目标退出后结清。
        internal static bool RestoreWithoutJob(GameAffinityJobRecord record, IntPtr process, out string reason)
        {
            reason = "job-handle-lost; game-exit-required";
            if (!record.OwnThreadAffinity || record.StrictThreadAdmission || record.Resources != null || !GameAffinityJobNative.Alive(process)) return false;
            if (!SGuardThreadAffinityJournal.CaptureRecovery(record, process, out reason)) return false;
            bool pending;
            if (!SGuardThreadAffinityJournal.HasPending(record, out pending, out reason)) return false;
            if (!pending) return true;
            if (!RestoreProcessRange(record, process, "job-handle-lost; game-exit-required", out reason)) return false;
            return SGuardThreadAffinityJournal.Restore(record, process, out reason);
        }

        internal static bool Restore(GameAffinityJobRecord record, IntPtr job, IntPtr process, out string reason)
        {
            if (!record.OwnThreadAffinity)
                return record.Phase != "armed" ? WindowsBackgroundJobNative.Neutral(record, job, out reason)
                    : process == IntPtr.Zero
                        ? WindowsBackgroundJobNative.ClearGone(record, job, out reason)
                        : WindowsBackgroundJobNative.Restore(record, job, process, out reason);
            // 冻结恢复归属后先放宽本方进程限制，避免宽线程原值与窄 Job 互相等待。
            if (!SGuardThreadAffinityJournal.CaptureRecovery(record, process, out reason)) return false;
            bool gone = process == IntPtr.Zero || Native.WaitForSingleObject(process, 0) == 0;
            bool pending;
            if (!SGuardThreadAffinityJournal.HasPending(record, out pending, out reason)) return false;
            // 仅准备而从未提交写入的阶段不拥有进程字段，不能因外部恰好同值而恢复它。
            if (!pending) return gone ? WindowsBackgroundJobNative.ClearGone(record, job, out reason)
                : WindowsBackgroundJobNative.Neutral(record, job, out reason);
            if (!(record.Phase != "armed" ? WindowsBackgroundJobNative.Neutral(record, job, out reason)
                : gone ? WindowsBackgroundJobNative.ClearGone(record, job, out reason)
                    : WindowsBackgroundJobNative.Restore(record, job, process, out reason))) return false;
            // 未进入 Job 的目标由本方直接写过进程范围，须先按归属写回，线程原值才有恢复空间。
            if (!gone && DirectRange(record, job, process) && !RestoreProcessRange(record, process, "restore-mask-unconfirmed", out reason)) return false;
            return SGuardThreadAffinityJournal.Restore(record, process, out reason);
        }
    }
}
