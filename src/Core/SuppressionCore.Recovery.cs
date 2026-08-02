// @author bdth 2074055628@qq.com
// 文件用途 持久化并恢复进程压制快照

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PaviseApp
{
    internal sealed partial class SuppressionCore
    {
        private enum JournalIdentity { Confirmed, Unknown, Mismatch }

        private bool SaveJournalLocked()
        {
            if (string.IsNullOrEmpty(journalPath))
            {
                MarkJournaledLocked();
                return true;
            }
            try
            {
                var lines = new List<string>();
                lines.Add("PAVISE_SUPPRESSION_V1");
                foreach (var kv in map)
                {
                    Entry e = kv.Value;
                    if (e.OrigPri == uint.MaxValue) continue;
                    lines.Add(kv.Key + "|" + e.Creation + "|" + B64(e.Name) + "|" + e.OrigPri + "|"
                        + e.OrigAff + "|" + e.OrigIo + "|" + e.OrigPg + "|" + CpuSetsText(e.OrigCpuSets)
                        + "|" + e.OrigQoSControl + "|" + e.OrigQoSState + "|" + e.OrigGpu);
                }
                if (lines.Count == 1)
                {
                    if (File.Exists(journalPath)) File.Delete(journalPath);
                    MarkJournaledLocked();
                    return true;
                }
                string tmp = journalPath + ".tmp";
                File.WriteAllLines(tmp, lines.ToArray(), new UTF8Encoding(false));
                if (File.Exists(journalPath)) File.Replace(tmp, journalPath, null);
                else File.Move(tmp, journalPath);
                MarkJournaledLocked();
                return true;
            }
            catch (Exception ex)
            {
                Logger.LogFailure("压制恢复日志写入失败，已阻止新的进程状态修改", ex);
                return false;
            }
        }

        private void MarkJournaledLocked()
        {
            foreach (Entry entry in map.Values)
                if (entry.OrigPri != uint.MaxValue) entry.Journaled = true;
        }

        private void LoadJournal()
        {
            if (string.IsNullOrEmpty(journalPath) || !File.Exists(journalPath)) return;
            try
            {
                string[] lines = File.ReadAllLines(journalPath, Encoding.UTF8);
                if (lines.Length == 0 || lines[0] != "PAVISE_SUPPRESSION_V1") return;
                for (int i = 1; i < lines.Length; i++)
                {
                    int pid;
                    Entry e = ParseJournalLine(lines[i], out pid);
                    if (e == null || pid <= 0 || e.OrigPri == uint.MaxValue) continue;
                    e.Journaled = true;
                    map[pid] = e;
                }
            }
            catch (Exception ex)
            {
                Logger.LogFailure("压制恢复日志读取失败", ex);
            }
        }

        private static Entry ParseJournalLine(string line, out int pid)
        {
            pid = 0;
            string[] a = line.Split('|');
            int io, pg; long creation; uint pri; ulong aff;
            if ((a.Length != 7 && a.Length != 8 && a.Length != 10 && a.Length != 11)
                || !int.TryParse(a[0], out pid) || !long.TryParse(a[1], out creation)
                || !uint.TryParse(a[3], out pri) || !ulong.TryParse(a[4], out aff)
                || !int.TryParse(a[5], out io) || !int.TryParse(a[6], out pg)) return null;
            uint[] cpuSets = a.Length >= 8 ? ParseCpuSets(a[7]) : new uint[0];
            if (cpuSets == null) return null;
            int qosControl = -1, qosState = -1;
            if (a.Length >= 10)
            {
                if (!int.TryParse(a[8], out qosControl)) qosControl = -1;
                if (!int.TryParse(a[9], out qosState)) qosState = -1;
            }
            int gpu = -1;
            if (a.Length >= 11 && !int.TryParse(a[10], out gpu)) gpu = -1;
            string name = Un64(a[2]);
            if (name == null) return null;
            return new Entry
            {
                Name = name,
                Creation = creation,
                OrigPri = pri,
                OrigAff = aff,
                OrigIo = io,
                OrigPg = pg,
                OrigCpuSets = cpuSets,
                OrigQoSControl = qosControl,
                OrigQoSState = qosState,
                OrigGpu = gpu
            };
        }

        private static JournalIdentity IdentifyJournalEntry(IntPtr h, Entry e)
        {
            if (e.Creation <= 0) return JournalIdentity.Unknown;
            string current = Native.ImageName(h);
            if (current != null && !SameName(current, e.Name)) return JournalIdentity.Mismatch;
            long currentCreation, cpu; ulong disk;
            bool sampled = Native.QueryProcessSample(h, out currentCreation, out cpu, out disk);
            if (e.Creation > 0)
            {
                if (!sampled) return JournalIdentity.Unknown;
                if (currentCreation != e.Creation) return JournalIdentity.Mismatch;
            }
            return current == null ? JournalIdentity.Unknown : JournalIdentity.Confirmed;
        }

        public static int HealFromCrash(string statePath)
        {
            if (string.IsNullOrEmpty(statePath) || !File.Exists(statePath)) return 0;
            int restored = 0;
            var keep = new List<string>(); keep.Add("PAVISE_SUPPRESSION_V1");
            try
            {
                string[] lines = File.ReadAllLines(statePath, Encoding.UTF8);
                if (lines.Length == 0 || lines[0] != "PAVISE_SUPPRESSION_V1") return 0;
                for (int i = 1; i < lines.Length; i++)
                {
                    int pid;
                    Entry entry = ParseJournalLine(lines[i], out pid);
                    if (entry == null)
                    {
                        keep.Add(lines[i]);
                        Logger.Log("压制恢复日志存在无法解析的记录，已原样保留（第 " + (i + 1) + " 行）");
                        continue;
                    }
                    IntPtr h = Native.OpenProcess(Native.PROCESS_SET_INFORMATION | Native.PROCESS_SET_LIMITED_INFORMATION
                        | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                    if (h == IntPtr.Zero)
                    {
                        IntPtr query = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                        if (query != IntPtr.Zero)
                        {
                            try
                            {
                                if (IdentifyJournalEntry(query, entry) != JournalIdentity.Mismatch) keep.Add(lines[i]);
                            }
                            finally { Native.CloseHandle(query); }
                        }
                        else if (!Native.LastOpenProcessFailureWasNoSuchProcess())
                        {
                            keep.Add(lines[i]);
                        }
                        continue;
                    }
                    try
                    {
                        JournalIdentity identity = IdentifyJournalEntry(h, entry);
                        if (identity == JournalIdentity.Mismatch) continue;
                        if (identity == JournalIdentity.Unknown) { keep.Add(lines[i]); continue; }
                        if (RestoreValues(h, entry.OrigPri, entry.OrigAff, entry.OrigIo, entry.OrigPg, CpuTopology.AllMask,
                            entry.OrigCpuSets, entry.OrigQoSControl, entry.OrigQoSState, entry.OrigGpu)) restored++;
                        else keep.Add(lines[i]);
                    }
                    catch (Exception ex)
                    {
                        keep.Add(lines[i]);
                        Logger.LogFailure("压制崩溃恢复失败 [pid " + pid + "]", ex);
                    }
                    finally { Native.CloseHandle(h); }
                }
                if (keep.Count == 1) File.Delete(statePath);
                else AtomicFile.WriteLines(statePath, keep.ToArray(), "压制恢复日志");
            }
            catch (Exception ex)
            {
                Logger.LogFailure("压制恢复日志读取失败", ex);
            }
            return restored;
        }

#if PAVISE_SELFTEST
        internal static string ProbeJournalLine(string raw)
        {
            int pid;
            Entry e = ParseJournalLine(raw, out pid);
            if (e == null) return "null";
            return pid + "|" + e.OrigQoSControl + "|" + e.OrigQoSState + "|" + e.OrigGpu;
        }
#endif

        private static string B64(string value)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? ""));
        }

        private static string Un64(string value)
        {
            try { return Encoding.UTF8.GetString(Convert.FromBase64String(value)); }
            catch { return null; }
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
            for (int i = 0; i < parts.Length; i++)
                if (!uint.TryParse(parts[i], out ids[i])) return null;
            return ids;
        }
    }
}
