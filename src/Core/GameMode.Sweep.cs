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
                || (mode == PerformancePreset.Custom && aggressiveOn);
        }

        internal static bool ResolvePowerPlanEnabled(PerformancePreset mode, bool manuallyEnabled)
        {
            return manuallyEnabled;
        }

        // 2.0 起后台只有两态 通过保护边界的一律直接隔离 不再看热度也不再从省电逐级往上爬
        //   不等它先吃十几秒资源才动手 对局一开始枚举一遍全压下去
        //   冷进程也压 因为不再改亲和性 冷进程没有就绪线程时本来就不耗 CPU
        //   偶尔醒来也能在任何一个没有更高优先级工作的核上跑 不会被挤着排队
        //   唯一还在把关的是 BasicBackgroundEligible 那道保护边界
        //   反作弊 系统核心 输入音频外设链 加速器 硬件控制 白名单 其它登录账户一律不碰
        //   2.1 起游戏家族不再整族豁免 只有渲染进程本体放行 平台客户端与启动器外壳照压
        //   档位差异不再体现在压制强度 只体现在哪些进程有资格被碰
        internal static SuppressionLevel BackgroundLevel()
        {
            return SuppressionLevel.Isolated;
        }

        internal static bool BasicBackgroundEligible(int pid, int self, string name, string path,
            int session, int ownerSession, int foreground, bool userFacingFamily, string windowsRoot,
            bool aggressive = false)
        {
            // 只有渲染进程本体豁免 其余一律压 它在调用方按 rendererPid 就已放行 到不了这里
            //   平台与启动器外壳 宿主祖先链 游戏根目录下的常驻进程 游戏派生的子进程全部照压
            //   这些客户端在对局中仍持续占用 CPU 放过它们等于把最大的一份后台开销留在场上
            //   降优先级不等于杀进程 Steam 和战网那类把客户端当 DRM 的 进程仍在运行 不受影响
            //   下面四条是安全边界 不受上述规则影响
            //   反作弊被压会心跳超时掉线 加速器被压会断流 输入音频外设链被压会卡鼠标和丢声音
            if (AntiCheatCatalog.IsAntiCheatLikeName(name)) return false;
            if (NetAcceleratorCatalog.IsAcceleratorLikeName(name)) return false;
            if (PeripheralCatalog.IsInputChainProcess(name, path)) return false;
            if (HardwareControlCatalog.IsHardwareControlProcess(name)) return false;
            if (pid <= 4 || pid == self || session < 0 || session != ownerSession) return false;

            if (!aggressive && (pid == foreground || userFacingFamily)) return false;
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(path)) return false;
            if (aggressive) return !SystemProcessCatalog.IsCoreSystemProcess(name, path, windowsRoot);
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

        private void Sweep(ProcessSnapshot all, int rendererPid)
        {

            lock (whiteEvalSync)
                SweepWithStableWhitelist(all, rendererPid);
        }

        private void SweepWithStableWhitelist(
            ProcessSnapshot all, int rendererPid)
        {
            PolicySnapshot sp = sessionPolicy;
            PerformancePreset mode = sp != null ? sp.Preset : ActivePreset;
            int foregroundPid = GameSessionDetector.ForegroundPid();
            bool aggressive = IsAggressive(mode, sp != null ? sp.Aggressive : aggressiveOn);
            WhitelistEvaluation whitelist = EvaluateWhitelist(all);
            HashSet<int> userFacingFamily = aggressive
                ? EmptyPidSet
                : CollectUserFacingFamily(foregroundPid, whitelist);
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
                    // 只有渲染进程本体和用户白名单放行 游戏家族的其余成员一律照压
                    //   上面的 boosted 只在提优真的落地时为真 提优关掉或被反作弊挡住句柄时它是假的
                    //   所以这条按 pid 的判断不能省 否则那些机器上游戏本体会被当后台压掉
                    if (white || (rendererPid > 0 && pid == rendererPid))
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
                    if (!BasicBackgroundEligible(pid, selfPid, nm, ipath,
                        sameSession ? selfSession : -1, selfSession, foregroundPid,
                        userFacingFamily.Contains(pid), windowsPrefix, aggressive))
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

                    // 模块已注入游戏进程的工具 宿主与游戏共生在同一条渲染路径上
                    //   压它就是压游戏自己 游戏等一个被压到零 CPU 的宿主回话即偶发整秒卡顿
                    if (LibraryRootOf(ipath, overlayExemptRoots) != null)
                    {
                        ReleaseBackgroundExemption(pid, nm, null);
                        continue;
                    }

                    SuppressionLevel desired = EffSuppress
                        ? BackgroundLevel() : SuppressionLevel.None;

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

            foreach (int pid in core.PidsWith(SuppressReason.Background))
                if (!live.Contains(pid)) { if (core.Release(pid, SuppressReason.Background)) ReportUntrack(pid); }

            if (first)
            {
                if (EffSuppress)
                {
                    string preset = mode == PerformancePreset.Competitive ? Lang.T("preset.competitive")
                        : mode == PerformancePreset.Custom ? Lang.T("preset.custom") : Lang.T("preset.standard");
                    bool strong = mode == PerformancePreset.Competitive
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
                    if (!names.TryGetValue(pair.Value, out parentName) || SystemProcessCatalog.IsShellProcess(parentName)) continue;
                    result.Add(pair.Key);
                    changed = true;
                }
            }
            while (changed);
            return result;
        }

        private void ReleaseBackgroundExemption(int pid, string name, string reason)
        {
            if (core.Release(pid, SuppressReason.Background))
            {
                ReportUntrack(pid);
                if (!string.IsNullOrEmpty(reason)) Logger.Log(reason + Lang.T("log.gamemodesweep.12") + name + " pid " + pid);
            }
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
    }
}
