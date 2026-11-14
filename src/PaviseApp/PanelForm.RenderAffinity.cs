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
            string expectedResource;
            profile.Overrides.TryGetValue(PolicyCatalog.KeyBoost, out expectedResource);
            bool expectedResourceEnabled = PolicyResolver.For(profile).BoostGame;
            CriticalThreadRule initial = CriticalThreadRule.ParseForRuntime(expected);
            using (var dialog = new RenderAffinityDialog(profile, initial,
                (rule, resource) => gameMode.SaveRenderSchedulingConfiguration(profile.Id, expected, expectedResource, expectedResourceEnabled, rule, resource), null, RenderAffinityDisabledStatus(profile),
                gameMode.LastSchedulingEvidence(profile.Id)))
                dialog.ShowDialog(LibraryDialogOwner);
            criticalThreadExpected = CriticalThreadRule.Raw(FindLibraryProfile(profile.Id));
            criticalThreadError = null;
            SyncCriticalThreadEditor(FindLibraryProfile(profile.Id));
        }

        private static string RenderAffinityDisabledStatus(GameProfile profile)
        {
            // 启停由外层开关管理，分别说明全局和逐游戏停用状态。
            if (PolicyResolver.GlobalValue(PolicyCatalog.KeyGameScheduling) != "1")
                return Lang.T("gm.scheduling.masteroff");
            if (PolicyResolver.For(profile).ValueOf(PolicyCatalog.KeyGameScheduling) != "1")
                return Lang.T("gm.scheduling.gameoff");
            return null;
        }

        private string SaveCoreConfigurationWithRender(GameProfile profile, string expectedPlan, ManualCorePlan plan, bool enabled)
        {
            return SaveCoreConfigurationWithRender(profile, expectedPlan, plan, enabled, CriticalThreadRule.Raw(profile),
                PolicyResolver.For(profile).ValueOf(PolicyCatalog.KeyAutoCores), null);
        }

        private string SaveCoreConfigurationWithRender(GameProfile profile, string expectedPlan, ManualCorePlan plan, bool enabled,
            string expectedRule, string expectedEnabled, CoreRangeSwitchDraft switchDraft)
        {
            if (profile == null) return "schedule.manual.changed";
            string expectedResource;
            profile.Overrides.TryGetValue(PolicyCatalog.KeyBoost, out expectedResource);
            bool expectedResourceEnabled = PolicyResolver.For(profile).BoostGame;
            CriticalThreadRule initial = CriticalThreadRule.Parse(expectedRule);
            GameProfile draft = profile.Clone();
            if (plan == null) draft.Overrides.Remove("GmManualCorePlanV2");
            else draft.Overrides["GmManualCorePlanV2"] = plan.Encode();
            draft.Overrides["GmAutoCoresV2"] = enabled ? "1" : "0";
            string conflict = GameMode.ValidateCriticalPreference(draft);
            if (conflict == null)
                return gameMode.SaveCoreConfiguration(profile.Id, expectedPlan, expectedRule, expectedEnabled, plan, enabled, initial, null, false, null, switchDraft);
            string explanation = ThreadUi("新核心范围与渲染线程目标冲突。请重新选核、改为自动，或关闭渲染线程调度后保存。\n", "The new CPU range conflicts with the render thread target. Choose another CPU, switch to Automatic, or turn render thread scheduling off to save.\n") + Lang.T(conflict);
            using (var dialog = new RenderAffinityDialog(draft, initial, (rule, resource) =>
                gameMode.SaveCoreConfiguration(profile.Id, expectedPlan, expectedRule, expectedEnabled, plan, enabled, rule,
                    expectedResource, expectedResourceEnabled, resource, switchDraft), explanation, RenderAffinityDisabledStatus(draft)))
            {
                if (dialog.ShowDialog(LibraryDialogOwner) != DialogResult.OK)
                    return ThreadUi("已取消核心范围修改", "CPU range change cancelled");
            }
            return null;
        }
    }
}
