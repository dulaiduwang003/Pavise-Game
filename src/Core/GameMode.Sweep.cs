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
            if (!string.IsNullOrEmpty(name) && AntiCheatCatalog.IsKnownProcess(name)) return false;
            if (gameHostAncestor) return false;
            if (!string.IsNullOrEmpty(activeGameRoot) && !string.IsNullOrEmpty(path)
                && path.StartsWith(activeGameRoot, StringComparison.OrdinalIgnoreCase)) return false;
            if (pid <= 4 || pid == self || session < 0 || session != ownerSession) return false;
            if (!aggressive && (pid == foreground || userFacingFamily)) return false;
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(path)) return false;
            if (aggressive) return !IsCoreSystemProcess(name, path, windowsRoot);
            return string.IsNullOrEmpty(windowsRoot) || !path.StartsWith(windowsRoot, StringComparison.OrdinalIgnoreCase);
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
            PerformancePreset mode = ActivePreset;
            int foregroundPid = GameSessionDetector.ForegroundPid();
            bool aggressive = IsAggressive(mode, aggressiveOn);
            HashSet<int> userFacingFamily = aggressive ? EmptyPidSet : CollectUserFacingFamily(all, foregroundPid);
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
                ? WalkAncestorChain(BuildParentMap(all), rendererPid, selfPid, 24)
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
                    live.Add(pid);
                    if (pid <= 4 || pid == selfPid) continue;

                    string nm = p.ProcessName;

                    if (string.Equals(nm, selfName, StringComparison.OrdinalIgnoreCase))
                    {
                        if (core.Release(pid, SuppressReason.Background)) ReportUntrack(pid);
                        bool thawed = freezer.Restore(pid);
                        if (thawed) Logger.Log("Aegis 自身保护：已解冻看门狗 (pid " + pid + ")");
                        continue;
                    }

                    bool sameSession = false;
                    try { sameSession = selfSession >= 0 && p.SessionId == selfSession; } catch { }
                    if (!sameSession)
                    {
                        ReleaseBackgroundExemption(pid, nm, null);
                        continue;
                    }

                    bool boosted;
                    lock (sync) boosted = gameBoost.ContainsKey(pid);
                    if (boosted) continue;

                    bool white;
                    lock (sync) white = whiteSet.Contains(nm);
                    if (white || gamePids.Contains(pid))
                    {
                        if (core.Release(pid, SuppressReason.Background)) ReportUntrack(pid);
                        bool thawed = freezer.Restore(pid);
                        if (thawed) Logger.Log("游戏/白名单豁免：已解冻 " + nm + " (pid " + pid + ")");
                        continue;
                    }

                    string ipath = null;
                    long creation = 0, cpu = 0; ulong io = 0;
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
                 
                    bool knownLauncherDuringSession = gameSessionActive && IsKnownLauncherShell(nm);
                    if (!BasicBackgroundEligible(pid, selfPid, nm, ipath,
                        sameSession ? selfSession : -1, selfSession, foregroundPid,
                        userFacingFamily.Contains(pid), windowsPrefix,
                        gameHostAncestors.Contains(pid) || knownLauncherDuringSession, activeGameRoot, aggressive))
                    {
                        ReleaseBackgroundExemption(pid, nm, null);
                        continue;
                    }

                    if (freezer.IsFrozen(pid)) continue;

                    if (deepFreezeOn && creation > 0 && CanDeepFreeze(p, nm, ipath))
                    {
                        InterferenceResult hot = interference.Observe(pid, nm, creation, cpu, io, DateTime.UtcNow.Ticks);
                        if (hot.Kind != InterferenceKind.None && freezer.Freeze(pid, nm, hot.Describe()))
                        {
                            string why = hot.Describe();
                            ReportFreeze(pid, nm, why);
                            interference.Forget(pid);
                            Logger.Log("极限模式：冻结高干扰后台 " + nm + " (pid " + pid + ", " + why + ")");
                            continue;
                        }
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
                                && core.IsThrottled(pid) && core.LevelOf(pid, SuppressReason.Background) == desired)
                            {
                                core.Reapply(pid, nm, SuppressReason.Background);
                                continue;
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

            core.BeginBatch();
            try
            {
                foreach (BackgroundRequest request in pending)
                {
                    try { request.Result = core.Acquire(request.Pid, request.Name, SuppressReason.Background, null, request.Desired); }
                    catch { request.Result = AcquireResult.AlreadyProtected; }
                }
            }
            finally { core.EndBatch(); }

            foreach (BackgroundRequest request in pending)
                if (!core.ConsumeBatchApplyResult(request.Pid)) request.Result = AcquireResult.ApplyFailed;

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

            freezer.Prune(live);
            interference.Prune(live);
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
                else if (deepFreezeOn)
                    Logger.Log("极限模式：首轮资源基线已建立，持续高 CPU / IO 的用户后台将在下一采样窗口冻结");
                lock (sync) firstSweep = false;
            }
        }

        private HashSet<int> CollectUserFacingFamily(Process[] all, int foregroundPid)
        {
            var parents = new Dictionary<int, int>();
            var names = new Dictionary<int, string>();
            var roots = new HashSet<int>();
            foreach (Process p in all)
            {
                try
                {
                    int pid = p.Id;
                    if (selfSession < 0 || p.SessionId != selfSession) continue;
                    string name = p.ProcessName;
                    names[pid] = name;
                    bool isLauncher = name != null && LauncherPlatforms.Contains(name);
                    if (pid == foregroundPid || (!isLauncher && GameSessionDetector.HasUserFacingWindow(p))) roots.Add(pid);
                    IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                    if (h == IntPtr.Zero) continue;
                    try { parents[pid] = Native.ParentProcessId(h); }
                    finally { Native.CloseHandle(h); }
                }
                catch { }
            }
            return ExpandUserFacingFamily(parents, names, roots);
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
            if (freezer.Restore(pid) && !string.IsNullOrEmpty(reason))
                Logger.Log(reason + "：已解冻 " + name + " (pid " + pid + ")");
            pressure.Forget(pid);
            interference.Forget(pid);
        }

        private bool CanDeepFreeze(Process p, string name, string path)
        {
            if (string.IsNullOrEmpty(path)
                || string.Equals(name, "Aegis", StringComparison.OrdinalIgnoreCase)) return false;
            try
            {
                if (selfSession < 0 || p.SessionId != selfSession) return false;
                if (path.StartsWith(windowsPrefix, StringComparison.OrdinalIgnoreCase)) return false;
            }
            catch { return false; }
            return true;
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
                if (path.StartsWith(d, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private Dictionary<int, int> BuildParentMap(Process[] all)
        {
            var parents = new Dictionary<int, int>();
            foreach (Process p in all)
            {
                try
                {
                    int pid = p.Id;
                    if (selfSession < 0 || p.SessionId != selfSession) continue;
                    IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                    if (h == IntPtr.Zero) continue;
                    try { parents[pid] = Native.ParentProcessId(h); }
                    finally { Native.CloseHandle(h); }
                }
                catch { }
            }
            return parents;
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
