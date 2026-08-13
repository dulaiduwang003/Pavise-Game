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
            public SuppressionLevel Desired;
            public SuppressionLevel Previous;
            public bool HadBackgroundReason;
            public AcquireResult Result;
            public string FailureDetail;
        }

        private static readonly HashSet<string> LauncherPlatforms = BuildLauncherPlatforms();

        private static HashSet<string> BuildLauncherPlatforms()
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in GamePlatformCatalog.PlatformShellNames()) names.Add(name);
            return names;
        }

        private static readonly HashSet<int> EmptyPidSet = new HashSet<int>();

        internal static bool IsAggressive(PerformancePreset mode, bool aggressiveOn)
        {
            return mode == PerformancePreset.Competitive
                || mode == PerformancePreset.Extreme
                || (mode == PerformancePreset.Custom && aggressiveOn);
        }

        internal static bool ResolvePowerPlanEnabled(PerformancePreset mode, bool manuallyEnabled)
        {
            return manuallyEnabled;
        }

        internal static SuppressionLevel ResolveBackgroundLevel(PerformancePreset mode, bool customAggressive,
            SuppressionLevel adaptive, bool safePartition)
        {
            if (mode == PerformancePreset.Competitive || mode == PerformancePreset.Extreme) return SuppressionLevel.Isolated;
            if (mode == PerformancePreset.Custom)
                return customAggressive ? SuppressionLevel.Isolated : SuppressionLevel.Eco;
            return adaptive > SuppressionLevel.Eco ? adaptive : SuppressionLevel.Eco;
        }

        internal static bool BasicBackgroundEligible(int pid, int self, string name, string path,
            int session, int ownerSession, int foreground, bool userFacingFamily, string windowsRoot,
            bool gameHostAncestor = false, string activeGameRoot = null, bool aggressive = false)
        {

            if (GameSessionDetector.IsAntiCheatLikeName(name)) return false;

            if (GamePlatformCatalog.IsPlatformProcess(name, path)) return false;
            if (NetAcceleratorCatalog.IsAcceleratorLikeName(name)) return false;
            if (IsInputChainProcess(name, path)) return false;
            if (gameHostAncestor) return false;
            if (UnderRoot(path, activeGameRoot)) return false;
            if (pid <= 4 || pid == self || session < 0 || session != ownerSession) return false;

            if (pid == foreground) return false;
            if (userFacingFamily) return false;
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(path)) return false;
            if (aggressive) return !IsCoreSystemProcess(name, path, windowsRoot);
            return string.IsNullOrEmpty(windowsRoot) || !path.StartsWith(windowsRoot, StringComparison.OrdinalIgnoreCase);
        }

        internal static bool UnderRoot(string path, string root)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(root)) return false;
            string prefix = root.TrimEnd('\\') + "\\";
            return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        internal static string LibraryRootOf(string path, IList<string> roots)
        {
            if (roots == null) return null;
            foreach (string root in roots)
            {
                if (root == null || root.TrimEnd('\\').Length <= 2) continue;
                if (UnderRoot(path, root)) return root;
            }
            return null;
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

        private static readonly string[] InputAudioKeywords =
        {
            "keyboard", "mouse", "hotkey", "keymap", "macro", "autohotkey",
            "hid", "input", "ime", "pinyin", "qqpy", "sogou", "sgtool", "wetype", "iflyime", "wubi",
            "audio", "sound", "voice", "headset", "nahimic", "realtek", "rtkaud", "creative",
            "logi", "lghub", "lcore", "razer", "synapse", "icue", "corsair",
            "steelseries", "armoury", "wooting", "keychron", "dareu", "rapoo",
            "bloody", "a4tech", "vgn", "langtu", "gamepp"
        };

        private static readonly string[] InputAudioDescWords =
        {
            "keyboard", "mouse", "headset", "earphone", "audio", "sound", "voice",
            "microphone", "input method", "ime", "hotkey", "macro", "peripheral",
            "gamepad", "joystick", "controller", "hid",
            "键盘", "鼠标", "耳机", "音频", "声卡", "麦克", "输入法", "按键", "外设", "手柄", "灯效", "搜狗", "讯飞输入"
        };

        internal static bool DescriptionLooksInputAudio(string description)
        {
            if (string.IsNullOrEmpty(description)) return false;
            string lower = description.ToLowerInvariant();
            foreach (string word in InputAudioDescWords)
                if (lower.Contains(word)) return true;
            return false;
        }

        internal static bool IsInputChainProcess(string name, string path)
        {
            return IsInputAudioLike(name) || FileLooksInputAudio(path);
        }

        private static readonly object descCacheSync = new object();
        private static readonly Dictionary<string, bool> descCache =
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        private static bool FileLooksInputAudio(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            lock (descCacheSync)
            {
                bool cached;
                if (descCache.TryGetValue(path, out cached)) return cached;
            }
            bool hit = false;
            try
            {
                var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
                hit = DescriptionLooksInputAudio(info.FileDescription)
                    || DescriptionLooksInputAudio(info.ProductName)
                    || DescriptionLooksInputAudio(info.CompanyName);
            }
            catch { }
            lock (descCacheSync)
            {
                if (descCache.Count > 512) descCache.Clear();
                descCache[path] = hit;
            }
            return hit;
        }

        internal static bool IsInputAudioLike(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string lower = name.ToLowerInvariant();
            foreach (string keyword in InputAudioKeywords)
                if (lower.Contains(keyword)) return true;
            return false;
        }

        private void Sweep(ProcessSnapshot all, HashSet<int> gamePids)
        {

            lock (whiteEvalSync)
                SweepWithStableWhitelist(all, gamePids);
        }

        private void SweepWithStableWhitelist(
            ProcessSnapshot all, HashSet<int> gamePids)
        {
            PerformancePreset mode = ActivePreset;
            int foregroundPid = GameSessionDetector.ForegroundPid();
            bool aggressive = IsAggressive(mode, aggressiveOn);
            WhitelistEvaluation whitelist = EvaluateWhitelist(all);
            HashSet<int> userFacingFamily = aggressive
                ? CollectForegroundFamily(foregroundPid, whitelist)
                : CollectUserFacingFamily(foregroundPid, whitelist);
            bool safePartition = CpuTopology.HasSafeBackgroundPartition();

            int rendererPid = 0;
            string activeGameRoot = null;
            var libraryRoots = new List<string>();
            lock (sync)
            {
                if (activeDetection != null)
                {
                    rendererPid = activeDetection.RendererPid;
                    if (activeDetection.Profile != null) activeGameRoot = activeDetection.Profile.Root;
                }
                foreach (GameProfile p in profiles)
                    if (!string.IsNullOrEmpty(p.Root)) libraryRoots.Add(p.Root);
            }
            bool gameSessionActive = rendererPid > 0;
            HashSet<int> gameHostAncestors = gameSessionActive
                ? WalkAncestorChain(whitelist.Parents, rendererPid, selfPid, 24)
                : EmptyPidSet;
            HashSet<int> gameDescendants = gameSessionActive
                ? WalkDescendants(whitelist.Parents, gamePids, selfPid, 24)
                : EmptyPidSet;

            bool first;
            lock (sync) first = firstSweep;
            int done = 0, denied = 0, retrying = 0, rosterSkipped = 0;
            var live = new HashSet<int>();
            var pending = new List<BackgroundRequest>();

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

                    bool boosted;
                    lock (sync) boosted = gameBoost.ContainsKey(pid);
                    if (boosted) continue;

                    bool white = whitelist.Protected.Contains(pid);
                    if (white || gamePids.Contains(pid) || gameDescendants.Contains(pid))
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
                    string containRoot = LibraryRootOf(ipath, libraryRoots);
                    if (containRoot == null) containRoot = activeGameRoot;
                    if (!BasicBackgroundEligible(pid, selfPid, nm, ipath,
                        sameSession ? selfSession : -1, selfSession, foregroundPid,
                        userFacingFamily.Contains(pid), windowsPrefix,
                        gameHostAncestors.Contains(pid) || knownLauncherDuringSession, containRoot, aggressive))
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

                    SuppressionLevel adaptive = SuppressionLevel.None;
                    if (mode == PerformancePreset.Standard && creation > 0)
                        adaptive = pressure.Observe(pid, nm, creation, cpu, io, DateTime.UtcNow.Ticks, mode);
                    else pressure.Forget(pid);
                    SuppressionLevel desired = ResolveBackgroundLevel(mode, aggressive, adaptive, safePartition);
                    if (!EffSuppress) desired = SuppressionLevel.None;

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

                    if (!EffSuppress) continue;

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
                    if (!first) Logger.Log("后台压制 " + request.Name + "(pid " + request.Pid + ") "
                        + SuppressionLevelText.Of(request.Desired));
                }
                else if (request.Result == AcquireResult.AlreadyThrottled)
                {
                    if (!request.HadBackgroundReason) ReportTrack(request.Pid, request.Name);
                    if (!first && request.Previous != request.Desired)
                        Logger.Log("后台压制 " + request.Name + "(pid " + request.Pid + ") "
                            + SuppressionLevelText.Of(request.Previous) + " → "
                            + SuppressionLevelText.Of(request.Desired));
                }
                else if (request.Result == AcquireResult.NewlyProtected) denied++;
                else if (request.Result == AcquireResult.ApplyFailed)
                {
                    retrying++;
                    Logger.Log("后台压制 " + request.Name + "(pid " + request.Pid + ") "
                        + ApplyFailureText.Of(request.FailureDetail) + " 下一轮重试");
                }
            }

            foreach (int pid in core.PidsWith(SuppressReason.Background))
                if (!live.Contains(pid)) { if (core.Release(pid, SuppressReason.Background)) ReportUntrack(pid); }

            pressure.Prune(live);

            if (first)
            {
                if (EffSuppress)
                {
                    string preset = mode == PerformancePreset.Extreme ? "极限"
                        : mode == PerformancePreset.Competitive ? "竞技"
                        : mode == PerformancePreset.Custom ? "自定义" : "常规";
                    bool strong = mode == PerformancePreset.Competitive
                        || mode == PerformancePreset.Extreme
                        || (mode == PerformancePreset.Custom && aggressive);
                    string policy = preset + (strong ? " 强力压制" : " 省电压制")
                        + (strong && safePartition ? " 后台归到后台核" : "")
                        + (aggressive ? " 只放行前台程序" : "");
                    Logger.Log("后台压制 " + policy
                        + (SuppressionCore.GpuDemoteEnabled ? " 显卡让位" : "")
                        + " 首轮 " + done + " 个"
                        + (retrying > 0 ? " " + retrying + " 个待重试" : "")
                        + (denied > 0 ? " " + denied + " 个受保护跳过" : "")
                        + (rosterSkipped > 0 ? " " + rosterSkipped + " 个免压名单跳过" : ""));
                }
                lock (sync) firstSweep = false;
            }
        }

        private HashSet<int> CollectForegroundFamily(
            int foregroundPid, WhitelistEvaluation whitelist)
        {
            var roots = new HashSet<int>();
            if (foregroundPid > 4 && foregroundPid != selfPid)
            {
                WhitelistProcessInfo info;
                if (!whitelist.Processes.TryGetValue(foregroundPid, out info)
                    || selfSession < 0 || info.Session == selfSession)
                    roots.Add(foregroundPid);
            }
            return ExpandUserFacingFamily(
                whitelist.Parents, whitelist.Names, roots);
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
                if (!string.IsNullOrEmpty(reason)) Logger.Log(reason + " 已恢复 " + name + " pid " + pid);
            }
            pressure.Forget(pid);
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

        internal static bool IsKnownLauncherShell(string name)
        {
            return !string.IsNullOrEmpty(name) && LauncherPlatforms.Contains(name);
        }

        internal static HashSet<int> WalkDescendants(
            Dictionary<int, int> parents, ICollection<int> rootPids, int selfPid, int maxDepth)
        {
            var result = new HashSet<int>();
            if (parents == null || rootPids == null || rootPids.Count == 0) return result;
            var roots = new HashSet<int>(rootPids);
            foreach (KeyValuePair<int, int> kv in parents)
            {
                int pid = kv.Key;
                if (pid <= 4 || pid == selfPid || roots.Contains(pid) || result.Contains(pid)) continue;
                int current = pid;
                for (int depth = 0; depth < maxDepth; depth++)
                {
                    int parent;
                    if (!parents.TryGetValue(current, out parent) || parent <= 4 || parent == current) break;
                    if (roots.Contains(parent)) { result.Add(pid); break; }
                    if (parent == selfPid) break;
                    current = parent;
                }
            }
            return result;
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
