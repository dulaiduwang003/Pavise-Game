// @author bdth 2074055628@qq.com
// 文件用途 取得压制 原值快照与压制状态写入
using System;
using System.Collections.Generic;
using System.Threading;

namespace PaviseApp
{
    internal sealed partial class SuppressionCore
    {
        public AcquireResult Acquire(int pid, string name, SuppressReason reason, string group)
        {
            return Acquire(pid, name, reason, group, SuppressionLevel.Isolated);
        }

        public AcquireResult Acquire(int pid, string name, SuppressReason reason, string group, SuppressionLevel level)
        {
            if (level == SuppressionLevel.None) level = SuppressionLevel.Eco;
            IntPtr h = Native.OpenProcess(Native.PROCESS_SET_INFORMATION | Native.PROCESS_SET_LIMITED_INFORMATION
                | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero)
            {
                lock (sync)
                {
                    Entry e0;
                    if (map.TryGetValue(pid, out e0) && SameName(e0.Name, name))
                    {
                        e0.Reasons |= reason;
                        SetReasonLevel(e0, reason, level);
                        if (group != null && e0.Group == null) e0.Group = group;
                        return AcquireResult.AlreadyProtected;
                    }
                    var protectedEntry = new Entry { Name = name, Group = group, OrigPri = uint.MaxValue, Reasons = reason };
                    SetReasonLevel(protectedEntry, reason, level);
                    map[pid] = protectedEntry;
                    return AcquireResult.NewlyProtected;
                }
            }
            try
            {

                string img = Native.ImageName(h);
                if (img == null || !SameName(img, name)) return AcquireResult.AlreadyProtected;
                long currentCreation = 0, sampleCpu; ulong sampleIo;
                bool identityKnown = Native.QueryProcessSample(h, out currentCreation, out sampleCpu, out sampleIo);
                if (!identityKnown || currentCreation <= 0)
                    return AcquireResult.ApplyFailed;
                if (ScalingService.IsHost(pid, currentCreation)) return AcquireResult.AlreadyProtected;
                lock (sync)
                {
                    Entry e;
                    bool known = map.TryGetValue(pid, out e);
                    if (known && !SameName(e.Name, name)) { map.Remove(pid); known = false; e = null; }

                    if (known && e.OrigPri != uint.MaxValue && e.Creation <= 0)
                    {
                        map.Remove(pid);
                        batchApply.Remove(pid);
                        known = false;
                        e = null;
                    }
                    if (known && e.Creation > 0)
                    {
                        if (!identityKnown) return AcquireResult.AlreadyProtected;
                        if (e.Creation != currentCreation)
                        {
                            map.Remove(pid);
                            known = false;
                            e = null;
                        }
                    }

                    if (known && e.OrigPri != uint.MaxValue && e.GaveUp)
                    {
                        e.Reasons |= reason;
                        SetReasonLevel(e, reason, level);
                        if (group != null && e.Group == null) e.Group = group;
                        return AcquireResult.AlreadyProtected;
                    }
                    if (known && e.OrigPri != uint.MaxValue)
                    {
                        SuppressionLevel previousLevel = e.Level;
                        SuppressReason previousReasons = e.Reasons;
                        string previousGroup = e.Group;
                        e.Reasons |= reason;
                        if (group != null && e.Group == null) e.Group = group;
                        SetReasonLevel(e, reason, level);
                        bool metadataChanged = previousReasons != e.Reasons
                            || previousLevel != e.Level || !SameName(previousGroup, e.Group);
                        long now = DateTime.UtcNow.Ticks;
                        bool levelChanged = previousLevel != e.Level;
                        bool mustWrite = !e.Journaled || levelChanged
                            || !e.Applied && now >= e.NextReconcileTicks;
                        if ((metadataChanged || !e.Journaled)
                            && !PersistJournalLocked())
                            return AcquireResult.ApplyFailed;
                        if (mustWrite)
                        {
                            if (QueueApplyLocked(pid, name)) return AcquireResult.AlreadyThrottled;
                            e.Applied = ApplyEntryLocked(h, e, pid);
                            ScheduleAfterApply(e, e.Applied, pid);
                            if (!e.Applied && TryNeutralizeUnwritableLocked(h, pid, e))
                                return AcquireResult.AlreadyProtected;
                            if (!e.Applied && GiveUpAntiCheatLocked(h, pid, e))
                                return AcquireResult.AlreadyProtected;
                            return e.Applied ? AcquireResult.AlreadyThrottled : AcquireResult.ApplyFailed;
                        }

                        if (!e.Applied)
                        {
                            RecordBatchApplyResultLocked(pid, false, "apply-pending-backoff");
                            return AcquireResult.AlreadyThrottled;
                        }
                        if (now < e.NextReconcileTicks)
                        {
                            RecordBatchApplyResultLocked(pid, true, null);
                            return AcquireResult.AlreadyThrottled;
                        }
                        bool matches = ThrottleMatches(h, e.Level, e.OrigPri, e.OrigAff, e.OrigCpuSets, DesiredGpu(e), AntiCheatThrottled(e), DesiredAffinityOf(e), e.SqueezeRefused);
                        if (matches)
                        {
                            ScheduleAfterMatch(e, pid);
                            RecordBatchApplyResultLocked(pid, true, null);
                            return AcquireResult.AlreadyThrottled;
                        }
                        if (QueueApplyLocked(pid, name)) return AcquireResult.AlreadyThrottled;
                        e.Applied = ApplyEntryLocked(h, e, pid);
                        ScheduleAfterApply(e, e.Applied, pid);
                        if (!e.Applied && TryNeutralizeUnwritableLocked(h, pid, e))
                            return AcquireResult.AlreadyProtected;
                        if (!e.Applied && GiveUpAntiCheatLocked(h, pid, e))
                            return AcquireResult.AlreadyProtected;

                        return AcquireResult.AlreadyThrottled;
                    }

                    uint rawPri = Native.GetPriorityClass(h);

                    if (rawPri == 0) return AcquireResult.ApplyFailed;
                    ulong oaff = Native.QueryAffinity(h);
                    uint[] ocpuSets = Native.QueryCpuSets(h);
                    if (ocpuSets == null) return AcquireResult.ApplyFailed;
                    int oio = Native.QueryIoPriority(h);
                    int opg = Native.QueryPagePriority(h);

                    if ((!CpuTopology.MultiGroup && oaff == 0)
                        || oio < 0 || opg < 0)
                        return AcquireResult.ApplyFailed;
                    uint[] yieldIds = CpuTopology.BackgroundYieldCpuSetIds();
                    bool cpuSetsLookPavise = SameCpuSets(ocpuSets, CpuTopology.BackgroundCpuSetIds())
                        || SameCpuSets(ocpuSets, CpuTopology.InactiveBackgroundCpuSetIds())
                        || yieldIds != null && yieldIds.Length > 0 && SameCpuSets(ocpuSets, yieldIds);
                    ulong squeezeMask = CpuTopology.MultiGroup ? 0 : CpuTopology.BackgroundSqueezeMask();
                    bool affinityLooksPavise = !CpuTopology.MultiGroup && (oaff == throttleMask
                        || CpuTopology.InactiveThrottleMask != 0 && oaff == CpuTopology.InactiveThrottleMask
                        || squeezeMask != 0 && oaff == squeezeMask);
                    bool residue = CrashGuard.UncleanThrottleAtLaunch
                        && rawPri == Native.IDLE_PRIORITY_CLASS && oio == 0 && opg == 1
                        && (cpuSetsLookPavise || affinityLooksPavise);
                    uint orig = residue ? Native.NORMAL_PRIORITY_CLASS : rawPri;
                    if (residue && affinityLooksPavise) oaff = 0;
                    if (residue && cpuSetsLookPavise) ocpuSets = new uint[0];
                    if (residue && oio == 0) oio = -1;
                    if (residue && opg == 1) opg = -1;
                    int oqc, oqs;
                    if (!Native.TryQueryPowerThrottling(h, out oqc, out oqs)) { oqc = -1; oqs = -1; }
                    else if (residue && (oqc == 1 || oqc == 5) && oqs == oqc) { oqc = -1; oqs = -1; }
                    int oboost = Native.QueryBoostDisabled(h);
                    if (residue && oboost == 1) oboost = 0;
                    int ogpu;
                    if (Native.D3DKMTGetProcessSchedulingPriorityClass(h, out ogpu) != 0) ogpu = -1;
                    else if (residue && ogpu == Native.GpuPriorityIdle) ogpu = Native.GpuPriorityNormal;
                    long creation = currentCreation;

                    Entry active;
                    if (known)
                    {
                        e.OrigPri = orig; e.OrigAff = oaff; e.OrigIo = oio; e.OrigPg = opg; e.OrigCpuSets = ocpuSets;
                        e.OrigGpu = ogpu;
                        e.OrigQoSControl = oqc; e.OrigQoSState = oqs; e.OrigBoost = oboost;
                        e.Reasons |= reason; SetReasonLevel(e, reason, level); e.Creation = creation; e.Applied = false;
                        e.Journaled = false;
                        if (group != null && e.Group == null) e.Group = group;
                        active = e;
                    }
                    else
                    {
                        var created = new Entry { Name = name, Group = group, OrigPri = orig, OrigAff = oaff,
                            OrigIo = oio, OrigPg = opg, OrigCpuSets = ocpuSets, OrigGpu = ogpu,
                            OrigQoSControl = oqc, OrigQoSState = oqs, OrigBoost = oboost,
                            Reasons = reason, Creation = creation };
                        SetReasonLevel(created, reason, level);
                        map[pid] = created;
                        active = created;
                    }
                    if (!PersistJournalLocked()) return AcquireResult.ApplyFailed;
                    bool queued = QueueApplyLocked(pid, name);
                    bool applied = queued || ApplyThrottle(h, level, orig, oaff, ocpuSets, DesiredGpu(active), oboost,
                        (reason & SuppressReason.AntiCheat) != 0, DesiredAffinityOf(active));
                    Entry appliedEntry;
                    if (map.TryGetValue(pid, out appliedEntry) && !queued)
                    {
                        appliedEntry.Applied = applied;
                        ScheduleAfterApply(appliedEntry, applied, pid);
                        if (!applied && TryNeutralizeUnwritableLocked(h, pid, appliedEntry))
                            return AcquireResult.NewlyProtected;
                        if (!applied && GiveUpAntiCheatLocked(h, pid, appliedEntry))
                            return AcquireResult.AlreadyProtected;
                    }
                    if (!marked) { marked = true; CrashGuard.MarkThrottle(throttleMask); }
                    return applied ? AcquireResult.NewlyThrottled : AcquireResult.ApplyFailed;
                }
            }
            finally { Native.CloseHandle(h); }
        }

    }
}
