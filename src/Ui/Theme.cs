// @author bdth 2074055628@qq.com
// 文件用途 集中管理颜色 字体 尺寸和主题资源

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
    internal static class Col
    {
        public static Color Lerp(Color a, Color b, float t)
        {
            if (t < 0f) t = 0f; if (t > 1f) t = 1f;
            return Color.FromArgb(
                (int)(a.A + (b.A - a.A) * t),
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }
        public static Color Alpha(Color c, int a) { return Color.FromArgb(a < 0 ? 0 : a > 255 ? 255 : a, c.R, c.G, c.B); }
    }

    internal static class Theme
    {
        public static int S(int v) { return Dpi.S(v); }

        private static bool light;
        public static bool LightMode { get { return light; } }

        public static Color Bg        { get { return light ? Color.FromArgb(243, 245, 248) : Color.FromArgb(9, 10, 12); } }
        public static Color Nav       { get { return light ? Color.FromArgb(232, 235, 240) : Color.FromArgb(6, 7, 9); } }
        public static Color Card      { get { return light ? Color.FromArgb(255, 255, 255) : Color.FromArgb(17, 19, 23); } }
        public static Color CardHover { get { return light ? Color.FromArgb(244, 247, 251) : Color.FromArgb(23, 27, 33); } }
        public static Color Inset     { get { return light ? Color.FromArgb(233, 236, 241) : Color.FromArgb(7, 8, 10); } }
        public static Color Stroke    { get { return light ? Color.FromArgb(211, 217, 226) : Color.FromArgb(39, 44, 52); } }
        public static Color StrokeHi  { get { return light ? Color.FromArgb(165, 174, 188) : Color.FromArgb(72, 81, 94); } }
        public static Color Fg        { get { return light ? Color.FromArgb(26, 30, 38)    : Color.FromArgb(244, 246, 249); } }
        public static Color Dim       { get { return light ? Color.FromArgb(96, 105, 118)  : Color.FromArgb(163, 170, 181); } }
        public static Color Faint     { get { return light ? Color.FromArgb(146, 154, 166) : Color.FromArgb(91, 100, 113); } }
        public static Color Green     { get { return light ? Color.FromArgb(16, 150, 92)   : Color.FromArgb(69, 224, 154); } }
        public static Color Danger    { get { return light ? Color.FromArgb(208, 30, 50)   : Color.FromArgb(255, 72, 88); } }
        public static Color TrackOff  { get { return light ? Color.FromArgb(200, 206, 215) : Color.FromArgb(43, 48, 57); } }
        private static Color accent = Color.FromArgb(239, 190, 66);
        private static Color accent2 = Color.FromArgb(184, 117, 24);
        private static Color fromAccent = accent, fromAccent2 = accent2;
        private static Color toAccent = accent, toAccent2 = accent2;
        private static float themeT = 1f;
        private static PerformancePreset currentMode = PerformancePreset.Standard;

        public static Color Accent { get { return accent; } }
        public static Color Accent2 { get { return accent2; } }
        public static Color Sel { get { return Col.Lerp(Card, accent, 0.20f); } }
        public static Color OnAccent
        {
            get
            {
                if (currentMode != PerformancePreset.Standard) return Color.White;
                return light ? Color.White : Color.FromArgb(23, 19, 10);
            }
        }
        public static PerformancePreset CurrentMode { get { return currentMode; } }

        public static Color ModeColor(PerformancePreset mode)
        {
            if (mode == PerformancePreset.Competitive)
                return light ? Color.FromArgb(222, 36, 58) : Color.FromArgb(255, 61, 82);
            if (mode == PerformancePreset.Custom)
                return light ? Color.FromArgb(16, 128, 216) : Color.FromArgb(48, 180, 255);
            return light ? Color.FromArgb(188, 132, 12) : Color.FromArgb(239, 190, 66);
        }

        public static Color ModeColor2(PerformancePreset mode)
        {
            if (mode == PerformancePreset.Competitive)
                return light ? Color.FromArgb(152, 14, 36) : Color.FromArgb(178, 22, 48);
            if (mode == PerformancePreset.Custom)
                return light ? Color.FromArgb(12, 78, 168) : Color.FromArgb(20, 99, 222);
            return light ? Color.FromArgb(142, 88, 8) : Color.FromArgb(184, 117, 24);
        }

        public static void SetLight(bool value)
        {
            if (light == value) return;
            light = value;
            accent = ModeColor(currentMode);
            accent2 = ModeColor2(currentMode);
            fromAccent = toAccent = accent;
            fromAccent2 = toAccent2 = accent2;
            themeT = 1f;
        }

        public static void SetMode(PerformancePreset mode, bool animate)
        {
            Color a = ModeColor(mode), b = ModeColor2(mode);
            if (currentMode == mode && toAccent == a && toAccent2 == b) return;
            currentMode = mode;
            fromAccent = accent; fromAccent2 = accent2;
            toAccent = a; toAccent2 = b;
            themeT = animate ? 0f : 1f;
            if (!animate) { accent = a; accent2 = b; }
            else UiClock.Wake(36);
        }

        public static bool StepTheme()
        {
            if (themeT >= 1f) return false;
            themeT = Math.Min(1f, themeT + 0.055f);
            float t = 1f - (float)Math.Pow(1f - themeT, 3);
            accent = Col.Lerp(fromAccent, toAccent, t);
            accent2 = Col.Lerp(fromAccent2, toAccent2, t);
            return true;
        }

        private static readonly Dictionary<int, Font> fontCache = new Dictionary<int, Font>();
        private static readonly object fontLk = new object();

        public static Font UI(float size, bool bold)
        {
            if (size < 9f) size = Math.Min(9f, size + 0.7f);
            int key = ((int)Math.Round(size * 100) << 1) | (bold ? 1 : 0);
            lock (fontLk)
            {
                Font f;
                if (!fontCache.TryGetValue(key, out f))
                {
                    f = SmoothFont("Microsoft YaHei UI", size,
                        bold ? FontStyle.Bold : FontStyle.Regular);
                    fontCache[key] = f;
                }
                return f;
            }
        }

        public static void DropFontCache()
        {
            lock (fontLk) { fontCache.Clear(); monoCache.Clear(); }
        }

        private static readonly Dictionary<int, Font> monoCache = new Dictionary<int, Font>();
        public static Font Mono(float size)
        {
            int key = (int)Math.Round(size * 100);
            lock (fontLk)
            {
                Font f;
                if (!monoCache.TryGetValue(key, out f))
                {
                    f = SmoothFont("Consolas", size, FontStyle.Regular);
                    monoCache[key] = f;
                }
                return f;
            }
        }

        private const byte ClearTypeQuality = 5;

        private static Font SmoothFont(string family, float size, FontStyle style)
        {
            try
            {
                int pixels = (int)Math.Max(1, Math.Round(size * Dpi.Scale * 96f / 72f));
                var lf = new LogFont();
                lf.lfHeight = -pixels;
                lf.lfWeight = (style & FontStyle.Bold) != 0 ? 700 : 400;
                lf.lfItalic = (byte)((style & FontStyle.Italic) != 0 ? 1 : 0);
                lf.lfCharSet = 1;
                lf.lfQuality = ClearTypeQuality;
                lf.lfFaceName = family;
                return Font.FromLogFont(lf);
            }
            catch
            {
                return new Font(family, Dpi.CrispPoint(size), style, GraphicsUnit.Point);
            }
        }

        [System.Runtime.InteropServices.StructLayout(
            System.Runtime.InteropServices.LayoutKind.Sequential,
            CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        private class LogFont
        {
            public int lfHeight;
            public int lfWidth;
            public int lfEscapement;
            public int lfOrientation;
            public int lfWeight;
            public byte lfItalic;
            public byte lfUnderline;
            public byte lfStrikeOut;
            public byte lfCharSet;
            public byte lfOutPrecision;
            public byte lfClipPrecision;
            public byte lfQuality;
            public byte lfPitchAndFamily;
            [System.Runtime.InteropServices.MarshalAs(
                System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 32)]
            public string lfFaceName = "";
        }

        public static GraphicsPath Rounded(Rectangle r, int rad)
        {
            var p = new GraphicsPath();
            if (rad <= 0) { p.AddRectangle(r); return p; }
            int d = rad * 2;
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            if (d <= 0) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static GraphicsPath TechPath(Rectangle r, int cut)
        {
            var p = new GraphicsPath();
            cut = Math.Max(1, Math.Min(cut, Math.Min(r.Width, r.Height) / 3));
            p.AddPolygon(new[] {
                new Point(r.Left, r.Top), new Point(r.Right - cut, r.Top),
                new Point(r.Right, r.Top + cut), new Point(r.Right, r.Bottom),
                new Point(r.Left + cut, r.Bottom), new Point(r.Left, r.Bottom - cut)
            });
            p.CloseFigure();
            return p;
        }

        public static void FillRound(Graphics g, Rectangle r, int rad, Color c)
        {
            using (var p = Rounded(r, rad)) using (var b = new SolidBrush(c)) g.FillPath(b, p);
        }

        public static void StyleList(ListBox lb)
        {
            StyleList(lb, true);
        }

        public static void StyleList(ListBox lb, bool defaultDraw)
        {
            lb.BackColor = Card;
            lb.ForeColor = Fg;
            lb.BorderStyle = BorderStyle.None;
            lb.DrawMode = DrawMode.OwnerDrawFixed;
            lb.ItemHeight = S(32);
            lb.IntegralHeight = false;
            lb.Font = UI(9.5f, false);
            lb.Tag = -1;
            if (defaultDraw) lb.DrawItem += DrawListItem;
            lb.MouseMove += (s, e) =>
            {
                var l = (ListBox)s;
                int idx = l.IndexFromPoint(e.Location);
                int was = l.Tag is int ? (int)l.Tag : -1;
                if (was == idx) return;
                l.Tag = idx;
                if (was >= 0 && was < l.Items.Count) l.Invalidate(l.GetItemRectangle(was));
                if (idx >= 0 && idx < l.Items.Count) l.Invalidate(l.GetItemRectangle(idx));
            };
            lb.MouseLeave += (s, e) =>
            {
                var l = (ListBox)s;
                int was = l.Tag is int ? (int)l.Tag : -1;
                if (was < 0) return;
                l.Tag = -1;
                if (was < l.Items.Count) l.Invalidate(l.GetItemRectangle(was));
            };
            Native.Dark(lb);
        }

        public static int HoverIndex(ListBox lb)
        {
            return lb != null && lb.Tag is int ? (int)lb.Tag : -1;
        }

        private static void DrawListItem(object s, DrawItemEventArgs e)
        {
            var lb = (ListBox)s;
            using (var bg = new SolidBrush(lb.BackColor)) e.Graphics.FillRectangle(bg, e.Bounds);
            if (e.Index < 0) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            bool sel = (e.State & DrawItemState.Selected) != 0;
            int hov = (lb.Tag is int) ? (int)lb.Tag : -1;
            var r = Rectangle.Inflate(e.Bounds, -S(4), -S(2));
            if (sel) FillRound(e.Graphics, r, S(8), Sel);
            else if (e.Index == hov) FillRound(e.Graphics, r, S(8), CardHover);
            TextRenderer.DrawText(e.Graphics, lb.Items[e.Index].ToString(), lb.Font,
                new Rectangle(e.Bounds.X + S(14), e.Bounds.Y, e.Bounds.Width - S(20), e.Bounds.Height),
                sel ? Color.White : Fg, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        public static TextBox MakeTextBox(int x, int y, int w)
        {
            var t = new TextBox();
            t.SetBounds(x, y, w, S(28));
            t.BackColor = Card;
            t.ForeColor = Fg;
            t.BorderStyle = BorderStyle.FixedSingle;
            t.Font = UI(9.5f, false);
            return t;
        }
    }

}
