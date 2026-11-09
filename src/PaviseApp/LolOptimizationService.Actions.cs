using System;
using System.Threading;

namespace PaviseApp
{
    internal sealed partial class LolOptimizationService
    {

        public bool CleanNow()
        {
            lock (actionLock)
            {
                string root;
                string weGame;
                Discover(out root, out weGame, true);
                if (root == null)
                {
                    SetError(Lang.T("lol.err.noinstall"));
                    return false;
                }
                LolProcessSnapshot lolProcessSnapshot = LolRuntimeProcesses.Scan(root, weGame);
                Interlocked.Exchange(ref credentialGenerationChanged, 0);
                InvalidateCredentials();
                bool ready;
                LolLcuCredentials lolLcuCredentials = ResolveCredentials(root, lolProcessSnapshot.ClientRunning, out ready);
                if (lolLcuCredentials == null || !ready)
                {
                    SetError(Lang.T("lol.err.notready"));
                    return false;
                }
                lock (stateLock)
                {
                    cleanupDirty = false;
                    cleanupBurstActive = false;
                    cleanupFollowUpsRemaining = 0;
                }
                LolCleanupResult lolCleanupResult = LolRuntimeProcesses.Clean(root, weGame, true);
                bool sessionReady = LolLcuClient.IsReady(lolLcuCredentials);
                lock (stateLock)
                {
                    cleanedProcessCount += lolCleanupResult.Count;
                    releasedWorkingSetBytes += lolCleanupResult.WorkingSetBytes;
                    lcuReady = sessionReady;
                    if (sessionReady)
                    {
                        lastAction = ((lolCleanupResult.Count > 0) ? Lang.F("lol.act.cleaned", lolCleanupResult.Count.ToString()) : Lang.T("lol.act.cleanidle"));
                        lastError = "";
                    }
                    else
                    {
                        lastAction = Lang.T("lol.act.recovering");
                        lastError = Lang.T("lol.err.sessionlost");
                    }
                    lastActionUtc = DateTime.UtcNow;
                    if (cleanupDirty)
                    {
                        cleanupBurstActive = true;
                        cleanupFollowUpsRemaining = 0;
                        nextCleanupUtc = DateTime.UtcNow.AddSeconds(10.0);
                    }
                    else
                    {
                        nextCleanupUtc = DateTime.UtcNow.AddSeconds(120.0);
                    }
                    updatedUtc = DateTime.UtcNow;
                }
                LolProcessSnapshot processes = LolRuntimeProcesses.Scan(root, weGame);
                UpdateProcessState(processes);
                Logger.Log("英雄联盟专栏：精准净化完成，结束 " + lolCleanupResult.Count + " 个附加进程");
                if (!sessionReady)
                {
                    InvalidateCredentials();
                    TryRecoverLoginChain(weGame, processes, false, lolCleanupResult.Count > 0);
                    RaiseChanged();
                    return false;
                }
                RaiseChanged();
                return true;
            }
        }

        public bool RestoreNow()
        {
            lock (actionLock)
            {
                string root;
                string weGame;
                Discover(out root, out weGame, true);
                if (root == null)
                {
                    SetError(Lang.T("lol.err.noinstall"));
                    return false;
                }
                RememberHeadlessLease(root);
                ObserveHeadlessOwnerExit();
                LolLcuCredentials credentialsForRestore = GetCredentialsForRestore(root, true);
                if (credentialsForRestore == null)
                {
                    SetError(Lang.T("lol.err.nosession"));
                    return false;
                }
                bool tryRestoreUxBounded = TryRestoreUxBounded(credentialsForRestore, root);
                if (!tryRestoreUxBounded)
                {
                    InvalidateCredentials();
                    credentialsForRestore = GetCredentialsForRestore(root, true);
                    tryRestoreUxBounded = TryRestoreUxBounded(credentialsForRestore, root);
                    if (!tryRestoreUxBounded && credentialsForRestore != null)
                    {
                        ScheduleRestoreCredentialRetry();
                    }
                }
                bool gameStillRunning = tryRestoreUxBounded && (LolRuntimeProcesses.IsGameRunning(root) || IsSameMatchPhase(LolLcuClient.GetGameflowPhase(credentialsForRestore)));
                lock (stateLock)
                {
                    if (tryRestoreUxBounded)
                    {
                        headlessActive = false;
                        headlessExitSamples = 0;
                        manualUxBypassGame = gameStillRunning;
                        lastAction = Lang.T("lol.act.restored");
                        lastError = "";
                    }
                    else
                    {
                        lastError = Lang.T("lol.err.restore");
                    }
                    lastActionUtc = DateTime.UtcNow;
                    updatedUtc = DateTime.UtcNow;
                }
                bool restoreNowSucceeded = tryRestoreUxBounded;
                if (tryRestoreUxBounded)
                {
                    restoreNowSucceeded = ClearOwnedHeadlessMark();
                    if (!restoreNowSucceeded)
                    {
                        lock (stateLock)
                        {
                            lastError = Lang.T("lol.err.leaseclear");
                            lastActionUtc = DateTime.UtcNow;
                            updatedUtc = DateTime.UtcNow;
                        }
                    }
                    Logger.Log("英雄联盟专栏：大厅界面已恢复");
                }
                RaiseChanged();
                return restoreNowSucceeded;
            }
        }

        public bool HasRecordedHeadlessState()
        {
            bool stateRecorded;
            lock (stateLock)
            {
                stateRecorded = headlessActive || !string.IsNullOrEmpty(headlessLeaseRoot);
            }
            if (!stateRecorded)
            {
                return LolHeadlessLease.Exists();
            }
            return true;
        }

        private bool StartWeGame(string weGame, bool updateAction)
        {
            string weGameExecutable = LolInstallDiscovery.FindWeGameExecutable(weGame);
            if (weGameExecutable == null)
            {
                if (updateAction)
                {
                    SetError(Lang.T("lol.err.nowegame"));
                }
                return false;
            }
            if (!UserLaunch.Start(weGameExecutable))
            {
                if (updateAction)
                {
                    SetError(Lang.T("lol.err.wegamestart"));
                }
                return false;
            }
            if (updateAction)
            {
                SetAction(Lang.T("lol.act.wegamestarting"));
            }
            Logger.Log("英雄联盟专栏：已发起 WeGame 启动");
            return true;
        }

    }
}
