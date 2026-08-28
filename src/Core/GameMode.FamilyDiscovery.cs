// Install scope is resolved at library load/add or confirmed target replacement.
// Runtime discovery reuses captured process identities; it never scans the disk
// or adds names for a particular game or launcher to the renderer rules.
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

        // The event source needs verified identities before its 750ms batch is
        // delivered, including a broker not yet known to the library. Keep this
        // separate from FastTrack so ordinary starts do not force full scans.
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

        // Caller holds sync together with taking the matching profile snapshot.
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
            return resolved != null && UnderRoot(executablePath, resolved) ? resolved : fallback;
        }

        // Existing entries can have a too-narrow inferred root. Repair that
        // metadata at load as well; leave the chosen EXE, options and observation
        // label untouched. No install record means no widening at all. A known
        // platform/common container is not a valid family fallback either.
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
                if (resolved != null && !UnderRoot(profile.ExecutablePath, resolved)) resolved = fallback;
                if (string.Equals(resolved, profile.Root, StringComparison.OrdinalIgnoreCase)) continue;
                // Do not narrow a previously declared install scope or exclude
                // an already confirmed renderer, unless the old scope itself
                // is a proven unsafe container. Exact EXE identities survive.
                if (!rejectedOldRoot)
                {
                    if (resolved == null) continue;
                    if (!string.IsNullOrEmpty(profile.Root) && !UnderRoot(profile.Root, resolved)) continue;
                    if (!string.IsNullOrEmpty(profile.LearnedExecutablePath)
                        && !UnderRoot(profile.LearnedExecutablePath, resolved)) continue;
                }
                profile.Root = resolved;
                changed = true;
            }
            return changed;
        }
    }
}
