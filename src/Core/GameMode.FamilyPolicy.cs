// File purpose Per-library-entry family policy, the gate chains publishing with the last background write check
// A queued scan must not reapply a policy that's already been turned off
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
                    if (!SaveProfileSnapshotLocked(next))
                    {
                        return false;
                    }
                    profiles[index] = replacement;
                    // Refresh the copy used for display and policy, renderer identity unchanged
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
                // The worker thread only restores the newly exempted Background reason, the UI thread
                // does no native process writes and clears no unrelated reason
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
            // Called by the worker thread, lifecycle and UI alike, no process writes
            Interlocked.Increment(ref familyPolicyEpoch);
        }

    }
}
