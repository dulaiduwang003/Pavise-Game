// 作者：bdth 2074055628@qq.com
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace PaviseApp
{
    internal sealed partial class PaviseCore : Control
    {
        private readonly Stopwatch clock = new Stopwatch();
        private readonly Timer timer;
        private PerformancePreset mode = PerformancePreset.Standard;
        private bool guardEnabled = true;
        private bool gameActive;
        private bool animationRequested;
        private bool foreground = true;
        private Bitmap staticLayer;
        private Bitmap foregroundLayer;
        private Bitmap hubLayer;
        private bool cachedLight;
        private Color cachedAccent = Color.Empty, cachedAccent2 = Color.Empty;

        public PaviseCore()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Opaque, true);
            BackColor = Theme.Bg;
            Cursor = Cursors.Default;
            timer = new Timer();
            timer.Interval = FrameMs;
            timer.Tick += delegate
            {
                if (!CanAnimate()) { timer.Stop(); clock.Stop(); return; }
                Invalidate();
            };
        }

        public void SetState(PerformancePreset value, bool enabled, bool active)
        {
            if (mode == value && guardEnabled == enabled && gameActive == active) return;
            mode = value; guardEnabled = enabled; gameActive = active;

            SyncAnimationTimer();
            DropCache();
            Invalidate();
        }

        public void RefreshVisual()
        {
            DropCache();
            Invalidate();
        }

        public void SetAnimationEnabled(bool value)
        {
            animationRequested = value;
            SyncAnimationTimer();
            if (value) Invalidate();
        }

        public void SetForeground(bool value)
        {
            if (foreground == value) return;
            foreground = value;
            SyncAnimationTimer();
            if (value) Invalidate();
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
            clock.Stop();
            DropCache();
            base.OnHandleDestroyed(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { timer.Dispose(); DropCache(); }
            base.Dispose(disposing);
        }

        internal static bool ShouldAnimate(bool requested, bool handleCreated, bool controlVisible,
            bool formVisible, FormWindowState windowState, bool gameActive)
        {
            return ShouldAnimate(requested, handleCreated, controlVisible, formVisible, windowState, gameActive, true);
        }

        internal static bool ShouldAnimate(bool requested, bool handleCreated, bool controlVisible,
            bool formVisible, FormWindowState windowState, bool gameActive, bool foregroundApp)
        {
            return requested && handleCreated && controlVisible && formVisible
                && windowState != FormWindowState.Minimized && !gameActive && foregroundApp;
        }

        internal const int FrameMs = UiClock.FrameMs;

        private bool CanAnimate()
        {
            Form f = FindForm();
            return ShouldAnimate(animationRequested, IsHandleCreated, Visible,
                f != null && f.Visible, f == null ? FormWindowState.Minimized : f.WindowState,
                gameActive, foreground);
        }

        private void SyncAnimationTimer()
        {
            if (CanAnimate()) { clock.Start(); timer.Start(); }
            else { timer.Stop(); clock.Stop(); }
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            DropCache();
            base.OnSizeChanged(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            RenderFrame(e.Graphics, clock.Elapsed.TotalSeconds);
        }

        private void RenderFrame(Graphics g, double seconds)
        {
            if (Width < 2 || Height < 2) return;
            Color accent = guardEnabled ? Theme.Accent : Col.Lerp(Theme.Faint, Theme.Accent, 0.55f);
            Color accent2 = guardEnabled ? Theme.Accent2 : Theme.Faint;
            EnsureStaticLayer(accent, accent2);

            if (Backdrop.AppliesTo(this)) Backdrop.Paint(g, this, ClientRectangle);
            g.DrawImageUnscaled(staticLayer, 0, 0);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            if (mode == PerformancePreset.Standard)
            {
                GraphicsState smart = BeginDesign(g);
                DrawSmartMotion(g, seconds * (guardEnabled ? 1.0 : .35), accent);
                g.Restore(smart);
                return;
            }
            GraphicsState saved = BeginDesign(g);
            double motionSeconds = seconds * (guardEnabled ? 1.0 : 0.35);
            DrawModeMotion(g, motionSeconds, accent);
            g.Restore(saved);
            float hover = mode == PerformancePreset.Handheld ? (float)Math.Sin(motionSeconds * Math.PI / 3) * 2.2f * DesignScale : 0;
            if (mode == PerformancePreset.Handheld)
            {
                Rectangle source = Rectangle.Ceiling(new RectangleF(Width * .5f - 83 * DesignScale,
                    Height * .44f - 42 * DesignScale, 166 * DesignScale, 87 * DesignScale));
                g.InterpolationMode = InterpolationMode.Bilinear;
                g.DrawImage(hubLayer, new RectangleF(source.X, source.Y + hover, source.Width, source.Height),
                    source, GraphicsUnit.Pixel);
            }
            else g.DrawImageUnscaled(hubLayer, 0, 0);
            saved = BeginDesign(g);
            DrawFrontLight(g, motionSeconds, accent);
            g.Restore(saved);
            g.DrawImageUnscaled(foregroundLayer, 0, 0);
        }

        private bool cachedBackdrop;

        private void EnsureStaticLayer(Color accent, Color accent2)
        {
            bool backdrop = Backdrop.AppliesTo(this);
            if (staticLayer != null && staticLayer.Width == Width && staticLayer.Height == Height &&
                cachedAccent == accent && cachedAccent2 == accent2 && cachedBackdrop == backdrop && cachedLight == Theme.LightMode) return;
            DropCache();
            if (Width <= 0 || Height <= 0) return;
            staticLayer = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb);
            if (mode != PerformancePreset.Standard)
            {
                foregroundLayer = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb);
                hubLayer = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb);
            }
            cachedAccent = accent; cachedAccent2 = accent2; cachedBackdrop = backdrop; cachedLight = Theme.LightMode;
            using (Graphics g = Graphics.FromImage(staticLayer))
            {
                g.Clear(backdrop ? Color.Transparent : Theme.Bg);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                Rectangle frame = new Rectangle(0, 0, Width - 1, Height - 1);
                using (GraphicsPath fp = Theme.TechPath(frame, Theme.S(15)))
                {
                    Color frameA = Backdrop.CardFill(this, Theme.LightMode ? Col.Lerp(Theme.Inset, accent, 0.055f) : Theme.Inset);
                    Color frameB = Backdrop.CardFill(this, Theme.LightMode ? Col.Lerp(Theme.Card, accent, 0.018f) : Theme.Card);
                    using (var fill = new LinearGradientBrush(frame, frameA, frameB, LinearGradientMode.ForwardDiagonal))
                        g.FillPath(fill, fp);
                    using (var border = new Pen(Theme.Stroke)) g.DrawPath(border, fp);
                    g.SetClip(fp);
                    DrawGrid(g);
                    g.ResetClip();
                }

                if (mode == PerformancePreset.Standard)
                {
                    GraphicsState saved = BeginDesign(g);
                    DrawSmartStructure(g, accent);
                    g.Restore(saved);
                    DrawCoreLabels(g, Width * .5f, Height * .44f, accent);
                    DrawFrameDetails(g, accent);
                }
                else
                {
                    GraphicsState saved = BeginDesign(g);
                    DrawModeStructure(g, accent);
                    g.Restore(saved);
                }
            }
            if (mode == PerformancePreset.Standard) return;
            using (Graphics g = Graphics.FromImage(hubLayer))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                GraphicsState saved = BeginDesign(g);
                DrawModeHub(g, accent, accent2);
                g.Restore(saved);
            }
            using (Graphics g = Graphics.FromImage(foregroundLayer))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                DrawCoreLabels(g, Width * .5f, Height * .44f, accent);
                DrawFrameDetails(g, accent);
            }
        }

        private void DropCache()
        {
            if (staticLayer != null) { staticLayer.Dispose(); staticLayer = null; }
            if (foregroundLayer != null) { foregroundLayer.Dispose(); foregroundLayer = null; }
            if (hubLayer != null) { hubLayer.Dispose(); hubLayer = null; }
            cachedAccent = cachedAccent2 = Color.Empty;
        }

        private void DrawGrid(Graphics g)
        {
            using (var p = new Pen(Color.FromArgb(5, 170, 185, 205)))
            {
                int gap = Math.Max(Theme.S(18), 18);
                for (int x = gap; x < Width; x += gap) g.DrawLine(p, x, 0, x, Height);
                for (int y = gap; y < Height; y += gap) g.DrawLine(p, 0, y, Width, y);
            }
            using (var p = new Pen(Color.FromArgb(10, 150, 160, 178)))
            {
                g.DrawLine(p, Width / 2, Theme.S(20), Width / 2, Height - Theme.S(20));
                g.DrawLine(p, Theme.S(20), Height / 2, Width - Theme.S(20), Height / 2);
            }
        }

        private void DrawCoreLabels(Graphics g, float cx, float cy, Color accent)
        {
            float u = DesignScale;
            string state = gameActive ? "ACTIVE" : guardEnabled ? "STANDBY" : "OFFLINE";
            float stateY = mode == PerformancePreset.Competitive || mode == PerformancePreset.Standard ? 106 : 50;
            LabelText(g, ModeButton.ModeName(mode), Theme.UI(13f, true), Theme.Fg,
                new RectangleF(0, Height - Theme.S(65), Width, Theme.S(25)));
            LabelText(g, state, Theme.Mono(6f), gameActive ? Theme.Green : guardEnabled ? accent : Theme.Faint,
                new RectangleF(cx - 42*u, cy + stateY*u, 84*u, 10*u));
        }

        private static void LabelText(Graphics g, string text, Font font, Color color, RectangleF box)
        {
            using (var brush = new SolidBrush(color))
            using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
                g.DrawString(text, font, brush, box, format);
        }

        private void DrawFrameDetails(Graphics g, Color accent)
        {
            string code = mode == PerformancePreset.Extreme ? "04 / ARC REACTOR"
                : mode == PerformancePreset.Competitive ? "02 / TARGET LOCK"
                : mode == PerformancePreset.Handheld ? "03 / MOBILE CORE" : "01 / ADAPTIVE CORE";
            LabelText(g, "SYS / 3.0", Theme.Mono(6.5f), Theme.Faint,
                new RectangleF(Theme.S(12), Theme.S(12), Theme.S(64), Theme.S(14)));
            LabelText(g, gameActive ? "LIVE LINK" : guardEnabled ? "READY" : "OFFLINE", Theme.Mono(6.5f), gameActive ? Theme.Green : Theme.Faint,
                new RectangleF(Width - Theme.S(84), Theme.S(12), Theme.S(72), Theme.S(14)));
            LabelText(g, code, Theme.Mono(7f), accent,
                new RectangleF(0, Height - Theme.S(30), Width, Theme.S(16)));
            using (var p = new Pen(accent, Math.Max(1f, Theme.S(1))))
            {
                g.DrawLine(p, Theme.S(1), Theme.S(1), Theme.S(72), Theme.S(1));
                g.DrawLine(p, Width - Theme.S(18), Theme.S(2), Width - Theme.S(2), Theme.S(18));
            }
            using (var p = new Pen(Col.Alpha(accent, 65), 1))
            {
                g.DrawLine(p, Theme.S(16), Height - Theme.S(22), Theme.S(57), Height - Theme.S(22));
                g.DrawLine(p, Width - Theme.S(57), Height - Theme.S(22), Width - Theme.S(16), Height - Theme.S(22));
            }
        }
    }
}
