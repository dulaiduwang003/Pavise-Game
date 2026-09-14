// @author bdth 2074055628@qq.com
// File purpose Placement proof for interrupt attribution: verifies the actual runnable set of every renderer thread; pure query, independent of match state
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
            // With a target CPU Sets, hard affinity must at least cover the target, otherwise the effective set is
            // the intersection of the two; without CPU Sets only an exact hard affinity is accepted
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
            // Windows lets a restrictive hard affinity win when it fully conflicts with the CPU Set assignment
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
            // The Win11 mask getter sees both the Masks and IDs setter paths; fall back to the old ID getter only
            // when old Win10 really lacks the export
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

        // Process-level placement: one identity check, one hard affinity, one default CPU Sets
        //   Used every round during capture; the full thread proof runs on its own cadence, and per-thread explicit CPU Sets escapes are caught by the next full pass
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

        // Cannot open a handle, owner is not this process, already exited: all mean the thread vanished, not escaped; vanished threads do not affect the proof
        //   If the active state cannot be queried, the handle itself is broken; nothing can be proven, so it must fail
        internal static AttributionThreadOutcome ClassifyAttributionThread(
            bool opened, int expectedPid, int ownerPid, bool activeKnown, bool active)
        {
            if (!opened) return AttributionThreadOutcome.Vanished;
            if (expectedPid <= 0) return AttributionThreadOutcome.Fail;
            if (ownerPid != expectedPid) return AttributionThreadOutcome.Vanished;
            if (!activeKnown) return AttributionThreadOutcome.Fail;
            return active ? AttributionThreadOutcome.Live : AttributionThreadOutcome.Vanished;
        }

        // Thread ids that appear only in after; both sides already sorted and deduplicated; new threads need their own proof, vanished ones are not chased
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

        // When a thread-level query fails, check once more whether it just exited; exited means vanished, otherwise nothing is proven
        private static AttributionThreadOutcome FailUnlessVanished(IntPtr thread)
        {
            bool active;
            return Native.TryQueryThreadActive(thread, out active) && !active
                ? AttributionThreadOutcome.Vanished : AttributionThreadOutcome.Fail;
        }

        // Prove one thread: on Live, hand the handle to held and merge its effective set into union; in every other case the handle is already closed
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

        // Full thread proof: threads exiting or being created inside the proof window do not count as failure; exited ones are ignored, new ones get proven once
        //   Only three failures remain: process-level placement changed, a live thread's group or thread-level CPU Sets changed, or the query itself errored
        //   Thread ids come from the record left by the latest process sweep, refreshed once at the end to catch new threads; no .NET Process involved
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

                // Re-check from the same batch of held handles: group and thread-level policy of threads still alive must not change; exited ones are ignored
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
