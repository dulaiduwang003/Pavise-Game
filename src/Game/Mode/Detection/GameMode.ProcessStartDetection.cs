using System;
using System.Collections.Generic;
using System.IO;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private sealed class GameStartNames
        {
            private readonly HashSet<string> entries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            private readonly HashSet<string> fallbackBases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            internal GameStartNames(IEnumerable<GameProfile> profiles)
            {
                foreach (GameProfile profile in profiles)
                {
                    if (profile == null) continue;
                    foreach (string entry in profile.Entries)
                    {
                        if (!string.IsNullOrEmpty(entry)) entries.Add(entry);
                    }
                    string baseName;
                    try { baseName = Path.GetFileNameWithoutExtension(profile.ExecutablePath); }
                    catch (ArgumentException) { continue; }
                    if (baseName != null && baseName.Length >= 3) fallbackBases.Add(baseName);
                }
            }

            internal bool IsEmpty { get { return entries.Count == 0 && fallbackBases.Count == 0; } }

            internal bool Contains(string name)
            {
                if (entries.Contains(name)) return true;
                foreach (string baseName in fallbackBases)
                {
                    if (GameSessionDetector.IsFallbackEntryName(baseName, name)) return true;
                }
                return false;
            }
        }

        private volatile GameStartNames gameStartNames;

        private void PublishGameProcessNames()
        {
            gameStartNames = new GameStartNames(profiles);
        }

        internal bool AllowsEtwProcessStart()
        {
            GameStartNames names = gameStartNames;
            return enabled && !stopping && !panicReq && selfSession >= 0 && names != null && !names.IsEmpty
                && !ProfileStoreSaveFailed && (libraryIgnoreTransaction == null || !libraryIgnoreTransaction.RecoveryPending);
        }

        internal bool MatchesFastGameStart(string name, int session)
        {
            GameStartNames names = gameStartNames;
            return session == selfSession && !string.IsNullOrEmpty(name) && names != null && names.Contains(name);
        }
    }
}
