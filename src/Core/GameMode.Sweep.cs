// @author bdth 2074055628@qq.com
// 文件用途 扫描并压制游戏之外的后台进程

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace AegisApp
{
    internal partial class GameMode
    {
        private HashSet<string> performanceSuppressionScope;

        // PerfLab 可运行真实 Sweep，但只能修改明确的合成负载。正式入口不调用
        // 此方法，因此 null 仍表示原有的完整策略范围。
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

        private bool PerformanceScopeAllows(string imagePath)
        {
            lock (sync)
            {
                if (performanceSuppressionScope == null) return true;
                return performanceSuppressionScope.Contains(
                    WhitelistRule.NormalizeImagePath(imagePath));
            }
        }

        private sealed class BackgroundRequest
        {
            public int Pid;
            public string Name;
            public SuppressionLevel Desired;
            public SuppressionLevel Previous;
            public bool HadBackgroundReason;
            public AcquireResult Result;
        }

        private static readonly HashSet<string> LauncherPlatforms = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "wegame", "wegame_env", "wegameclient", "steam", "steamwebhelper",
            "epicgameslauncher", "battle.net", "galaxyclient", "ubisoftconnect"
        };

        private static readonly HashSet<int> EmptyPidSet = new HashSet<int>();

        internal static bool IsAggressive(PerformancePreset mode, bool aggressiveOn)
        {
            return mode == PerformancePreset.Competitive
                || (mode == PerformancePreset.Custom && aggressiveOn);
        }

        internal static SuppressionLevel ResolveBackgroundLevel(PerformancePreset mode, bool customStrict,
            SuppressionLevel adaptive, bool safePartition)
        {
            if (mode == PerformancePreset.Competitive) return SuppressionLevel.Isolated;
            if (mode == PerformancePreset.Custom)
                return customStrict ? SuppressionLevel.Isolated : SuppressionLevel.Eco;
            return adaptive > SuppressionLevel.Eco ? adaptive : SuppressionLevel.Eco;
        }

        internal static bool BasicBackgroundEligible(int pid, int self, string name, string path,
            int session, int ownerSession, int foreground, bool userFacingFamily, string windowsRoot,
            bool gameHostAncestor = false, string activeGameRoot = null, bool aggressive = false)
        {
            // 必须用与检测器同一套（更宽的）判定：AntiCheatCatalog 是精确名单，
            // IsAntiCheatLikeName 在名单之外还认 anticheat/sguard/battleye 等子串。
            // 两边宽度不一致的话，会出现"检测器认定是反作弊、压制器照压不误"的缝隙，
            // 而被压的反作弊心跳超时 = 掉线甚至被判异常。宁可漏放也绝不错压。
            if (GameSessionDetector.IsAntiCheatLikeName(name)) return false;
            if (gameHostAncestor) return false;
            if (UnderRoot(path, activeGameRoot)) return false;
            if (pid <= 4 || pid == self || session < 0 || session != ownerSession) return false;
            if (!aggressive && (pid == foreground || userFacingFamily)) return false;
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(path)) return false;
            if (aggressive) return !IsCoreSystemProcess(name, path, windowsRoot);
            return string.IsNullOrEmpty(windowsRoot) || !path.StartsWith(windowsRoot, StringComparison.OrdinalIgnoreCase);
        }

        // 目录包含判断必须按路径分段锚定：裸 StartsWith 会让根目录 D:\Games\Apex
        // 把兄弟目录 D:\Games\ApexBackup 也算进来，从而错误豁免不相干的进程。
        internal static bool UnderRoot(string path, string root)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(root)) return false;
            string prefix = root.TrimEnd('\\') + "\\";
            return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        private static readonly HashSet<string> CoreSystemProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "smss", "csrss", "wininit", "winlogon", "services", "lsass",
            "svchost", "dwm", "audiodg", "fontdrvhost"
        };

        private static bool IsCoreSystemProcess(string name, string path, string windowsRoot)
        {
            return CoreSystemProcesses.Contains(name)
                && !string.IsNullOrEmpty(windowsRoot) && !string.IsNullOrEmpty(path)
                && path.StartsWith(windowsRoot, StringComparison.OrdinalIgnoreCase);
        }

        private void Sweep(Process[] all, HashSet<int> gamePids)
        {
            // 白名单修改和策略应用必须线性化。否则 UI 新增规则并立即恢复后，
            // 一个已拿到旧快照的 Sweep 仍可能把同一进程重新压制到下一轮校准。
            lock (whiteEvalSync)
                SweepWithStableWhitelist(all, gamePids);
        }

        private void SweepWithStableWhitelist(
            Process[] all, HashSet<int> gamePids)
        {
            PerformancePreset mode = ActivePreset;
            int foregroundPid = GameSessionDetector.ForegroundPid();
            bool aggressive = IsAggressive(mode, aggressiveOn);
            WhitelistEvaluation whitelist = EvaluateWhitelist(all);
            HashSet<int> userFacingFamily = aggressive ? EmptyPidSet
                : CollectUserFacingFamily(foregroundPid, whitelist);
            bool safePartition = CpuTopology.HasSafeBackgroundPartition();

            int rendererPid = 0;
            string activeGameRoot = null;
            lock (sync)
            {
                if (activeDetection != null)
                {
                    rendererPid = activeDetection.RendererPid;
                    if (activeDetection.Profile != null) activeGameRoot = activeDetection.Profile.Root;
                }
            }
            bool gameSessionActive = rendererPid > 0;
            HashSet<int> gameHostAncestors = gameSessionActive
                ? WalkAncestorChain(whitelist.Parents, rendererPid, selfPid, 24)
                : EmptyPidSet;

            bool first;
            lock (sync) first = firstSweep;
            int done = 0, fail = 0;
            var live = new HashSet<int>();
            var pending = new List<BackgroundRequest>();

            foreach (Process p in all)
            {
                try
                {
                    int pid = p.Id;
                    WhitelistProcessInfo processInfo;
                    whitelist.Processes.TryGetValue(pid, out processInfo);
                    live.Add(pid);
                    if (pid <= 4 || pid == selfPid) continue;

                    string nm = processInfo != null ? processInfo.Name : p.ProcessName;

                    if (string.Equals(nm, selfName, StringComparison.OrdinalIgnoreCase))
                    {
                        if (core.Release(pid, SuppressReason.Background)) ReportUntrack(pid);
                        continue;
                    }

                    bool sameSession = processInfo != null && selfSession >= 0 && processInfo.Session == selfSession;
                    if (processInfo == null) { try { sameSession = selfSession >= 0 && p.SessionId == selfSession; } catch { } }
                    if (!sameSession)
                    {
                        ReleaseBackgroundExemption(pid, nm, null);
                        continue;
                    }

                    bool boosted;
                    lock (sync) boosted = gameBoost.ContainsKey(pid);
                    if (boosted) continue;

                    bool white = whitelist.Protected.Contains(pid);
                    if (white || gamePids.Contains(pid))
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
                 
                    bool knownLauncherDuringSession = gameSessionActive && IsKnownLauncherShell(nm);
                    if (!PerformanceScopeAllows(ipath))
                    {
                        ReleaseBackgroundExemption(pid, nm, null);
                        continue;
                    }
                    if (!BasicBackgroundEligible(pid, selfPid, nm, ipath,
                        sameSession ? selfSession : -1, selfSession, foregroundPid,
                        userFacingFamily.Contains(pid), windowsPrefix,
                        gameHostAncestors.Contains(pid) || knownLauncherDuringSession, activeGameRoot, aggressive))
                    {
                        ReleaseBackgroundExemption(pid, nm, null);
                        continue;
                    }

                    SuppressionLevel adaptive = SuppressionLevel.None;
                    if (mode == PerformancePreset.Standard && creation > 0)
                        adaptive = pressure.Observe(pid, nm, creation, cpu, io, DateTime.UtcNow.Ticks, mode);
                    else pressure.Forget(pid);
                    SuppressionLevel desired = ResolveBackgroundLevel(mode, strictCoreOn, adaptive, safePartition);
                    if (!bgSuppressOn) desired = SuppressionLevel.None;

                    string tracked = core.NameOf(pid);
                    if (tracked != null)
                    {
                        if (string.Equals(tracked, nm, StringComparison.OrdinalIgnoreCase))
                        {
                            if (desired != SuppressionLevel.None && core.HasReason(pid, SuppressReason.Background)
                                && core.LevelOf(pid, SuppressReason.Background) == desired)
                            {
                                if (core.Reconcile(pid, nm, SuppressReason.Background)) continue;
                            }
                        }
                        else ReportUntrack(pid);
                    }

                    if (desired == SuppressionLevel.None)
                    {
                        if (core.Release(pid, SuppressReason.Background)) ReportUntrack(pid);
                        continue;
                    }

                    if (!bgSuppressOn) continue;

                    pending.Add(new BackgroundRequest
                    {
                        Pid = pid,
                        Name = nm,
                        Desired = desired,
                        Previous = core.LevelOf(pid, SuppressReason.Background),
                        HadBackgroundReason = core.HasReason(pid, SuppressReason.Background)
                    });
                }
                catch { }
            }

            SuppressionCore.BatchResult batchResult = null;
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

            foreach (BackgroundRequest request in pending)
                if ((request.Result == AcquireResult.NewlyThrottled || request.Result == AcquireResult.AlreadyThrottled)
                    && (batchResult == null || !batchResult.WasApplied(request.Pid)))
                    request.Result = AcquireResult.ApplyFailed;

            foreach (BackgroundRequest request in pending)
            {
                if (request.Result == AcquireResult.NewlyThrottled)
                {
                    done++;
                    ReportTrack(request.Pid, request.Name);
                    if (trimWs && mode == PerformancePreset.Custom && request.Desired >= SuppressionLevel.Restrained)
                        TrimWorkingSetOf(request.Pid);
                    if (!first) Logger.Log("后台策略：" + request.Name + " (pid " + request.Pid + ") → " + request.Desired);
                }
                else if (request.Result == AcquireResult.AlreadyThrottled)
                {
                    if (!request.HadBackgroundReason) ReportTrack(request.Pid, request.Name);
                    if (!first && request.Previous != request.Desired)
                        Logger.Log("后台策略：" + request.Name + " (pid " + request.Pid + ") " + request.Previous + " → " + request.Desired);
                }
                else if (request.Result == AcquireResult.NewlyProtected) fail++;
                else if (request.Result == AcquireResult.ApplyFailed)
                {
                    fail++;
                    Logger.Log("后台策略写入未完全生效：" + request.Name + " (pid " + request.Pid + ")，保留快照并将在下一轮重试");
                }
            }

            foreach (int pid in core.PidsWith(SuppressReason.Background))
                if (!live.Contains(pid)) { if (core.Release(pid, SuppressReason.Background)) ReportUntrack(pid); }

            pressure.Prune(live);

            if (first)
            {
                if (bgSuppressOn)
                {
                    string aggressiveNote = aggressive ? "，前台/可见窗口不再豁免，仅核心系统服务例外" : "";
                    string policy = mode == PerformancePreset.Competitive
                        ? (safePartition ? "竞技确定性隔离（Idle + EcoQoS + 锁核" + aggressiveNote + "）"
                            : "竞技隔离（核心数不足，Idle + EcoQoS 降优先级，不锁核" + aggressiveNote + "）")
                        : (mode == PerformancePreset.Custom
                            ? (strictCoreOn
                                ? (safePartition ? "自定义立即隔离（Idle + EcoQoS + 锁核" + aggressiveNote + "）" : "自定义隔离（核心数不足，Idle + EcoQoS，不锁核" + aggressiveNote + "）")
                                : "自定义全局 Eco" + aggressiveNote)
                            : "常规全局 Eco，持续大户再升级");
                    Logger.Log("后台策略：" + policy + "，首轮处理 " + done + " 个用户后台"
                        + (fail > 0 ? "（" + fail + " 个受保护进程已跳过）" : ""));
                }
                lock (sync) firstSweep = false;
            }
        }

        private HashSet<int> CollectUserFacingFamily(
            int foregroundPid, WhitelistEvaluation whitelist)
        {
            var roots = new HashSet<int>();
            HashSet<int> visible = GameSessionDetector.VisibleWindowPids(true);
            foreach (var pair in whitelist.Processes)
            {
                try
                {
                    int pid = pair.Key;
                    WhitelistProcessInfo info = pair.Value;
                    if (selfSession < 0 || info.Session != selfSession) continue;
                    string name = info.Name;
                    bool isLauncher = name != null && LauncherPlatforms.Contains(name);
                    if (pid == foregroundPid || (!isLauncher && visible.Contains(pid)))
                        roots.Add(pid);
                }
                catch { }
            }
            return ExpandUserFacingFamily(
                whitelist.Parents, whitelist.Names, roots);
        }

        internal static HashSet<int> ExpandUserFacingFamily(Dictionary<int, int> parents,
            Dictionary<int, string> names, HashSet<int> roots)
        {
            var result = new HashSet<int>(roots ?? new HashSet<int>());
            var rootNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (names != null)
                foreach (int pid in result)
                {
                    string name;
                    if (names.TryGetValue(pid, out name) && !string.IsNullOrEmpty(name)) rootNames.Add(name);
                }
            if (names != null)
                foreach (var pair in names)
                    if (rootNames.Contains(pair.Value)) result.Add(pair.Key);

            bool changed;
            do
            {
                changed = false;
                if (parents == null || names == null) break;
                foreach (var pair in parents)
                {
                    if (result.Contains(pair.Key) || !result.Contains(pair.Value)) continue;
                    string parentName;
                    if (!names.TryGetValue(pair.Value, out parentName) || IsShellProcess(parentName)) continue;
                    result.Add(pair.Key);
                    changed = true;
                }
            }
            while (changed);
            return result;
        }

        private static bool IsShellProcess(string name)
        {
            return string.Equals(name, "explorer", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "applicationframehost", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "shellexperiencehost", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "startmenuexperiencehost", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "searchhost", StringComparison.OrdinalIgnoreCase);
        }

        private void ReleaseBackgroundExemption(int pid, string name, string reason)
        {
            if (core.Release(pid, SuppressReason.Background))
            {
                ReportUntrack(pid);
                if (!string.IsNullOrEmpty(reason)) Logger.Log(reason + "：已恢复 " + name + " (pid " + pid + ")");
            }
            pressure.Forget(pid);
        }

        private static void TrimWorkingSetOf(int pid)
        {
            IntPtr hq = Native.OpenProcess(Native.PROCESS_SET_QUOTA, false, pid);
            if (hq == IntPtr.Zero) return;
            try { Native.SetProcessWorkingSetSize(hq, (IntPtr)(-1), (IntPtr)(-1)); }
            finally { Native.CloseHandle(hq); }
        }

        private static readonly Environment.SpecialFolder[] UnsafeRoots =
        {
            Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
            Environment.SpecialFolder.CommonProgramFiles, Environment.SpecialFolder.CommonProgramFilesX86,
            Environment.SpecialFolder.Windows, Environment.SpecialFolder.System, Environment.SpecialFolder.SystemX86,
            Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.DesktopDirectory,
            Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolder.ApplicationData,
            Environment.SpecialFolder.LocalApplicationData
        };

        private static bool SafeFamilyDir(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return false;
            string d = dir.TrimEnd('\\');
            if (d.Length <= 2) return false;
            foreach (Environment.SpecialFolder sf in UnsafeRoots)
            {
                string sd;
                try { sd = Environment.GetFolderPath(sf); } catch { continue; }
                if (!string.IsNullOrEmpty(sd) && string.Equals(d, sd.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            return true;
        }

        internal static bool IsGameFamily(string path, HashSet<string> gameDirs)
        {
            return IsGameFamily(path, gameDirs, null);
        }

        internal static bool IsGameFamily(string path, HashSet<string> gameDirs, string processName)
        {
            if (string.IsNullOrEmpty(path) || gameDirs.Count == 0) return false;
            if (GameSessionDetector.IsNonGameRole(processName, path)) return false;
            foreach (string d in gameDirs)
                if (UnderRoot(path, d)) return true;
            return false;
        }

        // 从当前活跃渲染进程往上找它的启动器链条（父进程、祖父进程……）。
        // 不认任何具体平台的名字——不管是哪家启动器，只要它现在真的是这局
        // 游戏进程树上的祖先，就在会话存续期间豁免压制；遇到父进程已退出、
        // 跨会话、绕回自身或起点这些断链情况就停，防止把无关进程也豁免掉。
        internal static bool IsKnownLauncherShell(string name)
        {
            return !string.IsNullOrEmpty(name) && LauncherPlatforms.Contains(name);
        }

        internal static HashSet<int> WalkAncestorChain(Dictionary<int, int> parents, int startPid, int selfPid, int maxHops)
        {
            var result = new HashSet<int>();
            if (parents == null || startPid <= 4) return result;
            int current = startPid;
            for (int hop = 0; hop < maxHops; hop++)
            {
                int parent;
                if (!parents.TryGetValue(current, out parent)) break;
                if (parent <= 4 || parent == selfPid || parent == startPid || result.Contains(parent)) break;
                result.Add(parent);
                current = parent;
            }
            return result;
        }
    }
}
