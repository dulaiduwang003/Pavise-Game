using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace PaviseApp
{
    internal static class CrashGuard
    {
        private enum BoostIdentity
        {
            Match,
            Mismatch,
            Unknown
        }

        internal sealed class OriginalBoostState
        {
            public uint Priority;

            public BoostPriorityPhase PriorityPhase;

            public uint LastAppliedPriority;

            public uint PreviousPriorityForRestore;

            public ulong Affinity;

            public int Io;

            public int Gpu;

            public uint[] CpuSets;

            public int QoSControl;

            public int QoSState;
        }

        private sealed class BoostEntry
        {
            public int Pid;

            public long Creation;

            public string Name;

            public uint Priority;

            public BoostPriorityPhase PriorityPhase;

            public uint LastAppliedPriority;

            public uint PreviousPriorityForRestore;

            public ulong Affinity;

            public int Io;

            public int Gpu;

            public GpuPriorityLease GpuLease = new GpuPriorityLease();

            public uint[] CpuSets;

            public int QoSControl = -1;

            public int QoSState = -1;

            public bool PlacementYielded;

            public bool PlacementTracked;

            public ulong AppliedAffinity;

            public ulong PreviousAffinity;
        }

        private const string KThrottle = "Crash_ThrottleMasks";

        private const string KBoost = "Crash_BoostMask";

        private const string KBoostNames = "Crash_BoostNames";

        private const string KBoostEntries = "Crash_BoostEntriesV2";

        internal const string PriorityOwnershipKey = "Crash_BoostPriorityOwnershipV1";

        private const string PriorityVersion = "priority-v1";

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint GetProcessId(IntPtr handle);

        private static readonly object sync = new object();

        private static readonly Dictionary<int, long> owned = new Dictionary<int, long>();

        private sealed class PrioritySettlement
        {
            internal long Creation;
            internal uint Applied;
            internal uint Previous;
        }

        // 日志保存失败时，不能把已知不持有的状态改回不确定意图；当前进程仍保留原生操作结果。

        private static readonly Dictionary<int, PrioritySettlement> prioritySettlements = new Dictionary<int, PrioritySettlement>();

        private static readonly Dictionary<string, GpuPriorityLease> gpuLeases = new Dictionary<string, GpuPriorityLease>();

        private static string GpuLeaseKey(BoostEntry entry) { return entry.Pid + ":" + entry.Creation; }

        internal static bool BoostGpuCheckpointPending(int pid, long creation)
        {
            lock (sync)
            {
                GpuPriorityLease lease;
                return gpuLeases.TryGetValue(pid + ":" + creation, out lease) && lease.PendingCheckpoint;
            }
        }

        internal static bool ApplyBoostGpu(IntPtr handle, int pid, long creation)
        {
            lock (sync)
            {
                long ownerBirth;
                if (!owned.TryGetValue(pid, out ownerBirth) || ownerBirth != creation || GetProcessId(handle) != (uint)pid) return false;
                List<BoostEntry> entries = LoadEntries();
                if (entries == null) return false;
                BoostEntry entry = FindEntry(entries, pid, creation);
                if (entry == null || entry.Gpu < 0) return false;
                gpuLeases[GpuLeaseKey(entry)] = entry.GpuLease;
                bool wrote;
                return entry.GpuLease.Apply(handle, 4, () => Native.QueryProcessIdentityState(handle, creation), () => SaveEntries(entries), out wrote);
            }
        }

        internal static bool RestoreBoostGpu(IntPtr handle, int pid, long creation)
        {
            lock (sync)
            {
                if (GetProcessId(handle) != (uint)pid) return false;
                List<BoostEntry> entries = LoadEntries();
                if (entries == null) return false;
                BoostEntry entry = FindEntry(entries, pid, creation);
                if (entry == null) return true;
                gpuLeases[GpuLeaseKey(entry)] = entry.GpuLease;
                return entry.GpuLease.Restore(handle, () => Native.QueryProcessIdentityState(handle, creation), () => SaveEntries(entries));
            }
        }

        public static bool UncleanThrottleAtLaunch { get; private set; }

        public static void MarkThrottle(ulong mask)
        {
            if (mask != 0)
            {
                Settings.SaveStr("Crash_ThrottleMasks", "journaled");
            }
        }

        public static void ReleaseThrottle(ulong mask)
        {
            Settings.SaveStr("Crash_ThrottleMasks", "");
        }

        public static bool MarkBoostProcess(int pid, long creation, string name, uint priority, ulong affinity, int io, int gpu, uint[] cpuSets)
        {
            return MarkBoostProcess(pid, creation, name, priority, affinity, io, gpu, cpuSets, -1, -1);
        }

        public static bool MarkBoostProcess(int pid, long creation, string name, uint priority, ulong affinity, int io, int gpu, uint[] cpuSets, int qosControl, int qosState)
        {
            OriginalBoostState recovered;
            return MarkBoostProcess(pid, creation, name, priority, affinity, io, gpu, cpuSets, qosControl, qosState, out recovered);
        }

        internal static bool MarkBoostProcess(int pid, long creation, string name, uint priority, ulong affinity, int io, int gpu, uint[] cpuSets, int qosControl, int qosState, out OriginalBoostState recovered)
        {
            recovered = null;
            if (pid <= 0 || creation <= 0 || string.IsNullOrEmpty(name) || !ValidPriority(priority))
            {
                return false;
            }
            lock (sync)
            {
                List<BoostEntry> list = LoadEntries();
                if (list == null) return false;
                foreach (BoostEntry item in list)
                {
                    if (item.Pid == pid && item.Creation == creation)
                    {
                        if (!string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase))
                        {
                            return false;
                        }
                        owned[pid] = creation;
                        recovered = new OriginalBoostState
                        {
                            Priority = item.Priority,
                            PriorityPhase = item.PriorityPhase,
                            LastAppliedPriority = item.LastAppliedPriority,
                            PreviousPriorityForRestore = item.PreviousPriorityForRestore,
                            Affinity = item.Affinity,
                            Io = item.Io,
                            Gpu = item.Gpu,
                            CpuSets = ((item.CpuSets == null) ? new uint[0] : ((uint[])item.CpuSets.Clone())),
                            QoSControl = item.QoSControl,
                            QoSState = item.QoSState
                        };
                        return true;
                    }
                }
                list.RemoveAll((BoostEntry e) => e.Pid == pid);
                List<BoostEntry> priorityEntries = LoadPriorityEntries();
                if (priorityEntries == null) return false;
                BoostEntry orphan = FindEntry(priorityEntries, pid, creation);
                if (orphan != null && (orphan.PriorityPhase != BoostPriorityPhase.None
                    || !string.Equals(orphan.Name, name, StringComparison.OrdinalIgnoreCase))) return false;
                BoostEntry fresh = new BoostEntry
                {
                    Pid = pid,
                    Creation = creation,
                    Name = name,
                    Priority = priority,
                    PriorityPhase = BoostPriorityPhase.None,
                    Affinity = affinity,
                    Io = io,
                    Gpu = gpu,
                    CpuSets = cpuSets,
                    QoSControl = qosControl,
                    QoSState = qosState,
                    PlacementTracked = true
                };
                // 恢复能看到旧格式基准前，先写入 None 附属记录。第二次写入失败时只留下无害的失效标记。

                if (!SavePriorityEntry(fresh)) return false;
                list.Add(fresh);
                bool saveEntriesSucceeded = SaveEntries(list);
                if (saveEntriesSucceeded)
                {
                    owned[pid] = creation;
                    prioritySettlements.Remove(pid);
                }
                else
                {
                    owned.Remove(pid);
                }
                return saveEntriesSucceeded;
            }
        }

        public static void ReleaseBoostProcess(int pid, long creation)
        {
            lock (sync)
            {
                long value;
                if (owned.TryGetValue(pid, out value) && (creation <= 0 || value == creation))
                {
                    List<BoostEntry> list = LoadEntries();
                    if (list == null) return;
                    List<BoostEntry> priorities = LoadPriorityEntries();
                    if (priorities == null) return;
                    BoostEntry entry = FindEntry(list, pid, value) ?? FindEntry(priorities, pid, value);
                    if (entry != null && !SettleBoostPriority(entry)) return;
                    int removed = list.RemoveAll((BoostEntry e) => e.Pid == pid && (creation <= 0 || e.Creation == creation));
                    if (removed == 0 || SaveEntries(list))
                    {
                        owned.Remove(pid);
                        gpuLeases.Remove(pid + ":" + value);
                        ForgetPrioritySettlement(pid, value);
                        PrunePriorityTombstones(list);
                    }
                }
            }
        }

        internal static bool PrepareBoostPriorityWrite(int pid, long creation, uint previous, uint applied)
        {
            if (!ValidPriority(previous) || !ValidPriority(applied) || previous == applied) return false;
            lock (sync)
            {
                long ownerBirth;
                if (!owned.TryGetValue(pid, out ownerBirth) || ownerBirth != creation) return false;
                List<BoostEntry> entries = LoadEntries();
                if (entries == null) return false;
                BoostEntry entry = FindEntry(entries, pid, creation);
                if (entry == null) return false;
                if (PrioritySettledInMemory(entry) && !SettleBoostPriority(entry)) return false;
                if (entry.PriorityPhase == BoostPriorityPhase.Intent) return false;
                // 尚未恢复的租约若仍匹配本方最近写入，不能替换；观测到不同值时，才将其作为新写入的直接前值。

                if (entry.PriorityPhase != BoostPriorityPhase.None && previous == entry.LastAppliedPriority) return false;
                entry.PriorityPhase = BoostPriorityPhase.Intent;
                entry.LastAppliedPriority = applied;
                entry.PreviousPriorityForRestore = previous;
                return SavePriorityEntry(entry);
            }
        }

        internal static bool CompleteBoostPriorityWrite(int pid, long creation, uint previous, uint applied)
        {
            lock (sync)
            {
                List<BoostEntry> entries = LoadEntries();
                if (entries == null) return false;
                List<BoostEntry> priorities = LoadPriorityEntries();
                if (priorities == null) return false;
                BoostEntry entry = FindEntry(entries, pid, creation) ?? FindEntry(priorities, pid, creation);
                if (!PriorityWriteMatches(entry, previous, applied)) return false;
                if (PrioritySettledInMemory(entry)) return false;
                if (entry.PriorityPhase == BoostPriorityPhase.Owned) return true;
                if (entry.PriorityPhase != BoostPriorityPhase.Intent) return false;
                entry.PriorityPhase = BoostPriorityPhase.Owned;
                return SavePriorityEntry(entry);
            }
        }

        internal static bool CancelUnwrittenBoostPriority(int pid, long creation, uint previous, uint applied)
        {
            lock (sync)
            {
                List<BoostEntry> entries = LoadEntries();
                List<BoostEntry> priorities = LoadPriorityEntries();
                if (entries == null || priorities == null)
                {
                    RememberUnwrittenPriority(pid, creation, previous, applied);
                    return false;
                }
                BoostEntry entry = FindEntry(entries, pid, creation) ?? FindEntry(priorities, pid, creation);
                if (entry == null || entry.PriorityPhase == BoostPriorityPhase.None)
                {
                    ForgetPrioritySettlement(pid, creation);
                    return true;
                }
                if (!PriorityWriteMatches(entry, previous, applied) || entry.PriorityPhase != BoostPriorityPhase.Intent) return false;
                return SettleBoostPriority(entry);
            }
        }

        internal static bool RestoreBoostPriority(IntPtr handle, int pid, long creation)
        {
            if (handle == IntPtr.Zero || pid <= 0 || creation <= 0) return false;
            try
            {
                lock (sync)
                {
                    List<BoostEntry> entries = LoadEntries();
                    if (entries == null) return false;
                    List<BoostEntry> priorities = LoadPriorityEntries();
                    if (priorities == null) return false;
                    BoostEntry entry = FindEntry(entries, pid, creation) ?? FindEntry(priorities, pid, creation);
                    return entry == null || RestoreBoostPriorityEntry(handle, entry);
                }
            }
            catch { return false; }
        }

        private static bool RestoreBoostPriorityEntry(IntPtr handle, BoostEntry entry)
        {
            // 拒绝调用方传入的无关句柄，不能用它结清实际目标的记录。
            if (handle == IntPtr.Zero || GetProcessId(handle) != (uint)entry.Pid) return false;
            BoostIdentity identity = Identify(handle, entry);
            if (identity == BoostIdentity.Unknown) return false;
            if (identity == BoostIdentity.Mismatch) return SettleBoostPriority(entry);
            if (entry.PriorityPhase == BoostPriorityPhase.None || PrioritySettledInMemory(entry)) return SettleBoostPriority(entry);
            uint current = Native.GetPriorityClass(handle);
            if (!ValidPriority(current)) return false;
            if (current != entry.LastAppliedPriority || current == entry.PreviousPriorityForRestore)
                    return SettleBoostPriority(entry);
            identity = Identify(handle, entry);
            if (identity == BoostIdentity.Unknown) return false;
            if (identity == BoostIdentity.Mismatch) return SettleBoostPriority(entry);
            // Windows 没有优先级类别的原子比较交换接口。写入前立即重新读取，若其他所有者已改值就让出。

            current = Native.GetPriorityClass(handle);
            if (!ValidPriority(current)) return false;
            if (current != entry.LastAppliedPriority) return SettleBoostPriority(entry);
            // 新调度只持有查询句柄；确有本方旧写入待恢复时，单独取得写权限并重新核对同一对象。
            IntPtr priorityWriter = Native.OpenProcess(0x1200, false, entry.Pid);
            if (priorityWriter == IntPtr.Zero) return false;
            try
            {
                if (GetProcessId(priorityWriter) != (uint)entry.Pid || Identify(priorityWriter, entry) != BoostIdentity.Match) return false;
                current = Native.GetPriorityClass(priorityWriter);
                if (!ValidPriority(current)) return false;
                if (current != entry.LastAppliedPriority) return SettleBoostPriority(entry);
                Native.SetPriorityClass(priorityWriter, entry.PreviousPriorityForRestore);
            }
            finally { Native.CloseHandle(priorityWriter); }
            current = Native.GetPriorityClass(handle);
            identity = Identify(handle, entry);
            if (identity == BoostIdentity.Unknown) return false;
            if (identity == BoostIdentity.Mismatch) return SettleBoostPriority(entry);
            if (!ValidPriority(current) || current == entry.LastAppliedPriority) return false;
            // 原值已恢复，或后续外部改动已经接管。
            return SettleBoostPriority(entry);
        }

        private static bool SettleBoostPriority(BoostEntry entry)
        {
            if (entry.PriorityPhase == BoostPriorityPhase.None)
            {
                ForgetPrioritySettlement(entry.Pid, entry.Creation);
                return true;
            }
            prioritySettlements[entry.Pid] = new PrioritySettlement
            {
                Creation = entry.Creation,
                Applied = entry.LastAppliedPriority,
                Previous = entry.PreviousPriorityForRestore
            };
            entry.PriorityPhase = BoostPriorityPhase.None;
            entry.LastAppliedPriority = 0;
            entry.PreviousPriorityForRestore = 0;
            if (!SavePriorityEntry(entry)) return false;
            ForgetPrioritySettlement(entry.Pid, entry.Creation);
            return true;
        }

        private static void RememberUnwrittenPriority(int pid, long creation, uint previous, uint applied)
        {
            long ownerBirth;
            if (!ValidPriority(previous) || !ValidPriority(applied) || previous == applied
                || !owned.TryGetValue(pid, out ownerBirth) || ownerBirth != creation) return;
            PrioritySettlement existing;
            if (prioritySettlements.TryGetValue(pid, out existing) && existing.Creation == creation
                && (existing.Previous != previous || existing.Applied != applied)) return;
            prioritySettlements[pid] = new PrioritySettlement { Creation = creation, Previous = previous, Applied = applied };
        }

        private static bool PrioritySettledInMemory(BoostEntry entry)
        {
            PrioritySettlement settled;
            return prioritySettlements.TryGetValue(entry.Pid, out settled) && settled.Creation == entry.Creation
                && settled.Applied == entry.LastAppliedPriority && settled.Previous == entry.PreviousPriorityForRestore;
        }

        private static void ForgetPrioritySettlement(int pid, long creation)
        {
            PrioritySettlement settled;
            if (prioritySettlements.TryGetValue(pid, out settled) && settled.Creation == creation) prioritySettlements.Remove(pid);
        }

        private static bool PriorityWriteMatches(BoostEntry entry, uint previous, uint applied)
        {
            return entry != null && ValidPriority(previous) && ValidPriority(applied)
                && entry.PreviousPriorityForRestore == previous && entry.LastAppliedPriority == applied;
        }

        private static BoostEntry FindEntry(List<BoostEntry> entries, int pid, long creation)
        {
            foreach (BoostEntry entry in entries)
                if (entry.Pid == pid && entry.Creation == creation) return entry;
            return null;
        }

        private static bool ValidPriority(uint priority)
        {
            return priority == 0x20 || priority == 0x40 || priority == 0x80 || priority == 0x100
                || priority == 0x4000 || priority == 0x8000;
        }

        internal static bool TrackBoostAffinity(int pid, long creation, ulong applied)
        {
            return TrackBoostAffinityTransition(pid, creation, applied, 0uL);
        }

        internal static bool TrackBoostAffinityTransition(int pid, long creation, ulong applied, ulong previous)
        {
            lock (sync)
            {
                List<BoostEntry> list = LoadEntries();
                if (list == null) return false;
                foreach (BoostEntry item in list)
                {
                    if (item.Pid == pid && item.Creation == creation)
                    {
                        if (item.PlacementYielded && applied != 0)
                        {
                            return false;
                        }
                        item.PlacementTracked = true;
                        item.AppliedAffinity = applied;
                        item.PreviousAffinity = previous;
                        return SaveEntries(list);
                    }
                }
                return false;
            }
        }

        internal static bool RestoreOwnedAffinity(IntPtr handle, ulong original, ulong applied)
        {
            // 同值应用没有持有进程掩码改动。再次写入会扩大游戏自行设置的线程亲和性范围。

            if (applied == 0 || applied == original)
            {
                return true;
            }
            ulong mask;
            if (!Native.TryQueryAffinity(handle, out mask))
            {
                return false;
            }
            if (mask != applied)
            {
                return true;
            }
            if (Native.SetProcessAffinityMask(handle, (UIntPtr)original) && Native.TryQueryAffinity(handle, out mask))
            {
                return mask == original;
            }
            return false;
        }

        public static void YieldBoostPlacement(int pid, long creation)
        {
            lock (sync)
            {
                List<BoostEntry> list = LoadEntries();
                if (list == null) return;
                bool placementChanged = false;
                foreach (BoostEntry item in list)
                {
                    if (item.Pid == pid && item.Creation == creation && !item.PlacementYielded)
                    {
                        item.PlacementYielded = true;
                        placementChanged = true;
                    }
                }
                if (placementChanged)
                {
                    SaveEntries(list);
                }
            }
        }

        public static void ClearBoost()
        {
            Settings.SaveStr("Crash_BoostMask", "");
            Settings.SaveStr("Crash_BoostNames", "");
        }

        public static bool HasPending()
        {
            string message;
            if (!Settings.TryLoadStr(KBoostEntries, out message)) return true;
            if (string.IsNullOrEmpty(message))
            {
                List<BoostEntry> priorities = LoadPriorityEntries();
                if (priorities == null) return true;
                foreach (BoostEntry entry in priorities)
                    if (entry.PriorityPhase != BoostPriorityPhase.None) return true;
                return CoreThreadPlacement.HasResidue;
            }
            return true;
        }

        public static void HealFromCrash()
        {
            CoreThreadPlacement.Recover();
            UncleanThrottleAtLaunch |= Settings.LoadStr("Crash_ThrottleMasks", "").Length > 0;
            lock (sync)
            {
                HealBoostEntries();
            }
        }

        private static void HealBoostEntries()
        {
            List<BoostEntry> list = LoadEntries();
            if (list == null) return;
            List<BoostEntry> list2 = new List<BoostEntry>();
            int processedCount = 0;
            foreach (BoostEntry item in list)
            {
                IntPtr intPtr = Native.OpenProcess(12800, false, item.Pid);
                if (intPtr == IntPtr.Zero)
                {
                    IntPtr intPtr2 = Native.OpenProcess(4096, false, item.Pid);
                    if (intPtr2 != IntPtr.Zero)
                    {
                        try
                        {
                            if (Identify(intPtr2, item) != BoostIdentity.Mismatch)
                            {
                                list2.Add(item);
                            }
                        }
                        finally
                        {
                            Native.CloseHandle(intPtr2);
                        }
                    }
                    else if (!Native.LastOpenProcessFailureWasNoSuchProcess())
                    {
                        list2.Add(item);
                    }
                    continue;
                }
                try
                {
                    switch (Identify(intPtr, item))
                    {
                    case BoostIdentity.Mismatch:
                        continue;
                    case BoostIdentity.Unknown:
                        list2.Add(item);
                        continue;
                    }
                    ulong affinity = item.Affinity;
                    uint[] cpuSets = item.CpuSets;
                    if (!RestoreBoostPriorityEntry(intPtr, item))
                    {
                        list2.Add(item);
                        continue;
                    }
                    bool placementRestored = !item.PlacementTracked || item.PlacementYielded || (RestoreOwnedAffinity(intPtr, affinity, item.AppliedAffinity) && RestoreOwnedAffinity(intPtr, affinity, item.PreviousAffinity));
                    // 游戏页优先级已退出治理；旧记录也只恢复仍受管的字段。
                    bool restored = SuppressionCore.RestoreValues(intPtr, item.Priority, affinity, item.Io, -1, CpuTopology.AllMask, cpuSets, item.QoSControl, item.QoSState, -1, -1, !item.PlacementTracked && !item.PlacementYielded, false, false) && placementRestored;
                    gpuLeases[GpuLeaseKey(item)] = item.GpuLease;
                    bool gpuRestored = item.GpuLease.Restore(intPtr, () => Native.QueryProcessIdentityState(intPtr, item.Creation), () => SaveEntries(list));
                    restored &= gpuRestored;
                    if (restored)
                    {
                        processedCount++;
                    }
                    else
                    {
                        list2.Add(item);
                    }
                }
                catch
                {
                    list2.Add(item);
                }
                finally
                {
                    Native.CloseHandle(intPtr);
                }
            }
            if (!SaveEntries(list2)) return;
            HealPriorityOrphans(list2);
            PrunePriorityTombstones(list2);
            Settings.SaveStr("Crash_ThrottleMasks", "");
            Settings.SaveStr("Crash_BoostMask", "");
            Settings.SaveStr("Crash_BoostNames", "");
            if (processedCount > 0)
            {
                Logger.Warn(Lang.T("log.crashguard.1", 0) + processedCount + Lang.T("log.crashguard.2", 0));
            }
            if (list2.Count > 0)
            {
                Logger.Warn(Lang.T("log.crashguard.3", 0) + list2.Count + Lang.T("log.crashguard.4", 0));
            }
        }

        private static void HealPriorityOrphans(List<BoostEntry> baseline)
        {
            List<BoostEntry> priorities = LoadPriorityEntries();
            if (priorities == null) return;
            foreach (BoostEntry entry in priorities)
            {
                if (entry.PriorityPhase == BoostPriorityPhase.None || FindEntry(baseline, entry.Pid, entry.Creation) != null) continue;
                IntPtr handle = Native.OpenProcess(12800, false, entry.Pid);
                if (handle == IntPtr.Zero)
                {
                    IntPtr query = Native.OpenProcess(4096, false, entry.Pid);
                    if (query == IntPtr.Zero)
                    {
                        if (Native.LastOpenProcessFailureWasNoSuchProcess()) SettleBoostPriority(entry);
                    }
                    else
                    {
                        try { if (Identify(query, entry) == BoostIdentity.Mismatch) SettleBoostPriority(entry); }
                        finally { Native.CloseHandle(query); }
                    }
                    continue;
                }
                try
                {
                    // 孤立附属记录不包含非 CPU 基准，不能凭空补造。
                    RestoreBoostPriorityEntry(handle, entry);
                }
                catch { }
                finally { Native.CloseHandle(handle); }
            }
        }

        private static BoostIdentity Identify(IntPtr h, BoostEntry entry)
        {
            string imageNameResult = Native.ImageName(h);
            if (imageNameResult == null)
            {
                return BoostIdentity.Unknown;
            }
            if (!string.Equals(imageNameResult, entry.Name, StringComparison.OrdinalIgnoreCase))
            {
                return BoostIdentity.Mismatch;
            }
            long creation;
            long cpu;
            ulong io;
            if (!Native.QueryProcessSample(h, out creation, out cpu, out io))
            {
                return BoostIdentity.Unknown;
            }
            if (creation != entry.Creation)
            {
                return BoostIdentity.Mismatch;
            }
            return BoostIdentity.Match;
        }

        private static List<BoostEntry> LoadEntries()
        {
            List<BoostEntry> baseline = LoadLegacyEntries();
            List<BoostEntry> priorities = LoadPriorityEntries();
            if (baseline == null || priorities == null) return null;
            foreach (BoostEntry entry in baseline)
            {
                GpuPriorityLease memoryLease;
                if (gpuLeases.TryGetValue(GpuLeaseKey(entry), out memoryLease)) entry.GpuLease = memoryLease;
                BoostEntry priority = FindEntry(priorities, entry.Pid, entry.Creation);
                if (priority == null) continue;
                if (!string.Equals(priority.Name, entry.Name, StringComparison.OrdinalIgnoreCase)) return null;
                entry.PriorityPhase = priority.PriorityPhase;
                entry.LastAppliedPriority = priority.LastAppliedPriority;
                entry.PreviousPriorityForRestore = priority.PreviousPriorityForRestore;
            }
            return baseline;
        }

        private static List<BoostEntry> LoadLegacyEntries()
        {
            try
            {
                string message;
                if (!Settings.TryLoadStr(KBoostEntries, out message) || message == null) return null;
                List<BoostEntry> list = new List<BoostEntry>();
                HashSet<string> identities = new HashSet<string>(StringComparer.Ordinal);
                foreach (string line in message.Split(new char[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string[] fields = line.TrimEnd('\r').Split('|');
                    GpuPriorityLease gpuLease = null;
                    if (fields.Length >= 11 && fields[fields.Length - 2] == "gpu-v1")
                    {
                        gpuLease = GpuPriorityLease.Parse(fields[fields.Length - 1]);
                        if (gpuLease == null) return null;
                        Array.Resize(ref fields, fields.Length - 2);
                    }
                    int oldCount = fields.Length;
                    if (oldCount != 9 && oldCount != 11 && oldCount != 12 && oldCount != 14 && oldCount != 15) return null;
                    BoostEntry entry = new BoostEntry();
                    // 保留旧字段位置，避免后面的 GPU、CPU Sets 与恢复归属错位；页值不再参与恢复。
                    int retiredPage;
                    if (!int.TryParse(fields[0], out entry.Pid) || entry.Pid <= 0
                        || !long.TryParse(fields[1], out entry.Creation) || entry.Creation <= 0
                        || !uint.TryParse(fields[3], out entry.Priority) || !ValidPriority(entry.Priority)
                        || !ulong.TryParse(fields[4], out entry.Affinity)
                        || !int.TryParse(fields[5], out entry.Io)
                        || !int.TryParse(fields[6], out retiredPage)
                        || !int.TryParse(fields[7], out entry.Gpu)) return null;
                    entry.GpuLease = gpuLease ?? GpuPriorityLease.Legacy(entry.Gpu);
                    entry.Name = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(fields[2]));
                    if (string.IsNullOrEmpty(entry.Name) || !identities.Add(entry.Pid + ":" + entry.Creation)) return null;
                    entry.CpuSets = ParseCpuSets(fields[8]);
                    if (entry.CpuSets == null) return null;
                    if (oldCount >= 11 && (!int.TryParse(fields[9], out entry.QoSControl) || !int.TryParse(fields[10], out entry.QoSState))) return null;
                    if (oldCount >= 12)
                    {
                        if (fields[11] != "0" && fields[11] != "1") return null;
                        entry.PlacementYielded = fields[11] == "1";
                    }
                    if (oldCount >= 14)
                    {
                        if (fields[13] != "owned" || !ulong.TryParse(fields[12], out entry.AppliedAffinity)) return null;
                        entry.PlacementTracked = true;
                    }
                    if (oldCount == 15 && !ulong.TryParse(fields[14], out entry.PreviousAffinity)) return null;
                    // 旧格式无法证明某次优先级写入曾被跳过。
                    entry.PriorityPhase = BoostPriorityPhase.Legacy;
                    entry.LastAppliedPriority = Native.HIGH_PRIORITY_CLASS;
                    entry.PreviousPriorityForRestore = entry.Priority;
                    list.Add(entry);
                }
                return list;
            }
            catch { return null; }
        }

        private static bool SaveEntries(List<BoostEntry> entries)
        {
            // 不能把不可读或损坏的日志解析成部分有效记录。
            if (entries == null || LoadEntries() == null) return false;
            List<string> list = new List<string>();
            foreach (BoostEntry entry in entries)
            {
                list.Add(entry.Pid + "|" + entry.Creation + "|" + Convert.ToBase64String(Encoding.UTF8.GetBytes(entry.Name ?? "")) + "|" + entry.Priority + "|" + entry.Affinity + "|" + entry.Io + "|-1|" + entry.Gpu + "|" + CpuSetsText(entry.CpuSets) + "|" + entry.QoSControl + "|" + entry.QoSState + (entry.PlacementTracked ? ("|" + (entry.PlacementYielded ? "1" : "0") + "|" + entry.AppliedAffinity + "|owned|" + entry.PreviousAffinity) : (entry.PlacementYielded ? "|1" : "")) + "|gpu-v1|" + entry.GpuLease.Encode());
            }
            string joinResult = string.Join("\n", list.ToArray());
            string value;
            if (Settings.SaveStrDurable("Crash_BoostEntriesV2", joinResult) && Settings.TryLoadStr("Crash_BoostEntriesV2", out value))
            {
                return value == joinResult;
            }
            return false;
        }

        private static List<BoostEntry> LoadPriorityEntries()
        {
            try
            {
                string message;
                if (!Settings.TryLoadStr(PriorityOwnershipKey, out message) || message == null) return null;
                List<BoostEntry> entries = new List<BoostEntry>();
                HashSet<string> identities = new HashSet<string>(StringComparer.Ordinal);
                foreach (string line in message.Split(new char[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string[] fields = line.TrimEnd('\r').Split('|');
                    BoostEntry entry = new BoostEntry();
                    if (fields.Length != 7 || fields[0] != PriorityVersion
                        || !int.TryParse(fields[1], out entry.Pid) || entry.Pid <= 0
                        || !long.TryParse(fields[2], out entry.Creation) || entry.Creation <= 0
                        || !uint.TryParse(fields[5], out entry.LastAppliedPriority)
                        || !uint.TryParse(fields[6], out entry.PreviousPriorityForRestore)) return null;
                    entry.Name = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(fields[3]));
                    if (string.IsNullOrEmpty(entry.Name) || !identities.Add(entry.Pid + ":" + entry.Creation)) return null;
                    switch (fields[4])
                    {
                        case "Legacy": entry.PriorityPhase = BoostPriorityPhase.Legacy; break;
                        case "None": entry.PriorityPhase = BoostPriorityPhase.None; break;
                        case "Intent": entry.PriorityPhase = BoostPriorityPhase.Intent; break;
                        case "Owned": entry.PriorityPhase = BoostPriorityPhase.Owned; break;
                        default: return null;
                    }
                    if (entry.PriorityPhase == BoostPriorityPhase.None)
                    {
                        if (entry.LastAppliedPriority != 0 || entry.PreviousPriorityForRestore != 0) return null;
                    }
                    else if (!ValidPriority(entry.LastAppliedPriority) || !ValidPriority(entry.PreviousPriorityForRestore)
                        || (entry.PriorityPhase == BoostPriorityPhase.Legacy && entry.LastAppliedPriority != Native.HIGH_PRIORITY_CLASS)) return null;
                    entries.Add(entry);
                }
                return entries;
            }
            catch { return null; }
        }

        private static bool SavePriorityEntry(BoostEntry entry)
        {
            if (entry == null || LoadEntries() == null) return false;
            List<BoostEntry> priorities = LoadPriorityEntries();
            if (priorities == null) return false;
            BoostEntry previous = FindEntry(priorities, entry.Pid, entry.Creation);
            if (previous != null && !string.Equals(previous.Name, entry.Name, StringComparison.OrdinalIgnoreCase)) return false;
            priorities.RemoveAll((BoostEntry e) => e.Pid == entry.Pid && e.Creation == entry.Creation);
            priorities.Add(entry);
            return SavePriorityEntries(priorities);
        }

        private static bool SavePriorityEntries(List<BoostEntry> priorities)
        {
            if (priorities == null || LoadEntries() == null) return false;
            List<string> lines = new List<string>();
            foreach (BoostEntry entry in priorities)
                lines.Add(PriorityVersion + "|" + entry.Pid + "|" + entry.Creation + "|"
                    + Convert.ToBase64String(Encoding.UTF8.GetBytes(entry.Name)) + "|" + entry.PriorityPhase
                    + "|" + entry.LastAppliedPriority + "|" + entry.PreviousPriorityForRestore);
            string joinResult = string.Join("\n", lines.ToArray());
            string readback;
            return Settings.SaveStrDurable(PriorityOwnershipKey, joinResult)
                && Settings.TryLoadStr(PriorityOwnershipKey, out readback) && readback == joinResult;
        }

        private static void PrunePriorityTombstones(List<BoostEntry> baseline)
        {
            if (baseline == null) return;
            List<BoostEntry> priorities = LoadPriorityEntries();
            if (priorities == null) return;
            if (priorities.RemoveAll((BoostEntry e) => e.PriorityPhase == BoostPriorityPhase.None
                && FindEntry(baseline, e.Pid, e.Creation) == null) > 0) SavePriorityEntries(priorities);
        }

        private static string CpuSetsText(uint[] ids)
        {
            if (ids == null || ids.Length == 0)
            {
                return "";
            }
            string[] textEntries = new string[ids.Length];
            for (int i = 0; i < ids.Length; i++)
            {
                textEntries[i] = ids[i].ToString();
            }
            return string.Join(",", textEntries);
        }

        private static uint[] ParseCpuSets(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return new uint[0];
            }
            string[] parts = text.Split(',');
            uint[] numericValueItems = new uint[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                if (!uint.TryParse(parts[i], out numericValueItems[i]))
                {
                    return null;
                }
            }
            return numericValueItems;
        }
    }
}
