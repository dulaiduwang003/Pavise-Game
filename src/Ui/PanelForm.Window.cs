// @author bdth 2074055628@qq.com
// 文件用途 窗口边框 尺寸自适应与界面活动节流
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
        private void AttachFormFrame()
        {
            if (formFrameAttached) return;
            UiClock.Frame += OnFormFrame;
            formFrameAttached = true;
        }

        private void DetachFormFrame()
        {
            if (!formFrameAttached) return;
            UiClock.Frame -= OnFormFrame;
            formFrameAttached = false;
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible) { CenterRoot(); ScheduleFit(); }
            else if (introActive) FinishIntro();
            SyncUiActivity();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            CenterRoot();
            ScheduleFit();
            SyncUiActivity();
        }

        private void CenterRoot()
        {
            if (root == null || root.IsDisposed) return;
            int x = (ClientSize.Width - root.Width) / 2;
            int y = (ClientSize.Height - root.Height) / 2;
            if (x < 0) x = 0;
            if (y < 0) y = 0;
            if (root.Left != x || root.Top != y) root.Location = new Point(x, y);
        }

        private void ScheduleFit()
        {
            if (fitting || IsDisposed || !IsHandleCreated) return;
            if (WindowState == FormWindowState.Minimized) return;
            if (!Dpi.FitDiffers(ClientSize.Width, ClientSize.Height)) return;
            if (moveSizeLoop) { fitDeferredByDrag = true; return; }
            if (fitTimer == null)
            {
                fitTimer = new System.Windows.Forms.Timer();
                fitTimer.Interval = 220;
                fitTimer.Tick += OnFitTick;
            }
            fitTimer.Stop();
            fitTimer.Start();
        }

        private void OnFitTick(object sender, EventArgs e)
        {
            if (fitTimer != null) fitTimer.Stop();
            if (fitting || IsDisposed || !IsHandleCreated) return;
            if (moveSizeLoop) { fitDeferredByDrag = true; return; }
            if (WindowState == FormWindowState.Minimized) return;
            int w = ClientSize.Width, h = ClientSize.Height;
            if (!Dpi.FitDiffers(w, h)) return;
            float target = Dpi.FitScale(w, h);
            fitting = true;
            try
            {
                Dpi.Scale = target;
                Theme.DropFontCache();
                Logger.Log(Lang.T("log.panelform.6") + w + "x" + h
                    + Lang.T("log.panelform.7") + target.ToString("F2"));
                RebuildUi();
            }
            finally { fitting = false; }
            CenterRoot();
        }

        internal bool UiActive
        {
            get { return uiActive; }
        }

        internal static bool ShouldRunUi(bool visible, FormWindowState windowState)
        {
            return visible && windowState != FormWindowState.Minimized;
        }

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            SyncUiForeground(true);
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            SyncUiForeground(false);
        }

        private void SyncUiForeground(bool foreground)
        {
            if (paviseCore != null) paviseCore.SetForeground(foreground);
            if (!foreground || !UiActive) return;
            UiClock.Wake();
            UiClock.WakeSlow();
        }

        private void SyncUiActivity()
        {
            bool next = ShouldRunUi(IsHandleCreated && !IsDisposed && Visible, WindowState);
#if PAVISE_SELFTEST
            if (SuppressUiWorkersForTest) next = false;
#endif
            if (uiActivityKnown && uiActive == next) return;

            uiActivityKnown = true;
            uiActive = next;
            bool gameActive = gameMode != null && gameMode.Enabled && gameMode.IsActive;
            SyncAutoHideBaseline(gameActive, ref lastGameActive, ref autoHideArmed);

            if (!next)
            {
                if (uiTimer != null) uiTimer.Stop();
                CancelAutoHide();
                UiClock.Suspended = true;
                if (paviseCore != null) paviseCore.SetAnimationEnabled(false);
                return;
            }

            if (builtLang != Lang.Cur) { RebuildUi(); return; }

            RefreshLightweightUiState();
            SyncToggleValues();

            UiClock.Suspended = false;
            if (uiTimer != null) uiTimer.Start();
            UiClock.Wake();
            UiClock.WakeSlow();
            NotifyPageActivation();
        }

        private void RefreshLightweightUiState()
        {
            if (gameMode == null) return;
            if (lblStatus != null)
            {
                string status = gameMode.StatusText;
                // 文案没变就别每 1.2 秒把多档 TextRenderer.MeasureText 重跑一遍
                if (lblStatus.Text != status)
                {
                    lblStatus.Text = status;
                    FitLabelFont(lblStatus, true, StatusFontMax, StatusFontMin);
                }
            }
            bool act = gameMode.Enabled && gameMode.IsActive;
            UiClock.Frozen = act;
            if (statusDot != null)
            {
                statusDot.Color = !gameMode.Enabled ? Theme.Dim : (act ? Theme.Green : Theme.Accent);
                statusDot.Pulse = act;
            }
            if (paviseCore != null) paviseCore.SetState(gameMode.ActivePreset, gameMode.Enabled, act);
            if (lblSub != null && elevated)
            {
                string game = gameMode.ActiveGame;
                string state = Lang.T("title.admin") + " "
                    + (game != null ? Lang.F("title.guard", game) : Lang.T("v20.admin.ready"));
                if (lblSub.Text != state) lblSub.Text = state;
                lblSub.ForeColor = game != null ? Theme.Green : Theme.Faint;
            }
            RefreshBoostPresentation();
        }

    }
}
