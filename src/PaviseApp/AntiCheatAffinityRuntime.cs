using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Diagnostics;
using System.Threading;

namespace PaviseApp
{
    internal sealed class AntiCheatAffinityGameContext
    {
        internal readonly int Pid;
        internal readonly long Birth, Session;
        internal readonly int Group;
        internal readonly ulong ProtectedMask, GameDomainMask;
        // 用户配置的游戏核心范围，仅作选核偏好，不依赖范围是否已强制执行。
        internal readonly ulong AvoidMask;
        internal readonly HashSet<int> GameAncestors;
        internal readonly ProcessSnapshot Processes;
        internal AntiCheatAffinityGameContext(int pid, long birth, long session, int group, ulong protectedMask, ulong gameDomainMask, HashSet<int> gameAncestors = null, ProcessSnapshot processes = null,
            ulong avoidMask = 0)
        { Pid = pid; Birth = birth; Session = session; Group = group; ProtectedMask = protectedMask; GameDomainMask = gameDomainMask; GameAncestors = gameAncestors; Processes = processes; AvoidMask = avoidMask; }
        internal string Key { get { return Pid + ":" + Birth + ":" + Session; } }
    }

    internal sealed class AntiCheatAffinityTarget
    {
        internal int Pid;
        internal long Birth;
        internal string Name, Group;
        internal string Key { get { return Pid + ":" + Birth; } }
    }

    // 可变状态由工作线程持有，界面只读取新发布的快照。
    internal sealed class AntiCheatAffinityView
    {
        internal readonly string Group, Name, Code, Detail, Kind;
        internal readonly ushort ProcessorGroup;
        internal readonly ulong Mask;
        internal readonly bool Bound, Pending;
        internal readonly int Pid;
        internal readonly long Birth;
        internal AntiCheatAffinityView(AntiCheatAffinityTarget target, string code, string detail, bool bound, bool pending, ushort group, ulong mask, string kind)
        { Group = target.Group; Name = target.Name; Pid = target.Pid; Birth = target.Birth; Code = code; Detail = detail; Bound = bound; Pending = pending; ProcessorGroup = group; Mask = mask; Kind = kind; }
    }

    internal sealed class AntiCheatAffinityRuntime
    {
        internal delegate bool Identify(int pid, long creation, string name, out AntiCheatAffinityIdentity identity, out string reason);
        internal delegate bool Inspect(AntiCheatAffinityIdentity identity, out ushort group, out ulong mask, out string reason, bool allowCpuSets);
        private sealed class TargetState
        {
            internal AntiCheatAffinityTarget Target;
            internal AntiCheatAffinityIdentity Identity;
            internal AntiCheatAffinityChoice Choice;
            internal ushort Group;
            internal ulong Allowed, Protected, Domain, Avoid;
            internal bool Prepared;
            internal string Failure;
            internal AntiCheatAffinityResult Halted;
            internal bool Admitted;
        }
        private readonly AntiCheatAffinityLease lease;
        private readonly Func<AntiCheatAffinitySnapshot> capture;
        private readonly Identify identify;
        private readonly Inspect inspect;
        private readonly Func<int, long, string, bool> prepare;
        private readonly Func<AntiCheatAffinityGameContext, bool> gameAlive;
        private readonly Func<AntiCheatAffinityTarget, AntiCheatAffinityGameContext, string> admission;
        private readonly Func<long> nowMilliseconds;
        private readonly Dictionary<string, TargetState> targets = new Dictionary<string, TargetState>(StringComparer.Ordinal);
        private readonly Dictionary<ushort, ulong> sharedPools = new Dictionary<ushort, ulong>();
        private volatile AntiCheatAffinityView[] views = new AntiCheatAffinityView[0];
        private AntiCheatAffinitySnapshot topology;
        private string session;
        private bool initialized;
        private int coreLimit;
        internal bool PendingRecovery { get { return lease.PendingRecovery; } }
        private long capturedAt;
        private sealed class PreparationAttempt { internal int Failures; internal long Next; }
        private readonly Dictionary<string, PreparationAttempt> preparationAttempts = new Dictionary<string, PreparationAttempt>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> discoveryFailures = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, long> nextLogs = new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly object sync = new object();
        private readonly object revocationGate = new object();
        private readonly Dictionary<string, int> revokedGroups = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private sealed class ForwardTarget
        {
            internal AntiCheatAffinityIdentity Identity;
            internal string Group;
        }
        private volatile ForwardTarget forwardTarget;
        private int generation;
        internal Func<AntiCheatAffinityTarget, int, bool> ForwardAdmission;

        internal AntiCheatAffinityRuntime(Func<int, long, string, bool> prepare, Func<AntiCheatAffinityTarget, AntiCheatAffinityGameContext, string> admission)
            : this(new AntiCheatAffinityLease(), AntiCheatAffinityTopology.Capture,
                AntiCheatAffinityLease.TryIdentify, AntiCheatAffinityLease.TryInspect, prepare, GameAlive, admission) { }
        internal AntiCheatAffinityRuntime(AntiCheatAffinityLease lease, Func<AntiCheatAffinitySnapshot> capture,
            Identify identify, Inspect inspect, Func<int, long, string, bool> prepare, Func<AntiCheatAffinityGameContext, bool> gameAlive,
            Func<AntiCheatAffinityTarget, AntiCheatAffinityGameContext, string> admission = null, Func<long> nowMilliseconds = null)
        { this.lease = lease; this.capture = capture; this.identify = identify; this.inspect = inspect; this.prepare = prepare; this.gameAlive = gameAlive; this.admission = admission; this.nowMilliseconds = nowMilliseconds ?? ClockMilliseconds; }

        private static long ClockMilliseconds() { return (long)(Stopwatch.GetTimestamp() * (1000.0 / Stopwatch.Frequency)); }

        private static bool GameAlive(AntiCheatAffinityGameContext context)
        {
            if (context == null || context.Pid <= 0 || context.Birth <= 0) return false;
            IntPtr handle = Native.OpenProcess(4096, false, context.Pid);
            if (handle == IntPtr.Zero) return false;
            try { return Native.QueryProcessIdentityState(handle, context.Birth) == 0; }
            finally { Native.CloseHandle(handle); }
        }

        internal bool MaintainsTarget(int pid, long birth)
        {
            lock (sync)
            {
                foreach (TargetState state in targets.Values)
                    if (state.Target.Pid == pid && state.Target.Birth == birth && !GroupRevoked(state.Target.Group)
                        && lease.HasAppliedOwnership(state.Identity)) return true;
                return false;
            }
        }

        private bool GroupRevoked(string group)
        { lock (revocationGate) return revokedGroups.ContainsKey("*") || revokedGroups.ContainsKey(group ?? ""); }

        // 立即撤销原生写入资格，不等待工作线程、持久化或界面快照发布。
        internal void RevokeGroup(string group = null)
        {
            lock (revocationGate)
            {
                revokedGroups[group ?? "*"] = Interlocked.Increment(ref generation);
                if (group == null) { lease.RevokeForward(); return; }
                ForwardTarget active = forwardTarget;
                if (active != null && string.Equals(active.Group, group, StringComparison.OrdinalIgnoreCase)) lease.RevokeForward(active.Identity);
                foreach (AntiCheatAffinityView view in views)
                    if (string.Equals(view.Group, group, StringComparison.OrdinalIgnoreCase))
                        lease.RevokeForward(new AntiCheatAffinityIdentity(view.Pid, view.Birth, ""));
            }
        }

        private bool SettleRevokedGroups()
        {
            KeyValuePair<string, int>[] pending;
            lock (revocationGate) pending = revokedGroups.ToArray();
            foreach (KeyValuePair<string, int> request in pending)
            {
                bool all = request.Key == "*", restored = true;
                if (all)
                {
                    restored = lease.ReleaseAll();
                    if (restored) { targets.Clear(); sharedPools.Clear(); }
                }
                else foreach (string key in new List<string>(targets.Keys))
                {
                    TargetState state = targets[key];
                    if (!string.Equals(state.Target.Group, request.Key, StringComparison.OrdinalIgnoreCase)) continue;
                    if (state.Identity == null || lease.Release(state.Identity)) targets.Remove(key);
                    else restored = false;
                }
                if (restored)
                    lock (revocationGate)
                    {
                        int token;
                        if (revokedGroups.TryGetValue(request.Key, out token) && token == request.Value) revokedGroups.Remove(request.Key);
                    }
                else if (all)
                {
                    Publish(targets.Values.Select(t => View(t, "recovery-pending", "restoration-unverified", false, true)).ToArray());
                    return false;
                }
            }
            return true;
        }

        internal void Tick(AntiCheatAffinityGameContext context, IList<AntiCheatAffinityTarget> current, int maximumPhysicalCores = 4, HashSet<int> pausedTargets = null)
        {
            int admittedGeneration = Interlocked.CompareExchange(ref generation, 0, 0);
            lock (sync)
            {
                if (admittedGeneration != generation) return;
                TickCore(context, current, maximumPhysicalCores, admittedGeneration, pausedTargets);
            }
        }

        private void TickCore(AntiCheatAffinityGameContext context, IList<AntiCheatAffinityTarget> current, int maximumPhysicalCores, int admittedGeneration, HashSet<int> pausedTargets)
        {
            if (context != null) lease.BeginRecoverySession(context.Session);
            if (!initialized) { lease.Recover(); initialized = true; }
            if (!SettleRevokedGroups()) return;
            if (context == null || !gameAlive(context)) { StopCore(); return; }
            if (session != context.Key || coreLimit != maximumPhysicalCores)
            {
                if (!StopCore()) return;
                session = context.Key;
                coreLimit = maximumPhysicalCores;
                topology = capture();
                capturedAt = nowMilliseconds();
            }
            long elapsed = nowMilliseconds() - capturedAt;
            bool refreshed = elapsed < 0 || elapsed >= 30000;
            if (refreshed)
            {
                topology = capture(); capturedAt = nowMilliseconds();
                // 停泊或分配状态变化后，重新评估此前不可用的拓扑；进程访问失败仍保持停止。

                foreach (TargetState retry in targets.Values)
                    if (retry.Choice != null && !retry.Choice.Applicable && retry.Halted == null)
                    { retry.Choice = null; retry.Failure = null; }
            }
            HashSet<string> present = new HashSet<string>(current.Select(t => t.Key), StringComparer.Ordinal);
            foreach (string key in new List<string>(targets.Keys))
            {
                TargetState old = targets[key];
                if (present.Contains(key)) continue;
                if (old.Identity == null || lease.Release(old.Identity)) targets.Remove(key);
            }
            foreach (ushort group in new List<ushort>(sharedPools.Keys))
                if (!PoolStillAvailable(topology, group, sharedPools[group], context.Group == group ? context.ProtectedMask : 0))
                {
                    bool ownedAffinity = targets.Values.Any(t => t.Identity != null
                        && t.Group == group && lease.HasAppliedOwnership(t.Identity));
                    if (!ownedAffinity) sharedPools.Remove(group);
                }
            List<AntiCheatAffinityView> next = new List<AntiCheatAffinityView>();
            foreach (AntiCheatAffinityTarget target in current)
            {
                // 游戏进程不能因为与反作弊进程同名而获准进入。
                if (target.Pid == context.Pid) continue;
                if (GroupRevoked(target.Group))
                {
                    TargetState recovering;
                    if (targets.TryGetValue(target.Key, out recovering)) next.Add(View(recovering, "recovery-pending", "group-revoked", false, true));
                    continue;
                }
                TargetState state;
                if (!targets.TryGetValue(target.Key, out state))
                { state = new TargetState { Target = target }; targets.Add(target.Key, state); }
                if (context.GameAncestors != null && context.GameAncestors.Contains(target.Pid))
                {
                    bool restored = state.Identity == null || lease.Release(state.Identity);
                    state.Halted = new AntiCheatAffinityResult("inheritance-risk", "game-process-or-ancestor", false, !restored);
                }
                if (state.Halted != null)
                {
                    if (state.Halted.PendingRecovery && state.Identity != null && lease.Release(state.Identity))
                        state.Halted = new AntiCheatAffinityResult(state.Halted.Code, state.Halted.Detail, false, false);
                    next.Add(View(state, state.Halted.Code, state.Halted.Detail, false, state.Halted.PendingRecovery)); continue;
                }
                ulong protectedMask = context.Group == state.Group ? context.ProtectedMask : 0;
                ulong domainMask = context.Group == state.Group ? context.GameDomainMask : 0;
                ulong avoidMask = context.Group == state.Group ? context.AvoidMask : 0;
                ulong sharedMask;
                bool owned = lease.HasAppliedOwnership(state.Identity);
                if (pausedTargets != null && pausedTargets.Contains(target.Pid))
                {
                    bool pending = lease.PendingRecoveryFor(state.Identity);
                    if (pending)
                    {
                        lease.RevokeForward(state.Identity);
                        pending = !lease.Release(state.Identity);
                    }
                    next.Add(View(state, pending ? "recovery-pending" : "maintenance-paused",
                        "服务或进程资格待核验；仅处理已有恢复责任，暂停前向写入", false, pending));
                    continue;
                }
                if (owned && !lease.PendingRecoveryFor(state.Identity) && state.Choice != null && state.Protected == protectedMask && state.Domain == domainMask
                    && !PoolStillAvailable(topology, state.Group, state.Choice.Mask, protectedMask))
                {
                    next.Add(View(state, "maintenance-paused", "原目标拓扑或有效范围待核验", false, false)); continue;
                }
                // 保护范围变化后，仍合法的核心池保持不变，避免停泊波动触发重复恢复与绑定。
                if (state.Choice != null && state.Choice.Applicable && state.Protected != protectedMask
                    && state.Domain == domainMask && state.Avoid == avoidMask
                    && sharedPools.TryGetValue(state.Group, out sharedMask) && sharedMask == state.Choice.Mask
                    && PoolStillAvailable(topology, state.Group, state.Choice.Mask, protectedMask))
                    state.Protected = protectedMask;
                if (state.Choice != null && (state.Protected != protectedMask || state.Domain != domainMask || state.Avoid != avoidMask
                    || (state.Choice.Applicable && (!sharedPools.TryGetValue(state.Group, out sharedMask) || sharedMask != state.Choice.Mask))
                    || (refreshed && state.Choice.Applicable && !PoolStillAvailable(topology, state.Group, state.Choice.Mask, protectedMask))))
                {
                    if (!lease.Release(state.Identity)) { next.Add(View(state, "recovery-pending", "game-layout-changed", false, true)); continue; }
                    if (sharedPools.TryGetValue(state.Group, out sharedMask) && sharedMask == state.Choice.Mask)
                        sharedPools.Remove(state.Group);
                    state.Choice = null; state.Failure = null;
                    topology = capture(); capturedAt = nowMilliseconds();
                }
                if (state.Failure != null) { next.Add(View(state, "unavailable", state.Failure, false, false)); continue; }
                string reason;
                string discoveryKey = context.Session + "|" + target.Key + "|" + maximumPhysicalCores;
                int failedDiscovery;
                discoveryFailures.TryGetValue(discoveryKey, out failedDiscovery);
                if (!owned && failedDiscovery >= 3)
                { next.Add(View(state, "acquisition-exhausted", "目标发现预算已耗尽；保留本阶段记录", false, false)); continue; }
                if (state.Identity == null && !identify(target.Pid, target.Birth, target.Name, out state.Identity, out reason))
                { discoveryFailures[discoveryKey] = failedDiscovery + 1; state.Failure = reason; next.Add(View(state, "unavailable", reason, false, false)); continue; }
                if (!state.Admitted)
                {
                    reason = admission == null ? null : admission(target, context);
                    if (!string.IsNullOrEmpty(reason))
                    { discoveryFailures[discoveryKey] = failedDiscovery + 1; state.Failure = reason; next.Add(View(state, "unavailable", reason, false, false)); continue; }
                    state.Admitted = true;
                }
                if (state.Choice == null)
                {
                    if (!state.Prepared)
                    {
                        string preparationKey = context.Session + "|" + target.Key + "|" + maximumPhysicalCores;
                        PreparationAttempt preparation;
                        if (!preparationAttempts.TryGetValue(preparationKey, out preparation)) preparationAttempts[preparationKey] = preparation = new PreparationAttempt();
                        if (preparation.Failures >= 3 || nowMilliseconds() < preparation.Next)
                        { next.Add(View(state, "handoff-pending", "previous-placement-recovery-budget", false, true)); continue; }
                        if (!prepare(target.Pid, target.Birth, target.Name))
                        {
                            preparation.Failures++; preparation.Next = nowMilliseconds() + 30000;
                            Logger.Warn("反作弊亲和性准备：目标=" + target.Key + " 失败详情=" + preparation.Failures + "/3；保留恢复状态");
                            next.Add(View(state, "handoff-pending", "previous-placement-recovery", false, true)); continue;
                        }
                        state.Prepared = true;
                    }
                    if (!inspect(state.Identity, out state.Group, out state.Allowed, out reason, true))
                    { discoveryFailures[discoveryKey] = failedDiscovery + 1; state.Failure = reason; next.Add(View(state, "unavailable", reason, false, false)); continue; }
                    state.Protected = context.Group == state.Group ? context.ProtectedMask : 0;
                    state.Domain = context.Group == state.Group ? context.GameDomainMask : 0;
                    state.Avoid = context.Group == state.Group ? context.AvoidMask : 0;
                    ulong allowed = state.Allowed;
                    if (sharedPools.TryGetValue(state.Group, out sharedMask)) allowed &= sharedMask;
                    state.Choice = AntiCheatAffinityTopology.Select(topology, state.Group, allowed, state.Protected, state.Domain, coreLimit, state.Avoid);
                    if (state.Choice.Applicable && sharedPools.TryGetValue(state.Group, out sharedMask) && state.Choice.Mask != sharedMask)
                        state.Choice = new AntiCheatAffinityChoice { Reason = "shared-pool-unavailable" };
                    if (!state.Choice.Applicable)
                    { discoveryFailures[discoveryKey] = failedDiscovery + 1; state.Failure = state.Choice.Reason; next.Add(View(state, "unavailable", state.Failure, false, false)); continue; }
                    if (!sharedPools.ContainsKey(state.Group)) sharedPools[state.Group] = state.Choice.Mask;
                }
                // 停用请求先撤销本轮资格；发现、准备和拓扑读取结束后不得再提交旧代写入。
                AntiCheatAffinityResult result;
                forwardTarget = new ForwardTarget { Identity = state.Identity, Group = target.Group };
                try
                {
                    // 先冻结原生资格，再检查本轮代号与实时开关；检查后才到达 Ensure 的旧周期不能借用新资格。
                    long admittedEpoch = lease.ForwardEpoch;
                    if (admittedGeneration != generation || GroupRevoked(target.Group)
                        || (ForwardAdmission != null && !ForwardAdmission(target, maximumPhysicalCores))) return;
                    result = lease.Ensure(state.Identity, state.Group, state.Choice.Mask, true, admittedEpoch);
                }
                finally { forwardTarget = null; }
                if (admittedGeneration != generation) return;
                if (!result.Bound && !result.PendingRecovery && result.Code != "already-matched"
                    && result.Code != "maintenance-paused" && result.Code != "acquisition-paused") state.Halted = result;
                next.Add(View(state, result.Code, result.Detail, result.Bound, result.PendingRecovery));
            }
            foreach (TargetState pending in targets.Values)
                if (!present.Contains(pending.Target.Key)) next.Add(View(pending, "recovery-pending", "process-release-pending", false, true));
            Publish(next.ToArray());
        }

        private static AntiCheatAffinityView View(TargetState state, string code, string detail, bool bound, bool pending)
        { return new AntiCheatAffinityView(state.Target, code, detail, bound, pending, state.Group,
            state.Choice == null ? 0 : state.Choice.Mask, state.Choice == null ? "none" : state.Choice.Kind); }

        private static bool PoolStillAvailable(AntiCheatAffinitySnapshot snapshot, ushort group, ulong mask, ulong protectedMask)
        {
            if (snapshot == null || !snapshot.Valid || !snapshot.CpuSetMetadata) return false;
            ulong covered = 0;
            foreach (AntiCheatAffinityCore core in snapshot.Cores)
            {
                if (core.Group != group || (core.Mask & mask) == 0) continue;
                if ((core.Mask & mask) != core.Mask || core.Allocated || core.Favored || (core.Mask & protectedMask) != 0) return false;
                // 已准入核心仅因空闲而停泊时，不撤销当前放置，避免每次停泊切换都触发重新绑定。

                covered |= core.Mask;
            }
            return mask != 0 && covered == mask;
        }

        private void Publish(AntiCheatAffinityView[] next)
        {
            string previous = string.Join(";", views.Select(v => v.Group + ":" + v.Name + ":" + v.Code + ":" + v.Detail + ":" + v.Mask).ToArray());
            string current = string.Join(";", next.Select(v => v.Group + ":" + v.Name + ":" + v.Code + ":" + v.Detail + ":" + v.Mask).ToArray());
            views = next;
            if (previous != current)
                foreach (AntiCheatAffinityView view in next)
                {
                    string key = view.Name + "|" + view.Pid;
                    long due;
                    if (nextLogs.TryGetValue(key, out due) && nowMilliseconds() < due) continue;
                    nextLogs[key] = nowMilliseconds() + 30000;
                    Logger.Info("反作弊亲和性 " + view.Name + " 进程编号=" + view.Pid + " " + view.Code + " " + view.Detail
                        + " 处理器组=" + view.ProcessorGroup + " 掩码=0x" + view.Mask.ToString("X", CultureInfo.InvariantCulture));
                }
        }

        internal bool Stop()
        {
            RevokeGroup();
            lock (sync) return StopCore();
        }

        private bool StopCore()
        {
            bool ok = lease.ReleaseAll();
            if (ok) { targets.Clear(); sharedPools.Clear(); topology = null; session = null; lock (revocationGate) revokedGroups.Clear(); Publish(new AntiCheatAffinityView[0]); }
            else Publish(targets.Values.Select(t => View(t, "recovery-pending", "restoration-unverified", false, true)).ToArray());
            return ok;
        }

        internal bool Shutdown()
        {
            RevokeGroup();
            lock (sync)
            {
                bool restored = StopCore();
                bool stopped = lease.Stop();
                return restored && stopped;
            }
        }

        internal string Summary(string group)
        {
            AntiCheatAffinityView[] rows = views.Where(v => v.Group == group).ToArray();
            if (rows.Length == 0) return "";
            if (rows.Any(v => v.Pending)) return PerformancePolicy.Text("硬绑恢复待核验", "Affinity recovery pending");
            int bound = rows.Count(v => v.Bound);
            if (bound > 0) return PerformancePolicy.Text("硬绑 ", "Hard bound ") + bound
                + (rows.Length > bound ? PerformancePolicy.Text(" · 部分未执行", " · partial") : "");
            if (rows.Any(v => v.Code == "conflict")) return PerformancePolicy.Text("亲和性冲突", "Affinity conflict");
            if (rows.All(v => v.Code == "already-matched")) return PerformancePolicy.Text("已有目标亲和性", "Target affinity already set");
            return PerformancePolicy.Text("硬绑未执行", "Affinity unavailable");
        }

        internal string Details(string group)
        {
            return string.Join("\r\n", views.Where(v => v.Group == group).Select(v => v.Name + " (PID " + v.Pid + "): "
                + (v.Bound ? PerformancePolicy.Text("核心绑定已核验", "Core binding verified") : Explain(v.Code, v.Detail))
                + (v.Mask == 0 ? "" : " · G" + v.ProcessorGroup + " CPU " + string.Join(",", Enumerable.Range(0,64).Where(i => (v.Mask & (1UL << i)) != 0).Select(i => i.ToString(CultureInfo.InvariantCulture)).ToArray()))
                + " · " + KindText(v.Kind)).ToArray());
        }

        private static string KindText(string kind)
        {
            if (kind.StartsWith("single-", StringComparison.Ordinal))
                return PerformancePolicy.Text("单物理核 · ", "One physical core · ") + KindText(kind.Substring(7));
            if (kind == "efficiency") return PerformancePolicy.Text("能效核池", "Efficiency cores");
            if (kind == "auxiliary") return PerformancePolicy.Text("辅助物理核", "Auxiliary physical cores");
            if (kind == "alternate-cache-domain") return PerformancePolicy.Text("其他共享缓存域", "Alternate shared-cache domain");
            return PerformancePolicy.Text("尚无目标核池", "No selected core pool");
        }

        private static string Explain(string code, string detail)
        {
            if (code == "conflict") return PerformancePolicy.Text("亲和性冲突，保留原目标等待核验", "Affinity conflict; retaining the original target for verification");
            if (code == "maintenance-paused") return PerformancePolicy.Text("亲和性核验暂停，保留原目标等待恢复", "Affinity verification paused; retaining the original target");
            if (code == "acquisition-paused") return PerformancePolicy.Text("首次接管暂未通过，等待有限重试", "Initial acquisition pending a bounded retry");
            if (code == "acquisition-exhausted") return PerformancePolicy.Text("首次接管预算已耗尽，本阶段停止尝试", "Initial acquisition budget exhausted for this phase");
            if (code == "recovery-pending") return PerformancePolicy.Text("原亲和性恢复待核验", "Original affinity restoration is unverified");
            if (code == "handoff-pending") return PerformancePolicy.Text("等待原核心分配恢复", "Waiting for earlier placement recovery");
            if (code == "already-matched") return PerformancePolicy.Text("已有目标亲和性，未接管恢复", "Target affinity already set; restoration not acquired");
            if (detail == "game-process-or-ancestor" || code == "inheritance-risk") return PerformancePolicy.Text("游戏进程或其祖先，避免亲和性继承", "Game process or ancestor; avoiding affinity inheritance");
            if (detail == "launcher-helper-or-unknown-role" || detail == "not-session-zero-service") return PerformancePolicy.Text("启动器或非独立服务进程，保留原分配", "Launcher or non-service process; preserving placement");
            if (detail == "shared-or-unverified-service-host" || detail == "process-not-in-running-services" || detail == "running-services-unavailable") return PerformancePolicy.Text("无法核验独立反作弊服务身份", "Dedicated anti-cheat service identity could not be verified");
            if (detail == "insufficient-game-core-capacity") return PerformancePolicy.Text("剩余游戏物理核不足", "Insufficient remaining physical cores for the game");
            if (detail == "insufficient-unreserved-cores") return PerformancePolicy.Text("可用辅助物理核不足", "Insufficient available auxiliary physical cores");
            if (detail == "shared-pool-unavailable") return PerformancePolicy.Text("进程原有核心范围无法使用本局共享核池", "Original process affinity cannot use the session's shared core pool");
            if (detail == "alternate-cache-domain-unavailable") return PerformancePolicy.Text("其他缓存域当前停放或不可用", "Alternate cache domain is parked or unavailable");
            return PerformancePolicy.Text("当前条件未执行硬绑", "Hard affinity unavailable under current conditions") + " (" + detail + ")";
        }
    }
}
