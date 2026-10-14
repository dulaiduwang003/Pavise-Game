using System;
using System.Collections.Generic;
using System.Threading;

namespace PaviseApp
{
    internal partial class GameMode
    {
        public List<string> GetAutoAddIgnoredPaths()
        {
            List<string> result;
            lock (sync) result = new List<string>(autoAddIgnore);
            result.Sort(StringComparer.OrdinalIgnoreCase);
            return result;
        }

        public bool RemoveAutoAddIgnoredPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            string selected = path.Trim();
            lock (sync)
            {
                if (stopping || ProfileStoreSaveFailed || !EnsureLibraryReadyLocked()
                    || !autoAddIgnore.Contains(selected)) return false;
                var nextIgnore = new HashSet<string>(autoAddIgnore, StringComparer.OrdinalIgnoreCase);
                nextIgnore.Remove(selected);
                if (!CommitLibraryLocked(GetProfiles(), nextIgnore)) return false;
                autoAddRejectUntil.Remove(selected);
                autoAddAttempts.Remove(selected);
                Interlocked.Increment(ref autoAddEpoch);
                Interlocked.Exchange(ref autoAddGateTicks, 0);
            }
            KickGameDetectionNow();
            RaiseLibraryChanged();
            return true;
        }

        private bool AddAutoDetectedExecutableCore(string executablePath, out string error, Func<bool> canCommit = null)
        {
            error = null;
            lock (sync)
            {
                if (!AutoDetectedCommitAllowed(executablePath) || (canCommit != null && !canCommit())
                    || !EnsureLibraryReadyLocked()) return false;
                List<GameProfile> next = GetProfiles();
                var nextIgnore = new HashSet<string>(autoAddIgnore, StringComparer.OrdinalIgnoreCase);
                if (!StageGameExecutable(null, executablePath, null, next, nextIgnore, out error)
                    || !CommitLibraryLocked(next, nextIgnore, delegate { return AutoDetectedCommitAllowed(executablePath)
                        && (canCommit == null || canCommit()); }))
                    return false;
            }
            KickLibraryChanged();
            return true;
        }

        private bool AutoDetectedCommitAllowed(string path)
        {
            return !stopping && !panicReq && enabled && autoAddOn && !active
                && !ProfileStoreSaveFailed && !autoIgnoreLoadFailed && !autoAddIgnore.Contains(path);
        }
    }
}
