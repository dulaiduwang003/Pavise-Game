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

        private int CellWpx { get { return Theme.S(CellW); } }
        private int CellHpx { get { return Theme.S(CellH); } }

        private readonly List<Band> bands = new List<Band>();
        private ulong selected;
        private ulong allMask;
        private ulong cacheHeavy;
        private int laidOutFor;
        private readonly Motion[] cellOn = new Motion[64];

        public ulong DisabledMask { get; set; }
        public ulong SchedulingGameMask { get; set; }
        public ulong SchedulingIsolationMask { get; set; }
        public bool SchedulingOverlay { get; set; }
        public Color SelectionColor { get; set; }
        internal static Color GameColor { get { return Theme.Accent; } }
        internal static Color IsolationColor { get { return Theme.Accent2; } }

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
            for (int i = 0; i < 64; i++) cellOn[i].Speed = 0.34f;
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
                if (cellOn[i].Step()) moved = true;
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

        internal void ShowSelectionImmediately(ulong mask)
        {
            selected = mask & allMask;
            SnapCells();
            Invalidate();
        }
    }
}
