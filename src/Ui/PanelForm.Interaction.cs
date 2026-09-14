// @author bdth 2074055628@qq.com
// File purpose Handle-destroy cleanup, window drag, show/hide and toggle sync
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
        protected override void OnHandleDestroyed(EventArgs e)
        {
            UnregisterPowerSchemeNotifications();
            DisposePageReveal();
            CancelAutoHide();
            outroActive = false;
            outroWatch.Reset();
            if (outroTimer != null) { outroTimer.Stop(); outroTimer.Dispose(); outroTimer = null; }
            if (uiTimer != null) uiTimer.Stop();
            if (fitTimer != null) { fitTimer.Stop(); fitTimer.Dispose(); fitTimer = null; }
            uiActive = false;
            uiActivityKnown = true;
            UiClock.Suspended = true;
            if (paviseCore != null) paviseCore.SetAnimationEnabled(false);
            DetachFormFrame();
            foreach (Bitmap bitmap in gameIconCache.Values) try { bitmap.Dispose(); } catch { }
            gameIconCache.Clear();
            foreach (Bitmap bitmap in whiteIconCache.Values)
                if (bitmap != null) try { bitmap.Dispose(); } catch { }
            whiteIconCache.Clear();
            whiteNameCache.Clear();
            if (appIcon != null) { appIcon.Dispose(); appIcon = null; }
            base.OnHandleDestroyed(e);
        }

        private void DragMove(object s, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                Native.ReleaseCapture();
                Native.SendMessage(Handle, Native.WM_NCLBUTTONDOWN, (IntPtr)Native.HT_CAPTION, IntPtr.Zero);
            }
        }

        public void ShowPanel()
        {
            if (InvokeRequired) { BeginInvoke((MethodInvoker)ShowPanel); return; }
            if (IsDisposed) return;
            CancelOutro();
            ApplyPendingDpiRebuild();
            bool wasVisible = Visible && WindowState != FormWindowState.Minimized;
            if (!wasVisible) BeginIntro();
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
            StartIntro();
            SyncUiActivity();
            if (wasVisible) SyncAllToggles();
        }

        public void SyncAllToggles()
        {
            if (InvokeRequired) { BeginInvoke((MethodInvoker)SyncAllToggles); return; }
            if (IsDisposed || !UiActive) return;
            if (builtLang != Lang.Cur) { RebuildUi(); return; }
            SyncToggleValues();
            RefreshSlowStateAsync();
            RefreshEnvironmentStateAsync();
        }

        private void SyncToggleValues()
        {
            if (gameMode == null || tamer == null) return;
            if (swGame != null) swGame.SetSilently(gameMode.Enabled);
            if (swAcMaster != null) swAcMaster.SetSilently(!tamer.Paused);
            if (swAutoHide != null) swAutoHide.SetSilently(Settings.Load(AutoHideKey, AutoHideDefault));
            if (swLogWrites != null) swLogWrites.SetSilently(Settings.Load(Logger.WritesEnabledKey, true));
            if (swPolicyBackground != null) swPolicyBackground.SetSilently(gameMode.SuppressBackground);
            for (int i = 0; i < policySync.Count; i++) policySync[i]();
            if (powerButton != null) powerButton.SetState(gameMode.PowerPlanSwitch, PowerPlanButtonLabel());
            SyncGraphicsToggles();
            SyncEnvironmentToggles();
            UpdateModePresentation(false);
            for (int i = 0; i < acGroups.Count && i < acToggles.Count; i++)
                acToggles[i].SetSilently(tamer.IsGroupEnabled(acGroups[i].Key));
        }
    }
}
