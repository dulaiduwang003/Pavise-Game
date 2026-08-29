using System;
using System.Windows.Forms;

namespace PaviseApp
{
    internal partial class PanelForm
    {
        private Toggle swGpuPref;

        private void BuildCommonGraphicsPage(Control scroll)
        {
            int y = 2, height;
            bool hybrid = GpuPrefStage.Supported;
            swGpuPref = MakeSwitch(gameMode.GpuPrefStageOn, null);
            swGpuPref.Enabled = hybrid || gameMode.GpuPrefStageOn;
            swGpuPref.CheckedChanged += delegate { gameMode.GpuPrefStageOn = swGpuPref.Checked; };
            MakeAutoCard(scroll, 6, y, ScrollContentW, 76, Lang.T("set.gpupref"),
                hybrid ? Lang.T("set.gpupref.n") : Lang.T("set.gpupref.single"), swGpuPref, out height);
            y += height + 8;

            var manage = new PillButton(Lang.T("apppref.manage"));
            manage.Size = new System.Drawing.Size(Theme.S(150), Theme.S(32));
            manage.Click += delegate
            {
                using (var dialog = new AppGpuPreferencesDialog(AppGpuPreferences.Shared))
                    dialog.ShowDialog(this);
            };
            SettingCard card = MakeAutoCard(scroll, 6, y, ScrollContentW,
                FullTextCardHeight(Lang.T("set.apppref.n"), ScrollContentW, manage, 108),
                Lang.T("set.apppref"), Lang.T("set.apppref.n"), manage, out height);
            card.SetStatus(Lang.T("apppref.nextlaunch"), Theme.Accent);
            // Keep the scope and next-launch notice visible when first opening this tab.
            EnableCardCollapse(scroll, card);
        }

        private void SyncCommonGraphicsToggles()
        {
            if (swGpuPref == null) return;
            swGpuPref.SetSilently(gameMode.GpuPrefStageOn);
            swGpuPref.Enabled = GpuPrefStage.Supported || gameMode.GpuPrefStageOn;
        }
    }
}
