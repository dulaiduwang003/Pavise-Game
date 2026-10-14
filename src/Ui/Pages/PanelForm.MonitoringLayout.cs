using System;
using System.Drawing;
using System.Windows.Forms;

namespace PaviseApp
{
    internal partial class PanelForm
    {
        private ToolTip monitoringTips;
        private Control monitoringComponentPanel, monitoringComponentDetails;
        private PillButton btnMonitoringComponentDetails;
        private bool monitoringComponentExpanded;
        private Label lblMonitoringSelectedNames;

        private void BuildMonitoringComponentCard(Control parent)
        {
            var component = MakeConsolePanel(parent, 6, 0, ScrollContentW, 58, false);
            monitoringComponentPanel = component;
            component.Name = "monitoringComponentCard";
            CardLabel(component, MonitoringText("显示组件", "Overlay component"), 18, 13, 140, 22, 9f, true, Theme.Fg);
            lblMonitoringComponent = CardLabel(component, "", 170, 14, ScrollContentW - 560, 22, 8.2f, false, Theme.Dim);
            lblMonitoringComponent.Name = "monitoringComponentState"; lblMonitoringComponent.AutoEllipsis = true;
            btnMonitoringComponentDetails = new PillButton("") { Name = "monitoringComponentDetailsToggle" };
            btnMonitoringComponentDetails.SetBounds(Theme.S(ScrollContentW - 176), Theme.S(14), Theme.S(158), Theme.S(30));
            btnMonitoringComponentDetails.Click += delegate {
                if (runtimeShutdownBusy || MonitoringComponentNeedsAttention) return;
                monitoringComponentExpanded = !monitoringComponentExpanded;
                SyncMonitoringComponentPresentation(); ReflowMonitoringSettings();
            };
            component.Controls.Add(btnMonitoringComponentDetails);
            btnMonitoringInstall = new PillButton(MonitoringText("下载并安装", "Download and install"), BtnKind.Primary) { Name = "monitoringInstall" };
            btnMonitoringInstall.SetBounds(Theme.S(ScrollContentW - 380), Theme.S(14), Theme.S(190), Theme.S(30));
            btnMonitoringInstall.Click += delegate { BeginMonitoringInstall(); };
            component.Controls.Add(btnMonitoringInstall);
            var body = new Panel { Name = "monitoringComponentDetails", BackColor = Color.Transparent };
            body.SetBounds(Theme.S(18), Theme.S(64), Theme.S(ScrollContentW - 36), Theme.S(110));
            component.Controls.Add(body); monitoringComponentDetails = body;
            btnMonitoringDetect = new PillButton(MonitoringText("重新检测", "Detect again")) { Name = "monitoringDetect" };
            btnMonitoringDetect.SetBounds(0, 0, Theme.S(142), Theme.S(30));
            btnMonitoringDetect.Click += delegate { BeginMonitoringDetection(); }; body.Controls.Add(btnMonitoringDetect);
            btnMonitoringUninstall = new PillButton(MonitoringText("卸载 RTSS", "Uninstall RTSS")) { Name = "monitoringUninstall" };
            btnMonitoringUninstall.SetBounds(Theme.S(154), 0, Theme.S(170), Theme.S(30));
            btnMonitoringUninstall.AccessibleDescription = MonitoringText("停止监测并运行 RTSS 官方卸载程序。", "Stop monitoring and run the official RTSS uninstaller.");
            btnMonitoringUninstall.Click += delegate { BeginMonitoringUninstall(); }; body.Controls.Add(btnMonitoringUninstall);
            lblMonitoringProgress = CardLabel(body, "", 0, 39, ScrollContentW - 36, 24, 8.5f, false, Theme.Dim);
            lblMonitoringProgress.Name = "monitoringInstallProgress"; lblMonitoringProgress.AutoEllipsis = true;
            CardLabel(body, MonitoringText("游戏内显示需要 RTSS，已有兼容版本可直接使用。下载约 44 MB，备用源约 18 MB。",
                "In-game display requires RTSS. Compatible installations can be reused. Download: about 44 MB; fallback: 18 MB."),
                0, 72, ScrollContentW - 36, 34, 8.2f, false, Theme.Faint);
        }

        private bool MonitoringComponentNeedsAttention
        {
            get { return monitoringComponent == null || !monitoringComponent.Installed || !monitoringComponent.SupportsOverlay
                || !monitoringComponent.SupportsFontOverride
                || MonitoringComponentInstallPending || monitoringInstalling || monitoringUninstalling || monitoringProgressError; }
        }

        private void SyncMonitoringComponentPresentation()
        {
            if (monitoringComponentPanel == null || monitoringComponentPanel.IsDisposed) return;
            bool required = MonitoringComponentNeedsAttention;
            bool expanded = required || monitoringComponentExpanded;
            monitoringComponentDetails.Visible = expanded;
            monitoringComponentPanel.Height = Theme.S(required ? 208 : expanded ? 186 : 50);
            monitoringComponentDetails.Top = Theme.S(required ? 88 : 64);
            lblMonitoringComponent.SetBounds(Theme.S(required ? 18 : 170), Theme.S(required ? 42 : 14),
                Theme.S(required ? ScrollContentW - 36 : ScrollContentW - 560), Theme.S(required ? 34 : 22));
            lblMonitoringComponent.AutoEllipsis = !required;
            btnMonitoringComponentDetails.Text = expanded ? MonitoringText("收起管理 ∧", "Hide controls ∧") : MonitoringText("组件管理 ∨", "Manage RTSS ∨");
            btnMonitoringComponentDetails.Enabled = !required && !runtimeShutdownBusy;
            btnMonitoringComponentDetails.AccessibleName = btnMonitoringComponentDetails.Text;
            btnMonitoringInstall.Visible = monitoringComponent == null || !monitoringComponent.Installed
                || monitoringInstalling || monitoringComponent.InstallationInProgress;
        }

        private void ReflowMonitoringSettings()
        {
            if (monitoringSettingsPanel == null || cardMonitoring == null || monitoringAppearancePanel == null
                || monitoringMetricsPanel == null || lblMonitoringMessage == null) return;
            var scroll = monitoringSettingsPanel as ScrollableControl;
            int offset = scroll == null ? 0 : scroll.AutoScrollPosition.Y;
            monitoringSettingsPanel.SuspendLayout();
            try
            {
                int top = Theme.S(2);
                cardMonitoring.Top = offset + top; top += cardMonitoring.Height + Theme.S(8);
                if (MonitoringComponentNeedsAttention)
                { monitoringComponentPanel.Top = offset + top; top += monitoringComponentPanel.Height + Theme.S(8); }
                if (monitoringHotkeyPanel != null)
                { monitoringHotkeyPanel.Top = offset + top; top += monitoringHotkeyPanel.Height + Theme.S(8); }
                monitoringAppearancePanel.Top = offset + top; top += monitoringAppearancePanel.Height + Theme.S(8);
                monitoringMetricsPanel.Top = offset + top; top += monitoringMetricsPanel.Height + Theme.S(8);
                if (!MonitoringComponentNeedsAttention)
                { monitoringComponentPanel.Top = offset + top; top += monitoringComponentPanel.Height + Theme.S(8); }
                lblMonitoringMessage.Visible = !String.IsNullOrEmpty(monitoringMessage);
                lblMonitoringMessage.Top = offset + top;
                if (lblMonitoringMessage.Visible) top += lblMonitoringMessage.Height + Theme.S(8);
                if (scroll != null) scroll.AutoScrollMinSize = new Size(0, top + Theme.S(2));
            }
            finally { monitoringSettingsPanel.ResumeLayout(true); }
        }
    }
}
