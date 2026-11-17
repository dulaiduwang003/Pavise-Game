using System;
using System.Windows.Forms;

namespace PaviseApp
{
    internal partial class PanelForm
    {
        private Toggle swPerformanceCpuMitigations;
        private SettingCard cardPerformanceCpuMitigations;
        private bool? environmentCpuMitigationsOverride;

        private void BuildPerformanceCpuMitigations(Control parent, ref int y)
        {
            PerformanceAction action = PerformanceActionCatalog.Find(PerformanceActionCatalog.CpuMitigations);
            string title = action.Title + PerformancePolicy.Text(" · 需重启", " · restart required");
            string detail = action.Detail;
            swPerformanceCpuMitigations = MakeSwitch(false, ChangePerformanceCpuMitigations);
            swPerformanceCpuMitigations.Name = "environment" + action.Key;
            swPerformanceCpuMitigations.AccessibleName = title;
            swPerformanceCpuMitigations.AccessibleDescription = detail;
            int used;
            cardPerformanceCpuMitigations = MakeAutoCard(parent, 6, y, ScrollContentW,
                FullTextCardHeight(detail, ScrollContentW, swPerformanceCpuMitigations, 100),
                title, detail, swPerformanceCpuMitigations, out used);
            cardPerformanceCpuMitigations.Height += Theme.S(17);
            y += used + 17 + 8;
        }

        private void ChangePerformanceCpuMitigations(object sender, EventArgs args)
        {
            if (!RequireElevationFor(swPerformanceCpuMitigations,
                Settings.LoadCached(PerformanceActionCatalog.CpuMitigations, false)))
            {
                return;
            }
            bool on = swPerformanceCpuMitigations.Checked;
            bool accepted = PerformanceTuning.SetCpuMitigations(on, delegate
            {
                try
                {
                    if (!IsDisposed && IsHandleCreated)
                    {
                        BeginInvoke((Action)delegate { RefreshPerformanceEnvironmentObservation(); });
                    }
                }
                catch
                {
                }
            });
            if (accepted)
            {
                environmentCpuMitigationsOverride = on;
                gameMode.Poke();
            }
            RefreshPerformanceEnvironmentObservation();
            if (!accepted)
            {
                PaviseDialog.Warn(this, "PAVISE AUTO", Lang.T("env.performance.savefail"));
            }
        }

        private void SyncPerformanceCpuMitigations()
        {
            if (swPerformanceCpuMitigations == null || swPerformanceCpuMitigations.IsDisposed
                || cardPerformanceCpuMitigations == null || cardPerformanceCpuMitigations.IsDisposed)
            {
                return;
            }
            EnvironmentTuningObservation observed = CachedEnvironmentTuning;
            bool busy = PerformanceTuning.EnvironmentBundleBusy();
            bool on = environmentCpuMitigationsOverride ?? (observed != null && observed.CpuMitigationsOn);
            bool expanded = environmentCpuMitigationsOverride.HasValue
                ? environmentCpuMitigationsOverride.Value : (observed != null && observed.CpuMitigationsExpanded);
            string state = busy ? "working" : (observed == null ? "unverified" : observed.CpuMitigationsState);
            swPerformanceCpuMitigations.SetSilently(on);
            swPerformanceCpuMitigations.Enabled = observed != null && !busy;
            bool failed = state == "failed" || state == "stopped" || state == "restore-failed";
            string label = state.Length == 0
                ? Lang.T(on ? "env.performance.pending" : "env.performance.off")
                : PerformanceTuning.Label(state);
            // 基础范围是作者同款预设的正常取值，不是故障：只在正常状态后标注范围，不用警告色。
            bool basicScope = on && !expanded && !failed && state != "working";
            if (basicScope) label += PerformancePolicy.Text(" · 基础范围", " · basic scope");
            cardPerformanceCpuMitigations.SetStatus(label, failed ? Theme.Warning : (on ? Theme.Accent : Theme.Dim));
        }
    }
}
