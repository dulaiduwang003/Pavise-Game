// @author bdth 2074055628@qq.com
// File purpose Core matrix painting, heat bars and legend
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

        // Only one tag in the card's top-right corner, priority exclusive > selected > big cache > SMT
        //   SMT only on multi-thread cards, hyperthreading is meaningless on a single-thread card, the other three ignore card width
        //   Whether it fits is decided by the caller from measured width, not guessed from the card's thread count
        internal static HeadTag HeadTagFor(bool exclusive, bool anyOn, bool cache, bool multiThread)
        {
            if (exclusive) return HeadTag.Exclusive;
            if (anyOn) return HeadTag.Primary;
            if (cache) return HeadTag.Cache;
            return multiThread ? HeadTag.Smt : HeadTag.None;
        }

        // Index on the left, tag on the right, not drawn if measurement says it does not fit
        //   It used to guess from the card's thread count, on hybrid parts E-cores are single-thread cards and the selected tag was always blocked
        //   while the exclusive tag took a separate path that ignored width and overlapped the index on the same narrow cards
        //   Both bugs share one root, thread count standing in for width, measuring real width fixes both
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

            if (annotate) DrawLegend(g);
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

            // Peak load of the whole physical core card decides whether the card glows red-hot
            float peak = 0f;
            if (annotate && HasLoads)
                foreach (Cell c in grp.Cells)
                    if (cellHeat[c.Cpu].Value > peak) peak = cellHeat[c.Cpu].Value;
            bool cardHot = annotate && HasLoads && peak >= WarmT;

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
                if (annotate)
                {
                    // Current placement card = violet neon border, clearly distinct from the high-load red glow, busy card = red-hot outer glow by peak load
                    if ((grp.Mask & seenMask) != 0)
                    {
                        edge = Col.Alpha(SeenViolet, 235);
                        edgeW = Math.Max(1f, Theme.S(2) * 1.0f);
                        NeonEdge(g, path, SeenViolet, 0.85f);
                    }
                    else if (cardHot)
                    {
                        Color rc = LoadRog(peak);
                        float ck = GlowK(peak);
                        edge = Col.Lerp(Col.Alpha(rc, 224), Color.White, 0.10f * ck);
                        edgeW = Math.Max(1f, Theme.S(2) * 0.85f);
                        NeonEdge(g, path, rc, ck);
                    }
                }
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
            if (annotate)
            {
                // Core picker dialog, head tag becomes core kind to guide where to move, the current placement card gets a violet Current tag so the placement explains itself
                bool grpSeen = (grp.Mask & seenMask) != 0;
                CoreKind kind = KindOf(grp);
                string ktag = grpSeen ? Lang.T("core.tag.now")
                    : kind == CoreKind.Game ? Lang.T("core.tag.game")
                    : kind == CoreKind.Eff ? Lang.T("core.irqtype.e")
                    : kind == CoreKind.PerfIdle ? Lang.T("core.irqtype.pidle")
                    : Lang.T("core.irqtype.p");
                Color kcol = grpSeen ? Col.Alpha(SeenViolet, 245)
                    : kind == CoreKind.Game ? Col.Alpha(Theme.Danger, 225)
                    : kind == CoreKind.Eff ? Col.Alpha(Theme.Faint, 190)
                    : kind == CoreKind.PerfIdle ? RogCool
                    : Col.Alpha(Theme.Dim, 190);
                TextRenderer.DrawText(g, ktag, Theme.MonoFor(ktag, 6.4f), head, kcol,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter
                        | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            }
            else
            {
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
            }

            foreach (Cell c in grp.Cells)
            {
                if (annotate) DrawCellAnno(g, c); else DrawCell(g, c);
            }
        }

        // Non-annotated pages, the Cores page and Custom cores, stay as they were, not a line changed
        private void DrawCell(Graphics g, Cell c)
        {
            float on = cellOn[c.Cpu].Value;
            float hot = cellHot[c.Cpu].Value;
            Rectangle r = c.Rect;
            r.Inflate(-Theme.S(2), -Theme.S(2));

            Color accent = SchedulingOverlay && !SelectionColor.IsEmpty ? SelectionColor : Theme.Accent;
            Color fill = Col.Lerp(Theme.Bg, accent, on);
            bool disabled = (DisabledMask & (1UL << c.Cpu)) != 0 || !Enabled;
            if (disabled) fill = Theme.Card;
            if (hot > 0.01f)
                fill = Col.Lerp(fill, Col.Lerp(accent, Color.White, on),
                    hot * (0.25f - on * 0.03f));
            using (GraphicsPath path = Theme.TechPath(r, Theme.S(4)))
            {
                using (var b = new SolidBrush(fill)) g.FillPath(b, path);
                Color border = Col.Lerp(Theme.Stroke, Col.Alpha(accent, 235), on);
                using (var p = new Pen(border, 1f)) g.DrawPath(p, path);
            }

            TextRenderer.DrawText(g, c.Cpu.ToString(), Theme.Mono(7.6f), r,
                disabled ? Theme.Faint : Col.Lerp(Col.Lerp(Theme.Dim, Theme.Fg, hot), Theme.OnAccent, on),
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

        // Core picker dialog, border and load bar keep the glow, the number is drawn once, staying legible under high load
        private void DrawCellAnno(Graphics g, Cell c)
        {
            float on = cellOn[c.Cpu].Value;
            float hot = cellHot[c.Cpu].Value;
            Rectangle r = c.Rect;
            r.Inflate(-Theme.S(2), -Theme.S(2));

            double value = 0;
            bool hasLoad = loads != null && loads.TryGetValue(c.Cpu, out value);
            float t = cellHeat[c.Cpu].Value;                 // Smoothed load 0..1
            Color load = LoadRog(t);
            bool warm = hasLoad && t >= WarmT;               // Glow tension starts at 60%
            float gk = GlowK(t);

            // A busy core first lays a big red glow under the card that spills onto the card background, idle cores get none, busy vs idle at a glance
            if (warm) GlowEllipse(g, r, load, gk, 150, 13);

            // Pale load base keeps text contrast in both light and dark themes, selected uses the accent color
            Color baseFill = Col.Lerp(Theme.Inset, Theme.Bg, 0.35f);
            if (hasLoad) baseFill = Col.Lerp(baseFill, load, 0.06f + 0.18f * t);
            Color fill = Col.Lerp(baseFill, Theme.Accent, on);
            if (hot > 0.01f)
                fill = Col.Lerp(fill, Col.Lerp(Theme.Accent, Color.White, on), hot * 0.22f);

            using (GraphicsPath path = Theme.TechPath(r, Theme.S(4)))
            {
                using (var b = new SolidBrush(fill)) g.FillPath(b, path);
                Color border;
                if (on >= 0.5f) border = Col.Alpha(Theme.Accent, 235);
                else if (warm) border = Col.Lerp(Col.Alpha(load, 220), Color.White, 0.12f * gk);
                else if (hasLoad) border = Col.Lerp(Col.Alpha(Theme.Stroke, 210), load, 0.30f + 0.55f * t);
                else border = Theme.Stroke;
                float bw = warm ? Math.Max(1f, Theme.S(2) * 0.8f) : 1f;
                // Unselected busy core, multi-layer red outer glow stroke
                if (warm && on < 0.5f) NeonEdge(g, path, load, gk);
                using (var p = new Pen(border, bw)) g.DrawPath(p, path);
            }

            // Glowing load bar, fill ratio = load, cool low to red high, brighter and more saturated under high load + glow above
            if (hasLoad) DrawLoadBar(g, r, t, load, warm, gk);

            if ((seenMask & (1UL << c.Cpu)) != 0) DrawSeenMark(g, r);

            if (hasLoad)
            {
                // Small core index in the corner for identification and clicking, the main visual goes to the percentage
                var idBox = new Rectangle(r.Left + Theme.S(3), r.Top + Theme.S(1),
                    r.Width - Theme.S(6), Theme.S(11));
                TextRenderer.DrawText(g, c.Cpu.ToString(), Theme.Mono(6.3f), idBox,
                    on >= 0.5f ? Col.Alpha(Theme.OnAccent, 205) : Col.Alpha(Theme.Faint, 205),
                    TextFormatFlags.Left | TextFormatFlags.Top
                        | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);

                int pct = (int)Math.Round(value);
                if (pct < 0) pct = 0; else if (pct > 100) pct = 100;
                string s = pct.ToString() + "%";
                var numBox = new Rectangle(r.Left, r.Top + Theme.S(6),
                    r.Width, r.Height - Theme.S(12));
                Font nf = Theme.Mono(pct >= 100 ? 8.6f : 9.8f);
                Color numCol = on >= 0.5f ? Theme.OnAccent : Theme.Fg;
                TextRenderer.DrawText(g, s, nf, numBox, numCol,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                        | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            }
            else
            {
                // No observation for that core is not 0%, the index stays in the corner and a dash is shown in the middle
                var idBox = new Rectangle(r.Left + Theme.S(3), r.Top + Theme.S(1),
                    r.Width - Theme.S(6), Theme.S(11));
                TextRenderer.DrawText(g, c.Cpu.ToString(), Theme.Mono(6.3f), idBox,
                    on >= 0.5f ? Theme.OnAccent : Theme.Faint,
                    TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, "—", Theme.Mono(9.8f), r,
                    on >= 0.5f ? Theme.OnAccent : Theme.Dim,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                        | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            }
        }

        // Bottom glowing load bar, rounded track + load fill + brighter and more saturated at high load + same-color outer glow
        private void DrawLoadBar(Graphics g, Rectangle r, float t, Color load, bool warm, float gk)
        {
            int barH = Math.Max(Theme.S(5), 4);
            var track = new Rectangle(r.Left + Theme.S(4), r.Bottom - barH - Theme.S(3),
                r.Width - Theme.S(8), barH);
            if (track.Width < 3) return;
            int rad = Math.Max(1, barH / 2);
            int fw = (int)(track.Width * t);
            if (fw < barH && t > 0f) fw = barH;
            var fillRect = new Rectangle(track.X, track.Y, Math.Max(fw, 0), track.Height);
            // A busy bar first pads a ring of glow around the bar body so the bar lights up
            if (warm && fw > 0)
            {
                var halo = new Rectangle(fillRect.X - Theme.S(2), fillRect.Y - Theme.S(3),
                    fillRect.Width + Theme.S(4), barH + Theme.S(6));
                GlowEllipse(g, halo, load, gk, 130, 3);
            }
            using (var tp = Theme.Rounded(track, rad))
            using (var tb = new SolidBrush(Col.Alpha(Theme.Stroke, 170)))
                g.FillPath(tb, tp);
            if (fw <= 0) return;
            Color barCol = warm ? Col.Lerp(load, Color.White, 0.14f) : load;
            using (var fp = Theme.Rounded(fillRect, rad))
            {
                using (var fb = new SolidBrush(barCol)) g.FillPath(fb, fp);
                // Bright top edge gives the bar a neon feel
                using (var hp = new Pen(Col.Lerp(load, Color.White, 0.45f), 1f))
                    g.DrawLine(hp, fillRect.X + rad, fillRect.Y + 0.5f,
                        fillRect.Right - rad, fillRect.Y + 0.5f);
            }
        }

        // Observed IRQ placement this match, violet triangle at bottom right, clearly distinct from high-load red
        private void DrawSeenMark(Graphics g, Rectangle r)
        {
            int s = Theme.S(9);
            var pts = new[]
            {
                new Point(r.Right, r.Bottom),
                new Point(r.Right - s, r.Bottom),
                new Point(r.Right, r.Bottom - s),
            };
            var box = new Rectangle(r.Right - s, r.Bottom - s, s, s);
            GlowEllipse(g, box, SeenViolet, 1f, 110, 6);
            using (var b = new SolidBrush(Col.Alpha(SeenViolet, 248))) g.FillPolygon(b, pts);
        }

        // Game core range and observed load this match are only a core selection reference, no claim that low average load guarantees a gain
        private enum CoreKind { Game, Eff, PerfIdle, Perf }

        private CoreKind KindOf(Group grp)
        {
            ulong game = ObservedGameMask;
            if (game != 0 && (grp.Mask & game) != 0) return CoreKind.Game;
            // E-cores exist only on true hybrid parts and must fall inside the E-core cluster EffMask
            //   On all-P-core machines like the i7-9750H, ThrottleMask is just the background reserve, not E-cores, never tag them E
            //   On non-hybrid parts EffMask is always 0 so this never matches, everything is treated as P and P idle
            if (CpuTopology.Hybrid && (grp.Mask & CpuTopology.EffMask) != 0) return CoreKind.Eff;
            if (HasLoads)
            {
                float peak = 0f;
                foreach (Cell c in grp.Cells)
                {
                    double value;
                    if (!loads.TryGetValue(c.Cpu, out value)) return CoreKind.Perf;
                    peak = Math.Max(peak, (float)(value / 100.0));
                }
                return peak < 0.35f ? CoreKind.PerfIdle : CoreKind.Perf;
            }
            return CoreKind.Perf;
        }

        // One legend item, a load gradient bar or a dot + text, explains what each visual element in the panel means
        private sealed class LegendItem
        {
            public bool Bar;      // true=cold-to-hot load gradient bar, false=category dot
            public Color Color;
            public string Text;
        }

        // Legend, average load this match, observed placement violet, low-load P-core cyan, game core red, E-core
        private List<LegendItem> BuildLegendItems()
        {
            var items = new List<LegendItem>();
            items.Add(new LegendItem { Bar = true, Text = Lang.T("core.legend.load") });
            items.Add(new LegendItem { Color = SeenViolet, Text = Lang.T("core.legend.seen") });
            items.Add(new LegendItem { Color = RogCool, Text = Lang.T("core.legend.pidle") });
            items.Add(new LegendItem { Color = Col.Alpha(Theme.Danger, 225), Text = Lang.T("core.legend.game") });
            // The E-core legend item is shown only on true hybrid parts, never on all-P-core machines
            if (CpuTopology.Hybrid && CpuTopology.EffMask != 0)
                items.Add(new LegendItem { Color = Theme.Faint, Text = Lang.T("core.legend.e") });
            return items;
        }

        private int LegendItemWidth(LegendItem it)
        {
            int lead = (it.Bar ? Theme.S(58) : Theme.S(8)) + Theme.S(4);
            int tw = TextRenderer.MeasureText(it.Text, Theme.MonoFor(it.Text, 7f)).Width;
            return lead + tw + Theme.S(14);
        }

        // Wraps the legend by available width, xs and rows may be null, pass null to only count rows, returns the total row count
        private int LayoutLegend(int avail, List<LegendItem> items, int[] xs, int[] rows)
        {
            int rowCount = 1, x = 0;
            for (int i = 0; i < items.Count; i++)
            {
                int w = LegendItemWidth(items[i]);
                if (x > 0 && x + w > avail) { rowCount++; x = 0; }
                if (xs != null) xs[i] = x;
                if (rows != null) rows[i] = rowCount - 1;
                x += w;
            }
            return rowCount;
        }

        // Actual legend height after wrapping, LayoutFor uses it to size the dialog
        private int LegendHeight(int width)
        {
            var items = BuildLegendItems();
            if (items.Count == 0) return 0;
            int rows = LayoutLegend(width - Theme.S(2), items, null, null);
            return rows * Theme.S(LegendRowH);
        }

        private void DrawLegend(Graphics g)
        {
            if (legendY < 0) return;
            var items = BuildLegendItems();
            if (items.Count == 0) return;
            var xs = new int[items.Count];
            var rows = new int[items.Count];
            LayoutLegend(Width - Theme.S(2), items, xs, rows);
            int rowH = Theme.S(LegendRowH);
            for (int i = 0; i < items.Count; i++)
            {
                var box = new Rectangle(Theme.S(1) + xs[i], legendY + rows[i] * rowH,
                    LegendItemWidth(items[i]), rowH);
                DrawLegendItem(g, box, items[i]);
            }
        }

        private void DrawLegendItem(Graphics g, Rectangle row, LegendItem it)
        {
            int x = row.Left;
            int mid = row.Top + row.Height / 2;
            if (it.Bar)
            {
                // Load gradient bar, ROG cool cyan to yellow-orange to red, explains the cold-to-hot coloring of the big number in each cell
                int barW = Theme.S(58), barH = Theme.S(8);
                var bar = new Rectangle(x, mid - barH / 2, barW, barH);
                using (var lg = new LinearGradientBrush(bar, RogCool, Theme.Danger, 0f))
                {
                    var blend = new ColorBlend(3);
                    blend.Colors = new[] { RogCool, RogAmber, Theme.Danger };
                    blend.Positions = new[] { 0f, 0.5f, 1f };
                    lg.InterpolationColors = blend;
                    g.FillRectangle(lg, bar);
                }
                x += barW + Theme.S(4);
            }
            else
            {
                int d = Theme.S(8);
                using (var b = new SolidBrush(it.Color))
                    g.FillEllipse(b, new Rectangle(x, mid - d / 2, d, d));
                x += d + Theme.S(4);
            }
            TextRenderer.DrawText(g, it.Text, Theme.MonoFor(it.Text, 7f),
                new Rectangle(x, row.Top, row.Right - x, row.Height),
                it.Bar ? Theme.Faint : Theme.Dim,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }
}
