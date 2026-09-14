// @author bdth 2074055628@qq.com
// File purpose Saves and restores the queryable process state left by game boost
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

        // Record the original values in the registry before boosting, so the next launch after a crash can restore from them
        //   The system recycles pids, so identity is always the pid plus creation timestamp pair
        internal static bool MarkBoostProcess(int pid, long creation, string name,
            uint priority, ulong affinity, int io, int page, int gpu, uint[] cpuSets,
            int qosControl, int qosState, out OriginalBoostState recovered)
        {
            recovered = null;
            if (pid <= 0 || creation <= 0 || string.IsNullOrEmpty(name)) return false;
            lock (sync)
            {
                List<BoostEntry> entries = LoadEntries();
                // When the same process is boosted a second time, the copy already in the ledger is the real original
                //   Hand the old values back as-is; never overwrite them with the current, already-boosted values,
                //   otherwise restore would leave the process in the boosted state
                foreach (BoostEntry old in entries)
                    if (old.Pid == pid && old.Creation == creation)
                    {
                        // pid and creation time match but the name differs, so the ledger can no longer be trusted
                        //   Refuse to claim it; leave the record for the startup restore flow to handle
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
                // Old entries with the same pid but a different creation time are leftovers from pid recycling; drop them
                entries.RemoveAll(e => e.Pid == pid);
                entries.Add(new BoostEntry
                {
                    Pid = pid, Creation = creation, Name = name, Priority = priority,
                    Affinity = affinity, Io = io, Page = page, Gpu = gpu, CpuSets = cpuSets,
                    QoSControl = qosControl, QoSState = qosState
                });
                bool saved = SaveEntries(entries);
                // Ownership is only claimed once the ledger actually hits disk; a failed write counts as never boosted
                //   Better to restore one time too few than to have memory claim a record that is not on disk
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
                // A creation time mismatch means this pid has changed owner; do not release someone else's record
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

        // Boosts left behind by the last abnormal exit are reclaimed here; the three identity verdicts each take their own path
        //   Mismatch: discard; uncertain: keep and retry next time; match: actually restore
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
                    // Cannot open and it is not a vanished process, so most likely a permission issue; keep the record
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
                    // Identity unresolvable is not identity mismatch; keep it for the next launch, neither restore nor discard
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

            // keep holds the debts not restored this pass; write them back as-is and retry on the next launch
            lock (sync) SaveEntries(keep);
            Settings.SaveStr(KThrottle, "");
            Settings.SaveStr(KBoost, "");
            Settings.SaveStr(KBoostNames, "");
            if (restored > 0) Logger.Warn(Lang.T("log.crashguard.1") + restored + Lang.T("log.crashguard.2"));
            if (keep.Count > 0) Logger.Warn(Lang.T("log.crashguard.3") + keep.Count + Lang.T("log.crashguard.4"));
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
