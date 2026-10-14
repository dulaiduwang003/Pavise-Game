#if PAVISE_SELFTEST
using System;
using System.Threading;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        private static int irqAutoChecks;

        internal static int RunIrqAutoPilotRegressionTests()
        {
            Action[] tests =
            {
                IrqAutoResidueDetection,
                IrqAutoClearForResetWipesAllRecords,
                IrqAutoHealRetiresSwitchWithoutResidue
            };
            irqAutoChecks = 0;
            foreach (Action test in tests)
            {
                Settings.UseTransientStoreForCurrentProcess();
                Lang.Cur = 0;
                try { test(); }
                finally { Settings.UseTransientStoreForCurrentProcess(); }
                Console.WriteLine("PASS " + test.Method.Name);
            }
            Console.WriteLine("PASS irq-autopilot assertions=" + irqAutoChecks
                + " native_devices=untouched settings=transient windows_shown=false");
            return tests.Length;
        }

        private static void AutoCheck(bool good, string message)
        {
            if (!good) throw new InvalidOperationException("IRQ autopilot regression: " + message);
            Interlocked.Increment(ref irqAutoChecks);
        }

        private static void IrqAutoResidueDetection()
        {
            AutoCheck(!IrqAutoPilot.HasResidue, "a clean store must have no residue");
            Settings.SaveStr(IrqAutoPilot.PlanKey, "1|dev|a|b|0|P");
            AutoCheck(IrqAutoPilot.HasResidue, "a leftover plan entry is residue");
            Settings.SaveStr(IrqAutoPilot.PlanKey, "");
            Settings.Save("IrqAutoAppliedV1", true);
            AutoCheck(IrqAutoPilot.HasResidue, "the engine applied flag is residue");
            Settings.Save("IrqAutoAppliedV1", false);
            AutoCheck(!IrqAutoPilot.HasResidue, "settled records must clear the residue verdict");
        }

        private static void IrqAutoClearForResetWipesAllRecords()
        {
            Settings.Save(IrqAutoPilot.EnabledKey, true);
            Settings.SaveStr(IrqAutoPilot.PlanKey, "1|dev|a|b|0|P");
            Settings.SaveStr(IrqAutoPilot.FuseKey, "a.sys|1.0");
            IrqAutoPilot.ClearForReset();
            AutoCheck(!Settings.Load(IrqAutoPilot.EnabledKey, false)
                && Settings.LoadStr(IrqAutoPilot.PlanKey, "").Length == 0
                && Settings.LoadStr(IrqAutoPilot.FuseKey, "").Length == 0,
                "reset must wipe the switch, the plan and the fuse list");
        }

        private static void IrqAutoHealRetiresSwitchWithoutResidue()
        {
            Settings.Save(IrqAutoPilot.EnabledKey, true);
            Settings.SaveStr(IrqAutoPilot.FuseKey, "a.sys|1.0");
            IrqAutoPilot.HealFromCrash();
            AutoCheck(!Settings.Load(IrqAutoPilot.EnabledKey, false),
                "healing must retire the removed feature's switch");
            AutoCheck(Settings.LoadStr(IrqAutoPilot.FuseKey, "") == "a.sys|1.0",
                "with no residue there is nothing to revert and the fuse list is left for reset");
        }
    }
}
#endif
