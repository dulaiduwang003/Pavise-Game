// @author bdth 2074055628@qq.com
// File purpose Match policy activation, session core mask and core domain switching
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
            isolationBlocked = !StopCoreIsolation();
            InvalidateCacheWarm();
            Interlocked.Increment(ref optionalServiceGeneration);
            Interlocked.Increment(ref cpuIdleGeneration);
            InvalidateStandbyCleanerWork();
            InvalidateIntelGraphicsWork();
            GameProfile source;
            lock (sync) source = activeDetection != null ? activeDetection.Profile : null;
            PolicySnapshot snap = source != null ? PolicyResolver.For(source) : PolicyResolver.Global();
            sessionPolicy = snap;
            BeginEnglishInputSession();
            if (!snap.IsGlobal)
                Logger.Log(Lang.T("log.gamemode.34") + snap.ProfileName + Lang.T("log.gamemode.35")
                    + snap.OverrideCount + Lang.T("log.gamemode.36"));
            ApplySessionCoreMask(snap);
            ApplySessionCoreDomain(snap);
        }

        private void ApplySessionCoreMask(PolicySnapshot snap)
        {
            ulong wanted = snap.CoreMask;
            if (wanted == CpuTopology.CustomMask) return;
            if (wanted == 0)
            {
                CpuTopology.SetCustomMask(0);
                Logger.Log(Lang.T("log.gamemodeenv.12") + snap.ProfileName + Lang.T("log.gamemode.37"));
                return;
            }
            if (CpuTopology.SetCustomMask(wanted))
                Logger.Log(Lang.T("log.gamemodeenv.12") + snap.ProfileName + Lang.T("log.gamemode.38")
                    + CpuTopology.DescribeMask(CpuTopology.CustomMask));
            else Logger.Log(Lang.T("log.gamemodeenv.12") + snap.ProfileName + Lang.T("log.gamemode.39"));
        }

        private void RestoreGlobalCoreMask()
        {
            string raw = PolicyResolver.GlobalValue(CoreMaskKey) ?? "";
            ulong parsed;
            if (raw.Length > 0 && ulong.TryParse(raw, System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out parsed)
                && CpuTopology.SetCustomMask(parsed))
                return;
            CpuTopology.SetCustomMask(0);
        }

        private void ApplySessionCoreDomain(PolicySnapshot snap)
        {
            if (!CpuTopology.DomainPreferenceApplied || !CpuTopology.HasAltPartition()) return;
            if (snap.CoreDomainAlt == CpuTopology.AltDomainActive) return;
            if (!CpuTopology.SwapDomains()) return;
            throttleMask = CpuTopology.ThrottleMask;
            strictMask = CpuTopology.StrictBoostMask;
            core.RefreshTopologyMasks();
            if (snap.HasOverride(PolicyCatalog.KeyCoreDomainAlt))
                Logger.Log(Lang.T("log.gamemodeenv.12") + snap.ProfileName + Lang.T("log.gamemode.40")
                    + CpuTopology.DescribeMask(strictMask) + Lang.T("log.gamemodesettings.3") + CpuTopology.DescribeMask(throttleMask));
            else
                Logger.Log(Lang.T("log.gamemode.41")
                    + CpuTopology.DescribeMask(strictMask) + Lang.T("log.gamemodesettings.3") + CpuTopology.DescribeMask(throttleMask));
        }

#if PAVISE_SELFTEST
        internal void ProbeSessionPolicyApply(GameProfile profile)
        {
            sessionPolicy = profile != null ? PolicyResolver.For(profile) : PolicyResolver.Global();
        }

        internal void ProbeSessionPolicyClear() { sessionPolicy = null; }

        internal bool ProbeEffSuppress { get { return EffSuppress; } }

#endif

        internal static bool ShouldUseCorePartition(bool manuallySelected, bool partitionAvailable)
        {
            return manuallySelected && partitionAvailable;
        }

        internal const int WideGameThreads = 64;
        internal const int PartitionKeepPercent = 70;

        internal static bool PartitionLikelyHurts(int threads, int givenCores, int totalCores)
        {
            if (threads < WideGameThreads) return false;
            if (givenCores <= 0 || totalCores <= 0 || givenCores >= totalCores) return false;
            return givenCores * 100 / totalCores <= PartitionKeepPercent;
        }
    }
}
