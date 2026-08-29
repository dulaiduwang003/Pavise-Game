// Intel vendor page. The user opts into a temporary global driver change, not XeSS injection.
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

        private void SyncIntelGraphicsToggles()
        {
            if (swIntelLowLatency == null) return;
            swIntelLowLatency.SetSilently(gameMode.IntelLowLatency);
            swIntelLowLatency.Enabled = IntelGraphicsTweaks.LowLatencySupported || gameMode.IntelLowLatency;
        }
    }
}
