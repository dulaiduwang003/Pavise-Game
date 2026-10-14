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
            PerformancePreset? preview = previewMode == "extreme" ? PerformancePreset.Extreme
                : previewMode == "competitive" ? PerformancePreset.Competitive
                : previewMode == "handheld" ? PerformancePreset.Handheld
                : previewMode == "standard" ? PerformancePreset.Standard : (PerformancePreset?)null;
            if (preview.HasValue)
            {
                Theme.SetMode(preview.Value, false);
                modeButton.SetMode(preview.Value); nav.SetMode(preview.Value, true);
                tuningNav.SetMode(preview.Value, true);
                if (lblHeroMode != null) { lblHeroMode.Text = ModeButton.ModeName(preview.Value); lblHeroMode.ForeColor = Theme.Accent; }
                if (paviseCore != null) paviseCore.SetState(preview.Value, true, false);
            }
            if (showModePicker && modeButton != null) modeButton.PerformClick();
            if (previewMode == "power" && powerFlyout != null) SetPowerFlyout(true);
            if (previewMode == "advanced")
            {
                nav.Select(lastAdvancedPage);
                tuningNav.SnapToSelection();
                if (curPage != null) curPage.Left = pageBaseLeft;
            }
            if (previewMode == "audit" && pageIndex == (int)PageId.Audit)
            {
                try { RenderAudit(SystemAudit.Collect(400)); } catch { }
                if (lblAuditStatus != null) lblAuditStatus.Text = "";
            }
            if (previewMode == "settings-appearance" && pageIndex == (int)PageId.Settings)
            {
                if (settingsTabs != null) settingsTabs.Index = 2;
            }
            if (previewMode == "settings-maint" && pageIndex == (int)PageId.Settings)
            {
                if (settingsTabs != null) settingsTabs.Index = 1;
            }
            Application.DoEvents();
            StopPageReveal();
            if (lstGames != null)
            {
                bool masked = false;
                foreach (GameLibraryItem shotRow in lstGames.Items)
                    if (shotRow.Profile != null && shotRow.Profile.ExecutablePath != null
                        && shotRow.Profile.ExecutablePath.IndexOf("PaviseShot_", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        const string fake = @"D:\Games\NebulaStrike\Binaries\Win64\NebulaStrike-Win64-Shipping.exe";
                        gameIconCache[fake] = GameIcon(shotRow.Profile.ExecutablePath);
                        shotRow.Profile.ExecutablePath = fake;
                        masked = true;
                    }
                if (masked) { lstGames.Invalidate(true); Application.DoEvents(); }
            }
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
