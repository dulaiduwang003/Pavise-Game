// File purpose Intel vendor page; what the user enables is a temporary global driver change, not XeSS injection
using System;
using System.Windows.Forms;

namespace PaviseApp
{
    internal partial class PanelForm
    {
        private Toggle swIntelLowLatency;
#if PAVISE_SELFTEST
        internal Func<bool> IntelLowLatencyConfirmationForTest;
#endif

        private void BuildIntelGraphicsPage(Control scroll)
        {
            bool available = IntelGraphicsTweaks.HasAvailable;
            bool supported = available && IntelGraphicsTweaks.LowLatencySupported;
            swIntelLowLatency = MakeSwitch(gameMode.IntelLowLatency, null);
            swIntelLowLatency.AccessibleName = Lang.T("set.intel.lowlatency");
            swIntelLowLatency.Enabled = supported || gameMode.IntelLowLatency;
            swIntelLowLatency.CheckedChanged += delegate
            {
                bool on = swIntelLowLatency.Checked;
                if (on && !ConfirmIntelLowLatency())
                {
                    swIntelLowLatency.SetSilently(gameMode.IntelLowLatency);
                    return;
                }
                gameMode.IntelLowLatency = on;
                SyncIntelGraphicsToggles();
            };
            int height;
            string detail = Lang.T(!available ? "set.intel.none" : supported
                ? "set.intel.lowlatency.n" : "set.intel.lowlatency.unsupported");
            MakeAutoCard(scroll, 6, 2, ScrollContentW,
                FullTextCardHeight(detail, ScrollContentW, swIntelLowLatency, 88),
                Lang.T("set.intel.lowlatency"), detail,
                swIntelLowLatency, out height);

            // Endurance Gaming only has something to turn off on Intel GPU machines with a battery; on desktops this row only explains why it is unavailable
            //   This page is built alone in the isolated regression without the GPU page's sync table; the switch is wired by hand like the low-latency one
            bool enduranceOk = available && Native.HasSystemBattery();
            swIntelEndurance = MakeSwitch(gameMode.IntelEnduranceOff, null);
            swIntelEndurance.Enabled = enduranceOk || gameMode.IntelEnduranceOff;
            swIntelEndurance.CheckedChanged += delegate
            {
                gameMode.IntelEnduranceOff = swIntelEndurance.Checked;
                SyncIntelGraphicsToggles();
            };
            string enduranceDetail = !available ? Lang.T("set.intel.none")
                : enduranceOk ? Lang.T("set.intel.endurance.n") : Lang.T("set.intel.endurance.desktop");
            int enduranceH;
            MakeAutoCard(scroll, 6, 2 + height + 8, ScrollContentW,
                FullTextCardHeight(enduranceDetail, ScrollContentW, swIntelEndurance, 88),
                Lang.T("set.intel.endurance"), enduranceDetail, swIntelEndurance, out enduranceH);
            SyncIntelGraphicsToggles();
            EnableCardCollapse(scroll);
        }

        private bool ConfirmIntelLowLatency()
        {
            if (!IntelGraphicsTweaks.LowLatencySupported) return false;
#if PAVISE_SELFTEST
            if (IntelLowLatencyConfirmationForTest != null) return IntelLowLatencyConfirmationForTest();
            throw new InvalidOperationException("Intel confirmation requires an injected test response.");
#elif PAVISE_PERFLAB
            throw new InvalidOperationException("Intel confirmation is unavailable in the performance lab.");
#else
            return PaviseDialog.Confirm(this, Lang.T("set.intel.lowlatency"),
                Lang.T("intel.lowlatency.warn"), DlgKind.Warn);
#endif
        }

        private Toggle swIntelEndurance;

        private void SyncIntelGraphicsToggles()
        {
            if (swIntelEndurance != null)
            {
                // No tier forces this item, so Not applicable here is the only remaining lock
                SettingCard enduranceCard = swIntelEndurance.Parent as SettingCard;
                bool usable = IntelGraphicsTweaks.HasAvailable && Native.HasSystemBattery();
                swIntelEndurance.SetSilently(gameMode.IntelEnduranceOff);
                swIntelEndurance.Enabled = gameMode.IntelEnduranceOff || usable;
                if (enduranceCard != null)
                    enduranceCard.SetLock(!gameMode.IntelEnduranceOff && !usable ? Lang.T("lock.na") : "", false);
            }
            if (swIntelLowLatency == null) return;
            SettingCard lowLatencyCard = swIntelLowLatency.Parent as SettingCard;
            if (lowLatencyCard != null)
                lowLatencyCard.SetLock(!gameMode.IntelLowLatency && !IntelGraphicsTweaks.LowLatencySupported
                    ? Lang.T("lock.na") : "", false);
            swIntelLowLatency.SetSilently(gameMode.IntelLowLatency);
            swIntelLowLatency.Enabled = IntelGraphicsTweaks.LowLatencySupported || gameMode.IntelLowLatency;
        }
    }
}
