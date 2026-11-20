using System;
using System.IO;
using System.Threading;

namespace PaviseApp
{
    internal sealed partial class LolOptimizationService
    {

        public void NotifyProcessChanges(ProcessChangeBatch batch)
        {
            if (batch == null)
            {
                return;
            }
            bool overflowed = batch.Overflowed;
            bool overflowed2 = batch.Overflowed;
            bool credentialsChanged = false;
            bool overflowed3 = batch.Overflowed;
            string root;
            string weGame;
            lock (stateLock)
            {
                if (!running || disposed)
                {
                    return;
                }
                root = lolRoot;
                weGame = weGameRoot;
            }
            ProcessChange[] changes = batch.Changes;
            foreach (ProcessChange processChange in changes)
            {
                bool isCredentialDiscoveryStartEvent = IsCredentialDiscoveryStartEvent(processChange);
                if (processChange != null && (isCredentialDiscoveryStartEvent || LolRuntimeProcesses.IsRelevantProcessChange(processChange.Name, processChange.Path, root, weGame)))
                {
                    overflowed = true;
                    if (IsExactCleanupStartEvent(processChange, root, weGame))
                    {
                        overflowed2 = true;
                    }
                    if (IsCredentialSourceStartEvent(processChange, root))
                    {
                        credentialsChanged = true;
                    }
                    if (isCredentialDiscoveryStartEvent)
                    {
                        credentialsChanged = true;
                        overflowed3 = true;
                    }
                }
            }
            if (!overflowed)
            {
                return;
            }
            if (overflowed3)
            {
                InvalidateDiscovery();
            }
            if (credentialsChanged)
            {
                Interlocked.Exchange(ref credentialGenerationChanged, 1);
            }
            bool scheduleWake = false;
            bool cancelPendingWake = false;
            int dueTime = -1;
            DateTime utcNow = DateTime.UtcNow;
            bool wakeNow;
            lock (stateLock)
            {
                if (batch.Overflowed)
                {
                    nextFullProcessScanUtc = DateTime.MinValue;
                }
                if (overflowed2)
                {
                    cleanupDirty = true;
                }
                wakeNow = credentialsChanged || utcNow >= nextProcessWakeUtc;
                if (wakeNow)
                {
                    nextProcessWakeUtc = utcNow.AddMilliseconds(5000.0);
                    cancelPendingWake = processWakePending;
                    processWakePending = false;
                }
                else if (!processWakePending)
                {
                    processWakePending = true;
                    scheduleWake = true;
                    dueTime = ProcessWakeDelayMs(utcNow, nextProcessWakeUtc);
                }
                if (cancelPendingWake)
                {
                    try
                    {
                        processWakeTimer.Change(-1, -1);
                    }
                    catch
                    {
                    }
                }
                if (scheduleWake)
                {
                    try
                    {
                        processWakeTimer.Change(dueTime, -1);
                    }
                    catch
                    {
                    }
                }
            }
            if (wakeNow)
            {
                Poke();
            }
        }

        private void ProcessWakeTimerElapsed(object unused)
        {
            bool wakeNow = false;
            bool rescheduleWake = false;
            int dueTime = -1;
            lock (stateLock)
            {
                if (disposed || !running || !processWakePending)
                {
                    return;
                }
                DateTime utcNow = DateTime.UtcNow;
                if (utcNow < nextProcessWakeUtc)
                {
                    rescheduleWake = true;
                    dueTime = ProcessWakeDelayMs(utcNow, nextProcessWakeUtc);
                }
                else
                {
                    processWakePending = false;
                    nextProcessWakeUtc = utcNow.AddMilliseconds(5000.0);
                    wakeNow = true;
                }
            }
            if (rescheduleWake)
            {
                try
                {
                    processWakeTimer.Change(dueTime, -1);
                }
                catch
                {
                }
            }
            if (wakeNow)
            {
                Poke();
            }
        }

        internal static int ProcessWakeDelayMs(DateTime nowUtc, DateTime deadlineUtc)
        {
            long remainingTicks = deadlineUtc.Ticks - nowUtc.Ticks;
            if (remainingTicks <= 0)
            {
                return 1;
            }
            long wakeDelayMilliseconds = (remainingTicks + 10000 - 1) / 10000;
            return (int)Math.Min(2147483647L, Math.Max(1L, wakeDelayMilliseconds));
        }

        internal static bool IsExactCleanupStartEvent(ProcessChange change, string root, string weGame)
        {
            if (change == null || change.Kind != 0 || string.IsNullOrEmpty(change.Path))
            {
                return false;
            }
            try
            {
                return LolRuntimeProcesses.IsCleanupTarget(change.Path, Path.GetFileName(change.Path), root, weGame);
            }
            catch
            {
                return false;
            }
        }

        internal static bool IsCredentialSourceStartEvent(ProcessChange change, string root)
        {
            if (change == null || change.Kind != 0 || !LolRuntimeProcesses.IsCredentialSourceName(change.Name))
            {
                return false;
            }
            if (string.IsNullOrEmpty(change.Path))
            {
                return true;
            }
            if (!LolRuntimeProcesses.IsUnder(change.Path, root))
            {
                return false;
            }
            string fileName;
            try
            {
                fileName = Path.GetFileName(change.Path);
            }
            catch
            {
                return false;
            }
            if (!string.Equals(fileName, "LeagueClient.exe", StringComparison.OrdinalIgnoreCase))
            {
                return string.Equals(fileName, "LeagueClientUx.exe", StringComparison.OrdinalIgnoreCase);
            }
            return true;
        }

        internal static bool IsCredentialDiscoveryStartEvent(ProcessChange change)
        {
            if (change == null || change.Kind != 0 || !LolRuntimeProcesses.IsCredentialSourceName(change.Name))
            {
                return false;
            }
            if (string.IsNullOrEmpty(change.Path))
            {
                return true;
            }
            string fileName;
            try
            {
                fileName = Path.GetFileName(change.Path);
            }
            catch
            {
                return false;
            }
            if (!string.Equals(fileName, "LeagueClient.exe", StringComparison.OrdinalIgnoreCase))
            {
                return string.Equals(fileName, "LeagueClientUx.exe", StringComparison.OrdinalIgnoreCase);
            }
            return true;
        }

    }
}
