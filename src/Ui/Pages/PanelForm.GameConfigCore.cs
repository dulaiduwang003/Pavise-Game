using System;
using System.Drawing;
using System.Windows.Forms;

namespace PaviseApp
{
    internal partial class PanelForm
    {
        private CoreSchedulingPanel cfgCoreSchedulingPanel;

        private void BuildCfgCoreTab(Control panel)
        {
            cfgCoreSchedulingPanel = new CoreSchedulingPanel(ScrollContentW - 16,
                delegate { return cfgProfile; },
                delegate(CoreSchedulingPlan plan, string expected, string profileToken, bool follow)
                {
                    string error = gameMode.SaveCoreScheduling(plan, expected, cfgProfileId, follow, profileToken);
                    if (error == null) RefreshCfgProfile();
                    return error;
                }, delegate { return gameMode.IsActive; });
            cfgCoreSchedulingPanel.Location = new Point(Theme.S(14), Theme.S(4));
            panel.Controls.Add(cfgCoreSchedulingPanel);
            cfgRowSync.Add(SyncCfgCoreTab);
        }

        private void SyncCfgCoreTab()
        {
            if (cfgCoreSchedulingPanel != null && !cfgCoreSchedulingPanel.IsDisposed)
                cfgCoreSchedulingPanel.RefreshView();
        }
    }
}
