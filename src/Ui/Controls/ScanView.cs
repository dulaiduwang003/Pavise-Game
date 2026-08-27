// @author bdth 2074055628@qq.com
// 文件用途 系统体检扫描过程的待机与运行动画
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
    internal enum AuditScanState
    {
        Idle = 0,
        Scanning = 1
    }

    internal sealed class ScanView : Control
    {
        private AuditScanState state = AuditScanState.Idle;
        private float phase;
        private float progress;
        private Motion shown;
        private Motion fill;
        private string caption = "";
        private string hint = "";

        public ScanView()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Opaque, true);
            BackColor = Theme.Bg;
            TabStop = false;
            shown.Speed = 0.18f;
            shown.Set(0f);
            fill.Speed = 0.12f;
            fill.Set(0f);
        }

        public string Caption
        {
            get { return caption; }
            set { string v = value ?? ""; if (caption == v) return; caption = v; Invalidate(); }
        }

        public string Hint
        {
            get { return hint; }
            set { string v = value ?? ""; if (hint == v) return; hint = v; Invalidate(); }
        }

        private float CoreRadius
        {
            get
            {
                float r = Math.Min(Width, Height) * 0.19f;
                return r < Theme.S(34) ? Theme.S(34) : r;
            }
        }

        public int RingCenterY { get { return (int)(Height * 0.38f); } }

        public int ContentBottom
        {
            get
            {
                int y = RingCenterY + (int)CoreRadius + Theme.S(22) + Theme.S(24);
                if (hint.Length > 0) y += Theme.S(26);
                y += Theme.S(30);
                return y;
            }
        }

        public void SetIdle(string text, string detail)
        {
            state = AuditScanState.Idle;
            Caption = text;
            Hint = detail;
            progress = 0f;
            fill.Set(0f);
            shown.To(1f);
            Animate(true);
        }

        public void BeginScan(string text)
        {
            state = AuditScanState.Scanning;
            Caption = text;
            Hint = "";
            progress = 0f;
            fill.Set(0f);
            phase = 0f;
            shown.Set(1f);
            Animate(true);
        }

        public void ReportProgress(float value, string text)
        {
            float v = value < 0f ? 0f : value > 1f ? 1f : value;
            bool dirty = Math.Abs(v - progress) > 0.0005f;
            progress = v;
            fill.To(v);
            if (text != null && text != caption) { caption = text; dirty = true; }
            if (dirty) Invalidate();
        }

        public void Stop() { Animate(false); }

        private void Animate(bool on)
        {
            UiClock.Frame -= OnFrame;
            if (!on || !Visible) return;
            UiClock.Frame += OnFrame;
            UiClock.Wake();
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            Animate(Visible);
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            UiClock.Frame -= OnFrame;
            base.OnHandleDestroyed(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) UiClock.Frame -= OnFrame;
            base.Dispose(disposing);
        }

        private void OnFrame(object sender, EventArgs e)
        {
            if (!Visible) { UiClock.Frame -= OnFrame; return; }
            phase += state == AuditScanState.Scanning ? 0.022f : 0.006f;
            if (phase > 1000f) phase -= 1000f;
            shown.Step();
            fill.Step();
            UiClock.Wake();
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            if (Backdrop.Active) Backdrop.PaintOnCard(g, this, ClientRectangle);
            else using (var back = new SolidBrush(Theme.Bg)) g.FillRectangle(back, ClientRectangle);
            if (Width <= 8 || Height <= 8) return;

            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            bool scanning = state == AuditScanState.Scanning;
            var frame = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath path = Theme.TechPath(frame, Theme.S(14)))
            {
                using (var bg = new LinearGradientBrush(frame,
                        Backdrop.CardFill(Theme.Inset), Backdrop.CardFill(Theme.Card),
                        LinearGradientMode.ForwardDiagonal))
                    g.FillPath(bg, path);
                g.SetClip(path);
                DrawHexField(g);
                DrawSlashes(g);
                if (scanning) DrawSweep(g);
                g.ResetClip();
                using (var border = new Pen(Col.Alpha(Theme.Stroke, scanning ? 255 : 190)))
                    g.DrawPath(border, path);
            }

            float cx = Width * 0.5f, cy = RingCenterY, r = CoreRadius;
            DrawBrackets(g, cx, cy, r);
            if (scanning) DrawArcs(g, cx, cy, r);
            DrawCore(g, cx, cy, r, scanning);
            DrawText(g, cy + r);
            if (scanning) DrawProgressBar(g, cy + r);
        }

        private void DrawHexField(Graphics g)
        {
            int side = Math.Max(Theme.S(17), 14);
            float w = side * 1.5f, h = (float)(Math.Sqrt(3.0) * side);
            using (var p = new Pen(Col.Alpha(Theme.StrokeHi, 16)))
            {
                int col = 0;
                for (float x = -side; x < Width + side; x += w, col++)
                {
                    float off = (col & 1) == 0 ? 0f : h * 0.5f;
                    for (float y = -h; y < Height + h; y += h)
                        DrawHex(g, p, x, y + off, side * 0.94f, 0f);
                }
            }
        }

        private static void DrawHex(Graphics g, Pen p, float cx, float cy, float r, float rot)
        {
            var pts = new PointF[6];
            for (int i = 0; i < 6; i++)
            {
                double a = rot + i * Math.PI / 3.0;
                pts[i] = new PointF(cx + (float)(Math.Cos(a) * r), cy + (float)(Math.Sin(a) * r));
            }
            g.DrawPolygon(p, pts);
        }

        private static void FillHex(Graphics g, Brush b, float cx, float cy, float r, float rot)
        {
            var pts = new PointF[6];
            for (int i = 0; i < 6; i++)
            {
                double a = rot + i * Math.PI / 3.0;
                pts[i] = new PointF(cx + (float)(Math.Cos(a) * r), cy + (float)(Math.Sin(a) * r));
            }
            g.FillPolygon(b, pts);
        }

        private void DrawSlashes(Graphics g)
        {
            int len = Math.Max(Theme.S(26), 20), gap = Math.Max(Theme.S(7), 6);
            int m = Theme.S(16);
            float drift = state == AuditScanState.Scanning ? (float)Math.Sin(phase * 2.4f) * Theme.S(3) : 0f;
            using (var p = new Pen(Col.Alpha(Theme.Accent, 70), Math.Max(2f, Theme.S(2))))
            {
                for (int i = 0; i < 3; i++)
                {
                    float o = i * gap + drift;
                    g.DrawLine(p, m + o, m + len, m + o + len, m);
                    g.DrawLine(p, Width - m - o, Height - m - len, Width - m - o - len, Height - m);
                }
            }
        }

        private void DrawSweep(Graphics g)
        {
            float travel = (phase * 0.42f) % 1f;
            int band = Math.Max(Theme.S(88), 56);
            int y = (int)(travel * (Height + band)) - band;
            var area = new Rectangle(0, y, Width, band);
            if (area.Height <= 0) return;
            using (var glow = new LinearGradientBrush(area,
                    Col.Alpha(Theme.Accent, 0), Col.Alpha(Theme.Accent, 40), LinearGradientMode.Vertical))
                g.FillRectangle(glow, area);
            using (var edge = new Pen(Col.Alpha(Theme.Accent, 190), Math.Max(1.5f, Theme.S(2))))
                g.DrawLine(edge, 0, area.Bottom, Width, area.Bottom);
            using (var hot = new Pen(Col.Alpha(Theme.Accent2, 120), 1f))
                g.DrawLine(hot, 0, area.Bottom - 1, Width, area.Bottom - 1);
        }

        private void DrawBrackets(Graphics g, float cx, float cy, float r)
        {
            float d = r * 1.62f;
            float arm = r * 0.42f;
            float pulse = state == AuditScanState.Scanning
                ? 1f + (float)Math.Sin(phase * 3.1f) * 0.035f : 1f;
            d *= pulse;
            using (var p = new Pen(Col.Alpha(Theme.StrokeHi, 200), Math.Max(1.5f, Theme.S(2))))
            {
                float l = cx - d, rt = cx + d, t = cy - d, b = cy + d;
                g.DrawLine(p, l, t, l + arm, t); g.DrawLine(p, l, t, l, t + arm);
                g.DrawLine(p, rt, t, rt - arm, t); g.DrawLine(p, rt, t, rt, t + arm);
                g.DrawLine(p, l, b, l + arm, b); g.DrawLine(p, l, b, l, b - arm);
                g.DrawLine(p, rt, b, rt - arm, b); g.DrawLine(p, rt, b, rt, b - arm);
            }
        }

        private void DrawArcs(Graphics g, float cx, float cy, float r)
        {
            DrawTickRing(g, cx, cy, r * 1.34f, phase * 42f, 48, 0.55f,
                Col.Alpha(Theme.Accent, 150), Math.Max(1.5f, Theme.S(2)));
            DrawTickRing(g, cx, cy, r * 1.14f, -phase * 78f, 30, 0.34f,
                Col.Alpha(Theme.Accent2, 120), Math.Max(1f, Theme.S(1)));

            var box = new RectangleF(cx - r * 1.34f, cy - r * 1.34f, r * 2.68f, r * 2.68f);
            using (var p = new Pen(Col.Alpha(Theme.Accent, 230), Math.Max(2.5f, Theme.S(3))))
            {
                p.StartCap = LineCap.Flat; p.EndCap = LineCap.Flat;
                g.DrawArc(p, box, phase * 42f % 360f, 46f);
            }
        }

        private void DrawTickRing(Graphics g, float cx, float cy, float r, float rot,
            int count, float duty, Color c, float w)
        {
            using (var p = new Pen(c, w))
            {
                float step = 360f / count;
                for (int i = 0; i < count; i++)
                {
                    double a0 = (rot + i * step) * Math.PI / 180.0;
                    double a1 = (rot + i * step + step * duty) * Math.PI / 180.0;
                    g.DrawLine(p,
                        cx + (float)(Math.Cos(a0) * r), cy + (float)(Math.Sin(a0) * r),
                        cx + (float)(Math.Cos(a1) * r), cy + (float)(Math.Sin(a1) * r));
                }
            }
        }

        private void DrawCore(Graphics g, float cx, float cy, float r, bool scanning)
        {
            float rot = scanning ? phase * 0.9f : phase * 0.25f;
            float breathe = scanning ? 1f + (float)Math.Sin(phase * 2.2f) * 0.045f : 1f;
            float rr = r * 0.72f * breathe;

            using (var glow = new GraphicsPath())
            {
                glow.AddEllipse(cx - rr * 1.9f, cy - rr * 1.9f, rr * 3.8f, rr * 3.8f);
                using (var br = new PathGradientBrush(glow))
                {
                    br.CenterColor = Col.Alpha(Theme.Accent, scanning ? 62 : 26);
                    br.SurroundColors = new[] { Col.Alpha(Theme.Accent, 0) };
                    g.FillPath(br, glow);
                }
            }

            using (var f = new SolidBrush(Col.Alpha(Theme.Accent, scanning ? 34 : 18)))
                FillHex(g, f, cx, cy, rr, rot);
            using (var p = new Pen(Col.Alpha(Theme.Accent, scanning ? 235 : 150), Math.Max(2f, Theme.S(3) * 0.85f)))
                DrawHex(g, p, cx, cy, rr, rot);
            using (var p2 = new Pen(Col.Alpha(Theme.Accent2, scanning ? 170 : 90), Math.Max(1f, Theme.S(1))))
                DrawHex(g, p2, cx, cy, rr * 0.62f, -rot * 1.6f);

            if (!scanning) return;
            float dot = Math.Max(2.5f, Theme.S(3)) * breathe;
            using (var b = new SolidBrush(Col.Alpha(Theme.Accent2, 220)))
                g.FillEllipse(b, cx - dot, cy - dot, dot * 2f, dot * 2f);
        }

        private void DrawProgressBar(Graphics g, float top)
        {
            int w = (int)(Width * 0.52f);
            if (w < Theme.S(140)) w = Math.Min(Width - Theme.S(40), Theme.S(140));
            int h = Math.Max(Theme.S(8), 7);
            int x = (Width - w) / 2;
            int y = (int)top + Theme.S(22) + Theme.S(24) + (hint.Length > 0 ? Theme.S(26) : 0);
            if (y + h > Height) return;

            var box = new Rectangle(x, y, w, h);
            using (GraphicsPath track = Skew(box, h / 2))
            {
                using (var b = new SolidBrush(Col.Alpha(Theme.Stroke, 150))) g.FillPath(b, track);
                g.SetClip(track);
                int done = (int)(w * fill.Value);
                if (done > 0)
                {
                    var lit = new Rectangle(x, y, done, h);
                    using (var b = new LinearGradientBrush(
                            new Rectangle(x, y, Math.Max(1, done), h),
                            Theme.Accent, Theme.Accent2, LinearGradientMode.Horizontal))
                        g.FillRectangle(b, lit);
                    using (var notch = new Pen(Col.Alpha(Theme.Inset, 200), Math.Max(1f, Theme.S(1))))
                        for (int nx = x + Theme.S(10); nx < x + done; nx += Theme.S(10))
                            g.DrawLine(notch, nx, y, nx - h, y + h);
                }
                g.ResetClip();
                using (var p = new Pen(Col.Alpha(Theme.StrokeHi, 210), 1f)) g.DrawPath(p, track);
            }
        }

        private static GraphicsPath Skew(Rectangle r, int cut)
        {
            var p = new GraphicsPath();
            p.AddPolygon(new[]
            {
                new Point(r.Left + cut, r.Top),
                new Point(r.Right, r.Top),
                new Point(r.Right - cut, r.Bottom),
                new Point(r.Left, r.Bottom)
            });
            return p;
        }

        private void DrawText(Graphics g, float ringBottom)
        {
            int y = (int)ringBottom + Theme.S(22);
            var area = new Rectangle(Theme.S(16), y, Width - Theme.S(32), Theme.S(24));
            Font f = Theme.UI(11f, true);
            using (var b = new SolidBrush(Col.Alpha(Theme.Fg, (int)(255 * Math.Max(0.25f, shown.Value)))))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
                g.DrawString(caption, f, b, area, sf);
            if (hint.Length == 0) return;
            var h = new Rectangle(Theme.S(24), y + Theme.S(24), Width - Theme.S(48), Theme.S(24));
            Font hf = Theme.UI(8.6f, false);
            using (var b = new SolidBrush(Col.Alpha(Theme.Dim, (int)(255 * Math.Max(0.25f, shown.Value)))))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter })
                g.DrawString(hint, hf, b, h, sf);
        }
    }
}
