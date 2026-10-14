// 作者：bdth 2074055628@qq.com
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
    internal sealed partial class CoreMatrix : Control
    {
        internal enum HeadTag { None, Exclusive, Primary, Cache, Smt }

        internal static HeadTag HeadTagFor(bool exclusive, bool anyOn, bool cache, bool multiThread)
        {
            if (exclusive) return HeadTag.Exclusive;
            if (anyOn) return HeadTag.Primary;
            if (cache) return HeadTag.Cache;
            return multiThread ? HeadTag.Smt : HeadTag.None;
        }

        internal static bool HeadTagFits(int headWidth, int indexWidth, int tagWidth, int gap)
        {
            return tagWidth > 0 && indexWidth + gap + tagWidth <= headWidth;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            if (Backdrop.AppliesTo(this)) Backdrop.PaintOnCard(g, this, ClientRectangle);
            else using (var bg = new SolidBrush(BackColor)) g.FillRectangle(bg, ClientRectangle);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            foreach (Band b in bands)
            {
                var titleRect = new Rectangle(0, b.TitleY, Width, Theme.S(BandHead));
                TextRenderer.DrawText(g, b.Title, Theme.UI(8.4f, true), titleRect, Theme.Fg,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                        | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, b.Meta, Theme.MonoFor(b.Meta, 7.4f), titleRect, Theme.Faint,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter
                        | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
                int lineY = b.TitleY + Theme.S(BandHead) - Theme.S(4);
                using (var p = new Pen(Theme.Stroke))
                    g.DrawLine(p, 0, lineY, Width - 1, lineY);

                foreach (Group grp in b.Groups) DrawGroup(g, grp);
            }
            DrawInputFocus(g);
        }

        private void DrawGroup(Graphics g, Group grp)
        {
            int on = 0;
            foreach (Cell c in grp.Cells)
                if ((selected & (1UL << c.Cpu)) != 0) on++;
            bool anyOn = on > 0;
            bool exclusive = MarkExclusive && grp.Cells.Count > 1 && on == 1;

            float lit = 0f;
            foreach (Cell c in grp.Cells)
                if (cellOn[c.Cpu].Value > lit) lit = cellOn[c.Cpu].Value;

            Rectangle r = grp.Rect;
            r.Width -= 1; r.Height -= 1;
            using (GraphicsPath path = Theme.TechPath(r, Theme.S(5)))
            {
                using (var b = new SolidBrush(Col.Lerp(Theme.Bg, Theme.Card, lit)))
                    g.FillPath(b, path);
                Color edge = exclusive ? Col.Alpha(Theme.Accent, 200)
                    : anyOn ? Theme.StrokeHi
                    : Theme.Stroke;
                float edgeW = exclusive ? Math.Max(1f, Theme.S(2) * 0.75f) : 1f;
                using (var p = new Pen(edge, edgeW))
                    g.DrawPath(p, path);
            }

            var head = new Rectangle(r.Left + Theme.S(GroupPad), r.Top + Theme.S(1),
                r.Width - Theme.S(GroupPad) * 2, Theme.S(GroupHead));
            bool cache = cacheHeavy != 0 && (grp.Mask & cacheHeavy) != 0;
            TextRenderer.DrawText(g, "C" + grp.Index.ToString("00"), Theme.Mono(6.9f), head,
                cache ? (anyOn ? Theme.Accent2 : Col.Alpha(Theme.Accent2, 170))
                    : anyOn ? Theme.Dim : Theme.Faint,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                    | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            bool roomy = grp.Cells.Count > 1;
            string tag;
            switch (HeadTagFor(exclusive, anyOn, cache, roomy))
            {
                case HeadTag.Exclusive: tag = Lang.T("core.tag.exclusive"); break;
                case HeadTag.Primary: tag = PrimaryTag; break;
                case HeadTag.Cache: tag = Lang.T("core.tag.cache"); break;
                case HeadTag.Smt: tag = "SMT"; break;
                default: tag = null; break;
            }
            if (tag != null)
            {
                Font tagFont = Theme.MonoFor(tag, 6.4f);
                int indexW = TextRenderer.MeasureText(g, "C" + grp.Index.ToString("00"),
                    Theme.Mono(6.9f), head.Size, TextFormatFlags.NoPadding).Width;
                int tagW = TextRenderer.MeasureText(g, tag, tagFont, head.Size,
                    TextFormatFlags.NoPadding).Width;
                if (!HeadTagFits(head.Width, indexW, tagW, Theme.S(3))) tag = null;
            }
            if (tag != null)
                TextRenderer.DrawText(g, tag, Theme.MonoFor(tag, 6.4f), head,
                    exclusive ? Theme.Accent
                        : anyOn ? Col.Alpha(Theme.Accent, 220)
                        : cache ? Col.Alpha(Theme.Accent2, 170)
                        : Col.Alpha(Theme.Faint, 150),
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter
                        | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);

            foreach (Cell c in grp.Cells) DrawCell(g, c);
        }

        private void DrawCell(Graphics g, Cell c)
        {
            float on = cellOn[c.Cpu].Value;
            Rectangle r = c.Rect;
            r.Inflate(-Theme.S(2), -Theme.S(2));

            Color accent = SchedulingOverlay && !SelectionColor.IsEmpty ? SelectionColor : Theme.Accent;
            Color fill = Col.Lerp(Theme.Bg, accent, on);
            bool disabled = (DisabledMask & (1UL << c.Cpu)) != 0 || !Enabled;
            if (disabled) fill = Theme.Card;
            using (GraphicsPath path = Theme.TechPath(r, Theme.S(4)))
            {
                using (var b = new SolidBrush(fill)) g.FillPath(b, path);
                Color border = Col.Lerp(Theme.Stroke, Col.Alpha(accent, 235), on);
                using (var p = new Pen(border, 1f)) g.DrawPath(p, path);
            }

            TextRenderer.DrawText(g, c.Cpu.ToString(), Theme.Mono(7.6f), r,
                disabled ? Theme.Faint : Col.Lerp(Theme.Dim, Theme.OnAccent, on),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                    | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            if (SchedulingOverlay)
            {
                ulong bit = 1UL << c.Cpu;
                int x = r.Left + Theme.S(3), y = r.Bottom - Theme.S(4);
                ulong[] masks = { SchedulingGameMask, SchedulingIsolationMask };
                Color[] colors = { GameColor, IsolationColor };
                for (int i = 0; i < masks.Length; i++)
                {
                    if ((masks[i] & bit) != 0)
                        using (var brush = new SolidBrush(colors[i]))
                            g.FillRectangle(brush, x, y, Theme.S(5), Theme.S(2));
                    x += Theme.S(7);
                }
            }
        }
    }
}
