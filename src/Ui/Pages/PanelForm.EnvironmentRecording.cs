using System;
using System.Windows.Forms;

namespace PaviseApp
{
    internal partial class PanelForm
    {
        private Toggle swEnvironmentGameDvr;
        private SettingCard cardEnvironmentGameDvr;

        private void BuildEnvironmentRecordingOption(Control parent, ref int y)
        {
            swEnvironmentGameDvr = MakeSwitch(gameMode.KillGameDvr, OnEnvironmentGameDvrToggle);
            swEnvironmentGameDvr.Name = "environmentGameDvrOff";
            swEnvironmentGameDvr.AccessibleName = Lang.T("set.dvr");
            swEnvironmentGameDvr.AccessibleDescription = Lang.T("env.dvr.manual");
            int height;
            cardEnvironmentGameDvr = MakeAutoCard(parent, 6, y, ScrollContentW,
                FullTextCardHeight(Lang.T("env.dvr.manual"), ScrollContentW, swEnvironmentGameDvr, 88),
                Lang.T("set.dvr"), Lang.T("env.dvr.manual"), swEnvironmentGameDvr, out height);
            cardEnvironmentGameDvr.Height += Theme.S(SettingCard.StatusLineH);
            y += height + SettingCard.StatusLineH + 8;
            SyncEnvironmentGameDvr();
        }

        private void OnEnvironmentGameDvrToggle(object sender, EventArgs e)
        {
            bool saved = gameMode.SetGameDvrSuppression(swEnvironmentGameDvr.Checked);
            SyncEnvironmentGameDvr();
            if (!saved) PaviseDialog.Warn(this, App.DisplayName, Lang.T("env.performance.savefail"));
        }

        private string EnvironmentGameDvrStatus()
        {
            string state = gameMode.AutomaticExecutionState("GameDvrOff");
            if (state == "failed" || state == "restore-failed" || state == "stopped")
                return GameMode.AutomationStateLabel(state);
            if (!gameMode.KillGameDvr)
                return Lang.T(state == "restored" ? "env.dvr.restored" : "env.performance.off");
            if (!gameMode.Enabled) return Lang.T("env.dvr.waiting");
            return Lang.T(state == "applied" ? "env.dvr.active" : "env.performance.pending");
        }

        private void SyncEnvironmentGameDvr()
        {
            if (swEnvironmentGameDvr == null || cardEnvironmentGameDvr == null) return;
            swEnvironmentGameDvr.SetSilently(gameMode.KillGameDvr);
            string state = gameMode.AutomaticExecutionState("GameDvrOff");
            bool failed = state == "failed" || state == "restore-failed" || state == "stopped";
            cardEnvironmentGameDvr.SetStatus(EnvironmentGameDvrStatus(),
                failed ? Theme.Warning : gameMode.KillGameDvr ? Theme.Accent : Theme.Dim);
        }
    }
}
