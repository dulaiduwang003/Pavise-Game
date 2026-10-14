using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace PaviseApp
{
    // 存活进程无法从 Job 移出，关闭限额后保持中性直到退出。
    internal sealed class BackgroundCpuCap
    {
        internal const decimal AutomaticPercent = 0.01m;
        internal const decimal ServicePercent = 5m;
        internal const string ReceiptKey = "BackgroundCpuCapJobsV1";
        private sealed class Lease
        {
            internal int Pid;
            internal decimal Percent;
            internal long Creation;
            internal IntPtr Process, Job;
            internal string Name;
        }
        private readonly object gate = new object();
        private readonly Dictionary<int, Lease> leases = new Dictionary<int, Lease>();
        private readonly Dictionary<int, long> refused = new Dictionary<int, long>();
        private Process guard;
        private Timer heartbeat;
        private bool failed;
        private string prefix;
        private long lastSync;
#if PAVISE_SELFTEST
        internal int OwnedTargetInHostJobForTest;
        internal HashSet<int> OwnedTargetsInHostJobForTest;
#endif
        internal string Error { get; private set; }
        internal int ActiveCount { get { lock (gate) { int n = 0; foreach (Lease l in leases.Values) if (l.Percent > 0) n++; return n; } } }

        // 分配前保存收据，整个进程树被终止后仍可恢复。
        internal static bool Recover()
        {
            string receipt;
            if (!Settings.TryLoadStr(ReceiptKey, out receipt)) return false;
            if (string.IsNullOrEmpty(receipt)) return true;
            var pending = new List<string>();
            foreach (string name in receipt.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!name.StartsWith(CpuCapNative.Prefix, StringComparison.Ordinal) || name.Length > 200)
                { pending.Add(name); continue; }
                IntPtr job = CpuCapNative.OpenJobObject(0x0002 | 0x0004, false, name);
                if (job == IntPtr.Zero)
                {
                    if (System.Runtime.InteropServices.Marshal.GetLastWin32Error() != 2) pending.Add(name);
                    continue;
                }
                try { if (!CpuCapNative.SetRate(job, 0)) pending.Add(name); }
                finally { Native.CloseHandle(job); }
            }
            return Settings.SaveStr(ReceiptKey, string.Join("\n", pending.ToArray())) && pending.Count == 0;
        }

        private bool SaveReceipt()
        {
            var names = new List<string>();
            foreach (Lease lease in leases.Values) names.Add(lease.Name);
            return Settings.SaveStr(ReceiptKey, string.Join("\n", names.ToArray()));
        }

        private string ReadReply()
        {
            StreamReader reader = guard.StandardOutput;
            Task<string> read = Task.Factory.StartNew(() => reader.ReadLine());
            if (!read.Wait(3000)) throw new TimeoutException("cpu-cap-guardian");
            return read.Result;
        }

        private bool Command(string command)
        {
            try { guard.StandardInput.WriteLine(command); guard.StandardInput.Flush(); return ReadReply() == "OK"; }
            catch { return false; }
        }

        private bool EnsureGuard()
        {
            if (failed) return false;
            if (guard != null) return Command("PING") || Fail("guardian-lost");
            if (!Recover()) return Fail("recovery-pending");
            try
            {
                long creation, cpu; ulong io;
                if (!Native.QueryProcessSample(new IntPtr(-1), out creation, out cpu, out io)) return Fail("identity");
                int pid;
                using (Process self = Process.GetCurrentProcess()) pid = self.Id;
                prefix = CpuCapNative.Prefix + Guid.NewGuid().ToString("N") + ".";
                guard = Process.Start(new ProcessStartInfo(typeof(BackgroundCpuCap).Assembly.Location,
                    CpuCapGuard.Argument + " " + pid + " " + creation + " " + prefix)
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true });
                if (ReadReply() != "READY") return Fail("guardian-start");
                heartbeat = new Timer(delegate { Patrol(); }, null, 1000, 1000);
                return true;
            }
            catch (Exception e) { return Fail(e.GetType().Name); }
        }

        private void Patrol()
        {
            if (!Monitor.TryEnter(gate)) return;
            try
            {
                // 游戏扫描失败或停滞时停止限额。
                if (Native.GetTickCount64() - (ulong)lastSync > 12000 && !ReleaseAllLocked())
                { Fail("stale-release-pending"); return; }
                if (!ReleaseLocked(GameSessionDetector.ForegroundPid()))
                { Fail("foreground-release-pending"); return; }
                if (!failed && guard != null && !Command("PING")) Fail("guardian-lost");
            }
            catch { Fail("patrol"); }
            finally { Monitor.Exit(gate); }
        }

        private bool Fail(string reason)
        {
            bool report = !failed || Error != reason;
            Error = reason; failed = true;
            ReleaseAllLocked();
            if (guard != null) { try { guard.StandardInput.Close(); } catch { } }
            if (report) Logger.Warn(Lang.F("cpucap.failed", reason));
            return false;
        }

        internal bool Sync(Dictionary<int, long> wanted, decimal percent, HashSet<int> servicePids = null)
        {
            lock (gate)
            {
                lastSync = (long)Native.GetTickCount64();
                if (!CpuCapNative.ValidActivePercent(percent)) return Fail("invalid-rate");
                var dead = new List<int>();
                bool clean = true;
                foreach (Lease lease in leases.Values)
                {
                    long creation;
                    if (!CpuCapNative.IsAlive(lease.Process))
                    {
                        Native.CloseHandle(lease.Process); Native.CloseHandle(lease.Job); dead.Add(lease.Pid); continue;
                    }
                    if (!wanted.TryGetValue(lease.Pid, out creation) || creation != lease.Creation)
                        clean &= ReleaseLocked(lease.Pid);
                }
                foreach (int pid in dead) leases.Remove(pid);
                if (!clean) return Fail("release-pending");
                if (wanted.Count == 0) return true;
                if (!EnsureGuard()) return false;
                foreach (KeyValuePair<int, long> target in wanted)
                {
                    decimal targetPercent = servicePids != null && servicePids.Contains(target.Key) ? ServicePercent : percent;
                    if (target.Key == GameSessionDetector.ForegroundPid())
                    { if (!ReleaseLocked(target.Key)) return Fail("foreground-release"); continue; }
                    long refusedCreation;
                    if (refused.TryGetValue(target.Key, out refusedCreation) && refusedCreation == target.Value) continue;
                    Lease lease;
                    if (!leases.TryGetValue(target.Key, out lease))
                    {
                        if (leases.Count >= 256) continue;
                        IntPtr process = CpuCapNative.OpenTarget(target.Key, target.Value);
                        if (process == IntPtr.Zero) { Refuse(target, "process-access"); continue; }
                        bool inside;
                        bool jobKnown = CpuCapNative.IsProcessInJob(process, IntPtr.Zero, out inside);
#if PAVISE_SELFTEST
                        if (target.Key == OwnedTargetInHostJobForTest
                            || (OwnedTargetsInHostJobForTest != null && OwnedTargetsInHostJobForTest.Contains(target.Key))) inside = false;
#endif
                        if (!jobKnown || inside)
                        { Native.CloseHandle(process); Refuse(target, "existing-job"); continue; }
                        string name = prefix + target.Key + "." + target.Value;
                        IntPtr job = CpuCapNative.Create(name);
                        if (job == IntPtr.Zero) { Native.CloseHandle(process); Refuse(target, "create-job"); continue; }
                        lease = new Lease { Pid = target.Key, Creation = target.Value, Process = process, Job = job, Name = name };
                        leases.Add(target.Key, lease);
                        if (!SaveReceipt()) return Fail("receipt-write");
                        // 首次限制前由守护进程持有第二个句柄。
                        if (!Command("WATCH " + name + " " + target.Key + " " + target.Value)) return Fail("guardian-watch");
                        if (!CpuCapNative.AssignProcessToJobObject(job, process))
                        { Refuse(target, "assign-job"); continue; }
                    }
                    else if (lease.Creation != target.Value) continue;
                    if (lease.Percent == targetPercent) continue;
                    lease.Percent = targetPercent;
                    if (!CpuCapNative.SetRate(lease.Job, targetPercent))
                    {
                        Refuse(target, "rate-write");
                        if (!ReleaseLocked(target.Key)) return Fail("rollback-pending");
                    }
                    else Logger.Info(Lang.F("cpucap.applied", target.Key, targetPercent));
                }
                return true;
            }
        }

        private void Refuse(KeyValuePair<int, long> target, string reason)
        {
            refused[target.Key] = target.Value;
            Logger.Info(Lang.F("cpucap.skipped", target.Key, reason));
        }
        private bool ReleaseLocked(int pid)
        {
            Lease lease;
            if (!leases.TryGetValue(pid, out lease) || lease.Percent == 0) return true;
            if (CpuCapNative.IsAlive(lease.Process) && !CpuCapNative.SetRate(lease.Job, 0)) return false;
            lease.Percent = 0;
            return true;
        }
        internal bool Release(int pid) { lock (gate) return ReleaseLocked(pid); }
        private bool ReleaseAllLocked()
        { bool ok = true; foreach (int pid in leases.Keys) ok &= ReleaseLocked(pid); return ok; }
        internal bool Clear() { lock (gate) return ReleaseAllLocked(); }
        internal bool Stop()
        {
            lock (gate)
            {
                bool clean = ReleaseAllLocked();
                if (!clean) return false;
                if (heartbeat != null) { heartbeat.Dispose(); heartbeat = null; }
                if (guard != null)
                {
                    try { guard.StandardInput.Close(); } catch { }
                    try { if (!guard.WaitForExit(3000)) return false; } catch { return false; }
                    guard.Dispose(); guard = null;
                }
                foreach (Lease lease in leases.Values) { Native.CloseHandle(lease.Job); Native.CloseHandle(lease.Process); }
                leases.Clear(); refused.Clear();
                clean = prefix == null || SaveReceipt(); failed = !clean;
                if (clean) Error = null;
                return clean;
            }
        }
    }
}
