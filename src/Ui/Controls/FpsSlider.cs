// @author bdth 2074055628@qq.com
// 文件用途 提供帧率上限滑块 左端关右端屏减三两个特殊档 中段在区间内连续取值 滚轮逐帧微调

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace PaviseApp
{
    internal sealed class FpsSlider : Control
    {
        private const int ZoneOff = 0;
        private const int ZoneTrack = 1;
        private const int ZoneScreen = 2;

        private string mode = "off";
        private int fps = 144;
        private bool dragging;
        private int hoverZone = -1;
        private Motion thumb;
        private Motion[] glow = new Motion[3];
        private bool thumbReady;
        public Action<string> ModeChanged;

        public FpsSlider()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Card;
            Cursor = Cursors.Hand;
            thumb.Speed = 0.30f;
            for (int i = 0; i < glow.Length; i++) glow[i].Speed = 0.26f;
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
            bool moved = thumb.Step();
            if (moved && Math.Abs(thumb.Value - thumb.Target) < 0.75f) thumb.Set(thumb.Target);
            for (int i = 0; i < glow.Length; i++) if (glow[i].Step()) moved = true;
            if (moved) Invalidate();
        }

        public string Mode
        {
            get { return mode; }
            set
            {
                string m = Normalize(value);
                if (m == mode) return;
                mode = m;
                int f = ParseFps(m);
                if (f > 0) fps = f;
                MoveThumb(false);
                Invalidate();
            }
        }

        private static string Normalize(string value)
        {
            if (value == "off" || value == "screen") return value;
            int f = ParseFps(value);
            return f > 0 ? ClampFps(f).ToString(CultureInfo.InvariantCulture) : "off";
        }

        private static int ParseFps(string value)
        {
            int f;
            return value != null && int.TryParse(value, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out f) ? f : 0;
        }

        private static int ClampFps(int f)
        {
            if (f < PolicyCatalog.FrlMin) return PolicyCatalog.FrlMin;
            if (f > PolicyCatalog.FrlMax) return PolicyCatalog.FrlMax;
            return f;
        }

        private Rectangle OffRect
        {
            get { return new Rectangle(0, 0, Theme.S(34), Height); }
        }

        private Rectangle ScreenRect
        {
            get { return new Rectangle(Width - Theme.S(44), 0, Theme.S(44), Height); }
        }

        private int ThumbW
        {
            get { return Theme.S(40); }
        }

        private int TrackLo
        {
            get { return OffRect.Right + Theme.S(8) + ThumbW / 2; }
        }

        private int TrackHi
        {
            get { return ScreenRect.Left - Theme.S(8) - ThumbW / 2; }
        }

        private int XOf(int f)
        {
            int lo = TrackLo, hi = TrackHi;
            if (hi <= lo) return lo;
            float t = (ClampFps(f) - PolicyCatalog.FrlMin)
                / (float)(PolicyCatalog.FrlMax - PolicyCatalog.FrlMin);
            return lo + (int)Math.Round(t * (hi - lo));
        }

        private int FpsAt(int x)
        {
            int lo = TrackLo, hi = TrackHi;
            if (hi <= lo) return PolicyCatalog.FrlMin;
            float t = (x - lo) / (float)(hi - lo);
            if (t < 0f) t = 0f;
            if (t > 1f) t = 1f;
            return PolicyCatalog.FrlMin
                + (int)Math.Round(t * (PolicyCatalog.FrlMax - PolicyCatalog.FrlMin));
        }

        private void MoveThumb(bool instant)
        {
            if (Width <= 0) { thumbReady = false; return; }
            int x = XOf(fps);
            if (instant || !thumbReady) { thumb.Set(x); thumbReady = true; }
            else thumb.To(x);
            UiClock.Wake();
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            if (Width <= 0) return;
            thumb.Set(XOf(fps));
            thumbReady = true;
        }

        private int ZoneAt(Point p)
        {
            if (OffRect.Contains(p)) return ZoneOff;
            if (ScreenRect.Contains(p)) return ZoneScreen;
            if (p.X >= OffRect.Right && p.X < ScreenRect.Left) return ZoneTrack;
            return -1;
        }

        private void SyncGlow()
        {
            for (int i = 0; i < glow.Length; i++) glow[i].To(i == hoverZone ? 1f : 0f);
            UiClock.Wake();
        }

        private void Commit(string next)
        {
            if (next == mode) return;
            mode = next;
            Invalidate();
            if (ModeChanged != null) ModeChanged(mode);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            int zone = ZoneAt(e.Location);
            if (zone == ZoneTrack)
            {
                dragging = true;
                fps = FpsAt(e.X);
                thumb.Set(XOf(fps));
                thumbReady = true;
                Invalidate();
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragging)
            {
                fps = FpsAt(e.X);
                thumb.Set(XOf(fps));
                Invalidate();
                return;
            }
            int zone = ZoneAt(e.Location);
            if (zone != hoverZone) { hoverZone = zone; SyncGlow(); Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hoverZone != -1) { hoverZone = -1; SyncGlow(); Invalidate(); }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            if (dragging)
            {
                dragging = false;
                Commit(fps.ToString(CultureInfo.InvariantCulture));
                return;
            }
            int zone = ZoneAt(e.Location);
            if (zone == ZoneOff) Commit("off");
            else if (zone == ZoneScreen) Commit("screen");
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            var h = e as HandledMouseEventArgs;
            if (h != null) h.Handled = true;
            int step = e.Delta > 0 ? 1 : -1;
            fps = ClampFps(fps + step);
            MoveThumb(true);
            Commit(fps.ToString(CultureInfo.InvariantCulture));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            using (var bg = new SolidBrush(BackColor)) g.FillRectangle(bg, ClientRectangle);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (!thumbReady && Width > 0) { thumb.Set(XOf(fps)); thumbReady = true; }
            bool numeric = mode != "off" && mode != "screen";

            DrawChip(g, OffRect, Lang.T("frl.off"), mode == "off", GlowAt(ZoneOff));
            DrawChip(g, ScreenRect, Lang.T("frl.screen"), mode == "screen", GlowAt(ZoneScreen));

            int cy = Height / 2;
            int lineH = Math.Max(2, Theme.S(4));
            int lx = OffRect.Right + Theme.S(8);
            int rx = ScreenRect.Left - Theme.S(8);
            if (rx > lx)
            {
                var track = new Rectangle(lx, cy - lineH / 2, rx - lx, lineH);
                using (GraphicsPath p = Theme.TechPath(track, lineH / 2))
                using (var b = new SolidBrush(Theme.Stroke)) g.FillPath(b, p);
                int tx = (int)thumb.Value;
                if (tx > lx)
                {
                    var fill = new Rectangle(lx, cy - lineH / 2, Math.Min(tx, rx) - lx, lineH);
                    using (GraphicsPath p = Theme.TechPath(fill, lineH / 2))
                    using (var b = new SolidBrush(Col.Alpha(Theme.Accent, numeric ? 200 : 70)))
                        g.FillPath(b, p);
                }
            }

            var tr = new Rectangle((int)thumb.Value - ThumbW / 2, 0, ThumbW - 1, Height - 1);
            float hover = GlowAt(ZoneTrack);
            using (GraphicsPath p = Theme.TechPath(tr, Theme.S(6)))
            {
                Color face = numeric
                    ? Col.Lerp(Theme.Card, Theme.Accent, 0.18f)
                    : Col.Lerp(Theme.Card, Theme.CardHover, hover);
                using (var b = new SolidBrush(face)) g.FillPath(b, p);
                Color edge = numeric
                    ? Col.Alpha(Theme.Accent, 215)
                    : Col.Lerp(Theme.Stroke, Theme.StrokeHi, hover);
                using (var pen = new Pen(edge)) g.DrawPath(pen, p);
            }
            TextRenderer.DrawText(g, fps.ToString(CultureInfo.InvariantCulture),
                Theme.UI(8.25f, numeric), tr, numeric ? Theme.Fg : Theme.Dim,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }

        private void DrawChip(Graphics g, Rectangle r, string label, bool selected, float hover)
        {
            r.Width -= 1; r.Height -= 1;
            using (GraphicsPath p = Theme.TechPath(r, Theme.S(6)))
            {
                Color face = selected
                    ? Col.Lerp(Theme.Card, Theme.Accent, 0.18f)
                    : Col.Lerp(Theme.Card, Theme.CardHover, hover);
                using (var b = new SolidBrush(face)) g.FillPath(b, p);
                Color edge = selected
                    ? Col.Alpha(Theme.Accent, 215)
                    : Col.Lerp(Theme.Stroke, Theme.StrokeHi, hover);
                using (var pen = new Pen(edge)) g.DrawPath(pen, p);
            }
            TextRenderer.DrawText(g, label, Theme.UI(8.25f, selected), r,
                selected ? Theme.Fg : Theme.Dim,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }

        private float GlowAt(int zone)
        {
            return zone >= 0 && zone < glow.Length ? glow[zone].Value : 0f;
        }
    }
}
