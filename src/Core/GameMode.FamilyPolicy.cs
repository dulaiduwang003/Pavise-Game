// Per-library-entry family policy. The gate serializes publication with the last
// background write check; a queued scan cannot reapply the policy that was closed.
using System;
using System.Collections.Generic;
using System.Threading;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private readonly object familyPolicyGate = new object();
        private int familyPolicyEpoch;

        public bool SetProfileFamilySuppression(string profileId, bool on)
        {
            return SetProfileFamilySuppression(profileId, on, null);
        }

        public bool SetProfileFamilySuppression(string profileId, bool on, string expectedExecutablePath)
        {
            bool changed = false;
            lock (familyPolicyGate)
            {
                lock (sync)
                {
                    GameProfile current = FindProfileLocked(profileId);
                    if (stopping || current == null || ProfileStoreSaveFailed) return false;
                    if (expectedExecutablePath != null && !string.Equals(current.ExecutablePath,
                        expectedExecutablePath, StringComparison.OrdinalIgnoreCase)) return false;
                    if (current.SuppressFamilyBackground == on) return true;
                    GameProfile replacement = current.Clone();
                    if (on) replacement.Overrides[PolicyCatalog.KeySuppressFamily] = "1";
                    else replacement.Overrides.Remove(PolicyCatalog.KeySuppressFamily);
                    var next = new List<GameProfile>(profiles);
                    int index = profiles.IndexOf(current);
                    next[index] = replacement;
                    if (!profileStore.Save(next))
                    {
                        SignalProfileStoreSaveFailure();
                        return false;
                    }
                    profiles[index] = replacement;
                    // Refresh copies used for display/policy without changing renderer identity.
                    if (activeDetection != null && activeDetection.Profile != null
                        && activeDetection.Profile.Id == profileId)
                        activeDetection.Profile = replacement.Clone();
                    if (stickyDetection != null && stickyDetection.Profile != null
                        && stickyDetection.Profile.Id == profileId)
                        stickyDetection.Profile = replacement.Clone();
                    Interlocked.Increment(ref familyPolicyEpoch);
                    firstSweep = true;
                    changed = true;
                }
            }
            if (changed)
            {
                // The worker restores only newly exempt Background reasons; no UI
                // thread native process writes and no unrelated reason is cleared.
                RequestPolicyApply();
                RaiseLibraryChanged();
            }
            return true;
        }

        internal int FamilyPolicyEpoch { get { return Volatile.Read(ref familyPolicyEpoch); } }

        internal bool FamilyPolicyEpochCurrent(int epoch)
        {
            return epoch == Volatile.Read(ref familyPolicyEpoch)
                && enabled && !stopping && !panicReq && !ProfileStoreSaveFailed && EffSuppress;
        }

        private bool RunBackgroundPolicy(int epoch, Action action)
        {
            lock (familyPolicyGate)
            {
                if (!FamilyPolicyEpochCurrent(epoch)) return false;
                action();
                return true;
            }
        }

        private void InvalidateFamilyPolicy()
        {
            // Called by the worker/lifecycle as well as the UI. No process writes.
            Interlocked.Increment(ref familyPolicyEpoch);
        }

        internal static bool FamilyExemptFor(GameProfile profile)
        {
            return profile == null || !profile.SuppressFamilyBackground;
        }

        // Protection belongs to every opted-out profile, not only the currently
        // foreground game. Reuse this sweep's immutable process snapshot; never
        // infer family ownership from a game/client executable name.
        internal static HashSet<int> CollectProtectedLibraryFamily(IList<GameProfile> configured,
            ProcessSnapshot snapshot, int selfPid, int ownerSession, GameFamilyEvidence familyEvidence = null)
        {
            var result = new HashSet<int>();
            if (configured == null || snapshot == null || ownerSession < 0) return result;
            var roots = new List<string>();
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (GameProfile profile in configured)
            {
                if (profile == null || !FamilyExemptFor(profile)) continue;
                if (!string.IsNullOrEmpty(profile.ExecutablePath)) paths.Add(profile.ExecutablePath);
                if (!string.IsNullOrEmpty(profile.LearnedExecutablePath)) paths.Add(profile.LearnedExecutablePath);
                if (SafeFamilyDir(profile.Root)) roots.Add(profile.Root);
            }
            if (paths.Count == 0 && roots.Count == 0) return result;
            var parents = new Dictionary<int, int>();
            var seeds = new HashSet<int>();
            foreach (ProcEntry child in snapshot.Entries)
            {
                if (child.Pid <= 4 || child.Pid == selfPid || child.Session != ownerSession
                    || child.Creation <= 0) continue;
                if (!string.IsNullOrEmpty(child.Path)
                    && (paths.Contains(child.Path) || LibraryRootOf(child.Path, roots) != null))
                    seeds.Add(child.Pid);
                if (familyEvidence != null)
                    foreach (GameProfile profile in configured)
                        if (profile != null && FamilyExemptFor(profile)
                            && familyEvidence.Contains(profile, child.Pid, child.Creation, child.Path))
                        { seeds.Add(child.Pid); break; }
                ProcEntry parent = snapshot.Find(child.ParentPid);
                // Do not carry the legacy PID-only fallback into these new
                // cross-root links: missing identity or a reused PID stops here.
                if (parent == null || parent.Pid <= 4 || parent.Pid == selfPid
                    || parent.Pid == child.Pid || parent.Session != ownerSession
                    || parent.Creation <= 0 || parent.Creation > child.Creation) continue;
                parents[child.Pid] = parent.Pid;
            }
            result.UnionWith(seeds);
            result.UnionWith(WalkDescendants(parents, seeds, selfPid, 24));
            foreach (int seed in seeds)
                result.UnionWith(WalkAncestorChain(parents, seed, selfPid, 24));
            // Ancestors are protected themselves, never used as new seeds. A
            // shared host must not exempt its unrelated siblings/other games.
            return result;
        }
    }
}
