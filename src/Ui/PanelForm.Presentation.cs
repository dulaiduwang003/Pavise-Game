// @author bdth 2074055628@qq.com
// File purpose Guard veil, mode presentation, theme switch and DPI rebuild
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
        private void BuildGuardVeils()
        {
            AddGuardVeil(pagePolicy);
            AddGuardVeil(pageCoreScheduling);
            SyncGuardVeils();
        }

        private void AddGuardVeil(DBPanel page)
        {
            if (page == null) return;
            var veil = new GuardVeil(Lang.T("guard.veil.title"),
                Lang.T("guard.veil.hint"), Lang.T("guard.veil.go"));
            veil.Bounds = new Rectangle(0, 0, page.Width, page.Height);
            veil.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            veil.Go = delegate
            {
                gameMode.Enabled = true;
                Settings.Save("GameModeOn", true);
                if (swGame != null) swGame.SetSilently(true);
                UpdateModePresentation(true);
            };
            veil.Tag = page;
            page.Controls.Add(veil);
            guardVeils.Add(veil);
        }

        private void SyncGuardVeils()
        {
            bool off = gameMode != null && !gameMode.Enabled;
            for (int i = 0; i < guardVeils.Count; i++)
            {
                GuardVeil v = guardVeils[i];
                if (v == null || v.IsDisposed) continue;
                var page = v.Tag as Control;
                if (off)
                {
                    v.Wanted = true;
                    if (page != null && page.Width > 0)
                    {
                        if (!v.HasFrost && page.Visible)
                        {
                            v.Visible = false;
                            v.Frost(page);
                        }
                        v.Bounds = new Rectangle(0, 0, page.Width, page.Height);
                    }
                    v.Visible = true;
                    v.BringToFront();
                }
                else if (v.Wanted) { v.Wanted = false; v.Visible = false; v.Drop(); }
            }
        }

        private void UpdateModePresentation(bool animate)
        {
            PerformancePreset effective = gameMode.ActivePreset;
            bool enabled = gameMode.Enabled;
            bool active = gameMode.IsActive;
            string policySource = gameMode.SessionPolicySourceName;
            bool first = !modeVisualInitialized;
            bool visualChanged = first || effective != visualMode || enabled != visualEnabled;
            bool sessionChanged = first || active != visualActive
                || !string.Equals(policySource, visualPolicySource, StringComparison.Ordinal);

            if (visualChanged)
            {
                SyncGuardVeils();
                if (modeButton != null) modeButton.SetMode(effective);
                if (lblHeroMode != null) lblHeroMode.Text = ModeButton.ModeName(effective);
                if (policyBanner != null)
                    policyBanner.State = Lang.F("mode.policy.active", ModeButton.ModeName(effective));
            }
            if (sessionChanged)
            {
                if (modeButton != null) modeButton.SetSource(ModeSourceText(policySource, true));
                if (lblHeroSource != null) lblHeroSource.Text = ModeSourceText(policySource, false);
            }
            if (paviseCore != null) paviseCore.SetState(effective, enabled, active);
            if (effective != visualMode)
            {
                visualMode = effective;
                Theme.SetMode(effective, animate);
            }
            visualEnabled = enabled;
            visualActive = active;
            visualPolicySource = policySource;
            modeVisualInitialized = true;
            if (visualChanged)
            {
                RefreshModeAccentLabels();
                if (nav != null) nav.SetMode(effective, enabled);
                if (tuningNav != null) tuningNav.SetMode(effective, enabled);
                using (Icon icon = IconArt.MakeMultiIcon(effective, enabled)) SetRuntimeIcon(icon);
                RefreshPolicyPresentation();
            }
            if (pageGameConfig != null && pageGameConfig.Visible) SyncCfgRows();
        }

        internal static string ModeSourceText(string sourceName, bool compact)
        {
            return string.IsNullOrEmpty(sourceName) ? Lang.T("mode.source.global")
                : Lang.F(compact ? "mode.source.game.short" : "mode.source.game", sourceName);
        }

        private void OnThemeToggled(bool light)
        {
            StopPageReveal();
            Settings.Save("UiLight", light);
            Theme.SetLight(light);
            Logger.Log(Lang.T("log.panelform.8") + (light ? Lang.T("log.panelform.9") : Lang.T("log.panelform.10")));
            BeginInvoke((MethodInvoker)delegate { if (!IsDisposed) RebuildUi(); });
        }

        public void SetRuntimeIcon(Icon value)
        {
            if (value == null) return;
            if (InvokeRequired) { Icon copy = (Icon)value.Clone(); BeginInvoke((MethodInvoker)delegate { using (copy) SetRuntimeIcon(copy); }); return; }
            Icon next = (Icon)value.Clone();
            Icon old = appIcon;
            appIcon = next; Icon = next;
            if (old != null) old.Dispose();
        }

        private void RebuildUi()
        {
            DisposePageReveal();
            CancelOutro();
            if (uiTimer != null) { uiTimer.Stop(); uiTimer.Dispose(); uiTimer = null; }
            uiActive = false;
            uiActivityKnown = false;
            UiClock.Suspended = true;
            if (paviseCore != null) paviseCore.SetAnimationEnabled(false);
            DetachFormFrame();
            var old = new List<Control>();
            int keep = nav != null ? nav.Selected : 0;
            int keepReturn = mainReturnPage;
            if (pages != null) foreach (DBPanel page in pages) SavePagePosition(page);
            string keepCfg = pageGameConfig != null && !pageGameConfig.IsDisposed
                && pageGameConfig.Visible ? cfgProfileId : null;
            PageViewPosition keepCfgPosition = keepCfg != null
                ? PageViewPosition.Capture(pageGameConfig, navigationScale) : null;
            CoreSchedulingPanel.EditorState keepCoreDraft = coreSchedulingPanel != null && !coreSchedulingPanel.IsDisposed
                ? coreSchedulingPanel.CaptureEditorState() : null;
            CoreSchedulingPanel.EditorState keepCfgCoreDraft = keepCfg != null && cfgCoreSchedulingPanel != null
                && !cfgCoreSchedulingPanel.IsDisposed ? cfgCoreSchedulingPanel.CaptureEditorState() : null;
            foreach (Control c in Controls) old.Add(c);
            Controls.Clear();
            foreach (var c in old) c.Dispose();
            acGroups.Clear(); acCards.Clear(); acToggles.Clear();
            // Rebuild reuses the form instance; do not let disposed controls and delegates holding them pile up across theme and DPI switches
            guardVeils.Clear();
            accentLabels.Clear();
            themeRefreshers.Clear();
            policySync.Clear();
            graphicsSync.Clear();
            stackBase.Clear();
            cfgRowSync.Clear();
            cfgCardByKey.Clear();
            lblCfgCount = null; lblCfgSub = null; cfgBanner = null; cfgTabs = null;
            cfgTabPanels = null; cfgTabKeys = null;
            for (int i = 0; i < modeAccentLabels.Length; i++)
            {
                modeAccentLabels[i] = null;
                modeSwatches[i].Clear();
            }
            BuildUi(appIcon);
            coreSchedulingPanel.RestoreEditorState(keepCoreDraft);
            mainReturnPage = keepReturn;
            nav.Select(keep);
            if (keepCfg != null)
            {
                ShowGameConfigPage(keepCfg);
                if (cfgProfile != null && cfgCoreSchedulingPanel != null)
                {
                    cfgCoreSchedulingPanel.RestoreEditorState(keepCfgCoreDraft);
                    if (keepCfgPosition != null) keepCfgPosition.Restore(pageGameConfig, Dpi.Scale);
                }
            }
            if (UiActive) RefreshSlowStateAsync();
        }

        protected override void WndProc(ref Message m)
        {
            HandlePowerSchemeNotification(m);
            // ShowDialog disables the owner at the native level, which does not necessarily raise managed EnabledChanged
            if (m.Msg == 0x000A && m.WParam == IntPtr.Zero) StopPageReveal();
            if (m.Msg == Native.WM_DROPFILES)
            {
                AddDroppedGames(Native.ReadDroppedFiles(m.WParam));
                m.Result = IntPtr.Zero;
                return;
            }
            if (m.Msg == Native.WM_ENTERSIZEMOVE) BeginMoveSizeLoop();
            else if (m.Msg == Native.WM_EXITSIZEMOVE) EndMoveSizeLoop();
            base.WndProc(ref m);
        }

        private void ApplyPendingDpiRebuild()
        {
            if (IsDisposed) return;
            int dpi = Dpi.WindowDpi(Handle);
            if (dpi <= 0 || !Dpi.WouldChange(dpi)) return;
            Dpi.Update(dpi);
            Theme.DropFontCache();
            Logger.Log(Lang.T("log.panelform.11") + dpi);
            RebuildUi();
        }

        private PageHook CurrentPageHook()
        {
            if (pageHooks == null || curPage == null) return null;
            for (int i = 0; i < pageHooks.Length; i++)
                if (pageHooks[i] != null && pageHooks[i].Panel == curPage) return pageHooks[i];
            return null;
        }

        private void BeginMoveSizeLoop()
        {
            StopPageReveal();
            if (moveSizeLoop) return;
            moveSizeLoop = true;
            if (introActive)
            {
                introActive = false;
                introPending = false;
                DropLayeredStyle();
            }
            if (uiTimer != null) uiTimer.Stop();
            if (fitTimer != null) fitTimer.Stop();
        }

        private void EndMoveSizeLoop()
        {
            if (!moveSizeLoop) return;
            moveSizeLoop = false;
            if (IsDisposed || !UiActive) return;
            if (uiTimer != null) uiTimer.Start();
            OnUiTick(null, EventArgs.Empty);
            if (fitDeferredByDrag) { fitDeferredByDrag = false; ScheduleFit(); }
        }
    }
}
