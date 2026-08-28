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

        // 选核弹窗(Annotate)把格子放大 好塞下大号负载百分比 其余页面维持原尺寸
        private const int AnnCellW = 46;
        private const int AnnCellH = 42;
        private int CellWpx { get { return Theme.S(annotate ? AnnCellW : CellW); } }
        private int CellHpx { get { return Theme.S(annotate ? AnnCellH : CellH); } }

        private readonly List<Band> bands = new List<Band>();
        private ulong selected;
        private ulong allMask;
        private ulong cacheHeavy;
        private int hoverCpu = -1;
        private int laidOutFor;
        private readonly Motion[] cellOn = new Motion[64];
        private readonly Motion[] cellHot = new Motion[64];
        private readonly Motion[] cellHeat = new Motion[64];

        // 选核弹窗专用的同局平均负载 + 核类型叠加，其余页面保持原样。
        private ulong seenMask;
        public ulong ObservedGameMask { get; set; }
        private bool annotate;
        private Dictionary<int, double> loads;
        private int legendY = -1;

        // 图例每行高度 会按可用宽度折行 行数由 LegendHeight 实算 撑够弹窗
        private const int LegendRowH = 22;

        public Action<ulong> SelectionChanged;

        // 打开负载热力/类型标注 会多占一行图例 必须在 LayoutFor 之前设
        public bool Annotate
        {
            get { return annotate; }
            set { annotate = value; }
        }

        public ulong SeenMask
        {
            get { return seenMask; }
            set { if (seenMask == value) return; seenMask = value & allMask; Invalidate(); }
        }

        // 采到的 per-core 负载 采集失败传空字典即可 热力自动退回不画 只留类型标注
        public void SetLoads(Dictionary<int, double> map)
        {
            loads = new Dictionary<int, double>();
            if (map != null)
                foreach (var item in map)
                    if (item.Key >= 0 && item.Key < 64 && !double.IsNaN(item.Value)
                        && !double.IsInfinity(item.Value) && item.Value >= 0 && item.Value <= 100)
                        loads[item.Key] = item.Value;
            if (!IsHandleCreated) { for (int i = 0; i < 64; i++) cellHeat[i].Set(HeatOf(i)); }
            else { for (int i = 0; i < 64; i++) cellHeat[i].To(HeatOf(i)); UiClock.Wake(); }
            Invalidate();
        }

        private bool HasLoads { get { return loads != null && loads.Count > 0; } }

        private float HeatOf(int cpu)
        {
            double v;
            if (loads == null || !loads.TryGetValue(cpu, out v)) return 0f;
            float t = (float)(v / 100.0);
            return t < 0f ? 0f : t > 1f ? 1f : t;
        }

        // ROG 电竞负载色阶 分档明确:低=冷青(压暗) 中=黄 高=橙 极高=ROG 红(Theme.Danger 恒红)
        //   阈值刻意压低 60% 就进「橙」段 72% 就烧成纯红 让中高负载核一眼可见地暖/红
        //   低段越接近 0 越暗 让空闲核冷下去 与繁忙红核拉开对比
        // 本局中断落核的专用标记色：紫罗兰，区别于负载红和低载青。
        private static readonly Color SeenViolet = Color.FromArgb(176, 138, 255);
        private static readonly Color RogCool = Color.FromArgb(64, 200, 240);
        private static readonly Color RogAmber = Color.FromArgb(255, 178, 44);
        private static readonly Color RogCyan = Color.FromArgb(96, 216, 248);
        private static readonly Color RogYellow = Color.FromArgb(255, 216, 72);
        private static readonly Color RogOrange = Color.FromArgb(255, 122, 36);

        // 发光起始阈值 与强度曲线:60% 起冒头 越往 100% 越炸
        private const float WarmT = 0.60f;
        private static float GlowK(float t)
        {
            float k = (t - 0.48f) / 0.42f;   // 0.60→0.29 0.80→0.76 0.88→0.95 1.0→1
            return k < 0f ? 0f : k > 1f ? 1f : k;
        }

        private static Color LoadRog(float t)
        {
            if (t < 0f) t = 0f; else if (t > 1f) t = 1f;
            Color red = Theme.Danger;
            if (t < 0.38f) return Col.Lerp(Color.FromArgb(118, 150, 170), RogCyan, t / 0.38f);
            if (t < 0.55f) return Col.Lerp(RogCyan, RogYellow, (t - 0.38f) / 0.17f);
            if (t < 0.72f) return Col.Lerp(RogYellow, RogOrange, (t - 0.55f) / 0.17f);
            return Col.Lerp(RogOrange, red, (t - 0.72f) / 0.13f);   // 85%+ 已是纯红
        }

        // 径向霓虹辉光 让高负载核从深黑底上「跳」出来 用 PathGradientBrush 才有真正的软发光
        //   pad 越大 光晕外溢越远 越「炸」;draw 在填充之上时用小 pad 只染内部 不糊字
        private static void GlowEllipse(Graphics g, Rectangle r, Color c, float k, int centerAlpha, int pad)
        {
            if (k < 0f) k = 0f; else if (k > 1f) k = 1f;
            if (k <= 0.001f) return;
            int p = Theme.S(pad);
            var glow = Rectangle.Inflate(r, p, p);
            if (glow.Width <= 0 || glow.Height <= 0) return;
            using (var path = new GraphicsPath())
            {
                path.AddEllipse(glow);
                using (var pg = new PathGradientBrush(path))
                {
                    pg.CenterPoint = new PointF(r.Left + r.Width / 2f, r.Top + r.Height / 2f);
                    pg.CenterColor = Col.Alpha(c, (int)(centerAlpha * k));
                    pg.SurroundColors = new[] { Col.Alpha(c, 0) };
                    g.FillPath(pg, path);
                }
            }
        }

        // 多层递减描边模拟外发光 用于繁忙核 / 繁忙卡片的红霓虹边
        private static void NeonEdge(Graphics g, GraphicsPath path, Color c, float k)
        {
            if (k <= 0.001f) return; if (k > 1f) k = 1f;
            float[] w = { Theme.S(2) * 3.6f, Theme.S(2) * 2.4f, Theme.S(2) * 1.3f };
            int[] a = { (int)(38 * k) + 6, (int)(66 * k) + 12, (int)(120 * k) + 28 };
            for (int i = 0; i < w.Length; i++)
                using (var p = new Pen(Col.Alpha(c, a[i]), w[i]))
                { p.LineJoin = LineJoin.Round; g.DrawPath(p, path); }
        }

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

        public bool MarkExclusive = true;

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
                cellHeat[i].Speed = 0.22f;
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
                if (cellHeat[i].Step()) moved = true;
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
            if (Backdrop.Active) Backdrop.PaintOnCard(g, this, ClientRectangle);
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

            // 整张物理核卡的峰值负载 决定卡片是否「烧红」发光
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
                    // 当前落核卡片=紫罗兰霓虹边(与高负载红发光明确区分) 繁忙卡片=按峰值负载烧红外发光
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
                // 选核弹窗 头标改成核类型 指引「挪去哪里」;当前落核卡片改标紫罗兰「当前」 让落点自解释
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
            }

            foreach (Cell c in grp.Cells)
            {
                if (annotate) DrawCellAnno(g, c); else DrawCell(g, c);
            }
        }

        // 非标注页(核心页 / 自定义核) 保持原样 一行不变
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
                Color border = Col.Lerp(Theme.Stroke, Col.Alpha(Theme.Accent, 235), on);
                using (var p = new Pen(border, 1f)) g.DrawPath(p, path);
            }

            TextRenderer.DrawText(g, c.Cpu.ToString(), Theme.Mono(7.6f), r,
                Col.Lerp(Col.Lerp(Theme.Dim, Theme.Fg, hot), Theme.OnAccent, on),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                    | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        }

        // 选核弹窗：边框/负载条保留辉光，数字只画一次，保证高负载时依然清晰。
        private void DrawCellAnno(Graphics g, Cell c)
        {
            float on = cellOn[c.Cpu].Value;
            float hot = cellHot[c.Cpu].Value;
            Rectangle r = c.Rect;
            r.Inflate(-Theme.S(2), -Theme.S(2));

            double value = 0;
            bool hasLoad = loads != null && loads.TryGetValue(c.Cpu, out value);
            float t = cellHeat[c.Cpu].Value;                 // 平滑后的负载 0..1
            Color load = LoadRog(t);
            bool warm = hasLoad && t >= WarmT;               // 60% 起就有发光张力
            float gk = GlowK(t);

            // 繁忙核先在卡底铺一圈大红辉光 外溢到卡片背景上 空闲核不铺 一眼分空/忙
            if (warm) GlowEllipse(g, r, load, gk, 150, 13);

            // 淡色负载底保留明暗主题的文字对比度，选中时使用强调色。
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
                // 未选中的繁忙核 多层红外发光描边
                if (warm && on < 0.5f) NeonEdge(g, path, load, gk);
                using (var p = new Pen(border, bw)) g.DrawPath(p, path);
            }

            // 发光负载条:填充比例=负载 低冷高红 高负载更亮更饱和 + 上方辉光
            if (hasLoad) DrawLoadBar(g, r, t, load, warm, gk);

            if ((seenMask & (1UL << c.Cpu)) != 0) DrawSeenMark(g, r);

            if (hasLoad)
            {
                // 角标小核号 便于识别与点选 主视觉让给百分比
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
                // 缺少该核心的观测不等于 0%：核号仍在角落，中间显示破折号。
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

        // 底部发光负载条 圆角轨道 + 负载填充 + 高负载更亮更饱和 + 同色外发光
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
            // 繁忙条先在条身四周垫一圈辉光 让条子「亮起来」
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
                // 亮顶边 让条子有霓虹感
                using (var hp = new Pen(Col.Lerp(load, Color.White, 0.45f), 1f))
                    g.DrawLine(hp, fillRect.X + rad, fillRect.Y + 0.5f,
                        fillRect.Right - rad, fillRect.Y + 0.5f);
            }
        }

        // 本局观测落核：右下角紫罗兰三角，与高负载红明确区分。
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

        // 同局游戏核范围与观测负载仅供选核参考，不宣称低平均负载能保证收益。
        private enum CoreKind { Game, Eff, PerfIdle, Perf }

        private CoreKind KindOf(Group grp)
        {
            ulong game = ObservedGameMask;
            if (game != 0 && (grp.Mask & game) != 0) return CoreKind.Game;
            // E 能效核只在真混合架构上存在 且必须落在能效核簇 EffMask
            //   全大核机器(i7-9750H 等)ThrottleMask 只是后台预留 不是能效核 绝不标 E
            //   非混合时 EffMask 恒为 0 这里自然不会命中 全部按 P / P·空闲 处理
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

        // 图例一项:负载渐变条 或 一枚圆点 + 文案 讲清面板里每个视觉元素的含义
        private sealed class LegendItem
        {
            public bool Bar;      // true=负载冷→热渐变条 false=分类圆点
            public Color Color;
            public string Text;
        }

        // 图例：同局平均负载 / 观测落核(紫) / 低载性能核(青) / 游戏核(红) / 能效核。
        private List<LegendItem> BuildLegendItems()
        {
            var items = new List<LegendItem>();
            items.Add(new LegendItem { Bar = true, Text = Lang.T("core.legend.load") });
            items.Add(new LegendItem { Color = SeenViolet, Text = Lang.T("core.legend.seen") });
            items.Add(new LegendItem { Color = RogCool, Text = Lang.T("core.legend.pidle") });
            items.Add(new LegendItem { Color = Col.Alpha(Theme.Danger, 225), Text = Lang.T("core.legend.game") });
            // E 能效核图例只在真混合架构显示 全大核机器不出现
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

        // 按可用宽度把图例折行 xs/rows 可空(只算行数时) 返回总行数
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

        // 图例实际高度(折行后) LayoutFor 用它把弹窗撑够
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
                // 负载渐变条 ROG 冷青→黄橙→红 说明格内大数字的冷→热配色
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
