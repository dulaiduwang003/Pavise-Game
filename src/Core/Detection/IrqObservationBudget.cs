// @author bdth 2074055628@qq.com
// File purpose Observation throttling: once evidence is saturated, observe matches on a budget; observe every match while evidence is short or verification is pending
using System;
using System.Globalization;

namespace PaviseApp
{
    // Match observation is the heaviest probe in the family: every DPC writes an event and the consumer thread decodes for the whole match
    //   The ledger keeps only 12 matches and the verdict looks at a 5-match window; observing every match in full after evidence saturates
    //   yields near-zero new information at full cost, so this samples matches on a budget: observe one in every four
    //
    // Three cases always observe in full; each criterion is required
    //   Usable matches from this boot are still short of the verdict window, evidence is still accumulating
    //   Autopilot has pinning awaiting reboot or under verification; verification is waiting on these matches
    //   Any step of the decision errors out; better to observe in full than to lose evidence
    // After a reboot or topology change the ledger's usable-match count resets on its own and full observation resumes; no explicit reset needed
    internal static class IrqObservationBudget
    {
        internal const int ObserveEveryN = 4;
        internal const string SkipCountKey = "IrqObserveSkipsV1";

        // Pure decision; isolated tests feed parameters directly
        internal static bool ShouldObserve(int bootUsableSessions, bool verificationPending, int skips)
        {
            if (verificationPending) return true;
            if (bootUsableSessions < IrqSessionLedger.VerdictWindow) return true;
            return skips >= ObserveEveryN - 1;
        }

        // Read-only decision at match start, counters untouched; bookkeeping is deferred to match end
        //   Booking at match start would let crash-outs and instant quits, which are not usable matches, burn observation slots
        //   Worst case, short matches alternate with real ones in phase lock: every observed match is junk and real matches keep getting skipped
        public static bool Peek()
        {
            try
            {
                bool pending = false; // Autopilot has been removed; no more pending-verification pinning forcing full observation
                int usable = 0;
                foreach (IrqSessionRecord rec in IrqSessionLedger.Load())
                    if (rec != null && rec.UsableForVerdict) usable++;
                bool observe = ShouldObserve(usable, pending, LoadSkips());
                if (!observe)
                    Logger.Log(Lang.F("log.irqbudget.1",
                        usable.ToString(CultureInfo.InvariantCulture),
                        ObserveEveryN.ToString(CultureInfo.InvariantCulture)));
                return observe;
            }
            catch { return true; }
        }

        // Match-end bookkeeping; matches shorter than the usable threshold leave counters untouched, observed or not
        //   A skipped real match counts one; an observed real match resets to zero
        public static void CommitSession(bool observed, int durationSeconds)
        {
            try
            {
                if (durationSeconds < IrqSessionRecord.MinUsableSeconds) return;
                Settings.SaveStr(SkipCountKey, observed ? "0"
                    : (LoadSkips() + 1).ToString(CultureInfo.InvariantCulture));
            }
            catch { }
        }

        private static int LoadSkips()
        {
            int skips;
            int.TryParse(Settings.LoadStr(SkipCountKey, "0"), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out skips);
            return skips;
        }
    }
}
