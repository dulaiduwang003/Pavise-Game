// @author bdth 2074055628@qq.com
// File purpose Auto add, switch on the game library page, unknown foreground fullscreen GPU-dominant games join the target library automatically, removal means permanent ignore

using System;
using System.Collections.Generic;
using System.IO;

namespace PaviseApp
{
    internal partial class GameMode
    {
        // 20% is stricter than the 10% used by renderer election, a false positive written to the library costs more than a miss
        private const double AutoAddMinUtilization = 20.0;
        private const int AutoAddGateMs = 30000;
        private const int AutoAddRejectMinutes = 10;
        private const int AutoAddRejectCacheLimit = 64;

        private string autoIgnorePath;
        private readonly LibraryIgnoreTransaction libraryIgnoreTransaction;
        private bool autoIgnoreLoadFailed;
        private volatile bool autoAddOn;
        private long autoAddGateTicks;
        private readonly HashSet<string> autoAddIgnore =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, long> autoAddRejectUntil =
            new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        public bool AutoAddFullscreen
        {
            get { return autoAddOn; }
            set
            {
                if (autoAddOn == value) return;
                autoAddOn = value;
                Settings.Save("GmAutoAdd", value);
                Logger.Log(Lang.T(value ? "log.autoadd.1" : "log.autoadd.2"));
            }
        }

        private void TryAutoAddForegroundGame()
        {
            if (!autoAddOn || stopping || !enabled || ProfileStoreSaveFailed
                || autoIgnoreLoadFailed || libraryIgnoreTransaction.RecoveryPending) return;
            RendererHandoffTracker handoff = rendererHandoff;
            if (handoff != null && (handoff.HasCandidate || handoff.HasProbe)) return;
            bool sessionActive;
            lock (sync) sessionActive = active;
            if (sessionActive) return;

            int pid;
            if (!GameSessionDetector.TryForegroundFullscreen(out pid)
                || pid <= 0 || pid == selfPid)
                return;
            long now = DateTime.UtcNow.Ticks;
            if (now < autoAddGateTicks) return;
            autoAddGateTicks = now + AutoAddGateMs * TimeSpan.TicksPerMillisecond;

            GameProcessSnapshot identity;
            if (!GameSessionDetector.TryCaptureProcessIdentity(pid, selfSession, out identity))
                return;
            if (!GameSessionDetector.IsLibraryCandidate(identity.Name, identity.Path, windowsPrefix))
                return;
            string path = identity.Path;
            lock (sync)
            {
                if (autoAddIgnore.Contains(path)) return;
                long until;
                if (autoAddRejectUntil.TryGetValue(path, out until) && now < until) return;
                foreach (GameProfile profile in profiles)
                    if (SameLibraryPath(profile.ExecutablePath, path)
                        || SameLibraryPath(profile.LearnedExecutablePath, path)
                        || profile.ContainsPath(path))
                        return;
            }

            if (System.Threading.Interlocked.CompareExchange(ref rendererGpuSamplingBusy, 1, 0) != 0) return;
            // Sampling sleeps 1.4s inside PDH and can't hold the detection main loop, hand it to the thread pool and re-verify the foreground when the verdict returns
            string identityName = identity.Name;
            bool queued = false;
            try
            {
                queued = System.Threading.ThreadPool.QueueUserWorkItem(delegate
                {
                    try { FinishAutoAdd(pid, path, identityName, now); }
                    catch { }
                    finally { System.Threading.Interlocked.Exchange(ref rendererGpuSamplingBusy, 0); }
                });
            }
            catch { }
            finally { if (!queued) System.Threading.Interlocked.Exchange(ref rendererGpuSamplingBusy, 0); }
        }

        private void FinishAutoAdd(int pid, string path, string identityName, long now)
        {
            if (stopping || panicReq || !enabled) return;
            Dictionary<int, double> util = GpuEvidence.Sample3D(GpuEvidence.BurstRounds, GpuEvidence.BurstIntervalMs,
                delegate { return stopping || panicReq; });
            if (util == null) { RememberAutoAddReject(path, now); return; }
            double candidate;
            if (!util.TryGetValue(pid, out candidate)) candidate = 0;
            if (candidate < AutoAddMinUtilization)
            { RememberAutoAddReject(path, now); return; }
            foreach (KeyValuePair<int, double> kv in util)
            {
                if (kv.Key == pid || kv.Value <= candidate) continue;
                if (!IsCompositorPid(kv.Key)) { RememberAutoAddReject(path, now); return; }
            }

            // Sampling takes 1.4s, the foreground may have changed hands or a match may have started meanwhile, confirm again before it qualifies for the library
            bool sessionNow;
            lock (sync) sessionNow = active;
            if (sessionNow || stopping || panicReq) return;
            int foregroundNow;
            if (!GameSessionDetector.TryForegroundFullscreen(out foregroundNow)
                || foregroundNow != pid)
                return;

            string error;
            if (!AddGameExecutableCore(null, path, null, out error))
            { RememberAutoAddReject(path, now); return; }
            Logger.Log(Lang.T("log.autoadd.3") + (int)candidate + Lang.T("log.autoadd.4")
                + identityName + Lang.T("log.autoadd.5") + path + Lang.T("log.autoadd.6"));
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
                autoAddRejectUntil[path] = now
                    + AutoAddRejectMinutes * TimeSpan.TicksPerMinute;
            }
        }

        private bool IsCompositorPid(int pid)
        {
            string name;
            long creation;
            return TryIdentity(pid, out name, out creation)
                && string.Equals(name, "dwm", StringComparison.OrdinalIgnoreCase);
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
            if (libraryIgnoreTransaction == null) return true; // Uninitialized read-only UI fixtures
            if (!libraryIgnoreTransaction.RecoveryPending && !autoIgnoreLoadFailed) return true;
            return libraryIgnoreTransaction.TryRecover() && LoadAutoIgnore();
        }

        private bool CommitLibraryLocked(List<GameProfile> next, HashSet<string> nextIgnore)
        {
            if (stopping || ProfileStoreSaveFailed || !EnsureLibraryReadyLocked()) return false;
            bool saved;
            if (autoAddIgnore.SetEquals(nextIgnore)) saved = SaveProfileSnapshotLocked(next);
            else
            {
                var lines = new List<string> { Lang.T("t.autoadd.1") };
                var sorted = new List<string>(nextIgnore);
                sorted.Sort(StringComparer.OrdinalIgnoreCase);
                lines.AddRange(sorted);
                byte[] snapshot = new System.Text.UTF8Encoding(false).GetBytes(
                    string.Join(Environment.NewLine, lines.ToArray()) + Environment.NewLine);
                saved = libraryIgnoreTransaction.Commit(next, snapshot, delegate { return !stopping; });
                if (!saved && profileStore.SaveFailed) SignalProfileStoreSaveFailure();
            }
            if (!saved) return false;
            profiles.Clear(); profiles.AddRange(next);
            autoAddIgnore.Clear(); autoAddIgnore.UnionWith(nextIgnore);
            return true;
        }
    }
}
