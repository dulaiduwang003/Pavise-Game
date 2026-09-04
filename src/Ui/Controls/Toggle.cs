// @author bdth 2074055628@qq.com
// 文件用途 提供开关控件和状态颜色反馈
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
    internal class Toggle : FxControl
    {
        private bool isOn;
        private Motion colorBlend;
        public event EventHandler CheckedChanged;

        public Toggle()
        {

            Size = new Size(Dpi.S(46), Dpi.S(30));
            ForeColor = Theme.Fg;
            Font = Theme.UI(9.75f, false);
            colorBlend.Speed = 0.32f;
        }

        public bool Checked
        {
            get { return isOn; }
            set
            {
                if (isOn == value) return;
                isOn = value; colorBlend.To(value ? 1f : 0f);
                UiClock.Wake(); Invalidate();
                if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty);
            }
        }

        public void SetSilently(bool v)
        {
            float target = v ? 1f : 0f;
            if (isOn == v && colorBlend.Value == target && colorBlend.Target == target) return;
            isOn = v;
            colorBlend.Set(target);
            Invalidate();
        }

        protected override bool StepAll() { bool a = base.StepAll(); bool b = colorBlend.Step(); return a || b; }
        protected override void OnClick(EventArgs e) { base.OnClick(e); Checked = !isOn; }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            FillBg(g);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            int th = Dpi.S(22), pad = Dpi.S(3);
            if (th > Height) th = Height;
            int tw = string.IsNullOrEmpty(Text) ? Math.Max(Dpi.S(20), Width - 1) : Dpi.S(44);
            int kd = th - pad * 2;
            var track = new Rectangle(0, (Height - th) / 2, tw, th);
            float p = colorBlend.Value;

            using (var path = Theme.Rounded(track, th / 2))
            {
                using (var b = new SolidBrush(Theme.TrackOff)) g.FillPath(b, path);
                if (p > 0.01f)
                {
                    var gr = track; if (gr.Width < 1) gr.Width = 1;
                    using (var lg = new LinearGradientBrush(gr, Col.Alpha(Theme.Accent, (int)(255 * p)), Col.Alpha(Theme.Accent2, (int)(255 * p)), LinearGradientMode.Horizontal))
                        g.FillPath(lg, path);
                }
                else
                {
                    using (var pen = new Pen(Theme.Stroke)) g.DrawPath(pen, path);
                }
                if (hover.Value > 0.01f)
                    using (var hl = new SolidBrush(Col.Alpha(Color.White, (int)(16 * hover.Value)))) g.FillPath(hl, path);
            }

            int kx = isOn ? track.Right - pad - kd : track.X + pad;
            int ky = track.Y + pad;
            using (var sh = new SolidBrush(Col.Alpha(Color.Black, 70))) g.FillEllipse(sh, kx, ky + Dpi.S(1), kd, kd);
            using (var kb = new SolidBrush(isOn ? Color.White : Color.FromArgb(178, 183, 192)))
                g.FillEllipse(kb, kx, ky, kd, kd);

            if (!string.IsNullOrEmpty(Text))
            {
                int tx = tw + Dpi.S(12);
                TextRenderer.DrawText(g, Text, Font, new Rectangle(tx, 0, Width - tx, Height), ForeColor,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
            if (!Enabled)
                using (var veil = new SolidBrush(Col.Alpha(Theme.Card, 105)))
                    g.FillRectangle(veil, ClientRectangle);
        }
    }

}
