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
        public bool IsActive { get { lock (sync) return active; } }

        public string ActiveGame { get { lock (sync) return active ? activeGame : null; } }
        private int modeLockGamePid;
        private long modeLockGameCreation;
        private string modeLockGameProfileId;
        private void RememberModeSwitchLockLocked(GameDetection detection)
        {
            if (detection == null || detection.RendererCreation <= 0) return;
            string profileId = detection.Profile != null ? detection.Profile.Id : null;
            if (!string.IsNullOrEmpty(profileId) && FindProfileLocked(profileId) == null) return;
            modeLockGamePid = detection.RendererPid;
            modeLockGameCreation = detection.RendererCreation;
            modeLockGameProfileId = profileId;
        }
        private void ClearModeSwitchLockForProfileLocked(string profileId)
        {
            if (string.IsNullOrEmpty(profileId)
                || !string.Equals(modeLockGameProfileId, profileId, StringComparison.OrdinalIgnoreCase)) return;
            modeLockGamePid = 0;
            modeLockGameCreation = 0;
            modeLockGameProfileId = null;
        }
        public bool ModeSwitchLocked
        {
            get { lock (sync) return ModeSwitchLockedLocked(); }
        }
        private bool ModeSwitchLockedLocked()
        {
            if (active || sessionPolicy != null
                || (activeDetection != null && activeDetection.RendererPid > 4)) return true;
            if (modeLockGamePid <= 4 || modeLockGameCreation <= 0) return false;
            IntPtr process = Native.OpenProcess(0x100000 | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, modeLockGamePid);
            if (process == IntPtr.Zero) return !Native.LastOpenProcessFailureWasNoSuchProcess();
            try
            {
                long born, cpu; ulong io;
                if (!CpuCapNative.IsAlive(process)) return false;
                return !Native.QueryProcessSample(process, out born, out cpu, out io) || born == modeLockGameCreation;
            }
            finally { Native.CloseHandle(process); }
        }
        public bool TrySetPreset(PerformancePreset value)
        {
            if (!PresetValue.IsValid((int)value)) return false;
            lock (sync)
            {
                if (stopping || ModeSwitchLockedLocked()) return false;
                if (!Settings.SaveStr("PerformancePreset", ((int)value).ToString())) return false;
                preset = value;
            }
            ReleaseUnsupportedCpuCaps(value);
            RequestPolicyApply();
            return true;
        }

        public PerformancePreset Preset
        {
            get { lock (sync) return preset; }
            set { TrySetPreset(value); }
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
                if (AutomaticPolicy.Enabled) return null;
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

        private const int MaxOverlayAttempts = 2;
        private long nextOverlayAuditTicks;

        private void MaybeActivatePowerOverlay(bool competitive)
        {
            if (PowerPlan.NoSwitchSelected) return;
            if (AutomaticPolicy.Enabled && (!AutomationPolicySnapshot.PowerPlanOn || !PowerPlan.AutomaticSelection)) return;
            long now = DateTime.UtcNow.Ticks;
            if (now < nextOverlayAuditTicks) return;
            if (!PowerOverlay.ShouldActivate(Native.HasSystemBattery(),
                    Native.OnAcPower(), competitive)) return;
            if (Volatile.Read(ref powerApplyInFlight) != 0) return;
            if (AutomaticPolicy.Enabled && AutomationPolicySnapshot.PowerPlanOn && !planActive) return;
            nextOverlayAuditTicks = now + TimeSpan.TicksPerSecond;
            NotifyPowerSchemeChanged();
            if (overlayAttempts >= MaxOverlayAttempts) return;
            if (!PowerOverlay.Supported()) return;
            overlayRaised = RunGuarded(
                delegate { return PowerOverlay.Activate(); });
            if (!overlayRaised) overlayAttempts++;
        }

        internal void RestorePowerOverlay()
        {
            overlayAttempts = 0;
            nextOverlayAuditTicks = 0;
            if (!overlayRaised && !PowerOverlay.HasResidue) return;
            overlayRaised = false;
            PowerOverlay.Restore();
        }

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

        private bool LaneEligible { get { return LaneSupported(EffPreset); } }

        internal const int LaneMinPhysicalCores = 6;

        internal static bool LaneSupported(PerformancePreset mode)
        {
            if (IsHandheld(mode)) return false;
            try { return CpuTopology.PhysicalCoreCount >= LaneMinPhysicalCores; }
            catch { return false; }
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
