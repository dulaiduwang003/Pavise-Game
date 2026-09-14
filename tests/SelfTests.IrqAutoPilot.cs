// File purpose All state lives in a temp settings store
// No native device interfaces, registry, or windows
// IRQ autopilot has been retired, only residue detection, cleanup record management, and observation budget are regressed here
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
                IrqAutoHealRetiresSwitchWithoutResidue,
                IrqAutoObservationBudget
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

        // The engine flag was written by old versions when landing a receipt, the plan leftover is a ghost P, both kinds of ledger must be recognized
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

        // Post-retirement boot cleanup: with no residue only silently retires the switch, touches no device
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

        // The observation budget is still in service: full observation while evidence is short, once saturated skip N-1 matches and observe the Nth
        //   The verificationPending parameter stays, always false, its semantics are still regressed
        private static void IrqAutoObservationBudget()
        {
            int window = IrqSessionLedger.VerdictWindow;
            AutoCheck(IrqObservationBudget.ShouldObserve(window + 3, true, 0),
                "pending verification must force full observation");
            AutoCheck(IrqObservationBudget.ShouldObserve(window - 1, false, 99),
                "insufficient evidence must force full observation");
            for (int skips = 0; skips < IrqObservationBudget.ObserveEveryN - 1; skips++)
                AutoCheck(!IrqObservationBudget.ShouldObserve(window, false, skips),
                    "a saturated ledger must skip until the budget lands: skips=" + skips);
            AutoCheck(IrqObservationBudget.ShouldObserve(window, false,
                    IrqObservationBudget.ObserveEveryN - 1),
                "the budgeted match must observe");

            // End-of-match accounting: short matches don't move the counter, observation slots aren't burned by instant crashes or quits
            Settings.SaveStr(IrqObservationBudget.SkipCountKey, "0");
            IrqObservationBudget.CommitSession(false, IrqSessionRecord.MinUsableSeconds - 1);
            AutoCheck(Settings.LoadStr(IrqObservationBudget.SkipCountKey, "0") == "0",
                "a short skipped session must not advance the counter");
            IrqObservationBudget.CommitSession(false, IrqSessionRecord.MinUsableSeconds);
            AutoCheck(Settings.LoadStr(IrqObservationBudget.SkipCountKey, "0") == "1",
                "a real skipped match must advance the counter");
            IrqObservationBudget.CommitSession(true, IrqSessionRecord.MinUsableSeconds - 1);
            AutoCheck(Settings.LoadStr(IrqObservationBudget.SkipCountKey, "0") == "1",
                "a short observed blip must not burn the budget slot");
            IrqObservationBudget.CommitSession(true, IrqSessionRecord.MinUsableSeconds);
            AutoCheck(Settings.LoadStr(IrqObservationBudget.SkipCountKey, "0") == "0",
                "a real observed match must reset the counter");
            Settings.SaveStr(IrqObservationBudget.SkipCountKey, "");
        }
    }
}
#endif
