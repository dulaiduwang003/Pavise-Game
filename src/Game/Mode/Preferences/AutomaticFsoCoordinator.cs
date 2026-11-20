using System;
using System.Collections.Generic;
using System.IO;

namespace PaviseApp
{
    // 自动全屏优化只通过这个接口读游戏模式的状态和请它记录 不直接碰 GameMode 的字段
    internal interface IAutomaticFsoHost
    {
        // 就是游戏模式自己的 sync 条目 检查时间和状态都在这把锁下读写
        object SyncRoot { get; }
        bool Stopping { get; }
        bool ProfileStoreSaveFailed { get; }
        // 自动忽略列表没读成 或者游戏库事务还有没恢复完的部分
        bool LibraryRecoveryPending { get; }
        GameDetection ActiveDetection { get; }
        // 名字带 Locked 的成员要求调用方已经持有 SyncRoot
        IEnumerable<GameProfile> ProfilesLocked { get; }
        GameProfile FindProfileLocked(string profileId);
        void RecordAutomation(string key, string state);
    }

    // 按游戏库里勾选的游戏和本局确认的渲染进程配置程序兼容性里的全屏优化 已经不需要的按记录撤销 实例归 GameMode 所有
    internal sealed class AutomaticFsoCoordinator
    {
        private sealed class Entry
        {
            internal bool Disable;

            internal bool Configured;

            internal int Failures;

            internal long CheckAt;
        }

        private readonly IAutomaticFsoHost host;
        private readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        private long checkAt;
        private string state = "";

        internal AutomaticFsoCoordinator(IAutomaticFsoHost host)
        {
            if (host == null) throw new ArgumentNullException("host");
            this.host = host;
        }

        // 调用方持有宿主的 SyncRoot
        internal string State { get { return state; } }

        internal void Invalidate()
        {
            lock (host.SyncRoot)
            {
                checkAt = 0L;
                foreach (Entry value in entries.Values)
                {
                    value.CheckAt = 0L;
                }
            }
        }

        internal void Sync(bool discoverTargets = false, long nowTicks = -1L)
        {
            if (nowTicks < 0)
            {
                nowTicks = DateTime.UtcNow.Ticks;
            }
            lock (host.SyncRoot)
            {
                if (host.Stopping || host.ProfileStoreSaveFailed || host.LibraryRecoveryPending)
                {
                    return;
                }
                GameDetection gameDetection = host.ActiveDetection;
                GameProfile gameProfile = ((gameDetection == null || gameDetection.Profile == null) ? null : host.FindProfileLocked(gameDetection.Profile.Id));
                bool canDiscoverActiveRenderer = gameDetection != null && gameDetection.Profile != null && gameDetection.RendererCandidateSelected && !gameDetection.RendererSafetyOnly && !gameDetection.RequiresGpuConfirm && gameProfile != null && gameProfile.DisableFullscreenOptimizations && RendererHandoffTracker.SameProfile(gameDetection.Profile, gameProfile);
                if ((!discoverTargets || !canDiscoverActiveRenderer || string.IsNullOrEmpty(gameDetection.RendererPath) || entries.ContainsKey(gameDetection.RendererPath)) && nowTicks < checkAt)
                {
                    return;
                }
                HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (GameProfile profile in host.ProfilesLocked)
                {
                    if (profile.DisableFullscreenOptimizations)
                    {
                        AddPath(hashSet, profile.ExecutablePath);
                        AddPath(hashSet, profile.LearnedExecutablePath);
                    }
                }
                if (canDiscoverActiveRenderer)
                {
                    AddPath(hashSet, gameDetection.RendererPath);
                }
                HashSet<string> hashSet2 = new HashSet<string>(hashSet, StringComparer.OrdinalIgnoreCase);
                bool restoredAny = false;
                try
                {
                    string[] trackedExesResult = FsoTweak.TrackedExes();
                    foreach (string item in trackedExesResult)
                    {
                        hashSet2.Add(item);
                        if (!hashSet.Contains(item))
                        {
                            restoredAny = true;
                        }
                    }
                }
                catch
                {
                    checkAt = nowTicks + 600000000;
                    state = "failed";
                    host.RecordAutomation("AutoDisableFullscreenOptimizations", state);
                    return;
                }
                foreach (string item2 in new List<string>(entries.Keys))
                {
                    if (!hashSet2.Contains(item2))
                    {
                        entries.Remove(item2);
                    }
                }
                bool allConfigured = true;
                checkAt = nowTicks + 600000000;
                foreach (string item3 in hashSet2)
                {
                    if (host.Stopping || host.ProfileStoreSaveFailed)
                    {
                        return;
                    }
                    bool disableValue = hashSet.Contains(item3);
                    Entry value;
                    if (!entries.TryGetValue(item3, out value))
                    {
                        Entry entry = new Entry();
                        entry.Disable = disableValue;
                        value = entry;
                        entries[item3] = value;
                    }
                    if (value.Disable != disableValue)
                    {
                        value.Disable = disableValue;
                        value.Configured = false;
                        value.Failures = 0;
                        value.CheckAt = 0L;
                    }
                    if (nowTicks >= value.CheckAt)
                    {
                        value.Configured = FsoTweak.SetForExe(item3, disableValue);
                        value.Failures = ((!value.Configured) ? Math.Min(value.Failures + 1, 5) : 0);
                        int upperBound = (value.Configured ? 60 : Math.Min(300, 30 << value.Failures - 1));
                        value.CheckAt = nowTicks + (long)upperBound * 10000000L;
                    }
                    allConfigured &= value.Configured;
                    checkAt = Math.Min(checkAt, value.CheckAt);
                }
                state = ((!allConfigured) ? "failed" : ((hashSet.Count > 0) ? "fso-configured" : (restoredAny ? "restored" : "")));
                host.RecordAutomation("AutoDisableFullscreenOptimizations", state);
            }
        }

        internal static void AddPath(HashSet<string> paths, string path)
        {
            if (!string.IsNullOrEmpty(path) && Path.IsPathRooted(path) && string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase) && string.Equals(GameProfileStore.NormalizePath(path), path, StringComparison.OrdinalIgnoreCase) && GamePerformanceTargetPolicy.AllowsGameTarget(path) && File.Exists(path))
            {
                paths.Add(path);
            }
        }
    }
}
