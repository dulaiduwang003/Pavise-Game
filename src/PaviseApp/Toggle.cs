using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
    internal class Toggle : FxControl
    {
        private sealed class ToggleAccessibleObject : FxAccessibleObject
        {
            private readonly Toggle owner;

            public override AccessibleStates State
            {
                get
                {
                    AccessibleStates accessibleStates = base.State;
                    if (owner.isOn)
                    {
                        accessibleStates |= AccessibleStates.Checked;
                    }
                    return accessibleStates;
                }
            }

            public override string DefaultAction
            {
                get
                {
                    return DsLabels.Pick("切换", "Toggle");
                }
            }

            internal ToggleAccessibleObject(Toggle toggle)
                : base(toggle)
            {
                owner = toggle;
            }

            public override void DoDefaultAction()
            {
                owner.PerformClick();
            }
        }

        private bool isOn;

        private Motion colorBlend;

        internal bool PerformanceConsole;

        internal bool OverviewGuard;

        private bool compact;

        internal bool Compact
        {
            get { return compact; }
            set
            {
                if (compact == value) return;
                compact = value;
                Invalidate();
            }
        }

        public bool Checked
        {
            get
            {
                return isOn;
            }
            set
            {
                if (isOn != value)
                {
                    isOn = value;
                    colorBlend.To(value ? 1f : 0f);
                    UiClock.Wake();
                    Invalidate();
                    NotifyStateChange();
                    if (this.CheckedChanged != null)
                    {
                        this.CheckedChanged(this, EventArgs.Empty);
                    }
                }
            }
        }

        public event EventHandler CheckedChanged;

        public Toggle()
        {
            base.Size = new Size(Theme.S(46), Theme.S(30));
            ForeColor = Theme.Fg;
            Font = DsType.Font(DsText.Title);
            colorBlend.Speed = 0.32f;
            base.TabStop = true;
            base.AccessibleRole = AccessibleRole.CheckButton;
        }

        public override Size GetPreferredSize(Size proposedSize)
        {
            return Compact ? new Size(Theme.S(44), Theme.S(24)) : base.GetPreferredSize(proposedSize);
        }

        public void SetSilently(bool v)
        {
            float measuredValue = (v ? 1f : 0f);
            if (isOn != v || colorBlend.Value != measuredValue || colorBlend.Target != measuredValue)
            {
                isOn = v;
                colorBlend.Set(measuredValue);
                Invalidate();
                NotifyStateChange();
            }
        }

        public void PerformClick()
        {
            if (base.CanInteract)
            {
                OnClick(EventArgs.Empty);
            }
        }

        private void NotifyStateChange()
        {
            if (base.IsHandleCreated)
            {
                AccessibilityNotifyClients(AccessibleEvents.StateChange, -1);
            }
        }

        protected override bool StepAll()
        {
            bool stepAllSucceeded = base.StepAll();
            bool result = colorBlend.Step();
            if (!stepAllSucceeded)
            {
                return result;
            }
            return true;
        }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            if (base.CanInteract)
            {
                Checked = !isOn;
            }
        }

        protected override bool IsInputKey(Keys keyData)
        {
            if (DsKeys.IsActivation(keyData))
            {
                return true;
            }
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (DsKeys.IsPlainActivation(e) && base.CanInteract)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                PerformClick();
            }
        }

        protected override AccessibleObject CreateAccessibilityInstance()
        {
            return new ToggleAccessibleObject(this);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (OverviewGuard)
            {
                FillBg(e.Graphics);
                OverviewGuardSwitch.Paint(e.Graphics, this, colorBlend.Value, hover.Value);
                using (GraphicsPath clip = Theme.TechPath(OverviewGuardSwitch.FrameBounds(this), Theme.S(6)))
                {
                    PaintBusy(e.Graphics, base.ClientRectangle, clip);
                }
                if (!base.Enabled)
                {
                    DsPaint.Veil(e.Graphics, base.ClientRectangle, Col.Alpha(Theme.Card, 105));
                }
                if (base.WantsFocusCue)
                {
                    DsFocus.Draw(e.Graphics, Rectangle.Inflate(base.ClientRectangle, -Theme.S(3), -Theme.S(3)), Theme.Fg, base.EffBg);
                }
                return;
            }
            if (Compact)
            {
                PaintCompact(e.Graphics);
                return;
            }
            if (PerformanceConsole)
            {
                PerformanceConsoleToggle.Paint(e.Graphics, this, colorBlend.Value, hover.Value);
                PaintBusy(e.Graphics, base.ClientRectangle, null);
                if (base.WantsFocusCue)
                {
                    DsFocus.Draw(e.Graphics, Rectangle.Inflate(base.ClientRectangle, -Theme.S(3), -Theme.S(3)), Theme.Fg, base.EffBg);
                }
                return;
            }
            Graphics graphics = e.Graphics;
            FillBg(graphics);
            if (base.Width < 2 || base.Height < 2)
            {
                return;
            }
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            int scaledDimension = Theme.S(22);
            int scaledDimension2 = Theme.S(3);
            if (scaledDimension > base.Height)
            {
                scaledDimension = base.Height;
            }
            int sLimit = (string.IsNullOrEmpty(Text) ? Math.Max(Theme.S(20), base.Width - 1) : Theme.S(44));
            int width = scaledDimension - scaledDimension2 * 2;
            Rectangle rectangle = new Rectangle(0, (base.Height - scaledDimension) / 2, sLimit, scaledDimension);
            float value = colorBlend.Value;
            using (GraphicsPath path = Theme.Rounded(rectangle, scaledDimension / 2))
            {
                using (SolidBrush brush = new SolidBrush(Theme.TrackOff))
                {
                    graphics.FillPath(brush, path);
                }
                if (value > 0.01f)
                {
                    using (LinearGradientBrush brush2 = new LinearGradientBrush(DsPaint.GradientRect(rectangle), Col.Alpha(Theme.Accent, (int)(255f * value)), Col.Alpha(Theme.Accent2, (int)(255f * value)), LinearGradientMode.Horizontal))
                    {
                        graphics.FillPath(brush2, path);
                    }
                }
                else
                {
                    using (Pen pen = new Pen(Theme.Stroke))
                    {
                        graphics.DrawPath(pen, path);
                    }
                }
                if (hover.Value > 0.01f)
                {
                    using (SolidBrush brush3 = new SolidBrush(Col.Alpha(Color.White, (int)(16f * hover.Value))))
                    {
                        graphics.FillPath(brush3, path);
                    }
                }
            }
            if (width > 0)
            {
                int left = (isOn ? (rectangle.Right - scaledDimension2 - width) : (rectangle.X + scaledDimension2));
                int top = rectangle.Y + scaledDimension2;
                using (SolidBrush brush4 = new SolidBrush(Col.Alpha(Color.Black, 70)))
                {
                    graphics.FillEllipse(brush4, left, top + Theme.S(1), width, width);
                }
                using (SolidBrush brush5 = new SolidBrush(isOn ? DsPalette.KnobOn : DsPalette.KnobOff))
                {
                    graphics.FillEllipse(brush5, left, top, width, width);
                }
            }
            if (!string.IsNullOrEmpty(Text))
            {
                int sLimitOffset = sLimit + Theme.S(12);
                TextRenderer.DrawText(graphics, Text, Font, new Rectangle(sLimitOffset, 0, base.Width - sLimitOffset, base.Height), ForeColor, TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
            }
            PaintBusy(graphics, new Rectangle(0, 0, sLimit, base.Height), null);
            if (!base.Enabled)
            {
                DsPaint.Veil(graphics, base.ClientRectangle, Col.Alpha(Theme.Card, 105));
            }
            if (base.WantsFocusCue)
            {
                int scaledDimension3 = Theme.S(2);
                DsFocus.Draw(graphics, Rectangle.Intersect(Rectangle.Inflate(rectangle, scaledDimension3, scaledDimension3), base.ClientRectangle), Theme.Fg, base.EffBg);
            }
        }

        private void PaintCompact(Graphics graphics)
        {
            FillBg(graphics);
            if (Width < 2 || Height < 2) return;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            int trackWidth = Math.Min(Theme.S(44), Width - 2);
            int trackHeight = Math.Min(Theme.S(22), Height - 2);
            Rectangle track = new Rectangle((Width - trackWidth) / 2, (Height - trackHeight) / 2, trackWidth, trackHeight);
            float blend = colorBlend.Value;
            Color fill = Col.Lerp(Theme.TrackOff, Theme.Accent, blend * (Enabled ? 0.92f : 0.45f));
            fill = Col.Lerp(fill, Theme.CardHover, hover.Value * 0.1f);
            using (GraphicsPath path = Theme.TechPath(track, Theme.S(6)))
            {
                using (var brush = new SolidBrush(fill)) graphics.FillPath(brush, path);
                using (var pen = new Pen(Enabled ? Col.Lerp(Theme.StrokeHi, Theme.Accent, blend) : Theme.Stroke))
                    graphics.DrawPath(pen, path);
                int inset = Theme.S(3), knobSize = trackHeight - inset * 2;
                if (knobSize > 0)
                {
                    int travel = Math.Max(0, trackWidth - inset * 2 - knobSize);
                    Rectangle knob = new Rectangle(track.Left + inset + (int)Math.Round(travel * blend), track.Top + inset, knobSize, knobSize);
                    using (GraphicsPath knobPath = Theme.TechPath(knob, Theme.S(3)))
                    using (var brush = new SolidBrush(!Enabled ? Theme.Faint : isOn ? DsPalette.KnobOn : DsPalette.KnobOff))
                        graphics.FillPath(brush, knobPath);
                }
                PaintBusy(graphics, track, path);
            }
            if (!Enabled) DsPaint.Veil(graphics, ClientRectangle, Col.Alpha(Theme.Card, 80));
            if (WantsFocusCue)
                DsFocus.Draw(graphics, Rectangle.Intersect(Rectangle.Inflate(track, Theme.S(1), Theme.S(1)), ClientRectangle), Theme.Fg, EffBg);
        }
    }
}
