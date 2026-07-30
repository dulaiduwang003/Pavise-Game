// @author bdth 2074055628@qq.com
// 文件用途 按用户配置压制指定反作弊进程

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace AegisApp
{
    internal class Tamer
    {
        private readonly object sync = new object();
        private readonly object engineSync = new object();
        private readonly Dictionary<string, bool> enabled = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, SuppressionLevel> levels = new Dictionary<string, SuppressionLevel>(StringComparer.OrdinalIgnoreCase);
        private readonly object eventSync = new object();
        private readonly List<ProcessChange> pendingChanges = new List<ProcessChange>();
        private readonly AutoResetEvent kick = new AutoResetEvent(true);
        private readonly SuppressionCore core;
        private readonly int selfPid;
        private readonly int selfSession;
        private volatile bool paused;
        private volatile bool stopping;
        private int processEventsAvailable;
        private long panicUntilUtcTicks;
        private int fullSweepRequested = 1;
        private int overflowSweepRequested;
        private Thread worker;
        private const int PollingFullSweepIntervalMs = 8000;
        private const int EventBackedFullSweepIntervalMs = 60000;
        private const int WorkerWakeIntervalMs = 8000;
        internal const int OverflowSweepIntervalMs = 8000;
        internal const int FailedSweepRetryMs = 1000;
        private const int MaxPendingChanges = 512;

        private sealed class AcquireRequest
        {
            public int Pid;
            public string Name;
            public string Group;
            public AcquireResult Result;
            public string FailureDetail;
        }

        public Tamer(SuppressionCore core)
        {
            this.core = core;
            using (Process self = Process.GetCurrentProcess())
            {
                selfPid = self.Id;
                try { selfSession = self.SessionId; } catch { selfSession = -1; }
            }
            foreach (AcGroup g in AntiCheatCatalog.Groups)
            {
                enabled[g.Key] = Settings.Load("Tame_" + g.Key, g.Default);
                levels[g.Key] = ParseLevel(Settings.LoadStr("TameLvl_" + g.Key, "eco"));
            }
        }

        public bool Paused
        {
            get { return paused; }
            set { paused = value; Poke(); }
        }

        public bool ProcessEventsAvailable
        {
            get
            {
                return Interlocked.CompareExchange(
                    ref processEventsAvailable, 0, 0) != 0;
            }
            set
            {
                int next = value ? 1 : 0;
                if (Interlocked.Exchange(
                        ref processEventsAvailable, next) == next)
                    return;

                Poke();
            }
        }

        public bool IsGroupEnabled(string key)
        {
            lock (sync) { bool v; return enabled.TryGetValue(key, out v) && v; }
        }

        public SuppressionLevel GroupLevel(string key)
        {
            if (string.IsNullOrEmpty(key)) return SuppressionLevel.Isolated;
            lock (sync)
            {
                SuppressionLevel v;
                return levels.TryGetValue(key, out v) ? v : SuppressionLevel.Isolated;
            }
        }

        public void SetGroupLevel(string key, SuppressionLevel level)
        {
            if (level < SuppressionLevel.Eco || level > SuppressionLevel.Isolated)
                level = SuppressionLevel.Isolated;
            lock (sync) levels[key] = level;
            Settings.SaveStr("TameLvl_" + key, LevelTag(level));
            Poke();
            Logger.Log("反作弊分组 " + key + " 压制档位 → " + LevelTag(level));
        }

        internal static SuppressionLevel ParseLevel(string tag)
        {
            if (tag == "eco") return SuppressionLevel.Eco;
            if (tag == "res") return SuppressionLevel.Restrained;
            return SuppressionLevel.Isolated;
        }

        internal static string LevelTag(SuppressionLevel level)
        {
            if (level == SuppressionLevel.Eco) return "eco";
            if (level == SuppressionLevel.Restrained) return "res";
            return "iso";
        }

        public bool PanicRestore()
        {
            Interlocked.Exchange(ref panicUntilUtcTicks, DateTime.UtcNow.AddSeconds(4).Ticks);

            Interlocked.Exchange(ref fullSweepRequested, 1);
            kick.Set();
            lock (engineSync) return ReleaseAll("紧急恢复");
        }

        public void SetGroupEnabled(string key, bool on)
        {
            lock (sync) enabled[key] = on;
            Settings.Save("Tame_" + key, on);
            Poke();
            Logger.Log("反作弊分组 " + key + " → " + (on ? "开启压制" : "关闭并恢复"));
        }

        public string GroupStatus(string key)
        {
            if (paused) return Lang.T("gs.moff");
            bool on;
            lock (sync) { if (!(enabled.TryGetValue(key, out on) && on)) return Lang.T("gs.noff"); }
            int t, f; core.AntiCheatGroupCounts(key, out t, out f);
            if (t == 0 && f == 0) return Lang.T("gs.noproc");
            string s = Lang.F("gs.thr", t);
            if (f > 0) s += Lang.F("gs.prot", f);
            return s;
        }

        public int GroupState(string key)
        {
            if (paused) return 0;
            bool on;
            lock (sync) { if (!(enabled.TryGetValue(key, out on) && on)) return 0; }
            int t, f; core.AntiCheatGroupCounts(key, out t, out f);
            return t > 0 ? 1 : 2;
        }

        public void Start()
        {
            worker = new Thread(Loop);
            worker.IsBackground = true;
            worker.Start();
        }

        public void Stop()
        {
            stopping = true;
            kick.Set();
            if (worker != null) worker.Join(6000);
        }

        public void Poke()
        {
            Interlocked.Exchange(ref fullSweepRequested, 1);
            kick.Set();
        }

        public void NotifyProcessChanges(ProcessChangeBatch batch)
        {
            if (batch == null) return;
            lock (eventSync)
            {
                if (batch.Overflowed
                    || pendingChanges.Count + batch.Changes.Length > MaxPendingChanges)
                {
                    pendingChanges.Clear();
                    Interlocked.Exchange(ref overflowSweepRequested, 1);
                }
                else
                    foreach (ProcessChange change in batch.Changes)
                        if (change != null && change.Pid > 0) pendingChanges.Add(change);
            }
            kick.Set();
        }

        private void Loop()
        {
            Logger.Log("反作弊压制引擎启动，可压制掩码 0x" + core.ThrottleMask.ToString("X"));
            long nextFullSweep = 0;
            long nextOverflowSweep = 0;
            while (!stopping)
            {
                try
                {
                    lock (engineSync)
                    {
                        long now = DateTime.UtcNow.Ticks;
                        bool panicHold = now < Interlocked.Read(ref panicUntilUtcTicks);
                        List<ProcessChange> changes = DrainProcessChanges();
                        bool fullRequested = false;
                        bool overflowRequested = false;
                        if (!paused && !panicHold)
                        {
                            fullRequested =
                                Interlocked.Exchange(
                                    ref fullSweepRequested, 0) != 0;
                            overflowRequested =
                                Interlocked.Exchange(
                                    ref overflowSweepRequested, 0) != 0;
                        }
                        if (paused || panicHold) ReleaseAll(paused ? "总开关关闭" : "紧急恢复冷却");
                        else if (fullRequested || now >= nextFullSweep
                            || overflowRequested && now >= nextOverflowSweep)
                        {
                            bool sweepSucceeded = false;
                            try { sweepSucceeded = Sweep(); }
                            finally
                            {
                                if (sweepSucceeded)
                                {
                                    nextFullSweep = DateTime.UtcNow.AddMilliseconds(
                                        FullSweepInterval(ProcessEventsAvailable)).Ticks;
                                    if (overflowRequested)
                                        nextOverflowSweep = DateTime.UtcNow.AddMilliseconds(
                                            OverflowSweepIntervalMs).Ticks;
                                }
                                else
                                {

                                    Interlocked.Exchange(ref fullSweepRequested, 1);
                                    if (overflowRequested)
                                        Interlocked.Exchange(
                                            ref overflowSweepRequested, 1);
                                    nextFullSweep = DateTime.UtcNow.AddMilliseconds(
                                        FailedSweepRetryMs).Ticks;
                                }
                            }
                        }
                        else
                        {
                            if (overflowRequested)
                                Interlocked.Exchange(ref overflowSweepRequested, 1);
                            HandleProcessChanges(changes);
                        }
                        core.RetryPending();
                    }
                }
                catch (Exception ex) { Logger.Log("反作弊压制异常: " + ex.Message); }
                long remainingTicks = nextFullSweep - DateTime.UtcNow.Ticks;
                long overflowRemaining = nextOverflowSweep
                    - DateTime.UtcNow.Ticks;
                if (Interlocked.CompareExchange(
                        ref overflowSweepRequested, 0, 0) != 0
                    && overflowRemaining > 0
                    && (remainingTicks <= 0
                        || overflowRemaining < remainingTicks))
                    remainingTicks = overflowRemaining;
                int wait = remainingTicks <= 0 ? WorkerWakeIntervalMs
                    : (int)Math.Min(WorkerWakeIntervalMs,
                        Math.Max(1, remainingTicks / TimeSpan.TicksPerMillisecond));
                kick.WaitOne(wait);
            }
            ReleaseAll("Aegis 退出");
        }

        internal static int FullSweepInterval(bool eventsAvailable)
        {
            return eventsAvailable
                ? EventBackedFullSweepIntervalMs
                : PollingFullSweepIntervalMs;
        }

        private List<ProcessChange> DrainProcessChanges()
        {
            lock (eventSync)
            {
                if (pendingChanges.Count == 0) return null;
                var result = new List<ProcessChange>(pendingChanges);
                pendingChanges.Clear();
                result.Sort(delegate(ProcessChange left, ProcessChange right)
                {
                    return left.Sequence.CompareTo(right.Sequence);
                });
                return result;
            }
        }

        private void HandleProcessChanges(List<ProcessChange> changes)
        {
            if (changes == null || changes.Count == 0) return;
            Dictionary<string, string> active = BuildActive();
            var acquireByPid = new Dictionary<int, AcquireRequest>();
            var releasePids = new HashSet<int>();
            var tracked = new HashSet<int>(
                core.PidsWith(SuppressReason.AntiCheat));
            foreach (ProcessChange change in changes)
            {
                if (change.Kind == ProcessChangeKind.Stopped)
                {

                    if (!tracked.Contains(change.Pid)
                        && !acquireByPid.ContainsKey(change.Pid))
                        continue;

                    if (!IsPidCurrentlyAlive(change.Pid))
                    {
                        acquireByPid.Remove(change.Pid);
                        releasePids.Add(change.Pid);
                        tracked.Remove(change.Pid);
                    }
                    continue;
                }
                string group;
                if (string.IsNullOrEmpty(change.Name) || !active.TryGetValue(change.Name, out group)) continue;
                AcquireRequest request;
                if (!TryBuildAcquireRequest(
                        change.Pid, change.Name, group, out request))
                    continue;
                releasePids.Remove(change.Pid);
                acquireByPid[change.Pid] = request;
            }
            ApplyBatch(
                new List<AcquireRequest>(acquireByPid.Values),
                new List<int>(releasePids));
        }

        private static bool IsPidCurrentlyAlive(int pid)
        {
            IntPtr handle = Native.OpenProcess(
                Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (handle == IntPtr.Zero)

                return !Native.LastOpenProcessFailureWasNoSuchProcess();

            try { return true; }
            finally { Native.CloseHandle(handle); }
        }

        private Dictionary<string, string> BuildActive()
        {
            var active = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            lock (sync)
            {
                foreach (AcGroup g in AntiCheatCatalog.Groups)
                {
                    bool on;
                    if (enabled.TryGetValue(g.Key, out on) && on)
                        foreach (string p in g.Procs) active[p] = g.Key;
                }
            }
            return active;
        }

        private bool Sweep()
        {
            var active = BuildActive();
            var seen = new HashSet<int>();
            var tracked = new HashSet<int>(
                core.PidsWith(SuppressReason.AntiCheat));
            var acquisitions = new List<AcquireRequest>();
            if (active.Count > 0)
            {
                Process[] all;
                try { all = Process.GetProcesses(); }
                catch
                {

                    return false;
                }
                foreach (Process p in all)
                {
                    int pid = -1;
                    try
                    {
                        pid = p.Id;
                        string grp, nm = p.ProcessName;
                        if (pid == selfPid) continue;
                        int session;
                        try { session = p.SessionId; }
                        catch
                        {
                            if (tracked.Contains(pid)) seen.Add(pid);
                            continue;
                        }

                        if (!active.TryGetValue(nm, out grp)) continue;
                        if (selfSession < 0 || session != selfSession && session != 0) continue;
                        seen.Add(pid);
                        acquisitions.Add(new AcquireRequest
                        {
                            Pid = pid,
                            Name = nm,
                            Group = grp
                        });
                    }
                    catch
                    {

                        if (pid > 0 && tracked.Contains(pid)) seen.Add(pid);
                    }
                    finally { p.Dispose(); }
                }
            }

            var releases = new List<int>();
            foreach (int pid in tracked)
                if (!seen.Contains(pid)) releases.Add(pid);
            ApplyBatch(acquisitions, releases);
            return true;
        }

        private bool TryBuildAcquireRequest(
            int pid, string expectedName, string group, out AcquireRequest request)
        {
            request = null;
            if (pid == selfPid) return false;
            try
            {
                using (Process process = Process.GetProcessById(pid))
                {
                    string name = process.ProcessName;
                    if (!string.Equals(
                            name, expectedName, StringComparison.OrdinalIgnoreCase))
                        return false;
                    int session;
                    try { session = process.SessionId; } catch { return false; }
                    if (selfSession < 0
                        || session != selfSession && session != 0)
                        return false;
                    request = new AcquireRequest
                    {
                        Pid = pid,
                        Name = name,
                        Group = group
                    };
                    return true;
                }
            }
            catch { return false; }
        }

        private void ApplyBatch(
            List<AcquireRequest> acquisitions, List<int> releases)
        {
            int acquireCount = acquisitions == null ? 0 : acquisitions.Count;
            int releaseCount = releases == null ? 0 : releases.Count;
            if (acquireCount == 0 && releaseCount == 0) return;

            SuppressionCore.BatchResult batchResult = null;
            core.BeginBatch();
            try
            {
                if (acquisitions != null)
                    foreach (AcquireRequest request in acquisitions)
                    {
                        try
                        {
                            request.Result = core.Acquire(
                                request.Pid, request.Name,
                                SuppressReason.AntiCheat, request.Group,
                                GroupLevel(request.Group));
                        }
                        catch { request.Result = AcquireResult.ApplyFailed; }
                    }
                if (releases != null)
                    foreach (int pid in releases)
                        try { core.Release(pid, SuppressReason.AntiCheat); }
                        catch { }
            }
            finally { batchResult = core.EndBatch(); }

            if (acquisitions == null) return;
            foreach (AcquireRequest request in acquisitions)
            {
                if ((request.Result == AcquireResult.NewlyThrottled
                        || request.Result == AcquireResult.AlreadyThrottled)
                    && (batchResult == null
                        || !batchResult.WasApplied(request.Pid)))
                {
                    request.Result = AcquireResult.ApplyFailed;
                    request.FailureDetail = batchResult != null ? batchResult.FailureOf(request.Pid) : "batch-missing";
                }
                LogAcquireResult(request);
            }
        }

        private static void LogAcquireResult(AcquireRequest request)
        {
            if (request.Result == AcquireResult.NewlyThrottled)
                Logger.Log("压制 " + request.Name + " (pid " + request.Pid + ")");
            else if (request.Result == AcquireResult.NewlyProtected)
                Logger.Log("打开 " + request.Name + " (pid " + request.Pid
                    + ") 失败（句柄被内核保护，压不动）");
            else if (request.Result == AcquireResult.ApplyFailed)
                Logger.Log("压制 " + request.Name + " (pid " + request.Pid
                    + ") 未完全生效"
                    + (string.IsNullOrEmpty(request.FailureDetail) ? "" : "，失败环节 [" + request.FailureDetail + "]")
                    + "，已保留快照，将按退避计划重试");
        }

        private bool ReleaseAll(string reason)
        {
            int n = core.ReleaseReason(SuppressReason.AntiCheat);
            if (n > 0) Logger.Log("反作弊压制解除（" + reason + "）：恢复 " + n + " 个进程");
            return !core.AnyWith(SuppressReason.AntiCheat);
        }
    }
}
