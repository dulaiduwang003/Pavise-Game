using System.Windows.Forms;

namespace PaviseApp
{
    internal partial class PanelForm
    {
        private GameCardDetailsButton criticalThreadMode;

        private void OpenRenderAffinityEditor()
        {
            GameProfile profile = FindLibraryProfile(criticalThreadProfile);
            if (profile == null) return;
            string expected = CriticalThreadRule.Raw(profile);
            using (var dialog = new RenderAffinityDialog(profile, CriticalThreadRule.ParseForRuntime(expected),
                rule => gameMode.SaveCriticalThreadRule(profile.Id, expected, rule)))
                dialog.ShowDialog(LibraryDialogOwner);
            criticalThreadExpected = CriticalThreadRule.Raw(FindLibraryProfile(profile.Id));
            criticalThreadError = null;
            SyncCriticalThreadEditor(FindLibraryProfile(profile.Id));
        }

        private string SaveCoreConfigurationWithRender(GameProfile profile, string expectedPlan, ManualCorePlan plan, bool enabled)
        {
            if (profile == null) return "schedule.manual.changed";
            string expectedRule = CriticalThreadRule.Raw(profile);
            string expectedEnabled = PolicyResolver.For(profile).ValueOf("GmAutoCoresV2");
            CriticalThreadRule initial = CriticalThreadRule.Parse(expectedRule);
            GameProfile draft = profile.Clone();
            if (plan == null) draft.Overrides.Remove("GmManualCorePlanV2");
            else draft.Overrides["GmManualCorePlanV2"] = plan.Encode();
            draft.Overrides["GmAutoCoresV2"] = enabled ? "1" : "0";
            string conflict = GameMode.ValidateCriticalPreference(draft);
            if (conflict == null)
                return gameMode.SaveCoreConfiguration(profile.Id, expectedPlan, expectedRule, expectedEnabled, plan, enabled, initial);
            string explanation = ThreadUi("新核心范围与渲染亲和性冲突。请重新选核、改为自动，或关闭渲染亲和性后保存。\n", "The new CPU range conflicts with render affinity. Choose another CPU, switch to Automatic, or turn render affinity off to save.\n") + Lang.T(conflict);
            using (var dialog = new RenderAffinityDialog(draft, initial, rule =>
                gameMode.SaveCoreConfiguration(profile.Id, expectedPlan, expectedRule, expectedEnabled, plan, enabled, rule), explanation))
            {
                if (dialog.ShowDialog(LibraryDialogOwner) != DialogResult.OK)
                    return ThreadUi("已取消核心范围修改", "CPU range change cancelled");
            }
            return null;
        }
    }
}
