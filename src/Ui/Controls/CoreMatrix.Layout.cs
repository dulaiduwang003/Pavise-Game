// @author bdth 2074055628@qq.com
// 文件用途 核心矩阵的分带布局与鼠标交互
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
    internal sealed partial class CoreMatrix : Control
    {
        public int LayoutFor(int width)
        {
            int h = LayoutBands(width);
            legendY = -1;
            if (annotate && h > Theme.S(40))
            {
                legendY = h + Theme.S(RowGap);
                // 图例按实际折行行数算高度 再撑进弹窗 避免多行图例被裁 / 底部内容被挤出对话框
                h = legendY + LegendHeight(width);
            }
            return h;
        }

        private int LayoutBands(int width)
        {
            allMask = CpuTopology.AllMask;
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
            int cardH = Theme.S(GroupHead) + CellHpx + Theme.S(GroupPad) * 2;
            int rowH = cardH;
            foreach (ulong mask in groups)
            {
                int members = CpuTopology.CountSetBits(mask);
                int groupW = members * CellWpx + Theme.S(GroupPad) * 2;
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
                            CellWpx, CellHpx),
                        Sibling = !first,
                    });
                    cx += CellWpx;
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
                            ToggleCpu(c.Cpu);
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
    }
}
