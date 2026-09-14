using System;
using System.Windows.Forms;

namespace PaviseApp
{
    internal partial class PanelForm
    {
        private Toggle swGpuPref;
        private Toggle swAutoGpu;
#if PAVISE_SELFTEST
        internal Func<bool> AutoGpuConfirmationForTest;
#endif

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
            y += height + 8;

            swAutoGpu = MakeSwitch(gameMode.AutoGpuPreference, null);
            swAutoGpu.Enabled = hybrid || gameMode.AutoGpuPreference;
            swAutoGpu.CheckedChanged += delegate
            {
                bool on = swAutoGpu.Checked;
                if (on && !ConfirmAutoGpuEnable())
                {
                    swAutoGpu.SetSilently(gameMode.AutoGpuPreference);
                    return;
                }
                gameMode.AutoGpuPreference = on;
                swAutoGpu.SetSilently(gameMode.AutoGpuPreference);
            };
            SettingCard auto = MakeAutoCard(scroll, 6, y, ScrollContentW,
                FullTextCardHeight(Lang.T("set.autogpu.n"), ScrollContentW, swAutoGpu, 96),
                Lang.T("set.autogpu"), Lang.T("set.autogpu.n"), swAutoGpu, out height);
            auto.SetStatus(Lang.T("apppref.nextlaunch"), Theme.Accent);
            y += height + 8;

            // The first time this tab opens, the scope and the takes-effect-next-launch hint must be visible
            EnableCardCollapse(scroll, card);
        }

        private bool ConfirmAutoGpuEnable()
        {
#if PAVISE_SELFTEST
            if (AutoGpuConfirmationForTest != null) return AutoGpuConfirmationForTest();
            throw new InvalidOperationException("Auto GPU confirmation requires an injected test response.");
#elif PAVISE_PERFLAB
            throw new InvalidOperationException("Auto GPU confirmation is unavailable in the performance lab.");
#else
            return PaviseDialog.Confirm(this, Lang.T("set.autogpu"),
                Lang.T("autogpu.warn"), DlgKind.Warn);
#endif
        }

        private void SyncCommonGraphicsToggles()
        {
            if (swGpuPref == null) return;
            swGpuPref.SetSilently(gameMode.GpuPrefStageOn);
            swGpuPref.Enabled = GpuPrefStage.Supported || gameMode.GpuPrefStageOn;
            if (swAutoGpu == null) return;
            swAutoGpu.SetSilently(gameMode.AutoGpuPreference);
            swAutoGpu.Enabled = GpuPrefStage.Supported || gameMode.AutoGpuPreference;
        }
    }
}
