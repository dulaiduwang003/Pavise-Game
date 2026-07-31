// @author bdth 2074055628@qq.com
// 文件用途 保存并恢复游戏提优留下的可查询进程状态

using System;
using System.Collections.Generic;
using System.Text;

namespace AegisApp
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

        internal static bool MarkBoostProcess(int pid, long creation, string name,
            uint priority, ulong affinity, int io, int page, int gpu, uint[] cpuSets,
            int qosControl, int qosState, out OriginalBoostState recovered)
        {
            recovered = null;
            if (pid <= 0 || creation <= 0 || string.IsNullOrEmpty(name)) return false;
            lock (sync)
            {
                List<BoostEntry> entries = LoadEntries();
                foreach (BoostEntry old in entries)
                    if (old.Pid == pid && old.Creation == creation)
                    {
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
                entries.RemoveAll(e => e.Pid == pid);
                entries.Add(new BoostEntry
                {
                    Pid = pid, Creation = creation, Name = name, Priority = priority,
                    Affinity = affinity, Io = io, Page = page, Gpu = gpu, CpuSets = cpuSets,
                    QoSControl = qosControl, QoSState = qosState
                });
                bool saved = SaveEntries(entries);
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

        public static void HealFromCrash()
        {
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

            lock (sync) SaveEntries(keep);
            Settings.SaveStr(KThrottle, "");
            Settings.SaveStr(KBoost, "");
            Settings.SaveStr(KBoostNames, "");
            if (restored > 0) Logger.Log("检测到上次异常退出：按 PID/创建时间/映像名恢复 " + restored + " 个进程的已记录状态");
            if (keep.Count > 0) Logger.Log("仍有 " + keep.Count + " 个提优进程暂时无法恢复，身份快照已保留待下次重试");
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

#if AEGIS_SELFTEST
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
