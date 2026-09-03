// @author bdth 2074055628@qq.com
// 文件用途 英雄联盟优化服务的安装目录发现调度

using System;
using System.Threading;

namespace PaviseApp
{
    internal sealed partial class LolOptimizationService
    {
        public void RequestDiscovery()
        {
            lock (stateLock)
            {
                discoveryRequested = true;
                discoveryMisses = 0;
                nextDiscoveryUtc = DateTime.MinValue;
            }
            Poke();
        }

        public void CancelDiscovery()
        {
            Interlocked.Exchange(ref discoveryCancelRequested, 1);
        }

        public bool AdoptRootFromPath(string executablePath)
        {
            string root = null;
            try
            {
                string dir = string.IsNullOrEmpty(executablePath) ? null : System.IO.Path.GetDirectoryName(executablePath);
                for (int i = 0; i < 6 && !string.IsNullOrEmpty(dir); i++)
                {
                    if (LolInstallDiscovery.IsValidLolRoot(dir)) { root = dir; break; }
                    dir = System.IO.Path.GetDirectoryName(dir);
                }
            }
            catch { root = null; }
            if (root == null) return false;
            bool changed;
            lock (stateLock)
            {
                changed = !string.Equals(lolRoot, root, StringComparison.OrdinalIgnoreCase) || !installationFound;
                lolRoot = root;
                installationFound = true;
                discoveryRequested = false;
                discoveryMisses = 0;
                nextDiscoveryUtc = DateTime.MinValue;
                updatedUtc = DateTime.UtcNow;
            }
            if (!changed) return true;
            InvalidateCredentials();
            Settings.SaveStr(LolRootKey, root);
            Logger.Log("英雄联盟增强 按游戏库条目定位安装目录 " + root);
            Poke();
            RaiseChanged();
            return true;
        }

        private bool DiscoveryCancelRequested()
        {
            return Volatile.Read(ref discoveryCancelRequested) != 0;
        }

        private void SetDiscovering(bool value)
        {
            lock (stateLock)
            {
                if (discovering == value) return;
                discovering = value;
                updatedUtc = DateTime.UtcNow;
            }
            Action handler = Changed;
            if (handler != null) { try { handler(); } catch { } }
        }

        private void Discover(out string root, out string weGame, bool force)
        {
            string preferredRoot;
            string preferredWeGame;
            bool shouldDiscover;
            lock (stateLock)
            {
                preferredRoot = lolRoot;
                preferredWeGame = weGameRoot;
                shouldDiscover = force || DateTime.UtcNow >= nextDiscoveryUtc;
            }
            if (shouldDiscover)
            {
                bool cacheValid = LolInstallDiscovery.IsValidLolRoot(preferredRoot);
                if (!force && !cacheValid)
                {
                    bool armed;
                    lock (stateLock) armed = discoveryRequested;
                    if (!armed)
                    {
                        lock (stateLock) nextDiscoveryUtc = DateTime.UtcNow.AddSeconds(DiscoverySettledSeconds);
                        root = null;
                        weGame = preferredWeGame;
                        return;
                    }
                }
                string discoveredRoot;
                string discoveredWeGame;
                bool armedNow;
                lock (stateLock) armedNow = discoveryRequested;
                // 安装目录已知时 手动指令只做轻量刷新 不再全盘扫 没装 WeGame 的机器每次点净化都扫盘是刑罚
                bool deep = !cacheValid || armedNow;
                bool announce = deep;
                Func<bool> cancelled = null;
                if (announce)
                {
                    Interlocked.Exchange(ref discoveryCancelRequested, 0);
                    cancelled = DiscoveryCancelRequested;
                    SetDiscovering(true);
                }
                try
                {
                    discoveredRoot = LolInstallDiscovery.FindLolRoot(preferredRoot, deep, cancelled);
                    discoveredWeGame = LolInstallDiscovery.FindWeGameRoot(
                        preferredWeGame, discoveredRoot, deep, cancelled);
                }
                finally { if (announce) SetDiscovering(false); }
                bool wasCancelled = announce && DiscoveryCancelRequested();
                bool rootChanged;
                bool weGameChanged;
                lock (stateLock)
                {
                    discoveryRequested = false;
                    rootChanged = !string.Equals(
                        lolRoot, discoveredRoot, StringComparison.OrdinalIgnoreCase);
                    weGameChanged = !string.Equals(
                        weGameRoot, discoveredWeGame, StringComparison.OrdinalIgnoreCase);
                    lolRoot = discoveredRoot;
                    weGameRoot = discoveredWeGame;
                    root = discoveredRoot;
                    weGame = discoveredWeGame;
                    installationFound = discoveredRoot != null;
                    weGameFound = LolInstallDiscovery.IsValidWeGameRoot(discoveredWeGame);
                    if (discoveredRoot == null)
                    {
                        if (wasCancelled)
                            nextDiscoveryUtc = DateTime.UtcNow.AddSeconds(DiscoveryMaxSeconds);
                        else
                        {
                            if (discoveryMisses < 8) discoveryMisses++;
                            int backoff = DiscoveryBaseSeconds;
                            for (int i = 1; i < discoveryMisses; i++)
                            {
                                backoff *= 2;
                                if (backoff >= DiscoveryMaxSeconds) break;
                            }
                            if (backoff > DiscoveryMaxSeconds) backoff = DiscoveryMaxSeconds;
                            nextDiscoveryUtc = DateTime.UtcNow.AddSeconds(backoff);
                        }
                    }
                    else
                    {
                        discoveryMisses = 0;
                        nextDiscoveryUtc = DateTime.UtcNow.AddSeconds(DiscoverySettledSeconds);
                    }
                    updatedUtc = DateTime.UtcNow;
                }
                if (rootChanged)
                {
                    InvalidateCredentials();
                    Settings.SaveStr(LolRootKey, root ?? "");
                }
                if (weGameChanged) Settings.SaveStr(WeGameRootKey, weGame ?? "");
                return;
            }
            root = preferredRoot;
            weGame = preferredWeGame;
        }

        private void InvalidateDiscovery()
        {
            lock (stateLock)
            {
                discoveryRequested = true;
                discoveryMisses = 0;
                nextDiscoveryUtc = DateTime.MinValue;
            }
        }
    }
}
