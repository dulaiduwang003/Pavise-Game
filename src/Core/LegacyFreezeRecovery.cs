// @author bdth 2074055628@qq.com
// 文件用途 仅兼容恢复旧版本留下的冻结日志；本版本不再创建任何冻结

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace AegisApp
{
    internal static class LegacyFreezeRecovery
    {
        public const string StateFileName = "Aegis.freeze.state";
        private const string Header = "AEGIS_FREEZE_V1";

        private sealed class Entry
        {
            public int Pid;
            public long Creation;
            public string Name;
        }

        private enum Identity { Match, Mismatch, Unknown }
        private enum ResumeResult { Restored, Gone, Protected }

        public static bool TryHandle(string[] args)
        {
            if (args == null || args.Length < 4
                || !string.Equals(args[0], "--freeze-watchdog", StringComparison.OrdinalIgnoreCase))
                return false;
            int owner;
            long creation;
            if (int.TryParse(args[1], out owner) && long.TryParse(args[2], out creation))
                WatchOwnerAndRestore(owner, creation, args[3]);
            return true;
        }

        public static void BeginHeal(string statePath)
        {
            int restored = RestoreJournal(statePath);
            if (restored > 0) Logger.Log("旧版深度冻结迁移：已恢复 " + restored + " 个后台进程");
            if (!File.Exists(statePath)) return;

            var retry = new Thread(() =>
            {
                int delaySeconds = 8;
                for (int attempt = 0; attempt < 6 && File.Exists(statePath); attempt++)
                {
                    Thread.Sleep(delaySeconds * 1000);
                    int count = RestoreJournal(statePath);
                    if (count > 0) Logger.Log("旧版深度冻结迁移重试：已恢复 " + count + " 个后台进程");
                    delaySeconds = Math.Min(120, delaySeconds * 2);
                }
                if (File.Exists(statePath))
                    Logger.Log("旧版深度冻结恢复仍有未确认项，已保留恢复日志：" + statePath);
            });
            retry.IsBackground = true;
            retry.Name = "Aegis.LegacyFreezeRecovery";
            retry.Start();
        }

        public static int RestoreJournal(string statePath)
        {
            List<Entry> entries = Load(statePath);
            if (entries == null)
            {
                if (File.Exists(statePath))
                    Logger.Log("旧版深度冻结恢复日志损坏，已保留原文件且未执行恢复：" + statePath);
                return 0;
            }
            if (entries.Count == 0)
            {
                TryDelete(statePath);
                return 0;
            }

            int restored = 0;
            var kept = new List<Entry>();
            foreach (Entry entry in entries)
            {
                ResumeResult result = Resume(entry);
                if (result == ResumeResult.Restored) restored++;
                if (result == ResumeResult.Protected) kept.Add(entry);
            }
            Save(statePath, kept);
            return restored;
        }

        public static void WatchOwnerAndRestore(int ownerPid, long ownerCreation, string statePath)
        {
            bool finished = false;
            for (int attempt = 0; attempt < 20 && !finished; attempt++)
            {
                try
                {
                    using (Process owner = Process.GetProcessById(ownerPid))
                    {
                        if (owner.StartTime.ToUniversalTime().ToFileTimeUtc() != ownerCreation) finished = true;
                        else { owner.WaitForExit(); finished = true; }
                    }
                }
                catch (ArgumentException) { finished = true; }
                catch { Thread.Sleep(100); }
            }
            if (!finished) return;
            for (int attempt = 0; attempt < 20 && File.Exists(statePath); attempt++)
            {
                RestoreJournal(statePath);
                if (File.Exists(statePath)) Thread.Sleep(250);
            }
        }

        private static ResumeResult Resume(Entry entry)
        {
            IntPtr handle = Native.OpenProcess(
                Native.PROCESS_SUSPEND_RESUME | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, entry.Pid);
            if (handle == IntPtr.Zero)
            {
                IntPtr query = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, entry.Pid);
                if (query == IntPtr.Zero)
                    return Native.LastOpenProcessFailureWasNoSuchProcess()
                        ? ResumeResult.Gone
                        : ResumeResult.Protected;
                try { return Identify(query, entry) == Identity.Mismatch ? ResumeResult.Gone : ResumeResult.Protected; }
                finally { Native.CloseHandle(query); }
            }
            try
            {
                Identity identity = Identify(handle, entry);
                if (identity == Identity.Mismatch) return ResumeResult.Gone;
                if (identity == Identity.Unknown) return ResumeResult.Protected;
                return Native.NtResumeProcess(handle) == 0 ? ResumeResult.Restored : ResumeResult.Protected;
            }
            finally { Native.CloseHandle(handle); }
        }

        private static Identity Identify(IntPtr handle, Entry entry)
        {
            string name = Native.ImageName(handle);
            long creation, cpu;
            ulong io;
            if (name == null || !Native.QueryProcessSample(handle, out creation, out cpu, out io))
                return Identity.Unknown;
            if (!string.Equals(name, entry.Name, StringComparison.OrdinalIgnoreCase)
                || creation != entry.Creation) return Identity.Mismatch;
            return Identity.Match;
        }

        private static List<Entry> Load(string path)
        {
            if (!File.Exists(path)) return new List<Entry>();
            try
            {
                string[] lines = File.ReadAllLines(path, Encoding.UTF8);
                if (lines.Length == 0 || lines[0] != Header) return null;
                var entries = new List<Entry>();
                var seen = new HashSet<int>();
                for (int i = 1; i < lines.Length; i++)
                {
                    string[] parts = lines[i].Split('|');
                    int pid;
                    long creation;
                    if (parts.Length != 4 || !int.TryParse(parts[0], out pid)
                        || !long.TryParse(parts[1], out creation) || pid <= 0 || creation <= 0
                        || !seen.Add(pid)) return null;
                    string name;
                    try { name = Encoding.UTF8.GetString(Convert.FromBase64String(parts[2])); }
                    catch { return null; }
                    if (string.IsNullOrEmpty(name)) return null;
                    entries.Add(new Entry { Pid = pid, Creation = creation, Name = name });
                }
                return entries;
            }
            catch { return null; }
        }

        private static void Save(string path, List<Entry> entries)
        {
            if (entries.Count == 0)
            {
                TryDelete(path);
                return;
            }
            var lines = new List<string> { Header };
            foreach (Entry entry in entries)
                lines.Add(entry.Pid + "|" + entry.Creation + "|"
                    + Convert.ToBase64String(Encoding.UTF8.GetBytes(entry.Name)) + "|");
            AtomicFile.WriteLines(path, lines.ToArray(), "旧版深度冻结恢复日志");
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { }
        }
    }
}
