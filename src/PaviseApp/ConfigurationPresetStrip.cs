using System;
using System.Drawing;
using System.Windows.Forms;

namespace PaviseApp
{
    internal sealed class ConfigurationPresetStrip : Control
    {
        // 和顶栏“访问官网”同一种按钮样式，并开启扫光提示，让入口更醒目。
        internal readonly RogLinkButton GuideButton;
        private readonly ToolTip tips = new ToolTip();
        internal event Action GuideRequested;

        internal ConfigurationPresetStrip()
        {
            Name = "ConfigurationPresetStrip";
            AccessibleRole = AccessibleRole.Grouping;
            AccessibleName = Lang.T("configpreset.guide");
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            GuideButton = new RogLinkButton(Lang.T("configpreset.guide"), "", "pulse") {
                Name = "OpenConfigurationPresetGuide", External = false, TabIndex = 0,
                AccessibleName = Lang.T("configpreset.guide"), AccessibleDescription = Lang.T("configpreset.guide.intro")
            };
            GuideButton.Attention = true;
            GuideButton.Click += delegate { if (Enabled && GuideButton.Enabled && GuideRequested != null) GuideRequested(); };
            Controls.Add(GuideButton);
        }

        internal int MinimumStripWidth
        {
            get {
                Font font = DsType.Font(DsText.Body, true);
                int widthLimit = Math.Max(TextRenderer.MeasureText(Lang.T("configpreset.guide"), font).Width,
                    TextRenderer.MeasureText(Lang.T("configpreset.working"), font).Width);
                // 左侧图标框约 43、右侧箭头约 36，再留一点余量。
                return Math.Max(Theme.S(132), widthLimit + Theme.S(43) + Theme.S(40));
            }
        }
        internal int PreferredStripWidth { get { return MinimumStripWidth; } }

        internal void SetState(ConfigurationPresetKind? current, bool busy, bool allowed, string message)
        {
            string currentText = Lang.T("configpreset.current") + (!current.HasValue ? Lang.T("configpreset.custom")
                : Lang.T(current.Value == ConfigurationPresetKind.Extreme ? "configpreset.extreme" : "configpreset.default"));
            GuideButton.Enabled = allowed && !busy;
            GuideButton.Busy = busy;
            GuideButton.Text = busy ? Lang.T("configpreset.working") : Lang.T("configpreset.guide");
            AccessibleDescription = currentText + "\n" + Lang.T("configpreset.guide.intro")
                + (String.IsNullOrEmpty(message) ? "" : "\n" + message);
            GuideButton.AccessibleDescription = AccessibleDescription;
            tips.SetToolTip(GuideButton, AccessibleDescription);
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            if (GuideButton != null) GuideButton.SetBounds(0, 0, Math.Max(1, Width), Math.Max(1, Height));
        }
        protected override void OnPaintBackground(PaintEventArgs e)
        { WorkspaceCanvasSurface.Paint(e.Graphics, this, e.ClipRectangle); }
        protected override void Dispose(bool disposing)
        { if (disposing) tips.Dispose(); base.Dispose(disposing); }
    }
}
