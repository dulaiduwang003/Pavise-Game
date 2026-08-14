// @author bdth 2074055628@qq.com
// 文件用途 英雄联盟优化服务的凭据解析与重试调度

using System;

namespace PaviseApp
{
    internal sealed partial class LolOptimizationService
    {
        private LolLcuCredentials ResolveCredentials(
            string root, bool isClientRunning, out bool ready)
        {
            ready = false;
            if (!isClientRunning)
            {
                InvalidateCredentials();
                return null;
            }
            if (!string.Equals(
                cachedCredentialRoot, root, StringComparison.OrdinalIgnoreCase))
                InvalidateCredentials();
            LolLcuCredentials credentials = cachedCredentials;
            if (credentials != null)
            {
                ready = LolLcuClient.IsReady(credentials);
                sessionVerified = ready;
                if (ready)
                {
                    lcuFailureStreak = 0;
                    credentialLookupFailures = 0;
                    nextCredentialRefreshUtc = DateTime.MinValue;
                    return credentials;
                }
                if (DateTime.UtcNow < nextCredentialRefreshUtc) return credentials;
            }
            else if (DateTime.UtcNow < nextCredentialRefreshUtc)
            {
                return null;
            }
            credentials = LolLcuCredentialSource.Find(root);
            if (credentials == null)
            {
                ScheduleCredentialRetry(root);
                return null;
            }
            cachedCredentials = credentials;
            cachedCredentialRoot = root;
            nextCredentialRefreshUtc = DateTime.UtcNow.AddSeconds(10);
            ready = LolLcuClient.IsReady(credentials);
            sessionVerified = ready;
            if (ready)
            {
                lcuFailureStreak = 0;
                credentialLookupFailures = 0;
                nextCredentialRefreshUtc = DateTime.MinValue;
                nextSessionVerifyUtc = DateTime.UtcNow.AddSeconds(
                    SessionVerifiedLobbySeconds);
            }
            return credentials;
        }

        private LolLcuCredentials ProbeLcu(
            string root, bool isClientRunning, out string currentPhase, out bool ready)
        {
            currentPhase = null;
            ready = false;
            if (!isClientRunning)
            {
                InvalidateCredentials();
                return null;
            }
            if (!string.Equals(
                cachedCredentialRoot, root, StringComparison.OrdinalIgnoreCase))
                InvalidateCredentials();
            LolLcuCredentials credentials = cachedCredentials;
            if (credentials == null)
            {
                if (DateTime.UtcNow < nextCredentialRefreshUtc) return null;
                credentials = LolLcuCredentialSource.Find(root);
                if (credentials == null)
                {
                    ScheduleCredentialRetry(root);
                    return null;
                }
                cachedCredentials = credentials;
                cachedCredentialRoot = root;
                nextCredentialRefreshUtc = DateTime.UtcNow.AddSeconds(10);
                sessionVerified = false;
                nextSessionVerifyUtc = DateTime.MinValue;
            }
            currentPhase = LolLcuClient.GetGameflowPhase(credentials);
            if (currentPhase == null)
            {
                lcuFailureStreak++;
                if (lcuFailureStreak >= 2)
                {
                    sessionVerified = false;
                    nextSessionVerifyUtc = DateTime.MinValue;
                }
                if (lcuFailureStreak >= 3)
                {
                    InvalidateCredentials(false);
                    ScheduleCredentialRetry(root);
                }
                return credentials;
            }
            lcuFailureStreak = 0;
            credentialLookupFailures = 0;
            bool inProgress = string.Equals(
                currentPhase, "InProgress", StringComparison.OrdinalIgnoreCase);
            if (DateTime.UtcNow >= nextSessionVerifyUtc)
            {
                sessionVerified = LolLcuClient.IsReady(credentials);
                nextSessionVerifyUtc = DateTime.UtcNow.AddSeconds(
                    sessionVerified
                        ? (inProgress
                            ? SessionVerifiedGameSeconds : SessionVerifiedLobbySeconds)
                        : SessionVerifyRetrySeconds);
            }
            ready = sessionVerified;
            return credentials;
        }

        private void TryRecoverLoginChain(
            string weGame,
            LolProcessSnapshot processes,
            bool inProgress,
            bool cleanupChangedProcesses)
        {
            if (!cleanupChangedProcesses || processes == null
                || !processes.ClientRunning)
                return;
            if (inProgress || processes.GameRunning) return;
            if (processes.WeGameProcessCount > 0) return;
            if (DateTime.UtcNow < nextRecoveryUtc) return;
            nextRecoveryUtc = DateTime.UtcNow.AddSeconds(RecoveryMinIntervalSeconds);
            StartWeGame(weGame, false);
        }

        private LolLcuCredentials GetCredentialsForRestore(
            string root, bool force)
        {
            if (!string.Equals(
                cachedCredentialRoot, root, StringComparison.OrdinalIgnoreCase))
                InvalidateCredentials();
            if (cachedCredentials != null) return cachedCredentials;
            if (!force && DateTime.UtcNow < nextRestoreCredentialLookupUtc)
                return null;
            cachedCredentials = LolLcuCredentialSource.Find(root);
            if (cachedCredentials == null)
            {
                ScheduleCredentialRetry(root);
                ScheduleRestoreCredentialRetry();
                return null;
            }
            cachedCredentialRoot = root;
            nextCredentialRefreshUtc = DateTime.UtcNow.AddSeconds(10);
            restoreCredentialLookupFailures = 0;
            nextRestoreCredentialLookupUtc = DateTime.MinValue;
            return cachedCredentials;
        }

        private void InvalidateCredentials()
        {
            InvalidateCredentials(true);
        }

        private void InvalidateCredentials(bool resetBackoff)
        {
            cachedCredentials = null;
            cachedCredentialRoot = null;
            if (resetBackoff)
            {
                nextCredentialRefreshUtc = DateTime.MinValue;
                credentialLookupFailures = 0;
                nextRestoreCredentialLookupUtc = DateTime.MinValue;
                restoreCredentialLookupFailures = 0;
            }
            sessionVerified = false;
            nextSessionVerifyUtc = DateTime.MinValue;
            lcuFailureStreak = 0;
        }

        private void ScheduleCredentialRetry(string root)
        {
            cachedCredentials = null;
            cachedCredentialRoot = root;
            if (credentialLookupFailures < int.MaxValue) credentialLookupFailures++;
            nextCredentialRefreshUtc = DateTime.UtcNow.AddSeconds(
                CredentialRetrySeconds(credentialLookupFailures));
            sessionVerified = false;
            nextSessionVerifyUtc = DateTime.MinValue;
            lcuFailureStreak = 0;
        }

        private void ScheduleRestoreCredentialRetry()
        {
            if (restoreCredentialLookupFailures < int.MaxValue)
                restoreCredentialLookupFailures++;
            nextRestoreCredentialLookupUtc = DateTime.UtcNow.AddSeconds(
                RestoreCredentialRetrySeconds(
                    restoreCredentialLookupFailures));
        }

        internal static int CredentialRetrySeconds(int failures)
        {
            if (failures <= 1) return 5;
            if (failures == 2) return 10;
            if (failures == 3) return 30;
            return 60;
        }

        internal static int RestoreCredentialRetrySeconds(int failures)
        {
            if (failures <= 1) return 2;
            if (failures == 2) return 5;
            if (failures == 3) return 10;
            return 30;
        }
    }
}
