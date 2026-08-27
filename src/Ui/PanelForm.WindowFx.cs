// @author bdth 2074055628@qq.com
// 文件用途 主窗口入场退场动效与对局中的自动隐藏
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PaviseApp
{
    internal partial class PanelForm
    {
        private Motion introMotion;
        private bool introActive, introPending;
        private int introBaseTop;
        private System.Windows.Forms.Timer autoHideTimer;
        private bool autoHideArmed, lastGameActive;
        private System.Windows.Forms.Timer outroTimer;
        private readonly Stopwatch outroWatch = new Stopwatch();
        private bool outroActive;
        private int outroBaseTop;

        private const string AutoHideKey = "AutoHideOnGame";
        private const bool AutoHideDefault = true;
        private const int AutoHideDelayMs = 10000;
        private const int IntroRise = 18;
        private const int OutroMs = 220;

        private void OnFormFrame(object s, EventArgs e)
        {
            if (Theme.StepTheme())
            {
                RefreshAccentLabels();
                if (!Theme.ThemeAnimating) RunThemeRefreshers();
                Invalidate(true);
            }
            if (curPage != null && pageSlide.Step())
                curPage.Left = pageBaseLeft + (int)(pageSlide.Value * Theme.S(16));
            StepIntro();
            if (!introActive && !introPending && !outroActive && AllowTransparency) DropLayeredStyle();
        }

        private void StepIntro()
        {
            if (!introActive) return;
            if (introMotion.Step())
            {
                Top = introBaseTop + (int)(introMotion.Value * Theme.S(IntroRise));
                Opacity = 1.0 - introMotion.Value;
            }
            else FinishIntro();
        }

        private void FinishIntro()
        {
            introActive = false;
            Top = introBaseTop;
            if (Opacity < 1.0) Opacity = 1.0;
            DropLayeredStyle();
        }

        private void DropLayeredStyle()
        {
            if (!AllowTransparency) return;
            try
            {
                Opacity = 1.0;
                AllowTransparency = false;
                Invalidate(true);
            }
            catch { }
        }

        private void BeginIntro()
        {
            if (introActive) { introActive = false; Top = introBaseTop; }
            introPending = true;
            Opacity = 0.0;
        }

        private void StartIntro()
        {
            if (!introPending) { DropLayeredStyle(); return; }
            introPending = false;
            introBaseTop = Top;
            introMotion.Speed = 0.24f;
            introMotion.Set(1f);
            introMotion.To(0f);
            introActive = true;
            Top = introBaseTop + Theme.S(IntroRise);
            PaintTree(this);
            UiClock.Wake(90);
            if (!UiClock.Running) FinishIntro();
        }

        private static void PaintTree(Control c)
        {
            if (!c.IsHandleCreated || !c.Visible) return;
            c.Update();
            for (int i = 0; i < c.Controls.Count; i++) PaintTree(c.Controls[i]);
        }

        internal static void SyncAutoHideBaseline(bool gameActive, ref bool lastActive, ref bool armed)
        {
            lastActive = gameActive;
            armed = gameActive;
        }

        private void OnUiTick(object s, EventArgs e)
        {
            if (!UiActive || moveSizeLoop) return;
            UpdateAutoHide(gameMode.Enabled && gameMode.IsActive);
            RefreshLightweightUiState();
            UpdateModePresentation(true);
            PageHook hook = CurrentPageHook();
            if (hook != null && hook.OnTick != null) hook.OnTick();
        }

        internal static AutoHideAction NextAutoHide(bool gameActive, ref bool lastActive, ref bool armed,
            bool settingOn, bool visible)
        {
            if (gameActive == lastActive) return AutoHideAction.None;
            lastActive = gameActive;
            if (!gameActive) { armed = false; return AutoHideAction.Cancel; }
            if (armed) return AutoHideAction.None;
            armed = true;
            if (!settingOn || !visible) return AutoHideAction.None;
            return AutoHideAction.Schedule;
        }

        private void UpdateAutoHide(bool gameActive)
        {
            AutoHideAction action = NextAutoHide(gameActive, ref lastGameActive, ref autoHideArmed,
                Settings.Load(AutoHideKey, AutoHideDefault), UiActive);
            if (action == AutoHideAction.Cancel) { CancelAutoHide(); return; }
            if (action != AutoHideAction.Schedule) return;
            CancelAutoHide();
            autoHideTimer = new System.Windows.Forms.Timer();
            autoHideTimer.Interval = AutoHideDelayMs;
            autoHideTimer.Tick += OnAutoHideTick;
            autoHideTimer.Start();
        }

        private void OnAutoHideTick(object s, EventArgs e)
        {
            if (moveSizeLoop) return;
            CancelAutoHide();
            if (IsDisposed || !UiActive) return;
            if (AnyDialogOpen()) return;
            BeginOutro();
        }

        private void BeginOutro()
        {
            if (outroActive) return;
            if (!IsHandleCreated || IsDisposed || !Visible
                || WindowState != FormWindowState.Normal) { Hide(); return; }
            outroActive = true;
            outroBaseTop = Top;
            AllowTransparency = true;
            Opacity = 1.0;
            outroWatch.Reset();
            outroWatch.Start();
            if (outroTimer == null)
            {
                outroTimer = new System.Windows.Forms.Timer();
                outroTimer.Interval = 10;
                outroTimer.Tick += OnOutroTick;
            }
            outroTimer.Start();
        }

        private void OnOutroTick(object s, EventArgs e)
        {
            if (!outroActive || IsDisposed) { FinishOutro(false); return; }
            double t = outroWatch.Elapsed.TotalMilliseconds / OutroMs;
            if (t >= 1.0) { FinishOutro(true); return; }
            double k = t * t;
            Opacity = 1.0 - k;
            Top = outroBaseTop + (int)(k * Theme.S(IntroRise));
        }

        private void FinishOutro(bool hide)
        {
            outroActive = false;
            outroWatch.Reset();
            if (outroTimer != null) outroTimer.Stop();
            if (IsDisposed) return;
            if (hide) Hide();
            Top = outroBaseTop;
            Opacity = 1.0;
            DropLayeredStyle();
        }

        private void CancelOutro()
        {
            if (!outroActive) return;
            FinishOutro(false);
        }

        private void CancelAutoHide()
        {
            if (autoHideTimer == null) return;
            autoHideTimer.Stop();
            autoHideTimer.Tick -= OnAutoHideTick;
            autoHideTimer.Dispose();
            autoHideTimer = null;
        }

        [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr hwnd);

        private bool AnyDialogOpen()
        {
            try
            {
                if (IsHandleCreated && !IsWindowEnabled(Handle)) return true;
                foreach (Form f in Application.OpenForms)
                    if (!ReferenceEquals(f, this) && f.Visible) return true;
            }
            catch { }
            return false;
        }

        private void OnAutoHideToggle(object s, EventArgs e)
        {
            Settings.Save(AutoHideKey, swAutoHide.Checked);
            if (!swAutoHide.Checked) CancelAutoHide();
            swAutoHide.SetSilently(Settings.Load(AutoHideKey, AutoHideDefault));
        }


        private void OnEscHide(object s, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape)
            {
                if (advancedPanel != null && advancedPanel.Visible) SetAdvancedPanel(false);
                else if (searchFlyout != null && searchFlyout.Visible) SetSearchFlyout(false);
                else if (powerFlyout != null && powerFlyout.Visible) SetPowerFlyout(false);
                else if (modeFlyout != null && modeFlyout.Visible) SetModeFlyout(false);
                else Hide();
            }
        }
    }
}
