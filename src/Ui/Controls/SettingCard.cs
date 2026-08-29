// @author bdth 2074055628@qq.com
// 文件用途 提供设置页卡片控件
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
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
            Radius = Theme.S(12);
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
        public int MetaReserve;
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
        public const int StatusLineH = 17;

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
            int hy = HostTop ? Theme.S(10) : (Height - host.Height) / 2;
            host.Location = new Point(Width - Theme.S(18) - host.Width, hy);
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
            var t = host as Toggle;
            if (t != null && t.Enabled) t.Checked = !t.Checked;
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
            var g = e.Graphics;
            DrawTacticalSurface(g);
            if (cardHover.Value > 0.01f)
                using (var edge = new Pen(Col.Alpha(Theme.Accent, (int)(190 * cardHover.Value)), Math.Max(1f, Theme.S(2))))
                    g.DrawLine(edge, 0, Theme.S(14), 0, Height - Theme.S(14));
            int padL = Theme.S(42);
            int reserve = padL + (host != null && host.Visible ? host.Width + Theme.S(14) : 0);

            int chevW = collapsible ? Theme.S(20) : 0;
            int textW = Width - padL - reserve - chevW;
            Font titleFont = Theme.UI(9.75f, true);
            int badgeW = lockText.Length == 0
                ? 0
                : TextRenderer.MeasureText(g, lockText, Theme.MonoFor(lockText, 7.0f)).Width
                    + Theme.S(12) + Theme.S(9);
            if (collapsible) DrawChevron(g, padL + textW + Theme.S(4));
            bool showDesc = desc.Length > 0 && ShowDesc;

            int valW = 0;
            Font valFont = Theme.UI(9f, false);
            if (val.Length > 0) valW = TextRenderer.MeasureText(g, val, valFont).Width + Theme.S(16);

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

            int titleY = HasStatus && !showDesc ? Theme.S(9) : Theme.S(11);
            if (valW > 0)
            {
                var vr = new Rectangle(padL, titleY, textW, Theme.S(22));
                TextRenderer.DrawText(g, val, valFont, vr, ValueColor,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter
                        | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            }
            var head = new Rectangle(padL, titleY, textW - badgeW - valW, Theme.S(22));
            TextRenderer.DrawText(g, title, titleFont, head, Theme.Fg,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            DrawLockBadge(g, padL, head, titleY + Theme.S(11));

            int y = head.Bottom + Theme.S(1);
            if (HasStatus)
            {
                var dot = new Rectangle(padL + Theme.S(1), y + Theme.S(5), Theme.S(6), Theme.S(6));
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var b = new SolidBrush(statusInk)) g.FillEllipse(b, dot);
                TextRenderer.DrawText(g, status, Theme.UI(8.2f, false),
                    new Rectangle(dot.Right + Theme.S(7), y, textW - Theme.S(14), Theme.S(StatusLineH)),
                    statusInk,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                        | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
                y += Theme.S(StatusLineH);
            }
            int metaPad = 0;
            if (meta.Length > 0)
            {
                metaPad = Theme.S(20);
                var mr = new Rectangle(padL, Height - Theme.S(26),
                    Math.Max(0, Width - padL - Theme.S(18) - MetaReserve), Theme.S(16));
                TextRenderer.DrawText(g, meta, Theme.Mono(7f), mr, Theme.Faint,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                        | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            }
            if (!showDesc) return;

            var dr = new Rectangle(padL, y + Theme.S(2), textW, Height - y - Theme.S(9) - metaPad);
            TextFormatFlags df = TextFormatFlags.Left | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis;
            int lineH = TextRenderer.MeasureText("Ag", Theme.UI(8.5f, false)).Height;
            if (lineH > 0 && dr.Height >= lineH) dr.Height = dr.Height / lineH * lineH;
            int wantH = TextRenderer.MeasureText(g, desc, Theme.UI(8.5f, false),
                new Size(dr.Width, int.MaxValue), TextFormatFlags.WordBreak).Height;
            DescLinesWanted = lineH > 0 ? (wantH + lineH - 1) / lineH : 0;
            DescLinesShown = lineH > 0 ? dr.Height / lineH : 0;
            TextRenderer.DrawText(g, desc, Theme.UI(8.5f, false), dr, Theme.DimTint, df);
        }

        private void DrawTacticalSurface(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int railX = Theme.S(30);
            using (var rail = new Pen(Col.Alpha(Theme.StrokeHi, 82)))
                g.DrawLine(rail, railX, Theme.S(11), railX, Height - Theme.S(11));
            using (var live = new Pen(Col.Alpha(Theme.Accent, 118 + (int)(cardHover.Value * 90))))
                g.DrawLine(live, railX, Theme.S(17), railX, Math.Min(Height - Theme.S(12), Theme.S(37)));

            string code = Channel > 0 ? Channel.ToString("00") : "--";
            TextRenderer.DrawText(g, code, Theme.Mono(5.8f),
                new Rectangle(Theme.S(7), Theme.S(9), Theme.S(18), Theme.S(15)),
                Col.Lerp(Theme.Faint, Theme.Accent, cardHover.Value * 0.75f),
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            int bladeX = Width - Theme.S(170);
            Point[] blade = {
                new Point(bladeX, 1), new Point(Width - Theme.S(10), 1),
                new Point(Width - 1, Theme.S(11)), new Point(Width - 1, Height - 1),
                new Point(bladeX + Theme.S(48), Height - 1)
            };
            using (var wash = new LinearGradientBrush(
                new Rectangle(Math.Max(0, bladeX), 0, Math.Max(1, Width - bladeX), Math.Max(1, Height)),
                Col.Alpha(Theme.Accent, 0), Col.Alpha(Theme.Accent,
                    (int)((Theme.LightMode ? 8 : 5) + cardHover.Value * 9)), LinearGradientMode.Horizontal))
                g.FillPolygon(wash, blade);

            using (var top = new Pen(Col.Alpha(Theme.Accent, 74 + (int)(cardHover.Value * 82))))
                g.DrawLine(top, Theme.S(1), Theme.S(1), Theme.S(58), Theme.S(1));
        }

        private void DrawChevron(Graphics g, int x)
        {
            int cy = expanded ? Theme.S(22) : Height / 2;
            float a = Theme.S(4);
            float top = expanded ? cy + a / 2f : cy - a / 2f;
            float bottom = expanded ? cy - a / 2f : cy + a / 2f;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var p = new Pen(Col.Lerp(Theme.Faint, Theme.Accent, cardHover.Value),
                Math.Max(1.4f, Theme.S(1))))
            {
                p.StartCap = LineCap.Round; p.EndCap = LineCap.Round; p.LineJoin = LineJoin.Round;
                g.DrawLines(p, new[]
                {
                    new PointF(x, top), new PointF(x + a, bottom), new PointF(x + a * 2, top),
                });
            }
        }

        private void DrawLockBadge(Graphics g, int padL, Rectangle titleRect, int centerY)
        {
            if (lockText.Length == 0) return;
            Font f = Theme.MonoFor(lockText, 7.0f);
            int tw = TextRenderer.MeasureText(g, title, Theme.UI(9.75f, true)).Width;
            if (tw > titleRect.Width) tw = titleRect.Width;
            int bw = TextRenderer.MeasureText(g, lockText, f).Width + Theme.S(12);
            int bh = Theme.S(17);
            var pill = new Rectangle(padL + tw + Theme.S(9), centerY - bh / 2, bw, bh);
            if (pill.Right > Width - Theme.S(8)) return;

            Color fill = lockOn ? Theme.Accent : Theme.Inset;
            Color edge = lockOn ? Col.Alpha(Theme.Accent, 235) : Theme.Stroke;
            Color ink = lockOn ? Theme.OnAccent : Theme.Faint;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath path = Theme.TechPath(pill, Theme.S(4)))
            {
                using (var b = new SolidBrush(fill)) g.FillPath(b, path);
                using (var p = new Pen(edge)) g.DrawPath(p, path);
            }
            TextRenderer.DrawText(g, lockText, f, pill, ink,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                    | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        }
    }

}
