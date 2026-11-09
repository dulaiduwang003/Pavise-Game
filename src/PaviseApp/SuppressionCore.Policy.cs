using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal sealed partial class SuppressionCore
    {

        private bool ThrottleMatches(IntPtr h, SuppressionLevel level, uint originalPriority, ulong originalAffinity, uint[] originalCpuSets, int desiredGpu, bool antiCheat, ulong desiredAffinity, bool affinityOwned, int originalBoost)
        {
            affinityOwned |= antiCheat;
            uint priority = DesiredPriority(level, originalPriority, antiCheat);
            if (Native.GetPriorityClass(h) != priority)
            {
                return false;
            }
            int cls;
            if (desiredGpu >= 0 && Native.D3DKMTGetProcessSchedulingPriorityClass(h, out cls) == 0 && cls != desiredGpu)
            {
                return false;
            }
            int ioPriority = DesiredIoPriority(level, antiCheat);
            int pagePriority = DesiredPagePriority(level, antiCheat);
            if (Native.QueryIoPriority(h) != ioPriority || Native.QueryPagePriority(h) != pagePriority)
            {
                return false;
            }
            int controlMask;
            int stateMask;
            if (Native.PowerThrottlingSupported && (!Native.TryQueryPowerThrottling(h, out controlMask, out stateMask) || !PowerStateMatches(controlMask, stateMask, Native.EcoQoSWantMask(DesiredTimerSeal(level, antiCheat)))))
            {
                return false;
            }
            bool disableDynamicBoostSucceeded = DisableDynamicBoost(level, antiCheat);
            uint[] expected = originalCpuSets ?? new uint[0];
            if (!affinityOwned && !Native.CpuSetsMatch(h, expected))
            {
                return false;
            }
            if (!CpuTopology.MultiGroup && !affinityOwned && Native.QueryAffinity(h) != desiredAffinity)
            {
                return false;
            }
            int boostState = DesiredBoostDisabled(level, antiCheat, originalBoost);
            if ((disableDynamicBoostSucceeded && boostState < 0) || (boostState >= 0 && Native.QueryBoostDisabled(h) != boostState))
            {
                return false;
            }
            return true;
        }

        internal static bool SevereAntiCheat(SuppressionLevel level, bool antiCheat)
        {
            return antiCheat && level >= SuppressionLevel.Isolated;
        }

        internal static bool DisableDynamicBoost(SuppressionLevel level, bool antiCheat)
        {
            if (antiCheat) return level >= SuppressionLevel.Restrained;
            return BackgroundBoostLimit && level >= SuppressionLevel.Isolated;
        }

        internal static uint DesiredPriority(SuppressionLevel level, uint originalPriority, bool antiCheat)
        {
            if (antiCheat && level >= SuppressionLevel.Eco)
                return level >= SuppressionLevel.Isolated ? 64u : 16384u;
            uint result = ((originalPriority == 0 || originalPriority == uint.MaxValue) ? 32u : originalPriority);
            if (level >= SuppressionLevel.Restrained)
            {
                result = ((level >= SuppressionLevel.Isolated && (!antiCheat || SevereAntiCheat(level, antiCheat))) ? 64u : 16384u);
            }
            return result;
        }

        internal static int DesiredIoPriority(SuppressionLevel level, bool antiCheat = false)
        {
            if (antiCheat && level >= SuppressionLevel.Restrained) return 0;
            if (level < SuppressionLevel.Isolated)
            {
                return 1;
            }
            return 0;
        }

        internal static int DesiredPagePriority(SuppressionLevel level, bool antiCheat)
        {
            if (antiCheat) return level >= SuppressionLevel.Isolated ? 1 : (level >= SuppressionLevel.Restrained ? 2 : 3);
            if (level < SuppressionLevel.Isolated || (antiCheat && !SevereAntiCheat(level, antiCheat)))
            {
                return 3;
            }
            return 1;
        }

        internal static bool DesiredTimerSeal(SuppressionLevel level, bool antiCheat)
        {
            if (level >= SuppressionLevel.Isolated)
            {
                if (antiCheat)
                {
                    return SevereAntiCheat(level, antiCheat);
                }
                return true;
            }
            return false;
        }

        internal static int DesiredBoostDisabled(SuppressionLevel level, bool antiCheat, int originalBoost)
        {
            if (originalBoost != 0 && originalBoost != 1)
            {
                return -1;
            }
            if (!DisableDynamicBoost(level, antiCheat))
            {
                return originalBoost;
            }
            return 1;
        }

        internal static string ApplyBoostPolicy(SuppressionLevel level, bool antiCheat, int originalBoost, Func<int> read, Func<bool, bool> write)
        {
            int desiredState = DesiredBoostDisabled(level, antiCheat, originalBoost);
            if (desiredState < 0)
            {
                if (!DisableDynamicBoost(level, antiCheat))
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
            return SuppressionAffinityPolicy.DesiredAffinity(e.Reasons, (e.BackgroundRestorePending && !AntiCheatThrottled(e)) ? 0 : e.SqueezeAff, e.OrigAff, allMask);
        }

        private static int DesiredGpu(Entry e)
        {
            return DesiredGpuClass(GpuDemoteEnabled && HagsTweak.SchedulingAllowsGpuDemotionCached(), e.Reasons, e.BackgroundLevel, e.OrigGpu);
        }

        internal static int AntiCheatAuditSeconds(SuppressionLevel level)
        {
            return level >= SuppressionLevel.Isolated ? 1 : (level >= SuppressionLevel.Restrained ? 2 : 4);
        }

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
            if ((reason & SuppressReason.AntiCheat) != 0)
            {
                e.AntiCheatLevel = level;
                if (!e.Journaled && !e.LegacyPlacementDebt && !e.BackgroundPlacementOwned && e.AutomaticLayoutSets == null && !e.AutomaticLayoutYielded)
                {
                    e.AntiCheatPlacementExternal = true;
                }
            }
            if ((reason & SuppressReason.Background) != 0)
            {
                e.BackgroundLevel = level;
            }
            e.Level = EffectiveLevel(e);
        }

        private static bool AntiCheatThrottled(Entry e)
        {
            return (e.Reasons & SuppressReason.AntiCheat) != 0;
        }

        private static SuppressionLevel EffectiveLevel(Entry e)
        {
            if (e.AntiCheatLevel <= e.BackgroundLevel)
            {
                return e.BackgroundLevel;
            }
            return e.AntiCheatLevel;
        }

    }
}
