using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace PaviseApp
{
    internal partial class PanelForm
    {
        private Toggle swPerformanceEnvironmentBundle;
        private SettingCard cardPerformanceEnvironmentBundle;
        private QuantumProfilePicker swPerformanceEnvironmentQuantum;
        private SettingCard cardPerformanceEnvironmentQuantum;
        private void BuildPerformanceTuningEnvironment(Control parent, ref int y)
        {
            swPerformanceEnvironmentBundle = MakeSwitch(PerformanceTuning.EnvironmentBundleEnabled, ChangePerformanceTuningBundle);
            swPerformanceEnvironmentBundle.Name = "environment" + PerformanceTuning.EnvironmentBundleKey;
            swPerformanceEnvironmentBundle.AccessibleName = PerformanceTuning.EnvironmentBundleTitle;
            int height;
            cardPerformanceEnvironmentBundle = MakeAutoCard(parent, 6, y, ScrollContentW,
                FullTextCardHeight(PerformanceTuning.EnvironmentBundleDetail, ScrollContentW, swPerformanceEnvironmentBundle, 100),
                PerformanceTuning.EnvironmentBundleTitle, PerformanceTuning.EnvironmentBundleDetail, swPerformanceEnvironmentBundle, out height);
            cardPerformanceEnvironmentBundle.Height += Theme.S(SettingCard.StatusLineH);
            y += height + SettingCard.StatusLineH + 8;

            PerformanceAction quantum = PerformanceActionCatalog.Find(PerformanceActionCatalog.Quantum);
            string title = quantum.Title + AutomaticPolicy.Text(" · 需重启", " · restart required");
            string detail = quantum.Detail + "\n" + AutomaticPolicy.Text(
                "此项单独设置。选择「恢复原配置」可撤销这里的修改。",
                "This setting is independent. Select Restore original configuration to undo these changes.");
            swPerformanceEnvironmentQuantum = new QuantumProfilePicker();
            swPerformanceEnvironmentQuantum.SetSilently(QuantumProfiles.Selected);
            swPerformanceEnvironmentQuantum.SelectionChanged += ChangePerformanceTuningQuantum;
            swPerformanceEnvironmentQuantum.Name = "environment" + quantum.Key;
            swPerformanceEnvironmentQuantum.AccessibleName = title;
            swPerformanceEnvironmentQuantum.AccessibleDescription = detail;
            cardPerformanceEnvironmentQuantum = MakeAutoCard(parent, 6, y, ScrollContentW,
                FullTextCardHeight(detail, ScrollContentW, swPerformanceEnvironmentQuantum, 88),
                title, detail, swPerformanceEnvironmentQuantum, out height);
            cardPerformanceEnvironmentQuantum.Height += Theme.S(SettingCard.StatusLineH);
            y += height + SettingCard.StatusLineH + 8;
            SyncPerformanceTuningEnvironment();
        }
        private void ChangePerformanceTuningBundle(object sender, EventArgs args)
        {
            if (!RequireElevationFor(swPerformanceEnvironmentBundle, PerformanceTuning.EnvironmentBundleEnabled)) return;
            bool ok = PerformanceTuning.SetEnvironmentBundle(swPerformanceEnvironmentBundle.Checked, delegate {
                try { if (!IsDisposed && IsHandleCreated) BeginInvoke((Action)delegate { SyncPerformanceTuningEnvironment(); }); } catch { }
            });
            if (ok) gameMode.Poke();
            SyncPerformanceTuningEnvironment();
            if (!ok) PaviseDialog.Warn(this, App.DisplayName, Lang.T("env.performance.savefail"));
        }
        private void ChangePerformanceTuningQuantum(object sender, EventArgs args)
        {
            int selection = swPerformanceEnvironmentQuantum.SelectedIndex;
            if (!elevated) {
                PaviseDialog.Warn(this, App.DisplayName, Lang.T("env.performance.needadmin"));
                SyncPerformanceTuningEnvironment(); return;
            }
            bool ok = PerformanceTuning.SetQuantumProfile(selection, delegate {
                    try { if (!IsDisposed && IsHandleCreated) BeginInvoke((Action)delegate { SyncPerformanceTuningEnvironment(); }); } catch { }
                });
            if (ok) gameMode.Poke();
            SyncPerformanceTuningEnvironment();
            if (!ok) PaviseDialog.Warn(this, App.DisplayName, Lang.T("env.performance.savefail"));
        }
        private void SyncPerformanceTuningEnvironment()
        {
            if (swPerformanceEnvironmentBundle == null) return;
            bool on = PerformanceTuning.EnvironmentBundleEnabled;
            string state = PerformanceTuning.EnvironmentBundleState();
            swPerformanceEnvironmentBundle.SetSilently(on);
            swPerformanceEnvironmentBundle.Enabled = !PerformanceTuning.EnvironmentBundleBusy();
            string detail = PerformanceTuning.EnvironmentBundleDetail;
            if (cardPerformanceEnvironmentBundle.Desc != detail)
            {
                SettingCard card = cardPerformanceEnvironmentBundle;
                card.Desc = detail;
                int height = Theme.S(FullTextCardHeight(detail, ScrollContentW, swPerformanceEnvironmentBundle, 100) + SettingCard.StatusLineH);
                int oldHeight = card.Collapsible ? card.ExpandedHeight : card.Height;
                Dictionary<Control, int> positions;
                if (card.Parent != null && stackBase.TryGetValue(card.Parent, out positions))
                {
                    int top = positions[card];
                    foreach (Control child in new List<Control>(positions.Keys))
                        if (positions[child] > top) positions[child] += height - oldHeight;
                }
                card.ExpandedHeight = height;
                card.Height = card.Collapsible && !card.Expanded ? card.CollapsedHeight : height;
                if (card.Parent != null) RestackCards(card.Parent);
            }
            cardPerformanceEnvironmentBundle.SetStatus(PerformanceTuning.EnvironmentBundleLabel(),
                state == "failed" || state == "stopped" || state == "restore-failed" || state == "partial" ? Theme.Warning : on ? Theme.Accent : Theme.Dim);
            if (swPerformanceEnvironmentQuantum == null) return;
            bool quantumOn = Settings.LoadCached(PerformanceActionCatalog.Quantum, false);
            string quantumState = PerformanceTuning.State(PerformanceActionCatalog.Quantum);
            swPerformanceEnvironmentQuantum.SetSilently(QuantumProfiles.Selected);
            swPerformanceEnvironmentQuantum.AccessibleDescription = QuantumProfiles.Description(QuantumProfiles.Selected);
            swPerformanceEnvironmentQuantum.Enabled = !PerformanceTuning.EnvironmentBundleBusy();
            string quantumLabel = quantumState.Length == 0
                ? Lang.T(quantumOn ? "env.performance.pending" : "env.performance.off")
                : PerformanceTuning.Label(quantumState);
            cardPerformanceEnvironmentQuantum.SetStatus(quantumLabel,
                quantumState == "failed" || quantumState == "stopped" || quantumState == "restore-failed" ? Theme.Warning : quantumOn ? Theme.Accent : Theme.Dim);
        }
    }
}
