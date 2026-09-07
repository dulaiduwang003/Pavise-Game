// @author bdth 2074055628@qq.com
// 文件用途 扫描并压制游戏之外的后台进程
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace PaviseApp
{
    internal partial class GameMode
    {
#if PAVISE_PERFLAB
        private HashSet<string> performanceSuppressionScope;
#endif

#if PAVISE_PERFLAB
        internal void RestrictBackgroundSuppressionToPaths(
            IEnumerable<string> executablePaths)
        {
            var next = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (executablePaths != null)
                foreach (string path in executablePaths)
                {
                    string normalized = WhitelistRule.NormalizeImagePath(path);
                    if (!string.IsNullOrEmpty(normalized)) next.Add(normalized);
                }
            lock (sync) performanceSuppressionScope = next;
        }
#endif

        private bool PerformanceScopeAllows(string imagePath)
        {
#if PAVISE_PERFLAB
            lock (sync)
            {
                if (performanceSuppressionScope == null) return true;
                return performanceSuppressionScope.Contains(
                    WhitelistRule.NormalizeImagePath(imagePath));
            }
#else
            return true;
#endif
        }

        private sealed class BackgroundRequest
        {
            public int Pid;
            public string Name;
            public long Creation;
            public long Cpu;
            public SuppressionLevel Desired;
            public SuppressionLevel Previous;
            public bool HadBackgroundReason;
            public AcquireResult Result;
            public string FailureDetail;
        }

        private static readonly HashSet<int> EmptyPidSet = new HashSet<int>();

        // 掌机档的后台压制跟专注一样狠 掌机核心少 后台抢一点都更疼 而且压后台本身还省电
        //   掌机跟专注的差别全在功耗侧 不在压制侧 见 IsHandheld 的几个挂点
        // 极限档的压制口径与电竞逐字节相同 差异全在功能开启广度 别在这里给它加狠
        internal static bool IsAggressive(PerformancePreset mode, bool aggressiveOn)
        {
            return mode == PerformancePreset.Competitive
                || mode == PerformancePreset.Extreme
                || mode == PerformancePreset.Handheld
                || (mode == PerformancePreset.Custom && aggressiveOn);
        }

        // 功耗侧默认避让 不拨电源滑块 插电也放开纯省电项 CPU 空闲由独立确认策略控制
        internal static bool IsHandheld(PerformancePreset mode)
        {
            return mode == PerformancePreset.Handheld;
        }

        internal static bool ResolvePowerPlanEnabled(PerformancePreset mode, bool manuallyEnabled)
        {
            return manuallyEnabled;
        }

        // 2.0 起后台只有两态 通过保护边界的一律直接隔离 不再看热度也不再从省电逐级往上爬
        //   不等它先吃十几秒资源才动手 对局一开始枚举一遍全压下去
        //   冷进程也压 默认不改亲和性 冷进程没有就绪线程时本来就不耗 CPU
        //   重压后台绑核开着时例外 只有热度坐实的才被限定核心 见 GameMode.HeavySqueeze
        //   偶尔醒来也能在任何一个没有更高优先级工作的核上跑 不会被挤着排队
        //   唯一还在把关的是 BasicBackgroundEligible 那道保护边界
        //   反作弊 系统核心 输入音频外设链 加速器 硬件控制 白名单 其它登录账户一律不碰
        //   每个游戏默认保留家族保护 按路径 同会话的有效父子身份确认成员
        //   用户逐项开启“压制家族后台”后取消该项的家族豁免 其余安全边界不变
        //   档位差异不再体现在压制强度 只体现在哪些进程有资格被碰
        internal static SuppressionLevel BackgroundLevel()
        {
            return SuppressionLevel.Isolated;
        }

        private void Sweep(ProcessSnapshot all, HashSet<int> gamePids)
        {

            lock (whiteEvalSync)
                SweepWithStableWhitelist(all, gamePids);
        }

        private void SweepWithStableWhitelist(
            ProcessSnapshot all, HashSet<int> gamePids)
        {
            PolicySnapshot sp = sessionPolicy;
            PerformancePreset mode = sp != null ? sp.Preset : ActivePreset;
            int foregroundPid = GameSessionDetector.ForegroundPid();
            // 自适应压制升档 智能档专属 见 AdaptiveGuard 局中切走预设的过渡周期也不放行
            bool aggressive = IsAggressive(mode, sp != null ? sp.Aggressive : aggressiveOn)
                || (adaptiveEscalated && mode == PerformancePreset.Standard);
            WhitelistEvaluation whitelist = EvaluateWhitelist(all);
            rogueTrustedPids = whitelist.Protected;
            int policyEpoch = FamilyPolicyEpoch;
            bool familyExempt = true;
            int rendererPid = 0;
            string activeGameRoot = null;
            GameProfile currentFamilyProfile = null;
            var libraryRoots = new List<string>();
            List<GameProfile> protectedProfiles;
            lock (sync)
            {
                if (activeDetection != null)
                {
                    rendererPid = activeDetection.RendererPid;
                    GameProfile configured = activeDetection.Profile == null
                        ? null : FindProfileLocked(activeDetection.Profile.Id);
                    currentFamilyProfile = configured == null ? null : configured.Clone();
                    familyExempt = FamilyBoundary.FamilyExemptFor(currentFamilyProfile);
                    if (familyExempt && activeDetection.Profile != null)
                        activeGameRoot = activeDetection.Profile.Root;
                }
                // 别的游戏的默认保护 不能被这个游戏的开关关掉
                // 根目录有重叠说不清归属时 一律偏向保护
                protectedProfiles = new List<GameProfile>();
                foreach (GameProfile profile in profiles)
                    if (FamilyBoundary.FamilyExemptFor(profile))
                    {
                        protectedProfiles.Add(profile.Clone());
                        if (FamilyBoundary.SafeFamilyDir(profile.Root)) libraryRoots.Add(profile.Root);
                    }
            }
            HashSet<int> protectedLibraryFamily = FamilyBoundary.CollectProtectedLibraryFamily(
                protectedProfiles, all, selfPid, selfSession, FamilyEvidence);
            bool haveSession = rendererPid > 0;
            // 名字说的是"家族豁免在这一局生效" 不是"有没有对局" 两者只在开关关着时不同
            bool familyExemptionActive = familyExempt && haveSession;
            // 这三份集合只有两种用途 开关开着时用来放行 关着时用来从可见窗口那条家族里扣掉
            //   专注和掌机档的可见窗口豁免整条是关的 扣无可扣 于是开关也关着时它们没人要
            //   这条路是对局里 500ms 一轮的热路径 能不算就不算 回到 2.1 的零开销
            bool needFamilySets = haveSession && (familyExempt || !aggressive);
            // 直接用白名单评估算好的那份 别再全量遍历一遍进程建第二份
            Dictionary<int, long> creations = needFamilySets ? whitelist.Creations : null;
            // 祖先链和后代都拿本轮快照的父子关系现算 不吃家族集合那 20 秒的滞后
            //   种子里补上 rendererPid 让对局中新生的子进程下一轮扫描就被认成家族
            //   只拿每 20 秒才刷新一次的 gamePids 当种子 同一个子进程会先被隔离再被放行
            HashSet<int> gameHostAncestors = needFamilySets
                ? FamilyBoundary.WalkAncestorChain(whitelist.Parents, rendererPid, selfPid, 24, creations)
                : EmptyPidSet;
            HashSet<int> familySeeds = null;
            if (needFamilySets)
            {
                familySeeds = new HashSet<int>(gamePids ?? EmptyPidSet);
                familySeeds.Add(rendererPid);
            }
            HashSet<int> gameDescendants = needFamilySets
                ? FamilyBoundary.WalkDescendants(whitelist.Parents, familySeeds, selfPid, 24, creations)
                : EmptyPidSet;
            // 家族豁免关着时 整个家族都不能当"用户正在看的窗口"那条家族的根
            //   光排掉种子不够 本体的父进程要是个可见的自带启动器 本体会顺着父子链被加回来
            //   它一回来 它的子进程也跟着进 宿主祖先自己也是家族成员 一并扣掉
            //   本体在上面按 rendererPid 已经放行 扣掉不影响游戏本身
            //   唯独没扣 pid == foreground 那条 那是"用户此刻正在操作的窗口"的保护
            //     跟家族豁免不是一回事 对局中前台通常就是本体 真弹出设置窗口也该让它跑
            HashSet<int> userFacingFamily = aggressive
                ? EmptyPidSet
                : CollectUserFacingFamily(foregroundPid, whitelist,
                    familyExempt ? 0 : rendererPid);
            FamilyBoundary.FilterUserFacingGameFamily(userFacingFamily, currentFamilyProfile, all,
                rendererPid, selfPid, selfSession, gamePids, gameDescendants,
                gameHostAncestors, FamilyEvidence);
            bool first;
            lock (sync) first = firstSweep;
            int done = 0, denied = 0, retrying = 0, rosterSkipped = 0;
            var live = new HashSet<int>();
            var pending = new List<BackgroundRequest>();
            var squeezeCandidates = new List<SqueezeCandidate>();

            foreach (ProcEntry p in all.Entries)
            {
                try
                {
                    int pid = p.Pid;
                    WhitelistProcessInfo processInfo;
                    whitelist.Processes.TryGetValue(pid, out processInfo);
                    live.Add(pid);
                    if (pid <= 4 || pid == selfPid) continue;

                    string nm = processInfo != null ? processInfo.Name : p.Name;

                    if (string.Equals(nm, selfName, StringComparison.OrdinalIgnoreCase))
                    {
                        if (core.Release(pid, SuppressReason.Background)) ReportUntrack(pid);
                        continue;
                    }

                    bool sameSession = processInfo != null && selfSession >= 0 && processInfo.Session == selfSession;
                    if (processInfo == null) sameSession = selfSession >= 0 && p.Session == selfSession;
                    if (!sameSession)
                    {
                        ReleaseBackgroundExemption(pid, nm, null);
                        continue;
                    }

                    // 候选保护先于一切后台写入 与家族豁免/模式/提优开关无关
                    // 只匹配本轮身份的 PID+创建时间+完整路径 不能把复用 PID 放行
                    long candidateCreation = processInfo != null ? processInfo.Creation : p.Creation;
                    string candidatePath = processInfo != null ? processInfo.Path : p.Path;
                    if (IsRendererHandoffProtected(pid, candidateCreation, candidatePath))
                    {
                        BackgroundReleaseState release = core.ReleaseBackgroundForRenderer(pid, candidateCreation, nm);
                        if (release == BackgroundReleaseState.Ready || release == BackgroundReleaseState.Gone)
                            ReportUntrack(pid);
                        continue;
                    }

                    bool boosted;
                    lock (sync) boosted = gameBoost.ContainsKey(pid);
                    if (boosted) continue;

                    bool white = whitelist.Protected.Contains(pid);
                    // 用户开关只取消当前档案的家族保护 不取消其他档案的保护
                    //   上面的 boosted 只在提优真的落地时为真 提优关掉或被反作弊挡住句柄时它是假的
                    //   所以这条按 pid 的判断不能省 否则那些机器上游戏本体会被当后台压掉
                    if (FamilyBoundary.IsGameOrWhitelistProtected(pid, rendererPid, white,
                        protectedLibraryFamily.Contains(pid), familyExempt, gamePids, gameDescendants))
                    {
                        if (core.Release(pid, SuppressReason.Background)) ReportUntrack(pid);
                        continue;
                    }

                    string ipath = processInfo != null ? processInfo.Path : null;
                    long creation = processInfo != null ? processInfo.Creation : 0;
                    long cpu = processInfo != null ? processInfo.Cpu : 0;
                    ulong io = processInfo != null ? processInfo.Io : 0;
                    if (processInfo == null)
                    {
                        IntPtr hq = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                        if (hq != IntPtr.Zero)
                        {
                            try
                            {
                                ipath = Native.ImagePath(hq);
                                Native.QueryProcessSample(hq, out creation, out cpu, out io);
                            }
                            finally { Native.CloseHandle(hq); }
                        }
                    }

                    // 同名的自己在上面按 selfName 已经排掉了 这里挡的是换了文件名的另一个构建
                    //   运行模式子进程绕过单实例锁 会被当普通后台进程压制 测量数据因此失真
                    if (SelfBuildGuard.IsOwnBuild(nm, ipath))
                    {
                        if (core.Release(pid, SuppressReason.Background)) ReportUntrack(pid);
                        continue;
                    }

                    if (!PerformanceScopeAllows(ipath))
                    {
                        ReleaseBackgroundExemption(pid, nm, null);
                        continue;
                    }
                    // 正在开麦的进程一律不碰 不分档位 名单点不到的语音软件靠这条兜底
                    if (TryProtectVoiceSession(pid, creation, nm)) continue;
                    // 集成在平台里的辅助进程跟随本局游戏的家族选择
                    // 独立的录屏宿主保持保护 直接复用快照
                    // 不读游戏模块 也不整个文件夹放行
                    if (TryProtectOverlayHost(pid, creation, nm, ipath, familyExempt)) continue;

                    string containRoot = FamilyBoundary.LibraryRootOf(ipath, libraryRoots);
                    if (containRoot == null && familyExempt) containRoot = activeGameRoot;
                    if (!FamilyBoundary.BasicBackgroundEligible(pid, selfPid, nm, ipath,
                        sameSession ? selfSession : -1, selfSession, foregroundPid,
                        userFacingFamily.Contains(pid), windowsPrefix,
                        familyExemptionActive && gameHostAncestors.Contains(pid),
                        containRoot, aggressive, familyExempt, creation))
                    {
                        ReleaseBackgroundExemption(pid, nm, null);
                        continue;
                    }

                    if (SelfProtectedRoster.Contains(nm))
                    {
                        ReleaseBackgroundExemption(pid, nm, null);
                        rosterSkipped++;
                        continue;
                    }

                    SuppressionLevel desired = EffSuppress
                        ? BackgroundLevel() : SuppressionLevel.None;
                    // 重压后台绑核的候选就是本轮要求隔离的这批 热度在 ApplyHeavySqueeze 里按快照时间推进
                    if (desired != SuppressionLevel.None && creation > 0)
                        squeezeCandidates.Add(new SqueezeCandidate
                            { Pid = pid, Creation = creation, Name = nm, Cpu = cpu });

                    string tracked = core.NameOf(pid);
                    if (tracked != null)
                    {
                        if (string.Equals(tracked, nm, StringComparison.OrdinalIgnoreCase))
                        {
                            if (desired != SuppressionLevel.None && core.HasReason(pid, SuppressReason.Background)
                                && core.LevelOf(pid, SuppressReason.Background) == desired)
                            {
                                bool reconciled = false;
                                if (!RunBackgroundPolicy(policyEpoch, delegate
                                    { reconciled = core.Reconcile(pid, nm, SuppressReason.Background); })) return;
                                if (reconciled) continue;
                            }
                        }
                        else ReportUntrack(pid);
                    }

                    if (desired == SuppressionLevel.None)
                    {
                        if (core.Release(pid, SuppressReason.Background)) ReportUntrack(pid);
                        continue;
                    }

                    if (!EffSuppress) continue;

                    pending.Add(new BackgroundRequest
                    {
                        Pid = pid,
                        Name = nm,
                        Creation = creation,
                        Cpu = cpu,
                        Desired = desired,
                        Previous = core.LevelOf(pid, SuppressReason.Background),
                        HadBackgroundReason = core.HasReason(pid, SuppressReason.Background)
                    });
                }
                catch { }
            }

            if (pending.Count > 1)
                pending.Sort(delegate (BackgroundRequest x, BackgroundRequest y)
                {
                    return y.Desired.CompareTo(x.Desired);
                });

            SuppressionCore.BatchResult batchResult = null;
            if (!RunBackgroundPolicy(policyEpoch, delegate
            {
                core.BeginBatch();
                try
                {
                    foreach (BackgroundRequest request in pending)
                    {
                        try { request.Result = core.Acquire(request.Pid, request.Name, SuppressReason.Background, null, request.Desired); }
                        catch { request.Result = AcquireResult.AlreadyProtected; }
                    }
                }
                finally { batchResult = core.EndBatch(); }
            })) return;

            foreach (BackgroundRequest request in pending)
                if ((request.Result == AcquireResult.NewlyThrottled || request.Result == AcquireResult.AlreadyThrottled)
                    && (batchResult == null || !batchResult.WasApplied(request.Pid)))
                {
                    string detail = batchResult != null ? batchResult.FailureOf(request.Pid) : "batch-missing";
                    if (detail == SuppressionCore.SelfProtectedDetail)
                    {
                        request.Result = AcquireResult.NewlyProtected;
                        continue;
                    }
                    request.Result = AcquireResult.ApplyFailed;
                    request.FailureDetail = detail;
                }

            foreach (BackgroundRequest request in pending)
            {
                if (request.Result == AcquireResult.NewlyThrottled)
                {
                    done++;
                    ReportTrack(request.Pid, request.Name);
                    if (!first) Logger.Log(Lang.T("log.gamemodesweep.1") + request.Name + "(pid " + request.Pid + ") "
                        + SuppressionLevelText.Of(request.Desired));
                }
                else if (request.Result == AcquireResult.AlreadyThrottled)
                {
                    if (!request.HadBackgroundReason) ReportTrack(request.Pid, request.Name);
                    if (!first && request.Previous != request.Desired)
                        Logger.Log(Lang.T("log.gamemodesweep.1") + request.Name + "(pid " + request.Pid + ") "
                            + SuppressionLevelText.Of(request.Previous) + " → "
                            + SuppressionLevelText.Of(request.Desired));
                }
                else if (request.Result == AcquireResult.NewlyProtected) denied++;
                else if (request.Result == AcquireResult.ApplyFailed)
                {
                    retrying++;
                    Logger.Log(Lang.T("log.gamemodesweep.1") + request.Name + "(pid " + request.Pid + ") "
                        + ApplyFailureText.Of(request.FailureDetail) + Lang.T("log.gamemodeboost.24"));
                }
            }

            // 重压后台绑核 在本轮隔离结果之上按热度决定亲和 开关关着时只负责放回
            if (!RunBackgroundPolicy(policyEpoch, delegate
                { ApplyHeavySqueeze(squeezeCandidates, live, all.TakenTicks); })) return;
            FamilyBoundary.PruneCatalogVerdicts(live);

            foreach (int pid in core.PidsWith(SuppressReason.Background))
                if (!live.Contains(pid)) { if (core.Release(pid, SuppressReason.Background)) ReportUntrack(pid); }

            if (first)
            {
                if (EffSuppress)
                {
                    string preset = mode == PerformancePreset.Competitive ? Lang.T("preset.competitive")
                        : mode == PerformancePreset.Extreme ? Lang.T("preset.extreme")
                        : mode == PerformancePreset.Handheld ? Lang.T("preset.handheld")
                        : mode == PerformancePreset.Custom ? Lang.T("preset.custom") : Lang.T("preset.standard");
                    bool strong = mode == PerformancePreset.Competitive
                        || mode == PerformancePreset.Extreme
                        || mode == PerformancePreset.Handheld
                        || (mode == PerformancePreset.Custom && aggressive);
                    // "后台归到后台核"那一段随移核一起删了 后台不再有专属核心
                    string policy = preset + (strong ? Lang.T("t.gamemodesweep.2") : Lang.T("t.gamemodesweep.3"))
                        + (aggressive ? Lang.T("t.gamemodesweep.5") : "");
                    Logger.Log(Lang.T("log.gamemodesweep.1") + policy
                        + (SuppressionCore.GpuDemoteEnabled ? Lang.T("log.gamemodesweep.6") : "")
                        + Lang.T("log.gamemodesweep.7") + done + Lang.T("log.gamemodesweep.8")
                        + (retrying > 0 ? " " + retrying + Lang.T("log.gamemodesweep.9") : "")
                        + (denied > 0 ? " " + denied + Lang.T("log.gamemodesweep.10") : "")
                        + (rosterSkipped > 0 ? " " + rosterSkipped + Lang.T("t.gamemodesweep.11") : ""));
                }
                lock (sync) firstSweep = false;
            }
        }

        private HashSet<int> CollectUserFacingFamily(
            int foregroundPid, WhitelistEvaluation whitelist, int excludeRootPid)
        {
            var roots = new HashSet<int>();
            HashSet<int> visible = GameSessionDetector.VisibleWindowPids(true);
            foreach (var pair in whitelist.Processes)
            {
                try
                {
                    int pid = pair.Key;
                    if (excludeRootPid > 0 && pid == excludeRootPid) continue;
                    WhitelistProcessInfo info = pair.Value;
                    if (selfSession < 0 || info.Session != selfSession) continue;
                    if (pid == foregroundPid || visible.Contains(pid))
                        roots.Add(pid);
                }
                catch { }
            }
            return FamilyBoundary.ExpandUserFacingFamily(
                whitelist.Parents, whitelist.Names, roots);
        }

        private bool TryProtectOverlayHost(int pid, long creation, string name, string imagePath, bool familyExempt)
        {
            // 显式的家族压制只摘掉集成平台那一层豁免
            // 独立的录屏和通信工具照旧保护
            if (pid <= 4 || !OverlayHostCatalog.ShouldProtectProcess(name, imagePath, familyExempt))
                return false;

            // 身份缺失或者 PID 被回收时 绝不能拿去释放另一个进程的记录
            // 把没解决的恢复欠账留着 换下一轮快照再试
            if (creation > 0 && core.ReleaseIfCreation(pid, SuppressReason.Background, creation))
                ReportUntrack(pid);
            return true;
        }

        // 音频采集会话在 Active 状态的进程 释放已有压制 每个 PID 本局只记一行日志
        private readonly HashSet<int> voiceExemptLogged = new HashSet<int>();

        private bool TryProtectVoiceSession(int pid, long creation, string name)
        {
            if (pid <= 4 || !VoiceSessionRoster.IsCapturing(pid, creation)) return false;
            // 知道创建时间就按身份释放 不知道就按 pid 释放 命中语音却留着旧压制不行
            if (creation > 0)
            {
                if (core.ReleaseIfCreation(pid, SuppressReason.Background, creation)) ReportUntrack(pid);
            }
            else ReleaseBackgroundExemption(pid, name, null);
            if (voiceExemptLogged.Add(pid))
                Logger.Log(Lang.T("log.voice.exempt") + name + " pid " + pid);
            return true;
        }

        private void ReleaseBackgroundExemption(int pid, string name, string reason)
        {
            if (core.Release(pid, SuppressReason.Background))
            {
                ReportUntrack(pid);
                if (!string.IsNullOrEmpty(reason)) Logger.Log(reason + Lang.T("log.gamemodesweep.12") + name + " pid " + pid);
            }
        }

    }
}
