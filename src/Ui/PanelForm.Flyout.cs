// @author bdth 2074055628@qq.com
// 文件用途 模式与电源浮层 高级页进入确认
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace PaviseApp
{
    internal partial class PanelForm : Form
    {
        private void ToggleModeFlyout()
        {
            bool opening = modeFlyout == null || !modeFlyout.Visible;
            // 对局中模式锁定 切换会把电源方案 压制范围和环境步整套还原再重写
            //   关闭方向不拦 弹窗开着时开局了还能收回去
            // 守护已关时立即放行 会话收尾由工作线程自己走完 不拿它扣住用户
            if (opening && gameMode.IsActive && gameMode.Enabled)
            {
                PaviseDialog.Info(this, App.DisplayName, Lang.T("mode.locked.ingame"));
                return;
            }
            SetModeFlyout(opening);
        }

        // 所有用户能走到的入口共用同一道门 只有勾了不再提示再确认进入才持久化
        // 取消 关闭弹窗或单纯勾选后反悔都不能悄悄跳过下次警告
        private bool ConfirmDeepTuningEntry()
        {
            if (Settings.Load(DeepTuningWarningSuppressedKey, false)) return true;
#if PAVISE_SELFTEST
            if (DeepTuningConfirmationForTest != null) return DeepTuningConfirmationForTest();
#endif
            var never = new Toggle();
            never.Text = Lang.T("v20.advanced.warn.never");
            never.Bg = Theme.Bg;
            never.SetSilently(false);
            never.Size = new Size(Theme.S(416), Theme.S(30));
            if (!PaviseDialog.Confirm(this,
                Lang.T("v20.advanced.warn.title"),
                Lang.T("v20.advanced.warn.body"), DlgKind.Warn, never, 468)) return false;
            if (never.Checked) Settings.Save(DeepTuningWarningSuppressedKey, true);
            return true;
        }

        private static bool IsAdvancedPage(int index)
        {
            switch ((PageId)index)
            {
                case PageId.Policy:
                case PageId.CoreScheduling:
                case PageId.AntiCheat:
                case PageId.Graphics:
                case PageId.Environment:
                case PageId.Interrupt:
                    return true;
                default:
                    return false;
            }
        }

        private void TogglePowerFlyout()
        {
            SetPowerFlyout(powerFlyout == null || !powerFlyout.Visible);
        }

        private void SetPowerFlyout(bool visible)
        {
            if (visible) StopPageReveal();
            if (powerFlyout == null) return;
            if (visible)
            {
                SetSearchFlyout(false);
                SetModeFlyout(false);
                powerFlyout.Open(gameMode.PowerPlanSwitch, PowerPlan.EffectivePlanId);
            }
            powerFlyout.Visible = visible;
            if (visible) powerFlyout.BringToFront();
        }

        private void ChoosePowerPlan(string id)
        {
            if (id == null) gameMode.PowerPlanSwitch = false;
            else { PowerPlan.SelectPlan(id); gameMode.PowerPlanSwitch = true; }
            SetPowerFlyout(false);
            if (powerButton != null) powerButton.SetState(gameMode.PowerPlanSwitch, PowerPlanButtonLabel());
            for (int i = 0; i < policySync.Count; i++) policySync[i]();
            if (pageGameConfig != null && pageGameConfig.Visible) SyncCfgRows();
        }

        private string PowerPlanButtonLabel()
        {
            if (!gameMode.PowerPlanSwitch) return Lang.T("plan.pick.off");
            if (PowerPlan.ManagedSelected) return PowerPlan.ManagedPlanTitle;
            string id = PowerPlan.EffectivePlanId;
            foreach (PowerPlanEntry entry in PowerPlan.ListUserPlans())
                if (string.Equals(entry.Id.ToString(), id, StringComparison.OrdinalIgnoreCase))
                    return entry.Name;
            return PowerPlan.ManagedPlanTitle;
        }

        private void SetModeFlyout(bool visible)
        {
            if (visible) StopPageReveal();
            if (modeFlyout == null) return;
            if (visible)
            {
                modeFlyout.Sync(gameMode.Preset);
                SetSearchFlyout(false);
                SetPowerFlyout(false);
            }
            modeFlyout.Visible = visible;
            if (visible) modeFlyout.BringToFront();
        }

        private void ChooseGlobalMode(PerformancePreset mode)
        {
            // 兜底 弹窗开着的瞬间恰好进了对局 点选也不放行
            if (gameMode.IsActive && gameMode.Enabled)
            {
                SetModeFlyout(false);
                PaviseDialog.Info(this, App.DisplayName, Lang.T("mode.locked.ingame"));
                return;
            }
            gameMode.Preset = mode;
            SetModeFlyout(false);
            UpdateModePresentation(true);
            SyncAllToggles();
        }

        private readonly System.Collections.Generic.List<GuardVeil> guardVeils
            = new System.Collections.Generic.List<GuardVeil>();

    }
}
