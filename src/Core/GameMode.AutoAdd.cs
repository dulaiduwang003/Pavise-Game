// 作者：bdth 2074055628@qq.com

using System;
using System.Collections.Generic;
using System.IO;

namespace PaviseApp
{
    internal partial class GameMode
    {
        // 强全屏证据可直接入库，窗口或弱活动需两轮独立证据。
        private const double AutoAddMinUtilization = 20.0;
        private const int AutoAddGateMs = 30000;
        private const int AutoAddRejectCacheLimit = 64;
        private const int AutoAddEvidenceAgeMs = 120000;
        private sealed class AutoAddAttempt
        {
            internal int Pid, Misses, Confirmations;
            internal long Creation, EvidenceAt, LastSeen, NoteAt;
            internal string Note;
        }
        private readonly Dictionary<string, AutoAddAttempt> autoAddAttempts =
            new Dictionary<string, AutoAddAttempt>(StringComparer.OrdinalIgnoreCase);
        private int autoAddEpoch;
        private bool autoAddPreferenceUnsaved;

        private string autoIgnorePath;
        private readonly LibraryIgnoreTransaction libraryIgnoreTransaction;
        private bool autoIgnoreLoadFailed;
        private volatile bool autoAddOn;
        private long autoAddGateTicks;
        private readonly HashSet<string> autoAddIgnore =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, long> autoAddRejectUntil =
            new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

#if PAVISE_SELFTEST
        internal Func<long> AutoAddTestNow;
        internal Func<int> AutoAddTestForeground;
        internal Func<ForegroundGameWindowSnapshot> AutoAddTestWindow;
        internal Func<int, GameProcessSnapshot> AutoAddTestIdentity;
        internal Func<ProcessSnapshot> AutoAddTestSnapshot;
        internal Func<Func<bool>, Dictionary<int, double>> AutoAddTestGpu;
#endif

        public bool AutoAddFullscreen
        {
            get { return autoAddOn; }
            set
            {
                lock (sync)
                {
                    if (autoAddOn == value) return;
                    autoAddOn = value;
                    System.Threading.Interlocked.Increment(ref autoAddEpoch);
                    if (value) System.Threading.Interlocked.Exchange(ref autoAddGateTicks, 0);
                    autoAddPreferenceUnsaved = !Settings.Save("GmAutoAdd", value);
                }
                Logger.Log(Lang.T(value ? "log.autoadd.1" : "log.autoadd.2"));
                if (autoAddPreferenceUnsaved) Logger.Warn(Lang.T("autoadd.preference.unsaved"));
            }
        }

        private void TryAutoAddForegroundGame()
        {
            if (!autoAddOn || stopping || !enabled || ProfileStoreSaveFailed
                || autoIgnoreLoadFailed || libraryIgnoreTransaction.RecoveryPending) return;
            bool sessionActive;
            lock (sync) sessionActive = active;
            if (sessionActive) return;

            ForegroundGameWindowSnapshot window;
            if (!AutoAddWindow(out window) || window.Pid <= 0 || window.Pid == selfPid)
                return;
            int pid = window.Pid;
#if PAVISE_SELFTEST
            long now = AutoAddTestNow != null ? AutoAddTestNow() : DateTime.UtcNow.Ticks;
#else
            long now = DateTime.UtcNow.Ticks;
#endif
            if (now < autoAddGateTicks) return;
            // 采集器繁忙只消耗短检查间隔，不占用完整采样预算。
            System.Threading.Interlocked.Exchange(ref autoAddGateTicks, now + TimeSpan.FromSeconds(5).Ticks);

            GameProcessSnapshot identity;
            if (!CaptureAutoAddIdentity(pid, out identity)) return;
            if (!GameSessionDetector.IsLibraryCandidate(identity.Name, identity.Path, windowsPrefix))
                return;
            if (identity.Pid != pid || identity.Creation <= 0) return;
            identity = new GameProcessSnapshot { Pid = pid, Creation = identity.Creation,
                Name = identity.Name, Path = identity.Path };
            string path = identity.Path;
            lock (sync)
            {
                if (autoAddIgnore.Contains(path)) return;
                AutoAddAttempt attempt;
                if (autoAddAttempts.TryGetValue(path, out attempt)
                    && (attempt.Pid != pid || attempt.Creation != identity.Creation))
                { autoAddAttempts.Remove(path); autoAddRejectUntil.Remove(path); }
                long until;
                if (autoAddRejectUntil.TryGetValue(path, out until) && now < until) return;
                foreach (GameProfile profile in profiles)
                    if (SameLibraryPath(profile.ExecutablePath, path)
                        || SameLibraryPath(profile.LearnedExecutablePath, path)
                        || profile.ContainsPath(path))
                        return;
                attempt = AutoAddAttemptLocked(path, now);
                attempt.Pid = pid; attempt.Creation = identity.Creation; attempt.LastSeen = now;
            }

            if (System.Threading.Interlocked.CompareExchange(ref rendererGpuSamplingBusy, 1, 0) != 0) return;
            // PDH 采样含等待，必须离开检测主循环执行。
            int epoch = System.Threading.Volatile.Read(ref autoAddEpoch);
            bool queued = false;
            try
            {
                queued = System.Threading.ThreadPool.QueueUserWorkItem(delegate
                {
                    try { FinishAutoAdd(identity, window.FullscreenLike, epoch, now); }
                    catch (Exception error)
                    {
                        if (!AutoAddCanceled(epoch))
                        { RememberAutoAddReject(path, now); NoteAutoAdd(path, "CaptureUnavailable", error.GetType().Name, now); }
                    }
                    finally { System.Threading.Interlocked.Exchange(ref rendererGpuSamplingBusy, 0); }
                });
            }
            catch { }
            finally
            {
                if (!queued) System.Threading.Interlocked.Exchange(ref rendererGpuSamplingBusy, 0);
                else System.Threading.Interlocked.Exchange(ref autoAddGateTicks, now + AutoAddGateMs * TimeSpan.TicksPerMillisecond);
            }
        }

        private bool AutoAddCanceled(int epoch)
        { return stopping || panicReq || !enabled || !autoAddOn || System.Threading.Volatile.Read(ref autoAddEpoch) != epoch; }

        private void FinishAutoAdd(GameProcessSnapshot identity, bool fullscreen, int epoch, long now)
        {
            if (AutoAddCanceled(epoch)) return;
            int pid = identity.Pid; string path = identity.Path;
            Dictionary<int, double> util;
            GpuSampleDiagnostics diagnostics = null;
#if PAVISE_SELFTEST
            if (AutoAddTestGpu != null) util = AutoAddTestGpu(delegate { return AutoAddCanceled(epoch); });
            else
#endif
            util = GpuEvidence.Sample3D(GpuEvidence.BurstRounds, GpuEvidence.BurstIntervalMs,
                delegate { return AutoAddCanceled(epoch); }, pid, out diagnostics);
            if (AutoAddCanceled(epoch)) return;
            if (util == null)
            { RememberAutoAddReject(path, now); NoteAutoAdd(path, "GpuUnavailable", diagnostics == null ? null : diagnostics.ToString(), now); return; }
            double candidate;
            if (!util.TryGetValue(pid, out candidate)) candidate = 0;
            if (double.IsNaN(candidate) || double.IsInfinity(candidate) || candidate < GpuEvidence.MinElectUtilization)
            {
                lock (sync)
                {
                    AutoAddAttempt previous;
                    if (autoAddAttempts.TryGetValue(path, out previous)) previous.Confirmations = 0;
                }
                RememberAutoAddReject(path, now); NoteAutoAdd(path, "ActivityWaiting", null, now); return;
            }

            // 采样返回后重新确认前台和对局状态。
            bool sessionNow;
            lock (sync) sessionNow = active;
            if (sessionNow || AutoAddCanceled(epoch)) return;
            ForegroundGameWindowSnapshot foregroundNow;
            if (!AutoAddWindow(out foregroundNow) || foregroundNow.Pid != pid)
                return;
            GameProcessSnapshot live;
            if (!CaptureAutoAddIdentity(pid, out live)) return;
            if (live == null || live.Pid != pid || live.Creation != identity.Creation || !SameLibraryPath(live.Path, path)) return;
            lock (sync)
            {
                if (AutoAddCanceled(epoch) || !AutoDetectedCommitAllowed(path)) return;
                AutoAddAttempt attempt = AutoAddAttemptLocked(path, now);
                if (attempt.Pid != pid || attempt.Creation != identity.Creation)
                { attempt.Pid = pid; attempt.Creation = identity.Creation; attempt.Confirmations = attempt.Misses = 0; }
                attempt.LastSeen = now; attempt.Misses = 0;
                bool strong = fullscreen && foregroundNow.FullscreenLike && candidate >= AutoAddMinUtilization;
                if (!strong)
                {
                    if (attempt.Confirmations == 0 || now < attempt.EvidenceAt
                        || now - attempt.EvidenceAt > AutoAddEvidenceAgeMs * TimeSpan.TicksPerMillisecond)
                    {
                        attempt.Confirmations = 1; attempt.EvidenceAt = now;
                        autoAddRejectUntil[path] = now + AutoAddGateMs * TimeSpan.TicksPerMillisecond;
                        NoteAutoAdd(path, "ConfirmingActivity", null, now); return;
                    }
                }
            }

            string error;
            if (!AddAutoDetectedExecutableCore(path, out error, delegate { return !AutoAddCanceled(epoch); }))
            { RememberAutoAddReject(path, now); return; }
            lock (sync) { autoAddRejectUntil.Remove(path); autoAddAttempts.Remove(path); }
            Logger.Log(Lang.T("log.autoadd.3") + (int)candidate + Lang.T("log.autoadd.4")
                + identity.Name + Lang.T("log.autoadd.5") + path + Lang.T("log.autoadd.6"));
        }

        private AutoAddAttempt AutoAddAttemptLocked(string path, long now)
        {
            AutoAddAttempt attempt;
            if (autoAddAttempts.TryGetValue(path, out attempt)) return attempt;
            if (autoAddAttempts.Count >= AutoAddRejectCacheLimit)
            {
                string oldest = null; long stamp = long.MaxValue;
                foreach (var entry in autoAddAttempts)
                    if (entry.Value.LastSeen < stamp) { oldest = entry.Key; stamp = entry.Value.LastSeen; }
                if (oldest != null) { autoAddAttempts.Remove(oldest); autoAddRejectUntil.Remove(oldest); }
            }
            attempt = new AutoAddAttempt { LastSeen = now }; autoAddAttempts[path] = attempt; return attempt;
        }

        private void RememberAutoAddReject(string path, long now)
        {
            lock (sync)
            {
                if (autoAddRejectUntil.Count >= AutoAddRejectCacheLimit)
                {
                    var expired = new List<string>();
                    foreach (KeyValuePair<string, long> kv in autoAddRejectUntil)
                        if (kv.Value <= now) expired.Add(kv.Key);
                    foreach (string key in expired) autoAddRejectUntil.Remove(key);
                    if (autoAddRejectUntil.Count >= AutoAddRejectCacheLimit)
                        autoAddRejectUntil.Clear();
                }
                AutoAddAttempt attempt = AutoAddAttemptLocked(path, now);
                attempt.LastSeen = now;
                attempt.Confirmations = 0;
                if (attempt.Misses < 4) ++attempt.Misses;
                int seconds = attempt.Misses == 1 ? 30 : attempt.Misses == 2 ? 60 : attempt.Misses == 3 ? 120 : 300;
                autoAddRejectUntil[path] = now + seconds * TimeSpan.TicksPerSecond;
            }
        }

        private void NoteAutoAdd(string path, string reason, string detail, long now)
        {
            lock (sync)
            {
                AutoAddAttempt attempt = AutoAddAttemptLocked(path, now);
                if (attempt.Note == reason && now < attempt.NoteAt) return;
                attempt.Note = reason; attempt.NoteAt = now + TimeSpan.FromMinutes(5).Ticks;
            }
            Logger.Info("AUTOADD " + Path.GetFileName(path) + " | " + Lang.T("autoadd.reason." + reason)
                + (string.IsNullOrEmpty(detail) ? "" : " [" + detail + "]"));
        }

        private bool AutoAddWindow(out ForegroundGameWindowSnapshot window)
        {
#if PAVISE_SELFTEST
            if (AutoAddTestWindow != null) { window = AutoAddTestWindow(); return window != null && window.Pid > 0; }
            if (AutoAddTestForeground != null)
            {
                int pid = AutoAddTestForeground(); window = pid <= 0 ? null
                    : new ForegroundGameWindowSnapshot { Pid = pid, FullscreenLike = true };
                return window != null;
            }
#endif
            return GameSessionDetector.TryForegroundGameWindow(out window);
        }

        private bool CaptureAutoAddIdentity(int pid, out GameProcessSnapshot identity)
        {
            identity = null;
            try
            {
#if PAVISE_SELFTEST
                if (AutoAddTestIdentity != null)
                {
                    identity = AutoAddTestIdentity(pid);
                    if (identity != null) return true;
                    if (AutoAddTestSnapshot == null) return false;
                    ProcessSnapshot mocked = AutoAddTestSnapshot();
                    return mocked != null && GameSessionDetector.TryCaptureProcessIdentity(mocked.Find(pid), selfSession, out identity);
                }
#endif
                if (GameSessionDetector.TryCaptureProcessIdentity(pid, selfSession, out identity)) return true;
                // 拒绝 SYNCHRONIZE 不代表没有游戏，身份由系统快照佐证。
                ProcessSnapshot snapshot = ProcessSnapshotSource.Capture(selfSession, 0);
                return snapshot != null && GameSessionDetector.TryCaptureProcessIdentity(snapshot.Find(pid), selfSession, out identity);
            }
            catch { identity = null; return false; }
        }

        private static bool SameLibraryPath(string a, string b)
        {
            return !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b)
                && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        private bool LoadAutoIgnore()
        {
            try
            {
                var loaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string line in File.Exists(autoIgnorePath) ? File.ReadAllLines(autoIgnorePath) : new string[0])
                {
                    string t = line.Trim();
                    if (t.Length == 0 || t.StartsWith("#")) continue;
                    loaded.Add(t);
                }
                autoAddIgnore.Clear(); autoAddIgnore.UnionWith(loaded);
                autoIgnoreLoadFailed = false;
                return true;
            }
            catch { autoIgnoreLoadFailed = true; return false; }
        }

        private bool EnsureLibraryReadyLocked()
        {
            if (libraryIgnoreTransaction == null) return true;
            if (!libraryIgnoreTransaction.RecoveryPending && !autoIgnoreLoadFailed) return true;
            return libraryIgnoreTransaction.TryRecover() && LoadAutoIgnore();
        }

        private bool CommitLibraryLocked(List<GameProfile> next, HashSet<string> nextIgnore,
            Func<bool> canCommit = null)
        {
            if (stopping || ProfileStoreSaveFailed || !EnsureLibraryReadyLocked()
                || (canCommit != null && !canCommit())) return false;
            bool saved;
            if (autoAddIgnore.SetEquals(nextIgnore)) saved = SaveProfileSnapshotLocked(next, canCommit);
            else
            {
                var lines = new List<string> { Lang.T("t.autoadd.1") };
                var sorted = new List<string>(nextIgnore);
                sorted.Sort(StringComparer.OrdinalIgnoreCase);
                lines.AddRange(sorted);
                byte[] snapshot = new System.Text.UTF8Encoding(false).GetBytes(
                    string.Join(Environment.NewLine, lines.ToArray()) + Environment.NewLine);
                saved = libraryIgnoreTransaction.Commit(next, snapshot,
                    delegate { return !stopping && (canCommit == null || canCommit()); });
                if (!saved && profileStore.SaveFailed) SignalProfileStoreSaveFailure();
            }
            if (!saved) return false;
            profiles.Clear(); profiles.AddRange(next);
            autoAddIgnore.Clear(); autoAddIgnore.UnionWith(nextIgnore);
            return true;
        }
    }
}
