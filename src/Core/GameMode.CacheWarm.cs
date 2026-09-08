using System;
using System.Threading;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private volatile bool cacheWarmOn;
        private int cacheWarmGeneration;
        private long cacheWarmSessionId;
        private readonly CacheWarmRunner cacheWarm = new CacheWarmRunner();
        public bool CacheWarmOn
        {
            get { return cacheWarmOn; }
            set
            {
                lock (sync)
                {
                    if (stopping) return;
                    InvalidateCacheWarm();
                    cacheWarmOn = Settings.Save(PolicyCatalog.KeyCacheWarm, value) && value;
                }
                RequestPolicyApply();
            }
        }
        public string CacheWarmStatus { get { return Lang.T(cacheWarm == null ? "cachewarm.idle" : cacheWarm.Status); } }
        private void InvalidateCacheWarm()
        { Interlocked.Increment(ref cacheWarmGeneration); if (cacheWarm != null) cacheWarm.Cancel(true); }
        private void BeginCacheWarmSession()
        {
            lock (sync)
            {
                // 首次提优计时戳会在成功后清零；预热必须使用整局稳定的独立身份。
                Interlocked.Increment(ref cacheWarmSessionId);
                InvalidateCacheWarm();
            }
        }
        private Func<bool> CaptureCacheWarmAdmission(out string key, out string executable, out string root)
        {
            int generation; string profileId, profileEntry, rendererPath, installRoot; long session;
            lock (sync)
            {
                GameDetection d = activeDetection;
                profileId = d != null && d.Profile != null ? d.Profile.Id : null;
                executable = d != null ? d.RendererPath : null;
                root = d != null && d.Profile != null ? d.Profile.Root : null;
                rendererPath = executable; installRoot = root;
                profileEntry = d != null && d.Profile != null ? d.Profile.ExecutablePath : null;
                session = Interlocked.Read(ref cacheWarmSessionId);
                generation = cacheWarmGeneration;
                // 学习新 renderer 可保留档案 ID 却改变安装目录；旧任务不能沿用旧路径。
                key = profileId + ":" + session + ":" + generation
                    + "|" + installRoot + "|" + rendererPath + "|" + profileEntry;
            }
            return delegate
            {
                lock (sync)
                {
                    var d = activeDetection;
                    GameProfile current = FindProfileLocked(profileId);
                    return generation == cacheWarmGeneration && active && enabled && !stopping && !panicReq
                        && !ProfileStoreSaveFailed && !stickyGraceOnly && gameGoneSinceTicks == 0 && session > 0
                        && Volatile.Read(ref standbyCleanerRestorePending) == 0
                        && session == Interlocked.Read(ref cacheWarmSessionId) && !string.IsNullOrEmpty(profileId)
                        && d != null && d.Profile != null && d.Profile.Id == profileId
                        && string.Equals(d.RendererPath, rendererPath, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(d.Profile.Root, installRoot, StringComparison.OrdinalIgnoreCase)
                        && current != null
                        && string.Equals(current.Root, installRoot, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(current.ExecutablePath, profileEntry, StringComparison.OrdinalIgnoreCase)
                        && LiveBoolPreferenceLocked(PolicyCatalog.KeyCacheWarm, cacheWarmOn)
                        && !LiveBoolPreferenceLocked(PolicyCatalog.KeyStandbyCleaner, standbyCleanerOn);
                }
            };
        }
        private void ApplyCacheWarmPolicy()
        {
            if (cacheWarm == null) return;
            string key, executable, root;
            Func<bool> allowed = CaptureCacheWarmAdmission(out key, out executable, out root);
            if (allowed()) cacheWarm.Update(key, executable, root, allowed);
            else cacheWarm.Cancel(false);
        }
#if PAVISE_SELFTEST
        internal Func<bool> CacheWarmAdmissionForTest()
        { string key, executable, root; return CaptureCacheWarmAdmission(out key, out executable, out root); }
#endif
    }
}
