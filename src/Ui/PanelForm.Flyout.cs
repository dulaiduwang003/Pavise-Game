// 作者：bdth 2074055628@qq.com
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
            if (opening && gameMode.ModeSwitchLocked)
            {
                PaviseDialog.Info(this, App.DisplayName, Lang.T("mode.locked.ingame"));
                return;
            }
            SetModeFlyout(opening);
        }

        private static bool IsAdvancedPage(int index)
        {
            return false;
        }

        private void TogglePowerFlyout()
        {
            SetPowerFlyout(powerFlyout != null && !powerFlyout.Visible);
        }

        private void SetPowerFlyout(bool visible)
        {
            if (visible) StopPageReveal();
            if (powerFlyout == null) return;
            if (visible)
            {
                SetModeFlyout(false);
                powerFlyout.Open(PowerPlan.EffectivePlanId);
                powerFlyout.Left = Math.Min(powerButton.Parent.Left + powerButton.Left, root.ClientSize.Width - powerFlyout.Width - Theme.S(12));
                powerFlyout.Top = powerButton.Parent.Bottom + Theme.S(8);
            }
            powerFlyout.Visible = visible;
            if (visible) powerFlyout.BringToFront();
        }

        private void ChoosePowerPlan(string id)
        {
            if (gameMode.IsActive && gameMode.Enabled)
            {
                SetPowerFlyout(false);
                PaviseDialog.Info(this, App.DisplayName, Lang.T("mode.locked.ingame"));
                return;
            }
            if (id == null || !PowerPlan.SelectPlan(id))
            {
                PaviseDialog.Warn(this, App.DisplayName, Lang.T("lib.family.save.failed"));
                return;
            }
            gameMode.RefreshAutomaticPreferences();
            SetPowerFlyout(false);
            if (powerButton != null) powerButton.SetState(gameMode.PowerPlanSwitch, PowerPlanButtonLabel());
            for (int i = 0; i < policySync.Count; i++) policySync[i]();
            RefreshPolicyPresentation();
        }

        private string PowerPlanButtonLabel()
        {
            return PowerPlan.SelectionLabel;
        }

        private void SetModeFlyout(bool visible)
        {
            if (visible) StopPageReveal();
            if (modeFlyout == null) return;
            if (visible)
            {
                modeFlyout.Sync(gameMode.Preset);
                SetPowerFlyout(false);
            }
            modeFlyout.Visible = visible;
            if (visible) modeFlyout.BringToFront();
        }

        private void ChooseGlobalMode(PerformancePreset mode)
        {
            if (!gameMode.TrySetPreset(mode))
            {
                SetModeFlyout(false);
                PaviseDialog.Info(this, App.DisplayName, Lang.T(gameMode.ModeSwitchLocked ? "mode.locked.ingame" : "lib.family.save.failed"));
                return;
            }
            SetModeFlyout(false);
            UpdateModePresentation(true);
            SyncAllToggles();
        }

    }
}
