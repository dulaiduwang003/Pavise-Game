using System;
using System.IO;
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
            string message = null;
            try
            {
                string directoryName = (string.IsNullOrEmpty(executablePath) ? null : Path.GetDirectoryName(executablePath));
                for (int i = 0; i < 6; i++)
                {
                    if (string.IsNullOrEmpty(directoryName))
                    {
                        break;
                    }
                    if (LolInstallDiscovery.IsValidLolRoot(directoryName))
                    {
                        message = directoryName;
                        break;
                    }
                    directoryName = Path.GetDirectoryName(directoryName);
                }
            }
            catch
            {
                message = null;
            }
            if (message == null)
            {
                return false;
            }
            bool rootChanged;
            lock (stateLock)
            {
                rootChanged = !string.Equals(lolRoot, message, StringComparison.OrdinalIgnoreCase) || !installationFound;
                lolRoot = message;
                installationFound = true;
                discoveryRequested = false;
                discoveryMisses = 0;
                nextDiscoveryUtc = DateTime.MinValue;
                updatedUtc = DateTime.UtcNow;
            }
            if (!rootChanged)
            {
                return true;
            }
            InvalidateCredentials();
            Settings.SaveStr("LolInstallPath", message);
            Logger.Log("英雄联盟增强 按游戏库条目定位安装目录 " + message);
            Poke();
            RaiseChanged();
            return true;
        }

        private bool DiscoveryCancelRequested()
        {
            return Volatile.Read(ref discoveryCancelRequested) != 0;
        }

        private bool RuntimeStopped()
        {
            lock (stateLock)
            {
                return disposed || !running;
            }
        }

        private void SetDiscovering(bool value)
        {
            lock (stateLock)
            {
                if (discovering == value)
                {
                    return;
                }
                discovering = value;
                updatedUtc = DateTime.UtcNow;
            }
            Action changed = this.Changed;
            if (changed != null)
            {
                try
                {
                    changed();
                }
                catch
                {
                }
            }
        }

        private void Discover(out string root, out string weGame, bool force, bool stopWithWorker = false)
        {
            string lolRootSnapshot;
            string weGameRootSnapshot;
            bool discoveryDue;
            lock (stateLock)
            {
                lolRootSnapshot = lolRoot;
                weGameRootSnapshot = weGameRoot;
                discoveryDue = force || DateTime.UtcNow >= nextDiscoveryUtc;
            }
            if (discoveryDue)
            {
                bool isValidLolRoot = LolInstallDiscovery.IsValidLolRoot(lolRootSnapshot);
                if (!force && !isValidLolRoot)
                {
                    bool discoveryRequestedSnapshot;
                    lock (stateLock)
                    {
                        discoveryRequestedSnapshot = discoveryRequested;
                    }
                    if (!discoveryRequestedSnapshot)
                    {
                        lock (stateLock)
                        {
                            nextDiscoveryUtc = DateTime.UtcNow.AddSeconds(120.0);
                        }
                        root = null;
                        weGame = weGameRootSnapshot;
                        return;
                    }
                }
                bool discoveryRequestedSnapshot2;
                lock (stateLock)
                {
                    discoveryRequestedSnapshot2 = discoveryRequested;
                }
                bool discoveryNeeded = !isValidLolRoot || discoveryRequestedSnapshot2;
                bool discoveryActive = discoveryNeeded;
                Func<bool> func = null;
                if (discoveryActive)
                {
                    Interlocked.Exchange(ref discoveryCancelRequested, 0);
                    func = () => DiscoveryCancelRequested() || (stopWithWorker && RuntimeStopped());
                    SetDiscovering(true);
                }
                string lolRoot2;
                string weGameRoot2;
                try
                {
#if PAVISE_SELFTEST
                    lolRoot2 = FindLolRootForTest != null ? FindLolRootForTest(lolRootSnapshot, discoveryNeeded, func) : LolInstallDiscovery.FindLolRoot(lolRootSnapshot, discoveryNeeded, func);
                    weGameRoot2 = ((func != null && func()) ? weGameRootSnapshot : (FindWeGameRootForTest != null ? FindWeGameRootForTest(weGameRootSnapshot, lolRoot2, discoveryNeeded, func) : LolInstallDiscovery.FindWeGameRoot(weGameRootSnapshot, lolRoot2, discoveryNeeded, func)));
#else
                    lolRoot2 = LolInstallDiscovery.FindLolRoot(lolRootSnapshot, discoveryNeeded, func);
                    weGameRoot2 = ((func != null && func()) ? weGameRootSnapshot : LolInstallDiscovery.FindWeGameRoot(weGameRootSnapshot, lolRoot2, discoveryNeeded, func));
#endif
                }
                finally
                {
                    if (discoveryActive)
                    {
                        SetDiscovering(false);
                    }
                }
                if (stopWithWorker && RuntimeStopped())
                {
                    root = lolRootSnapshot;
                    weGame = weGameRootSnapshot;
                    return;
                }
                bool discoveryCancelled = discoveryActive && func();
                bool lolRootChanged;
                bool weGameRootChanged;
                lock (stateLock)
                {
                    discoveryRequested = false;
                    lolRootChanged = !string.Equals(lolRoot, lolRoot2, StringComparison.OrdinalIgnoreCase);
                    weGameRootChanged = !string.Equals(weGameRoot, weGameRoot2, StringComparison.OrdinalIgnoreCase);
                    lolRoot = lolRoot2;
                    weGameRoot = weGameRoot2;
                    root = lolRoot2;
                    weGame = weGameRoot2;
                    installationFound = lolRoot2 != null;
                    weGameFound = LolInstallDiscovery.IsValidWeGameRoot(weGameRoot2);
                    if (lolRoot2 == null)
                    {
                        if (discoveryCancelled)
                        {
                            nextDiscoveryUtc = DateTime.UtcNow.AddSeconds(600.0);
                        }
                        else
                        {
                            if (discoveryMisses < 8)
                            {
                                discoveryMisses++;
                            }
                            int discoverValue = 15;
                            for (int i = 1; i < discoveryMisses; i++)
                            {
                                discoverValue *= 2;
                                if (discoverValue >= 600)
                                {
                                    break;
                                }
                            }
                            if (discoverValue > 600)
                            {
                                discoverValue = 600;
                            }
                            nextDiscoveryUtc = DateTime.UtcNow.AddSeconds(discoverValue);
                        }
                    }
                    else
                    {
                        discoveryMisses = 0;
                        nextDiscoveryUtc = DateTime.UtcNow.AddSeconds(120.0);
                    }
                    updatedUtc = DateTime.UtcNow;
                }
                if (lolRootChanged)
                {
                    InvalidateCredentials();
                    Settings.SaveStr("LolInstallPath", root ?? "");
                }
                if (weGameRootChanged)
                {
                    Settings.SaveStr("LolWeGamePath", weGame ?? "");
                }
            }
            else
            {
                root = lolRootSnapshot;
                weGame = weGameRootSnapshot;
            }
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
