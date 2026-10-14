using System;
using System.Windows.Forms;

namespace PaviseApp
{
    internal partial class PanelForm
    {
        private Toggle swEnvironmentVisualEffects;
        private SettingCard cardEnvironmentVisualEffects;
        private void BuildEnvironmentVisualEffects(Control parent, ref int y)
        {
            PerformanceAction action = PerformanceActionCatalog.Find(PerformanceActionCatalog.VisualEffects);
            swEnvironmentVisualEffects = MakeSwitch(MatchVisualEffects.Enabled, OnEnvironmentVisualEffectsToggle);
            swEnvironmentVisualEffects.Name = "environmentVisualEffects";
            swEnvironmentVisualEffects.AccessibleName = action.Title;
            swEnvironmentVisualEffects.AccessibleDescription = action.Detail;
            int height;
            cardEnvironmentVisualEffects = MakeAutoCard(parent, 6, y, ScrollContentW,
                FullTextCardHeight(action.Detail, ScrollContentW, swEnvironmentVisualEffects, 88),
                action.Title, action.Detail, swEnvironmentVisualEffects, out height);
            cardEnvironmentVisualEffects.Height += Theme.S(SettingCard.StatusLineH);
            y += height + SettingCard.StatusLineH + 8;
            SyncEnvironmentVisualEffects();
        }
        private void OnEnvironmentVisualEffectsToggle(object sender, EventArgs e)
        {
            bool ok = gameMode.SetVisualEffectsReduction(swEnvironmentVisualEffects.Checked);
            SyncEnvironmentVisualEffects();
            if (!ok) PaviseDialog.Warn(this, App.DisplayName, Lang.T("env.performance.savefail"));
        }
        private string EnvironmentVisualEffectsStatus()
        {
            string state = gameMode.PerformanceExecutionState(PerformanceActionCatalog.VisualEffects);
            if (state == "failed" || state == "stopped" || state == "restore-failed") return PerformanceTuning.Label(state);
            if (!MatchVisualEffects.Enabled) return Lang.T("env.performance.off");
            if (state == "applied") return PerformanceTuning.Label(state);
            return AutomaticPolicy.Text("已开启 · 等待对局", "Enabled; waiting for a match");
        }
        private void SyncEnvironmentVisualEffects()
        {
            if (swEnvironmentVisualEffects == null) return;
            swEnvironmentVisualEffects.SetSilently(MatchVisualEffects.Enabled);
            string state = gameMode.PerformanceExecutionState(PerformanceActionCatalog.VisualEffects);
            cardEnvironmentVisualEffects.SetStatus(EnvironmentVisualEffectsStatus(),
                state == "failed" || state == "stopped" || state == "restore-failed" ? Theme.Warning
                    : state == "applied" ? Theme.Accent : Theme.Dim);
        }
    }
}
