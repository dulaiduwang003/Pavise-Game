// @author bdth 2074055628@qq.com
// 文件用途 守护遮罩 模式呈现 主题切换与 DPI 重建
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
            SyncGuardVeils();
            PerformancePreset effective = gameMode.ActivePreset;
            bool enabled = gameMode.Enabled;
            bool visualChanged = !modeVisualInitialized || effective != visualMode || enabled != visualEnabled;
            if (modeButton != null) modeButton.SetMode(effective);
            if (lblHeroMode != null) lblHeroMode.Text = ModeButton.ModeName(effective);
            if (lblHeroSource != null)
            {
                string policySource = gameMode.SessionPolicySourceName;
                lblHeroSource.Text = policySource != null
                    ? Lang.F("mode.source.game", policySource) : Lang.T("mode.source.global");
            }
            if (policyBanner != null)
                policyBanner.State = Lang.F("mode.policy.active", ModeButton.ModeName(effective));
            if (paviseCore != null) paviseCore.SetState(effective, enabled, gameMode.IsActive);
            if (effective != visualMode)
            {
                visualMode = effective;
                Theme.SetMode(effective, animate);
            }
            visualEnabled = enabled;
            modeVisualInitialized = true;
            RefreshModeAccentLabels();
            RefreshExtremeCardAccent();
            if (nav != null) nav.SetMode(effective, enabled);
            if (tuningNav != null) tuningNav.SetMode(effective, enabled);
            if (visualChanged)
                using (Icon icon = IconArt.MakeMultiIcon(effective, enabled)) SetRuntimeIcon(icon);
            RefreshPolicyPresentation();
            if (pageGameConfig != null && pageGameConfig.Visible) SyncCfgRows();
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
            foreach (Control c in Controls) old.Add(c);
            Controls.Clear();
            foreach (var c in old) c.Dispose();
            acGroups.Clear(); acCards.Clear(); acToggles.Clear();
            BuildUi(appIcon);
            mainReturnPage = keepReturn;
            nav.Select(keep);
            if (keepCfg != null) ShowGameConfigPage(keepCfg);
            if (UiActive) RefreshSlowStateAsync();
        }

        protected override void WndProc(ref Message m)
        {
            // ShowDialog 会在原生层禁用 owner 不一定触发托管 EnabledChanged
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
