// 作者：bdth 2074055628@qq.com
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private void BeginSessionPolicy()
        {
            lock (sync)
                RememberModeSwitchLockLocked(activeDetection);
            BeginRetrySession();
            bool isolationClean = StopCoreIsolation();
            InvalidateCacheWarm();
            Interlocked.Increment(ref optionalServiceGeneration);
            InvalidateStandbyCleanerWork();
            GameProfile source;
            lock (sync) source = activeDetection != null && activeDetection.Profile != null
                ? activeDetection.Profile.Clone() : null;
            PolicySnapshot snap = source != null ? PolicyResolver.For(source) : PolicyResolver.Global();
            sessionPolicy = snap;
            SyncAutomationPolicy();
            BeginAutomationEvidence();
            BeginEnglishInputSession();
            if (!snap.IsGlobal)
                Logger.Log(Lang.T("log.gamemode.34") + snap.ProfileName + Lang.T("log.gamemode.35")
                    + snap.OverrideCount + Lang.T("log.gamemode.36"));
            BeginAutoCores(snap, isolationClean);
        }

#if PAVISE_SELFTEST
        internal void ProbeSessionPolicyApply(GameProfile profile)
        {
            sessionPolicy = profile != null ? PolicyResolver.For(profile) : PolicyResolver.Global();
        }

        internal void ProbeSessionPolicyClear() { sessionPolicy = null; }

        internal bool ProbeEffSuppress { get { return EffSuppress; } }
#endif
    }
}
