// @author bdth 2074055628@qq.com
// 文件用途 保存并恢复游戏提优留下的可查询进程状态
using System;
using System.Collections.Generic;
using System.Text;

namespace PaviseApp
{
    internal static class CrashGuard
    {
        private const string KThrottle = "Crash_ThrottleMasks";
        private const string KBoost = "Crash_BoostMask";
        private const string KBoostNames = "Crash_BoostNames";
        private const string KBoostEntries = "Crash_BoostEntriesV2";
        private static readonly object sync = new object();
        private static readonly Dictionary<int, long> owned = new Dictionary<int, long>();
        private enum BoostIdentity { Match, Mismatch, Unknown }

        internal sealed class OriginalBoostState
        {
            public uint Priority;
            public ulong Affinity;
            public int Io;
            public int Page;
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
            public ulong Affinity;
            public int Io;
            public int Page;
            public int Gpu;
            public uint[] CpuSets;

            public int QoSControl = -1;
            public int QoSState = -1;
        }

        public static void MarkThrottle(ulong mask)
        {
            if (mask != 0) Settings.SaveStr(KThrottle, "journaled");
        }

        public static void ReleaseThrottle(ulong mask)
        {
            Settings.SaveStr(KThrottle, "");
        }

        public static bool MarkBoostProcess(int pid, long creation, string name,
            uint priority, ulong affinity, int io, int page, int gpu, uint[] cpuSets)
        {
            return MarkBoostProcess(pid, creation, name, priority, affinity, io, page, gpu, cpuSets, -1, -1);
        }

        public static bool MarkBoostProcess(int pid, long creation, string name,
            uint priority, ulong affinity, int io, int page, int gpu, uint[] cpuSets,
            int qosControl, int qosState)
        {
            OriginalBoostState ignored;
            return MarkBoostProcess(
                pid, creation, name, priority, affinity, io, page, gpu,
                cpuSets, qosControl, qosState, out ignored);
        }

        // 提优之前先把原值记进注册表 崩溃后下次启动照着还原
        //   pid 会被系统回收 所以身份一律是 pid 加创建时间戳两件套
        internal static bool MarkBoostProcess(int pid, long creation, string name,
            uint priority, ulong affinity, int io, int page, int gpu, uint[] cpuSets,
            int qosControl, int qosState, out OriginalBoostState recovered)
        {
            recovered = null;
            if (pid <= 0 || creation <= 0 || string.IsNullOrEmpty(name)) return false;
            lock (sync)
            {
                List<BoostEntry> entries = LoadEntries();
                // 同一个进程被提优第二次时 台账里已有的那份才是真原值
                //   这里把旧值原样交回去 绝不能拿当前这份已经被提过优的值覆盖
                //   否则还原会把进程留在提优状态上
                foreach (BoostEntry old in entries)
                    if (old.Pid == pid && old.Creation == creation)
                    {
                        // pid 和创建时间都对上 名字却不一样 说明台账已经不可信
                        //   拒绝认领 让这条记录留着 交给启动时的还原流程处理
                        if (!string.Equals(
                                old.Name, name,
                                StringComparison.OrdinalIgnoreCase))
                            return false;

                        owned[pid] = creation;
                        recovered = new OriginalBoostState
                        {
                            Priority = old.Priority,
                            Affinity = old.Affinity,
                            Io = old.Io,
                            Page = old.Page,
                            Gpu = old.Gpu,
                            CpuSets = old.CpuSets == null
                                ? new uint[0]
                                : (uint[])old.CpuSets.Clone(),
                            QoSControl = old.QoSControl,
                            QoSState = old.QoSState
                        };
                        return true;
                    }
                // 同 pid 但创建时间不同的旧条目是 pid 回收留下的残渣 直接丢掉
                entries.RemoveAll(e => e.Pid == pid);
                entries.Add(new BoostEntry
                {
                    Pid = pid, Creation = creation, Name = name, Priority = priority,
                    Affinity = affinity, Io = io, Page = page, Gpu = gpu, CpuSets = cpuSets,
                    QoSControl = qosControl, QoSState = qosState
                });
                bool saved = SaveEntries(entries);
                // 台账真的落盘了才认所有权 写失败就当没提过优
                //   宁可少还原一次 也不能让内存说有账而磁盘上没有
                if (saved) owned[pid] = creation; else owned.Remove(pid);
                return saved;
            }
        }

        public static void ReleaseBoostProcess(int pid, long creation)
        {
            lock (sync)
            {
                long mine;
                if (!owned.TryGetValue(pid, out mine)) return;
                // 创建时间对不上说明这个 pid 已经换了主人 别去释放别人的记录
                if (creation > 0 && mine != creation) return;
                owned.Remove(pid);
                List<BoostEntry> entries = LoadEntries();
                if (entries.RemoveAll(e => e.Pid == pid && (creation <= 0 || e.Creation == creation)) > 0)
                    SaveEntries(entries);
            }
        }

        public static void ClearBoost()
        {
            Settings.SaveStr(KBoost, "");
            Settings.SaveStr(KBoostNames, "");
        }

        public static bool HasPending()
        {
            return Settings.LoadStr(KBoostEntries, "").Length > 0;
        }

        public static bool UncleanThrottleAtLaunch { get; private set; }

        // 上次异常退出遗留的提优在这里收回 三种身份判定各走各的路
        //   对不上就丢弃 拿不准就留着下次再试 对得上才真还原
        public static void HealFromCrash()
        {
            UncleanThrottleAtLaunch |= Settings.LoadStr(KThrottle, "").Length > 0;
            List<BoostEntry> entries;
            lock (sync) entries = LoadEntries();
            var keep = new List<BoostEntry>();
            int restored = 0;

            foreach (BoostEntry entry in entries)
            {
                IntPtr h = Native.OpenProcess(Native.PROCESS_SET_INFORMATION | Native.PROCESS_SET_LIMITED_INFORMATION
                    | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, entry.Pid);
                if (h == IntPtr.Zero)
                {
                    IntPtr query = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, entry.Pid);
                    if (query != IntPtr.Zero)
                    {
                        try
                        {
                            if (Identify(query, entry) != BoostIdentity.Mismatch) keep.Add(entry);
                        }
                        finally { Native.CloseHandle(query); }
                    }
                    // 打不开且不是进程已消失 那多半是权限问题 记录留着别删
                    else if (!Native.LastOpenProcessFailureWasNoSuchProcess())
                    {
                        keep.Add(entry);
                    }
                    continue;
                }

                try
                {
                    BoostIdentity identity = Identify(h, entry);
                    if (identity == BoostIdentity.Mismatch) continue;
                    // 身份查不出来不等于身份不对 留着等下次启动 不还原也不丢弃
                    if (identity == BoostIdentity.Unknown)
                    {
                        keep.Add(entry);
                        continue;
                    }
                    bool ok = SuppressionCore.RestoreValues(h, entry.Priority, entry.Affinity,
                        entry.Io, entry.Page, CpuTopology.AllMask, entry.CpuSets,
                        entry.QoSControl, entry.QoSState);
                    if (ok && entry.Gpu >= 0)
                        ok = Native.D3DKMTSetProcessSchedulingPriorityClass(h, entry.Gpu) == 0;
                    if (ok) restored++;
                    else keep.Add(entry);
                }
                catch { keep.Add(entry); }
                finally { Native.CloseHandle(h); }
            }

            // keep 里是这轮没还原成的欠账 原样写回去 下次启动接着试
            lock (sync) SaveEntries(keep);
            Settings.SaveStr(KThrottle, "");
            Settings.SaveStr(KBoost, "");
            Settings.SaveStr(KBoostNames, "");
            if (restored > 0) Logger.Log(Lang.T("log.crashguard.1") + restored + Lang.T("log.crashguard.2"));
            if (keep.Count > 0) Logger.Log(Lang.T("log.crashguard.3") + keep.Count + Lang.T("log.crashguard.4"));
        }

        private static BoostIdentity Identify(IntPtr h, BoostEntry entry)
        {
            string name = Native.ImageName(h);
            if (name == null) return BoostIdentity.Unknown;
            if (!string.Equals(name, entry.Name, StringComparison.OrdinalIgnoreCase))
                return BoostIdentity.Mismatch;
            long creation, cpu; ulong io;
            if (!Native.QueryProcessSample(h, out creation, out cpu, out io))
                return BoostIdentity.Unknown;
            return creation == entry.Creation ? BoostIdentity.Match : BoostIdentity.Mismatch;
        }

#if PAVISE_SELFTEST
        internal static string ProbeParse(string raw)
        {
            string prev = Settings.LoadStr(KBoostEntries, "");
            try
            {
                Settings.SaveStr(KBoostEntries, raw);
                List<BoostEntry> list = LoadEntries();
                if (list.Count == 0) return "0";
                return list.Count + "|" + list[0].QoSControl + "|" + list[0].QoSState;
            }
            finally { Settings.SaveStr(KBoostEntries, prev); }
        }
#endif

        private static List<BoostEntry> LoadEntries()
        {
            var entries = new List<BoostEntry>();
            string raw = Settings.LoadStr(KBoostEntries, "");
            foreach (string line in raw.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] a = line.TrimEnd('\r').Split('|');
                int pid, io, page, gpu; long creation; uint pri; ulong aff;
                if (a.Length < 9 || !int.TryParse(a[0], out pid) || !long.TryParse(a[1], out creation)
                    || !uint.TryParse(a[3], out pri) || !ulong.TryParse(a[4], out aff)
                    || !int.TryParse(a[5], out io) || !int.TryParse(a[6], out page)
                    || !int.TryParse(a[7], out gpu)) continue;
                uint[] cpuSets = ParseCpuSets(a[8]);
                if (cpuSets == null) continue;
                string name;
                try { name = Encoding.UTF8.GetString(Convert.FromBase64String(a[2])); }
                catch { continue; }
                int qc = -1, qs = -1;
                if (a.Length >= 11) { if (!int.TryParse(a[9], out qc)) qc = -1; if (!int.TryParse(a[10], out qs)) qs = -1; }
                entries.Add(new BoostEntry
                {
                    Pid = pid, Creation = creation, Name = name, Priority = pri,
                    Affinity = aff, Io = io, Page = page, Gpu = gpu, CpuSets = cpuSets,
                    QoSControl = qc, QoSState = qs
                });
            }
            return entries;
        }

        private static bool SaveEntries(List<BoostEntry> entries)
        {
            var lines = new List<string>();
            foreach (BoostEntry e in entries)
                lines.Add(e.Pid + "|" + e.Creation + "|"
                    + Convert.ToBase64String(Encoding.UTF8.GetBytes(e.Name ?? "")) + "|"
                    + e.Priority + "|" + e.Affinity + "|" + e.Io + "|" + e.Page + "|" + e.Gpu
                    + "|" + CpuSetsText(e.CpuSets) + "|" + e.QoSControl + "|" + e.QoSState);
            string value = string.Join("\n", lines.ToArray());
            Settings.SaveStr(KBoostEntries, value);
            return Settings.LoadStr(KBoostEntries, "") == value;
        }

        private static string CpuSetsText(uint[] ids)
        {
            if (ids == null || ids.Length == 0) return "";
            var values = new string[ids.Length];
            for (int i = 0; i < ids.Length; i++) values[i] = ids[i].ToString();
            return string.Join(",", values);
        }

        private static uint[] ParseCpuSets(string text)
        {
            if (string.IsNullOrEmpty(text)) return new uint[0];
            string[] parts = text.Split(',');
            var ids = new uint[parts.Length];
            for (int i = 0; i < parts.Length; i++) if (!uint.TryParse(parts[i], out ids[i])) return null;
            return ids;
        }
    }
}
