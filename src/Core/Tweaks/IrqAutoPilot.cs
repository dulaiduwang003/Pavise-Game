// @author bdth 2074055628@qq.com
// File purpose IRQ autopilot is withdrawn; only receipt-based cleanup of historical pins, the residue report and the wipe flow interfaces remain
using System;
using System.Collections.Generic;

namespace PaviseApp
{
    // IRQ autopilot shipped in 2.1.3.3 and was withdrawn soon after; IRQ core moves only go through the manual flow on the interrupt page
    //   Registry pins written by older versions per verdict are still on the machine; this restores them all by receipt at startup
    //   A failed restore retries at next startup, the wipe flow is the fallback; plan and circuit breaker records are cleared too
    internal static class IrqAutoPilot
    {
        internal const string EnabledKey = "IrqAutoOn";
        internal const string PlanKey = "IrqAutoPlanV1";
        internal const string FuseKey = "IrqAutoFuseV1";

        private static readonly object lk = new object();
        private static readonly IrqAffinityEngine engine =
            new IrqAffinityEngine("IrqAutoAppliedV1", "IrqAuto_", Lang.T("irqauto.logprefix"));

        public static bool HasResidue
        {
            get { return engine.HasResidue || Settings.LoadStr(PlanKey, "").Length != 0; }
        }

        // The manual IRQ core move page must treat auto pins not yet cleaned up as already managed; no double takeover
        public static List<string> TouchedDevices() { return engine.TouchedDevices(); }

        public static bool RevertAll()
        {
            lock (lk)
            {
                bool ok = IrqMutationBoundary.Run(delegate { return engine.Disable(null); });
                // Failing to clear the plan counts as failure too; leftover entries would be treated as unsettled records forever
                if (ok && !Settings.SaveStr(PlanKey, "")) ok = false;
                return ok;
            }
        }

        // Post-withdrawal boot cleanup; the switch is silently retired; restore whatever is on the books until it's clear
        public static void HealFromCrash()
        {
            try
            {
                Settings.Save(EnabledKey, false);
                if (HasResidue && RevertAll()) Settings.SaveStr(FuseKey, "");
            }
            catch { }
        }

        public static void ClearForReset()
        {
            lock (lk)
            {
                Settings.SaveStr(PlanKey, "");
                Settings.SaveStr(FuseKey, "");
                Settings.Save(EnabledKey, false);
            }
        }
    }
}
