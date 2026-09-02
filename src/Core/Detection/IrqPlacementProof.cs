// @author bdth 2074055628@qq.com
// 文件用途 中断归属的放置证明 校验渲染进程全部线程的实际可运行集合 纯查询不依赖对局状态
using System;
using System.Collections.Generic;
using System.Diagnostics;

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

            int[] firstThreads = SnapshotRendererThreadIds(
                pid, expectedCreation);
            if (firstThreads == null || firstThreads.Length == 0)
                return false;

            var handles = new List<IntPtr>(firstThreads.Length);
            var initialGroups = new ushort[firstThreads.Length];
            var initialGroupAffinities = new ulong[firstThreads.Length];
            var initialSelected =
                new AttributionCpuSetAssignment[firstThreads.Length];
            ulong threadUnion = 0;
            try
            {
                for (int i = 0; i < firstThreads.Length; i++)
                {
                    IntPtr thread = Native.OpenThread(
                        Native.THREAD_QUERY_LIMITED_INFORMATION
                            | Native.SYNCHRONIZE,
                        false, firstThreads[i]);
                    if (thread == IntPtr.Zero) return false;
                    handles.Add(thread);

                    if (Native.QueryThreadOwnerPid(thread) != pid)
                        return false;
                    bool active;
                    if (!Native.TryQueryThreadActive(thread, out active)
                        || !active)
                        return false;
                    ushort group;
                    ulong groupAffinity;
                    if (!Native.TryQueryThreadGroupAffinity(
                            thread, out group, out groupAffinity)
                        || group != 0)
                        return false;

                    AttributionCpuSetAssignment selected;
                    if (!TryQueryThreadAttributionCpuSets(
                            thread, out selected))
                        return false;
                    initialGroups[i] = group;
                    initialGroupAffinities[i] = groupAffinity;
                    initialSelected[i] = selected;
                    AttributionCpuSetAssignment effectiveAssignment =
                        selected.Assigned ? selected : processDefault;
                    ulong effective = EffectiveAttributionThreadMask(
                        processHard, groupAffinity,
                        effectiveAssignment.Mask,
                        effectiveAssignment.Assigned);
                    if (effective == 0) return false;
                    threadUnion |= effective;
                }

                // 首尾必须从同一批持有句柄复查 owner 存活与线程级策略
                // 仅比较 TID 集不足以排除线程在证明窗口内退出或改绑
                for (int i = 0; i < handles.Count; i++)
                {
                    bool active;
                    int ownerPid = Native.QueryThreadOwnerPid(handles[i]);
                    if (!Native.TryQueryThreadActive(
                            handles[i], out active))
                        return false;
                    ushort group;
                    ulong groupAffinity;
                    if (!Native.TryQueryThreadGroupAffinity(
                            handles[i], out group, out groupAffinity))
                        return false;
                    AttributionCpuSetAssignment selected;
                    if (!TryQueryThreadAttributionCpuSets(
                            handles[i], out selected))
                        return false;
                    if (!AttributionThreadStateMatches(
                            pid, ownerPid, active,
                            initialGroups[i], initialGroupAffinities[i],
                            initialSelected[i].Assigned,
                            initialSelected[i].Mask,
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
                int[] finalThreads = SnapshotRendererThreadIds(
                    pid, expectedCreation);
                if (!SameAttributionThreadSnapshot(
                        firstThreads, finalThreads))
                    return false;

                return AttributionPlacementProofMatches(
                    desiredMask, processHard, multiGroup,
                    true, threadUnion);
            }
            finally
            {
                for (int i = 0; i < handles.Count; i++)
                    Native.CloseHandle(handles[i]);
            }
        }

        private static int[] SnapshotRendererThreadIds(
            int pid, long expectedCreation)
        {
            try
            {
                using (Process process = Process.GetProcessById(pid))
                {
                    if (process.StartTime.ToFileTimeUtc()
                        != expectedCreation)
                        return null;
                    ProcessThreadCollection threads = process.Threads;
                    if (threads == null || threads.Count == 0) return null;
                    var ids = new int[threads.Count];
                    for (int i = 0; i < threads.Count; i++)
                    {
                        ids[i] = threads[i].Id;
                        if (ids[i] <= 0) return null;
                    }
                    Array.Sort(ids);
                    for (int i = 1; i < ids.Length; i++)
                        if (ids[i] == ids[i - 1]) return null;
                    return ids;
                }
            }
            catch { return null; }
        }

        internal static bool SameAttributionThreadSnapshot(
            int[] a, int[] b)
        {
            if (a == null || b == null || a.Length != b.Length)
                return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
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
