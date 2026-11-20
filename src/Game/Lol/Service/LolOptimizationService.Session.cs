using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal sealed partial class LolOptimizationService
    {

        private void GoIdle()
        {
            bool stateChanged;
            lock (stateLock)
            {
                stateChanged = clientRunning || gameRunning || lcuReady || phase.Length > 0 || weGameProcessCount != 0 || crossProcessCount != 0 || uxProcessCount != 0;
                clientRunning = false;
                gameRunning = false;
                lcuReady = false;
                phase = "";
                weGameProcessCount = 0;
                weGameMainRunning = false;
                crossProcessCount = 0;
                uxProcessCount = 0;
                headlessExitSamples = 0;
                lastAction = Lang.T("lol.act.disabled");
                lastError = "";
                updatedUtc = DateTime.UtcNow;
            }
            InvalidateCredentials();
            if (stateChanged)
            {
                RaiseChanged();
            }
        }

        internal static bool IsVerifiedCleanupSession(bool clientRunning, bool lcuReady, string currentPhase, bool gameRunning, bool matchActive)
        {
            if (!clientRunning || !lcuReady || string.IsNullOrEmpty(currentPhase))
            {
                return false;
            }
            if (IsSameMatchPhase(currentPhase))
            {
                if (matchActive)
                {
                    return gameRunning;
                }
                return false;
            }
            if (!gameRunning)
            {
                return IsConfirmedMatchExitPhase(currentPhase);
            }
            return false;
        }

        internal static bool ShouldResetMatchSafetyState(bool clientExitConfirmed, bool matchExitConfirmed, bool matchStateActive)
        {
            if (!clientExitConfirmed)
            {
                if (matchExitConfirmed)
                {
                    return matchStateActive;
                }
                return false;
            }
            return true;
        }

        internal static bool RegisterUxRespawn(DateTime nowUtc, Queue<DateTime> recent)
        {
            if (recent == null)
            {
                throw new ArgumentNullException("recent");
            }
            while (recent.Count > 0 && (nowUtc < recent.Peek() || (nowUtc - recent.Peek()).TotalSeconds >= 60.0))
            {
                recent.Dequeue();
            }
            recent.Enqueue(nowUtc);
            return recent.Count > 3;
        }

        internal static bool RegisterCleanupKillCycle(DateTime nowUtc, Queue<DateTime> recent)
        {
            if (recent == null)
            {
                throw new ArgumentNullException("recent");
            }
            while (recent.Count > 0 && (nowUtc < recent.Peek() || (nowUtc - recent.Peek()).TotalSeconds >= 60.0))
            {
                recent.Dequeue();
            }
            recent.Enqueue(nowUtc);
            return recent.Count > 3;
        }

        internal static bool IsConfirmedMatchExitPhase(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }
            if (!string.Equals(value, "None", StringComparison.OrdinalIgnoreCase) && !string.Equals(value, "Lobby", StringComparison.OrdinalIgnoreCase) && !string.Equals(value, "Matchmaking", StringComparison.OrdinalIgnoreCase) && !string.Equals(value, "CheckedIntoTournament", StringComparison.OrdinalIgnoreCase) && !string.Equals(value, "ReadyCheck", StringComparison.OrdinalIgnoreCase) && !string.Equals(value, "ChampSelect", StringComparison.OrdinalIgnoreCase) && !string.Equals(value, "FailedToLaunch", StringComparison.OrdinalIgnoreCase) && !string.Equals(value, "PreEndOfGame", StringComparison.OrdinalIgnoreCase) && !string.Equals(value, "EndOfGame", StringComparison.OrdinalIgnoreCase) && !string.Equals(value, "WaitingForStats", StringComparison.OrdinalIgnoreCase))
            {
                return string.Equals(value, "TerminatedInError", StringComparison.OrdinalIgnoreCase);
            }
            return true;
        }

        internal static bool IsSameMatchPhase(string value)
        {
            if (!string.Equals(value, "InProgress", StringComparison.OrdinalIgnoreCase) && !string.Equals(value, "Reconnect", StringComparison.OrdinalIgnoreCase))
            {
                return string.Equals(value, "GameStart", StringComparison.OrdinalIgnoreCase);
            }
            return true;
        }

        internal static bool ConfirmClientExit(bool scanSucceeded, bool clientRunning, bool gameRunning, ref int absentSamples)
        {
            if (scanSucceeded)
            {
                if (clientRunning || gameRunning)
                {
                    absentSamples = 0;
                }
                else if (absentSamples < int.MaxValue)
                {
                    absentSamples++;
                }
            }
            return absentSamples >= 4;
        }

        private void ResetMatchSafetyStateLocked()
        {
            manualUxBypassGame = false;
            uxRespawns.Clear();
            nextUxKillUtc = DateTime.MinValue;
            cleanupDirty = false;
            cleanupMatchActive = false;
            cleanupBurstActive = false;
            cleanupFollowUpsRemaining = 0;
            cleanupCircuitOpen = false;
            cleanupKillCycles.Clear();
            nextCleanupUtc = DateTime.MinValue;
        }

        private void UpdateProcessState(LolProcessSnapshot processes)
        {
            if (processes == null)
            {
                return;
            }
            lock (stateLock)
            {
                clientRunning = processes.ClientRunning;
                gameRunning = processes.GameRunning;
                weGameProcessCount = processes.WeGameProcessCount;
                weGameMainRunning = processes.WeGameMainRunning;
                crossProcessCount = processes.CrossProcessCount;
                uxProcessCount = processes.UxProcessCount;
                updatedUtc = DateTime.UtcNow;
            }
        }

        private void SetError(string message)
        {
            lock (stateLock)
            {
                lastError = message ?? "";
                lastActionUtc = DateTime.UtcNow;
                updatedUtc = DateTime.UtcNow;
            }
            RaiseChanged();
        }

        private void Poke()
        {
            try
            {
                pokeEvent.Set();
            }
            catch
            {
            }
        }

        internal static bool SamePublishedState(LolOptimizationSnapshot left, LolOptimizationSnapshot right)
        {
            if (left == null || right == null)
            {
                return left == right;
            }
            if (left.Running == right.Running && left.Enabled == right.Enabled && left.CleanupEnabled == right.CleanupEnabled && left.HeadlessEnabled == right.HeadlessEnabled && string.Equals(left.LolRoot, right.LolRoot, StringComparison.OrdinalIgnoreCase) && string.Equals(left.WeGameRoot, right.WeGameRoot, StringComparison.OrdinalIgnoreCase) && left.InstallationFound == right.InstallationFound && left.WeGameFound == right.WeGameFound && left.ClientRunning == right.ClientRunning && left.GameRunning == right.GameRunning && left.LcuReady == right.LcuReady && string.Equals(left.Phase, right.Phase, StringComparison.Ordinal) && left.HeadlessActive == right.HeadlessActive && left.WeGameProcessCount == right.WeGameProcessCount && left.WeGameMainRunning == right.WeGameMainRunning && left.CrossProcessCount == right.CrossProcessCount && left.UxProcessCount == right.UxProcessCount && left.CleanedProcessCount == right.CleanedProcessCount && left.ReleasedWorkingSetBytes == right.ReleasedWorkingSetBytes && string.Equals(left.LastAction, right.LastAction, StringComparison.Ordinal) && string.Equals(left.LastError, right.LastError, StringComparison.Ordinal))
            {
                return left.LastActionUtc == right.LastActionUtc;
            }
            return false;
        }

        internal void RaiseChanged()
        {
            Action changed = this.Changed;
            if (changed == null)
            {
                return;
            }
            LolOptimizationSnapshot snapshot = GetSnapshot();
            lock (publishLock)
            {
                if (SamePublishedState(lastPublishedSnapshot, snapshot))
                {
                    return;
                }
                lastPublishedSnapshot = snapshot;
            }
            try
            {
                changed();
            }
            catch
            {
            }
        }

    }
}
