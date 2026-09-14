// @author bdth 2074055628@qq.com
// File purpose Mode and power flyouts, Advanced page entry confirmation
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
            // Mode is locked during a match: switching would restore and rewrite the whole set of power scheme, suppression scope and environment steps
            //   Closing is not blocked, so the flyout can still be dismissed if a match starts while it is open
            // Guard already off: let through immediately, session teardown finishes on the worker thread, do not hold the user on it
            if (opening && gameMode.IsActive && gameMode.Enabled)
            {
                PaviseDialog.Info(this, App.DisplayName, Lang.T("mode.locked.ingame"));
                return;
            }
            SetModeFlyout(opening);
        }

        // Every user-reachable entry shares this one gate; only persist when "Don't show this again" is checked and entry is confirmed
        // Cancel, closing the dialog, or checking then backing out must never silently skip the next warning
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
            // policySync only silently re-reads toggle values, it does not recompute the "applies on this machine" layer
            //   Idle policy only takes effect on the managed scheme, must recompute right after a scheme change or the hint stays stale
            RefreshPolicyPresentation();
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
            // Fallback: if a match started while the flyout was open, a click still does not go through
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
