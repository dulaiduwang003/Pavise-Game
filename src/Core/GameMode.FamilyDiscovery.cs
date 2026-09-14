// File purpose Install scope is determined at library load, add or confirmed target replacement
// Runtime discovery only reuses captured process identities and never scans the disk
// nor adds names to the renderer rules for any specific game or launcher
using System;
using System.Collections.Generic;
using System.Threading;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private GameFamilyHistory gameFamilyHistory;
        private GameFamilyEvidence gameFamilyEvidence = GameFamilyEvidence.Empty;

        private GameFamilyHistory FamilyHistory
        {
            get
            {
                if (gameFamilyHistory == null)
                    Interlocked.CompareExchange(ref gameFamilyHistory, new GameFamilyHistory(), null);
                return gameFamilyHistory;
            }
        }

        private GameFamilyEvidence FamilyEvidence
        {
            get { return Volatile.Read(ref gameFamilyEvidence) ?? GameFamilyEvidence.Empty; }
        }

        private void ClearFamilyDiscovery()
        {
            lock (sync)
            {
                GameFamilyHistory history = gameFamilyHistory;
                if (history != null) history.Clear();
                Volatile.Write(ref gameFamilyEvidence, GameFamilyEvidence.Empty);
            }
        }

        // Verified identity is needed before the event source's 750ms batch is delivered
        // including relay processes the library doesn't know yet, this path is separate from FastTrack
        // so ordinary launches aren't forced through a full scan
        public bool NeedsGameFamilyIdentity(int session)
        {
            if (session != selfSession || session < 0 || stopping || !enabled) return false;
            lock (sync) return profiles.Count > 0;
        }

        private void ObserveGameFamilyChanges(ProcessChangeBatch batch)
        {
            if (batch == null || stopping || !enabled) return;
            lock (sync)
            {
                if (stopping || !enabled || profiles.Count == 0) return;
                FamilyHistory.ObserveEvents(batch, selfSession, RendererNowMs());
            }
        }

        // Caller holds sync and takes the matching profile snapshot under the same lock
        private void CaptureGameFamily(ProcessSnapshot snapshot, IList<GameProfile> library)
        {
            Volatile.Write(ref gameFamilyEvidence,
                FamilyHistory.Capture(snapshot, library, selfSession, RendererNowMs()));
        }

        internal static string ResolveLibraryInstallRoot(string executablePath, string fallbackRoot)
        {
            string fallback = NormalizeGameRoot(fallbackRoot);
            if (fallback == null) fallback = NormalizeGameRoot(GameScan.InferGameRoot(executablePath));
            fallback = GameInstallScope.RestrictFallback(executablePath, fallback);
            string resolved = NormalizeGameRoot(GameInstallScope.Resolve(executablePath, fallback));
            return resolved != null && FamilyBoundary.UnderRoot(executablePath, resolved) ? resolved : fallback;
        }

        // An existing entry's inferred root may be too narrow, repair that metadata on load while we're at it
        // Leave the selected EXE options and observation tags alone, no relaxation at all without an install record
        // Known platform or common container directories can't be the family's fallback root either
        internal static bool RefreshLibraryInstallRoots(IList<GameProfile> library)
        {
            bool changed = false;
            if (library == null) return false;
            foreach (GameProfile profile in library)
            {
                if (profile == null || string.IsNullOrEmpty(profile.ExecutablePath)) continue;
                string fallback = GameInstallScope.RestrictFallback(
                    profile.ExecutablePath, NormalizeGameRoot(profile.Root));
                bool rejectedOldRoot = !string.IsNullOrEmpty(profile.Root) && fallback == null;
                string resolved = NormalizeGameRoot(GameInstallScope.Resolve(
                    profile.ExecutablePath, fallback));
                if (resolved != null && !FamilyBoundary.UnderRoot(profile.ExecutablePath, resolved)) resolved = fallback;
                if (string.Equals(resolved, profile.Root, StringComparison.OrdinalIgnoreCase)) continue;
                // Never narrow an already declared install scope, and never exclude
                // a confirmed renderer process, unless the old scope itself is
                // a container directory proven unsafe, EXE-exact identities are always kept
                if (!rejectedOldRoot)
                {
                    if (resolved == null) continue;
                    if (!string.IsNullOrEmpty(profile.Root) && !FamilyBoundary.UnderRoot(profile.Root, resolved)) continue;
                    if (!string.IsNullOrEmpty(profile.LearnedExecutablePath)
                        && !FamilyBoundary.UnderRoot(profile.LearnedExecutablePath, resolved)) continue;
                }
                profile.Root = resolved;
                changed = true;
            }
            return changed;
        }
    }
}
