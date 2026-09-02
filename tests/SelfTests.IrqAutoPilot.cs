// All state lives in the transient settings store. No native device access,
// registry or windows are used.
// 自动中断编排已下架 这里只回归残账识别 清退记录管理与观测预算
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

        // 引擎旗标是旧版本落地收据时写下的 计划残条是"幽灵 P" 两种账都必须被认出来
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

        // 下架后的开机清退 无残账时只静默退役开关 不碰任何设备
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

        // 观测预算仍在服役 证据不足全量观测 饱和后跳满 N-1 局观测第 N 局
        //   verificationPending 参数保留(恒为假) 语义回归照测
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

            // 局末记账 短局不动计数 观测名额不被闪退秒退烧掉
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
