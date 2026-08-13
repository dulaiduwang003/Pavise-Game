// @author bdth 2074055628@qq.com
// 文件用途 概览页设备规格条 斜切分段的机能面板风格

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
    internal sealed class DeviceSpecBar : FxControl
    {
        private static readonly string[] Keys = { "v16.spec.cpu", "v16.spec.gpu", "v16.spec.mem", "v16.spec.hags" };
        private static readonly string[] Icons = { "chip", "gpu", "settings", "shield" };
        private string[] values = { " ", " ", " ", " " };

        public DeviceSpecBar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            TabStop = false;
            Cursor = Cursors.Default;
        }

        public void SetValues(string[] next)
        {
            if (next == null || next.Length < 4) return;
            values = next;
            Invalidate();
        }

        protected override bool StepAll()
        {
            return false;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color accent = Theme.Accent;
            var full = new Rectangle(0, 0, Width, Height);
            using (var bg = new SolidBrush(Theme.Card)) g.FillRectangle(bg, full);

            int slice = Width / 4;
            int skew = Theme.S(14);
            for (int i = 0; i < 4; i++)
            {
                int x = i * slice;
                if (i % 2 == 1)
                    using (var tint = new SolidBrush(Color.FromArgb(16, 255, 255, 255)))
                        g.FillPolygon(tint, Cell(x, slice, skew, i));
                if (i > 0)
                    using (var pen = new Pen(Color.FromArgb(70, accent), Math.Max(1f, Theme.S(1))))
                        g.DrawLine(pen, x + skew, 0, x - skew, Height);
            }

            using (var top = new Pen(Color.FromArgb(150, accent), Math.Max(1f, Theme.S(2))))
                g.DrawLine(top, 0, 0, Width, 0);
            using (var edge = new Pen(Theme.StrokeHi)) g.DrawRectangle(edge, 0, 0, Width - 1, Height - 1);

            Font kf = Theme.UI(7.4f, true);
            Font vf = Theme.UI(9.6f, true);
            for (int i = 0; i < 4; i++)
            {
                int x = i * slice;
                int pad = Theme.S(16);
                var icon = new Rectangle(x + pad, Theme.S(13), Theme.S(15), Theme.S(15));
                Glyphs_Draw(g, Icons[i], icon, Color.FromArgb(210, accent));
                using (var kb = new SolidBrush(Theme.Faint))
                    g.DrawString(Lang.T(Keys[i]), kf, kb, x + pad + Theme.S(20), Theme.S(14));
                using (var vb = new SolidBrush(Theme.Fg))
                {
                    var box = new RectangleF(x + pad, Theme.S(34), slice - pad * 2 + Theme.S(6), Theme.S(20));
                    using (var fmt = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
                        g.DrawString(values[i], vf, vb, box, fmt);
                }
            }
        }

        private static void Glyphs_Draw(Graphics g, string name, Rectangle box, Color color)
        {
            try { Glyphs.Draw(g, name, box, color); }
            catch { }
        }

        private Point[] Cell(int x, int slice, int skew, int index)
        {
            return new[]
            {
                new Point(x + skew, 0), new Point(x + slice + skew, 0),
                new Point(x + slice - skew, Height), new Point(x - skew, Height)
            };
        }
    }
}
