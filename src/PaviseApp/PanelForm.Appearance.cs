using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace PaviseApp
{
    internal partial class PanelForm : Form
    {
        private void SetToolbarVisible(bool visible)
        {
            lblAuditStatus.Visible = visible;
            btnAuditRun.Visible = visible;
            btnAuditFixAll.Visible = visible;
            btnAuditCancel.Visible = false;
            if (visible)
            {
                UpdateFixAllState();
            }
        }
        private static bool SameRgb(Color a, Color b)
        {
            if (a.R == b.R && a.G == b.G)
            {
                return a.B == b.B;
            }
            return false;
        }
        private static void ApplyBackdropLabels(Control host)
        {
            if (!Backdrop.AppliesTo(host))
            {
                return;
            }
            foreach (Control control in host.Controls)
            {
                Label label = control as Label;
                if (label != null && label.BackColor != Color.Transparent)
                {
                    label.BackColor = Color.Transparent;
                }
                if (control.HasChildren)
                {
                    ApplyBackdropLabels(control);
                }
            }
        }
        public void SetRuntimeIcon(Icon value)
        {
            if (value == null)
            {
                return;
            }
            if (base.InvokeRequired)
            {
                Icon copy = (Icon)value.Clone();
                BeginInvoke((MethodInvoker)delegate
                {
                    using (copy)
                    {
                        SetRuntimeIcon(copy);
                    }
                });
                return;
            }
            Icon icon = (Icon)value.Clone();
            Icon currentAppIcon = appIcon;
            appIcon = icon;
            base.Icon = icon;
            if (currentAppIcon != null)
            {
                currentAppIcon.Dispose();
            }
        }
        private void ApplyPendingDpiRebuild()
        {
            if (base.IsDisposed) return;
            int dpi = Dpi.WindowDpi(base.Handle);
            if (dpi <= 0 || !Dpi.WouldChange(dpi)) return;
            Dpi.Update(dpi);
            Theme.DropFontCache();
            Logger.Log(Lang.T("log.panelform.11", 0) + dpi);
            RebuildUi();
        }
        private static void CollectThemeControls(Control parent, List<Control> controls)
        {
            controls.Add(parent);
            foreach (Control control in parent.Controls)
            {
                CollectThemeControls(control, controls);
            }
        }
        private void ApplyUiPalette(UiPalette previous)
        {
            UiPalette next = new UiPalette();
            List<Control> controls = new List<Control>();
            CollectThemeControls(this, controls);
            if (libraryConfigurationDialog != null && !libraryConfigurationDialog.IsDisposed)
            {
                CollectThemeControls(libraryConfigurationDialog, controls);
            }
            foreach (Control currentControl in controls)
            {
                currentControl.SuspendLayout();
            }
            try
            {
                for (int listIndex = controls.Count - 1; listIndex >= 0; listIndex--)
                {
                    Control control = controls[listIndex];
                    Color color = previous.Remap(control.BackColor, next);
                    Color remap = previous.Remap(control.ForeColor, next);
                    if (control.BackColor != color)
                    {
                        control.BackColor = color;
                    }
                    if (control.ForeColor != remap)
                    {
                        control.ForeColor = remap;
                    }
                    FxControl fxControl = control as FxControl;
                    if (fxControl != null)
                    {
                        fxControl.Bg = previous.Remap(fxControl.Bg, next);
                    }
                    RoundPanel roundPanel = control as RoundPanel;
                    if (roundPanel != null)
                    {
                        roundPanel.Fill = previous.Remap(roundPanel.Fill, next);
                        roundPanel.Border = previous.Remap(roundPanel.Border, next);
                    }
                    SettingCard settingCard = control as SettingCard;
                    if (settingCard != null)
                    {
                        settingCard.ValueColor = previous.Remap(settingCard.ValueColor, next);
                        settingCard.SetStatus(settingCard.StatusText, previous.Remap(settingCard.StatusColor, next));
                    }
                    FamilySuppressionSwitch familySuppressionSwitch = control as FamilySuppressionSwitch;
                    if (familySuppressionSwitch != null)
                    {
                        familySuppressionSwitch.Surface = previous.Remap(familySuppressionSwitch.Surface, next);
                    }
                    GameCardExtension gameCardExtension = control as GameCardExtension;
                    if (gameCardExtension != null)
                    {
                        gameCardExtension.SetSurface(Theme.Card);
                    }
                    ModuleBanner moduleBanner = control as ModuleBanner;
                    if (moduleBanner != null)
                    {
                        moduleBanner.StateColor = previous.Remap(moduleBanner.StateColor, next);
                    }
                    StatusDot statusDot = control as StatusDot;
                    if (statusDot != null)
                    {
                        statusDot.Color = previous.Remap(statusDot.Color, next);
                    }
                    CoreMatrix coreMatrix = control as CoreMatrix;
                    if (coreMatrix != null)
                    {
                        coreMatrix.SelectionColor = previous.Remap(coreMatrix.SelectionColor, next);
                    }
                    LinkLabel linkLabel = control as LinkLabel;
                    if (linkLabel != null)
                    {
                        linkLabel.LinkColor = previous.Remap(linkLabel.LinkColor, next);
                        linkLabel.ActiveLinkColor = previous.Remap(linkLabel.ActiveLinkColor, next);
                        linkLabel.VisitedLinkColor = previous.Remap(linkLabel.VisitedLinkColor, next);
                    }
                    if (control.ContextMenuStrip != null)
                    {
                        RefreshMenuPalette(control.ContextMenuStrip, previous, next);
                    }
                    if (control is ScrollableControl || control is TextBoxBase || control is ListControl)
                    {
                        Native.RefreshDark(control);
                    }
                }
                if (themeSwitch != null)
                {
                    themeSwitch.SetSilently(Theme.LightMode);
                }
                for (int i = 0; i < modeSwatches.Length; i++)
                {
                    foreach (ColorSwatch modeSwatche in modeSwatches[i])
                    {
                        if (modeSwatche.IsDefault)
                        {
                            modeSwatche.Swatch = Theme.ModeColorBuiltin((PerformancePreset)i);
                            modeSwatche.Invalidate();
                        }
                    }
                }
                RefreshAccentLabels();
                RefreshModeAccentLabels();
                if (pageAbout == curPage)
                {
                    RefreshAboutIcon();
                }
            }
            finally
            {
                for (int listIndex2 = controls.Count - 1; listIndex2 >= 0; listIndex2--)
                {
                    controls[listIndex2].ResumeLayout(false);
                }
            }
            InvalidateVisibleTheme();
            if (libraryConfigurationDialog != null && !libraryConfigurationDialog.IsDisposed)
            {
                libraryConfigurationDialog.Invalidate(true);
            }
        }
        private void InvalidateVisibleTheme()
        {
            Invalidate(false);
            if (root == null || root.IsDisposed)
            {
                return;
            }
            root.Invalidate(false);
            foreach (Control control in root.Controls)
            {
                if (control.Visible)
                {
                    control.Invalidate(true);
                }
            }
        }
        private void StepIntro()
        {
            if (introActive)
            {
                if (introMotion.Step())
                {
                    base.Opacity = 1.0 - (double)introMotion.Value;
                }
                else
                {
                    FinishIntro();
                }
            }
        }
        private void FinishIntro()
        {
            introActive = false;
            if (base.Opacity < 1.0)
            {
                base.Opacity = 1.0;
            }
            DropLayeredStyle();
            QueueUnseenReleaseNotes();
        }
        private void DropLayeredStyle()
        {
            if (!base.AllowTransparency)
            {
                return;
            }
            try
            {
                base.Opacity = 1.0;
                base.AllowTransparency = false;
                Invalidate(true);
            }
            catch
            {
            }
        }
        private void BeginIntro()
        {
            StopPageReveal();
            introActive = false;
            introPending = true;
            base.Opacity = 0.0;
        }
        internal static AutoHideAction NextAutoHide(bool gameActive, ref bool lastActive, ref bool armed, bool settingOn, bool visible)
        {
            if (gameActive == lastActive)
            {
                return AutoHideAction.None;
            }
            lastActive = gameActive;
            if (!gameActive)
            {
                armed = false;
                return AutoHideAction.Cancel;
            }
            if (armed)
            {
                return AutoHideAction.None;
            }
            armed = true;
            if (!settingOn || !visible)
            {
                return AutoHideAction.None;
            }
            return AutoHideAction.Schedule;
        }
        private void BeginOutro()
        {
            if (SelfUpdateGate.IsBusy) { UpdateDialog.ActivateCurrent(); return; }
            StopPageReveal();
            if (outroActive)
            {
                return;
            }
            if (!base.IsHandleCreated || base.IsDisposed || !base.Visible || base.WindowState != 0)
            {
                Hide();
                return;
            }
            if (introActive)
            {
                FinishIntro();
            }
            introPending = false;
            outroActive = true;
            base.AllowTransparency = true;
            base.Opacity = 1.0;
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
        private void FinishOutro(bool hide)
        {
            outroActive = false;
            outroWatch.Reset();
            if (outroTimer != null)
            {
                outroTimer.Stop();
            }
            if (!base.IsDisposed)
            {
                if (hide)
                {
                    Hide();
                }
                base.Opacity = 1.0;
                DropLayeredStyle();
            }
        }
        private void CancelOutro()
        {
            if (outroActive)
            {
                FinishOutro(false);
            }
        }
        private void CancelAutoHide()
        {
            if (autoHideTimer != null)
            {
                autoHideTimer.Stop();
                autoHideTimer.Tick -= OnAutoHideTick;
                autoHideTimer.Dispose();
                autoHideTimer = null;
            }
        }
    }
}
