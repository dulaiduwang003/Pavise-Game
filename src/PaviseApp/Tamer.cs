using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace PaviseApp
{
    internal class Tamer
    {
        private sealed class AcquireRequest
        {
            public int Pid;

            public string Name;

            public string Group;

            public long Birth;


            public AcquireResult Result;

            public string FailureDetail;
        }

        internal const int OverflowSweepIntervalMs = 8000;

        internal const int FailedSweepRetryMs = 1000;

        private sealed class SweepRetryPhase
        {
            internal int Failures;
            internal long NotBefore;
            internal int CaptureFailures;
            internal long CaptureNotBefore;
        }
        private readonly Dictionary<string, SweepRetryPhase> sweepRetryPhases = new Dictionary<string, SweepRetryPhase>(StringComparer.Ordinal);
        private readonly Dictionary<int, long> sweepEventBirths = new Dictionary<int, long>();
        private readonly HashSet<string> sweepObservedEvents = new HashSet<string>(StringComparer.Ordinal);
        private long sweepEventPhase;
        private string sweepPhaseKey;
        private string sweepConfigurationKey;
        private SweepRetryPhase captureRetryPhase = new SweepRetryPhase();
        private ProcessSnapshot iterationSnapshot;
        private bool iterationCaptureAttempted;

        private ProcessSnapshot CaptureSweepSnapshot()
        {
            if (iterationCaptureAttempted) return iterationSnapshot;
            iterationCaptureAttempted = true;
            if (captureRetryPhase.CaptureFailures >= 3 || DateTime.UtcNow.Ticks < captureRetryPhase.CaptureNotBefore) return null;
            try { iterationSnapshot = ProcessSnapshotSource.Capture(); } catch { iterationSnapshot = null; }
            if (iterationSnapshot == null)
            {
                captureRetryPhase.CaptureFailures++;
                captureRetryPhase.CaptureNotBefore = DateTime.UtcNow.AddMilliseconds(FailedSweepRetryMs).Ticks;
                Logger.Log("后台治理快照：尝试=" + captureRetryPhase.CaptureFailures + "/3；观察与扫描共用失败采样预算");
            }
            else { captureRetryPhase.CaptureFailures = 0; captureRetryPhase.CaptureNotBefore = 0; }
            return iterationSnapshot;
        }

        private SweepRetryPhase SelectSweepRetryPhase(List<ProcessChange> changes, out bool changed)
        {
            Dictionary<string, string> active = BuildActive();
            if (changes != null)
            {
                foreach (ProcessChange change in changes)
                {
                    long birth;
                    string group;
                    AcquireRequest request;
                    if (change.Kind == ProcessChangeKind.Started)
                    {
                        if (string.IsNullOrEmpty(change.Name) || !active.TryGetValue(change.Name, out group)
                            || !TryBuildAcquireRequest(change.Pid, change.Name, group, out request) || request.Birth <= 0) continue;
                        birth = request.Birth;
                        sweepEventBirths[change.Pid] = birth;
                    }
                    else
                    {
                        if (!sweepEventBirths.TryGetValue(change.Pid, out birth))
                        {
                            string name = core.NameOf(change.Pid);
                            if (name == null || !active.ContainsKey(name)) continue;
                            birth = core.CreationOf(change.Pid);
                        }
                        if (birth <= 0 || IsPidCurrentlyAlive(change.Pid)) continue;
                        core.ObserveTargetExit(change.Pid, birth);
                    }
                    // 序号不能单独确认进程身份，重放通知不能补充预算。
                    string identity = change.Pid + "/" + birth + "/" + change.Kind;
                    if (sweepObservedEvents.Add(identity)) sweepEventPhase++;
                }
            }
            var names = new List<string>(active.Keys);
            names.Sort(StringComparer.OrdinalIgnoreCase);
            string configuration = (governanceContextKey ?? "") + "\n" + mode + "\n" + string.Join("\n", names.ToArray());
            string key = configuration + "\nevent:" + sweepEventPhase;
            SweepRetryPhase previous;
            bool recovering = sweepPhaseKey != null && sweepRetryPhases.TryGetValue(sweepPhaseKey, out previous) && previous.Failures > 0;
            changed = configuration != sweepConfigurationKey || (recovering && key != sweepPhaseKey);
            sweepConfigurationKey = configuration;
            sweepPhaseKey = key;
            SweepRetryPhase phase;
            if (!sweepRetryPhases.TryGetValue(key, out phase)) sweepRetryPhases[key] = phase = new SweepRetryPhase();
            return phase;
        }

        private readonly object sync = new object();

        private readonly object engineSync = new object();

        private readonly Dictionary<string, bool> enabled = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        private readonly object eventSync = new object();

        private readonly List<ProcessChange> pendingChanges = new List<ProcessChange>();

        private readonly AutoResetEvent kick = new AutoResetEvent(true);

        private readonly SuppressionCore core;

        private readonly int selfPid;

        private readonly int selfSession;

        private volatile int mode = 2;

        private volatile bool stopping;

        private readonly Dictionary<int, long> recordedProcesses = new Dictionary<int, long>();

        private int processEventsAvailable;

        private long panicUntilUtcTicks;

        private int fullSweepRequested = 1;

        private int overflowSweepRequested;

        private Thread worker;

        internal Func<AntiCheatAffinityGameContext> AffinityGameContext;
        private AntiCheatAffinityRuntime affinityRuntime;
        private bool affinityFastWake;
        private bool affinityShutdownOk = true;
        private bool groupMigrationReady;
        private List<KeyValuePair<string, int>> governanceServices;
        private long governanceServicesNextRead;
        private string governanceContextKey;

        internal string AffinityDetails(string group)
        {
            AntiCheatAffinityRuntime runtime = affinityRuntime;
            return runtime == null ? "" : runtime.Details(group);
        }

        private string WithAffinity(string status, string group)
        {
            AntiCheatAffinityRuntime runtime = affinityRuntime;
            string affinity = runtime == null ? "" : runtime.Summary(group);
            return affinity.Length == 0 ? status : status + " · " + affinity;
        }

        public AntiCheatMode Mode
        {
            get
            {
                return (AntiCheatMode)mode;
            }
            set
            {
                if (mode == (int)value)
                {
                    return;
                }
                bool shouldReleasePins = AntiCheatModes.ShouldReleasePins((AntiCheatMode)mode, value);
                mode = (int)value;
                AntiCheatModes.Save(value);
                if (shouldReleasePins)
                {
                    int setModeValue = 0;
                    try
                    {
                        lock (engineSync)
                        {
                            setModeValue = core.ClearSqueezes(SuppressReason.AntiCheat);
                        }
                    }
                    catch
                    {
                        setModeValue = 0;
                    }
                    if (setModeValue > 0)
                    {
                        Logger.Info(string.Format(Lang.T("log.tamer.unpin", 0), setModeValue));
                    }
                }
                Interlocked.Exchange(ref fullSweepRequested, 1);
                Poke();
                Logger.Info(string.Format(Lang.T("log.tamer.mode", 0), AntiCheatModes.NameOf(value)));
            }
        }

        public bool Paused
        {
            get
            {
                if (!groupMigrationReady) return true;
                lock (sync)
                {
                    foreach (AcGroup group in AntiCheatCatalog.Groups)
                    {
                        bool on;
                        if (group.Suppressible && enabled.TryGetValue(group.Key, out on) && on) return false;
                    }
                    return true;
                }
            }
        }

        public bool ProcessEventsAvailable
        {
            get
            {
                return Interlocked.CompareExchange(ref processEventsAvailable, 0, 0) != 0;
            }
            set
            {
                int setProcessEventsAvailableValue = (value ? 1 : 0);
                if (Interlocked.Exchange(ref processEventsAvailable, setProcessEventsAvailableValue) != setProcessEventsAvailableValue)
                {
                    Poke();
                }
            }
        }

        public Tamer(SuppressionCore core)
        {
            this.core = core;
            using (Process process = Process.GetCurrentProcess())
            {
                selfPid = process.Id;
                try
                {
                    selfSession = process.SessionId;
                }
                catch
                {
                    selfSession = -1;
                }
            }
            groupMigrationReady = MigrateGroupControls();
            foreach (AcGroup acGroup in AntiCheatCatalog.Groups)
            {
                enabled[acGroup.Key] = groupMigrationReady && Settings.Load("Tame_" + acGroup.Key, acGroup.Default);
            }
            mode = (int)AntiCheatModes.Current;
            RetireNonSuppressibleGroups();
        }

        private static bool MigrateGroupControls()
        {
            List<string> groups = new List<string>();
            foreach (AcGroup group in AntiCheatCatalog.Groups) if (group.Suppressible) groups.Add(group.Key);
            bool ready = AntiCheatGroupMigration.Ensure(AntiCheatGroupMigration.ReadStored, Settings.SaveDurable, groups);
            if (!ready) Logger.Warn("反作弊分组控制迁移未完成，后台治理继续暂停");
            return ready;
        }

        private void RetireNonSuppressibleGroups()
        {
            foreach (AcGroup acGroup in AntiCheatCatalog.Groups)
            {
                if (!acGroup.Suppressible && Settings.Load("Tame_" + acGroup.Key, false))
                {
                    Settings.Save("Tame_" + acGroup.Key, false);
                    lock (sync)
                    {
                        enabled[acGroup.Key] = false;
                    }
                    Logger.Info(string.Format(Lang.T(acGroup.RecordOnly ? "log.tamer.recordonly.retired" : "log.tamer.retired", 0), acGroup.Name));
                }
            }
        }

        public bool IsGroupEnabled(string key)
        {
            lock (sync)
            {
                bool value;
                return groupMigrationReady && enabled.TryGetValue(key, out value) && value;
            }
        }

        public bool PanicRestore()
        {
            Interlocked.Exchange(ref panicUntilUtcTicks, DateTime.UtcNow.AddSeconds(4.0).Ticks);
            Interlocked.Exchange(ref fullSweepRequested, 1);
            kick.Set();
            lock (engineSync)
            {
                return ReleaseAll(Lang.T("t.gamemode.42"));
            }
        }

        public void SetGroupEnabled(string key, bool on)
        {
            AcGroup acGroup = Array.Find(AntiCheatCatalog.Groups, (AcGroup g) => string.Equals(g.Key, key, StringComparison.OrdinalIgnoreCase));
            if (acGroup != null && acGroup.Suppressible)
            {
                lock (sync)
                {
                    if (!groupMigrationReady) groupMigrationReady = MigrateGroupControls();
                    if (!groupMigrationReady || !Settings.SaveDurable("Tame_" + key, on)) return;
                    enabled[key] = on;
                }
                Poke();
                Logger.Log(Lang.T("log.tamer.1", 0) + key + " " + (on ? Lang.T("log.tamer.3", 0) : Lang.T("log.tamer.4", 0)));
            }
        }

        public string GroupStatus(string key)
        {
            if (Paused)
            {
                return WithAffinity(Lang.T("gs.noff"), key);
            }
            lock (sync)
            {
                bool currentEnabled;
                if (!enabled.TryGetValue(key, out currentEnabled) || !currentEnabled)
                {
                    return WithAffinity(Lang.T("gs.noff"), key);
                }
            }
            int throttled;
            int protectedCnt;
            core.AntiCheatGroupCountsCached(key, out throttled, out protectedCnt);
            if (throttled == 0 && protectedCnt == 0)
            {
                return WithAffinity(Lang.T("gs.noproc"), key);
            }
            string gsThrText = Lang.F("gs.thr", throttled);
            if (protectedCnt > 0)
            {
                gsThrText += Lang.F("gs.prot", protectedCnt);
            }
            return WithAffinity(gsThrText, key);
        }

        public int GroupState(string key)
        {
            if (Paused)
            {
                return 0;
            }
            lock (sync)
            {
                bool currentEnabled;
                if (!enabled.TryGetValue(key, out currentEnabled) || !currentEnabled)
                {
                    return 0;
                }
            }
            int throttled;
            int protectedCnt;
            core.AntiCheatGroupCountsCached(key, out throttled, out protectedCnt);
            if (throttled <= 0)
            {
                return 2;
            }
            return 1;
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
            Thread thread = worker;
            if (thread != null)
            {
                if (thread != Thread.CurrentThread)
                {
                    return thread.Join(6000) && affinityShutdownOk;
                }
                return false;
            }
            return true;
        }

        public void Poke()
        {
            Interlocked.Exchange(ref fullSweepRequested, 1);
            kick.Set();
        }

        public void NotifyProcessChanges(ProcessChangeBatch batch)
        {
            if (batch == null)
            {
                return;
            }
            lock (eventSync)
            {
                if (batch.Overflowed || pendingChanges.Count + batch.Changes.Length > 512)
                {
                    pendingChanges.Clear();
                    Interlocked.Exchange(ref overflowSweepRequested, 1);
                }
                else
                {
                    foreach (ProcessChange processChange in batch.Changes)
                    {
                        if (processChange != null && processChange.Pid > 0)
                        {
                            pendingChanges.Add(processChange);
                        }
                    }
                }
            }
            kick.Set();
        }

        private void Loop()
        {
            Logger.Log(Lang.T("log.tamer.5", 0));
            affinityRuntime = new AntiCheatAffinityRuntime(core.PrepareAntiCheatAffinity, CheckAffinityAdmission);
            long nextFullSweepTicks = 0L;
            long nextOverflowSweepTicks = 0L;
            long nextMaintenanceTicks = 0L;
            long nextCoreRetry = 0L;
            long failedSweepRetryNotBefore = 0L;
            while (!stopping)
            {
                try
                {
                    lock (engineSync)
                    {
                        long ticks = DateTime.UtcNow.Ticks;
                        bool panicCooldown = ticks < Interlocked.Read(ref panicUntilUtcTicks);
                        List<ProcessChange> changes = DrainProcessChanges();
                        bool fullSweep = false;
                        bool overflowSweep = false;
                        bool phaseChanged = false;
                        RefreshGovernanceSession();
                        SweepRetryPhase retryPhase = SelectSweepRetryPhase(changes, out phaseChanged);
                        captureRetryPhase = retryPhase;
                        iterationSnapshot = null;
                        iterationCaptureAttempted = false;
                        if (ticks >= nextMaintenanceTicks)
                        {
                            nextMaintenanceTicks = ticks + TimeSpan.FromSeconds(30).Ticks;
                            try { ObserveRecordOnlyProcesses(CaptureSweepSnapshot()); } catch { }
                        }
                        if (!Paused && !panicCooldown)
                        {
                            failedSweepRetryNotBefore = retryPhase.NotBefore;
                            if (phaseChanged) nextFullSweepTicks = retryPhase.NotBefore;
                            fullSweep = Interlocked.Exchange(ref fullSweepRequested, 0) != 0;
                            overflowSweep = Interlocked.Exchange(ref overflowSweepRequested, 0) != 0;
                        }
                        if (Paused || panicCooldown)
                        {
                            ReleaseAll(Paused ? Lang.T("gs.noff") : Lang.T("t.tamer.6"));
                        }
                        // 只有执行失败才消耗阶段预算，Poke 不能重置预算或截止时间。
                        else if (retryPhase.Failures < 3 && ticks >= failedSweepRetryNotBefore
                            && (phaseChanged || (fullSweep && retryPhase.Failures == 0) || ticks >= nextFullSweepTicks || (overflowSweep && ticks >= nextOverflowSweepTicks)))
                        {
                            bool sweepSucceeded = false;
                            try
                            {
                                sweepSucceeded = Sweep();
                            }
                            finally
                            {
                                if (sweepSucceeded)
                                {
                                    failedSweepRetryNotBefore = 0L;
                                    retryPhase.Failures = 0;
                                    retryPhase.NotBefore = 0L;
                                    nextFullSweepTicks = DateTime.UtcNow.AddMilliseconds(FullSweepInterval(ProcessEventsAvailable)).Ticks;
                                    if (overflowSweep)
                                    {
                                        nextOverflowSweepTicks = DateTime.UtcNow.AddMilliseconds(8000.0).Ticks;
                                    }
                                }
                                else
                                {
                                    if (overflowSweep)
                                    {
                                        Interlocked.Exchange(ref overflowSweepRequested, 1);
                                    }
                                    retryPhase.Failures++;
                                    failedSweepRetryNotBefore = retryPhase.Failures >= 3 ? DateTime.MaxValue.Ticks : DateTime.UtcNow.AddMilliseconds(FailedSweepRetryMs).Ticks;
                                    retryPhase.NotBefore = failedSweepRetryNotBefore;
                                    Logger.Log("后台治理扫描恢复：阶段=" + sweepPhaseKey + " 尝试=" + retryPhase.Failures + "/3; "
                                        + (retryPhase.Failures >= 3 ? "已停止主动重试，进程事件继续运行" : "backoff"));
                                    nextFullSweepTicks = failedSweepRetryNotBefore;
                                    // 快照失败后，将已取出的事件重新入队，交给有界队列按原预算重试。
                                    if (changes != null && changes.Count > 0)
                                        NotifyProcessChanges(new ProcessChangeBatch(changes.ToArray(), false));
                                }
                            }
                        }
                        else
                        {
                            if (overflowSweep)
                            {
                                Interlocked.Exchange(ref overflowSweepRequested, 1);
                            }
                            try
                            {
                                HandleProcessChanges(changes);
                            }
                            catch
                            {
                                // 使用完整快照校正部分批次，不立即自唤醒重放。
                                Interlocked.Exchange(ref overflowSweepRequested, 1);
                                if (ticks >= failedSweepRetryNotBefore)
                                    failedSweepRetryNotBefore = DateTime.UtcNow.AddMilliseconds(FailedSweepRetryMs).Ticks;
                                retryPhase.NotBefore = failedSweepRetryNotBefore;
                                nextFullSweepTicks = failedSweepRetryNotBefore;
                                throw;
                            }
                        }
                        if (ticks >= nextCoreRetry)
                        {
                            core.RetryPending();
                            nextCoreRetry = ticks + TimeSpan.FromSeconds(8).Ticks;
                        }
                        SyncAffinity(Paused || panicCooldown);
                    }
                    Logger.RepeatedRecovered("Tamer");
                }
                catch (Exception ex)
                {
                    Logger.RepeatedError("Tamer", Lang.T("log.tamer.7", 0) + ex.GetType().Name + ": " + ex.Message, -1L);
                }
                long remainingNumericValue = nextFullSweepTicks - DateTime.UtcNow.Ticks;
                long remainingMax = Math.Max(nextOverflowSweepTicks, failedSweepRetryNotBefore) - DateTime.UtcNow.Ticks;
                if (Interlocked.CompareExchange(ref overflowSweepRequested, 0, 0) != 0 && remainingMax > 0 && (remainingNumericValue <= 0 || remainingMax < remainingNumericValue))
                {
                    remainingNumericValue = remainingMax;
                }
                int millisecondsTimeout = (int)((remainingNumericValue <= 0) ? 8000 : Math.Min(8000L, Math.Max(1L, remainingNumericValue / 10000)));
                if (affinityFastWake) millisecondsTimeout = Math.Min(millisecondsTimeout, 1000);
                kick.WaitOne(millisecondsTimeout);
            }
            affinityShutdownOk = ReleaseAll(Lang.T("t.gamemode.53"));
            if (affinityRuntime != null) affinityShutdownOk = affinityRuntime.Shutdown() && affinityShutdownOk;
        }

        private void SyncAffinity(bool suspended)
        {
            AntiCheatAffinityGameContext context = suspended ? null : GovernanceContext();
            List<KeyValuePair<string, int>> services = context == null ? null : GovernanceServices();
            Dictionary<string, string> activeGroups = BuildActive();
            List<AntiCheatAffinityTarget> targets = new List<AntiCheatAffinityTarget>();
            foreach (int pid in core.PidsWith(SuppressReason.AntiCheat))
            {
                string name = core.NameOf(pid), group;
                long birth = core.CreationOf(pid);
                AcquireRequest retained = new AcquireRequest { Pid = pid, Name = name, Birth = birth };
                if (suspended || name == null || !activeGroups.TryGetValue(name, out group)
                    || CheckGovernanceAdmission(retained, context, services) != null)
                {
                    core.Release(pid, SuppressReason.AntiCheat);
                    continue;
                }
                if (!suspended && birth > 0 && name != null)
                    core.Reconcile(pid, name, SuppressReason.AntiCheat);
                if (birth > 0 && core.IsThrottled(pid) && name != null && activeGroups.TryGetValue(name, out group))
                    targets.Add(new AntiCheatAffinityTarget { Pid = pid, Birth = birth, Name = name, Group = group });
            }
            if (targets.Count == 0) context = null;
            affinityRuntime.Tick(context, targets, AntiCheatModes.CoreLimitOf((AntiCheatMode)mode));
            affinityFastWake = context != null || affinityRuntime.PendingRecovery || (!suspended && targets.Count > 0);
        }

        private AntiCheatAffinityGameContext GovernanceContext()
        {
            try { return AffinityGameContext == null ? null : AffinityGameContext(); }
            catch { return null; }
        }

        private void RefreshGovernanceSession()
        {
            AntiCheatAffinityGameContext context = GovernanceContext();
            string key = context == null ? null : context.Key;
            if (key == governanceContextKey) return;
            governanceContextKey = key;
            governanceServicesNextRead = 0L;
            Interlocked.Exchange(ref fullSweepRequested, 1);
        }

        private List<KeyValuePair<string, int>> GovernanceServices()
        {
            long now = DateTime.UtcNow.Ticks;
            if (now >= governanceServicesNextRead)
            {
                try { governanceServices = SvcQuery.RunningServices(); }
                catch { governanceServices = null; }
                governanceServicesNextRead = now + TimeSpan.FromSeconds(8).Ticks;
            }
            return governanceServices;
        }

        private static bool ReadGovernanceIdentity(int pid, out string name, out long birth, out int session)
        {
            name = null; birth = 0; session = -1;
            try
            {
                using (Process process = Process.GetProcessById(pid))
                {
                    name = process.ProcessName;
                    birth = process.StartTime.ToUniversalTime().ToFileTimeUtc();
                    session = process.SessionId;
                    return birth > 0 && !process.HasExited;
                }
            }
            catch { return false; }
        }

        private static string CheckGovernanceAdmission(AcquireRequest request, AntiCheatAffinityGameContext context,
            IEnumerable<KeyValuePair<string, int>> services)
        {
            if (context == null || context.GameAncestors == null) return "game-ancestry-unavailable";
            if (request == null || request.Birth <= 0) return "process-identity-unavailable";
            string name; long birth; int session;
            if (!ReadGovernanceIdentity(request.Pid, out name, out birth, out session)
                || birth != request.Birth || !string.Equals(name, request.Name, StringComparison.OrdinalIgnoreCase))
                return "process-identity-changed";
            string reason;
            return AntiCheatAffinityEligibility.Eligible(name, request.Pid, session, services, context.GameAncestors, out reason)
                ? null : reason;
        }

        private string CheckAffinityAdmission(AntiCheatAffinityTarget target, AntiCheatAffinityGameContext context)
        {
            try
            {
                using (Process process = Process.GetProcessById(target.Pid))
                {
                    if (process.StartTime.ToUniversalTime().ToFileTimeUtc() != target.Birth) return "process-identity-changed";
                    string reason;
                    return AntiCheatAffinityEligibility.Eligible(target.Name, target.Pid, process.SessionId,
                        SvcQuery.RunningServices(), context.GameAncestors, out reason) ? null : reason;
                }
            }
            catch { return "service-identity-unavailable"; }
        }

        internal static int FullSweepInterval(bool eventsAvailable)
        {
            if (!eventsAvailable)
            {
                return 8000;
            }
            return 60000;
        }

        private List<ProcessChange> DrainProcessChanges()
        {
            lock (eventSync)
            {
                if (pendingChanges.Count == 0)
                {
                    return null;
                }
                List<ProcessChange> pendingChangesSnapshot = new List<ProcessChange>(pendingChanges);
                pendingChanges.Clear();
                pendingChangesSnapshot.Sort((ProcessChange left, ProcessChange right) => left.Sequence.CompareTo(right.Sequence));
                return pendingChangesSnapshot;
            }
        }

        private void HandleProcessChanges(List<ProcessChange> changes)
        {
            if (changes == null || changes.Count == 0)
            {
                return;
            }
            Dictionary<string, string> textsByName = BuildActive();
            Dictionary<int, AcquireRequest> requestsByPid = new Dictionary<int, AcquireRequest>();
            HashSet<int> pids = new HashSet<int>();
            HashSet<int> pidsWithSnapshot = new HashSet<int>(core.PidsWith(SuppressReason.AntiCheat));
            foreach (ProcessChange change in changes)
            {
                string text;
                AcquireRequest request;
                if (change.Kind == ProcessChangeKind.Stopped)
                {
                    if ((pidsWithSnapshot.Contains(change.Pid) || requestsByPid.ContainsKey(change.Pid)) && !IsPidCurrentlyAlive(change.Pid))
                    {
                        requestsByPid.Remove(change.Pid);
                        pids.Add(change.Pid);
                        pidsWithSnapshot.Remove(change.Pid);
                    }
                }
                else if (!string.IsNullOrEmpty(change.Name) && textsByName.TryGetValue(change.Name, out text) && TryBuildAcquireRequest(change.Pid, change.Name, text, out request))
                {
                    pids.Remove(change.Pid);
                    requestsByPid[change.Pid] = request;
                }
            }
            ApplyBatch(new List<AcquireRequest>(requestsByPid.Values), new List<int>(pids));
        }

        private static bool IsPidCurrentlyAlive(int pid)
        {
            IntPtr processHandle = Native.OpenProcess(4096, false, pid);
            if (processHandle == IntPtr.Zero)
            {
                return !Native.LastOpenProcessFailureWasNoSuchProcess();
            }
            try
            {
                return true;
            }
            finally
            {
                Native.CloseHandle(processHandle);
            }
        }

        private Dictionary<string, string> BuildActive()
        {
            Dictionary<string, string> textsByKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            lock (sync)
            {
                if (!groupMigrationReady) return textsByKey;
                foreach (AcGroup acGroup in AntiCheatCatalog.Groups)
                {
                    bool currentEnabled;
                    if (acGroup.Suppressible && enabled.TryGetValue(acGroup.Key, out currentEnabled) && currentEnabled)
                    {
                        foreach (string key in acGroup.Procs)
                        {
                            textsByKey[key] = acGroup.Key;
                        }
                    }
                }
                return textsByKey;
            }
        }

        internal int ObserveRecordOnlyProcesses(ProcessSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return 0;
            }
            HashSet<int> pids = new HashSet<int>();
            int processedCount = 0;
            foreach (ProcEntry procEntry in snapshot.Entries)
            {
                if (procEntry.Pid <= 0 || procEntry.Pid == selfPid || procEntry.Creation <= 0 || selfSession < 0 || (procEntry.Session != selfSession && procEntry.Session != 0))
                {
                    continue;
                }
                AcGroup acGroup = AntiCheatCatalog.RecordOnlyGroupOf(procEntry.Name);
                if (acGroup != null)
                {
                    pids.Add(procEntry.Pid);
                    long recordedProcesse;
                    if (!recordedProcesses.TryGetValue(procEntry.Pid, out recordedProcesse) || recordedProcesse != procEntry.Creation)
                    {
                        recordedProcesses[procEntry.Pid] = procEntry.Creation;
                        Logger.Info(string.Format(Lang.T("log.tamer.recordonly", 0), acGroup.Name, procEntry.Name, procEntry.Pid));
                        processedCount++;
                    }
                }
            }
            foreach (int observedPid in new List<int>(recordedProcesses.Keys))
            {
                if (!pids.Contains(observedPid))
                {
                    recordedProcesses.Remove(observedPid);
                }
            }
            return processedCount;
        }

        private bool Sweep()
        {
            Dictionary<string, string> textsByName = BuildActive();
            HashSet<int> sweepValues = new HashSet<int>();
            HashSet<int> pidsWithSnapshot = new HashSet<int>(core.PidsWith(SuppressReason.AntiCheat));
            List<AcquireRequest> acquireRequests = new List<AcquireRequest>();
            if (textsByName.Count > 0)
            {
                ProcessSnapshot processSnapshot;
                try
                {
                    processSnapshot = CaptureSweepSnapshot();
                }
                catch
                {
                    processSnapshot = null;
                }
                if (processSnapshot == null)
                {
                    return false;
                }
                foreach (ProcEntry procEntry in processSnapshot.Entries)
                {
                    int candidatePid = -1;
                    try
                    {
                        candidatePid = procEntry.Pid;
                        string name = procEntry.Name;
                        if (candidatePid != selfPid)
                        {
                            int session = procEntry.Session;
                            string text;
                            if (textsByName.TryGetValue(name, out text) && selfSession >= 0 && (session == selfSession || session == 0))
                            {
                                sweepValues.Add(candidatePid);
                                acquireRequests.Add(new AcquireRequest
                                {
                                    Pid = candidatePid,
                                    Name = name,
                                    Group = text,
                                    Birth = procEntry.Creation
                                });
                            }
                        }
                    }
                    catch
                    {
                        if (candidatePid > 0 && pidsWithSnapshot.Contains(candidatePid))
                        {
                            sweepValues.Add(candidatePid);
                        }
                    }
                }
            }
            List<int> exitedPids = new List<int>();
            foreach (int currentPidsWithSnapshot in pidsWithSnapshot)
            {
                if (!sweepValues.Contains(currentPidsWithSnapshot))
                {
                    exitedPids.Add(currentPidsWithSnapshot);
                }
            }
            ApplyBatch(acquireRequests, exitedPids);
            return true;
        }

        private bool TryBuildAcquireRequest(int pid, string expectedName, string group, out AcquireRequest request)
        {
            request = null;
            if (pid == selfPid) return false;
            string name; long birth; int session;
            if (!ReadGovernanceIdentity(pid, out name, out birth, out session)
                || !string.Equals(name, expectedName, StringComparison.OrdinalIgnoreCase)
                || selfSession < 0 || (session != selfSession && session != 0)) return false;
            request = new AcquireRequest { Pid = pid, Name = name, Group = group, Birth = birth };
            return true;
        }

        private void ApplyBatch(List<AcquireRequest> acquisitions, List<int> releases)
        {
            int acquisitionsCount = ((acquisitions != null) ? acquisitions.Count : 0);
            int releasesCount = ((releases != null) ? releases.Count : 0);
            if (acquisitionsCount == 0 && releasesCount == 0)
            {
                return;
            }
            SuppressionCore.BatchResult batchResult = null;
            AntiCheatAffinityGameContext context = GovernanceContext();
            List<KeyValuePair<string, int>> services = context == null ? null : GovernanceServices();
            core.BeginBatch();
            try
            {
                if (acquisitions != null)
                {
                    foreach (AcquireRequest acquisition in acquisitions)
                    {
                        try
                        {
                            string blocked = !IsGroupEnabled(acquisition.Group) ? "group-disabled"
                                : CheckGovernanceAdmission(acquisition, context, services);
                            if (blocked != null)
                            {
                                core.Release(acquisition.Pid, SuppressReason.AntiCheat);
                                acquisition.Result = AcquireResult.AlreadyProtected;
                                continue;
                            }
                            acquisition.Result = core.Acquire(acquisition.Pid, acquisition.Name, SuppressReason.AntiCheat, acquisition.Group, AntiCheatModes.LevelOf((AntiCheatMode)mode), acquisition.Birth);
                        }
                        catch
                        {
                            acquisition.Result = AcquireResult.ApplyFailed;
                        }
                    }
                }
                if (releases != null)
                {
                    foreach (int release in releases)
                    {
                        try
                        {
                            core.Release(release, SuppressReason.AntiCheat);
                        }
                        catch
                        {
                        }
                    }
                }
            }
            finally
            {
                batchResult = core.EndBatch();
            }
            if (acquisitions == null)
            {
                return;
            }
            foreach (AcquireRequest acquisition2 in acquisitions)
            {
                if ((acquisition2.Result == AcquireResult.NewlyThrottled || acquisition2.Result == AcquireResult.AlreadyThrottled) && (batchResult == null || !batchResult.WasApplied(acquisition2.Pid)))
                {
                    string failureDetailValue = ((batchResult != null) ? batchResult.FailureOf(acquisition2.Pid) : "batch-missing");
                    if (failureDetailValue == "self-protected")
                    {
                        acquisition2.Result = AcquireResult.NewlyProtected;
                        acquisition2.FailureDetail = failureDetailValue;
                    }
                    else
                    {
                        acquisition2.Result = AcquireResult.ApplyFailed;
                        acquisition2.FailureDetail = failureDetailValue;
                    }
                }
                LogAcquireResult(acquisition2);
            }
        }

        private static void LogAcquireResult(AcquireRequest request)
        {
            if (request.Result == AcquireResult.NewlyThrottled)
            {
                Logger.Log(Lang.T("log.tamer.8", 0) + request.Name + " 进程编号 " + request.Pid);
            }
            else if (request.Result == AcquireResult.NewlyProtected && request.FailureDetail != "self-protected")
            {
                Logger.Log(Lang.T("log.tamer.9", 0) + request.Name + " 进程编号 " + request.Pid + Lang.T("log.tamer.10", 0));
            }
            else if (request.Result == AcquireResult.ApplyFailed)
            {
                Logger.Log(Lang.T("log.tamer.8", 0) + request.Name + " 进程编号 " + request.Pid + Lang.T("log.tamer.11", 0) + (string.IsNullOrEmpty(request.FailureDetail) ? "" : (Lang.T("log.tamer.12", 0) + request.FailureDetail)) + Lang.T("log.tamer.13", 0));
            }
        }

        private bool ReleaseAll(string reason)
        {
            bool affinityRestored = affinityRuntime == null || affinityRuntime.Stop();
            int releaseReasonResult = core.ReleaseReason(SuppressReason.AntiCheat);
            if (releaseReasonResult > 0)
            {
                Logger.Log(Lang.T("log.tamer.14", 0) + reason + Lang.T("log.gamemodeboost.53", 0) + releaseReasonResult + Lang.T("log.program.3", 0));
            }
            return affinityRestored && !core.AnyWith(SuppressReason.AntiCheat);
        }
    }
}
