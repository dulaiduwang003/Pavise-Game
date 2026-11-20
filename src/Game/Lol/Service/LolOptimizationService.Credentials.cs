using System;

namespace PaviseApp
{
    internal sealed partial class LolOptimizationService
    {

        private LolLcuCredentials ResolveCredentials(string root, bool isClientRunning, out bool ready)
        {
            ready = false;
            if (!isClientRunning)
            {
                InvalidateCredentials();
                return null;
            }
            if (!string.Equals(cachedCredentialRoot, root, StringComparison.OrdinalIgnoreCase))
            {
                InvalidateCredentials();
            }
            LolLcuCredentials lolLcuCredentials = cachedCredentials;
            if (lolLcuCredentials != null)
            {
                ready = LolLcuClient.IsReady(lolLcuCredentials);
                sessionVerified = ready;
                if (ready)
                {
                    lcuFailureStreak = 0;
                    credentialLookupFailures = 0;
                    nextCredentialRefreshUtc = DateTime.MinValue;
                    return lolLcuCredentials;
                }
                if (DateTime.UtcNow < nextCredentialRefreshUtc)
                {
                    return lolLcuCredentials;
                }
            }
            else if (DateTime.UtcNow < nextCredentialRefreshUtc)
            {
                return null;
            }
            if (!LolLcuCredentialSource.TryFind(root, out lolLcuCredentials)) return cachedCredentials;
            if (lolLcuCredentials == null)
            {
                ScheduleCredentialRetry(root);
                return null;
            }
            cachedCredentials = lolLcuCredentials;
            cachedCredentialRoot = root;
            nextCredentialRefreshUtc = DateTime.UtcNow.AddSeconds(10.0);
            ready = LolLcuClient.IsReady(lolLcuCredentials);
            sessionVerified = ready;
            if (ready)
            {
                lcuFailureStreak = 0;
                credentialLookupFailures = 0;
                nextCredentialRefreshUtc = DateTime.MinValue;
                nextSessionVerifyUtc = DateTime.UtcNow.AddSeconds(15.0);
            }
            return lolLcuCredentials;
        }

        private LolLcuCredentials ProbeLcu(string root, bool isClientRunning, out string currentPhase, out bool ready)
        {
            currentPhase = null;
            ready = false;
            if (!isClientRunning)
            {
                InvalidateCredentials();
                return null;
            }
            if (!string.Equals(cachedCredentialRoot, root, StringComparison.OrdinalIgnoreCase))
            {
                InvalidateCredentials();
            }
            LolLcuCredentials lolLcuCredentials = cachedCredentials;
            if (lolLcuCredentials == null)
            {
                if (DateTime.UtcNow < nextCredentialRefreshUtc)
                {
                    return null;
                }
                if (!LolLcuCredentialSource.TryFind(root, out lolLcuCredentials)) return null;
                if (lolLcuCredentials == null)
                {
                    ScheduleCredentialRetry(root);
                    return null;
                }
                cachedCredentials = lolLcuCredentials;
                cachedCredentialRoot = root;
                nextCredentialRefreshUtc = DateTime.UtcNow.AddSeconds(10.0);
                sessionVerified = false;
                nextSessionVerifyUtc = DateTime.MinValue;
            }
            currentPhase = LolLcuClient.GetGameflowPhase(lolLcuCredentials);
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
                return lolLcuCredentials;
            }
            lcuFailureStreak = 0;
            credentialLookupFailures = 0;
            bool valuesEqual = string.Equals(currentPhase, "InProgress", StringComparison.OrdinalIgnoreCase);
            if (DateTime.UtcNow >= nextSessionVerifyUtc)
            {
                sessionVerified = LolLcuClient.IsReady(lolLcuCredentials);
                nextSessionVerifyUtc = DateTime.UtcNow.AddSeconds((!sessionVerified) ? 5 : (valuesEqual ? 60 : 15));
            }
            ready = sessionVerified;
            return lolLcuCredentials;
        }

        private void TryRecoverLoginChain(string weGame, LolProcessSnapshot processes, bool inProgress, bool cleanupChangedProcesses)
        {
            if (cleanupChangedProcesses && processes != null && processes.ClientRunning && !inProgress && !processes.GameRunning && processes.WeGameProcessCount <= 0 && !(DateTime.UtcNow < nextRecoveryUtc))
            {
                nextRecoveryUtc = DateTime.UtcNow.AddSeconds(120.0);
                StartWeGame(weGame, false);
            }
        }

        private LolLcuCredentials GetCredentialsForRestore(string root, bool force)
        {
            if (!string.Equals(cachedCredentialRoot, root, StringComparison.OrdinalIgnoreCase))
            {
                InvalidateCredentials();
            }
            if (cachedCredentials != null)
            {
                return cachedCredentials;
            }
            if (!force && DateTime.UtcNow < nextRestoreCredentialLookupUtc)
            {
                return null;
            }
            LolLcuCredentials found;
            if (!LolLcuCredentialSource.TryFind(root, out found)) return cachedCredentials;
            cachedCredentials = found;
            if (cachedCredentials == null)
            {
                ScheduleCredentialRetry(root);
                ScheduleRestoreCredentialRetry();
                return null;
            }
            cachedCredentialRoot = root;
            nextCredentialRefreshUtc = DateTime.UtcNow.AddSeconds(10.0);
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
            if (credentialLookupFailures < int.MaxValue)
            {
                credentialLookupFailures++;
            }
            nextCredentialRefreshUtc = DateTime.UtcNow.AddSeconds(CredentialRetrySeconds(credentialLookupFailures));
            sessionVerified = false;
            nextSessionVerifyUtc = DateTime.MinValue;
            lcuFailureStreak = 0;
        }

        private void ScheduleRestoreCredentialRetry()
        {
            if (restoreCredentialLookupFailures < int.MaxValue)
            {
                restoreCredentialLookupFailures++;
            }
            nextRestoreCredentialLookupUtc = DateTime.UtcNow.AddSeconds(RestoreCredentialRetrySeconds(restoreCredentialLookupFailures));
        }

        internal static int CredentialRetrySeconds(int failures)
        {
            if (failures <= 1)
            {
                return 5;
            }
            switch (failures)
            {
            case 2:
                return 10;
            case 3:
                return 30;
            default:
                return 60;
            }
        }

        internal static int RestoreCredentialRetrySeconds(int failures)
        {
            if (failures <= 1)
            {
                return 2;
            }
            switch (failures)
            {
            case 2:
                return 5;
            case 3:
                return 10;
            default:
                return 30;
            }
        }

    }
}
