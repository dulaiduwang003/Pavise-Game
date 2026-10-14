// 作者：bdth 2074055628@qq.com
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
    // 设置卡：标题、说明、状态、徽章与宿主控件；点击卡面切换宿主开关需通过开关的交互门禁。
    internal class SettingCard : RoundPanel
    {
        private string title = "", desc = "", val = "";
        private Control host;
        private bool hoverOn;
        private bool pressed;
        private Motion cardHover;
        public Color ValueColor = Theme.Dim;
        public int Channel;

        public SettingCard()
        {
            Radius = Theme.S(DsMetrics.Card.Radius);
            Fill = Theme.Card;
            Border = Theme.Stroke;
            BackColor = Theme.Bg;
            cardHover.Speed = 0.24f;
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); UiClock.Frame += OnFrame; }
        protected override void OnHandleDestroyed(EventArgs e) { UiClock.Frame -= OnFrame; base.OnHandleDestroyed(e); }
        private void OnFrame(object sender, EventArgs e)
        {
            if (cardHover.Step())
            {
                Fill = Col.Lerp(Theme.Card, Theme.CardHover, cardHover.Value * 0.55f);
                Border = Col.Lerp(Theme.Stroke, Theme.StrokeHi, cardHover.Value);
                Invalidate();
            }
        }

        public void Flash()
        {
            cardHover.Set(1f); cardHover.To(0f);
            UiClock.Wake(); Invalidate();
        }

        public string Title { get { return title; } set { string v = value ?? ""; if (title != v) { title = v; Invalidate(); } } }
#if PAVISE_SELFTEST
        public bool Suppressed;
#endif

        private string lockText = "";
        private bool lockOn;

        public void SetLock(string text, bool on)
        {
            string v = text ?? "";
            if (lockText == v && lockOn == on) return;
            lockText = v; lockOn = on;
            Invalidate();
        }

        public string Desc { get { return desc; } set { string v = value ?? ""; if (desc != v) { desc = v; Invalidate(); } } }

        private string meta = "";
        private bool hostTop;
        public bool HostTop
        {
            get { return hostTop; }
            set { if (hostTop == value) return; hostTop = value; LayoutHost(); }
        }

        public string Meta { get { return meta; } set { string v = value ?? ""; if (meta != v) { meta = v; Invalidate(); } } }

        private bool collapsible;
        private bool expanded = true;
        public int CollapsedHeight, ExpandedHeight;
        public Action ExpandedChanged;

        public bool Collapsible
        {
            get { return collapsible; }
            set
            {
                if (collapsible == value) return;
                collapsible = value;
                if (value) Cursor = Cursors.Hand;
                Invalidate();
            }
        }

        public bool Expanded
        {
            get { return expanded; }
            set
            {
                if (expanded == value) return;
                expanded = value;
                if (collapsible && ExpandedHeight > 0 && CollapsedHeight > 0)
                    Height = expanded ? ExpandedHeight : CollapsedHeight;
                if (ExpandedChanged != null) ExpandedChanged();
                Invalidate();
            }
        }

        internal void SnapExpanded(bool value)
        {
            expanded = value;
            Invalidate();
        }

        private string status = "";
        private Color statusInk = Theme.Faint;
        public bool HasStatus { get { return status.Length > 0; } }
        public const int StatusLineH = DsMetrics.Card.StatusLine;

        internal int DescLinesWanted { get; private set; }
        internal int DescLinesShown { get; private set; }

        public void SetStatus(string text, Color ink)
        {
            string v = text ?? "";
            bool textChanged = v != status;
            bool changed = textChanged || ink != statusInk;
            bool flash = textChanged && v.Length > 0 && status.Length > 0 && !string.IsNullOrEmpty(title);
            status = v; statusInk = ink;
            if (flash) Flash();
            if (changed) Invalidate();
        }

        private bool ShowDesc { get { return !collapsible || expanded; } }

        public string Value { get { return val; } set { string v = value ?? ""; if (val != v) { val = v; Invalidate(); } } }
        public void SetValue(string v, Color c)
        {
            string nv = v ?? "";
            bool changed = nv != val || c != ValueColor;
            ValueColor = c; val = nv;
            if (changed) Invalidate();
        }

        public Control Hosted { get { return host; } }

        public void Host(Control c)
        {
            host = c;
            Controls.Add(c);
            c.MouseLeave += OnHostMouseLeave;
            c.VisibleChanged += delegate { Invalidate(); };
            LayoutHost();
            if (c is Toggle) Cursor = Cursors.Hand;
        }

        private void OnHostMouseLeave(object sender, EventArgs e)
        {
            ClearHoverIfOutside();
        }

        public void TrackChildHover(Control c)
        {
            c.MouseLeave += OnHostMouseLeave;
        }

        protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); LayoutHost(); }

        private void LayoutHost()
        {
            if (host == null) return;
            int hy = HostTop ? Theme.S(DsMetrics.Card.HostTop) : (Height - host.Height) / 2;
            host.Location = new Point(Width - Theme.S(DsMetrics.Card.HostRight) - host.Width, hy);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left) pressed = true;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            bool was = pressed;
            if (e.Button == MouseButtons.Left) pressed = false;
            if (!was || e.Button != MouseButtons.Left) return;
            if (!ClientRectangle.Contains(e.Location)) return;
            if (collapsible) { Expanded = !expanded; return; }
            // 卡面点击只在宿主开关可交互时切换，禁用或忙碌时不改变状态。
            var t = host as Toggle;
            if (t != null && t.CanInteract) t.Checked = !t.Checked;
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            if (!hoverOn) { hoverOn = true; cardHover.To(1f); UiClock.Wake(); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            ClearHoverIfOutside();
        }

        private void ClearHoverIfOutside()
        {
            if (!hoverOn || IsDisposed) return;
            Point p;
            try { p = PointToClient(Cursor.Position); }
            catch { return; }
            if (ClientRectangle.Contains(p)) return;
            hoverOn = false; cardHover.To(0f); UiClock.Wake();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (Width < 2 || Height < 2) return;
            var g = e.Graphics;
            DrawTacticalSurface(g);
            if (cardHover.Value > 0.01f)
                using (var edge = new Pen(Col.Alpha(Theme.Accent, (int)(190 * cardHover.Value)), DsSpace.Line(2)))
                    g.DrawLine(edge, 0, Theme.S(14), 0, Height - Theme.S(14));
            int padL = Theme.S(DsMetrics.Card.PadLeft);
            int reserve = padL + (host != null && host.Visible ? host.Width + Theme.S(DsMetrics.Card.HostGap) : 0);

            int chevW = collapsible ? Theme.S(DsMetrics.Card.ChevronWidth) : 0;
            int textW = Width - padL - reserve - chevW;
            Font titleFont = DsType.Font(DsText.Title, true);
            int badgeW = lockText.Length == 0
                ? 0
                : DsPaint.BadgeWidth(g, lockText, DsType.MonoFor(DsMono.Badge, lockText)) + Theme.S(DsMetrics.Card.BadgeGap);
            if (collapsible) DrawChevron(g, padL + textW + Theme.S(4));
            bool showDesc = desc.Length > 0 && ShowDesc;

            int valW = 0;
            Font valFont = DsType.Font(DsText.Body);
            if (val.Length > 0) valW = TextRenderer.MeasureText(g, val, valFont).Width + Theme.S(DsMetrics.Card.ValuePad);

            if (!showDesc && !HasStatus)
            {
                if (valW > 0)
                {
                    var vr = new Rectangle(Width / 3, 0, Width - Width / 3 - reserve - chevW, Height);
                    TextRenderer.DrawText(g, val, valFont, vr, ValueColor,
                        TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                }
                var tr = new Rectangle(padL, 0, textW - badgeW - valW, Height);
                TextRenderer.DrawText(g, title, titleFont, tr, Theme.Fg,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                DrawLockBadge(g, padL, tr, Height / 2);
                return;
            }

            int titleY = Theme.S(HasStatus && !showDesc ? DsMetrics.Card.TitleTopWithStatus : DsMetrics.Card.TitleTop);
            int titleH = Theme.S(DsMetrics.Card.TitleHeight);
            if (valW > 0)
            {
                var vr = new Rectangle(padL, titleY, textW, titleH);
                TextRenderer.DrawText(g, val, valFont, vr, ValueColor,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter
                        | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            }
            var head = new Rectangle(padL, titleY, textW - badgeW - valW, titleH);
            TextRenderer.DrawText(g, title, titleFont, head, Theme.Fg,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            DrawLockBadge(g, padL, head, titleY + Theme.S(DsMetrics.Card.TitleHeight / 2));

            int y = head.Bottom + Theme.S(1);
            if (HasStatus)
            {
                int dotSize = Theme.S(DsMetrics.Card.StatusDot);
                var dot = new Rectangle(padL + Theme.S(1), y + Theme.S(DsMetrics.Card.StatusDotTop), dotSize, dotSize);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var b = new SolidBrush(statusInk)) g.FillEllipse(b, dot);
                TextRenderer.DrawText(g, status, DsType.Font(DsText.Status),
                    new Rectangle(dot.Right + Theme.S(DsMetrics.Card.StatusGap), y, textW - Theme.S(14), Theme.S(StatusLineH)),
                    statusInk,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                        | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
                y += Theme.S(StatusLineH);
            }
            int metaPad = 0;
            if (meta.Length > 0)
            {
                metaPad = Theme.S(DsMetrics.Card.MetaPad);
                var mr = new Rectangle(padL, Height - Theme.S(DsMetrics.Card.MetaBottom),
                    Math.Max(0, Width - padL - Theme.S(DsMetrics.Card.HostRight)), Theme.S(DsMetrics.Card.MetaHeight));
                TextRenderer.DrawText(g, meta, DsType.Mono(DsMono.Meta), mr, Theme.Faint,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                        | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            }
            if (!showDesc) return;

            Font descFont = DsType.Font(DsText.Description);
            var dr = new Rectangle(padL, y + Theme.S(DsMetrics.Card.DescTop), textW,
                Height - y - Theme.S(DsMetrics.Card.DescBottom) - metaPad);
            TextFormatFlags df = TextFormatFlags.Left | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis;
            int lineH = DsType.LineHeight(DsText.Description);
            if (lineH > 0 && dr.Height >= lineH) dr.Height = dr.Height / lineH * lineH;
            int wantH = TextRenderer.MeasureText(g, desc, descFont,
                new Size(dr.Width, int.MaxValue), TextFormatFlags.WordBreak).Height;
            DescLinesWanted = lineH > 0 ? (wantH + lineH - 1) / lineH : 0;
            DescLinesShown = lineH > 0 ? dr.Height / lineH : 0;
            TextRenderer.DrawText(g, desc, descFont, dr, Theme.DimTint, df);
        }

        private void DrawTacticalSurface(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int railX = Theme.S(DsMetrics.Card.RailX);
            using (var rail = new Pen(Col.Alpha(Theme.StrokeHi, 82)))
                g.DrawLine(rail, railX, Theme.S(DsMetrics.Card.RailInset), railX, Height - Theme.S(DsMetrics.Card.RailInset));
            using (var live = new Pen(Col.Alpha(Theme.Accent, 118 + (int)(cardHover.Value * 90))))
                g.DrawLine(live, railX, Theme.S(DsMetrics.Card.LiveTop), railX,
                    Math.Min(Height - Theme.S(DsMetrics.Card.LiveMargin), Theme.S(DsMetrics.Card.LiveBottom)));

            string code = Channel > 0 ? Channel.ToString("00") : "--";
            TextRenderer.DrawText(g, code, DsType.Mono(DsMono.Channel),
                new Rectangle(Theme.S(DsMetrics.Card.CodeX), Theme.S(DsMetrics.Card.CodeY),
                    Theme.S(DsMetrics.Card.CodeW), Theme.S(DsMetrics.Card.CodeH)),
                Col.Lerp(Theme.Faint, Theme.Accent, cardHover.Value * 0.75f),
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            int bladeX = Width - Theme.S(DsMetrics.Card.BladeReach);
            Point[] blade = {
                new Point(bladeX, 1), new Point(Width - Theme.S(DsMetrics.Card.BladeTopInset), 1),
                new Point(Width - 1, Theme.S(DsMetrics.Card.BladeDrop)), new Point(Width - 1, Height - 1),
                new Point(bladeX + Theme.S(DsMetrics.Card.BladeTail), Height - 1)
            };
            using (var wash = new LinearGradientBrush(
                DsPaint.GradientRect(new Rectangle(Math.Max(0, bladeX), 0, Width - bladeX, Height)),
                Col.Alpha(Theme.Accent, 0), Col.Alpha(Theme.Accent,
                    (int)((Theme.LightMode ? 8 : 5) + cardHover.Value * 9)), LinearGradientMode.Horizontal))
                g.FillPolygon(wash, blade);

            using (var top = new Pen(Col.Alpha(Theme.Accent, 74 + (int)(cardHover.Value * 82))))
                g.DrawLine(top, Theme.S(1), Theme.S(1), Theme.S(DsMetrics.Card.TopLineReach), Theme.S(1));
        }

        private void DrawChevron(Graphics g, int x)
        {
            int cy = expanded ? Theme.S(DsMetrics.Card.ChevronTop) : Height / 2;
            DsPaint.Chevron(g, x, cy, Theme.S(DsMetrics.Card.ChevronArm), expanded,
                Col.Lerp(Theme.Faint, Theme.Accent, cardHover.Value));
        }

        private void DrawLockBadge(Graphics g, int padL, Rectangle titleRect, int centerY)
        {
            if (lockText.Length == 0) return;
            Font f = DsType.MonoFor(DsMono.Badge, lockText);
            int tw = TextRenderer.MeasureText(g, title, DsType.Font(DsText.Title, true)).Width;
            if (tw > titleRect.Width) tw = titleRect.Width;
            int bw = DsPaint.BadgeWidth(g, lockText, f);
            int bh = Theme.S(DsMetrics.Badge.Height);
            var pill = new Rectangle(padL + tw + Theme.S(DsMetrics.Card.BadgeGap), centerY - bh / 2, bw, bh);
            if (pill.Right > Width - Theme.S(DsMetrics.Card.BadgeRightMargin)) return;
            DsPaint.Badge(g, pill, lockText, f, lockOn);
        }
    }

}
