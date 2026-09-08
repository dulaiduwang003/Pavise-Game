using System;
using System.Drawing;
using System.Windows.Forms;

namespace PaviseApp
{
    internal sealed class ScalingCardPanel : GameCardExtension
    {
        private readonly string profileId;
        private readonly GameCardExtension inner;
        private readonly Toggle enabled, sharpen, mapped;
        private readonly Label title, status, sharpLabel, mouseLabel, hint;
        private readonly ToolTip tips = new ToolTip();
        private readonly Timer timer = new Timer();
        private Color surface = Theme.Card;
        private bool refreshing;
        // GameLibraryRow reserves nine pixels from PreferredHeight for spacing.
        private int ScaleHeight { get { return Theme.S(120); } }
        public override int PreferredHeight { get { return ScaleHeight + (inner == null ? 0 : inner.PreferredHeight); } }

        internal ScalingCardPanel(GameProfile profile, GameCardExtension previous)
        {
            profileId = profile.Id; inner = previous;
            Lang.Merge(ScalingLang.Table);
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
            BackColor = surface;
            AccessibleRole = AccessibleRole.Grouping; AccessibleName = profile.Name + " · " + Lang.T("scale.title");
            if (inner != null) Controls.Add(inner);
            title = Label(Lang.T("scale.title"), true); status = Label("", false);
            sharpLabel = Label(Lang.T("scale.sharp"), false); mouseLabel = Label(Lang.T("scale.mouse"), false);
            hint = Label(Lang.T("scale.hint"), false); hint.ForeColor = Theme.Dim;
            enabled = Switch("Enabled", "scale.title"); sharpen = Switch("Sharpen", "scale.sharp"); mapped = Switch("MappedMouse", "scale.mouse");
            tips.SetToolTip(enabled, Lang.T("scale.tip")); tips.SetToolTip(mapped, Lang.T("scale.mouse.tip"));
            tips.SetToolTip(mouseLabel, Lang.T("scale.mouse.tip")); tips.SetToolTip(title, Lang.T("scale.tip"));
            timer.Interval = 600; timer.Tick += delegate { if (Visible) RefreshState(); };
            RefreshState();
        }
        private Label Label(string text, bool bold)
        {
            var label = new Label { Text = text, Font = Theme.UI(bold ? 9f : 8f, bold), ForeColor = Theme.Fg,
                BackColor = Color.Transparent, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
            Controls.Add(label); return label;
        }
        private Toggle Switch(string option, string languageKey)
        {
            var toggle = new Toggle { Bg = surface, AccessibleName = Lang.T(languageKey) };
            toggle.CheckedChanged += delegate {
                if (refreshing) return;
                if (!ScalingSettings.Set(profileId, option, toggle.Checked)) tips.Show(Lang.T("scale.savefailed"), toggle, 3000);
                RefreshState();
            };
            Controls.Add(toggle); return toggle;
        }
        private void RefreshState()
        {
            if (enabled == null || sharpen == null || mapped == null || status == null) return;
            refreshing = true;
            try
            {
                bool on = ScalingSettings.Enabled(profileId);
                enabled.SetSilently(on); sharpen.SetSilently(ScalingSettings.Sharpen(profileId)); mapped.SetSilently(ScalingSettings.MappedMouse(profileId));
                enabled.Enabled = ScalingPayload.Available || on;
                ScalingSnapshot snapshot = ScalingService.Snapshot(profileId);
                string key = !ScalingPayload.Available ? "unavailable" : !on ? "off" : snapshot.State;
                status.Text = Lang.T("scale." + key);
                if (key == "running" && ScalingService.ParseState(snapshot.Detail) == "running")
                {
                    string[] dimensions = snapshot.Detail.Split(' ');
                    status.Text += " · " + dimensions[1] + "×" + dimensions[2] + " → " + dimensions[3] + "×" + dimensions[4];
                }
                status.ForeColor = key == "running" ? Theme.Accent : Theme.Dim;
                tips.SetToolTip(status, snapshot.Detail);
            }
            finally { refreshing = false; }
        }
        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (timer == null) return;
            if (Visible) { timer.Start(); RefreshState(); } else timer.Stop();
        }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); if (Visible) timer.Start(); }
        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (enabled == null || mapped == null || hint == null) return;
            int y = inner == null ? 0 : inner.PreferredHeight;
            if (inner != null) inner.SetBounds(0, 0, Width, Math.Max(1, y - Theme.S(8)));
            int inset = Theme.S(4), toggleW = Theme.S(46), textW = Math.Max(50, Width - toggleW - Theme.S(16));
            title.SetBounds(inset, y + Theme.S(3), Math.Min(Theme.S(144), textW), Theme.S(23));
            enabled.SetBounds(Width - toggleW - inset, y + Theme.S(1), toggleW, Theme.S(28));
            status.SetBounds(inset, y + Theme.S(27), textW, Theme.S(20));
            sharpen.SetBounds(inset, y + Theme.S(49), toggleW, Theme.S(26));
            sharpLabel.SetBounds(inset + toggleW + Theme.S(6), y + Theme.S(50), Theme.S(62), Theme.S(23));
            mapped.SetBounds(Theme.S(130), y + Theme.S(49), toggleW, Theme.S(26));
            mouseLabel.SetBounds(Theme.S(182), y + Theme.S(50), Math.Max(1, Width - Theme.S(186)), Theme.S(23));
            hint.SetBounds(inset, y + Theme.S(79), Math.Max(1, Width - inset * 2), Theme.S(26));
        }
        public override void SetSurface(Color color)
        {
            surface = color; BackColor = color;
            if (inner != null) inner.SetSurface(color);
            foreach (Control control in Controls) { FxControl fx = control as FxControl; if (fx != null) { fx.Bg = color; fx.Invalidate(); } }
            Invalidate();
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { timer.Stop(); timer.Dispose(); tips.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
