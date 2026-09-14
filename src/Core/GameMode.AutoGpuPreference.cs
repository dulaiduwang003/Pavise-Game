// @author bdth 2074055628@qq.com
// File purpose Auto background power-saving GPU, background programs occupying the game's render GPU during a match are enrolled to use the power-saving GPU on next launch
//   The preference only affects the next launch, no process is migrated or restarted, each program is auto-enrolled only once ever
using System;
using System.Collections.Generic;
using System.Threading;

namespace PaviseApp
{
    internal partial class GameMode
    {
        // Judge from 5%, real usage like video playback and animation rendering sits above it, transient noise below
        private const double AutoGpuMinUtilization = 5.0;
        // Sample only after the match has settled, lobby switches and loading-phase usage don't count
        private const int AutoGpuDelaySeconds = 60;
        private const int AutoGpuMaxPerSession = 2;
        internal const string AutoGpuHandledKey = "AppGpuAutoHandledV1";
        private const int AutoGpuHandledLimit = 256;

        private volatile bool autoGpuOn;
        private volatile bool autoGpuScanned;
        // Commit, match switch and shutdown share one boundary, on the auto path the GPU lock and sync are only try-entered
        // Never hold one while waiting for the other, or the lock order loops with the scan and driver staging locks
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
            // Once this returns, the old session's commits and handled bookkeeping are all over
            lock (autoGpuCommitGate) Interlocked.Exchange(ref sessionStartTicks, stamp);
        }

        private AppGpuPreferenceResult CommitAutoGpu(AppGpuPreferenceManager manager, string path,
            long stamp, Func<bool> stillEligible)
        {
            lock (autoGpuCommitGate)
            {
                // ActivePreset may need sync, check lock-free identity first, then take sync and check policy
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

        // Once per match, reuses the renderer election sampling mutex so PDH never runs concurrently with auto add or election
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
            // The abort condition carries session identity, a direct match switch skips Deactivate and active stays true throughout
            //   A leftover sampling window spanning matches would judge parent/child with the old renderer pid and mis-enroll the new game's helper processes
            Func<bool> abort = delegate
            {
                return !AutoGpuSessionCurrent(sessionStamp);
            };
            RenderAdapter adapter = GpuEvidence.ResolveRenderAdapter(rendererPid, GpuEvidence.BurstIntervalMs);
            // If the render GPU isn't unique skip the whole match, better to miss than push a program onto the wrong GPU
            if (adapter == null || adapter.Ambiguous || abort()) return;
            Dictionary<int, double> util = GpuEvidence.SampleAdapter3D(
                adapter.LuidHigh, adapter.LuidLow, 3, GpuEvidence.BurstIntervalMs, abort);
            if (util == null || abort()) return;
            // Programs owning a visible top-level window are untouched, the player or browser being watched on a second screen is exactly that shape
            //   They run on the render GPU precisely because the second screen is wired to the dGPU, switching them to the power-saving GPU would add a per-frame cross-GPU copy
            // Slots are limited, process in descending usage order, dictionary hash order must not decide who gets enrolled
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
                // Helper processes launched directly by the game are untouched regardless of install directory
                if (identity.ParentPid == rendererPid) continue;
                string path = identity.Path, name = identity.Name;
                if (!AutoGpuEligible(name, path, windowsPrefix)) continue;
                bool blocked;
                lock (sync) blocked = AutoGpuProtectedLocked(name, path);
                if (blocked || AutoGpuAlreadyHandled(path)) continue;
                AppGpuPreferenceResult result = CommitAutoGpu(AppGpuPreferences.Shared, path, sessionStamp, delegate
                {
                    // Re-check between Prepare and the actual commit, enrollment applies per EXE so checking only the candidate PID isn't enough
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

        // Set of processes with a visible top-level window, returns null when enumeration fails
        //   Callers treat null as everything visible and enroll nothing this match, better to miss than hit a program in use
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

        // Both the snapshot and the window enumeration must be complete, visible processes and descendants with identity evidence are protected by image path
        // This is read-only admission only, no claim that window state and the registry commit form one atomic transaction
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
                    // Shells, terminals and runtimes are not anchors of an independent app family and can't be traversed through either
                    // The visible host's own image is still covered by protectedPaths above
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

        // Pure filter, touches no state, isolated tests call it directly
        //   Recording, communication and overlay hosts need the dGPU for encoding, hardware and peripheral chains are untouchable, system directories are off limits
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

        // Caller must hold sync, members of any game library profile and whitelist entries never get their preference auto-changed
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

        // Only claim programs with no existing GPU preference at all, an existing value = an explicit choice by the user or an external tool
        //   The auto path never overrides, only the manual path has a confirmation dialog that can
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

        // Each path is auto-enrolled only once ever, once the user removes it from the management list it won't be auto-added back
        //   Re-enrolling goes through manual add, the list is capped FIFO, not cached, config is read for real every time
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
