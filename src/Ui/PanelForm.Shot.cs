// @author bdth 2074055628@qq.com
// 文件用途 截图渲染与关闭拦截
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
        public void RenderTo(string path, int pageIndex, bool showAntiCheat = false, bool showModePicker = false, string previewMode = null)
        {
            shotMode = true;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-20000, -20000);
            Show();
            if (pageIndex >= 0 && pageIndex < pages.Length)
            {
                nav.Select(pageIndex);
                nav.SnapToSelection();
                tuningNav.SnapToSelection();
                if (curPage != null) curPage.Left = pageBaseLeft;
            }
            OnUiTick(null, EventArgs.Empty);
            if (showAntiCheat) { nav.Select((int)PageId.AntiCheat); nav.SnapToSelection(); if (curPage != null) curPage.Left = pageBaseLeft; }
            PerformancePreset? preview = previewMode == "competitive" ? PerformancePreset.Competitive
                : previewMode == "extreme" ? PerformancePreset.Extreme
                : previewMode == "handheld" ? PerformancePreset.Handheld
                : previewMode == "custom" ? PerformancePreset.Custom
                : previewMode == "standard" ? PerformancePreset.Standard : (PerformancePreset?)null;
            if (preview.HasValue)
            {
                Theme.SetMode(preview.Value, false);
                modeButton.SetMode(preview.Value); nav.SetMode(preview.Value, true);
                tuningNav.SetMode(preview.Value, true);
                if (lblHeroMode != null) { lblHeroMode.Text = ModeButton.ModeName(preview.Value); lblHeroMode.ForeColor = Theme.Accent; }
                if (paviseCore != null) paviseCore.SetState(preview.Value, true, false);
            }
            if (previewMode == "gameconfig" || previewMode == "gameconfig-core"
                || previewMode == "gameconfig-env" || previewMode == "gameconfig-gpu")
            {
                List<GameProfile> shotProfiles = gameMode.GetProfiles();
                if (shotProfiles.Count > 0)
                {
                    ShowGameConfigPage(shotProfiles[0].Id);
                    if (curPage != null) curPage.Left = pageBaseLeft;
                    if (previewMode == "gameconfig-core" && cfgTabs != null) cfgTabs.Index = 1;
                    if (previewMode == "gameconfig-env" && cfgTabs != null) cfgTabs.Index = 2;
                    if (previewMode == "gameconfig-gpu" && cfgTabs != null) cfgTabs.Index = 3;
                    if (cfgTabs != null) cfgTabs.SnapToSelection();
                }
            }
            if (showModePicker && modeButton != null) modeButton.PerformClick();
            if (previewMode == "power" && powerFlyout != null) SetPowerFlyout(true);
            if (previewMode == "search" && searchFlyout != null) SetSearchFlyout(true);
            if (previewMode == "advanced")
            {
                nav.Select(lastAdvancedPage);
                tuningNav.SnapToSelection();
                if (curPage != null) curPage.Left = pageBaseLeft;
            }
            if (previewMode == "search-hit" && searchFlyout != null)
            {
                SetSearchFlyout(true);
                searchFlyout.SetQuery(Lang.T("core.tag.bg"));
            }
            if (previewMode == "audit" && pageIndex == (int)PageId.Audit)
            {
                try { RenderAudit(SystemAudit.Collect(400)); } catch { }
                if (lblAuditStatus != null) lblAuditStatus.Text = "";
            }
            if (previewMode == "settings-appearance" && pageIndex == (int)PageId.Settings)
            {
                foreach (Control child in pageSettings.Controls)
                {
                    ScrollableControl settingsScroll = child as ScrollableControl;
                    if (settingsScroll != null && settingsScroll.AutoScroll)
                    {
                        settingsScroll.AutoScrollPosition = new Point(0, Theme.S(470));
                        break;
                    }
                }
            }
            Application.DoEvents();
            StopPageReveal();
            using (var bmp = new Bitmap(ClientSize.Width, ClientSize.Height))
            {
                DrawToBitmap(bmp, new Rectangle(0, 0, ClientSize.Width, ClientSize.Height));
                if (showModePicker && modeFlyout != null && modeFlyout.Visible)
                    using (var overlay = new Bitmap(modeFlyout.Width, modeFlyout.Height))
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        modeFlyout.DrawToBitmap(overlay, new Rectangle(0, 0, overlay.Width, overlay.Height));
                        g.DrawImageUnscaled(overlay, modeFlyout.Left, modeFlyout.Top);
                    }
                if (searchFlyout != null && searchFlyout.Visible)
                    using (var overlay = new Bitmap(searchFlyout.Width, searchFlyout.Height))
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        searchFlyout.DrawToBitmap(overlay, new Rectangle(0, 0, overlay.Width, overlay.Height));
                        g.DrawImageUnscaled(overlay, searchFlyout.Left, searchFlyout.Top);
                    }
                if (powerFlyout != null && powerFlyout.Visible)
                    using (var overlay = new Bitmap(powerFlyout.Width, powerFlyout.Height))
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        powerFlyout.DrawToBitmap(overlay, new Rectangle(0, 0, overlay.Width, overlay.Height));
                        g.DrawImageUnscaled(overlay, powerFlyout.Left, powerFlyout.Top);
                    }
                bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
            Hide();
            shotMode = false;
        }

        protected override bool ShowWithoutActivation { get { return shotMode; } }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!RealExit && e.CloseReason != CloseReason.WindowsShutDown && e.CloseReason != CloseReason.ApplicationExitCall)
            {
                e.Cancel = true;
                BeginOutro();
                return;
            }
            base.OnFormClosing(e);
        }
    }
}
