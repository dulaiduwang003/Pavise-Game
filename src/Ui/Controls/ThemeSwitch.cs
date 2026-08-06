// @author bdth 2074055628@qq.com
// 文件用途 提供标题栏亮暗主题切换控件

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{

    internal sealed class ThemeSwitch : FxControl
    {
        private bool lightOn;
        private Motion slide;
        private Motion flash;
        public Action<bool> Toggled;

        public ThemeSwitch(bool light)
        {
            lightOn = light;
            Bg = Theme.Bg;
            TabStop = false;
            AccessibleName = Lang.T("set.light");
            AccessibleDescription = Lang.T("set.light.n");
            slide.Speed = 0.30f; slide.Set(light ? 1f : 0f);
            flash.Speed = 0.12f; flash.Set(0f);
        }

        public bool LightOn { get { return lightOn; } }

        public void SetSilently(bool light)
        {
            if (lightOn == light) return;
            lightOn = light;
            slide.To(light ? 1f : 0f);
            UiClock.Wake();
        }

        protected override bool StepAll()
        {
            bool a = hover.Step();
            bool b = slide.Step();
            bool c = flash.Step();
            return a || b || c;
        }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            lightOn = !lightOn;
            slide.To(lightOn ? 1f : 0f);
            flash.Set(1f); flash.To(0f);
            UiClock.Wake();
            Invalidate();
            if (Toggled != null) Toggled(lightOn);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            FillBg(g);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float t = slide.Value, h = hover.Value, fl = flash.Value;

            Rectangle body = new Rectangle(0, Theme.S(2), Width - 1, Height - Theme.S(5));
            int cut = Theme.S(9);
            using (GraphicsPath p = Theme.TechPath(body, cut))
            {
                using (var b = new SolidBrush(Col.Lerp(Theme.Card, Theme.CardHover, h))) g.FillPath(b, p);
                using (var pen = new Pen(Col.Lerp(Theme.Stroke, Theme.Accent, 0.30f + h * 0.5f))) g.DrawPath(pen, p);
            }
            using (var edge = new Pen(Theme.Accent, Math.Max(1f, Theme.S(2))))
                g.DrawLine(edge, 0, Theme.S(13), 0, Height - Theme.S(14));
            using (var corner = new Pen(Col.Alpha(Theme.Accent, (int)(110 + 110 * h)), Math.Max(1f, Theme.S(1))))
                g.DrawLine(corner, body.Right - cut, body.Top, body.Right, body.Top + cut);

            int pad = Theme.S(7);
            RectangleF track = new RectangleF(body.X + pad, body.Y + pad,
                body.Width - pad * 2, body.Height - pad * 2);
            if (track.Width < Theme.S(12) || track.Height < Theme.S(8)) return;
            int inner = Theme.S(7);

            using (GraphicsPath p = TechPathF(track, inner))
            {
                using (var b = new SolidBrush(Theme.Inset)) g.FillPath(b, p);
                g.SetClip(p);
                using (var hatch = new Pen(Col.Alpha(Theme.Accent, (int)(13 + 15 * h)), Math.Max(1f, Theme.S(1))))
                    for (float sx = track.Left - track.Height; sx < track.Right; sx += Theme.S(6))
                        g.DrawLine(hatch, sx, track.Bottom, sx + track.Height, track.Top);
                g.ResetClip();
                using (var pen = new Pen(Col.Lerp(Theme.Stroke, Theme.StrokeHi, 0.45f))) g.DrawPath(pen, p);
            }

            float cellW = track.Width / 2f;
            RectangleF thumb = new RectangleF(track.X + cellW * t, track.Y, cellW, track.Height);
            using (GraphicsPath p = TechPathF(thumb, inner))
            {
                for (int i = 3; i >= 1; i--)
                    using (var glow = new Pen(Col.Alpha(Theme.Accent, (int)((24 + 30 * h + 55 * fl) / i)), Theme.S(2) * i))
                        g.DrawPath(glow, p);
                using (var fill = new LinearGradientBrush(
                    new PointF(thumb.Left, thumb.Top - 1f), new PointF(thumb.Right, thumb.Bottom + 1f),
                    Col.Lerp(Theme.Accent, Color.White, 0.12f + fl * 0.32f), Theme.Accent2))
                    g.FillPath(fill, p);
                using (var gloss = new Pen(Col.Alpha(Color.White, (int)(64 + 90 * fl)), Math.Max(1f, Theme.S(1))))
                    g.DrawLine(gloss, thumb.Left + Theme.S(2), thumb.Top + 0.5f, thumb.Right - inner - Theme.S(1), thumb.Top + 0.5f);
                using (var pen = new Pen(Col.Alpha(Theme.Accent, 235), Math.Max(1f, Theme.S(1)))) g.DrawPath(pen, p);
            }

            int ic = Theme.S(17);
            Color idle = Col.Lerp(Theme.Faint, Theme.Fg, 0.25f + h * 0.45f);
            Glyphs.Draw(g, "moon", IconBox(track.X, track.Y, cellW, track.Height, ic),
                Col.Lerp(idle, Theme.OnAccent, 1f - t));
            Glyphs.Draw(g, "sun", IconBox(track.X + cellW, track.Y, cellW, track.Height, ic),
                Col.Lerp(idle, Theme.OnAccent, t));
        }

        private static Rectangle IconBox(float x, float y, float w, float hgt, int size)
        {
            return new Rectangle(
                (int)Math.Round(x + (w - size) / 2f),
                (int)Math.Round(y + (hgt - size) / 2f),
                size, size);
        }

        private static GraphicsPath TechPathF(RectangleF r, float cut)
        {
            var p = new GraphicsPath();
            cut = Math.Max(1f, Math.Min(cut, Math.Min(r.Width, r.Height) / 3f));
            p.AddPolygon(new[] {
                new PointF(r.Left, r.Top), new PointF(r.Right - cut, r.Top),
                new PointF(r.Right, r.Top + cut), new PointF(r.Right, r.Bottom),
                new PointF(r.Left + cut, r.Bottom), new PointF(r.Left, r.Bottom - cut)
            });
            p.CloseFigure();
            return p;
        }
    }

}
