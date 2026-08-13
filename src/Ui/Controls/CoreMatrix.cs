// @author bdth 2074055628@qq.com
// 文件用途 处理器核心矩阵 按物理核成卡 超线程兄弟同卡并列 性能核与能效核分带显示

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
    internal sealed class CoreMatrix : Control
    {
        private sealed class Cell
        {
            public int Cpu;
            public Rectangle Rect;
            public bool Sibling;
        }

        private sealed class Group
        {
            public int Index;
            public ulong Mask;
            public Rectangle Rect;
            public readonly List<Cell> Cells = new List<Cell>();
        }

        private sealed class Band
        {
            public string Title;
            public string Meta;
            public int TitleY;
            public readonly List<Group> Groups = new List<Group>();
        }

        private const int CellW = 30;
        private const int CellH = 30;
        private const int GroupPad = 3;
        private const int GroupHead = 15;
        private const int GroupGap = 7;
        private const int RowGap = 7;
        private const int BandHead = 26;
        private const int BandGap = 16;

        private readonly List<Band> bands = new List<Band>();
        private ulong selected;
        private ulong allMask;
        private ulong cacheHeavy;
        private int hoverCpu = -1;
        private int laidOutFor;
        private readonly Motion[] cellOn = new Motion[64];
        private readonly Motion[] cellHot = new Motion[64];

        public Action<ulong> SelectionChanged;

#if PAVISE_SELFTEST
        internal string[] SelfTestBandTitles()
        {
            var list = new List<string>();
            foreach (Band b in bands) list.Add(b.Title);
            return list.ToArray();
        }

        internal ulong SelfTestBandMask(int index)
        {
            if (index < 0 || index >= bands.Count) return 0;
            ulong m = 0;
            foreach (Group g in bands[index].Groups) m |= g.Mask;
            return m;
        }
#endif

        public string PrimaryTag;

        public CoreMatrix()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bg;
            allMask = CpuTopology.AllMask;
            selected = allMask;
            for (int i = 0; i < 64; i++)
            {
                cellOn[i].Speed = 0.34f;
                cellHot[i].Speed = 0.30f;
            }
            SnapCells();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            UiClock.Frame += OnFrame;
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            UiClock.Frame -= OnFrame;
            base.OnHandleDestroyed(e);
        }

        private void OnFrame(object s, EventArgs e)
        {
            bool moved = false;
            for (int i = 0; i < 64; i++)
            {
                if (cellOn[i].Step()) moved = true;
                if (cellHot[i].Step()) moved = true;
            }
            if (moved) Invalidate();
        }

        private void SnapCells()
        {
            for (int i = 0; i < 64; i++)
                cellOn[i].Set((selected & (1UL << i)) != 0 ? 1f : 0f);
        }

        private void SyncCells()
        {
            if (!IsHandleCreated) { SnapCells(); return; }
            for (int i = 0; i < 64; i++)
                cellOn[i].To((selected & (1UL << i)) != 0 ? 1f : 0f);
            UiClock.Wake();
        }

        private void SyncHot()
        {
            if (!IsHandleCreated) return;
            for (int i = 0; i < 64; i++)
                cellHot[i].To(i == hoverCpu ? 1f : 0f);
            UiClock.Wake();
        }

        public ulong Selected
        {
            get { return selected; }
            set
            {
                ulong v = value & allMask;
                if (v == selected) return;
                selected = v;
                SyncCells();
                Invalidate();
            }
        }

        public int LayoutFor(int width)
        {
            bands.Clear();
            laidOutFor = width;
            ulong[] cores = CpuTopology.PhysicalCoreMasks();
            if (cores.Length == 0) return Theme.S(40);

            cacheHeavy = CpuTopology.CacheHeavyMask();
            ulong[] dies = CpuTopology.DieMasks();
            int y = 0;

            if (dies.Length >= 2)
            {
                for (int d = 0; d < dies.Length; d++)
                {
                    var members = new List<ulong>();
                    foreach (ulong c in cores)
                    {
                        ulong m = c & allMask;
                        if (m != 0 && (m & dies[d]) != 0) members.Add(m);
                    }
                    if (members.Count == 0) continue;
                    if (y > 0) y += Theme.S(BandGap);
                    bool cache = cacheHeavy != 0 && (dies[d] & cacheHeavy) != 0;
                    y = AddBand(members, Lang.F(cache ? "core.band.ccd.cache" : "core.band.ccd", d),
                        width, y, true);
                }
                return y;
            }

            ulong perfMask = CpuTopology.Hybrid ? CpuTopology.PerfMask : 0;
            var big = new List<ulong>();
            var small = new List<ulong>();
            foreach (ulong c in cores)
            {
                ulong m = c & allMask;
                if (m == 0) continue;
                bool isBig = perfMask != 0
                    ? (m & perfMask) != 0
                    : CpuTopology.CountSetBits(m) > 1;
                if (isBig) big.Add(m); else small.Add(m);
            }

            bool split = big.Count > 0 && small.Count > 0;
            if (big.Count > 0)
                y = AddBand(big, split ? Lang.T("core.band.perf") : Lang.T("core.band.all"),
                    width, y);
            if (small.Count > 0)
            {
                if (big.Count > 0) y += Theme.S(BandGap);
                y = AddBand(small, split ? Lang.T("core.band.eff") : Lang.T("core.band.all"),
                    width, y);
            }
            return y;
        }

        private int AddBand(List<ulong> groups, string title, int width, int y)
        {
            bool smt = false;
            foreach (ulong m in groups)
                if (CpuTopology.CountSetBits(m) > 1) { smt = true; break; }
            return AddBand(groups, title, width, y, smt);
        }

        private int AddBand(List<ulong> groups, string title, int width, int y, bool smt)
        {
            int logical = 0;
            foreach (ulong m in groups) logical += CpuTopology.CountSetBits(m);
            var band = new Band
            {
                Title = title,
                Meta = Lang.F(smt ? "core.band.meta.smt" : "core.band.meta.solo",
                    groups.Count, logical),
                TitleY = y,
            };
            y += Theme.S(BandHead);

            int x = 0;
            int cardH = Theme.S(GroupHead) + Theme.S(CellH) + Theme.S(GroupPad) * 2;
            int rowH = cardH;
            foreach (ulong mask in groups)
            {
                int members = CpuTopology.CountSetBits(mask);
                int groupW = members * Theme.S(CellW) + Theme.S(GroupPad) * 2;
                if (x > 0 && x + groupW > width)
                {
                    x = 0;
                    y += rowH + Theme.S(RowGap);
                }
                var grp = new Group
                {
                    Index = LowestIndex(mask),
                    Mask = mask,
                    Rect = new Rectangle(x, y, groupW, cardH),
                };
                int cx = x + Theme.S(GroupPad);
                bool first = true;
                for (int cpu = 0; cpu < 64; cpu++)
                {
                    if ((mask & (1UL << cpu)) == 0) continue;
                    grp.Cells.Add(new Cell
                    {
                        Cpu = cpu,
                        Rect = new Rectangle(cx, y + Theme.S(GroupHead) + Theme.S(GroupPad),
                            Theme.S(CellW), Theme.S(CellH)),
                        Sibling = !first,
                    });
                    cx += Theme.S(CellW);
                    first = false;
                }
                band.Groups.Add(grp);
                x += groupW + Theme.S(GroupGap);
            }
            bands.Add(band);
            return y + rowH;
        }

        private static int LowestIndex(ulong mask)
        {
            for (int i = 0; i < 64; i++) if ((mask & (1UL << i)) != 0) return i;
            return 0;
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (Width > 0 && Width != laidOutFor) { LayoutFor(Width); Invalidate(); }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            foreach (Band b in bands)
                foreach (Group g in b.Groups)
                    foreach (Cell c in g.Cells)
                        if (c.Rect.Contains(e.Location))
                        {
                            selected ^= 1UL << c.Cpu;
                            SyncCells();
                            Invalidate();
                            if (SelectionChanged != null) SelectionChanged(selected);
                            return;
                        }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int now = -1;
            Rectangle dirty = Rectangle.Empty;
            foreach (Band b in bands)
                foreach (Group g in b.Groups)
                    foreach (Cell c in g.Cells)
                        if (c.Rect.Contains(e.Location)) { now = c.Cpu; dirty = g.Rect; }
            if (now == hoverCpu) return;
            hoverCpu = now;
            Cursor = now >= 0 ? Cursors.Hand : Cursors.Default;
            SyncHot();
            Invalidate();
            if (!dirty.IsEmpty) Invalidate(dirty);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hoverCpu >= 0) { hoverCpu = -1; SyncHot(); Invalidate(); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            using (var bg = new SolidBrush(BackColor)) g.FillRectangle(bg, ClientRectangle);
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
        }

        private void DrawGroup(Graphics g, Group grp)
        {
            int on = 0;
            foreach (Cell c in grp.Cells)
                if ((selected & (1UL << c.Cpu)) != 0) on++;
            bool anyOn = on > 0;
            bool exclusive = grp.Cells.Count > 1 && on == 1;

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
                using (var p = new Pen(edge, exclusive ? Math.Max(1f, Theme.S(2) * 0.75f) : 1f))
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
            string tag = exclusive ? Lang.T("core.tag.exclusive")
                : anyOn ? (roomy ? PrimaryTag : null)
                : cache ? Lang.T("core.tag.cache")
                : roomy ? "SMT" : null;
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
            float hot = cellHot[c.Cpu].Value;
            Rectangle r = c.Rect;
            r.Inflate(-Theme.S(2), -Theme.S(2));

            Color fill = Col.Lerp(Theme.Bg, Theme.Accent, on);
            if (hot > 0.01f)
                fill = Col.Lerp(fill, Col.Lerp(Theme.Accent, Color.White, on),
                    hot * (0.25f - on * 0.03f));
            using (GraphicsPath path = Theme.TechPath(r, Theme.S(4)))
            {
                using (var b = new SolidBrush(fill)) g.FillPath(b, path);
                using (var p = new Pen(Col.Lerp(Theme.Stroke, Col.Alpha(Theme.Accent, 235), on), 1f))
                    g.DrawPath(p, path);
            }

            TextRenderer.DrawText(g, c.Cpu.ToString(), Theme.Mono(7.6f), r,
                Col.Lerp(Col.Lerp(Theme.Dim, Theme.Fg, hot), Theme.OnAccent, on),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                    | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        }
    }
}
