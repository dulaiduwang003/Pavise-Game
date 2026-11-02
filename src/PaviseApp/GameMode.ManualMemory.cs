using System;
using System.Collections.Generic;
using System.IO;

namespace PaviseApp
{
    internal partial class GameMode
    {
        // Read-only and independent of foreground focus or the guard enable switch.
        internal bool MemoryCleanupGamePresent()
        {
            try
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                lock (sync)
                {
                    if (active || activeDetection != null || stickyDetection != null || armedAwaitingElection
                        || !string.IsNullOrEmpty(armedGameName) || ProfileStoreSaveFailed) return true;
                    foreach (GameProfile profile in profiles)
                    {
                        AddMemoryGameName(names, profile.ExecutablePath);
                        AddMemoryGameName(names, profile.LearnedExecutablePath);
                        foreach (string entry in profile.Entries) AddMemoryGameName(names, entry);
                    }
                }
                if (names.Count == 0) return false;
                ProcessSnapshot snapshot = ProcessSnapshotSource.Capture(selfSession, 0);
                if (snapshot == null) return true;
                foreach (ProcEntry process in snapshot.Entries)
                    if (process != null && names.Contains(Path.GetFileNameWithoutExtension(process.Name ?? ""))) return true;
                return false;
            }
            catch { return true; }
        }
        private static void AddMemoryGameName(HashSet<string> names, string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            string name = Path.GetFileNameWithoutExtension(path);
            if (!string.IsNullOrEmpty(name)) names.Add(name);
        }
    }
}
