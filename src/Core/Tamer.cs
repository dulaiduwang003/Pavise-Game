// @author bdth 2074055628@qq.com
// 文件用途 按用户配置压制指定反作弊进程
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace PaviseApp
{
    internal class Tamer
    {
        private readonly object sync = new object();
        private readonly object engineSync = new object();
        private readonly Dictionary<string, bool> enabled = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private readonly object eventSync = new object();
        private readonly List<ProcessChange> pendingChanges = new List<ProcessChange>();
        private readonly AutoResetEvent kick = new AutoResetEvent(true);
        private readonly SuppressionCore core;
        private readonly int selfPid;
        private readonly int selfSession;
        private volatile bool paused;
        // 全局强度档 三档共用同一批有效成分 递进的是介入深度
        private volatile int mode = (int)AntiCheatModes.Default;
        // 反作弊绑核是压制构成的一部分；写入被拒的进程实例本进程生命周期内不再重试
        private readonly Dictionary<int, long> pinRefused = new Dictionary<int, long>();
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
            paused = !Settings.Load("TameOn", false);
            using (Process self = Process.GetCurrentProcess())
            {
                selfPid = self.Id;
                try { selfSession = self.SessionId; } catch { selfSession = -1; }
            }
            foreach (AcGroup g in AntiCheatCatalog.Groups)
                enabled[g.Key] = Settings.Load("Tame_" + g.Key, g.Default);
            mode = (int)AntiCheatModes.Current;
            RetireNonSuppressibleGroups();
        }

        // 改判为仅保护的分组 老配置里可能还开着 静默失效比留着更糟 说明一次再清掉
        private void RetireNonSuppressibleGroups()
        {
            foreach (AcGroup g in AntiCheatCatalog.Groups)
            {
                if (g.Suppressible) continue;
                if (!Settings.Load("Tame_" + g.Key, false)) continue;
                Settings.Save("Tame_" + g.Key, false);
                lock (sync) enabled[g.Key] = false;
                Logger.Info(Lang.F("log.tamer.retired", g.Name));
            }
        }

        public AntiCheatMode Mode
        {
            get { return (AntiCheatMode)mode; }
            set
            {
                if ((AntiCheatMode)mode == value) return;
                bool release = AntiCheatModes.ShouldReleasePins((AntiCheatMode)mode, value);
                mode = (int)value;
                AntiCheatModes.Save(value);
                // 降档只是不再新增绑核 已经绑上的必须主动放回去
                //   SqueezeAff 不清零的话 DesiredAffinity 会一直返回落点 每轮对账又写回来
                //   反作弊会被钉在末尾核上直到进程退出 界面上却写着这一档不绑核
                if (release)
                {
                    int released = 0;
                    try { lock (engineSync) released = core.ClearSqueezes(SuppressReason.AntiCheat); }
                    catch { released = 0; }
                    lock (pinRefused) pinRefused.Clear();
                    if (released > 0) Logger.Info(Lang.F("log.tamer.unpin", released));
                }
                Interlocked.Exchange(ref fullSweepRequested, 1);
                Poke();
                Logger.Info(Lang.F("log.tamer.mode", AntiCheatModes.NameOf(value)));
            }
        }

        public bool Paused
        {
            get { return paused; }
            set { paused = value; Poke(); }
        }

        // 压制落地之后再下落点；6 到 8 核机器上使用末尾一个物理核
        //   写入被反作弊自身保护拒绝的 pid 记一次日志后不再重试 换了进程实例才会再来
        private void PinAntiCheatCores(List<AcquireRequest> acquisitions)
        {
            if (acquisitions == null || acquisitions.Count == 0) return;
            // 绑核最容易被反作弊自身保护拒绝 只有隔离档做
            if (!AntiCheatModes.PinsCores((AntiCheatMode)mode)) return;
            ulong mask;
            try { mask = CpuTopology.MultiGroup ? 0 : CpuTopology.BackgroundSqueezeMask(); }
            catch { mask = 0; }
            if (mask == 0) return;
            foreach (AcquireRequest request in acquisitions)
            {
                if (request.Result != AcquireResult.NewlyThrottled
                    && request.Result != AcquireResult.AlreadyThrottled) continue;
                long creation = core.CreationOf(request.Pid);
                if (creation <= 0) continue;
                lock (pinRefused)
                {
                    long refusedCreation;
                    if (pinRefused.TryGetValue(request.Pid, out refusedCreation) && refusedCreation == creation) continue;
                }
                bool changed;
                bool ok = core.SetSqueeze(request.Pid, creation, request.Name, mask,
                    SuppressReason.AntiCheat, out changed);
                if (!ok)
                {
                    lock (pinRefused) pinRefused[request.Pid] = creation;
                    Logger.Log(Lang.T("log.tamer.pin.1") + request.Name + " pid " + request.Pid + Lang.T("log.tamer.pin.3"));
                    continue;
                }
                if (changed)
                    Logger.Log(Lang.T("log.tamer.pin.1") + request.Name + " pid " + request.Pid
                        + Lang.T("log.tamer.pin.2") + CpuTopology.DescribeMask(mask));
            }
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

        public bool PanicRestore()
        {
            Interlocked.Exchange(ref panicUntilUtcTicks, DateTime.UtcNow.AddSeconds(4).Ticks);

            Interlocked.Exchange(ref fullSweepRequested, 1);
            kick.Set();
            lock (engineSync) return ReleaseAll(Lang.T("t.gamemode.42"));
        }

        public void SetGroupEnabled(string key, bool on)
        {
            lock (sync) enabled[key] = on;
            Settings.Save("Tame_" + key, on);
            Poke();
            Logger.Log(Lang.T("log.tamer.1") + key + " " + (on ? Lang.T("log.tamer.3") : Lang.T("log.tamer.4")));
        }

        public string GroupStatus(string key)
        {
            if (paused) return Lang.T("gs.moff");
            bool on;
            lock (sync) { if (!(enabled.TryGetValue(key, out on) && on)) return Lang.T("gs.noff"); }
            int t, f; core.AntiCheatGroupCountsCached(key, out t, out f);
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
            int t, f; core.AntiCheatGroupCountsCached(key, out t, out f);
            return t > 0 ? 1 : 2;
        }

        public void Start()
        {
            worker = new Thread(Loop);
            worker.IsBackground = true;
            worker.Start();
        }

        public bool Stop()
        {
            stopping = true;
            kick.Set();
            // 超时的工作线程可能还在改进程和它自己的恢复台账
            // 工作线程真正退出之前 重置不许删那份台账
            Thread current = worker;
            return current == null || current != Thread.CurrentThread && current.Join(6000);
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
            Logger.Log(Lang.T("log.tamer.5") + CpuTopology.DescribeMask(core.ThrottleMask));
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
                        if (paused || panicHold) ReleaseAll(paused ? Lang.T("gs.moff") : Lang.T("t.tamer.6"));
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
                catch (Exception ex) { Logger.Log(Lang.T("log.tamer.7") + ex.Message); }
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
            ReleaseAll(Lang.T("t.gamemode.53"));
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
                    // 仅保护的分组永不进压制目标 它们的进程名照旧进豁免名单
                    if (!g.Suppressible) continue;
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
                ProcessSnapshot all;
                try { all = ProcessSnapshotSource.Capture(); }
                catch { all = null; }
                if (all == null) return false;
                foreach (ProcEntry p in all.Entries)
                {
                    int pid = -1;
                    try
                    {
                        pid = p.Pid;
                        string grp, nm = p.Name;
                        if (pid == selfPid) continue;
                        int session = p.Session;

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
                            // 强度跟全局档位走 三档的有效成分相同 递进的是介入深度
                            //   见 SuppressionCore.Apply 的四个 Desired* 函数与 AntiCheatModes
                            request.Result = core.Acquire(
                                request.Pid, request.Name,
                                SuppressReason.AntiCheat, request.Group,
                                AntiCheatModes.LevelOf((AntiCheatMode)mode));
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
                    string detail = batchResult != null ? batchResult.FailureOf(request.Pid) : "batch-missing";
                    if (detail == SuppressionCore.SelfProtectedDetail)
                    {
                        request.Result = AcquireResult.NewlyProtected;
                        request.FailureDetail = detail;
                    }
                    else
                    {
                        request.Result = AcquireResult.ApplyFailed;
                        request.FailureDetail = detail;
                    }
                }
                LogAcquireResult(request);
            }
            PinAntiCheatCores(acquisitions);
        }

        private static void LogAcquireResult(AcquireRequest request)
        {
            if (request.Result == AcquireResult.NewlyThrottled)
                Logger.Log(Lang.T("log.tamer.8") + request.Name + " pid " + request.Pid);
            else if (request.Result == AcquireResult.NewlyProtected
                && request.FailureDetail != SuppressionCore.SelfProtectedDetail)
                Logger.Log(Lang.T("log.tamer.9") + request.Name + " pid " + request.Pid
                    + Lang.T("log.tamer.10"));
            else if (request.Result == AcquireResult.ApplyFailed)
                Logger.Log(Lang.T("log.tamer.8") + request.Name + " pid " + request.Pid
                    + Lang.T("log.tamer.11")
                    + (string.IsNullOrEmpty(request.FailureDetail) ? "" : Lang.T("log.tamer.12") + request.FailureDetail)
                    + Lang.T("log.tamer.13"));
        }

        private bool ReleaseAll(string reason)
        {
            int n = core.ReleaseReason(SuppressReason.AntiCheat);
            if (n > 0) Logger.Log(Lang.T("log.tamer.14") + reason + Lang.T("log.gamemodeboost.53") + n + Lang.T("log.program.3"));
            return !core.AnyWith(SuppressReason.AntiCheat);
        }
    }
}
