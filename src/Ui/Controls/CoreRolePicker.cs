// @author bdth 2074055628@qq.com
// 文件用途 核心划分的状态牌 图例与实时计数
// 图例样式和矩阵里的核完全一致 用户不必猜实心代表谁 计数就在手边不用去底部找

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
    internal sealed class CoreRolePicker : Control
    {
        private const int PadX = 12;
        private const int SwatchW = 15;
        private const int SwatchH = 15;

        private int gameCores, gamePhysical;
        private Motion pulse;

        public string GameLabel = "";
        public string GameCountText = "";

        public CoreRolePicker()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bg;
            pulse.Speed = 0.18f;
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
            if (pulse.Step()) Invalidate();
        }

        public static int PreferredHeight { get { return Theme.S(44); } }

        public void SetCounts(int game, int gamePhys)
        {
            if (gameCores == game && gamePhysical == gamePhys) return;
            gameCores = game; gamePhysical = gamePhys;
            if (IsHandleCreated) { pulse.Set(1f); pulse.To(0f); UiClock.Wake(); }
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            using (var back = new SolidBrush(BackColor)) g.FillRectangle(back, ClientRectangle);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            Rectangle r = ClientRectangle;
            r.Inflate(-Theme.S(2), -Theme.S(2));
            r.Width -= 1; r.Height -= 1;

            using (GraphicsPath path = Theme.TechPath(r, Theme.S(5)))
            {
                using (var b = new SolidBrush(Theme.Card)) g.FillPath(b, path);
                using (var p = new Pen(Theme.Stroke)) g.DrawPath(p, path);
            }

            var swatch = new Rectangle(r.Left + Theme.S(PadX),
                r.Top + (r.Height - Theme.S(SwatchH)) / 2, Theme.S(SwatchW), Theme.S(SwatchH));
            using (GraphicsPath path = Theme.TechPath(swatch, Theme.S(3)))
            {
                using (var b = new SolidBrush(Col.Lerp(Theme.Accent, Color.White, pulse.Value * 0.55f)))
                    g.FillPath(b, path);
                using (var p = new Pen(Col.Alpha(Theme.Accent, 235))) g.DrawPath(p, path);
            }

            int textLeft = swatch.Right + Theme.S(9);
            var top = new Rectangle(textLeft, r.Top + Theme.S(5),
                r.Right - textLeft - Theme.S(PadX), Theme.S(17));
            var bottom = new Rectangle(textLeft, top.Bottom - Theme.S(1), top.Width, Theme.S(15));

            const TextFormatFlags Line = TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;

            TextRenderer.DrawText(g, GameLabel, Theme.UI(8.6f, true), top, Theme.Fg, Line);
            TextRenderer.DrawText(g, GameCountText, Theme.MonoFor(GameCountText, 7.4f),
                bottom, Theme.Accent, Line);
        }
    }
}
