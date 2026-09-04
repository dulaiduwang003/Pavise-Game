#if PAVISE_SELFTEST
namespace PaviseApp
{
    internal static partial class SelfTests
    {
        internal static int RunPowerYieldTargetRegressionTests()
        {
            bool known = false, changed;
            int high = 0;
            uint low = 0;

            Eq(-1.0, PowerBudgetYieldRunner.AcceptTargetAdapterSample(null,
                ref known, ref high, ref low, out changed));
            Eq(false, known);
            Eq(false, changed);

            var ambiguous = new RenderAdapter
            {
                LuidHigh = 1,
                LuidLow = 2,
                Util = 92.0,
                Ambiguous = true
            };
            Eq(-1.0, PowerBudgetYieldRunner.AcceptTargetAdapterSample(ambiguous,
                ref known, ref high, ref low, out changed));
            Eq(false, known);
            Eq(false, changed);

            var inactive = new RenderAdapter
            {
                LuidHigh = 7,
                LuidLow = 8,
                Util = 0.0
            };
            Eq(-1.0, PowerBudgetYieldRunner.AcceptTargetAdapterSample(inactive,
                ref known, ref high, ref low, out changed));
            Eq(false, known);
            Eq(false, changed);

            var first = new RenderAdapter
            {
                LuidHigh = 1,
                LuidLow = 2,
                Util = 143.0
            };
            Eq(100.0, PowerBudgetYieldRunner.AcceptTargetAdapterSample(first,
                ref known, ref high, ref low, out changed));
            Eq(true, known);
            Eq(1, high);
            Eq((uint)2, low);
            Eq(false, changed);

            var stable = new RenderAdapter
            {
                LuidHigh = 1,
                LuidLow = 2,
                Util = 87.5
            };
            Eq(87.5, PowerBudgetYieldRunner.AcceptTargetAdapterSample(stable,
                ref known, ref high, ref low, out changed));
            Eq(false, changed);

            // 另一块卡 0% 只是没数据 不算迁移 基线保留
            var idleOther = new RenderAdapter
            {
                LuidHigh = 1,
                LuidLow = 3,
                Util = 0.0
            };
            Eq(-1.0, PowerBudgetYieldRunner.AcceptTargetAdapterSample(idleOther,
                ref known, ref high, ref low, out changed));
            Eq(false, changed);
            Eq((uint)2, low);

            var moved = new RenderAdapter
            {
                LuidHigh = 1,
                LuidLow = 3,
                Util = 95.0
            };
            Eq(-1.0, PowerBudgetYieldRunner.AcceptTargetAdapterSample(moved,
                ref known, ref high, ref low, out changed));
            Eq(true, changed);
            Eq((uint)2, low);
            return 1;
        }
    }
}
#endif
