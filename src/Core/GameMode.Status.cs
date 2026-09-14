// @author bdth 2074055628@qq.com
// File purpose Match state and preset reads, policy accessors and power slider overlay
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace PaviseApp
{
    internal partial class GameMode
    {
        public bool IsActive { get { lock (sync) return active; } }

        public string ActiveGame { get { lock (sync) return active ? activeGame : null; } }
        internal PolicySnapshot PolicyForDiagnostics { get { return sessionPolicy; } }

        public PerformancePreset Preset
        {
            get { lock (sync) return preset; }
            set
            {
                if (!PresetValue.IsValid((int)value)) value = PerformancePreset.Standard;
                lock (sync) preset = value;
                Settings.SaveStr("PerformancePreset", ((int)value).ToString());
                RequestPolicyApply();
            }
        }

        public PerformancePreset ActivePreset
        {
            get
            {
                PolicySnapshot s = sessionPolicy;
                if (s != null) return s.Preset;
                lock (sync) return preset;
            }
        }

        public string SessionPolicySourceName
        {
            get
            {
                PolicySnapshot s = sessionPolicy;
                return s != null && !s.IsGlobal && IsActive ? s.ProfileName : null;
            }
        }

        public string SessionPolicyProfileId
        {
            get
            {
                PolicySnapshot s = sessionPolicy;
                return s != null && IsActive ? s.ProfileId : null;
            }
        }

        private bool EffSuppress
        {
            get
            {
                PolicySnapshot s = sessionPolicy;
                return s != null ? s.EffSuppress : bgSuppressOn;
            }
        }

        private bool overlayRaised;
        private int overlayAttempts;

        // This hook runs every scan round, once per 500ms during a match
        //   without a cap a failed slider push would retry every 500ms for the whole match, each a registry write plus a failure log line
        //   same idea as EnvFuse, stop after enough tries, the next match gets a fresh chance
        private const int MaxOverlayAttempts = 3;

        // Push the power slider to Best performance, only on a plugged-in laptop on the Esports tier, always restored on exit
        private void MaybeActivatePowerOverlay(bool competitive)
        {
            if (overlayRaised || overlayAttempts >= MaxOverlayAttempts) return;
            if (!PowerOverlay.ShouldActivate(Native.HasSystemBattery(),
                    Native.OnAcPower(), competitive)) return;
            if (!PowerOverlay.Supported()) { overlayAttempts = MaxOverlayAttempts; return; }
            overlayAttempts++;
            overlayRaised = RunIrqIsolatedMutation(
                delegate { return PowerOverlay.Activate(); });
        }

        internal void RestorePowerOverlay()
        {
            overlayAttempts = 0;
            if (!overlayRaised) return;
            overlayRaised = false;
            PowerOverlay.Restore();
        }

        // The tier frozen at the moment the match activated, power yield only applies on the Esports tier
        private PerformancePreset EffPreset
        {
            get
            {
                PolicySnapshot s = sessionPolicy;
                return s != null ? s.Preset : Preset;
            }
        }

        private bool EffBoost
        {
            get
            {
                PolicySnapshot s = sessionPolicy;
                return s != null ? s.EffBoost : boostOn;
            }
        }

        private bool EffLane
        {
            get
            {
                PolicySnapshot s = sessionPolicy;
                return s != null ? s.EffLane : renderLaneOn;
            }
        }

        // The switch is user intent, this is whether this machine qualifies, two separate things, keep it out of EffLane
        //   the candidate thread is raised to THREAD_PRIORITY_HIGHEST while the game process sits in the HIGH class
        //   so it runs at 15 while the game's other threads stay at 13, harmless when cores are plentiful
        //   when runnable threads exceed available cores, 15 keeps preempting 13 while it is itself waiting on those threads for data
        //   the render thread spins, worker threads get deferred, frame time gets longer instead
        //   handhelds with a 15 to 30W power wall and all cores at low clocks hit this most easily, a user measured 60 dropping to 45
        //   the CPU-saturation rollback protection cannot be relied on here, handhelds are usually GPU- or power-wall-bound
        //   overall utilization never reaches the 90 gate, so the rollback never fires
        private bool LaneEligible { get { return LaneSupported(EffPreset); } }

        // Not offered below six cores, on a 4-core/8-thread laptop game thread count already exceeds core count
        internal const int LaneMinPhysicalCores = 6;

        // The Policy page must ask the same criterion, so the UI does not say it can be enabled when it actually will not
        internal static bool LaneSupported(PerformancePreset mode)
        {
            if (IsHandheld(mode)) return false;
            try { return CpuTopology.PhysicalCoreCount >= LaneMinPhysicalCores; }
            catch { return false; }
        }

        // The session snapshot freezes only keys with a per-game override, keys without one re-read the global setting live every time
        //   so turning off the global switch mid-match takes effect immediately, not re-asserted by a frozen value
        //   only games with an override get freeze semantics, the override value is fixed when the snapshot is built, changing it mid-match goes through the clear flow
        private bool EffVramShield
        {
            get
            {
                PolicySnapshot s = sessionPolicy;
                return s != null ? s.VramShield : vramShieldOn;
            }
        }

        private bool EffPowerYield
        {
            get
            {
                PolicySnapshot s = sessionPolicy;
                return s != null ? s.PowerYield : PowerBudgetYieldRunner.EnabledSetting;
            }
        }

        private bool EffIntelEnduranceOff
        {
            get
            {
                PolicySnapshot s = sessionPolicy;
                return s != null ? s.IntelEnduranceOff : intelEnduranceOn;
            }
        }

    }
}
