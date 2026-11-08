using System;
using System.Drawing;
using System.Windows.Forms;

namespace PaviseApp
{
    internal partial class PanelForm
    {
        private SecurityCompatibilityWarningIcon overviewSecurityWarning;
        private SecurityProductSnapshot overviewSecuritySnapshot;
        private bool securityCompatibilityOpen;
        private bool overviewSecurityWarningShown;

        private void BuildOverviewSecurityWarning()
        {
            overviewSecuritySnapshot = SecurityProductCatalog.StartupSnapshot;
            overviewSecurityWarningShown = !Settings.Load(SecurityCompatibilityPresentation.HiddenSettingKey, false)
                && SecurityCompatibilityPresentation.NeedsWarning(overviewSecuritySnapshot);
            overviewSecurityWarning = new SecurityCompatibilityWarningIcon { Visible = overviewSecurityWarningShown, TabIndex = 1 };
            overviewSecurityWarning.SetDescription(SecurityCompatibilityPresentation.Title(overviewSecuritySnapshot),
                SecurityCompatibilityPresentation.Summary(overviewSecuritySnapshot));
            overviewSecurityWarning.Click += delegate { ShowSecurityCompatibility(); };
            pageOverview.Controls.Add(overviewSecurityWarning);
        }

        private void LayoutOverviewSecurityWarning(int pageWidth, int statusY)
        {
            if (overviewSecurityWarning == null) return;
            SetOverviewBounds(overviewSecurityWarning, Math.Max(0, (pageWidth - 36) / 2), Math.Max(0, statusY - 44), 36, 36);
            overviewSecurityWarning.BringToFront();
        }

        private void ShowSecurityCompatibility()
        {
            if (securityCompatibilityOpen || IsDisposed || Disposing || !overviewSecurityWarningShown) return;
            securityCompatibilityOpen = true;
            try
            {
                PaviseDialog.ShowWithSecondaryAction(this, SecurityCompatibilityPresentation.Title(overviewSecuritySnapshot),
                    SecurityCompatibilityPresentation.Details(overviewSecuritySnapshot),
                    SecurityCompatibilityPresentation.T("不再显示", "Don't show again", "今後表示しない"),
                    SecurityCompatibilityPresentation.T("保存失败", "Save failed", "保存失敗"),
                    HideOverviewSecurityWarning, 600);
            }
            finally { securityCompatibilityOpen = false; }
        }

        private bool HideOverviewSecurityWarning()
        {
            if (!Settings.SaveDurable(SecurityCompatibilityPresentation.HiddenSettingKey, true)) return false;
            overviewSecurityWarningShown = false;
            if (overviewSecurityWarning != null) overviewSecurityWarning.Visible = false;
            overviewViewport = Size.Empty; LayoutOverviewPage();
            return true;
        }
    }
}
