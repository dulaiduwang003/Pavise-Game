using System;
using System.Windows.Forms;

#if PAVISE_SELFTEST
namespace PaviseApp
{
    internal partial class PanelForm
    {
        private void BuildIntelGraphicsPage(Control scroll)
        {
            bool available = IntelGraphicsApi.HasAvailable;
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
            MakeAutoCard(scroll, 6, 2, ScrollContentW,
                FullTextCardHeight(enduranceDetail, ScrollContentW, swIntelEndurance, 88),
                Lang.T("set.intel.endurance"), enduranceDetail, swIntelEndurance, out enduranceH);
            SyncIntelGraphicsToggles();
            EnableCardCollapse(scroll);
        }

        private Toggle swIntelEndurance;

        private void SyncIntelGraphicsToggles()
        {
            if (swIntelEndurance != null)
            {
                SettingCard enduranceCard = swIntelEndurance.Parent as SettingCard;
                bool usable = IntelGraphicsApi.HasAvailable && Native.HasSystemBattery();
                swIntelEndurance.SetSilently(gameMode.IntelEnduranceOff);
                swIntelEndurance.Enabled = gameMode.IntelEnduranceOff || usable;
                if (enduranceCard != null)
                    enduranceCard.SetLock(!gameMode.IntelEnduranceOff && !usable ? Lang.T("lock.na") : "", false);
            }
        }
    }
}
#endif
