// @author bdth 2074055628@qq.com
// 文件用途 冻结高干扰进程并保证异常退出后能够恢复

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace AegisApp
{
    internal sealed class FreezeGuard
    {
        internal const string StateFileName = "Aegis.freeze.state";
        private const string Header = "AEGIS_FREEZE_V1";

        private sealed class Entry
        {
            public int Pid;
            public long Creation;
            public string Name;
            public string Reason;
        }

        private enum ResumeResult { Restored, Gone, Protected }
        private enum IdentityResult { Match, Mismatch, Unknown }

        private readonly object sync = new object();
        private readonly Dictionary<int, Entry> entries = new Dictionary<int, Entry>();
        private readonly string statePath;
        private readonly bool watchdogEnabled;
        private readonly bool journalBlocked;
        private bool watchdogStarted;

        public FreezeGuard(string statePath) : this(statePath, true) { }

        internal FreezeGuard(string statePath, bool watchdogEnabled)
        {
            this.statePath = statePath;
            this.watchdogEnabled = watchdogEnabled;
            if (File.Exists(statePath))
            {
                List<Entry> old = LoadEntries(statePath);
                if (old == null)
                {
                    journalBlocked = true;
                    Logger.Log("极限冻结恢复日志已损坏：为避免覆盖恢复证据，本次运行禁止新增冻结，请保留 " + statePath + " 排查");
                }
                else foreach (Entry e in old) entries[e.Pid] = e;
            }
        }

        private int lastCount;

        // 同 SuppressionCore.CountThrottled：状态文字从 UI 线程读这个值，而 sync 可能正被
        // Freeze 持有——那里会在锁内写状态文件并拉起看门狗进程，耗时不可控。
        // UI 线程等在这里时握着 GameMode.sync，会连带拖住工作线程，所以拿不到锁就用上次的值。
        public int Count
        {
            get
            {
                if (!Monitor.TryEnter(sync, 15)) return Volatile.Read(ref lastCount);
                try
                {
                    int n = entries.Count;
                    Volatile.Write(ref lastCount, n);
                    return n;
                }
                finally { Monitor.Exit(sync); }
            }
        }

        public bool IsFrozen(int pid) { lock (sync) return entries.ContainsKey(pid); }

        public bool Freeze(int pid, string expectedName, string reason)
        {
            if (journalBlocked) return false;
            lock (sync) if (entries.ContainsKey(pid)) return true;
            IntPtr h = Native.OpenProcess(Native.PROCESS_SUSPEND_RESUME | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return false;
            Entry e = null;
            try
            {
                string name = Native.ImageName(h);
                long creation, cpu; ulong io;
                if (name == null || !string.Equals(name, expectedName, StringComparison.OrdinalIgnoreCase)
                    || !Native.QueryProcessSample(h, out creation, out cpu, out io)) return false;
                e = new Entry { Pid = pid, Creation = creation, Name = name, Reason = reason ?? "" };

                lock (sync)
                {
                    entries[pid] = e;
                    if (!SaveLocked() || !EnsureWatchdogLocked())
                    {
                        entries.Remove(pid);
                        SaveLocked();
                        return false;
                    }
                }

                if (Native.NtSuspendProcess(h) != 0)
                {
                    lock (sync) { entries.Remove(pid); SaveLocked(); }
                    return false;
                }

                if (!JournalContains(statePath, e.Pid, e.Creation, e.Name))
                {
                    Native.NtResumeProcess(h);
                    lock (sync) { entries.Remove(pid); SaveLocked(); }
                    return false;
                }
                return true;
            }
            finally { Native.CloseHandle(h); }
        }

        public bool Restore(int pid)
        {
            Entry e;
            lock (sync) { if (!entries.TryGetValue(pid, out e)) return false; }
            ResumeResult r = ResumeOne(e);
            if (r == ResumeResult.Protected) return false;
            lock (sync) { entries.Remove(pid); SaveLocked(); }
            return r == ResumeResult.Restored;
        }

        public int RestoreByName(string name)
        {
            var pids = new List<int>();
            lock (sync)
                foreach (var kv in entries)
                    if (string.Equals(kv.Value.Name, name, StringComparison.OrdinalIgnoreCase)) pids.Add(kv.Key);
            int n = 0;
            foreach (int pid in pids) if (Restore(pid)) n++;
            return n;
        }

        public int RestoreAll()
        {
            List<int> pids;
            lock (sync) pids = new List<int>(entries.Keys);
            int n = 0;
            foreach (int pid in pids) if (Restore(pid)) n++;
            return n;
        }

        public void Prune(HashSet<int> live)
        {
            List<int> missing = null;
            lock (sync)
                foreach (int pid in entries.Keys)
                    if (!live.Contains(pid)) { if (missing == null) missing = new List<int>(); missing.Add(pid); }
            if (missing != null) foreach (int pid in missing) Restore(pid);
        }

        private bool EnsureWatchdogLocked()
        {
            if (!watchdogEnabled || watchdogStarted) return true;
            try
            {
                var si = new ProcessStartInfo
                {
                    FileName = Application.ExecutablePath,
                    Arguments = WatchdogArguments(statePath),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                Process p = Process.Start(si);
                if (p == null) return false;
                p.Dispose();
                watchdogStarted = true;
                return true;
            }
            catch { return false; }
        }

        private static string WatchdogArguments(string path)
        {
            using (Process self = Process.GetCurrentProcess())
                return "--freeze-watchdog " + self.Id + " " + self.StartTime.ToUniversalTime().ToFileTimeUtc() + " " + Quote(path);
        }

        private bool SaveLocked()
        {
            return SaveEntries(statePath, new List<Entry>(entries.Values));
        }

        private static ResumeResult ResumeOne(Entry e)
        {
            IntPtr h = Native.OpenProcess(Native.PROCESS_SUSPEND_RESUME | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, e.Pid);
            if (h == IntPtr.Zero)
            {
                IntPtr hq = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, e.Pid);
                if (hq == IntPtr.Zero) return ResumeResult.Gone;
                try
                {
                    return Identify(hq, e) == IdentityResult.Mismatch ? ResumeResult.Gone : ResumeResult.Protected;
                }
                finally { Native.CloseHandle(hq); }
            }
            try
            {
                IdentityResult id = Identify(h, e);
                if (id == IdentityResult.Mismatch) return ResumeResult.Gone;
                if (id == IdentityResult.Unknown) return ResumeResult.Protected;
                return Native.NtResumeProcess(h) == 0 ? ResumeResult.Restored : ResumeResult.Protected;
            }
            finally { Native.CloseHandle(h); }
        }

        private static IdentityResult Identify(IntPtr h, Entry e)
        {
            string name = Native.ImageName(h);
            long creation, cpu; ulong io;
            if (name == null) return IdentityResult.Unknown;
            if (!string.Equals(name, e.Name, StringComparison.OrdinalIgnoreCase)) return IdentityResult.Mismatch;
            if (!Native.QueryProcessSample(h, out creation, out cpu, out io)) return IdentityResult.Unknown;
            return creation == e.Creation ? IdentityResult.Match : IdentityResult.Mismatch;
        }

        public static int HealFromCrash(string statePath)
        {
            int n = RestoreJournal(statePath);
            if (n > 0) Logger.Log("检测到上次异常退出：已恢复 " + n + " 个被冻结的后台进程");
            return n;
        }

        public static void WatchOwnerAndRestore(int ownerPid, long ownerCreation, string statePath)
        {
            bool ownerFinished = false;
            for (int attempt = 0; attempt < 20 && !ownerFinished; attempt++)
            {
                try
                {
                    using (Process owner = Process.GetProcessById(ownerPid))
                    {
                        if (owner.StartTime.ToUniversalTime().ToFileTimeUtc() != ownerCreation)
                            ownerFinished = true;
                        else
                        {
                            owner.WaitForExit();
                            ownerFinished = true;
                        }
                    }
                }
                catch (ArgumentException) { ownerFinished = true; }
                catch { Thread.Sleep(100); }
            }
            if (!ownerFinished) return;

            for (int i = 0; i < 20 && File.Exists(statePath); i++)
            {
                RestoreJournal(statePath);
                if (File.Exists(statePath)) Thread.Sleep(250);
            }
        }

        internal static int RestoreJournal(string statePath)
        {
            List<Entry> list = LoadEntries(statePath);
            if (list == null) return 0;
            if (list.Count == 0) { TryDelete(statePath); return 0; }
            int restored = 0;
            var kept = new List<Entry>();
            foreach (Entry e in list)
            {
                ResumeResult r = ResumeOne(e);
                if (r == ResumeResult.Protected) kept.Add(e);
                else if (r == ResumeResult.Restored) restored++;
            }
            SaveEntries(statePath, kept);
            return restored;
        }

        private static List<Entry> LoadEntries(string path)
        {
            var list = new List<Entry>();
            try
            {
                string[] lines = File.ReadAllLines(path, Encoding.UTF8);
                if (lines.Length == 0 || lines[0] != Header) return null;
                var seen = new HashSet<int>();
                for (int i = 1; i < lines.Length; i++)
                {
                    string[] p = lines[i].Split('|');
                    int pid; long creation;
                    if (p.Length != 4 || !int.TryParse(p[0], out pid) || !long.TryParse(p[1], out creation)) return null;
                    string name, reason;
                    try
                    {
                        name = Encoding.UTF8.GetString(Convert.FromBase64String(p[2]));
                        reason = Encoding.UTF8.GetString(Convert.FromBase64String(p[3]));
                    }
                    catch { return null; }
                    if (pid > 0 && creation > 0 && name.Length > 0)
                    {
                        if (!seen.Add(pid)) return null;
                        list.Add(new Entry { Pid = pid, Creation = creation, Name = name, Reason = reason });
                    }
                    else return null;
                }
            }
            catch { return null; }
            return list;
        }

        private static bool JournalContains(string path, int pid, long creation, string name)
        {
            List<Entry> list = LoadEntries(path);
            if (list == null) return false;
            foreach (Entry e in list)
                if (e.Pid == pid && e.Creation == creation
                    && string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static bool SaveEntries(string path, List<Entry> list)
        {
            try
            {
                if (list.Count == 0) { TryDelete(path); return true; }
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var lines = new List<string>();
                lines.Add(Header);
                foreach (Entry e in list)
                    lines.Add(e.Pid + "|" + e.Creation + "|" + B64(e.Name) + "|" + B64(e.Reason));
                string tmp = path + ".tmp." + Process.GetCurrentProcess().Id;
                File.WriteAllLines(tmp, lines.ToArray(), Encoding.UTF8);
                if (File.Exists(path))
                {
                    try { File.Replace(tmp, path, null); }
                    catch { File.Copy(tmp, path, true); TryDelete(tmp); }
                }
                else File.Move(tmp, path);
                return true;
            }
            catch { return false; }
        }

        private static string B64(string s)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(s ?? ""));
        }

        private static string Quote(string s) { return "\"" + (s ?? "").Replace("\"", "\"\"") + "\""; }

        private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    }
}
