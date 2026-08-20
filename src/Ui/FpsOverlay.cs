// @author bdth 2074055628@qq.com
// 文件用途 游戏内帧率小窗 一条横排 默认紧贴屏幕顶部中间 分层置顶且点击穿透
// 从不查询游戏窗口句柄 也不打开游戏进程 位置只按屏幕算 对游戏进程零查询
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PaviseApp
{
    internal sealed class FpsOverlay : Form
    {
        internal const string EnabledKey = "FpsOverlay";
        internal const string PosKey = "FpsOverlayCorner";
        internal const string OpacityKey = "FpsOverlayOpacity";

        internal const int PosTop = 0;
        internal const int PosTopLeft = 1;
        internal const int PosTopRight = 2;
        internal const int PosBottom = 3;

        private const int RefreshMs = 500;
        private const int EdgeGapDip = 2;
        private const int SideGapDip = 12;
        private const int PadXDip = 9;
        private const int PadYDip = 4;
        private const int SegGapDip = 12;
        private const int LabelGapDip = 4;

        private const int WsExLayered = 0x00080000;
        private const int WsExTransparent = 0x00000020;
        private const int WsExToolWindow = 0x00000080;
        private const int WsExNoActivate = 0x08000000;
        private const int WsExTopMost = 0x00000008;
        private const int UlwAlpha = 0x00000002;
        private const int AcSrcOver = 0x00;
        private const int AcSrcAlpha = 0x01;

        private static readonly IntPtr HwndTopMost = new IntPtr(-1);
        private const uint SwpNoMove = 0x0002;
        private const uint SwpNoSize = 0x0001;
        private const uint SwpNoActivate = 0x0010;

        private static FpsOverlay instance;
        private static readonly object gate = new object();

        private readonly Timer tick = new Timer();
        private string shown;

        internal static bool EnabledSetting
        {
            get { return Settings.Load(EnabledKey, false); }
            set { Settings.Save(EnabledKey, value); }
        }

        internal static int PosSetting
        {
            get
            {
                int c;
                if (!int.TryParse(Settings.LoadStr(PosKey, "0"), out c)) c = PosTop;
                return c < 0 || c > 3 ? PosTop : c;
            }
            set { Settings.SaveStr(PosKey, (value < 0 || value > 3 ? PosTop : value).ToString()); }
        }

        internal static int OpacitySetting
        {
            get
            {
                int a;
                if (!int.TryParse(Settings.LoadStr(OpacityKey, "170"), out a)) a = 170;
                return a < 60 ? 60 : a > 255 ? 255 : a;
            }
            set { Settings.SaveStr(OpacityKey, (value < 60 ? 60 : value > 255 ? 255 : value).ToString()); }
        }

        internal static bool Visible2 { get { lock (gate) return instance != null; } }

        internal static Rectangle Area
        {
            get { lock (gate) return instance != null ? instance.Bounds : Rectangle.Empty; }
        }

        // 只能在界面线程上调 由常驻的托盘时钟驱动
        internal static void Sync()
        {
            bool want = EnabledSetting && FrameRateMonitor.InstantText() != null;
            lock (gate)
            {
                if (want && instance == null)
                {
                    SystemLoadProbe.Reset();
                    try { instance = new FpsOverlay(); instance.Show(); }
                    catch { instance = null; }
                }
                else if (!want && instance != null)
                {
                    try { instance.Close(); } catch { }
                    instance = null;
                }
            }
        }

        internal static void Shutdown()
        {
            lock (gate)
            {
                if (instance == null) return;
                try { instance.Close(); } catch { }
                instance = null;
            }
        }

        private FpsOverlay()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            Bounds = new Rectangle(-32000, -32000, 1, 1);
            tick.Interval = RefreshMs;
            tick.Tick += (s, e) => Repaint();
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WsExLayered | WsExTransparent | WsExToolWindow
                    | WsExNoActivate | WsExTopMost;
                return cp;
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Repaint();
            tick.Start();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            tick.Stop();
            tick.Dispose();
            base.OnFormClosed(e);
        }

        private sealed class Seg
        {
            public string Text;
            public Font Font;
            public Color Color;
            public SizeF Size;
            public int Gap;
        }

        private List<Seg> Build(out string signature)
        {
            SystemLoadProbe.SampleIfDue(FrameRateMonitor.ActivePid);

            Font val = Theme.Mono(Dpi.S(10));
            Font lab = Theme.UI(Dpi.S(8), false);
            Color valColor = Color.FromArgb(255, 244, 246, 249);
            Color labColor = Color.FromArgb(205, 158, 166, 178);

            var segs = new List<Seg>();
            var sig = new System.Text.StringBuilder();

            FrameWindow w = FrameRateMonitor.Current;
            if (w != null && w.Enough)
            {
                string fps = w.Fps.ToString("F0");
                Add(segs, sig, fps, val, valColor, 0);
                Add(segs, sig, "FPS", lab, labColor, Dpi.S(LabelGapDip));
                string ms = w.MedianMs.ToString("F1");
                Add(segs, sig, ms, val, valColor, Dpi.S(SegGapDip));
                Add(segs, sig, "ms", lab, labColor, Dpi.S(LabelGapDip));
                if (w.MultiPresenter)
                    Add(segs, sig, Lang.T("t.framerate.multi.tag"), lab, labColor, Dpi.S(LabelGapDip));
            }

            double cpu = SystemLoadProbe.CpuPercent;
            if (cpu >= 0) Pair(segs, sig, "CPU", cpu.ToString("F0") + "%", lab, val, labColor, valColor);

            double gameCpu = SystemLoadProbe.GameCpuPercent;
            if (gameCpu >= 0)
                Pair(segs, sig, Lang.T("t.overlay.game"), gameCpu.ToString("F0") + "%",
                    lab, val, labColor, valColor);

            double gpu = SystemLoadProbe.GpuPercent;
            if (gpu >= 0) Pair(segs, sig, "GPU", gpu.ToString("F0") + "%", lab, val, labColor, valColor);

            double temp = SystemLoadProbe.GpuTempC;
            if (temp >= 0)
            {
                Add(segs, sig, temp.ToString("F0"), val, valColor,
                    segs.Count > 0 ? Dpi.S(SegGapDip) : 0);
                Add(segs, sig, "°C", lab, labColor, Dpi.S(LabelGapDip));
            }

            double used = SystemLoadProbe.VramUsedMb, whole = SystemLoadProbe.VramTotalMb;
            if (used >= 0)
            {
                string text = whole >= 1024
                    ? (used / 1024).ToString("F1") + "/" + (whole / 1024).ToString("F0")
                    : used.ToString("F0");
                Add(segs, sig, "VRAM", lab, labColor, segs.Count > 0 ? Dpi.S(SegGapDip) : 0);
                Add(segs, sig, text, val, valColor, Dpi.S(LabelGapDip));
                Add(segs, sig, whole >= 1024 ? "GB" : "MB", lab, labColor, Dpi.S(LabelGapDip));
            }

            signature = sig.ToString();
            return segs;
        }

        private static void Pair(List<Seg> segs, System.Text.StringBuilder sig,
            string label, string value, Font lab, Font val, Color labColor, Color valColor)
        {
            Add(segs, sig, label, lab, labColor, segs.Count > 0 ? Dpi.S(SegGapDip) : 0);
            Add(segs, sig, value, val, valColor, Dpi.S(LabelGapDip));
        }

        private static void Add(List<Seg> segs, System.Text.StringBuilder sig,
            string text, Font font, Color color, int gap)
        {
            segs.Add(new Seg { Text = text, Font = font, Color = color, Gap = gap });
            sig.Append(text).Append('|');
        }

        private void Repaint()
        {
            string signature;
            List<Seg> segs = Build(out signature);
            if (segs.Count == 0) return;
            if (signature == shown) return;
            shown = signature;

            int padX = Dpi.S(PadXDip), padY = Dpi.S(PadYDip);
            float total = 0, tallest = 0;
            using (var probe = new Bitmap(1, 1))
            using (Graphics g = Graphics.FromImage(probe))
                foreach (Seg s in segs)
                {
                    s.Size = g.MeasureString(s.Text, s.Font, PointF.Empty,
                        StringFormat.GenericTypographic);
                    total += s.Gap + s.Size.Width;
                    if (s.Size.Height > tallest) tallest = s.Size.Height;
                }

            var size = new Size((int)Math.Ceiling(total) + padX * 2,
                (int)Math.Ceiling(tallest) + padY * 2);

            using (var bmp = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    g.Clear(Color.Transparent);

                    var body = new Rectangle(0, 0, size.Width - 1, size.Height - 1);
                    using (GraphicsPath path = Round(body, Math.Min(Dpi.S(5), size.Height / 2)))
                    {
                        using (var back = new SolidBrush(Color.FromArgb(OpacitySetting, 10, 12, 16)))
                            g.FillPath(back, path);
                        // 底色是暗的 压在暗画面上就看不出边 一条极淡的描边把它框出来
                        using (var edge = new Pen(Color.FromArgb(52, 255, 255, 255)))
                            g.DrawPath(edge, path);
                    }

                    float x = padX;
                    foreach (Seg s in segs)
                    {
                        x += s.Gap;
                        float y = (size.Height - s.Size.Height) / 2f;
                        // 亮画面上白字会糊 垫一层黑影 这是游戏叠加层的常规做法
                        using (var shadow = new SolidBrush(Color.FromArgb(150, 0, 0, 0)))
                            g.DrawString(s.Text, s.Font, shadow, x + 1, y + 1,
                                StringFormat.GenericTypographic);
                        using (var brush = new SolidBrush(s.Color))
                            g.DrawString(s.Text, s.Font, brush, x, y,
                                StringFormat.GenericTypographic);
                        x += s.Size.Width;
                    }
                }
                Place(size);
                Push(bmp);
            }
        }

        private void Place(Size size)
        {
            Rectangle screen = Screen.PrimaryScreen.Bounds;
            int edge = Dpi.S(EdgeGapDip), side = Dpi.S(SideGapDip);
            int mid = screen.Left + (screen.Width - size.Width) / 2;
            int x, y;
            switch (PosSetting)
            {
                case PosTopLeft: x = screen.Left + side; y = screen.Top + edge; break;
                case PosTopRight: x = screen.Right - size.Width - side; y = screen.Top + edge; break;
                case PosBottom: x = mid; y = screen.Bottom - size.Height - edge; break;
                default: x = mid; y = screen.Top + edge; break;
            }
            Bounds = new Rectangle(x, y, size.Width, size.Height);
        }

        private void Push(Bitmap bmp)
        {
            IntPtr screenDc = GetDC(IntPtr.Zero);
            IntPtr memDc = CreateCompatibleDC(screenDc);
            IntPtr hBitmap = IntPtr.Zero;
            IntPtr old = IntPtr.Zero;
            try
            {
                hBitmap = bmp.GetHbitmap(Color.FromArgb(0));
                old = SelectObject(memDc, hBitmap);
                var size = new NativeSize(bmp.Width, bmp.Height);
                var src = new NativePoint(0, 0);
                var dst = new NativePoint(Left, Top);
                var blend = new BlendFunction
                {
                    BlendOp = AcSrcOver,
                    BlendFlags = 0,
                    SourceConstantAlpha = 255,
                    AlphaFormat = AcSrcAlpha
                };
                UpdateLayeredWindow(Handle, screenDc, ref dst, ref size,
                    memDc, ref src, 0, ref blend, UlwAlpha);
                SetWindowPos(Handle, HwndTopMost, 0, 0, 0, 0,
                    SwpNoMove | SwpNoSize | SwpNoActivate);
            }
            catch { }
            finally
            {
                ReleaseDC(IntPtr.Zero, screenDc);
                if (hBitmap != IntPtr.Zero)
                {
                    SelectObject(memDc, old);
                    DeleteObject(hBitmap);
                }
                DeleteDC(memDc);
            }
        }

        private static GraphicsPath Round(Rectangle r, int radius)
        {
            var p = new GraphicsPath();
            int d = radius * 2;
            if (d <= 0 || d > r.Width || d > r.Height) { p.AddRectangle(r); return p; }
            p.AddArc(r.Left, r.Top, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            public int X, Y;
            public NativePoint(int x, int y) { X = x; Y = y; }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeSize
        {
            public int Cx, Cy;
            public NativeSize(int cx, int cy) { Cx = cx; Cy = cy; }
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct BlendFunction
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr dstDc,
            ref NativePoint dst, ref NativeSize size, IntPtr srcDc, ref NativePoint src,
            int colorKey, ref BlendFunction blend, int flags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after,
            int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr dc);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr dc);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr obj);
    }
}
