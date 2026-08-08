// @author bdth 2074055628@qq.com
// 文件用途 绘制首页核心状态和动态效果

using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace PaviseApp
{
    internal sealed class PaviseCore : Control
    {
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly Timer timer;
        private PerformancePreset mode = PerformancePreset.Standard;
        private bool guardEnabled = true;
        private bool gameActive;
        private bool animationRequested;
        private Bitmap staticLayer;
        private Color cachedAccent = Color.Empty, cachedAccent2 = Color.Empty;
        private static readonly int selfPid;

        static PaviseCore()
        {
            using (Process self = Process.GetCurrentProcess()) selfPid = self.Id;
        }

        public PaviseCore()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Opaque, true);
            BackColor = Theme.Bg;
            Cursor = Cursors.Default;
            timer = new Timer();
            timer.Interval = 33;
            timer.Tick += delegate
            {
                if (!CanAnimate()) { timer.Stop(); return; }
                SyncFrameInterval();
                Invalidate();
            };
        }

        public void SetState(PerformancePreset value, bool enabled, bool active)
        {
            if (mode == value && guardEnabled == enabled && gameActive == active) return;
            mode = value; guardEnabled = enabled; gameActive = active;

            SyncFrameInterval();
            DropCache();
            Invalidate();
        }

        public void SetAnimationEnabled(bool value)
        {
            animationRequested = value;
            SyncAnimationTimer();
            if (value) Invalidate();
        }

        internal void NotifyForegroundChanged()
        {
            SyncFrameInterval();
            SyncAnimationTimer();
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            SyncAnimationTimer();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            SyncAnimationTimer();
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            timer.Stop();
            DropCache();
            base.OnHandleDestroyed(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { timer.Dispose(); DropCache(); }
            base.Dispose(disposing);
        }

        internal bool AnimationTimerEnabled
        {
            get { return timer.Enabled; }
        }

        internal static bool ShouldAnimate(bool requested, bool handleCreated, bool controlVisible,
            bool formVisible, FormWindowState windowState)
        {
            return requested && handleCreated && controlVisible && formVisible
                && windowState != FormWindowState.Minimized;
        }

        internal const int ActiveFrameMs = 33;
        internal const int BackgroundFrameMs = 500;
        internal const int GameBackgroundFrameMs = 2000;

        internal static int DesiredFrameInterval(bool gameActive, bool selfForeground)
        {
            if (selfForeground) return ActiveFrameMs;
            return gameActive ? GameBackgroundFrameMs : BackgroundFrameMs;
        }

        private void SyncFrameInterval()
        {
            int next = DesiredFrameInterval(gameActive,
                GameSessionDetector.ForegroundPid() == selfPid);
            if (timer.Interval != next) timer.Interval = next;
        }

        private bool CanAnimate()
        {
            Form f = FindForm();
            return ShouldAnimate(animationRequested, IsHandleCreated, Visible,
                f != null && f.Visible, f == null ? FormWindowState.Minimized : f.WindowState);
        }

        private void SyncAnimationTimer()
        {
            if (CanAnimate()) timer.Start(); else timer.Stop();
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            DropCache();
            base.OnSizeChanged(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Color accent = guardEnabled ? Theme.Accent : Col.Lerp(Theme.Faint, Theme.Accent, 0.55f);
            Color accent2 = guardEnabled ? Theme.Accent2 : Theme.Faint;
            EnsureStaticLayer(accent, accent2);

            Graphics g = e.Graphics;
            g.DrawImageUnscaled(staticLayer, 0, 0);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            double seconds = clock.Elapsed.TotalSeconds;
            float speed = gameActive ? 1.45f : guardEnabled ? 0.72f : 0.28f;
            float spin = (float)(seconds * 14.0 * speed);

            float cx = Width * 0.50f;
            float cy = Height * 0.49f;
            float radius = Math.Min(Width, Height) * 0.355f;
            float energy = gameActive ? 1f : guardEnabled ? 0.72f : 0.34f;
            float breath = 0.5f + 0.5f * (float)Math.Sin(seconds * (gameActive ? 4.8 : 2.5));

            DrawRadarSweep(g, cx, cy, radius * 0.94f, spin * 0.86f, accent, energy);
            DrawMotionRing(g, Ring(cx, cy, radius * 1.015f), -spin * 0.33f, accent, 24, 5.2f, energy);
            DrawMotionRing(g, Ring(cx, cy, radius * 0.79f), spin * 0.57f, accent2, 12, 8.5f, energy * 0.88f);
            DrawPulseWaves(g, cx, cy, radius, seconds, accent, energy);
            DrawOrbiters(g, cx, cy, radius, spin, accent, accent2, energy);
            DrawSweep(g, Ring(cx, cy, radius * 0.92f), spin * 2.25f, accent);
            DrawKineticBlades(g, cx, cy, radius, -spin * 1.12f, accent, energy);
            DrawCoreEnergy(g, cx, cy, radius * 0.43f, spin, breath, accent, energy);
        }

        private void EnsureStaticLayer(Color accent, Color accent2)
        {
            if (staticLayer != null && staticLayer.Width == Width && staticLayer.Height == Height &&
                cachedAccent == accent && cachedAccent2 == accent2) return;
            DropCache();
            if (Width <= 0 || Height <= 0) return;
            staticLayer = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb);
            cachedAccent = accent; cachedAccent2 = accent2;
            using (Graphics g = Graphics.FromImage(staticLayer))
            {
                g.Clear(Theme.Bg);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                Rectangle frame = new Rectangle(0, 0, Width - 1, Height - 1);
                using (GraphicsPath fp = Theme.TechPath(frame, Theme.S(15)))
                {
                    using (var fill = new LinearGradientBrush(frame, Theme.Inset, Theme.Card, LinearGradientMode.ForwardDiagonal))
                        g.FillPath(fill, fp);
                    using (var border = new Pen(Theme.Stroke)) g.DrawPath(border, fp);
                    g.SetClip(fp);
                    DrawGrid(g);
                    g.ResetClip();
                }

                float cx = Width * 0.50f, cy = Height * 0.49f;
                float radius = Math.Min(Width, Height) * 0.355f;
                DrawAmbientGlow(g, cx, cy, radius, accent, guardEnabled ? 1f : 0.52f);
                DrawTicks(g, cx, cy, radius, 0f, accent);
                DrawSegmentRing(g, Ring(cx, cy, radius), 0f, accent, 3.1f, 16, 12f, 10f);
                DrawSegmentRing(g, Ring(cx, cy, radius * 0.82f), 7f, Col.Lerp(accent, accent2, 0.32f), 2f, 12, 17f, 13f);
                DrawCircuitRing(g, cx, cy, radius * 0.65f, 0f, accent);
                DrawCore(g, cx, cy, radius * 0.43f, accent, accent2);
                DrawCoreLabels(g, cx, cy, accent);
                DrawFrameDetails(g, accent);
            }
        }

        private void DropCache()
        {
            if (staticLayer != null) { staticLayer.Dispose(); staticLayer = null; }
            cachedAccent = cachedAccent2 = Color.Empty;
        }

        private void DrawGrid(Graphics g)
        {
            using (var p = new Pen(Color.FromArgb(11, 170, 185, 205)))
            {
                int gap = Math.Max(Theme.S(18), 18);
                for (int x = gap; x < Width; x += gap) g.DrawLine(p, x, 0, x, Height);
                for (int y = gap; y < Height; y += gap) g.DrawLine(p, 0, y, Width, y);
            }
            using (var p = new Pen(Color.FromArgb(24, 150, 160, 178)))
            {
                g.DrawLine(p, Width / 2, Theme.S(20), Width / 2, Height - Theme.S(20));
                g.DrawLine(p, Theme.S(20), Height / 2, Width - Theme.S(20), Height / 2);
            }
        }

        private static RectangleF Ring(float cx, float cy, float r)
        {
            return new RectangleF(cx - r, cy - r, r * 2f, r * 2f);
        }

        private static void DrawAmbientGlow(Graphics g, float cx, float cy, float r, Color c, float power)
        {
            for (int i = 8; i >= 1; i--)
            {
                float grow = i * r * 0.035f;
                int alpha = (int)((9 - i) * 1.55f * power);
                using (var p = new Pen(Col.Alpha(c, alpha), Math.Max(2f, grow * 0.62f)))
                    g.DrawEllipse(p, cx - r - grow, cy - r - grow, (r + grow) * 2, (r + grow) * 2);
            }
        }

        private static void DrawTicks(Graphics g, float cx, float cy, float r, float rotation, Color c)
        {
            for (int i = 0; i < 96; i++)
            {
                double a = (i * 3.75 + rotation - 90) * Math.PI / 180.0;
                bool major = i % 8 == 0;
                bool medium = i % 4 == 0;
                float len = major ? Theme.S(9) : medium ? Theme.S(6) : Theme.S(3);
                float inner = r + Theme.S(4);
                float outer = inner + len;
                Color tc = major ? Col.Alpha(c, 220) : medium ? Col.Alpha(c, 120) : Col.Alpha(Theme.Dim, 54);
                using (var p = new Pen(tc, major ? Math.Max(1.4f, Theme.S(1)) : 1f))
                    g.DrawLine(p,
                        cx + (float)Math.Cos(a) * inner, cy + (float)Math.Sin(a) * inner,
                        cx + (float)Math.Cos(a) * outer, cy + (float)Math.Sin(a) * outer);
            }
        }

        private static void DrawSegmentRing(Graphics g, RectangleF ring, float rotation, Color c, float width,
            int count, float sweep, float gap)
        {
            using (var p = new Pen(Col.Alpha(c, 196), Math.Max(1.2f, Theme.S((int)Math.Round(width)))))
            {
                p.StartCap = LineCap.Flat; p.EndCap = LineCap.Flat;
                float pitch = sweep + gap;
                for (int i = 0; i < count; i++) g.DrawArc(p, ring, rotation + i * pitch, sweep);
            }
        }

        private static void DrawCircuitRing(Graphics g, float cx, float cy, float r, float rotation, Color c)
        {
            using (var thin = new Pen(Col.Alpha(c, 88), 1f))
            using (var node = new SolidBrush(Col.Alpha(c, 210)))
            {
                for (int i = 0; i < 12; i++)
                {
                    double a1 = (rotation + i * 30 - 90) * Math.PI / 180.0;
                    double a2 = (rotation + i * 30 + 18 - 90) * Math.PI / 180.0;
                    float x1 = cx + (float)Math.Cos(a1) * r;
                    float y1 = cy + (float)Math.Sin(a1) * r;
                    float x2 = cx + (float)Math.Cos(a2) * r;
                    float y2 = cy + (float)Math.Sin(a2) * r;
                    g.DrawLine(thin, x1, y1, x2, y2);
                    if ((i & 1) == 0) g.FillRectangle(node, x1 - Theme.S(2), y1 - Theme.S(2), Theme.S(4), Theme.S(4));
                }
            }
        }

        private static void DrawSweep(Graphics g, RectangleF ring, float head, Color c)
        {
            float width = Math.Max(1.4f, Theme.S(2));
            using (var hot = new Pen(Col.Alpha(c, 190), width)) g.DrawArc(hot, ring, head - 6f, 8f);
            using (var warm = new Pen(Col.Alpha(c, 112), width)) g.DrawArc(warm, ring, head - 17f, 10f);
            using (var tail = new Pen(Col.Alpha(c, 46), width)) g.DrawArc(tail, ring, head - 31f, 13f);
            double a = head * Math.PI / 180.0;
            float cx = ring.X + ring.Width / 2f, cy = ring.Y + ring.Height / 2f, r = ring.Width / 2f;
            float x = cx + (float)Math.Cos(a) * r, y = cy + (float)Math.Sin(a) * r;
            using (var glow = new SolidBrush(Col.Alpha(c, 58))) g.FillEllipse(glow, x - Theme.S(7), y - Theme.S(7), Theme.S(14), Theme.S(14));
            using (var dot = new SolidBrush(c)) g.FillEllipse(dot, x - Theme.S(2), y - Theme.S(2), Theme.S(4), Theme.S(4));
        }

        private static void DrawRadarSweep(Graphics g, float cx, float cy, float r, float head, Color c, float energy)
        {
            using (var trail = new Pen(Col.Alpha(c, (int)(13 * energy)), Math.Max(1f, Theme.S(1))))
            {
                for (int i = 10; i >= 1; i--)
                {
                    float angle = head - i * 3.1f;
                    double a = (angle - 90f) * Math.PI / 180.0;
                    float inner = r * 0.47f;
                    g.DrawLine(trail,
                        cx + (float)Math.Cos(a) * inner, cy + (float)Math.Sin(a) * inner,
                        cx + (float)Math.Cos(a) * r, cy + (float)Math.Sin(a) * r);
                }
            }

            double h = (head - 90f) * Math.PI / 180.0;
            using (var p = new Pen(Col.Alpha(c, (int)(138 * energy)), Math.Max(1f, Theme.S(1))))
                g.DrawLine(p, cx, cy, cx + (float)Math.Cos(h) * r, cy + (float)Math.Sin(h) * r);
        }

        private static void DrawMotionRing(Graphics g, RectangleF ring, float rotation, Color c,
            int count, float sweep, float energy)
        {
            float pitch = 360f / count;
            using (var major = new Pen(Col.Alpha(c, (int)(184 * energy)), Math.Max(1.3f, Theme.S(2))))
            using (var minor = new Pen(Col.Alpha(c, (int)(72 * energy)), Math.Max(1f, Theme.S(1))))
            {
                for (int i = 0; i < count; i++)
                    g.DrawArc(i % 3 == 0 ? major : minor, ring, rotation + i * pitch,
                        sweep + (i % 4 == 0 ? 2.8f : 0f));
            }
        }

        private static void DrawPulseWaves(Graphics g, float cx, float cy, float r,
            double seconds, Color c, float energy)
        {
            for (int i = 0; i < 2; i++)
            {
                double raw = seconds * 0.38 + i * 0.5;
                float phase = (float)(raw - Math.Floor(raw));
                float pr = r * (0.45f + phase * 0.46f);
                int alpha = (int)((1f - phase) * 42f * energy);
                using (var p = new Pen(Col.Alpha(c, alpha), Math.Max(1f, Theme.S(1))))
                    g.DrawEllipse(p, Ring(cx, cy, pr));
            }
        }

        private static void DrawOrbiters(Graphics g, float cx, float cy, float r, float spin,
            Color a, Color b, float energy)
        {
            float[] radii = { 1.08f, 0.72f, 0.56f };
            float[] rates = { 0.72f, -1.18f, 1.66f };
            for (int orbit = 0; orbit < radii.Length; orbit++)
            {
                float angle = spin * rates[orbit] + orbit * 119f;
                Color c = orbit == 1 ? b : a;
                float rr = r * radii[orbit];
                using (var trail = new Pen(Col.Alpha(c, (int)(54 * energy)), Math.Max(1f, Theme.S(1))))
                    g.DrawArc(trail, Ring(cx, cy, rr), angle - 19f, 19f);
                double rad = (angle - 90f) * Math.PI / 180.0;
                float x = cx + (float)Math.Cos(rad) * rr;
                float y = cy + (float)Math.Sin(rad) * rr;
                float size = Theme.S(4);
                using (var brush = new SolidBrush(Col.Alpha(c, (int)(225 * energy))))
                    g.FillEllipse(brush, x - size / 2f, y - size / 2f, size, size);
            }
        }

        private static void DrawCoreEnergy(Graphics g, float cx, float cy, float r, float spin,
            float breath, Color c, float energy)
        {
            float grow = Theme.S(2) + Theme.S(3) * breath;
            int glowAlpha = (int)((18f + breath * 24f) * energy);
            using (var glow = new Pen(Col.Alpha(c, glowAlpha), Math.Max(2f, Theme.S(3))))
                g.DrawEllipse(glow, Ring(cx, cy, r + grow));

            RectangleF track = Ring(cx, cy, r * 0.88f);
            using (var head = new Pen(Col.Alpha(c, (int)(185 * energy)), Math.Max(1.5f, Theme.S(2))))
            {
                head.StartCap = LineCap.Round;
                head.EndCap = LineCap.Round;
                g.DrawArc(head, track, -spin * 1.32f, 34f + breath * 18f);
                g.DrawArc(head, track, 180f - spin * 1.32f, 18f + breath * 12f);
            }
        }

        private static void DrawKineticBlades(Graphics g, float cx, float cy, float r,
            float rotation, Color c, float energy)
        {
            RectangleF outer = Ring(cx, cy, r * 0.58f);
            RectangleF inner = Ring(cx, cy, r * 0.52f);
            using (var bright = new Pen(Col.Alpha(c, (int)(158 * energy)), Math.Max(1.3f, Theme.S(2))))
            using (var dim = new Pen(Col.Alpha(c, (int)(64 * energy)), Math.Max(1f, Theme.S(1))))
            {
                for (int i = 0; i < 4; i++)
                {
                    float at = rotation + i * 90f;
                    g.DrawArc(bright, outer, at, 13f);
                    g.DrawArc(dim, inner, at + 24f, 18f);
                }
            }
        }

        private static void DrawCore(Graphics g, float cx, float cy, float r, Color a, Color b)
        {
            RectangleF rr = Ring(cx, cy, r);
            using (var fill = new SolidBrush(Color.FromArgb(224, Theme.Inset))) g.FillEllipse(fill, rr);
            using (var border = new Pen(Col.Alpha(a, 178), Math.Max(1.4f, Theme.S(1)))) g.DrawEllipse(border, rr);
            using (var inner = new Pen(Col.Alpha(a, 48), Math.Max(1f, Theme.S(1))))
                g.DrawEllipse(inner, Ring(cx, cy, r * 0.82f));

            float u = Math.Max(0.7f, r / 62f);
            PointF[] bolt = {
                new PointF(cx + 7*u, cy - 31*u), new PointF(cx - 18*u, cy + 1*u),
                new PointF(cx - 5*u, cy + 1*u), new PointF(cx - 13*u, cy + 30*u),
                new PointF(cx + 20*u, cy - 10*u), new PointF(cx + 5*u, cy - 10*u)
            };
            using (var brush = new LinearGradientBrush(new RectangleF(cx - r, cy - r, r * 2, r * 2), a, b, LinearGradientMode.Vertical))
                g.FillPolygon(brush, bolt);
        }

        private void DrawCoreLabels(Graphics g, float cx, float cy, Color accent)
        {
            string state = gameActive ? "ACTIVE" : guardEnabled ? "STANDBY" : "OFFLINE";
            Color stateColor = gameActive ? Theme.Green : guardEnabled ? accent : Theme.Faint;
            Rectangle top = new Rectangle((int)(cx - Theme.S(70)), (int)(cy - Theme.S(67)), Theme.S(140), Theme.S(16));
            TextRenderer.DrawText(g, "PAVISE CORE", Theme.Mono(7f), top, Theme.Dim,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            Rectangle modeBox = new Rectangle((int)(cx - Theme.S(76)), (int)(cy + Theme.S(42)), Theme.S(152), Theme.S(22));
            TextRenderer.DrawText(g, ModeButton.ModeName(mode), Theme.UI(10f, true), modeBox, Theme.Fg,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            Rectangle stateBox = new Rectangle((int)(cx - Theme.S(70)), (int)(cy + Theme.S(63)), Theme.S(140), Theme.S(16));
            TextRenderer.DrawText(g, state, Theme.Mono(6.75f), stateBox, stateColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        private void DrawFrameDetails(Graphics g, Color accent)
        {
            TextRenderer.DrawText(g, "SYS // 01", Theme.Mono(6.5f),
                new Rectangle(Theme.S(16), Theme.S(12), Theme.S(90), Theme.S(14)), Theme.Faint,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, gameActive ? "LIVE LINK" : "READY", Theme.Mono(6.5f),
                new Rectangle(Width - Theme.S(104), Theme.S(12), Theme.S(88), Theme.S(14)), gameActive ? Theme.Green : Theme.Faint,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            using (var p = new Pen(accent, Math.Max(1f, Theme.S(1))))
            {
                g.DrawLine(p, Theme.S(1), Theme.S(1), Theme.S(72), Theme.S(1));
                g.DrawLine(p, Width - Theme.S(18), Theme.S(2), Width - Theme.S(2), Theme.S(18));
            }
        }
    }
}
