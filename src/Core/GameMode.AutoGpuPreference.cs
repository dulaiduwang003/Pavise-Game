// 作者：bdth 2074055628@qq.com
// GPU 偏好仅影响下次启动，不迁移正在运行的进程。
using System;
using System.Collections.Generic;
using System.Threading;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private const double AutoGpuMinUtilization = 5.0;
        private const int AutoGpuDelaySeconds = 60;
        private const int AutoGpuMaxPerSession = 2;
        internal const string AutoGpuHandledKey = "AppGpuAutoHandledV1";
        private const int AutoGpuHandledLimit = 256;

        private volatile bool autoGpuOn;
        private volatile bool autoGpuScanned;
        // GPU 锁和 sync 只尝试获取，持有其中一个时不等待另一个。
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
            return stamp > 0 && !stopping && !panicReq && enabled && !ProfileStoreSaveFailed && Volatile.Read(ref active)
                && Interlocked.Read(ref sessionStartTicks) == stamp;
        }

        private void SetAutoGpuSessionStamp(long stamp)
        {
            lock (autoGpuCommitGate) Interlocked.Exchange(ref sessionStartTicks, stamp);
        }

        private AppGpuPreferenceResult CommitAutoGpu(AppGpuPreferenceManager manager, string path,
            long stamp, Func<bool> stillEligible)
        {
            lock (autoGpuCommitGate)
            {
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
            // 采样带对局身份，防止跨局结果误处理新游戏进程。
            Func<bool> abort = delegate
            {
                return !AutoGpuSessionCurrent(sessionStamp);
            };
            RenderAdapter adapter = GpuEvidence.ResolveRenderAdapter(rendererPid, GpuEvidence.BurstIntervalMs);
            // 无法确定唯一渲染 GPU 时跳过本局。
            if (adapter == null || adapter.Ambiguous || abort()) return;
            Dictionary<int, double> util = GpuEvidence.SampleAdapter3D(
                adapter.LuidHigh, adapter.LuidLow, 3, GpuEvidence.BurstIntervalMs, abort);
            if (util == null || abort()) return;
            // 有可见窗口的程序不自动改 GPU，避免干扰第二屏应用。
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
                if (identity.ParentPid == rendererPid) continue;
                string path = identity.Path, name = identity.Name;
                if (!AutoGpuEligible(name, path, windowsPrefix)) continue;
                bool blocked;
                lock (sync) blocked = AutoGpuProtectedLocked(name, path);
                if (blocked || AutoGpuAlreadyHandled(path)) continue;
                AppGpuPreferenceResult result = CommitAutoGpu(AppGpuPreferences.Shared, path, sessionStamp, delegate
                {
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

        // 已有 GPU 偏好视为用户选择，自动路径不覆盖。
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

        // 用户删除后不再自动加入，重新管理需手动操作。
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
