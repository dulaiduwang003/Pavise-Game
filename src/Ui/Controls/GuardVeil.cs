// @author bdth 2074055628@qq.com
// 文件用途 守护未开启时盖住那些开了也不生效的页面 并说清该去哪开
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
    internal sealed class GuardVeil : Control
    {
        private const int Shrink = 10;
        private Bitmap frosted;
        private readonly Label title = new Label();
        private readonly Label hint = new Label();
        private readonly PillButton action;

        public Action Go;

        public bool Wanted;

        public bool HasFrost { get { return frosted != null; } }

        public GuardVeil(string titleText, string hintText, string buttonText)
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bg;
            Visible = false;

            title.Text = titleText;
            title.ForeColor = Theme.Fg;
            title.BackColor = Color.Transparent;
            title.Font = Theme.UI(15f, true);
            title.TextAlign = ContentAlignment.MiddleCenter;
            title.UseCompatibleTextRendering = false;
            Controls.Add(title);

            hint.Text = hintText;
            hint.ForeColor = Theme.Dim;
            hint.BackColor = Color.Transparent;
            hint.Font = Theme.UI(9f, false);
            hint.TextAlign = ContentAlignment.MiddleCenter;
            hint.UseCompatibleTextRendering = false;
            Controls.Add(hint);

            action = new PillButton(buttonText, BtnKind.Primary);
            action.Click += delegate { Action g = Go; if (g != null) g(); };
            Controls.Add(action);
        }

        public void Frost(Control under)
        {
            Drop();
            if (under == null || under.Width <= 0 || under.Height <= 0) return;
            try
            {
                using (var raw = new Bitmap(under.Width, under.Height))
                {
                    under.DrawToBitmap(raw, new Rectangle(0, 0, under.Width, under.Height));
                    int sw = Math.Max(1, under.Width / Shrink);
                    int sh = Math.Max(1, under.Height / Shrink);
                    using (var small = new Bitmap(sw, sh))
                    {
                        using (Graphics g = Graphics.FromImage(small))
                        {
                            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                            g.DrawImage(raw, 0, 0, sw, sh);
                        }
                        var big = new Bitmap(under.Width, under.Height);
                        using (Graphics g = Graphics.FromImage(big))
                        {
                            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                            g.PixelOffsetMode = PixelOffsetMode.Half;
                            g.DrawImage(small, 0, 0, under.Width, under.Height);
                        }
                        frosted = big;
                    }
                }
            }
            catch { Drop(); }
        }

        public void Drop()
        {
            if (frosted != null) { try { frosted.Dispose(); } catch { } frosted = null; }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            int w = Math.Min(Dpi.S(520), Width - Dpi.S(40));
            if (w < Dpi.S(120)) w = Math.Max(Dpi.S(120), Width);
            int cx = (Width - w) / 2;
            int cy = Height / 2 - Dpi.S(70);
            title.SetBounds(cx, cy, w, Dpi.S(38));
            hint.SetBounds(cx, cy + Dpi.S(44), w, Dpi.S(40));
            action.SetBounds((Width - Dpi.S(190)) / 2, cy + Dpi.S(100), Dpi.S(190), Dpi.S(36));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            if (frosted != null)
            {
                try { g.DrawImage(frosted, 0, 0, Width, Height); }
                catch { g.Clear(Theme.Bg); }
                using (var scrim = new SolidBrush(Col.Alpha(Theme.Bg, 205)))
                    g.FillRectangle(scrim, 0, 0, Width, Height);
            }
            else g.Clear(Theme.Bg);

            int w = Math.Min(Dpi.S(120), Width / 3);
            int y = Height / 2 - Dpi.S(88);
            using (var p = new Pen(Theme.Accent, Math.Max(1, Dpi.S(2))))
                g.DrawLine(p, (Width - w) / 2, y, (Width + w) / 2, y);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Drop();
            base.Dispose(disposing);
        }
    }
}
