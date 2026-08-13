// @author bdth 2074055628@qq.com
// 文件用途 核心分配的占比条 一条横条分两段 左段游戏右段后台 空心表示还没应用

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
    internal sealed class CoreSplitBar : Control
    {
        private const int HeadH = 18;
        private const int BarH = 10;
        private const int Gap = 5;
        private const int CapH = 15;
        private const int CapMinW = 44;

        private int gameCores, totalCores;
        private string headText = "", tailText = "";
        private bool tailWarn, dirty, valid = true;
        private Motion frac;
        private bool fracReady;

        public CoreSplitBar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bg;
            frac.Speed = 0.30f;
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
            if (frac.Step()) Invalidate();
        }

        public static int PreferredHeight
        {
            get { return Theme.S(HeadH) + Theme.S(Gap) + Theme.S(BarH) + Theme.S(CapH); }
        }

        public void SetState(int game, int total,
            string head, string tail, bool warnTail, bool pendingApply, bool selectionValid)
        {
            gameCores = game; totalCores = total;
            headText = head ?? ""; tailText = tail ?? "";
            tailWarn = warnTail; dirty = pendingApply; valid = selectionValid;
            float want = total > 0 ? (float)game / total : 0f;
            if (!fracReady) { frac.Set(want); fracReady = true; }
            else if (Math.Abs(frac.Target - want) > 0.0005f) { frac.To(want); UiClock.Wake(); }
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            using (var back = new SolidBrush(BackColor)) g.FillRectangle(back, ClientRectangle);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            var head = new Rectangle(0, 0, Width, Theme.S(HeadH));
            const TextFormatFlags Line = TextFormatFlags.VerticalCenter
                | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis;

            TextRenderer.DrawText(g, headText, Theme.UI(valid ? 8.6f : 8.4f, true), head,
                !valid ? Theme.Danger : dirty ? Theme.Accent : Theme.Fg,
                TextFormatFlags.Left | Line);
            if (valid)
                TextRenderer.DrawText(g, tailText, Theme.UI(7.9f, false), head,
                    tailWarn ? Theme.Danger : Theme.Dim, TextFormatFlags.Right | Line);

            var track = new Rectangle(0, Theme.S(HeadH) + Theme.S(Gap),
                Width - 1, Theme.S(BarH) - 1);
            if (track.Width <= 4) return;

            using (GraphicsPath path = Theme.TechPath(track, Theme.S(3)))
            {
                using (var fill = new SolidBrush(Theme.Inset)) g.FillPath(fill, path);

                Region saved = g.Clip;
                g.SetClip(path, CombineMode.Intersect);
                if (!valid) FillDanger(g, track);
                else if (totalCores > 0) PaintSegments(g, track);
                g.Clip = saved;

                using (var edge = new Pen(valid ? Theme.Stroke : Col.Alpha(Theme.Danger, 170)))
                    g.DrawPath(edge, path);
            }
            if (valid && totalCores > 0) PaintCaptions(g, track);
        }

        private void PaintCaptions(Graphics g, Rectangle track)
        {
            int gameW = Scale(track.Width, gameCores);
            int spare = totalCores - gameCores;

            Caption(g, track, track.X, gameW, Lang.T("core.tag.game"), gameCores,
                dirty ? Theme.Accent : Theme.Fg);
            if (spare > 0)
                Caption(g, track, track.X + gameW, track.Width - gameW,
                    Lang.T("core.bar.spare"), spare, Theme.Faint);
        }

        private static void Caption(Graphics g, Rectangle track, int x, int w,
            string label, int cores, Color ink)
        {
            if (w < Theme.S(CapMinW) || cores <= 0) return;
            var r = new Rectangle(x, track.Bottom + Theme.S(2), w, Theme.S(CapH) - Theme.S(2));
            string text = label + " " + cores;
            TextRenderer.DrawText(g, text, Theme.MonoFor(text, 7.0f), r, ink,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.Top
                    | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        }

        private static void FillDanger(Graphics g, Rectangle r)
        {
            using (var fill = new HatchBrush(HatchStyle.WideUpwardDiagonal,
                Col.Alpha(Theme.Danger, 165), Theme.Inset))
                g.FillRectangle(fill, r);
        }

        private void PaintSegments(Graphics g, Rectangle track)
        {
            int gameW = Scale(track.Width, gameCores);
            if (gameW > 0)
            {
                var seg = new Rectangle(track.X, track.Y, gameW, track.Height);
                if (dirty)
                {
                    using (var fill = new SolidBrush(Col.Alpha(Theme.Accent, 70)))
                        g.FillRectangle(fill, seg);
                    using (var edge = new Pen(Col.Alpha(Theme.Accent, 235)))
                        g.DrawRectangle(edge, seg.X, seg.Y, seg.Width - 1, seg.Height - 1);
                }
                else
                {
                    using (var fill = new LinearGradientBrush(
                        new Rectangle(seg.X, seg.Y, seg.Width, seg.Height + 1),
                        Theme.Accent, Theme.Accent2, LinearGradientMode.Horizontal))
                        g.FillRectangle(fill, seg);
                }
            }
            int spare = track.Width - gameW;
            if (spare > 0 && gameCores < totalCores)
            {
                var seg = new Rectangle(track.X + gameW, track.Y, spare, track.Height);
                using (var fill = new HatchBrush(HatchStyle.WideUpwardDiagonal,
                    Col.Alpha(Theme.Danger, 165), Theme.Inset))
                    g.FillRectangle(fill, seg);
            }
        }

        private int Scale(int width, int cores)
        {
            if (cores <= 0) return 0;
            int w = (int)(width * frac.Value);
            return w < 2 ? 2 : w > width ? width : w;
        }
    }
}
