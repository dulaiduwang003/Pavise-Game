// @author bdth 2074055628@qq.com
// 文件用途 中断归属的放置证明 校验渲染进程全部线程的实际可运行集合 纯查询不依赖对局状态
using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal static class IrqPlacementProof
    {
        internal static bool PlacementProofMatches(
            ulong desiredMask, ulong hardAffinity,
            bool multiGroup, bool cpuSetsMatch,
            bool cpuSetsUnconstrained)
        {
            if (desiredMask == 0) return false;
            if (multiGroup) return cpuSetsMatch;
            // 有目标 CPU Sets 时 hard affinity 至少要覆盖目标 否则有效集合是
            // 两者交集 没有 CPU Sets 时则只接受精确 hard affinity
            if (cpuSetsMatch)
                return hardAffinity != 0
                    && (desiredMask & ~hardAffinity) == 0;
            return cpuSetsUnconstrained
                && hardAffinity == desiredMask;
        }

        internal static bool AttributionPlacementProofMatches(
            ulong desiredMask, ulong hardAffinity, bool multiGroup,
            bool threadProofComplete, ulong threadUnion)
        {
            return !multiGroup && threadProofComplete
                && desiredMask != 0 && hardAffinity == desiredMask
                && threadUnion == desiredMask;
        }

        internal static ulong EffectiveAttributionThreadMask(
            ulong processHardAffinity, ulong threadGroupAffinity,
            ulong assignedCpuSetMask, bool hasCpuSetAssignment)
        {
            ulong threadHard = processHardAffinity & threadGroupAffinity;
            if (threadHard == 0 || !hasCpuSetAssignment) return threadHard;
            ulong intersection = threadHard & assignedCpuSetMask;
            // Windows 在 CPU Set 分配与限制性硬亲和性完全冲突时以后者为准
            return intersection != 0 ? intersection : threadHard;
        }

        private struct AttributionCpuSetAssignment
        {
            public bool Assigned;
            public ulong Mask;
        }

        private static bool TryQueryProcessAttributionCpuSets(
            IntPtr process, out AttributionCpuSetAssignment assignment)
        {
            assignment = new AttributionCpuSetAssignment();
            bool assigned;
            ulong mask;
            Native.CpuSetMaskQueryResult result =
                Native.QueryProcessDefaultCpuSetMasks(
                    process, out assigned, out mask);
            if (result == Native.CpuSetMaskQueryResult.Success)
            {
                assignment.Assigned = assigned;
                assignment.Mask = mask;
                return !assigned || mask != 0;
            }
            // Win11 mask getter 能同时看到 Masks 与 IDs 两条设置路径 只在
            // 老 Win10 确实没有导出时才允许回退旧 ID getter
            if (result != Native.CpuSetMaskQueryResult.ApiUnavailable)
                return false;
            uint[] ids = Native.QueryCpuSets(process);
            if (ids == null) return false;
            assignment.Assigned = ids.Length > 0;
            return !assignment.Assigned
                || CpuTopology.TryCpuSetIdsToMask(ids, out assignment.Mask);
        }

        private static bool TryQueryThreadAttributionCpuSets(
            IntPtr thread, out AttributionCpuSetAssignment assignment)
        {
            assignment = new AttributionCpuSetAssignment();
            bool assigned;
            ulong mask;
            Native.CpuSetMaskQueryResult result =
                Native.QueryThreadSelectedCpuSetMasks(
                    thread, out assigned, out mask);
            if (result == Native.CpuSetMaskQueryResult.Success)
            {
                assignment.Assigned = assigned;
                assignment.Mask = mask;
                return !assigned || mask != 0;
            }
            if (result != Native.CpuSetMaskQueryResult.ApiUnavailable)
                return false;
            uint[] ids = Native.QueryThreadSelectedCpuSets(thread);
            if (ids == null) return false;
            assignment.Assigned = ids.Length > 0;
            return !assignment.Assigned
                || CpuTopology.TryCpuSetIdsToMask(ids, out assignment.Mask);
        }

        // 进程级放置 一次身份 一次硬亲和 一次默认 CPU Sets
        //   采集中每轮用它 全量线程证明按节奏做 线程显式 CPU Sets 逃逸由下一次全量兜住
        internal static bool AttributionProcessPlacementMatches(
            IntPtr processHandle, int pid, long expectedCreation,
            ulong desiredMask, bool multiGroup)
        {
            if (processHandle == IntPtr.Zero || pid <= 0
                || expectedCreation <= 0 || desiredMask == 0
                || multiGroup)
                return false;
            long creation, cpu;
            ulong io;
            if (!Native.QueryProcessSample(
                    processHandle, out creation, out cpu, out io)
                || creation != expectedCreation)
                return false;
            if (Native.QueryAffinity(processHandle) != desiredMask) return false;
            AttributionCpuSetAssignment processDefault;
            return TryQueryProcessAttributionCpuSets(processHandle, out processDefault);
        }

        internal enum AttributionThreadOutcome { Live, Vanished, Fail }

        // 开不出句柄 属主不是本进程 已经退出 都是线程消失 不是逃逸 消失的线程不影响证明
        //   活跃状态查不出来是句柄本身出了问题 证明不了 只能判失败
        internal static AttributionThreadOutcome ClassifyAttributionThread(
            bool opened, int expectedPid, int ownerPid, bool activeKnown, bool active)
        {
            if (!opened) return AttributionThreadOutcome.Vanished;
            if (expectedPid <= 0) return AttributionThreadOutcome.Fail;
            if (ownerPid != expectedPid) return AttributionThreadOutcome.Vanished;
            if (!activeKnown) return AttributionThreadOutcome.Fail;
            return active ? AttributionThreadOutcome.Live : AttributionThreadOutcome.Vanished;
        }

        // 只在 after 里出现的线程号 两边都已排序去重 新线程要单独证明一次 消失的不追
        internal static int[] NewAttributionThreads(int[] before, int[] after)
        {
            if (after == null) return new int[0];
            if (before == null) return (int[])after.Clone();
            var added = new List<int>();
            int i = 0;
            foreach (int id in after)
            {
                while (i < before.Length && before[i] < id) i++;
                if (i >= before.Length || before[i] != id) added.Add(id);
            }
            return added.ToArray();
        }

        private struct AttributionThreadProof
        {
            public IntPtr Handle;
            public ushort Group;
            public ulong GroupAffinity;
            public AttributionCpuSetAssignment Selected;
        }

        // 线程级查询失败时再看一眼它是不是刚退出 退出了就是消失 否则证明不了
        private static AttributionThreadOutcome FailUnlessVanished(IntPtr thread)
        {
            bool active;
            return Native.TryQueryThreadActive(thread, out active) && !active
                ? AttributionThreadOutcome.Vanished : AttributionThreadOutcome.Fail;
        }

        // 证明一个线程 Live 时句柄交给 held 并把有效集合并进 union 其余情况句柄已关
        private static AttributionThreadOutcome ProveThread(
            int tid, int pid, ulong processHard,
            AttributionCpuSetAssignment processDefault,
            ref ulong union, List<AttributionThreadProof> held)
        {
            IntPtr thread = Native.OpenThread(
                Native.THREAD_QUERY_LIMITED_INFORMATION | Native.SYNCHRONIZE, false, tid);
            bool opened = thread != IntPtr.Zero;
            int owner = opened ? Native.QueryThreadOwnerPid(thread) : -1;
            bool active = false;
            bool activeKnown = opened && Native.TryQueryThreadActive(thread, out active);
            AttributionThreadOutcome outcome = ClassifyAttributionThread(opened, pid, owner, activeKnown, active);
            if (outcome != AttributionThreadOutcome.Live)
            {
                if (opened) Native.CloseHandle(thread);
                return outcome;
            }
            ushort group;
            ulong groupAffinity;
            AttributionCpuSetAssignment selected;
            if (!Native.TryQueryThreadGroupAffinity(thread, out group, out groupAffinity)
                || !TryQueryThreadAttributionCpuSets(thread, out selected))
            {
                outcome = FailUnlessVanished(thread);
                Native.CloseHandle(thread);
                return outcome;
            }
            if (group != 0)
            {
                Native.CloseHandle(thread);
                return AttributionThreadOutcome.Fail;
            }
            AttributionCpuSetAssignment effectiveAssignment = selected.Assigned ? selected : processDefault;
            ulong effective = EffectiveAttributionThreadMask(
                processHard, groupAffinity, effectiveAssignment.Mask, effectiveAssignment.Assigned);
            if (effective == 0)
            {
                Native.CloseHandle(thread);
                return AttributionThreadOutcome.Fail;
            }
            union |= effective;
            held.Add(new AttributionThreadProof
            { Handle = thread, Group = group, GroupAffinity = groupAffinity, Selected = selected });
            return AttributionThreadOutcome.Live;
        }

        // 全量线程证明 线程在证明窗口内退出或新建都不算失败 退出的忽略 新建的单独证明一次
        //   失败只剩三种 进程级放置变了 存活线程的组或线程级 CPU Sets 变了 查询本身出错
        //   线程号取自最近一次进程扫描留下的记录 末尾再刷新一次抓新线程 不走 .NET Process
        internal static bool AttributionThreadPlacementMatches(
            IntPtr processHandle, int pid, long expectedCreation,
            ulong desiredMask, bool multiGroup)
        {
            if (processHandle == IntPtr.Zero || pid <= 0
                || expectedCreation <= 0 || desiredMask == 0
                || multiGroup)
                return false;

            long creation, cpu;
            ulong io;
            if (!Native.QueryProcessSample(
                    processHandle, out creation, out cpu, out io)
                || creation != expectedCreation)
                return false;

            ulong processHard = Native.QueryAffinity(processHandle);
            if (processHard != desiredMask) return false;

            AttributionCpuSetAssignment processDefault;
            if (!TryQueryProcessAttributionCpuSets(
                    processHandle, out processDefault))
                return false;

            int[] firstThreads = ProcessSnapshotSource.ThreadIdsOf(pid, expectedCreation, false)
                ?? ProcessSnapshotSource.ThreadIdsOf(pid, expectedCreation, true);
            if (firstThreads == null || firstThreads.Length == 0)
                return false;

            var held = new List<AttributionThreadProof>(firstThreads.Length);
            ulong threadUnion = 0;
            try
            {
                foreach (int tid in firstThreads)
                    if (ProveThread(tid, pid, processHard, processDefault, ref threadUnion, held)
                        == AttributionThreadOutcome.Fail)
                        return false;
                if (held.Count == 0) return false;

                // 从同一批持有句柄复查 仍活着的线程组与线程级策略不能变 退出的忽略
                foreach (AttributionThreadProof proof in held)
                {
                    int ownerPid = Native.QueryThreadOwnerPid(proof.Handle);
                    bool active;
                    bool activeKnown = Native.TryQueryThreadActive(proof.Handle, out active);
                    AttributionThreadOutcome outcome =
                        ClassifyAttributionThread(true, pid, ownerPid, activeKnown, active);
                    if (outcome == AttributionThreadOutcome.Fail) return false;
                    if (outcome == AttributionThreadOutcome.Vanished) continue;
                    ushort group;
                    ulong groupAffinity;
                    AttributionCpuSetAssignment selected;
                    if (!Native.TryQueryThreadGroupAffinity(proof.Handle, out group, out groupAffinity)
                        || !TryQueryThreadAttributionCpuSets(proof.Handle, out selected))
                    {
                        if (FailUnlessVanished(proof.Handle) == AttributionThreadOutcome.Vanished) continue;
                        return false;
                    }
                    if (!AttributionThreadStateMatches(
                            pid, ownerPid, true,
                            proof.Group, proof.GroupAffinity,
                            proof.Selected.Assigned, proof.Selected.Mask,
                            group, groupAffinity,
                            selected.Assigned, selected.Mask))
                        return false;
                }

                AttributionCpuSetAssignment finalDefault;
                if (!TryQueryProcessAttributionCpuSets(
                        processHandle, out finalDefault)
                    || finalDefault.Assigned != processDefault.Assigned
                    || finalDefault.Mask != processDefault.Mask
                    || Native.QueryAffinity(processHandle) != processHard)
                    return false;
                if (!Native.QueryProcessSample(
                        processHandle, out creation, out cpu, out io)
                    || creation != expectedCreation)
                    return false;
                int[] finalThreads = ProcessSnapshotSource.ThreadIdsOf(pid, expectedCreation, true);
                if (finalThreads == null) return false;
                foreach (int tid in NewAttributionThreads(firstThreads, finalThreads))
                    if (ProveThread(tid, pid, processHard, processDefault, ref threadUnion, held)
                        == AttributionThreadOutcome.Fail)
                        return false;

                return AttributionPlacementProofMatches(
                    desiredMask, processHard, multiGroup,
                    true, threadUnion);
            }
            finally
            {
                foreach (AttributionThreadProof proof in held)
                    Native.CloseHandle(proof.Handle);
            }
        }

        internal static bool AttributionThreadStateMatches(
            int expectedPid, int ownerPid, bool active,
            ushort initialGroup, ulong initialGroupAffinity,
            bool initialCpuSetAssigned, ulong initialCpuSetMask,
            ushort finalGroup, ulong finalGroupAffinity,
            bool finalCpuSetAssigned, ulong finalCpuSetMask)
        {
            return expectedPid > 0 && ownerPid == expectedPid && active
                && initialGroup == 0 && finalGroup == initialGroup
                && initialGroupAffinity != 0
                && finalGroupAffinity == initialGroupAffinity
                && finalCpuSetAssigned == initialCpuSetAssigned
                && finalCpuSetMask == initialCpuSetMask
                && (initialCpuSetAssigned
                    ? initialCpuSetMask != 0
                    : initialCpuSetMask == 0);
        }
    }
}
