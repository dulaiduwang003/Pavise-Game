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
                bool announce = force || !cacheValid;
                Func<bool> cancelled = null;
                if (announce)
                {
                    Interlocked.Exchange(ref discoveryCancelRequested, 0);
                    cancelled = DiscoveryCancelRequested;
                    SetDiscovering(true);
                }
                try
                {
                    discoveredRoot = LolInstallDiscovery.FindLolRoot(preferredRoot, force, cancelled);
                    discoveredWeGame = LolInstallDiscovery.FindWeGameRoot(
                        preferredWeGame, discoveredRoot, force, cancelled);
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
