// 作者：bdth 2074055628@qq.com
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
    // 开关：可经 Tab 聚焦，Space/Enter 切换；禁用或忙碌时不响应用户激活，程序赋值仍照常触发事件。
    internal class Toggle : FxControl
    {
        private bool isOn;
        private Motion colorBlend;
        public event EventHandler CheckedChanged;

        public Toggle()
        {
            Size = new Size(Theme.S(DsMetrics.Toggle.Width), Theme.S(DsMetrics.Toggle.Height));
            ForeColor = Theme.Fg;
            Font = DsType.Font(DsText.Title);
            colorBlend.Speed = 0.32f;
            TabStop = true;
            AccessibleRole = AccessibleRole.CheckButton;
        }

        public bool Checked
        {
            get { return isOn; }
            set
            {
                if (isOn == value) return;
                isOn = value; colorBlend.To(value ? 1f : 0f);
                UiClock.Wake(); Invalidate();
                NotifyStateChange();
                if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty);
            }
        }

        public void SetSilently(bool v)
        {
            float target = v ? 1f : 0f;
            if (isOn == v && colorBlend.Value == target && colorBlend.Target == target) return;
            isOn = v;
            colorBlend.Set(target);
            Invalidate();
            NotifyStateChange();
        }

        // 用户激活入口：鼠标、键盘与辅助功能默认动作都经过这里。
        public void PerformClick()
        {
            if (CanInteract) OnClick(EventArgs.Empty);
        }

        private void NotifyStateChange()
        {
            if (IsHandleCreated) AccessibilityNotifyClients(AccessibleEvents.StateChange, -1);
        }

        protected override bool StepAll() { bool a = base.StepAll(); bool b = colorBlend.Step(); return a || b; }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            if (!CanInteract) return;
            Checked = !isOn;
        }

        protected override bool IsInputKey(Keys keyData)
        {
            if (DsKeys.IsActivation(keyData)) return true;
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (!DsKeys.IsPlainActivation(e) || !CanInteract) return;
            e.Handled = true; e.SuppressKeyPress = true;
            PerformClick();
        }

        protected override AccessibleObject CreateAccessibilityInstance()
        {
            return new ToggleAccessibleObject(this);
        }

        private sealed class ToggleAccessibleObject : FxAccessibleObject
        {
            private readonly Toggle owner;
            internal ToggleAccessibleObject(Toggle toggle) : base(toggle) { owner = toggle; }
            public override AccessibleStates State
            {
                get
                {
                    AccessibleStates state = base.State;
                    if (owner.isOn) state |= AccessibleStates.Checked;
                    return state;
                }
            }
            public override string DefaultAction { get { return DsLabels.Pick("切换", "Toggle"); } }
            public override void DoDefaultAction() { owner.PerformClick(); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            FillBg(g);
            if (Width < 2 || Height < 2) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            int th = Theme.S(DsMetrics.Toggle.TrackHeight), pad = Theme.S(DsMetrics.Toggle.TrackPad);
            if (th > Height) th = Height;
            int tw = string.IsNullOrEmpty(Text)
                ? Math.Max(Theme.S(DsMetrics.Toggle.MinTrack), Width - 1)
                : Theme.S(DsMetrics.Toggle.TrackWithText);
            int kd = th - pad * 2;
            var track = new Rectangle(0, (Height - th) / 2, tw, th);
            float p = colorBlend.Value;

            using (var path = Theme.Rounded(track, th / 2))
            {
                using (var b = new SolidBrush(Theme.TrackOff)) g.FillPath(b, path);
                if (p > 0.01f)
                {
                    using (var lg = new LinearGradientBrush(DsPaint.GradientRect(track),
                        Col.Alpha(Theme.Accent, (int)(255 * p)), Col.Alpha(Theme.Accent2, (int)(255 * p)),
                        LinearGradientMode.Horizontal))
                        g.FillPath(lg, path);
                }
                else
                {
                    using (var pen = new Pen(Theme.Stroke)) g.DrawPath(pen, path);
                }
                if (hover.Value > 0.01f)
                    using (var hl = new SolidBrush(Col.Alpha(Color.White, (int)(DsAlpha.HoverLight * hover.Value))))
                        g.FillPath(hl, path);
            }

            if (kd > 0)
            {
                int kx = isOn ? track.Right - pad - kd : track.X + pad;
                int ky = track.Y + pad;
                using (var sh = new SolidBrush(Col.Alpha(Color.Black, DsAlpha.KnobShadow)))
                    g.FillEllipse(sh, kx, ky + Theme.S(1), kd, kd);
                using (var kb = new SolidBrush(isOn ? DsPalette.KnobOn : DsPalette.KnobOff))
                    g.FillEllipse(kb, kx, ky, kd, kd);
            }

            if (!string.IsNullOrEmpty(Text))
            {
                int tx = tw + Theme.S(DsMetrics.Toggle.TextGap);
                TextRenderer.DrawText(g, Text, Font, new Rectangle(tx, 0, Width - tx, Height), ForeColor,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
            PaintBusy(g, new Rectangle(0, 0, tw, Height), null);
            if (!Enabled) DsPaint.Veil(g, ClientRectangle, Col.Alpha(Theme.Card, DsAlpha.VeilToggle));
            if (WantsFocusCue)
            {
                int inset = Theme.S(DsMetrics.Toggle.FocusInset);
                DsFocus.Draw(g, Rectangle.Intersect(Rectangle.Inflate(track, inset, inset), ClientRectangle),
                    Theme.Fg, EffBg);
            }
        }
    }

}
