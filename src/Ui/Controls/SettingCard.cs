// @author bdth 2074055628@qq.com
// 文件用途 提供设置页卡片控件

using System;
using System.Drawing;
using System.Windows.Forms;

namespace AegisApp
{
    internal class SettingCard : RoundPanel
    {
        private string title = "", desc = "", val = "";
        private Control host;
        private bool hoverOn;
        private Motion cardHover;
        public Color ValueColor = Theme.Dim;

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

        public string Title { get { return title; } set { string v = value ?? ""; if (title != v) { title = v; Invalidate(); } } }
        public string Desc { get { return desc; } set { string v = value ?? ""; if (desc != v) { desc = v; Invalidate(); } } }

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
            LayoutHost();
            if (c is Toggle) Cursor = Cursors.Hand;
        }

        protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); LayoutHost(); }

        private void LayoutHost()
        {
            if (host == null) return;
            host.Location = new Point(Width - Theme.S(18) - host.Width, (Height - host.Height) / 2);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            var t = host as Toggle;
            if (t != null && t.Enabled && e.Button == MouseButtons.Left) t.Checked = !t.Checked;
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            if (!hoverOn) { hoverOn = true; cardHover.To(1f); UiClock.Wake(); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hoverOn && !ClientRectangle.Contains(PointToClient(Cursor.Position)))
            { hoverOn = false; cardHover.To(0f); UiClock.Wake(); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            if (cardHover.Value > 0.01f)
                using (var edge = new Pen(Col.Alpha(Theme.Accent, (int)(190 * cardHover.Value)), Math.Max(1f, Theme.S(2))))
                    g.DrawLine(edge, 0, Theme.S(14), 0, Height - Theme.S(14));
            int padL = Theme.S(18);
            int reserve = padL + (host != null ? host.Width + Theme.S(14) : 0);

            int valW = 0;
            if (val.Length > 0)
            {
                var vr = new Rectangle(Width / 3, 0, Width - Width / 3 - reserve, Height);
                TextRenderer.DrawText(g, val, Theme.UI(9f, false), vr, ValueColor,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                valW = TextRenderer.MeasureText(g, val, Theme.UI(9f, false)).Width + Theme.S(16);
            }

            int textW = Width - padL - reserve - valW;
            if (desc.Length == 0)
            {
                TextRenderer.DrawText(g, title, Theme.UI(9.75f, true),
                    new Rectangle(padL, 0, textW, Height), Theme.Fg,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
            else
            {
                TextRenderer.DrawText(g, title, Theme.UI(9.75f, true),
                    new Rectangle(padL, Theme.S(11), textW, Theme.S(22)), Theme.Fg,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                var dr = new Rectangle(padL, Theme.S(33), textW, Height - Theme.S(40));
                TextFormatFlags df = TextFormatFlags.Left | TextFormatFlags.EndEllipsis;
                // 原来的门槛是卡片高度 >72px，而全项目卡片都是 56~66px，换行分支从不执行，
                // 所有说明都被截成一行加省略号——被截掉的往往正是风险提示那半句。
                // 改成看描述区本身还能不能容下第二行。
                int lineH = TextRenderer.MeasureText("Ag", Theme.UI(8.5f, false)).Height;
                if (dr.Height >= lineH * 2) df |= TextFormatFlags.WordBreak;
                TextRenderer.DrawText(g, desc, Theme.UI(8.5f, false), dr, Theme.Dim, df);
            }
        }
    }


}
