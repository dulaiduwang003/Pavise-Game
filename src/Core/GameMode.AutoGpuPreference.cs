// @author bdth 2074055628@qq.com
// 文件用途 自动后台节能显卡 对局中占用游戏渲染卡的后台程序 登记为下次启动用节能显卡
//   偏好只影响下次启动 不迁移不重启任何进程 每个程序一生只自动登记一次
using System;
using System.Collections.Generic;
using System.Threading;

namespace PaviseApp
{
    internal partial class GameMode
    {
        // 5% 起判 视频播放和动画渲染这类真实占用都在其上 瞬时噪声在其下
        private const double AutoGpuMinUtilization = 5.0;
        // 对局稳定后再采样 大厅切换和加载期的占用不算数
        private const int AutoGpuDelaySeconds = 60;
        private const int AutoGpuMaxPerSession = 2;
        internal const string AutoGpuHandledKey = "AppGpuAutoHandledV1";
        private const int AutoGpuHandledLimit = 256;

        private volatile bool autoGpuOn;
        private volatile bool autoGpuScanned;
        // 提交 换局 关闭走同一道边界 GPU 锁和 sync 在自动路径上只尝试进入
        // 不拿着一把等另一把 免得和扫描 驱动暂存的锁顺序绕成环
        private readonly object autoGpuCommitGate = new object();

        private void InitializeAutoGpu()
        {
            autoGpuOn = Settings.Load("AppGpuAutoV1", false);
        }

        public bool AutoGpuPreference
        {
            get { return autoGpuOn; }
            set
            {
                lock (autoGpuCommitGate)
                {
                    if (autoGpuOn == value) return;
                    bool saved = Settings.Save("AppGpuAutoV1", value);
                    autoGpuOn = value && saved;
                    Logger.Log(Lang.T(autoGpuOn ? "log.autogpu.1" : "log.autogpu.2"));
                }
            }
        }

        private bool AutoGpuWanted { get { return autoGpuOn; } }

        private bool AutoGpuSessionCurrent(long stamp)
        { return AutoGpuSessionIdentityCurrent(stamp) && AutoGpuWanted; }

        private bool AutoGpuSessionIdentityCurrent(long stamp)
        {
            return stamp > 0 && !stopping && !panicReq && Volatile.Read(ref active)
                && Interlocked.Read(ref sessionStartTicks) == stamp;
        }

        private void SetAutoGpuSessionStamp(long stamp)
        {
            // 函数一返回 旧会话的提交和 handled 记账就都结束了
            lock (autoGpuCommitGate) Interlocked.Exchange(ref sessionStartTicks, stamp);
        }

        private AppGpuPreferenceResult CommitAutoGpu(AppGpuPreferenceManager manager, string path,
            long stamp, Func<bool> stillEligible)
        {
            lock (autoGpuCommitGate)
            {
                // ActivePreset 可能要 sync 先查无锁身份 拿到 sync 再查策略
                if (!AutoGpuSessionIdentityCurrent(stamp)) return AppGpuPreferenceResult.Changed;
                if (!Monitor.TryEnter(GpuPrefStage.MutationGate)) return AppGpuPreferenceResult.Busy;
                try
                {
                    if (!Monitor.TryEnter(sync)) return AppGpuPreferenceResult.Busy;
                    try
                    {
                        if (!AutoGpuSessionCurrent(stamp)) return AppGpuPreferenceResult.Changed;
                        AppGpuPreferenceResult result = AutoGpuEnroll(manager, path, delegate
                        {
                            return AutoGpuSessionCurrent(stamp) && stillEligible != null && stillEligible()
                                && AutoGpuSessionCurrent(stamp);
                        });
                        if (result == AppGpuPreferenceResult.Success || result == AppGpuPreferenceResult.AlreadyPresent)
                            RememberAutoGpuHandled(path);
                        return result;
                    }
                    finally { Monitor.Exit(sync); }
                }
                finally { Monitor.Exit(GpuPrefStage.MutationGate); }
            }
        }

        // 每局一次 复用渲染进程选举的采样互斥 与自动入库和选举不并发跑 PDH
        private void MaybeAutoEnrollBackgroundGpu(int rendererPid)
        {
            bool autoGpuWanted = AutoGpuWanted;
            if (!autoGpuWanted || autoGpuScanned || stopping || panicReq || rendererPid <= 0) return;
            long start = Interlocked.Read(ref sessionStartTicks);
            if (start == 0 || DateTime.UtcNow.Ticks - start
                < AutoGpuDelaySeconds * TimeSpan.TicksPerSecond) return;
            if (!AppGpuPreferences.Shared.Supported) { autoGpuScanned = true; return; }
            if (Interlocked.CompareExchange(ref rendererGpuSamplingBusy, 1, 0) != 0) return;
            autoGpuScanned = true;
            int pid = rendererPid;
            long stamp = start;
            bool queued = false;
            try { queued = ThreadPool.QueueUserWorkItem(delegate
            {
                try { AutoEnrollBackgroundGpu(pid, stamp); }
                catch { }
                finally { Interlocked.Exchange(ref rendererGpuSamplingBusy, 0); }
            }); }
            finally { if (!queued) Interlocked.Exchange(ref rendererGpuSamplingBusy, 0); }
        }

        private void AutoEnrollBackgroundGpu(int rendererPid, long sessionStamp)
        {
            // 中止条件带会话身份 直接换局不经过 Deactivate active 全程为真
            //   残余采样窗跨局会拿旧渲染 pid 判亲子关系 把新游戏的辅助进程误登记
            Func<bool> abort = delegate
            {
                return !AutoGpuSessionCurrent(sessionStamp);
            };
            RenderAdapter adapter = GpuEvidence.ResolveRenderAdapter(rendererPid, GpuEvidence.BurstIntervalMs);
            // 渲染卡不唯一就整局不做 宁可漏也不能把程序赶去错误的卡
            if (adapter == null || adapter.Ambiguous || abort()) return;
            Dictionary<int, double> util = GpuEvidence.SampleAdapter3D(
                adapter.LuidHigh, adapter.LuidLow, 3, GpuEvidence.BurstIntervalMs, abort);
            if (util == null || abort()) return;
            // 拥有可见顶层窗口的程序不碰 副屏上正在看的播放器和浏览器就是这形态
            //   它们跑在渲染卡上恰恰因为副屏接在独显 改成省电卡会引入逐帧跨卡拷贝
            // 名额有限 按占用降序处理 不能让字典哈希序决定登记谁
            var ranked = new List<KeyValuePair<int, double>>(util);
            ranked.Sort(delegate(KeyValuePair<int, double> a, KeyValuePair<int, double> b)
            { return b.Value.CompareTo(a.Value); });
            int enrolled = 0;
            foreach (KeyValuePair<int, double> kv in ranked)
            {
                if (enrolled >= AutoGpuMaxPerSession || abort()) break;
                if (kv.Key == rendererPid || kv.Key == selfPid
                    || kv.Value < AutoGpuMinUtilization) continue;
                GameProcessSnapshot identity;
                if (!GameSessionDetector.TryCaptureProcessIdentity(kv.Key, selfSession, out identity))
                    continue;
                // 游戏自己直接拉起的辅助进程不碰 不论装在哪个目录
                if (identity.ParentPid == rendererPid) continue;
                string path = identity.Path, name = identity.Name;
                if (!AutoGpuEligible(name, path, windowsPrefix)) continue;
                bool blocked;
                lock (sync) blocked = AutoGpuProtectedLocked(name, path);
                if (blocked || AutoGpuAlreadyHandled(path)) continue;
                AppGpuPreferenceResult result = CommitAutoGpu(AppGpuPreferences.Shared, path, sessionStamp, delegate
                {
                    // Prepare 到真正提交之间再查一次 登记按 EXE 生效 只看候选 PID 不够
                    if (abort()) return false;
                    GameProcessSnapshot current;
                    if (!GameSessionDetector.TryCaptureProcessIdentity(kv.Key, selfSession, out current)
                        || current.Creation != identity.Creation || !SameLibraryPath(current.Path, path)) return false;
                    ProcessSnapshot snapshot = ProcessSnapshotSource.Capture(selfSession, 0);
                    if (!AutoGpuVisibilityAllows(current, snapshot, CollectVisibleWindowPids(), selfSession))
                        return false;
                    lock (sync) if (AutoGpuProtectedLocked(name, path)) return false;
                    return !abort();
                });
                if (result != AppGpuPreferenceResult.Success
                    && result != AppGpuPreferenceResult.AlreadyPresent) continue;
                if (result == AppGpuPreferenceResult.Success)
                {
                    enrolled++;
                    Logger.Log(Lang.T("log.autogpu.3") + name + Lang.T("log.autogpu.4")
                        + (int)kv.Value + Lang.T("log.autogpu.5"));
                }
            }
        }

        // 有可见顶层窗口的进程集合 枚举失败返回 null
        //   调用方把 null 当"全部可见"处理 本局一个都不登记 宁可漏也不误伤在用的程序
        private static HashSet<int> CollectVisibleWindowPids()
        {
            try
            {
                var pids = new HashSet<int>();
                bool complete = EnumWindows(delegate(IntPtr hwnd, IntPtr lparam)
                {
                    if (IsWindowVisible(hwnd))
                    {
                        uint pid;
                        GetWindowThreadProcessId(hwnd, out pid);
                        if (pid > 0) pids.Add((int)pid);
                    }
                    return true;
                }, IntPtr.Zero);
                return VisibleWindowResult(complete, pids);
            }
            catch { return null; }
        }

        internal static HashSet<int> VisibleWindowResult(bool complete, HashSet<int> pids)
        { return complete ? pids : null; }

        // 快照和窗口枚举都得完整 可见进程连同有身份依据的后代按镜像路径保护
        // 这里只是只读准入 不敢说窗口状态和注册表提交能做成一个原子事务
        internal static bool AutoGpuVisibilityAllows(GameProcessSnapshot candidate,
            ProcessSnapshot snapshot, HashSet<int> visible, int session)
        {
            if (candidate == null || snapshot == null || visible == null) return false;
            ProcEntry found = snapshot.Find(candidate.Pid);
            string path = WhitelistRule.NormalizeImagePath(candidate.Path);
            if (found == null || found.Session != session || found.Creation <= 0
                || found.Creation != candidate.Creation || string.IsNullOrEmpty(path)
                || !string.Equals(path, WhitelistRule.NormalizeImagePath(found.Path), StringComparison.OrdinalIgnoreCase))
                return false;
            var protectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (int pid in visible)
            {
                ProcEntry entry = snapshot.Find(pid);
                if (entry == null || entry.Session < 0) return false;
                if (entry.Session != session) continue;
                string image = WhitelistRule.NormalizeImagePath(entry.Path);
                if (entry.Creation <= 0 || string.IsNullOrEmpty(image)) return false;
                protectedPaths.Add(image);
            }
            foreach (ProcEntry entry in snapshot.Entries)
            {
                if (entry.Session != session || entry.Creation <= 0) continue;
                ProcEntry cursor = entry;
                var visited = new HashSet<int>();
                while (cursor != null && visited.Add(cursor.Pid))
                {
                    // Shell 终端 运行时都不是独立应用家族的锚点 也不能拿来穿越
                    // 可见宿主自己的镜像还是由上面的 protectedPaths 兜着
                    if (WhitelistRule.IsUnsafeFamilyAnchor(cursor.Path)) break;
                    if (visible.Contains(cursor.Pid))
                    {
                        string image = WhitelistRule.NormalizeImagePath(entry.Path);
                        if (string.IsNullOrEmpty(image)) return false;
                        protectedPaths.Add(image);
                        break;
                    }
                    ProcEntry parent = snapshot.Find(cursor.ParentPid);
                    if (parent == null || parent.Session != session || parent.Creation <= 0
                        || parent.Creation >= cursor.Creation) break;
                    cursor = parent;
                }
            }
            return !protectedPaths.Contains(path);
        }

        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lparam);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lparam);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hwnd);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

        // 纯过滤 不碰任何状态 隔离测试直接调用
        //   录屏/通信/覆盖层宿主要用独显编码 硬件与外设链动不得 系统目录不碰
        internal static bool AutoGpuEligible(string name, string path, string windowsPrefix)
        {
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(path)) return false;
            if (!string.IsNullOrEmpty(windowsPrefix)
                && path.StartsWith(windowsPrefix, StringComparison.OrdinalIgnoreCase)) return false;
            if (OverlayHostCatalog.ShouldProtectProcess(name, path, true)) return false;
            if (HardwareControlCatalog.IsHardwareControlProcess(name)) return false;
            if (PeripheralCatalog.IsInputChainProcess(name, path)) return false;
            return true;
        }

        // 调用方须持有 sync 游戏库任一档案的成员和白名单都不自动改偏好
        private bool AutoGpuProtectedLocked(string name, string path)
        {
            GameDetection detection = activeDetection;
            if (detection != null && detection.Profile != null)
            {
                string root = detection.Profile.Root;
                if (FamilyBoundary.SafeFamilyDir(root) && FamilyBoundary.UnderRoot(path, root)) return true;
            }
            foreach (GameProfile profile in profiles)
                if (SameLibraryPath(profile.ExecutablePath, path)
                    || SameLibraryPath(profile.LearnedExecutablePath, path)
                    || profile.ContainsPath(path)) return true;
            string normalizedName = WhitelistRule.NormalizeName(name);
            string normalizedPath = WhitelistRule.NormalizeImagePath(path);
            foreach (WhitelistRule rule in whiteRules)
                if (rule.MatchesNormalized(normalizedName, normalizedPath)) return true;
            return false;
        }

        // 只认领没有任何既有显卡偏好的程序 已有偏好值 = 用户或外部工具的明确
        //   选择 自动路径永不覆盖 人工路径才有确认弹窗可以覆盖
        internal static AppGpuPreferenceResult AutoGpuEnroll(AppGpuPreferenceManager manager, string path)
        { return AutoGpuEnroll(manager, path, null); }

        internal static AppGpuPreferenceResult AutoGpuEnroll(AppGpuPreferenceManager manager, string path,
            Func<bool> stillEligible)
        {
            AppGpuPreferenceChange change;
            AppGpuPreferenceResult prepared = manager.Prepare(path, out change);
            if (prepared != AppGpuPreferenceResult.Success) return prepared;
            if (change.NeedsConfirmation) return AppGpuPreferenceResult.NeedsConfirmation;
            if (stillEligible != null && !stillEligible()) return AppGpuPreferenceResult.Changed;
            return manager.Apply(change, false, stillEligible);
        }

        // 每个路径一生只自动登记一次 用户从管理列表移除后不会被自动加回
        //   想再登记走人工添加 列表按先进先出封顶 不缓存 每次实读配置
        internal static bool AutoGpuAlreadyHandled(string path)
        {
            string raw = Settings.LoadStr(AutoGpuHandledKey, "");
            if (raw.Length == 0) return false;
            foreach (string line in raw.Split('\n'))
                if (string.Equals(line, path, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        internal static bool RememberAutoGpuHandled(string path)
        {
            if (string.IsNullOrEmpty(path) || path.IndexOf('\n') >= 0) return false;
            string raw = Settings.LoadStr(AutoGpuHandledKey, "");
            var lines = new List<string>();
            if (raw.Length != 0)
                foreach (string line in raw.Split('\n'))
                    if (line.Length != 0 && !string.Equals(line, path, StringComparison.OrdinalIgnoreCase))
                        lines.Add(line);
            lines.Add(path);
            while (lines.Count > AutoGpuHandledLimit) lines.RemoveAt(0);
            return Settings.SaveStr(AutoGpuHandledKey, string.Join("\n", lines.ToArray()));
        }
    }
}
