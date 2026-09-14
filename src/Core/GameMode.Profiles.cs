// @author bdth 2074055628@qq.com
// File purpose Game profile access, save-failure signal and boost status text
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace PaviseApp
{
    internal partial class GameMode
    {
        public List<GameProfile> GetProfiles()
        {
            lock (sync)
            {
                var copy = new List<GameProfile>();
                foreach (GameProfile p in profiles) copy.Add(p.Clone());
                return copy;
            }
        }

        public bool ProfileStoreSaveFailed
        {
            get
            {
                return profileStore.SaveFailed
                    || Interlocked.CompareExchange(ref profileSaveFailureSignaled, 0, 0) != 0;
            }
        }

        public event Action ProfileStoreSaveFailure;

        private bool SaveProfilesLocked()
        {
            return SaveProfileSnapshotLocked(profiles, null);
        }

        private bool SaveProfileSnapshotLocked(IList<GameProfile> next, Func<bool> canCommit = null)
        {
            // Only a fatal disk write failure trips the breaker, the forced-clear UI callback is async
            // no further game library writes may be attempted before that callback runs
            if (stopping || ProfileStoreSaveFailed || !EnsureLibraryReadyLocked()) return false;
            if (profileStore.Save(next, delegate
                { return !stopping && (canCommit == null || canCommit()); })) return true;
            SignalProfileStoreSaveFailure(profileStore.RetryableSaveFailure);
            return false;
        }

        private void SignalProfileStoreSaveFailure(bool retryableProfileSave = false)
        {
            if ((retryableProfileSave || profileStore.SaveCanceled) && !profileStore.SaveFailed) return;
            if (Interlocked.Exchange(ref profileSaveFailureSignaled, 1) != 0) return;
            InvalidateStandbyCleanerWork();
            InvalidateEnglishInputWork();
            InvalidateIntelGraphicsWork();
            InvalidateRendererHandoff();
            Action handler = ProfileStoreSaveFailure;
            if (handler != null) { try { handler(); } catch { } }
        }

        private const string CoreMaskKey = "GmCoreMask";

        public string StatusText
        {
            get
            {
                if (!enabled) return Lang.T("st.off");
                lock (sync)
                {
                    if (!active)
                    {
                        string armedName = armedGameName;
                        return armedName != null
                            ? Lang.F("st.armed", armedName) : Lang.T("st.mon");
                    }
                    long gone = gameGoneSinceTicks;
                    if (gone != 0)
                    {
                        int remain = ExitGraceSeconds
                            - (int)((DateTime.UtcNow.Ticks - gone) / TimeSpan.TicksPerSecond);
                        if (remain < 0) remain = 0;
                        return Lang.F("st.grace", activeGame, remain);
                    }
                    int n = core.ThrottledCountCached();
                    int b = boostStateVerified.Count;
                    string s = Lang.F("st.active", activeGame, n);
                    // The two parts used to be concatenated directly, producing 'Suppressed 28 processes Boosted 1' with nothing separating them
                    s += Lang.T("st.sep") + Lang.F("st.boost", b, Lang.T(planSwitch ? "st.hp" : "st.pr"));
                    return s;
                }
            }
        }

        public bool BoostStateVerified
        {
            get
            {
                lock (sync) return active && activeDetection != null
                    && boostStateVerified.Contains(activeDetection.RendererPid);
            }
        }

        public bool BoostHandleProtected
        {
            get
            {
                lock (sync) return active && activeDetection != null
                    && (boostHandleStripped.Contains(activeDetection.RendererPid)
                        || boostStateWarned.Contains(activeDetection.RendererPid));
            }
        }

        public string BoostStatusText
        {
            get
            {
                if (!enabled || !EffBoost) return Lang.T("v14.boost.disabled");
                lock (sync)
                {
                    if (!active) return Lang.T("v14.boost.wait");
                    if (activeDetection == null || activeDetection.RendererPid <= 0)
                        return Lang.T("v14.boost.no.renderer");
                    string name = activeDetection.RendererName ?? activeGame ?? "Game";
                    if (ProtectedGameRoster.Contains(activeDetection.RendererName))
                        return Lang.F("v14.boost.protected", name);
                    if (boostStateVerified.Contains(activeDetection.RendererPid))
                        return Lang.F("v14.boost.verified", name);
                    if (boostHandleStripped.Contains(activeDetection.RendererPid)
                        || boostStateWarned.Contains(activeDetection.RendererPid))
                        return Lang.F("v14.boost.protected", name);
                    return Lang.F("v14.boost.applying", name);
                }
            }
        }
    }
}
