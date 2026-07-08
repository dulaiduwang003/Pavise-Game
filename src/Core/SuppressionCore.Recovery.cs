// @author bdth 2074055628@qq.com
// 文件用途 持久化并恢复进程压制快照

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AegisApp
{
    internal sealed partial class SuppressionCore
    {
        private bool SaveJournalLocked()
        {
            if (string.IsNullOrEmpty(journalPath)) return true;
            try
            {
                var lines = new List<string>();
                lines.Add("AEGIS_SUPPRESSION_V1");
                foreach (var kv in map)
                {
                    Entry e = kv.Value;
                    if (e.OrigPri == uint.MaxValue) continue;
                    lines.Add(kv.Key + "|" + e.Creation + "|" + B64(e.Name) + "|" + e.OrigPri + "|"
                        + e.OrigAff + "|" + e.OrigIo + "|" + e.OrigPg + "|" + CpuSetsText(e.OrigCpuSets));
                }
                if (lines.Count == 1)
                {
                    if (File.Exists(journalPath)) File.Delete(journalPath);
                    return true;
                }
                string tmp = journalPath + ".tmp";
                File.WriteAllLines(tmp, lines.ToArray(), new UTF8Encoding(false));
                if (File.Exists(journalPath)) File.Replace(tmp, journalPath, null);
                else File.Move(tmp, journalPath);
                return true;
            }
            catch (Exception ex)
            {
                Logger.LogFailure("压制恢复日志写入失败，已阻止新的进程状态修改", ex);
                return false;
            }
        }

        public static int HealFromCrash(string statePath)
        {
            if (string.IsNullOrEmpty(statePath) || !File.Exists(statePath)) return 0;
            int restored = 0;
            var keep = new List<string>(); keep.Add("AEGIS_SUPPRESSION_V1");
            try
            {
                string[] lines = File.ReadAllLines(statePath, Encoding.UTF8);
                if (lines.Length == 0 || lines[0] != "AEGIS_SUPPRESSION_V1") return 0;
                for (int i = 1; i < lines.Length; i++)
                {
                    string[] a = lines[i].Split('|');
                    int pid, io, pg; long creation; uint pri; ulong aff;
                    if ((a.Length != 7 && a.Length != 8) || !int.TryParse(a[0], out pid) || !long.TryParse(a[1], out creation)
                        || !uint.TryParse(a[3], out pri) || !ulong.TryParse(a[4], out aff)
                        || !int.TryParse(a[5], out io) || !int.TryParse(a[6], out pg)) continue;
                    string name = Un64(a[2]);
                    uint[] cpuSets = a.Length == 8 ? ParseCpuSets(a[7]) : new uint[0];
                    if (cpuSets == null) continue;
                    IntPtr h = Native.OpenProcess(Native.PROCESS_SET_INFORMATION | Native.PROCESS_SET_LIMITED_INFORMATION
                        | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                    if (h == IntPtr.Zero)
                    {
                        IntPtr query = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                        if (query != IntPtr.Zero)
                        {
                            try
                            {
                                string current = Native.ImageName(query);
                                long currentCreation, cpu; ulong disk;
                                if (Native.QueryProcessSample(query, out currentCreation, out cpu, out disk)
                                    && currentCreation == creation && SameName(current, name)) keep.Add(lines[i]);
                            }
                            finally { Native.CloseHandle(query); }
                        }
                        continue;
                    }
                    try
                    {
                        string current = Native.ImageName(h);
                        long currentCreation, cpu; ulong disk;
                        bool identity = Native.QueryProcessSample(h, out currentCreation, out cpu, out disk)
                            && currentCreation == creation && SameName(current, name);
                        if (!identity) continue;
                        if (RestoreValues(h, pri, aff, io, pg, CpuTopology.AllMask, cpuSets)) restored++;
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
                else File.WriteAllLines(statePath, keep.ToArray(), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Logger.LogFailure("压制恢复日志读取失败", ex);
            }
            return restored;
        }

        private static string B64(string value)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? ""));
        }

        private static string Un64(string value)
        {
            try { return Encoding.UTF8.GetString(Convert.FromBase64String(value)); }
            catch { return ""; }
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
