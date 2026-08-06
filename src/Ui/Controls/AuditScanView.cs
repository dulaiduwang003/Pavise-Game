// @author bdth 2074055628@qq.com
// 文件用途 系统体检的待机提示与检测过程动画

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

    internal sealed class AuditScanView : Control
    {
        private AuditScanState state = AuditScanState.Idle;
        private float phase;
        private float progress;
        private Motion shown;
        private string caption = "";
        private string hint = "";

        public AuditScanView()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Opaque, true);
            BackColor = Theme.Bg;
            TabStop = false;
            shown.Speed = 0.18f;
            shown.Set(0f);
        }

        public string Caption
        {
            get { return caption; }
            set { string v = value ?? ""; if (caption == v) return; caption = v; Invalidate(); }
        }

        public int RingCenterY
        {
            get { return (int)(Height * 0.38f); }
        }

        public int ContentBottom
        {
            get
            {
                float r = Math.Min(Width, Height) * 0.19f;
                if (r < Theme.S(34)) r = Theme.S(34);
                int y = RingCenterY + (int)r + Theme.S(22) + Theme.S(24);
                if (hint.Length > 0) y += Theme.S(26);
                return y;
            }
        }

        public string Hint
        {
            get { return hint; }
            set { string v = value ?? ""; if (hint == v) return; hint = v; Invalidate(); }
        }

        public void SetIdle(string text, string detail)
        {
            state = AuditScanState.Idle;
            Caption = text;
            Hint = detail;
            progress = 0f;
            shown.To(1f);
            Animate(true);
        }

        public void BeginScan(string text)
        {
            state = AuditScanState.Scanning;
            Caption = text;
            Hint = "";
            progress = 0f;
            phase = 0f;
            shown.Set(1f);
            Animate(true);
        }

        public void ReportProgress(float value, string text)
        {
            float v = value < 0f ? 0f : value > 1f ? 1f : value;
            bool dirty = Math.Abs(v - progress) > 0.0005f;
            progress = v;
            if (text != null && text != caption) { caption = text; dirty = true; }
            if (dirty) Invalidate();
        }

        public void Stop()
        {
            Animate(false);
        }

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
            UiClock.Wake();
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            using (var back = new SolidBrush(Theme.Bg)) g.FillRectangle(back, ClientRectangle);
            if (Width <= 4 || Height <= 4) return;

            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            var frame = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath path = Theme.TechPath(frame, Theme.S(14)))
            {
                using (var fill = new LinearGradientBrush(frame, Theme.Inset, Theme.Card, LinearGradientMode.ForwardDiagonal))
                    g.FillPath(fill, path);
                using (var border = new Pen(Theme.Stroke)) g.DrawPath(border, path);
                g.SetClip(path);
                DrawGrid(g);
                if (state == AuditScanState.Scanning) DrawScanBand(g);
                g.ResetClip();
            }

            float cx = Width * 0.5f;
            float cy = RingCenterY;
            float radius = Math.Min(Width, Height) * 0.19f;
            if (radius < Theme.S(34)) radius = Theme.S(34);

            if (state == AuditScanState.Scanning) DrawScanRing(g, cx, cy, radius);
            else DrawIdleMark(g, cx, cy, radius);

            DrawText(g, cy + radius);
        }

        private void DrawGrid(Graphics g)
        {
            using (var p = new Pen(Color.FromArgb(10, 170, 185, 205)))
            {
                int gap = Math.Max(Theme.S(22), 18);
                for (int x = gap; x < Width; x += gap) g.DrawLine(p, x, 0, x, Height);
                for (int y = gap; y < Height; y += gap) g.DrawLine(p, 0, y, Width, y);
            }
        }

        private void DrawScanBand(Graphics g)
        {
            float travel = (phase * 0.42f) % 1f;
            int band = Math.Max(Theme.S(72), 48);
            int y = (int)(travel * (Height + band)) - band;
            var area = new Rectangle(0, y, Width, band);
            if (area.Height <= 0) return;
            using (var glow = new LinearGradientBrush(area,
                Col.Alpha(Theme.Accent, 0), Col.Alpha(Theme.Accent, 34), LinearGradientMode.Vertical))
                g.FillRectangle(glow, area);
            using (var edge = new Pen(Col.Alpha(Theme.Accent, 132), Math.Max(1f, Theme.S(1))))
                g.DrawLine(edge, 0, area.Bottom, Width, area.Bottom);
        }

        private void DrawScanRing(Graphics g, float cx, float cy, float r)
        {
            var ring = new RectangleF(cx - r, cy - r, r * 2f, r * 2f);
            float thickness = Math.Max(2.5f, Theme.S(4));

            using (var track = new Pen(Col.Alpha(Theme.Stroke, 190), thickness)) g.DrawEllipse(track, ring);

            for (int i = 0; i < 60; i++)
            {
                double a = (i * 6 - 90) * Math.PI / 180.0;
                bool major = i % 5 == 0;
                float inner = r + Theme.S(7);
                float len = major ? Theme.S(7) : Theme.S(3);
                Color tc = i / 60f <= progress
                    ? Col.Alpha(Theme.Accent, major ? 210 : 110)
                    : Col.Alpha(Theme.Faint, major ? 96 : 44);
                using (var p = new Pen(tc, major ? Math.Max(1.3f, Theme.S(1)) : 1f))
                    g.DrawLine(p,
                        cx + (float)Math.Cos(a) * inner, cy + (float)Math.Sin(a) * inner,
                        cx + (float)Math.Cos(a) * (inner + len), cy + (float)Math.Sin(a) * (inner + len));
            }

            float sweep = progress * 360f;
            if (sweep > 0.5f)
                using (var arc = new Pen(Theme.Accent, thickness))
                {
                    arc.StartCap = LineCap.Round;
                    arc.EndCap = LineCap.Round;
                    g.DrawArc(arc, ring, -90f, sweep);
                }

            float spin = phase * 190f;
            using (var lead = new Pen(Col.Alpha(Theme.Accent2, 150), Math.Max(1.4f, Theme.S(2))))
            {
                lead.StartCap = LineCap.Round;
                lead.EndCap = LineCap.Round;
                var inner = new RectangleF(cx - r * 0.72f, cy - r * 0.72f, r * 1.44f, r * 1.44f);
                g.DrawArc(lead, inner, spin, 74f);
                g.DrawArc(lead, inner, spin + 180f, 42f);
            }

            float pulse = 0.5f + 0.5f * (float)Math.Sin(phase * 5.2f);
            using (var halo = new Pen(Col.Alpha(Theme.Accent, (int)(16 + pulse * 26)), Math.Max(2f, Theme.S(3))))
                g.DrawEllipse(halo, cx - r - Theme.S(6), cy - r - Theme.S(6), (r + Theme.S(6)) * 2f, (r + Theme.S(6)) * 2f);

            string pct = (int)Math.Round(progress * 100f) + "%";
            var box = new Rectangle((int)(cx - r), (int)(cy - Theme.S(14)), (int)(r * 2f), Theme.S(28));
            TextRenderer.DrawText(g, pct, Theme.Mono(15f), box, Theme.Fg,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        private void DrawIdleMark(Graphics g, float cx, float cy, float r)
        {
            float breath = 0.5f + 0.5f * (float)Math.Sin(phase * 2.6f);
            var ring = new RectangleF(cx - r, cy - r, r * 2f, r * 2f);

            using (var halo = new Pen(Col.Alpha(Theme.Accent, (int)(10 + breath * 20)), Math.Max(2f, Theme.S(3))))
                g.DrawEllipse(halo, cx - r - Theme.S(7), cy - r - Theme.S(7), (r + Theme.S(7)) * 2f, (r + Theme.S(7)) * 2f);
            using (var track = new Pen(Col.Alpha(Theme.Stroke, 190), Math.Max(2f, Theme.S(3))))
                g.DrawEllipse(track, ring);
            using (var arc = new Pen(Col.Alpha(Theme.Accent, 150), Math.Max(2f, Theme.S(3))))
            {
                arc.StartCap = LineCap.Round;
                arc.EndCap = LineCap.Round;
                g.DrawArc(arc, ring, phase * 42f, 58f);
            }

            using (var pen = new Pen(Col.Alpha(Theme.Accent, 210), Math.Max(1.6f, Theme.S(2))))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                float s = r * 0.42f;
                g.DrawLine(pen, cx - s, cy + s * 0.15f, cx - s * 0.28f, cy + s * 0.72f);
                g.DrawLine(pen, cx - s * 0.28f, cy + s * 0.72f, cx + s, cy - s * 0.66f);
            }
        }

        private void DrawText(Graphics g, float top)
        {
            int y = (int)top + Theme.S(22);
            var title = new Rectangle(Theme.S(24), y, Width - Theme.S(48), Theme.S(24));
            TextRenderer.DrawText(g, caption, Theme.UI(10.5f, true), title, Theme.Fg,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            if (hint.Length == 0) return;
            var detail = new Rectangle(Theme.S(24), y + Theme.S(26), Width - Theme.S(48), Theme.S(20));
            TextRenderer.DrawText(g, hint, Theme.UI(8.6f, false), detail, Theme.Dim,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        }
    }
}
