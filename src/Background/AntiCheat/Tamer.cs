using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

namespace PaviseApp
{
    // 反作弊引擎工作线程 只在开了逐游戏开关的对局里压制已核验的 ACE EasyAntiCheat BattlEye 服务
    // 没有对局也没有在管对象时一直阻塞在唤醒事件上 不读进程快照 不查服务表 也不做别的周期系统调用
    internal class Tamer
    {
        private sealed class AcquireRequest
        {
            public int Pid;

            public string Name;

            public string Group;

            public long Birth;

            public int Session;


            public AcquireResult Result;

            public string FailureDetail;
        }

        internal const int OverflowSweepIntervalMs = 8000;

        internal const int FailedSweepRetryMs = 1000;

        // 开了压制但还没有在管对象时 最长隔这么久复查一次对局上下文
        internal const int SessionWaitMs = 8000;

        internal const int ObservationIntervalMs = 30000;

        // 暂停后还有恢复债务时的有限唤醒次数 用完后等下一次外部唤醒
        internal const int SuspendedWakeLimit = 12;

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
            try { iterationSnapshot = CaptureProcesses(); } catch { iterationSnapshot = null; }
            if (iterationSnapshot == null)
            {
                captureRetryPhase.CaptureFailures++;
                captureRetryPhase.CaptureNotBefore = DateTime.UtcNow.AddMilliseconds(FailedSweepRetryMs).Ticks;
                Logger.Log(() => Logger.Format("反作弊引擎快照：尝试={0}/3；观察与扫描共用失败采样预算", "Anti-cheat engine snapshot: attempt={0}/3; failed capture shared by observation and sweep", captureRetryPhase.CaptureFailures));
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
                    // 序号不能单独确认进程身份 重放通知不能补充预算
                    string identity = change.Pid + "/" + birth + "/" + change.Kind;
                    if (sweepObservedEvents.Add(identity)) sweepEventPhase++;
                }
            }
            var names = new List<string>(active.Keys);
            names.Sort(StringComparer.OrdinalIgnoreCase);
            string configuration = (governanceContextKey ?? "") + "\n" + string.Join("\n", names.ToArray());
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
        private volatile Dictionary<string, bool> forwardEnabled = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        private readonly object eventSync = new object();

        private readonly List<ProcessChange> pendingChanges = new List<ProcessChange>();

        private readonly AutoResetEvent kick = new AutoResetEvent(true);

        private readonly SuppressionCore core;

        private readonly int selfPid;

        private readonly int selfSession;

        private volatile bool stopping;

        private readonly Dictionary<int, long> recordedProcesses = new Dictionary<int, long>();

        private int processEventsAvailable;

        private long panicUntilUtcTicks;

        private int fullSweepRequested = 1;

        private int overflowSweepRequested;

        // 工作线程每轮结束时发布 不需要进程变化时通知直接丢弃 不唤醒线程
        private volatile bool listening = true;

        private volatile HashSet<int> relevantPids = new HashSet<int>();

        private int suspendedWakes;

        private int loopIterations;

        private Thread worker;

        internal Func<AntiCheatAffinityGameContext> AffinityGameContext;
        // 当前对局 为空表示不在对局 false 表示对局中没开压制 true 表示开了 未接入时保持手动控制 供隔离测试使用
        internal Func<bool?> SessionChoice;
        // 测试接缝 生产中就是系统快照和 SCM 服务表
        internal Func<ProcessSnapshot> CaptureProcesses = ProcessSnapshotSource.Capture;
        internal Func<List<KeyValuePair<string, int>>> QueryServices = SvcQuery.RunningServices;
        private AntiCheatAffinityRuntime affinityRuntime;
        private bool affinityFastWake;
        private bool affinityShutdownOk = true;
        private string governanceContextKey;

        internal int LoopIterations { get { return Interlocked.CompareExchange(ref loopIterations, 0, 0); } }

        internal int ServiceQueries { get { return Interlocked.CompareExchange(ref serviceQueryCount, 0, 0); } }

        public bool Paused
        {
            get
            {
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
            using (System.Diagnostics.Process process = System.Diagnostics.Process.GetCurrentProcess())
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
            // 启动时一律不压制 等对局按游戏开关开启
            foreach (AcGroup acGroup in AntiCheatCatalog.Groups)
            {
                enabled[acGroup.Key] = false;
            }
            forwardEnabled = new Dictionary<string, bool>(enabled, StringComparer.OrdinalIgnoreCase);
            core.AntiCheatForwardAdmission = delegate(string group, SuppressionLevel level)
            {
                bool on;
                return !stopping && DateTime.UtcNow.Ticks >= Interlocked.Read(ref panicUntilUtcTicks)
                    && group != null && forwardEnabled.TryGetValue(group, out on) && on
                    && level == AntiCheatModes.Level;
            };
        }

        public bool IsGroupEnabled(string key)
        {
            lock (sync)
            {
                bool value;
                return enabled.TryGetValue(key, out value) && value;
            }
        }

        public bool PanicRestore()
        {
            Interlocked.Exchange(ref panicUntilUtcTicks, DateTime.UtcNow.AddSeconds(4.0).Ticks);
            RevokeRuntimeWrites();
            Interlocked.Exchange(ref fullSweepRequested, 1);
            kick.Set();
            lock (engineSync)
            {
                return ReleaseAll(Lang.T("t.gamemode.42"));
            }
        }

        public void SetGroupEnabled(string key, bool on)
        {
            if (SetGroupEnabledCore(key, on)) Poke();
        }

        // 工作线程同步对局开关时直接调用 不唤醒自己 本轮按新开关接着执行
        private bool SetGroupEnabledCore(string key, bool on)
        {
            AcGroup acGroup = Array.Find(AntiCheatCatalog.Groups, (AcGroup g) => string.Equals(g.Key, key, StringComparison.OrdinalIgnoreCase));
            if (acGroup != null && acGroup.Suppressible)
            {
                if (!on)
                {
                    // 先发布只读资格快照 再撤销旧代 之后的周期不能借旧资格重新准入
                    lock (sync)
                    {
                        var next = new Dictionary<string, bool>(enabled, StringComparer.OrdinalIgnoreCase);
                        next[key] = false;
                        forwardEnabled = next;
                    }
                    RevokeRuntimeWrites(key);
                }
                bool changed;
                lock (sync)
                {
                    bool previous;
                    changed = !enabled.TryGetValue(key, out previous) || previous != on;
                    enabled[key] = on;
                    forwardEnabled = new Dictionary<string, bool>(enabled, StringComparer.OrdinalIgnoreCase);
                }
                if (changed) Logger.Log(() => Logger.Format("反作弊分组 {0} {1}", "Anti-cheat group {0} {1}", key, (on ? Logger.Text("开启压制", "suppression on") : Logger.Text("关闭并恢复", "off and restored"))));
                return true;
            }
            return false;
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
            RevokeRuntimeWrites();
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

        private void RevokeRuntimeWrites(string group = null)
        {
            core.RevokeAntiCheatRuntimeWrites(group);
            AntiCheatAffinityRuntime runtime = affinityRuntime;
            if (runtime != null) runtime.RevokeGroup(group);
        }

        public void Poke()
        {
            Interlocked.Exchange(ref fullSweepRequested, 1);
            Interlocked.Exchange(ref suspendedWakes, 0);
            listening = true;
            kick.Set();
        }

        public void NotifyProcessChanges(ProcessChangeBatch batch)
        {
            if (batch == null || !listening)
            {
                return;
            }
            bool queued = false;
            lock (eventSync)
            {
                if (batch.Overflowed)
                {
                    pendingChanges.Clear();
                    Interlocked.Exchange(ref overflowSweepRequested, 1);
                    queued = true;
                }
                else
                {
                    var relevant = new List<ProcessChange>();
                    foreach (ProcessChange processChange in batch.Changes)
                    {
                        if (processChange != null && processChange.Pid > 0 && RelevantChange(processChange))
                        {
                            relevant.Add(processChange);
                        }
                    }
                    if (pendingChanges.Count + relevant.Count > 512)
                    {
                        pendingChanges.Clear();
                        Interlocked.Exchange(ref overflowSweepRequested, 1);
                        queued = true;
                    }
                    else if (relevant.Count > 0)
                    {
                        pendingChanges.AddRange(relevant);
                        queued = true;
                    }
                }
            }
            if (queued) kick.Set();
        }

        // 只有反作弊进程的启动 以及本方在管或刚见过的进程的退出 才需要处理
        private bool RelevantChange(ProcessChange change)
        {
            if (!string.IsNullOrEmpty(change.Name) && AntiCheatCatalog.IsKnownProcess(change.Name)) return true;
            return change.Kind == ProcessChangeKind.Stopped && relevantPids.Contains(change.Pid);
        }

        private void Loop()
        {
            Logger.Log("反作弊压制引擎启动 保留原核心分配", "Anti-cheat suppression engine starting, original core assignment kept");
            affinityRuntime = new AntiCheatAffinityRuntime(core.PrepareAntiCheatAffinity, CheckAffinityAdmission)
            {
                ForwardAdmission = delegate(AntiCheatAffinityTarget target, int maximumPhysicalCores)
                {
                    bool on;
                    return !stopping && DateTime.UtcNow.Ticks >= Interlocked.Read(ref panicUntilUtcTicks)
                        && target != null && target.Group != null && forwardEnabled.TryGetValue(target.Group, out on) && on
                        && maximumPhysicalCores == AntiCheatModes.CoreLimit;
                }
            };
            long nextFullSweepTicks = 0L;
            long nextOverflowSweepTicks = 0L;
            long nextObservationTicks = 0L;
            long failedSweepRetryNotBefore = 0L;
            bool wasInSession = false;
            while (!stopping)
            {
                Interlocked.Increment(ref loopIterations);
                bool suspended = true;
                bool engineWork = true;
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
                        bool inSession = SyncSession();
                        RefreshGovernanceSession();
                        SweepRetryPhase retryPhase = SelectSweepRetryPhase(changes, out phaseChanged);
                        captureRetryPhase = retryPhase;
                        iterationSnapshot = null;
                        iterationCaptureAttempted = false;
                        iterationServices = null;
                        iterationServicesRead = false;
                        // 只读观察只在对局中做 对局开始时一次 之后跟着已有的唤醒最多每 30 秒一次
                        if (inSession && (!wasInSession || ticks >= nextObservationTicks))
                        {
                            nextObservationTicks = ticks + TimeSpan.FromMilliseconds(ObservationIntervalMs).Ticks;
                            try { ObserveRecordOnlyProcesses(CaptureSweepSnapshot()); } catch { }
                        }
                        wasInSession = inSession;
                        suspended = Paused || panicCooldown;
                        if (!suspended)
                        {
                            failedSweepRetryNotBefore = retryPhase.NotBefore;
                            if (phaseChanged) nextFullSweepTicks = retryPhase.NotBefore;
                            fullSweep = Interlocked.Exchange(ref fullSweepRequested, 0) != 0;
                            overflowSweep = Interlocked.Exchange(ref overflowSweepRequested, 0) != 0;
                        }
                        if (suspended)
                        {
                            // 没有在管对象和恢复债务时不必释放 空闲时不碰任何进程
                            if (HasEngineWork()) ReleaseAll(Paused ? Logger.Text("本局未开启", "not enabled this session") : Logger.Text("紧急恢复冷却", "emergency restore cooldown"));
                        }
                        // 只有执行失败才消耗阶段预算 Poke 不能重置预算或截止时间
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
                                    Logger.Log(() => Logger.Format("反作弊引擎扫描恢复：阶段={0} 尝试={1}/3; {2}", "Anti-cheat engine sweep restore: phase={0} attempt={1}/3; {2}", sweepPhaseKey, retryPhase.Failures, (retryPhase.Failures >= 3 ? Logger.Text("已停止主动重试，进程事件继续运行", "further retries stopped; process events keep running") : "backoff")));
                                    nextFullSweepTicks = failedSweepRetryNotBefore;
                                    // 快照失败后 将已取出的事件重新入队 交给有界队列按原预算重试
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
                                // 使用完整快照校正部分批次 不立即自唤醒重放
                                Interlocked.Exchange(ref overflowSweepRequested, 1);
                                if (ticks >= failedSweepRetryNotBefore)
                                    failedSweepRetryNotBefore = DateTime.UtcNow.AddMilliseconds(FailedSweepRetryMs).Ticks;
                                retryPhase.NotBefore = failedSweepRetryNotBefore;
                                nextFullSweepTicks = failedSweepRetryNotBefore;
                                throw;
                            }
                        }
                        SyncAffinity(suspended);
                        engineWork = HasEngineWork();
                        PublishListening(inSession || !suspended || engineWork);
                    }
                    Logger.RepeatedRecovered("Tamer");
                }
                catch (Exception ex)
                {
                    Logger.RepeatedError("Tamer", Logger.Format("反作弊压制异常 {0}: {1}", "Anti-cheat suppression exception {0}: {1}", ex.GetType().Name, ex.Message), -1L);
                }
                int millisecondsTimeout;
                if (suspended)
                {
                    // 暂停时只为结清恢复债务做有限次数的退避唤醒 没有债务就一直等外部唤醒
                    int wakes = Interlocked.CompareExchange(ref suspendedWakes, 0, 0);
                    if (!engineWork || wakes >= SuspendedWakeLimit) millisecondsTimeout = Timeout.Infinite;
                    else
                    {
                        Interlocked.Increment(ref suspendedWakes);
                        millisecondsTimeout = (int)Math.Min(30000L, 1000L << Math.Min(wakes, 5));
                    }
                }
                else
                {
                    Interlocked.Exchange(ref suspendedWakes, 0);
                    long remainingNumericValue = nextFullSweepTicks - DateTime.UtcNow.Ticks;
                    long remainingMax = Math.Max(nextOverflowSweepTicks, failedSweepRetryNotBefore) - DateTime.UtcNow.Ticks;
                    if (Interlocked.CompareExchange(ref overflowSweepRequested, 0, 0) != 0 && remainingMax > 0 && (remainingNumericValue <= 0 || remainingMax < remainingNumericValue))
                    {
                        remainingNumericValue = remainingMax;
                    }
                    millisecondsTimeout = (int)((remainingNumericValue <= 0) ? SessionWaitMs : Math.Min(SessionWaitMs, Math.Max(1L, remainingNumericValue / 10000)));
                    // 每秒核验只在有在管对象时进行
                    if (affinityFastWake) millisecondsTimeout = Math.Min(millisecondsTimeout, 1000);
                }
                kick.WaitOne(millisecondsTimeout);
            }
            affinityShutdownOk = ReleaseAll(Lang.T("t.gamemode.53"));
            if (affinityRuntime != null) affinityShutdownOk = affinityRuntime.Shutdown() && affinityShutdownOk;
        }

        private bool HasEngineWork()
        {
            AntiCheatAffinityRuntime runtime = affinityRuntime;
            return core.AnyWith(SuppressReason.AntiCheat) || core.AntiCheatResourceRecoveryPending
                || (runtime != null && runtime.PendingRecovery);
        }

        private void PublishListening(bool value)
        {
            var pids = new HashSet<int>(core.PidsWith(SuppressReason.AntiCheat));
            foreach (int pid in sweepEventBirths.Keys) pids.Add(pid);
            relevantPids = pids;
            listening = value;
        }

        private void SyncAffinity(bool suspended)
        {
            AntiCheatAffinityGameContext context = suspended ? null : GovernanceContext();
            Dictionary<string, string> activeGroups = BuildActive();
            List<AntiCheatAffinityTarget> targets = new List<AntiCheatAffinityTarget>();
            HashSet<int> pausedTargets = new HashSet<int>();
            foreach (int pid in core.PidsWith(SuppressReason.AntiCheat))
            {
                string name = core.NameOf(pid), group;
                long birth = core.CreationOf(pid);
                AcquireRequest retained = new AcquireRequest { Pid = pid, Name = name, Birth = birth };
                if (suspended || name == null || !activeGroups.TryGetValue(name, out group))
                {
                    core.Release(pid, SuppressReason.AntiCheat);
                    continue;
                }
                if (RetainedGovernanceUnknown(retained, context))
                {
                    if (affinityRuntime.MaintainsTarget(pid, birth))
                    {
                        targets.Add(new AntiCheatAffinityTarget { Pid = pid, Birth = birth, Name = name, Group = group });
                        pausedTargets.Add(pid);
                    }
                    continue;
                }
                if (CheckGovernanceAdmission(retained, context) != null)
                { core.Release(pid, SuppressReason.AntiCheat); continue; }
                if (!suspended && birth > 0 && name != null)
                    core.Reconcile(pid, name, SuppressReason.AntiCheat);
                // 资源准入已经核验独立服务或 ACE 直属客户端 客户端同样需要现有的核心范围限制
                if (birth > 0 && (core.IsThrottled(pid) || affinityRuntime.MaintainsTarget(pid, birth)) && name != null && activeGroups.TryGetValue(name, out group))
                    targets.Add(new AntiCheatAffinityTarget { Pid = pid, Birth = birth, Name = name, Group = group });
            }
            if (targets.Count == 0) context = null;
            affinityRuntime.Tick(context, targets, AntiCheatModes.CoreLimit, pausedTargets);
            affinityFastWake = !suspended && targets.Count > 0;
        }

        private AntiCheatAffinityGameContext GovernanceContext()
        {
            try { return AffinityGameContext == null ? null : AffinityGameContext(); }
            catch { return null; }
        }

        // 对局开始时按游戏开关开启全部可压制分组 实际只压本局在运行且通过核验的那些
        // 退局 游戏退出或关掉开关时撤销本方写入并恢复 返回是否在对局中
        private bool SyncSession()
        {
            Func<bool?> provider = SessionChoice;
            if (provider == null) return !Paused;
            bool? choice;
            try { choice = provider(); }
            catch { choice = null; }
            bool on = choice.HasValue && choice.Value;
            foreach (AcGroup group in AntiCheatCatalog.Groups)
                if (group.Suppressible && on != IsGroupEnabled(group.Key)) SetGroupEnabledCore(group.Key, on);
            return choice.HasValue;
        }

        private void RefreshGovernanceSession()
        {
            AntiCheatAffinityGameContext context = GovernanceContext();
            string key = context == null ? null : context.Key;
            if (key == governanceContextKey) return;
            governanceContextKey = key;
            Interlocked.Exchange(ref fullSweepRequested, 1);
        }

        private sealed class GovernanceRead
        {
            internal List<KeyValuePair<string, int>> Services;
            internal int ServiceQueries, ServiceFailures;
            internal long NextServiceQuery;
            internal string Path;
            internal int PathFailures;
            internal long NextPathRead;
        }

        private readonly Dictionary<string, GovernanceRead> governanceReads = new Dictionary<string, GovernanceRead>(StringComparer.Ordinal);

        private List<KeyValuePair<string, int>> iterationServices;

        private bool iterationServicesRead;

        private int serviceQueryCount;

        private GovernanceRead GovernanceReadOf(int pid, long birth)
        {
            string key = pid.ToString(CultureInfo.InvariantCulture) + ":" + birth.ToString(CultureInfo.InvariantCulture);
            GovernanceRead read;
            if (!governanceReads.TryGetValue(key, out read))
            {
                if (governanceReads.Count >= 256) PruneGovernanceReads(null);
                governanceReads[key] = read = new GovernanceRead();
            }
            return read;
        }

        private void PruneGovernanceReads(ProcessSnapshot snapshot)
        {
            if (snapshot == null) { governanceReads.Clear(); return; }
            var live = new HashSet<string>(StringComparer.Ordinal);
            foreach (ProcEntry entry in snapshot.Entries)
                live.Add(entry.Pid.ToString(CultureInfo.InvariantCulture) + ":" + entry.Creation.ToString(CultureInfo.InvariantCulture));
            foreach (string key in new List<string>(governanceReads.Keys))
                if (!live.Contains(key)) governanceReads.Remove(key);
        }

        // 服务表只在出现尚未核验的反作弊服务候选时读取 一轮最多读一次 结果按进程编号和创建时间缓存
        // 进程不变就不再查询 服务刚起来时 SCM 可能还没登记它的进程编号 没找到时 5 秒后最多再查一次
        private List<KeyValuePair<string, int>> ServicesFor(int pid, long birth)
        {
            if (pid <= 0 || birth <= 0) return null;
            GovernanceRead read = GovernanceReadOf(pid, birth);
            long now = DateTime.UtcNow.Ticks;
            bool recheck = read.Services != null && read.Services.Count == 0 && read.ServiceQueries < 2 && now >= read.NextServiceQuery;
            if (read.Services != null && !recheck) return read.Services;
            if (read.ServiceFailures >= 3 || now < read.NextServiceQuery) return read.Services;
            if (!iterationServicesRead)
            {
                iterationServicesRead = true;
                Interlocked.Increment(ref serviceQueryCount);
                Func<List<KeyValuePair<string, int>>> query = QueryServices;
                try { iterationServices = query == null ? null : query(); }
                catch { iterationServices = null; }
            }
            if (iterationServices == null)
            {
                read.ServiceFailures++;
                read.NextServiceQuery = now + TimeSpan.FromSeconds(8).Ticks;
                Logger.Warn(Logger.Format("反作弊服务表读取失败：进程编号={0} 尝试={1}/3{2}", "Anti-cheat service table read failed: pid={0} attempt={1}/3{2}", pid, read.ServiceFailures, (read.ServiceFailures >= 3 ? Logger.Text("；该进程只保护", "; this process is protected only") : "")));
                return read.Services;
            }
            var rows = new List<KeyValuePair<string, int>>();
            foreach (KeyValuePair<string, int> service in iterationServices) if (service.Value == pid) rows.Add(service);
            read.ServiceQueries++;
            read.Services = rows;
            read.NextServiceQuery = rows.Count == 0 && read.ServiceQueries < 2 ? now + TimeSpan.FromSeconds(5).Ticks : long.MaxValue;
            return rows;
        }

        // 映像路径按进程编号和创建时间缓存 读失败最多三次
        private string GovernanceImageOf(int pid, long birth)
        {
            GovernanceRead read = GovernanceReadOf(pid, birth);
            if (read.Path != null) return read.Path;
            long now = DateTime.UtcNow.Ticks;
            if (read.PathFailures >= 3 || now < read.NextPathRead) return null;
            string path = null;
            try { path = ReadGovernanceImage(pid, birth); } catch { path = null; }
            if (string.IsNullOrEmpty(path))
            {
                read.PathFailures++;
                read.NextPathRead = now + TimeSpan.FromSeconds(2 * read.PathFailures).Ticks;
                Logger.Warn(Logger.Format("反作弊映像路径读取失败：进程编号={0} 尝试={1}/3{2}", "Anti-cheat image path read failed: pid={0} attempt={1}/3{2}", pid, read.PathFailures, (read.PathFailures >= 3 ? Logger.Text("；该进程只保护", "; this process is protected only") : "")));
                return null;
            }
            return read.Path = path;
        }

        // 会话 0 的服务以 SYSTEM 运行 不开调试权限读不到映像路径 也写不进资源字段
        private static string ReadGovernanceImage(int pid, long birth)
        {
            Native.TryEnableDebugPrivilege();
            IntPtr process = Native.OpenProcess(0x1000, false, pid);
            if (process == IntPtr.Zero) return null;
            try { return Native.QueryProcessIdentityState(process, birth) == 0 ? Native.ImagePath(process) : null; }
            finally { Native.CloseHandle(process); }
        }

        private static bool ReadGovernanceIdentity(int pid, out string name, out long birth, out int session)
        {
            name = null; birth = 0; session = -1;
            IntPtr process = Native.OpenProcess(0x101000, false, pid);
            if (process == IntPtr.Zero) return false;
            try
            {
                long cpu; ulong io;
                if (!Native.QueryProcessSample(process, out birth, out cpu, out io) || birth <= 0) return false;
                if (!Native.TryGetLiveProcessSessionId(process, pid, out session)) return false;
                name = Native.ImageName(process);
                return !string.IsNullOrEmpty(name);
            }
            catch { return false; }
            finally { Native.CloseHandle(process); }
        }

        // 已纳管对象读取暂时失败不等于退出或失去资格 暂停写入 待原身份和服务表可核验后继续
        private bool RetainedGovernanceUnknown(AcquireRequest request, AntiCheatAffinityGameContext context)
        {
            if (context == null || request == null || request.Birth <= 0) return false;
            if (context.GameAncestors == null) return true;
            IntPtr process = Native.OpenProcess(0x101000, false, request.Pid);
            if (process == IntPtr.Zero) return !Native.LastOpenProcessFailureWasNoSuchProcess();
            try
            {
                int state = Native.QueryProcessIdentityState(process, request.Birth);
                if (state != 0) return state < 0;
                string name; long birth; int session;
                if (!ReadGovernanceIdentity(request.Pid, out name, out birth, out session)) return true;
                // 会话 0 服务的服务表和映像路径都是缓存值 已核验过的不再读取 读不到时暂停写入
                return session == 0 && (ServicesFor(request.Pid, request.Birth) == null || GovernanceImageOf(request.Pid, request.Birth) == null);
            }
            finally { Native.CloseHandle(process); }
        }

        private string CheckGovernanceAdmission(AcquireRequest request, AntiCheatAffinityGameContext context)
        {
            if (context == null || context.GameAncestors == null) return "game-ancestry-unavailable";
            if (request == null || request.Birth <= 0) return "process-identity-unavailable";
            string name; long birth; int session;
            if (!ReadGovernanceIdentity(request.Pid, out name, out birth, out session)
                || birth != request.Birth || !string.Equals(name, request.Name, StringComparison.OrdinalIgnoreCase))
                return "process-identity-changed";
            request.Session = session;
            string reason;
            if (session == 0) return ServiceAdmission(name, request.Pid, birth, context);
            ProcEntry service;
            if (!AntiCheatAffinityEligibility.TryAceClientService(context.Processes, name, request.Pid, birth, session,
                context.GameAncestors, out service, out reason)) return reason;
            string serviceName; long serviceBirth; int serviceSession;
            if (!ReadGovernanceIdentity(service.Pid, out serviceName, out serviceBirth, out serviceSession)
                || serviceBirth != service.Creation || serviceSession != service.Session
                || !string.Equals(serviceName, service.Name, StringComparison.OrdinalIgnoreCase)) return "service-identity-changed";
            return ServiceAdmission(serviceName, service.Pid, serviceBirth, context);
        }

        // 先查不需要系统调用的条件 再按缓存读映像路径 最后才查服务表 游戏目录自带的组件不会触发服务表读取
        private string ServiceAdmission(string name, int pid, long birth, AntiCheatAffinityGameContext context)
        {
            string reason;
            if (!AntiCheatAffinityEligibility.ServiceCandidate(name, pid, 0, context.GameAncestors, out reason)) return reason;
            if (!AntiCheatAffinityEligibility.InstalledImage(name, GovernanceImageOf(pid, birth), out reason)) return reason;
            return AntiCheatAffinityEligibility.Eligible(name, pid, 0, ServicesFor(pid, birth), context.GameAncestors, out reason) ? null : reason;
        }

        private string CheckAffinityAdmission(AntiCheatAffinityTarget target, AntiCheatAffinityGameContext context)
        {
            try
            {
                if (target == null) return "process-identity-unavailable";
                if (!IsGroupEnabled(target.Group)) return "group-disabled";
                // 真正施加核心限制前再次核验客户端 父服务和创建时间 避免只在扫描时放行
                return CheckGovernanceAdmission(new AcquireRequest { Pid = target.Pid, Birth = target.Birth, Name = target.Name }, context);
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
                        Logger.Info(Logger.Format("反作弊只记录 {0} · {1} PID {2} · 未执行压制", "Anti-cheat record only {0} · {1} PID {2} · not suppressed", acGroup.Name, procEntry.Name, procEntry.Pid));
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
                PruneGovernanceReads(processSnapshot);
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
                                : CheckGovernanceAdmission(acquisition, context);
                            if (blocked != null)
                            {
                                if (IsGroupEnabled(acquisition.Group) && core.HasReason(acquisition.Pid, SuppressReason.AntiCheat)
                                    && RetainedGovernanceUnknown(acquisition, context))
                                { acquisition.Result = AcquireResult.AlreadyProtected; continue; }
                                core.Release(acquisition.Pid, SuppressReason.AntiCheat);
                                acquisition.Result = AcquireResult.AlreadyProtected;
                                continue;
                            }
                            acquisition.Result = core.Acquire(acquisition.Pid, acquisition.Name, SuppressReason.AntiCheat, acquisition.Group, AntiCheatModes.Level, acquisition.Birth);
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
                            string trackedName = core.NameOf(release), group;
                            Dictionary<string, string> groups = BuildActive();
                            if (trackedName != null && groups.TryGetValue(trackedName, out group)
                                && RetainedGovernanceUnknown(new AcquireRequest { Pid = release, Birth = core.CreationOf(release), Name = trackedName }, context)) continue;
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

        private void LogAcquireResult(AcquireRequest request)
        {
            if (request.Result == AcquireResult.NewlyThrottled)
            {
                string evidence;
                if (core.TryReadAntiCheatEvidence(request.Pid, request.Birth, out evidence))
                    Logger.Log(() => Logger.Format("压制 {0} 进程编号 {1} | {2}", "Suppress {0} pid {1} | {2}", request.Name, request.Pid, evidence));
                else
                    Logger.Warn(Logger.Format("反作弊压制后的状态未能核验：{0} 进程编号={1}；{2}", "Anti-cheat post-suppression state could not be verified: {0} pid={1}; {2}", request.Name, request.Pid, evidence));
            }
            else if (request.Result == AcquireResult.NewlyProtected && request.FailureDetail != "self-protected")
            {
                Logger.Log(() => Logger.Format("打开 {0} 进程编号 {1} 未压制：句柄受内核保护", "Open {0} pid {1} not suppressed: handle is kernel-protected", request.Name, request.Pid));
            }
            else if (request.Result == AcquireResult.ApplyFailed)
            {
                Logger.Log(() => Logger.Format("压制 {0} 进程编号 {1} 未完全生效{2} 已保留快照 将按退避计划重试", "Suppress {0} pid {1} not fully applied{2}; snapshot kept, will try again per the backoff plan", request.Name, request.Pid, (string.IsNullOrEmpty(request.FailureDetail) ? "" : (Logger.Text(" 失败环节 ", " failed step ") + request.FailureDetail))));
            }
        }

        private bool ReleaseAll(string reason)
        {
            bool affinityRestored = affinityRuntime == null || affinityRuntime.Stop();
            int releaseReasonResult = core.ReleaseReason(SuppressReason.AntiCheat);
            if (releaseReasonResult > 0)
            {
                Logger.Log(() => Logger.Format("反作弊压制解除 {0} restoredProcessCount={1} 个进程", "Anti-cheat suppression released {0} restoredProcessCount={1} processes", reason, releaseReasonResult));
            }
            return affinityRestored && !core.AnyWith(SuppressReason.AntiCheat) && !core.AntiCheatResourceRecoveryPending;
        }
    }
}
