using System;
using System.Windows.Forms;

namespace PaviseApp
{
    internal partial class PanelForm
    {
        private void ConfigureBackgroundGovernanceControl()
        {
            TechSelect picker = modeFlyout.BackgroundGovernance;
            picker.SelectionChanged += delegate(int selection) {
                if (automationUpdating || !picker.Enabled) return;
                bool ok = gameMode.TrySetBackgroundGovernanceSelection(selection);
                RefreshStrategyControls();
                RefreshAutomationPage();
                if (!ok) ShowManualPerformanceSaveFailure();
            };
        }

        private void RefreshBackgroundGovernanceControl()
        {
            TechSelect picker = modeFlyout.BackgroundGovernance;
            bool enabled = PolicyResolver.RawGlobalValue(PolicyCatalog.KeySuppress) == "1";
            picker.SetSilently(BackgroundGovernancePolicy.Selection(enabled, BackgroundGovernancePolicy.Load()));
            picker.Enabled = !runtimeShutdownBusy && wipeBusy == 0;
            if (!picker.Enabled) picker.CloseDropDown();
            picker.AccessibleDescription = Lang.T("background.governance.detail") + Environment.NewLine + gameMode.BackgroundGovernanceStatus;
            modeFlyout.RefreshBackgroundGovernanceInformation();
        }
    }
}
