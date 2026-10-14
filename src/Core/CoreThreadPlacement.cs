using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    // 线程选择只是软调度偏好，不能据此认定帧关键线程。
    internal sealed class CoreThreadPlacement
    {
        internal const string JournalKey = "AutoCoreThreadPlacementV1";
        internal sealed class Receipt
        {
            internal int Pid, Tid;
            internal long ProcessCreation, ThreadCreation;
            internal uint[] Original, Applied;
            internal ulong TargetMask;
            internal IntPtr Handle;
        }
        private readonly List<Receipt> receipts = new List<Receipt>();
        private bool journalOwned;
        internal bool Active { get { return receipts.Count > 0; } }
        internal int Candidates { get; private set; }
        internal ulong Mask { get; private set; }
        internal ulong CandidateMask
        { get { ulong mask = 0; foreach (Receipt r in receipts) if (r.Tid != 0) mask |= r.TargetMask; return mask; } }
        internal ulong WorkerMask
        { get { foreach (Receipt r in receipts) if (r.Tid == 0) return r.TargetMask; return 0; } }
        internal string State = "observing";
        internal bool Pending { get; private set; }
        private int journalGeneration = -1;
        private bool unownedPending;
        internal bool UnownedPending
        {
            get
            {
                if (journalOwned) return false;
                int generation = Settings.MutationGeneration;
                if (journalGeneration != generation) { unownedPending = HasResidue; journalGeneration = generation; }
                return unownedPending;
            }
        }
        internal static bool HasResidue
        { get { string raw; return !Settings.TryLoadStr(JournalKey, out raw) || raw.Length != 0; } }
        internal static bool Same(uint[] a, uint[] b)
        {
            return a != null && b != null && a.Length == b.Length && new HashSet<uint>(a).SetEquals(b);
        }
        private bool Save()
        {
            var rows = new List<string>();
            foreach (Receipt r in receipts)
                rows.Add(r.Pid + "|" + r.ProcessCreation + "|" + r.Tid + "|" + r.ThreadCreation
                    + "|" + string.Join(",", r.Original) + "|" + string.Join(",", r.Applied));
            return Settings.SaveStrDurable(JournalKey, string.Join(";", rows));
        }
        private bool Add(int pid, long creation, int tid, long threadCreation, ulong mask)
        {
            IntPtr h = tid == 0 ? Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION | Native.PROCESS_SET_INFORMATION | Native.PROCESS_SET_LIMITED_INFORMATION, false, pid)
                : Native.OpenThread(Native.THREAD_QUERY_LIMITED_INFORMATION | Native.THREAD_SET_LIMITED_INFORMATION | Native.SYNCHRONIZE, false, tid);
            if (h == IntPtr.Zero) { State = "access-denied"; return false; }
            bool owned = false;
            try
            {
                long born, cpu, exit, kernel, user; ulong disk;
                bool identity = tid == 0 ? Native.QueryProcessSample(h, out born, out cpu, out disk) && born == creation
                    : Native.QueryThreadOwnerPid(h) == pid && Native.GetThreadTimes(h, out born, out exit, out kernel, out user)
                        && born == threadCreation && exit == 0 && Native.ThreadAllowsMask(h, mask);
                if (!identity) { State = "identity-changed"; return false; }
                uint[] original = tid == 0 ? Native.QueryCpuSets(h) : Native.QueryThreadCpuSets(h);
                uint[] applied = CpuTopology.CpuSetIdsFor(mask);
                if (original == null || original.Length != 0 || applied == null || applied.Length != AutoCores.CountBits(mask))
                { State = "existing-placement"; return false; }
                var r = new Receipt { Pid = pid, ProcessCreation = creation, Tid = tid, ThreadCreation = threadCreation,
                    Handle = h, Original = original, Applied = applied, TargetMask = mask };
                receipts.Add(r);
                if (!Save()) { receipts.Remove(r); State = "journal-failed"; return false; }
                journalOwned = true;
                owned = true;
                bool written = tid == 0 ? Native.RestoreCpuSets(h, applied) : Native.WriteThreadCpuSets(h, applied);
                if (!written || !Same(tid == 0 ? Native.QueryCpuSets(h) : Native.QueryThreadCpuSets(h), applied))
                { State = "write-unverified"; return false; }
                return true;
            }
            finally { if (!owned) Native.CloseHandle(h); }
        }
        internal bool Apply(int pid, long creation, IList<CoreThreadCandidate> candidates, ulong gameMask, ulong[] physical, ulong favored)
        {
            if (Active || Pending || HasResidue) { State = "recovery-pending"; return false; }
            var cores = new List<ulong>();
            foreach (ulong c in physical) if ((c & gameMask) == c && c != 0) cores.Add(c);
            cores.Sort(delegate(ulong a, ulong b) { int f = ((b & favored) != 0 ? 1 : 0).CompareTo((a & favored) != 0 ? 1 : 0); return f != 0 ? f : a.CompareTo(b); });
            int count = Math.Min(2, Math.Min(candidates.Count, cores.Count - 2));
            if (count <= 0) { State = "capacity-limited"; return false; }
            ulong reserved = 0;
            for (int i = 0; i < count; i++) reserved |= cores[i];
            // 先安排候选线程，失败时避免限制整个游戏线程池。
            for (int i = 0; i < count; i++)
            {
                ulong logical = cores[i] & (~cores[i] + 1);
                if (!Add(pid, creation, candidates[i].Tid, candidates[i].Creation, logical))
                { string failure = State; Restore(); State = failure; return false; }
            }
            if (!Add(pid, creation, 0, 0, gameMask & ~reserved))
            { string failure = State; Restore(); State = failure; return false; }
            Candidates = count; Mask = gameMask; State = "applied"; return true;
        }
        internal bool Audit(ThreadTimes sample)
        {
            foreach (Receipt r in receipts)
            {
                if (r.Tid != 0)
                {
                    bool found = false;
                    if (CoreThreadTracker.ValidShape(sample))
                        for (int i = 0; i < sample.Ids.Length; i++)
                            if (sample.Ids[i] == r.Tid && sample.Created[i] == r.ThreadCreation) { found = true; break; }
                    if (!found) { State = "candidate-exited"; return false; }
                }
                if (!Same(r.Tid == 0 ? Native.QueryCpuSets(r.Handle) : Native.QueryThreadCpuSets(r.Handle), r.Applied))
                { State = "external-placement"; return false; }
            }
            return true;
        }
        private static bool RestoreReceipt(Receipt r)
        {
            if (r.Tid != 0 && Native.WaitForSingleObject(r.Handle, 0) == 0) return true;
            if (r.Tid == 0 && !Native.StillActive(r.Handle)) return true;
            uint[] current = r.Tid == 0 ? Native.QueryCpuSets(r.Handle) : Native.QueryThreadCpuSets(r.Handle);
            if (current == null) return false;
            if (!Same(current, r.Applied)) return true;
            bool ok = r.Tid == 0 ? Native.RestoreCpuSets(r.Handle, r.Original) : Native.WriteThreadCpuSets(r.Handle, r.Original);
            return ok && Same(r.Tid == 0 ? Native.QueryCpuSets(r.Handle) : Native.QueryThreadCpuSets(r.Handle), r.Original);
        }
        internal bool Restore()
        {
            if (!journalOwned && receipts.Count == 0) return true;
            for (int i = receipts.Count - 1; i >= 0; i--)
            {
                Receipt r = receipts[i];
                if (!RestoreReceipt(r)) continue;
                Native.CloseHandle(r.Handle); receipts.RemoveAt(i);
            }
            Pending = receipts.Count != 0 || !Save();
            if (!Pending) { Candidates = 0; Mask = 0; journalOwned = false; State = "restored"; }
            return !Pending;
        }
        internal static bool Recover()
        {
            string journal;
            if (!Settings.TryLoadStr(JournalKey, out journal)) return false;
            if (journal.Length == 0) return true;
            if (journal.Length > 65536) return false;
            var keeper = new List<string>();
            string[] rows = journal.Split(';'); Array.Reverse(rows);
            foreach (string row in rows)
            {
                IntPtr process = IntPtr.Zero, thread = IntPtr.Zero;
                try
                {
                    string[] f = row.Split('|'); if (f.Length != 6) { keeper.Add(row); continue; }
                    var r = new Receipt { Pid = int.Parse(f[0]), ProcessCreation = long.Parse(f[1]), Tid = int.Parse(f[2]), ThreadCreation = long.Parse(f[3]),
                        Original = ParseIds(f[4]), Applied = ParseIds(f[5]) };
                    if (r.Pid <= 0 || r.ProcessCreation <= 0 || r.Tid < 0 || (r.Tid > 0 && r.ThreadCreation <= 0) || r.Applied.Length == 0)
                    { keeper.Add(row); continue; }
                    process = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION | (r.Tid == 0 ? Native.PROCESS_SET_INFORMATION | Native.PROCESS_SET_LIMITED_INFORMATION : 0), false, r.Pid);
                    if (process == IntPtr.Zero) { if (Marshal.GetLastWin32Error() != 87) keeper.Add(row); continue; }
                    long born, cpu; ulong disk;
                    if (!Native.QueryProcessSample(process, out born, out cpu, out disk)) { keeper.Add(row); continue; }
                    if (born != r.ProcessCreation || !Native.StillActive(process)) continue;
                    if (r.Tid == 0) r.Handle = process;
                    else
                    {
                        thread = Native.OpenThread(Native.THREAD_QUERY_LIMITED_INFORMATION | Native.THREAD_SET_LIMITED_INFORMATION | Native.SYNCHRONIZE, false, r.Tid);
                        if (thread == IntPtr.Zero) { if (Marshal.GetLastWin32Error() != 87) keeper.Add(row); continue; }
                        long exit, kernel, user;
                        if (!Native.GetThreadTimes(thread, out born, out exit, out kernel, out user)) { keeper.Add(row); continue; }
                        if (exit != 0 || born != r.ThreadCreation || Native.QueryThreadOwnerPid(thread) != r.Pid) continue;
                        r.Handle = thread;
                    }
                    if (!RestoreReceipt(r)) keeper.Add(row);
                }
                catch { keeper.Add(row); }
                finally { if (thread != IntPtr.Zero) Native.CloseHandle(thread); if (process != IntPtr.Zero) Native.CloseHandle(process); }
            }
            // 恢复顺序固定为进程默认值在前、单线程设置在后。
            keeper.Reverse();
            return Settings.SaveStrDurable(JournalKey, string.Join(";", keeper)) && keeper.Count == 0;
        }
        private static uint[] ParseIds(string value)
        { return value.Length == 0 ? new uint[0] : Array.ConvertAll(value.Split(','), uint.Parse); }
    }
    internal sealed class CoreThreadCandidate
    {
        internal int Tid, Windows;
        internal long Creation;
        internal double Mean, Last;
    }
    internal sealed class CoreThreadTracker
    {
        private readonly Dictionary<long, CoreThreadCandidate> tracked = new Dictionary<long, CoreThreadCandidate>();
        internal static bool ValidShape(ThreadTimes sample)
        {
            return sample != null && sample.Ids != null && sample.Created != null && sample.Cpu != null
                && sample.Ids.Length <= 16384 && sample.Ids.Length == sample.Created.Length && sample.Ids.Length == sample.Cpu.Length;
        }
        internal List<CoreThreadCandidate> Observe(ThreadTimes before, ThreadTimes after)
        {
            var result = new List<CoreThreadCandidate>();
            if (!ValidShape(before) || !ValidShape(after) || !AutoCores.WindowUsable(before, after)) { tracked.Clear(); return result; }
            var old = new Dictionary<long, int>();
            for (int i = 0; i < before.Ids.Length; i++) old[before.Ids[i]] = i;
            var next = new Dictionary<long, CoreThreadCandidate>();
            for (int i = 0; i < after.Ids.Length; i++)
            {
                int j; long tid = after.Ids[i];
                if (tid <= 0 || tid > int.MaxValue || after.Created[i] <= 0 || !old.TryGetValue(tid, out j) || before.Created[j] != after.Created[i]) continue;
                double load = (after.Cpu[i] - before.Cpu[j]) / (double)(after.Ticks - before.Ticks);
                if (load < .35 || load > 1.10) continue;
                CoreThreadCandidate c;
                if (!tracked.TryGetValue(tid, out c) || c.Creation != after.Created[i] || Math.Abs(c.Last - load) > .25)
                    c = new CoreThreadCandidate { Tid = (int)tid, Creation = after.Created[i] };
                c.Mean = (c.Mean * Math.Min(7, c.Windows) + load) / (Math.Min(7, c.Windows) + 1);
                c.Windows++; c.Last = load; next[tid] = c;
                if (c.Windows >= 3) result.Add(c);
            }
            tracked.Clear(); foreach (var c in next) tracked[c.Key] = c.Value;
            result.Sort(delegate(CoreThreadCandidate a, CoreThreadCandidate b) { return b.Mean.CompareTo(a.Mean); });
            if (result.Count > 2) result.RemoveRange(2, result.Count - 2);
            return result;
        }
    }
}
