using System;
using System.Drawing;
using System.Windows.Forms;

namespace PaviseApp
{
    internal sealed class ConfigurationPresetStrip : Control
    {
        internal readonly PerformanceActionButton GuideButton;
        private readonly ToolTip tips = new ToolTip();
        internal event Action GuideRequested;

        internal ConfigurationPresetStrip()
        {
            Name = "ConfigurationPresetStrip";
            AccessibleRole = AccessibleRole.Grouping;
            AccessibleName = Lang.T("configpreset.guide");
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            GuideButton = new PerformanceActionButton(Lang.T("configpreset.guide")) {
                Name = "OpenConfigurationPresetGuide", Kind = BtnKind.Primary, TabIndex = 0,
                AccessibleName = Lang.T("configpreset.guide"), AccessibleDescription = Lang.T("configpreset.guide.intro")
            };
            GuideButton.Click += delegate { if (Enabled && GuideButton.Enabled && GuideRequested != null) GuideRequested(); };
            Controls.Add(GuideButton);
        }

        internal int MinimumStripWidth
        {
            get {
                int widthLimit = Math.Max(TextRenderer.MeasureText(Lang.T("configpreset.guide"), Theme.UI(9.5f, true)).Width,
                    TextRenderer.MeasureText(Lang.T("configpreset.working"), Theme.UI(9.5f, true)).Width);
                return Math.Max(Theme.S(108), widthLimit + Theme.S(32));
            }
        }
        internal int PreferredStripWidth { get { return MinimumStripWidth; } }

        internal void SetState(ConfigurationPresetKind? current, bool busy, bool allowed, string message)
        {
            string currentText = Lang.T("configpreset.current") + (!current.HasValue ? Lang.T("configpreset.custom")
                : Lang.T(current.Value == ConfigurationPresetKind.Extreme ? "configpreset.extreme" : "configpreset.default"));
            GuideButton.Enabled = allowed && !busy;
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
