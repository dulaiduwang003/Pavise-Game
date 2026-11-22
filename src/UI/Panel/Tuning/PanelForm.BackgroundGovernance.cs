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
                if (!ConfirmBackgroundGovernanceSelection(selection))
                {
                    // 取消就把选择框退回已保存的档位
                    RefreshBackgroundGovernanceControl();
                    return;
                }
                bool ok = gameMode.TrySetBackgroundGovernanceSelection(selection);
                RefreshStrategyControls();
                RefreshAutomationPage();
                if (!ok) ShowManualPerformanceSaveFailure();
            };
        }

        // 界面测试用它代替确认框 参数是确认文案的键
        internal Func<string, bool> BackgroundGovernanceConfirmForTest = null;

        // 切到疯狂前先说明代价 已经在这一档时不再重复询问
        // 激进档每进程留 0.5 核 键鼠和耳机软件仍能响应 所以不弹窗 免得用户对确认框麻木
        private bool ConfirmBackgroundGovernanceSelection(int selection)
        {
            bool masterEnabled = PolicyResolver.RawGlobalValue(PolicyCatalog.KeySuppress) == "1";
            if (selection == BackgroundGovernancePolicy.Selection(masterEnabled, BackgroundGovernancePolicy.Load()))
            {
                return true;
            }
            string key = BackgroundGovernanceConfirmKey(selection);
            if (key == null)
            {
                return true;
            }
            if (BackgroundGovernanceConfirmForTest != null)
            {
                return BackgroundGovernanceConfirmForTest(key);
            }
            return PaviseDialog.Confirm(this, Lang.T("background.governance.title"), Lang.T(key), DlgKind.Warn);
        }

        internal static string BackgroundGovernanceConfirmKey(int selection)
        {
            if (selection == BackgroundGovernancePolicy.Freeze + 1) return "background.governance.freeze.confirm";
            return null;
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
