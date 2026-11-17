using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal sealed partial class SuppressionCore
    {

        private bool ThrottleMatches(IntPtr h, SuppressionLevel level, uint originalPriority, ulong originalAffinity, uint[] originalCpuSets, int desiredGpu, ulong desiredAffinity, bool affinityOwned, int originalBoost)
        {
            uint priority = DesiredPriority(level, originalPriority);
            if (Native.GetPriorityClass(h) != priority)
            {
                return false;
            }
            int cls;
            if (desiredGpu >= 0 && (Native.D3DKMTGetProcessSchedulingPriorityClass(h, out cls) != 0 || cls != desiredGpu))
            {
                return false;
            }
            int ioPriority = DesiredIoPriority(level);
            int pagePriority = DesiredPagePriority(level);
            if (Native.QueryIoPriority(h) != ioPriority || Native.QueryPagePriority(h) != pagePriority)
            {
                return false;
            }
            int controlMask;
            int stateMask;
            if (Native.PowerThrottlingSupported && (!Native.TryQueryPowerThrottling(h, out controlMask, out stateMask) || !PowerStateMatches(controlMask, stateMask, Native.EcoQoSWantMask(DesiredTimerSeal(level)))))
            {
                return false;
            }
            bool disableDynamicBoostSucceeded = DisableDynamicBoost(level);
            uint[] expected = originalCpuSets ?? new uint[0];
            if (!affinityOwned && !Native.CpuSetsMatch(h, expected))
            {
                return false;
            }
            if (!CpuTopology.MultiGroup && !affinityOwned && Native.QueryAffinity(h) != desiredAffinity)
            {
                return false;
            }
            int boostState = DesiredBoostDisabled(level, originalBoost);
            if ((disableDynamicBoostSucceeded && boostState < 0) || (boostState >= 0 && Native.QueryBoostDisabled(h) != boostState))
            {
                return false;
            }
            return true;
        }

        internal static bool DisableDynamicBoost(SuppressionLevel level)
        {
            return BackgroundBoostLimit && level >= SuppressionLevel.Isolated;
        }

        internal static uint DesiredPriority(SuppressionLevel level, uint originalPriority)
        {
            uint result = ((originalPriority == 0 || originalPriority == uint.MaxValue) ? 32u : originalPriority);
            if (level >= SuppressionLevel.Restrained)
            {
                result = level >= SuppressionLevel.Isolated ? 64u : 16384u;
            }
            return result;
        }

        internal static int DesiredIoPriority(SuppressionLevel level)
        {
            if (level < SuppressionLevel.Isolated)
            {
                return 1;
            }
            return 0;
        }

        internal static int DesiredPagePriority(SuppressionLevel level)
        {
            if (level < SuppressionLevel.Isolated)
            {
                return 3;
            }
            return 1;
        }

        internal static bool DesiredTimerSeal(SuppressionLevel level)
        {
            return level >= SuppressionLevel.Isolated;
        }

        internal static int DesiredBoostDisabled(SuppressionLevel level, int originalBoost)
        {
            if (originalBoost != 0 && originalBoost != 1)
            {
                return -1;
            }
            if (!DisableDynamicBoost(level))
            {
                return originalBoost;
            }
            return 1;
        }

        internal static string ApplyBoostPolicy(SuppressionLevel level, int originalBoost, Func<int> read, Func<bool, bool> write)
        {
            int desiredState = DesiredBoostDisabled(level, originalBoost);
            if (desiredState < 0)
            {
                if (!DisableDynamicBoost(level))
                {
                    return "";
                }
                return "boost-snapshot";
            }
            if (read() != desiredState && !write(desiredState == 1))
            {
                return "boost-write";
            }
            if (read() != desiredState)
            {
                return "boost-readback";
            }
            return "";
        }

        internal static bool PowerStateMatches(int control, int state, uint required)
        {
            if (control >= 0 && state >= 0 && ((uint)control & required) == required && ((uint)state & required) == required)
            {
                if (required == 1)
                {
                    return (control & 4) == 0;
                }
                return true;
            }
            return false;
        }

        internal static int DesiredGpuClass(bool demoteEnabled, SuppressReason reasons, SuppressionLevel backgroundLevel, int origGpu)
        {
            if (origGpu < 0)
            {
                return -1;
            }
            if (!demoteEnabled || (reasons & SuppressReason.Background) == 0 || backgroundLevel < SuppressionLevel.Restrained)
            {
                return origGpu;
            }
            if (backgroundLevel < SuppressionLevel.Isolated)
            {
                return 1;
            }
            return 0;
        }

        private ulong DesiredAffinityOf(Entry e)
        {
            return SuppressionAffinityPolicy.DesiredAffinity(e.Reasons, e.BackgroundRestorePending ? 0 : e.SqueezeAff, e.OrigAff, allMask);
        }

        private static int DesiredGpu(Entry e)
        {
            bool managed = GpuDemoteEnabled && HagsTweak.SchedulingAllowsGpuDemotionCached()
                && (e.Reasons & SuppressReason.Background) != 0 && e.BackgroundLevel >= SuppressionLevel.Restrained;
            return managed ? DesiredGpuClass(true, e.Reasons, e.BackgroundLevel, e.OrigGpu) : -1;
        }

        private static bool GpuPolicyNeedsRelease(Entry entry)
        { return entry.GpuLease.PendingCheckpoint || DesiredGpu(entry) < 0 && entry.GpuLease.Phase != 'N' && entry.GpuLease.Phase != 'S'; }

        private static int PositiveMod(int value, int modulo)
        {
            int valueOffset = value % modulo;
            if (valueOffset >= 0)
            {
                return valueOffset;
            }
            return valueOffset + modulo;
        }

        private static bool SameCpuSets(uint[] a, uint[] b)
        {
            if (a == null || b == null || a.Length == 0 || a.Length != b.Length)
            {
                return false;
            }
            HashSet<uint> expectedSets = new HashSet<uint>(b);
            foreach (uint item in a)
            {
                if (!expectedSets.Contains(item))
                {
                    return false;
                }
            }
            return true;
        }

        private static void SetReasonLevel(Entry e, SuppressReason reason, SuppressionLevel level)
        {
            if ((reason & SuppressReason.Background) != 0)
            {
                e.BackgroundLevel = level;
            }
            e.Level = e.BackgroundLevel;
        }

    }
}
