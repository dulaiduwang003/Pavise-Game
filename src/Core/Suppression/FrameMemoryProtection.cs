using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal interface IFrameThreadMemory
    {
        bool UnderPressure(int pid, long birth);
        int ReadMemory(FrameThreadReceipt identity, out int priority);
        bool SetMemory(FrameThreadReceipt identity, int expected, int desired);
    }

    internal sealed partial class NativeFrameThreads
    {
        [StructLayout(LayoutKind.Sequential)] private struct MemoryPriorityInfo { internal uint Priority; }
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetThreadInformation(IntPtr thread, int kind, out MemoryPriorityInfo info, uint size);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetThreadInformation(IntPtr thread, int kind, ref MemoryPriorityInfo info, uint size);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessWorkingSetSizeEx(IntPtr process, out UIntPtr min, out UIntPtr max, out uint flags);

        public bool UnderPressure(int pid, long birth)
        {
            if (ProcessState(pid, birth) != 0) return false;
            double used, total, available;
            if (SystemAudit.TryMemory(out used, out total, out available) && used >= .85 && available <= 2) return true;
            IntPtr process = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (process == IntPtr.Zero) return false;
            try
            {
                long actual, cpu; ulong io; UIntPtr min, max; uint flags;
                return Native.QueryProcessSample(process, out actual, out cpu, out io) && actual == birth
                    && GetProcessWorkingSetSizeEx(process, out min, out max, out flags) && (flags & 4) != 0;
            }
            catch { return false; }
            finally { Native.CloseHandle(process); }
        }
        public int ReadMemory(FrameThreadReceipt id, out int priority)
        {
            priority = 0; int state = ProcessState(id.Pid, id.ProcessBirth); if (state != 0) return state;
            // 线程内存优先级查询需要 QUERY_INFORMATION。
            IntPtr thread = Native.OpenThread(0x40 | Native.THREAD_QUERY_LIMITED_INFORMATION, false, id.Tid);
            if (thread == IntPtr.Zero) return Marshal.GetLastWin32Error() == 87 ? 1 : -1;
            try
            {
                int ignored; state = ThreadState(thread, id, out ignored); if (state != 0) return state;
                MemoryPriorityInfo value;
                if (!GetThreadInformation(thread, 0, out value, 4) || value.Priority < 1 || value.Priority > 5) return -1;
                priority = (int)value.Priority; return 0;
            }
            catch { return -1; }
            finally { Native.CloseHandle(thread); }
        }
        public bool SetMemory(FrameThreadReceipt id, int expected, int desired)
        {
            if (expected < 1 || expected > 5 || desired < 1 || desired > 5 || ProcessState(id.Pid, id.ProcessBirth) != 0) return false;
            IntPtr thread = Native.OpenThread(0x40 | 0x20 | Native.THREAD_QUERY_LIMITED_INFORMATION, false, id.Tid);
            if (thread == IntPtr.Zero) return false;
            try
            {
                int ignored; MemoryPriorityInfo current;
                if (ThreadState(thread, id, out ignored) != 0 || !GetThreadInformation(thread, 0, out current, 4) || current.Priority != expected) return false;
                var wanted = new MemoryPriorityInfo { Priority = (uint)desired };
                return SetThreadInformation(thread, 0, ref wanted, 4) && GetThreadInformation(thread, 0, out current, 4) && current.Priority == desired;
            }
            catch { return false; }
            finally { Native.CloseHandle(thread); }
        }
    }

    internal static class FrameMemoryProtection
    {
        [ThreadStatic] private static string executionFailure;
        internal static string ExecutionFailure { get { return executionFailure; } }
        internal const string JournalKey = "FrameMemoryProtectionJournalV1";
        private static readonly object gate = new object();
        private static IFrameThreadMemory platform = new NativeFrameThreads();
        internal static bool HasResidue { get { string raw; return !Settings.TryLoadStr(JournalKey, out raw) || raw.Length > 0; } }

        private static bool Parse(string raw, out List<FrameThreadReceipt> rows)
        {
            rows = new List<FrameThreadReceipt>(); if (raw == "") return true;
            string[] parts = raw.Split(';'); if (parts.Length < 2 || parts.Length > 3 || parts[0] != "v1") return false;
            var tids = new HashSet<int>();
            for (int i = 1; i < parts.Length; i++)
            {
                string[] p = parts[i].Split('|'); var id = new FrameThreadReceipt();
                if (p.Length != 5 || !int.TryParse(p[0], out id.Pid) || !long.TryParse(p[1], out id.ProcessBirth)
                    || !int.TryParse(p[2], out id.Tid) || !long.TryParse(p[3], out id.ThreadBirth) || !int.TryParse(p[4], out id.Original)
                    || id.Pid <= 0 || id.ProcessBirth <= 0 || id.Tid <= 0 || id.ThreadBirth <= 0 || id.Original < 1 || id.Original >= 5 || !tids.Add(id.Tid)) return false;
                if (rows.Count > 0 && (id.Pid != rows[0].Pid || id.ProcessBirth != rows[0].ProcessBirth)) return false;
                rows.Add(id);
            }
            return true;
        }
        internal static bool Apply(List<FrameThreadReceipt> selected, IFrameThreadMemory adapter, Func<bool> alive)
        {
            lock (gate)
            {
                executionFailure = null;
                if (!RestoreLocked()) { executionFailure = "Previous memory protection restoration is incomplete"; return false; }
                if (!OvernightPerformancePolicy.Enabled) return true;
                if (adapter == null || selected == null || selected.Count == 0 || !alive()
                    || !adapter.UnderPressure(selected[0].Pid, selected[0].ProcessBirth)) return true;
                var owned = new List<FrameThreadReceipt>();
                foreach (var id in selected)
                {
                    int memory;
                    if (id.Original < -2 || id.Original >= 1 || adapter.ReadMemory(id, out memory) != 0 || memory < 1 || memory >= 5) continue;
                    owned.Add(new FrameThreadReceipt { Pid = id.Pid, ProcessBirth = id.ProcessBirth, Tid = id.Tid, ThreadBirth = id.ThreadBirth, Original = memory });
                }
                if (owned.Count == 0) return true;
                var parts = new List<string> { "v1" }; foreach (var id in owned) parts.Add(id.Encode());
                string raw = string.Join(";", parts.ToArray()), stored;
                if (!alive()) return false;
                if (!Settings.SaveStrDurable(JournalKey, raw) || !Settings.TryLoadStr(JournalKey, out stored) || raw != stored)
                { executionFailure = "Memory protection recovery record could not be saved or verified"; return false; }
                platform = adapter;
                foreach (var id in owned)
                {
                    if (!alive()) { RestoreLocked(); return false; }
                    if (!platform.SetMemory(id, id.Original, 5))
                    {
                        int current;
                        int state = platform.ReadMemory(id, out current);
                        if (state <= 0 && (state < 0 || current == id.Original || current == 5))
                            executionFailure = "Memory protection apply or readback failed";
                        RestoreLocked(); return false;
                    }
                }
                if (!alive()) { RestoreLocked(); return false; }
                return true;
            }
        }
        internal static bool Healthy()
        {
            lock (gate)
            {
                string raw; List<FrameThreadReceipt> rows;
                if (!Settings.TryLoadStr(JournalKey, out raw) || !Parse(raw, out rows)) return false;
                foreach (var id in rows) { int value; if (platform.ReadMemory(id, out value) != 0 || value != 5) return false; }
                return true;
            }
        }
        internal static bool Restore() { lock (gate) return RestoreLocked(); }
        private static bool RestoreLocked()
        {
            string raw; List<FrameThreadReceipt> rows;
            if (!Settings.TryLoadStr(JournalKey, out raw) || !Parse(raw, out rows)) return false;
            if (raw == "") return true;
            bool clean = true;
            foreach (var id in rows)
            {
                int value; int state = platform.ReadMemory(id, out value);
                if (state < 0) { clean = false; continue; }
                // 线程退出、身份复用或外部修改后放弃内存优先级所有权。
                if (state == 0 && value == 5 && !platform.SetMemory(id, 5, id.Original)) clean = false;
            }
            string current;
            return clean && Settings.TryLoadStr(JournalKey, out current) && current == raw
                && Settings.SaveStr(JournalKey, "") && Settings.TryLoadStr(JournalKey, out current) && current == "";
        }
#if PAVISE_SELFTEST
        internal static void SetPlatformForTest(IFrameThreadMemory adapter) { lock (gate) platform = adapter ?? new NativeFrameThreads(); }
#endif
    }
}
