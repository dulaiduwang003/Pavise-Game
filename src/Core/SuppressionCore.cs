// @author bdth 2074055628@qq.com
// 文件用途 统一管理进程压制 快照 回读和恢复

using System;
using System.Collections.Generic;
using System.Threading;

namespace AegisApp
{
    [Flags]
    internal enum SuppressReason
    {
        None = 0,
        AntiCheat = 1,
        Background = 2
    }

    internal enum AcquireResult
    {
        AlreadyThrottled,
        NewlyThrottled,
        NewlyProtected,
        AlreadyProtected,
        ApplyFailed
    }

    internal sealed partial class SuppressionCore
    {
        public const string StateFileName = "Aegis.suppression.state";
        private sealed class Entry
        {
            public string Name;
            public string Group;
            public uint OrigPri;
            public ulong OrigAff;
            public int OrigIo = -1;
            public int OrigPg = -1;
            public uint[] OrigCpuSets;
            // 进程原本的 PowerThrottling 状态。-1 表示读取失败/未知，
            // 此时还原只能退回"交给系统托管"，其余情况必须原样写回：
            // Edge / Teams / OneDrive 这类后台进程会自己主动开 EcoQoS，
            // 一律写 0 等于把它们自愿的省电设置永久剥掉。
            public int OrigQoSControl = -1;
            public int OrigQoSState = -1;
            public long Creation;
            public SuppressionLevel Level;
            public SuppressionLevel AntiCheatLevel;
            public SuppressionLevel BackgroundLevel;
            public bool Applied;
            public SuppressReason Reasons;
        }

        private enum RestoreResult { Restored, Gone, Protected }

        private readonly object sync = new object();
        private readonly Dictionary<int, Entry> map = new Dictionary<int, Entry>();
        private readonly ulong throttleMask;
        private readonly ulong allMask;
        private readonly string journalPath;
        private bool marked;
        private int batchDepth;
        private bool batchJournalDirty;
        private readonly Dictionary<int, string> batchApply = new Dictionary<int, string>();
        private readonly Dictionary<int, bool> batchApplyResults = new Dictionary<int, bool>();
        public string LastApplyError { get; private set; }

        public SuppressionCore() : this(null) { }

        public SuppressionCore(string statePath)
        {
            throttleMask = CpuTopology.ThrottleMask;
            allMask = CpuTopology.AllMask;
            journalPath = statePath;
        }

        public ulong ThrottleMask { get { return throttleMask; } }

        public void BeginBatch()
        {
            Monitor.Enter(sync);
            if (batchDepth == 0) batchApplyResults.Clear();
            batchDepth++;
        }

        public void EndBatch()
        {
            List<KeyValuePair<int, string>> pending = null;
            bool journalOk = true;
            try
            {
                if (batchDepth <= 0) return;
                batchDepth--;
                if (batchDepth == 0)
                {
                    if (batchJournalDirty) journalOk = SaveJournalLocked();
                    batchJournalDirty = false;
                    if (batchApply.Count > 0)
                    {
                        pending = new List<KeyValuePair<int, string>>(batchApply);
                        batchApply.Clear();
                    }
                }
            }
            finally { Monitor.Exit(sync); }

            if (pending != null)
                foreach (KeyValuePair<int, string> item in pending)
                {
                    bool ok = journalOk && ApplyQueued(item.Key, item.Value);
                    lock (sync) batchApplyResults[item.Key] = ok;
                }
        }

        public bool ConsumeBatchApplyResult(int pid)
        {
            lock (sync)
            {
                bool ok;
                if (!batchApplyResults.TryGetValue(pid, out ok)) return false;
                batchApplyResults.Remove(pid);
                return ok;
            }
        }

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
                // 读不到映像名就不能当作"身份已确认"：Sweep 在把进程放进待处理队列时
                // 已经要求路径可读（见 BasicBackgroundEligible），所以此刻读不到，本身
                // 就说明这个 PID 已经换成了另一个受保护/加固的进程。宁可放过不压。
                string img = Native.ImageName(h);
                if (img == null || !SameName(img, name)) return AcquireResult.AlreadyProtected;
                long currentCreation = 0, sampleCpu; ulong sampleIo;
                bool identityKnown = Native.QueryProcessSample(h, out currentCreation, out sampleCpu, out sampleIo);
                lock (sync)
                {
                    Entry e;
                    bool known = map.TryGetValue(pid, out e);
                    if (known && !SameName(e.Name, name)) { map.Remove(pid); known = false; e = null; }
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

                    if (known && e.OrigPri != uint.MaxValue)
                    {
                        e.Reasons |= reason;
                        if (group != null && e.Group == null) e.Group = group;
                        SetReasonLevel(e, reason, level);
                        if (!PersistJournalLocked()) return AcquireResult.ApplyFailed;
                        if (QueueApplyLocked(pid, name)) return AcquireResult.AlreadyThrottled;
                        e.Applied = ApplyThrottle(h, e.Level, e.OrigPri, e.OrigAff, e.OrigCpuSets);
                        return e.Applied ? AcquireResult.AlreadyThrottled : AcquireResult.ApplyFailed;
                    }

                    uint rawPri = Native.GetPriorityClass(h);
                    // rawPri==0 表示查询失败，不是"这个进程是 Normal"。把它当 Normal 存进快照，
                    // 还原时会把原本 HIGH/ABOVE_NORMAL 的进程永久降级，所以直接放弃这次压制。
                    if (rawPri == 0) return AcquireResult.ApplyFailed;
                    bool residue = rawPri == Native.IDLE_PRIORITY_CLASS;
                    uint orig = residue ? Native.NORMAL_PRIORITY_CLASS : rawPri;
                    ulong oaff = Native.QueryAffinity(h);
                    uint[] ocpuSets = Native.QueryCpuSets(h);
                    if (ocpuSets == null) return AcquireResult.ApplyFailed;
                    if (residue && oaff == throttleMask) oaff = 0;
                    int oio = Native.QueryIoPriority(h);
                    if (residue && oio == 0) oio = -1;
                    int opg = Native.QueryPagePriority(h);
                    if (residue && opg == 1) opg = -1;
                    int oqc, oqs;
                    if (!Native.TryQueryPowerThrottling(h, out oqc, out oqs)) { oqc = -1; oqs = -1; }
                    else if (residue && oqc == 1 && oqs == 1) { oqc = -1; oqs = -1; }
                    long creation = identityKnown ? currentCreation : 0;

                    if (known)
                    {
                        e.OrigPri = orig; e.OrigAff = oaff; e.OrigIo = oio; e.OrigPg = opg; e.OrigCpuSets = ocpuSets;
                        e.OrigQoSControl = oqc; e.OrigQoSState = oqs;
                        e.Reasons |= reason; SetReasonLevel(e, reason, level); e.Creation = creation; e.Applied = false;
                        if (group != null && e.Group == null) e.Group = group;
                    }
                    else
                    {
                        var created = new Entry { Name = name, Group = group, OrigPri = orig, OrigAff = oaff,
                            OrigIo = oio, OrigPg = opg, OrigCpuSets = ocpuSets,
                            OrigQoSControl = oqc, OrigQoSState = oqs, Reasons = reason, Creation = creation };
                        SetReasonLevel(created, reason, level);
                        map[pid] = created;
                    }
                    if (!PersistJournalLocked()) return AcquireResult.ApplyFailed;
                    bool queued = QueueApplyLocked(pid, name);
                    bool applied = queued || ApplyThrottle(h, level, orig, oaff, ocpuSets);
                    Entry appliedEntry;
                    if (map.TryGetValue(pid, out appliedEntry) && !queued) appliedEntry.Applied = applied;
                    if (!marked) { marked = true; CrashGuard.MarkThrottle(throttleMask); }
                    return applied ? AcquireResult.NewlyThrottled : AcquireResult.ApplyFailed;
                }
            }
            finally { Native.CloseHandle(h); }
        }

        public bool Release(int pid, SuppressReason reason)
        {
            bool had;
            ReleaseOne(pid, reason, out had);
            return had;
        }

        public int ReleaseReason(SuppressReason reason)
        {
            int restored = 0; bool had;
            foreach (int pid in PidsWith(reason)) restored += ReleaseOne(pid, reason, out had);
            return restored;
        }

        public int ReleaseByName(string name, SuppressReason reason)
        {
            var pids = new List<int>();
            lock (sync)
                foreach (var kv in map)
                    if ((kv.Value.Reasons & reason) != 0 && SameName(kv.Value.Name, name)) pids.Add(kv.Key);
            int restored = 0; bool had;
            foreach (int pid in pids) restored += ReleaseOne(pid, reason, out had);
            return restored;
        }

        private int ReleaseOne(int pid, SuppressReason reason, out bool had)
        {
            Entry e;
            bool adjust = false;
            bool remaining = false;
            lock (sync)
            {
                had = map.TryGetValue(pid, out e) && (e.Reasons & reason) != 0;
                if (!had) return 0;
                e.Reasons &= ~reason;
                if ((reason & SuppressReason.AntiCheat) != 0) e.AntiCheatLevel = SuppressionLevel.None;
                if ((reason & SuppressReason.Background) != 0) e.BackgroundLevel = SuppressionLevel.None;
                e.Level = EffectiveLevel(e);
                if (e.Reasons != SuppressReason.None)
                {
                    remaining = true;
                    adjust = e.OrigPri != uint.MaxValue;
                    SaveJournalLocked();
                }
                else if (e.OrigPri == uint.MaxValue) { map.Remove(pid); SaveJournalLocked(); return 0; }
            }
            if (adjust)
            {
                IntPtr h = Native.OpenProcess(Native.PROCESS_SET_INFORMATION | Native.PROCESS_SET_LIMITED_INFORMATION
                    | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                bool applied = false;
                if (h != IntPtr.Zero) { try { if (SameProcess(h, e)) applied = ApplyThrottle(h, e.Level, e.OrigPri, e.OrigAff, e.OrigCpuSets); } finally { Native.CloseHandle(h); } }
                lock (sync) { Entry cur; if (map.TryGetValue(pid, out cur) && cur == e) cur.Applied = applied; }
                return 0;
            }
            if (remaining) return 0;
            return TryRestore(pid, e) ? 1 : 0;
        }

        private bool TryRestore(int pid, Entry e)
        {
            RestoreResult r = RestoreOne(pid, e);
            bool reThrottle = false;
            lock (sync)
            {
                Entry cur;
                if (map.TryGetValue(pid, out cur) && cur == e)
                {
                    if (e.Reasons == SuppressReason.None)
                    {
                        if (r != RestoreResult.Protected) map.Remove(pid);
                    }
                    else if (r == RestoreResult.Restored) reThrottle = true;
                }
                TryClearMarkLocked();
            }
            if (reThrottle)
            {
                IntPtr h = Native.OpenProcess(Native.PROCESS_SET_INFORMATION | Native.PROCESS_SET_LIMITED_INFORMATION
                    | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                bool applied = false;
                if (h != IntPtr.Zero) { try { if (SameProcess(h, e)) applied = ApplyThrottle(h, e.Level, e.OrigPri, e.OrigAff, e.OrigCpuSets); } finally { Native.CloseHandle(h); } }
                lock (sync) { Entry cur; if (map.TryGetValue(pid, out cur) && cur == e) cur.Applied = applied; }
                return false;
            }
            if (r == RestoreResult.Protected)
                Logger.Log("还原 " + e.Name + " (pid " + pid + ") 暂被句柄保护挡住，快照保留待重试");
            return r == RestoreResult.Restored;
        }

        public void RetryPending()
        {
            List<KeyValuePair<int, Entry>> pending = null;
            lock (sync)
                foreach (var kv in map)
                    if (kv.Value.Reasons == SuppressReason.None)
                    {
                        if (pending == null) pending = new List<KeyValuePair<int, Entry>>();
                        pending.Add(kv);
                    }
            if (pending == null) return;
            foreach (var kv in pending)
                if (TryRestore(kv.Key, kv.Value))
                    Logger.Log("补还原成功：" + kv.Value.Name + " (pid " + kv.Key + ")");
        }

        private void TryClearMarkLocked()
        {
            SaveJournalLocked();
            if (!marked) return;
            foreach (var kv in map) if (kv.Value.OrigPri != uint.MaxValue) return;
            marked = false;
            CrashGuard.ReleaseThrottle(throttleMask);
        }

        public bool HasReason(int pid, SuppressReason reason)
        {
            lock (sync) { Entry e; return map.TryGetValue(pid, out e) && (e.Reasons & reason) != 0; }
        }

        public bool IsThrottled(int pid)
        {
            lock (sync) { Entry e; return map.TryGetValue(pid, out e) && e.OrigPri != uint.MaxValue && e.Applied; }
        }

        public SuppressionLevel LevelOf(int pid)
        {
            lock (sync) { Entry e; return map.TryGetValue(pid, out e) ? e.Level : SuppressionLevel.None; }
        }

        public SuppressionLevel LevelOf(int pid, SuppressReason reason)
        {
            lock (sync)
            {
                Entry e;
                if (!map.TryGetValue(pid, out e)) return SuppressionLevel.None;
                SuppressionLevel level = SuppressionLevel.None;
                if ((reason & SuppressReason.AntiCheat) != 0) level = e.AntiCheatLevel;
                if ((reason & SuppressReason.Background) != 0 && e.BackgroundLevel > level) level = e.BackgroundLevel;
                return level;
            }
        }

        public bool Reapply(int pid, string expectedName, SuppressReason reason)
        {
            uint pri;
            ulong aff;
            uint[] cpuSets;
            SuppressionLevel level;
            lock (sync)
            {
                Entry e;
                if (!map.TryGetValue(pid, out e) || (e.Reasons & reason) == 0
                    || e.OrigPri == uint.MaxValue || !SameName(e.Name, expectedName)) return false;
                pri = e.OrigPri;
                aff = e.OrigAff;
                cpuSets = e.OrigCpuSets;
                level = e.Level;
            }

            IntPtr h = Native.OpenProcess(Native.PROCESS_SET_INFORMATION | Native.PROCESS_SET_LIMITED_INFORMATION
                | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return false;
            try
            {
                string current = Native.ImageName(h);
                if (current != null && !SameName(current, expectedName)) return false;
                long creation, cpu; ulong io;
                if (!Native.QueryProcessSample(h, out creation, out cpu, out io)) return false;
                lock (sync)
                {
                    Entry currentEntry;
                    if (!map.TryGetValue(pid, out currentEntry)
                        || currentEntry.Creation > 0 && currentEntry.Creation != creation) return false;
                }
                bool applied = ApplyThrottle(h, level, pri, aff, cpuSets);
                lock (sync)
                {
                    Entry currentEntry;
                    if (map.TryGetValue(pid, out currentEntry) && SameName(currentEntry.Name, expectedName))
                        currentEntry.Applied = applied;
                }
                return applied;
            }
            finally { Native.CloseHandle(h); }
        }

        public bool AnyWith(SuppressReason reason)
        {
            lock (sync)
                foreach (var kv in map) if ((kv.Value.Reasons & reason) != 0) return true;
            return false;
        }

        public string NameOf(int pid)
        {
            lock (sync) { Entry e; return map.TryGetValue(pid, out e) ? e.Name : null; }
        }

        public List<int> PidsWith(SuppressReason reason)
        {
            var list = new List<int>();
            lock (sync)
                foreach (var kv in map) if ((kv.Value.Reasons & reason) != 0) list.Add(kv.Key);
            return list;
        }

        private int lastThrottledCount;

        // 这个计数只用来显示状态文字，却是从 UI 线程调用的，而 sync 会被整轮后台批处理
        // 长时间持有（每个进程都要走一遍 OpenProcess/查询/写入）。UI 线程在这里等锁时
        // 还握着 GameMode.sync，会把工作线程一起拖住，表现为游戏中界面每几秒卡一下。
        // 拿不到锁就返回上一次的数字：状态文字晚几秒刷新无所谓，界面卡住不行。
        public int CountThrottled(SuppressReason reason)
        {
            if (!Monitor.TryEnter(sync, 15)) return Volatile.Read(ref lastThrottledCount);
            try
            {
                int n = 0;
                foreach (var kv in map) if ((kv.Value.Reasons & reason) != 0 && kv.Value.OrigPri != uint.MaxValue && kv.Value.Applied) n++;
                Volatile.Write(ref lastThrottledCount, n);
                return n;
            }
            finally { Monitor.Exit(sync); }
        }

        public void AntiCheatGroupCounts(string groupKey, out int throttled, out int protectedCnt)
        {
            int t = 0, f = 0;
            lock (sync)
                foreach (var kv in map)
                    if ((kv.Value.Reasons & SuppressReason.AntiCheat) != 0 && SameName(kv.Value.Group, groupKey))
                    {
                        if (kv.Value.OrigPri == uint.MaxValue || !kv.Value.Applied) f++; else t++;
                    }
            throttled = t; protectedCnt = f;
        }

        private bool ApplyThrottle(IntPtr h, SuppressionLevel level, uint originalPriority, ulong originalAffinity,
            uint[] originalCpuSets)
        {
            var failed = new List<string>();
            uint desiredPriority = originalPriority == 0 || originalPriority == uint.MaxValue
                ? Native.NORMAL_PRIORITY_CLASS : originalPriority;
            if (level == SuppressionLevel.Eco)
            {
                if (!Native.SetPriorityClass(h, desiredPriority)) failed.Add("priority-write");
            }
            else if (level >= SuppressionLevel.Restrained)
            {
                desiredPriority = level >= SuppressionLevel.Isolated ? Native.IDLE_PRIORITY_CLASS : Native.BELOW_NORMAL_PRIORITY_CLASS;
                if (!Native.SetPriorityClass(h, desiredPriority)) failed.Add("priority-write");
            }
            if (level >= SuppressionLevel.Isolated)
            {
                if (CpuTopology.HasSafeBackgroundPartition())
                {
                    uint[] backgroundCpuSets = CpuTopology.BackgroundCpuSetIds();
                    bool soft = Native.TrySetCpuSets(h, backgroundCpuSets);
                    if (soft && !Native.CpuSetsMatch(h, backgroundCpuSets)) failed.Add("cpu-sets-readback");
                    if (!soft && !CpuTopology.MultiGroup)
                    {
                        if (!Native.SetProcessAffinityMask(h, (UIntPtr)throttleMask)) failed.Add("affinity-write");
                        else if (Native.QueryAffinity(h) != throttleMask) failed.Add("affinity-readback");
                    }
                    else if (!soft) failed.Add("cpu-sets-write");
                }
            }
            else
            {
                if (!Native.RestoreCpuSetsVerified(h, originalCpuSets)) failed.Add("cpu-sets-restore");
                if (!CpuTopology.MultiGroup)
                {
                    ulong desiredAffinity = originalAffinity != 0 ? originalAffinity : allMask;
                    if (!Native.SetProcessAffinityMask(h, (UIntPtr)desiredAffinity)) failed.Add("affinity-restore");
                    else if (Native.QueryAffinity(h) != desiredAffinity) failed.Add("affinity-restore-readback");
                }
            }
            int io = level >= SuppressionLevel.Isolated ? 0 : 1;
            if (!Native.TrySetIoPriority(h, io)) failed.Add("io-write");
            int pg = level >= SuppressionLevel.Isolated ? 1 : 3;
            if (!Native.TrySetPagePriority(h, pg)) failed.Add("page-write");
            if (!Native.ApplyEcoQoS(h)) failed.Add("eco-write");
            if (Native.GetPriorityClass(h) != desiredPriority) failed.Add("priority-readback");
            if (Native.QueryIoPriority(h) != io) failed.Add("io-readback");
            if (Native.QueryPagePriority(h) != pg) failed.Add("page-readback");
            LastApplyError = string.Join(",", failed.ToArray());
            return failed.Count == 0;
        }

        public static bool RestoreValues(IntPtr h, uint pri, ulong aff, int io, int pg, ulong allMask)
        {
            return RestoreValues(h, pri, aff, io, pg, allMask, new uint[0]);
        }

        public static bool RestoreValues(IntPtr h, uint pri, ulong aff, int io, int pg, ulong allMask,
            uint[] cpuSets)
        {
            // 不带 QoS 参数的旧入口（崩溃恢复日志里没有这个字段）：沿用"交给系统托管"
            return RestoreValues(h, pri, aff, io, pg, allMask, cpuSets, -1, -1);
        }

        public static bool RestoreValues(IntPtr h, uint pri, ulong aff, int io, int pg, ulong allMask,
            uint[] cpuSets, int qosControl, int qosState)
        {
            bool ok = Native.RestoreCpuSetsVerified(h, cpuSets);
            uint desiredPriority = pri == 0 || pri == uint.MaxValue ? Native.NORMAL_PRIORITY_CLASS : pri;
            ok &= Native.SetPriorityClass(h, desiredPriority);
            ulong desiredAffinity = aff != 0 ? aff : allMask;
            if (!CpuTopology.MultiGroup) ok &= Native.SetProcessAffinityMask(h, (UIntPtr)desiredAffinity);
            int rio = io >= 0 ? io : 2; ok &= Native.TrySetIoPriority(h, rio);
            int rpg = pg >= 0 ? pg : 5; ok &= Native.TrySetPagePriority(h, rpg);
            ok &= Native.RestorePowerThrottling(h, qosControl, qosState);
            ok &= Native.GetPriorityClass(h) == desiredPriority;
            ok &= Native.QueryIoPriority(h) == rio;
            ok &= Native.QueryPagePriority(h) == rpg;
            if (!CpuTopology.MultiGroup) ok &= Native.QueryAffinity(h) == desiredAffinity;
            return ok;
        }

        private RestoreResult RestoreOne(int pid, Entry e)
        {
            IntPtr h = Native.OpenProcess(Native.PROCESS_SET_INFORMATION | Native.PROCESS_SET_LIMITED_INFORMATION
                | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero)
            {
                IntPtr hq = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (hq == IntPtr.Zero) return RestoreResult.Gone;
                try
                {
                    string nm = Native.ImageName(hq);
                    return nm == null || SameName(nm, e.Name) ? RestoreResult.Protected : RestoreResult.Gone;
                }
                finally { Native.CloseHandle(hq); }
            }
            try
            {
                if (e.Name != null)
                {
                    string cur = Native.ImageName(h);
                    if (cur != null && !SameName(cur, e.Name)) return RestoreResult.Gone;
                }
                long creation, cpu; ulong io;
                if (e.Creation > 0)
                {
                    if (!Native.QueryProcessSample(h, out creation, out cpu, out io)) return RestoreResult.Protected;
                    if (creation != e.Creation) return RestoreResult.Gone;
                }
                return RestoreValues(h, e.OrigPri, e.OrigAff, e.OrigIo, e.OrigPg, allMask, e.OrigCpuSets,
                        e.OrigQoSControl, e.OrigQoSState)
                    ? RestoreResult.Restored : RestoreResult.Protected;
            }
            finally { Native.CloseHandle(h); }
        }

        private static bool SameProcess(IntPtr h, Entry e)
        {
            if (e.Name != null)
            {
                string cur = Native.ImageName(h);
                if (cur != null && !SameName(cur, e.Name)) return false;
            }
            if (e.Creation > 0)
            {
                long creation, cpu; ulong io;
                if (!Native.QueryProcessSample(h, out creation, out cpu, out io)) return false;
                if (creation != e.Creation) return false;
            }
            return true;
        }

        private static bool SameName(string a, string b)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        private static void SetReasonLevel(Entry e, SuppressReason reason, SuppressionLevel level)
        {
            if ((reason & SuppressReason.AntiCheat) != 0) e.AntiCheatLevel = level;
            if ((reason & SuppressReason.Background) != 0) e.BackgroundLevel = level;
            e.Level = EffectiveLevel(e);
        }

        private static SuppressionLevel EffectiveLevel(Entry e)
        {
            return e.AntiCheatLevel > e.BackgroundLevel ? e.AntiCheatLevel : e.BackgroundLevel;
        }

        private bool PersistJournalLocked()
        {
            if (batchDepth > 0) { batchJournalDirty = true; return true; }
            return SaveJournalLocked();
        }

        private bool QueueApplyLocked(int pid, string name)
        {
            if (batchDepth <= 0) return false;
            batchApply[pid] = name;
            return true;
        }

        private bool ApplyQueued(int pid, string expectedName)
        {
            uint pri;
            ulong aff;
            uint[] cpuSets;
            SuppressionLevel level;
            long expectedCreation;
            lock (sync)
            {
                Entry e;
                if (!map.TryGetValue(pid, out e) || e.Reasons == SuppressReason.None
                    || e.OrigPri == uint.MaxValue || !SameName(e.Name, expectedName)) return false;
                pri = e.OrigPri; aff = e.OrigAff; level = e.Level; expectedCreation = e.Creation;
                cpuSets = e.OrigCpuSets;
            }
            IntPtr h = Native.OpenProcess(Native.PROCESS_SET_INFORMATION | Native.PROCESS_SET_LIMITED_INFORMATION
                | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return false;
            try
            {
                string current = Native.ImageName(h);
                if (current != null && !SameName(current, expectedName)) return false;
                if (expectedCreation > 0)
                {
                    long creation, cpu; ulong io;
                    if (!Native.QueryProcessSample(h, out creation, out cpu, out io) || creation != expectedCreation) return false;
                }
                bool applied = ApplyThrottle(h, level, pri, aff, cpuSets);
                lock (sync)
                {
                    Entry currentEntry;
                    if (map.TryGetValue(pid, out currentEntry) && SameName(currentEntry.Name, expectedName))
                        currentEntry.Applied = applied;
                }
                return applied;
            }
            finally { Native.CloseHandle(h); }
        }

    }
}
