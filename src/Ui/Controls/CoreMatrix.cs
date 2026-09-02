// @author bdth 2074055628@qq.com
// 文件用途 核心矩阵控件的单元格模型 配色与动画帧
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
    internal sealed partial class CoreMatrix : Control
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

        // 选核弹窗专用的同局平均负载 + 核类型叠加 其余页面保持原样
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
        //   阈值刻意压低 60% 就进 橙 段 72% 就烧成纯红 让中高负载核一眼可见地暖/红
        //   低段越接近 0 越暗 让空闲核冷下去 与繁忙红核拉开对比
        // 本局中断落核的专用标记色 紫罗兰 区别于负载红和低载青
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

        // 径向霓虹辉光 让高负载核从深黑底上 跳 出来 用 PathGradientBrush 才有真正的软发光
        //   pad 越大 光晕外溢越远 越 炸 ;draw 在填充之上时用小 pad 只染内部 不糊字
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
    }
}
