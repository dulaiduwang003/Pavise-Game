#if PAVISE_SELFTEST
// File purpose Locks the local eligibility criteria for candidate thread boost, never enabled on the Handheld tier or low-core machines
//   user measured 45 to 50 fps on a handheld with it on, a stable 60 with it off
//   the boosted thread runs at 15 while the rest of the game stays at 13, when cores run short it preempts exactly the threads it is waiting on
//   the CPU-saturation rollback guard cannot be relied on, handhelds usually sit on the GPU or the power limit and whole-machine utilization never reaches 90
using System;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        internal static void RunRenderLaneEligibilityTests()
        {
            LaneNeverRunsOnHandheld();
            LaneNeedsEnoughPhysicalCores();
            LaneThresholdMatchesDocumentedValue();
        }

        // Handheld tier ignores the core count and is refused outright, a handheld may have many cores but the power limit pins the clocks, same collision
        private static void LaneNeverRunsOnHandheld()
        {
            Eq(false, GameMode.LaneSupported(PerformancePreset.Handheld));
        }

        // Other tiers decide by physical core count, below the threshold never enabled, above it the toggle decides
        private static void LaneNeedsEnoughPhysicalCores()
        {
            int cores;
            try { cores = CpuTopology.PhysicalCoreCount; }
            catch { return; }

            bool expected = cores >= GameMode.LaneMinPhysicalCores;
            Eq(expected, GameMode.LaneSupported(PerformancePreset.Standard));
            Eq(expected, GameMode.LaneSupported(PerformancePreset.Competitive));
            Eq(expected, GameMode.LaneSupported(PerformancePreset.Custom));

        }

        // The threshold value is written into UI copy, changing the criteria blows this up first as a reminder to update the copy too
        private static void LaneThresholdMatchesDocumentedValue()
        {
            Eq(6, GameMode.LaneMinPhysicalCores);
            string zh = Lang.T("gm.lane.unsupported");
            if (string.IsNullOrEmpty(zh) || zh == "gm.lane.unsupported")
                throw new InvalidOperationException("gm.lane.unsupported 文案缺失");
            if (zh.IndexOf("6", StringComparison.Ordinal) < 0)
                throw new InvalidOperationException("不可用文案没有写出门槛核数");
        }
    }
}
#endif
