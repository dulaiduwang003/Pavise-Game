using System;
using System.IO;
using System.Threading;

namespace PaviseApp
{
    internal sealed partial class LolOptimizationService
    {

        private bool SetHeadlessMark(string root, int gamePid, long gameCreation)
        {
            string existing; LolHeadlessLeaseInfo previous;
            if (!Settings.TryLoadStr("LolHeadlessEngagedRoot", out existing)) return false;
            if (!string.IsNullOrEmpty(existing) && (!LolHeadlessLease.TryParse(existing, out previous)
                || !SameRootPath(previous.Root, root) || previous.GamePid != gamePid || previous.GameCreation != gameCreation)) return false;
            if (!LolHeadlessLease.Set(root, gamePid, gameCreation))
            {
                return false;
            }
            lock (stateLock)
            {
                headlessLeaseRoot = root;
                headlessLeaseGamePid = gamePid;
                headlessLeaseGameCreation = gameCreation;
                headlessLeaseReleasePending = false;
            }
            return true;
        }

        private bool ClearOwnedHeadlessMark()
        {
            string headlessLeaseRootSnapshot;
            int headlessLeaseGamePidSnapshot;
            long headlessLeaseGameCreationSnapshot;
            lock (stateLock)
            {
                headlessLeaseRootSnapshot = headlessLeaseRoot;
                headlessLeaseGamePidSnapshot = headlessLeaseGamePid;
                headlessLeaseGameCreationSnapshot = headlessLeaseGameCreation;
            }
            if (string.IsNullOrEmpty(headlessLeaseRootSnapshot))
            {
                return true;
            }
            string recoveryStage = HeadlessRecoveryStage(headlessLeaseRootSnapshot, headlessLeaseGamePidSnapshot, headlessLeaseGameCreationSnapshot, "clear-mark");
            if (!headlessFailures.Allowed(headlessLeaseGamePidSnapshot, headlessLeaseGameCreationSnapshot, headlessLeaseRootSnapshot, recoveryStage)) return false;
            bool leaseCleared = headlessLeaseGamePidSnapshot > 0 && headlessLeaseGameCreationSnapshot > 0 && LolHeadlessLease.ClearIfMatches(headlessLeaseRootSnapshot, headlessLeaseGamePidSnapshot, headlessLeaseGameCreationSnapshot);
            string current; LolHeadlessLeaseInfo remaining;
            bool confirmedReleased = Settings.TryLoadStr("LolHeadlessEngagedRoot", out current)
                && (string.IsNullOrEmpty(current) || LolHeadlessLease.TryParse(current, out remaining)
                && (!SameRootPath(remaining.Root, headlessLeaseRootSnapshot) || remaining.GamePid != headlessLeaseGamePidSnapshot || remaining.GameCreation != headlessLeaseGameCreationSnapshot));
            bool releaseConfirmed = leaseCleared || confirmedReleased;
            if (releaseConfirmed) headlessFailures.Succeeded(headlessLeaseGamePidSnapshot, headlessLeaseGameCreationSnapshot, headlessLeaseRootSnapshot, recoveryStage);
            else Logger.Warn("LOL 恢复标记保存失败=" + headlessFailures.Failed(headlessLeaseGamePidSnapshot, headlessLeaseGameCreationSnapshot, headlessLeaseRootSnapshot, recoveryStage) + "/3；保留持有标记");
            lock (stateLock)
            {
                if (SameLocalLeaseLocked(headlessLeaseRootSnapshot, headlessLeaseGamePidSnapshot, headlessLeaseGameCreationSnapshot))
                {
                    headlessLeaseReleasePending = !releaseConfirmed;
                    if (releaseConfirmed)
                    {
                        headlessLeaseRoot = null;
                        headlessLeaseGamePid = 0;
                        headlessLeaseGameCreation = 0L;
                    }
                }
            }
            return releaseConfirmed;
        }

        private void RememberHeadlessLease(string root)
        {
            string stored;
            LolHeadlessLeaseInfo info;
            if (!Settings.TryLoadStr("LolHeadlessEngagedRoot", out stored)) return;
            if (!string.IsNullOrEmpty(stored) && !LolHeadlessLease.TryParse(stored, out info)) return;
            bool matchingLease = LolHeadlessLease.TryParse(stored, out info) && SameRootPath(info.Root, root);
            lock (stateLock)
            {
                if (matchingLease)
                {
                    bool sameLocalLeaseLockedSucceeded = SameLocalLeaseLocked(info.Root, info.GamePid, info.GameCreation);
                    headlessLeaseRoot = info.Root;
                    headlessLeaseGamePid = info.GamePid;
                    headlessLeaseGameCreation = info.GameCreation;
                    if (!sameLocalLeaseLockedSucceeded)
                    {
                        headlessLeaseReleasePending = false;
                    }
                }
                else if (SameRootPath(headlessLeaseRoot, root))
                {
                    headlessLeaseRoot = null;
                    headlessLeaseGamePid = 0;
                    headlessLeaseGameCreation = 0L;
                    headlessLeaseReleasePending = false;
                }
            }
        }

        private bool SameLocalLeaseLocked(string root, int pid, long creation)
        {
            if (headlessLeaseGamePid == pid && headlessLeaseGameCreation == creation)
            {
                return SameRootPath(headlessLeaseRoot, root);
            }
            return false;
        }

        private void ForgetRememberedHeadlessLease()
        {
            string stored;
            if (!Settings.TryLoadStr("LolHeadlessEngagedRoot", out stored) || !string.IsNullOrEmpty(stored)) return;
            lock (stateLock)
            {
                headlessLeaseRoot = null;
                headlessLeaseGamePid = 0;
                headlessLeaseGameCreation = 0L;
                headlessLeaseReleasePending = false;
            }
        }

        private static bool SameRootPath(string left, string right)
        {
            try
            {
                string getFullPathTrimmed = Path.GetFullPath(left ?? "").TrimEnd('\\');
                string getFullPathTrimmed2 = Path.GetFullPath(right ?? "").TrimEnd('\\');
                return getFullPathTrimmed.Length > 2 && getFullPathTrimmed2.Length > 2 && string.Equals(getFullPathTrimmed, getFullPathTrimmed2, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static bool HasHeadlessMark()
        {
            return LolHeadlessLease.Exists();
        }

        private static bool HasHeadlessMark(string root)
        {
            return LolHeadlessLease.MatchesRoot(root);
        }

        private bool AdoptHeadlessLease(string root, LolProcessSnapshot processes, bool active, bool sameMatch)
        {
            int pid;
            long creation;
            if (!active && sameMatch && processes.GameRunning && LolRuntimeProcesses.TryGetGameIdentity(root, processes.GameProcessId, out pid, out creation) && LolHeadlessLease.Matches(root, pid, creation))
            {
                lock (stateLock)
                {
                    headlessActive = true;
                    active = true;
                }
            }
            return active;
        }

        private bool ReleaseOnClientExit(bool active, bool clientExitConfirmed)
        {
            if (clientExitConfirmed) ObserveHeadlessOwnerExit();
            if (active && clientExitConfirmed)
            {
                lock (stateLock)
                {
                    headlessActive = false;
                    headlessExitSamples = 0;
                    ResetMatchSafetyStateLocked();
                }
                ClearOwnedHeadlessMark();
                active = false;
            }
            else if (clientExitConfirmed)
            {
                lock (stateLock)
                {
                    ResetMatchSafetyStateLocked();
                }
                ClearOwnedHeadlessMark();
            }
            return active;
        }

        private void RunHeadlessKillStage(LolLcuCredentials credentials, string root, string weGame, bool serviceEnabled, bool noUxEnabled, bool ready, bool inProgress, ref LolProcessSnapshot processes)
        {
            if (!serviceEnabled || !noUxEnabled || !ready || !inProgress)
            {
                return;
            }
            bool uxKillDue;
            lock (stateLock)
            {
                uxKillDue = !headlessActive && !manualUxBypassGame && processes.MainUxProcessCount > 0 && DateTime.UtcNow >= nextUxKillUtc;
            }
            if (!uxKillDue)
            {
                return;
            }
            lock (stateLock)
            {
                nextUxKillUtc = DateTime.UtcNow.AddSeconds(10.0);
            }
            string gameflowPhase = LolLcuClient.GetGameflowPhase(credentials);
            if (!string.Equals(gameflowPhase, "InProgress", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            int pid;
            long creation;
            bool gameIdentity = LolRuntimeProcesses.TryGetGameIdentity(root, processes.GameProcessId, out pid, out creation);
            if (gameIdentity && !headlessFailures.Allowed(pid, creation, root, "apply")) return;
            bool leaseMarked = false, watchdogStarted = false, uxStopped = false;
            try
            {
                leaseMarked = gameIdentity && SetHeadlessMark(root, pid, creation);
                watchdogStarted = leaseMarked && LolWatchdog.StartDetached(root, pid, creation);
                uxStopped = watchdogStarted && LolLcuClient.KillUx(credentials, root);
            }
            finally
            {
                if (uxStopped) headlessFailures.Succeeded(pid, creation, root, "apply");
                else if (gameIdentity) Logger.Warn("LOL 无头客户端应用失败=" + headlessFailures.Failed(pid, creation, root, "apply")
                    + "/3 目标=" + pid + "/" + creation + "；已有恢复所有权继续保留");
            }
            // 超时或请求失败不能证明辅助进程或改动不存在；保留租约，直到恢复核验完成。
            lock (stateLock)
            {
                if (uxStopped)
                {
                    headlessActive = true;
                    headlessExitSamples = 0;
                    lastAction = Lang.T("lol.act.headless");
                    lastError = "";
                    lastActionUtc = DateTime.UtcNow;
                }
                else
                {
                    lastError = ((!gameIdentity) ? Lang.T("lol.err.gameidentity") : ((!leaseMarked) ? Lang.T("lol.err.lease") : ((!watchdogStarted) ? Lang.T("lol.err.watchdog") : Lang.T("lol.err.killux"))));
                    lastActionUtc = DateTime.UtcNow;
                }
                updatedUtc = DateTime.UtcNow;
            }
            if (uxStopped)
            {
                Logger.Log("英雄联盟专栏：独立恢复器已确认守护，对局真无头已启用");
                Thread.Sleep(400);
                processes = LolRuntimeProcesses.Scan(root, weGame, true);
            }
        }

        private bool RestoreCore(LolLcuCredentials credentials, string root)
        {
            return RestoreCore(credentials, root, false);
        }

        private bool RestoreCore(LolLcuCredentials credentials, string root, bool force)
        {
            DateTime utcNow = DateTime.UtcNow;
            if (!force && utcNow < nextRestoreCredentialLookupUtc)
            {
                return false;
            }
            if (credentials == null)
            {
                credentials = GetCredentialsForRestore(root, force);
            }
            bool tryRestoreUxBounded = TryRestoreUxBounded(credentials, root);
            lock (stateLock)
            {
                string a = lastAction;
                string a2 = lastError;
                if (tryRestoreUxBounded)
                {
                    headlessActive = false;
                    headlessExitSamples = 0;
                    lastAction = Lang.T("lol.act.restored");
                    lastError = "";
                    credentialLookupFailures = 0;
                    restoreCredentialLookupFailures = 0;
                    nextRestoreCredentialLookupUtc = DateTime.MinValue;
                }
                else
                {
                    lastError = Lang.T("lol.err.restorepending");
                    if (credentials != null)
                    {
                        ScheduleRestoreCredentialRetry();
                    }
                }
                if (!string.Equals(a, lastAction, StringComparison.Ordinal) || !string.Equals(a2, lastError, StringComparison.Ordinal))
                {
                    lastActionUtc = DateTime.UtcNow;
                }
                updatedUtc = DateTime.UtcNow;
            }
            if (tryRestoreUxBounded)
            {
                ClearOwnedHeadlessMark();
                Logger.Log("英雄联盟专栏：大厅界面已自动恢复");
            }
            return tryRestoreUxBounded;
        }

        private string HeadlessRecoveryStage(string root, int pid, long birth, string stage)
        {
            lock (stateLock) return stage + (exitedHeadlessLeases.Contains(root + "|" + pid + "|" + birth) ? "-exited" : "");
        }

        private void ObserveHeadlessOwnerExit()
        {
            string root; int pid; long birth;
            lock (stateLock)
            {
                root = headlessLeaseRoot; pid = headlessLeaseGamePid; birth = headlessLeaseGameCreation;
                if (pid <= 0 || birth <= 0 || string.IsNullOrEmpty(root) || exitedHeadlessLeases.Contains(root + "|" + pid + "|" + birth)) return;
            }
            if (!LolWatchdog.OriginalGameIdentityGone(root, pid, birth)) return;
            lock (stateLock) if (SameLocalLeaseLocked(root, pid, birth)) exitedHeadlessLeases.Add(root + "|" + pid + "|" + birth);
        }

        private bool TryRestoreUxBounded(LolLcuCredentials credentials, string root)
        {
            // 缺少凭据属于正常的发现或可用性结果，不算写入失败。
            if (credentials == null) return false;
            int pid; long birth;
            lock (stateLock) { pid = headlessLeaseGamePid; birth = headlessLeaseGameCreation; }
            string stage = HeadlessRecoveryStage(root, pid, birth, "restore");
            if (!headlessFailures.Allowed(pid, birth, root, stage)) return false;
            bool ok = false;
            try { ok = LolLcuClient.RestoreUx(credentials, root); return ok; }
            finally
            {
                if (ok) headlessFailures.Succeeded(pid, birth, root, stage);
                else Logger.Warn("LOL 界面恢复失败=" + headlessFailures.Failed(pid, birth, root, stage) + "/3 目标=" + pid + "/" + birth + "；保留租约");
            }
        }

    }
}
