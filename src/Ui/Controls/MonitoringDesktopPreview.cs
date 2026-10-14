using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PaviseApp
{
    internal sealed class MonitoringDesktopPreview : Form
    {
        private MonitoringLayout layout;
        private MonitoringMetrics metrics = MonitoringMetrics.All;
        private int opacity = 62, offsetX, offsetY, accentRgb = -1, scalePercent = 100, spacingPercent = 100;
        private int previewLanguage = -1;
        private Rectangle screenBounds = new Rectangle(0, 0, 1920, 1080);
        private string screenDeviceName;
        private bool offscreen, dragging, closedNotified;
        private Point grab;
        private Bitmap surface;
        private MonitoringOverlayGeometry geometry;
        internal event Action<int, int> PositionChanging;
        internal event Action<int, int> PositionEdited;
        internal event Action PreviewClosed;
        internal MonitoringOverlayGeometry OverlayGeometry { get { EnsureGeometry(); return geometry; } }
        internal int SelectedOpacity { get { return opacity; } }
        internal int SelectedOffsetX { get { return offsetX; } }
        internal int SelectedOffsetY { get { return offsetY; } }
        internal int SelectedScalePercent { get { return scalePercent; } }

        internal int SelectedSpacingPercent { get { return spacingPercent; } }

        internal MonitoringDesktopPreview()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None; TopMost = true; TabStop = false;
            Cursor = Cursors.SizeAll;
            AccessibleName = AutomaticPolicy.Text("性能监测屏幕预览 · 示例数据", "Monitoring screen preview · sample data");
            AccessibleDescription = AutomaticPolicy.Text("拖动调整位置，右键结束预览。", "Drag to position; right-click to end preview.");
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        }

        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams value = base.CreateParams;
                value.ExStyle |= 0x00080000 | 0x00000080 | 0x08000000;
                return value;
            }
        }

        internal void ShowPreview(Screen screen, bool testOffscreen = false)
        {
            if (IsDisposed) return;
            Screen selected = screen ?? Screen.PrimaryScreen;
            screenDeviceName = selected.DeviceName;
            screenBounds = selected.Bounds;
#if PAVISE_SELFTEST
            testOffscreen = true;
#endif
            offscreen = testOffscreen;
            if (surface != null) { surface.Dispose(); surface = null; }
            EnsureGeometry(); ApplySurface();
            if (!Visible) Show();
            SetWindowPos(Handle, new IntPtr(-1), Left, Top, Width, Height, 0x0010 | 0x0040);
        }

        private void OnDisplaySettingsChanged(object sender, EventArgs args)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try
            {
                BeginInvoke((MethodInvoker)delegate {
                    if (IsDisposed) return;
                    Screen selected = Screen.PrimaryScreen;
                    foreach (Screen screen in Screen.AllScreens)
                        if (screen.DeviceName == screenDeviceName) { selected = screen; break; }
                    screenDeviceName = selected.DeviceName;
                    UpdateScreenBounds(selected.Bounds);
                });
            }
            catch (InvalidOperationException) { }
        }

        private void UpdateScreenBounds(Rectangle nextBounds)
        {
            if (screenBounds == nextBounds) return;
            screenBounds = nextBounds;
            if (surface != null) { surface.Dispose(); surface = null; }
            EnsureGeometry();
            if (IsHandleCreated) ApplySurface();
        }

        internal void UpdatePreview(MonitoringLayout nextLayout, MonitoringMetrics nextMetrics, int nextOpacity, int nextOffsetX, int nextOffsetY, int? nextAccentRgb = null, int scale = 100, int spacing = 100)
        {
            if (IsDisposed) return;
            nextOpacity = Math.Max(0, Math.Min(100, nextOpacity));
            nextOffsetX = Math.Max(-1000, Math.Min(1000, nextOffsetX)); nextOffsetY = Math.Max(-1000, Math.Min(1000, nextOffsetY));
            scale = Math.Max(MonitoringPreferences.MinimumScalePercent, Math.Min(MonitoringPreferences.MaximumScalePercent, scale));
            spacing = Math.Max(MonitoringPreferences.MinimumSpacingPercent, Math.Min(MonitoringPreferences.MaximumSpacingPercent, spacing));
            int nextAccent = (nextAccentRgb ?? Theme.ModeColor(Theme.CurrentMode).ToArgb()) & 0xFFFFFF;
            bool contentChanged = layout != nextLayout || metrics != nextMetrics || opacity != nextOpacity || accentRgb != nextAccent || scalePercent != scale || spacingPercent != spacing || previewLanguage != Lang.Cur;
            if (!contentChanged && offsetX == nextOffsetX && offsetY == nextOffsetY) return;
            layout = nextLayout; metrics = nextMetrics;
            previewLanguage = Lang.Cur;
            Cursor = layout == MonitoringLayout.TopBar ? Cursors.SizeNS : Cursors.SizeAll;
            opacity = nextOpacity; offsetX = nextOffsetX; offsetY = nextOffsetY; accentRgb = nextAccent; scalePercent = scale; spacingPercent = spacing;
            EnsureGeometry();
            if (contentChanged && surface != null) { surface.Dispose(); surface = null; }
            if (IsHandleCreated)
            {
                if (surface == null) ApplySurface();
                else MoveSurface();
            }
        }

        private void EnsureGeometry()
        {
            geometry = MonitoringOverlayGeometry.Calculate(layout, metrics, screenBounds.Width, screenBounds.Height, offsetX, offsetY, scalePercent, spacing: spacingPercent);
        }
        internal Bitmap RenderForTest()
        {
            EnsureGeometry();
            return MonitoringPreviewRenderer.Render(geometry, layout, metrics, opacity,
                accentRgb < 0 ? Theme.ModeColor(Theme.CurrentMode).ToArgb() & 0xFFFFFF : accentRgb);
        }

        internal static void MakeTransparentPixelsHitTestable(byte[] row)
        {
            // 编辑预览保留最低非零透明度，使整个区域可拖动。
            for (int pixel = 0; pixel < row.Length; pixel += 4)
            {
                if (row[pixel + 3] != 0) continue;
                row[pixel] = row[pixel + 1] = row[pixel + 2] = 0;
                row[pixel + 3] = 1;
            }
        }

        private void ApplySurface()
        {
            if (IsDisposed) return;
            EnsureGeometry();
            if (surface == null) surface = RenderForTest();
            Point destination = offscreen ? new Point(-30000 + geometry.Bounds.Left, -30000 + geometry.Bounds.Top)
                : new Point(screenBounds.Left + geometry.Bounds.Left, screenBounds.Top + geometry.Bounds.Top);
            // 直接设置无边框窗口尺寸，绕过主显示器跟踪上限。
            SetWindowPos(Handle, IntPtr.Zero, destination.X, destination.Y, surface.Width, surface.Height, 0x0014);
            IntPtr screenDc = GetDC(IntPtr.Zero), memoryDc = IntPtr.Zero, bitmap = IntPtr.Zero, oldBitmap = IntPtr.Zero;
            BitmapData data = null;
            try
            {
                if (screenDc == IntPtr.Zero) throw new InvalidOperationException("Could not create preview drawing context.");
                memoryDc = CreateCompatibleDC(screenDc);
                var info = new BitmapInfo();
                info.Header.Size = (uint)Marshal.SizeOf(typeof(BitmapInfoHeader));
                info.Header.Width = surface.Width; info.Header.Height = -surface.Height;
                info.Header.Planes = 1; info.Header.BitCount = 32; info.Header.SizeImage = (uint)(surface.Width * surface.Height * 4);
                IntPtr pixels;
                bitmap = CreateDIBSection(memoryDc, ref info, 0, out pixels, IntPtr.Zero, 0);
                if (bitmap == IntPtr.Zero || pixels == IntPtr.Zero) throw new InvalidOperationException("Could not create preview surface.");
                data = surface.LockBits(new Rectangle(Point.Empty, surface.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
                var row = new byte[surface.Width * 4];
                for (int y = 0; y < surface.Height; y++)
                {
                    Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, row.Length);
                    MakeTransparentPixelsHitTestable(row);
                    Marshal.Copy(row, 0, IntPtr.Add(pixels, y * row.Length), row.Length);
                }
                surface.UnlockBits(data); data = null;
                oldBitmap = SelectObject(memoryDc, bitmap);
                var source = new NativePoint(); var target = new NativePoint { X = destination.X, Y = destination.Y };
                var size = new NativeSize { Width = surface.Width, Height = surface.Height };
                var blend = new BlendFunction { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
                if (!UpdateLayeredWindow(Handle, screenDc, ref target, ref size, memoryDc, ref source, 0, ref blend, 2))
                    throw new InvalidOperationException("Could not draw the screen preview (" + Marshal.GetLastWin32Error() + ").");
            }
            finally
            {
                if (data != null) surface.UnlockBits(data);
                if (oldBitmap != IntPtr.Zero) SelectObject(memoryDc, oldBitmap);
                if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
                if (memoryDc != IntPtr.Zero) DeleteDC(memoryDc);
                if (screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        private void MoveSurface()
        {
            EnsureGeometry();
            Point location = offscreen ? new Point(-30000 + geometry.Bounds.Left, -30000 + geometry.Bounds.Top)
                : new Point(screenBounds.Left + geometry.Bounds.Left, screenBounds.Top + geometry.Bounds.Top);
            SetWindowPos(Handle, IntPtr.Zero, location.X, location.Y, geometry.Bounds.Width, geometry.Bounds.Height, 0x0014);
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x0021) { message.Result = new IntPtr(3); return; }
            base.WndProc(ref message);
            if (message.Msg == 0x0024 && message.LParam != IntPtr.Zero)
            {
                var limits = (MinMaxInfo)Marshal.PtrToStructure(message.LParam, typeof(MinMaxInfo));
                limits.MaxTrackSize.X = Math.Max(limits.MaxTrackSize.X, screenBounds.Width);
                limits.MaxTrackSize.Y = Math.Max(limits.MaxTrackSize.Y, screenBounds.Height);
                Marshal.StructureToPtr(limits, message.LParam, false);
            }
        }
        protected override void OnPaint(PaintEventArgs e) { }
        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (!Enabled || !Visible || IsDisposed || Disposing) return;
            if (e.Button == MouseButtons.Right) { Close(); return; }
            if (e.Button != MouseButtons.Left) return;
            dragging = true; grab = e.Location; Capture = true;
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!dragging) return;
            Point cursor = Cursor.Position;
            int x = cursor.X - grab.X - (offscreen ? -30000 : screenBounds.Left);
            int y = cursor.Y - grab.Y - (offscreen ? -30000 : screenBounds.Top);
            MoveDraft(x, y);
        }
        private void MoveDraft(int x, int y)
        {
            Point next = MonitoringOverlayGeometry.GetOffsets(layout, metrics, screenBounds.Width, screenBounds.Height, x, y, scalePercent, spacingPercent);
            if (next.X == offsetX && next.Y == offsetY) return;
            offsetX = next.X; offsetY = next.Y; MoveSurface();
            Action<int, int> changed = PositionChanging; if (changed != null) changed(offsetX, offsetY);
        }
#if PAVISE_SELFTEST
        internal void MoveDraftForTest(int viewportX, int viewportY) { MoveDraft(viewportX, viewportY); }
#endif
        private void FinishDrag()
        {
            if (!dragging) return;
            dragging = false; Capture = false;
            Action<int, int> edited = PositionEdited; if (edited != null) edited(offsetX, offsetY);
        }
        private void CancelDrag()
        {
            // 先清除拖动状态，避免同步捕获丢失事件提交半成品位置。
            dragging = false;
            if (IsHandleCreated && Capture) Capture = false;
        }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); if (e.Button == MouseButtons.Left) FinishDrag(); }
        protected override void OnMouseCaptureChanged(EventArgs e) { if (!Capture) CancelDrag(); base.OnMouseCaptureChanged(e); }
        protected override void OnVisibleChanged(EventArgs e) { if (!Visible) CancelDrag(); base.OnVisibleChanged(e); }
        protected override void OnEnabledChanged(EventArgs e) { if (!Enabled) CancelDrag(); base.OnEnabledChanged(e); }
        protected override void OnHandleDestroyed(EventArgs e) { CancelDrag(); base.OnHandleDestroyed(e); }
        protected override void OnFormClosed(FormClosedEventArgs e) { CancelDrag(); base.OnFormClosed(e); NotifyClosed(); }
        private void NotifyClosed()
        {
            if (closedNotified) return;
            closedNotified = true;
            Action closed = PreviewClosed; if (closed != null) closed();
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) CancelDrag();
            if (disposing) Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            if (disposing && surface != null) { surface.Dispose(); surface = null; }
            base.Dispose(disposing);
            if (disposing) NotifyClosed();
        }

        [StructLayout(LayoutKind.Sequential)] private struct NativePoint { internal int X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct MinMaxInfo
        { internal NativePoint Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
        [StructLayout(LayoutKind.Sequential)] private struct NativeSize { internal int Width, Height; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct BlendFunction { internal byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
        [StructLayout(LayoutKind.Sequential)] private struct BitmapInfoHeader
        { internal uint Size; internal int Width, Height; internal ushort Planes, BitCount; internal uint Compression, SizeImage; internal int XPelsPerMeter, YPelsPerMeter; internal uint ColorsUsed, ColorsImportant; }
        [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo { internal BitmapInfoHeader Header; internal uint Colors; }
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool UpdateLayeredWindow(IntPtr window, IntPtr destinationDc, ref NativePoint destination,
            ref NativeSize size, IntPtr sourceDc, ref NativePoint source, uint colorKey, ref BlendFunction blend, uint flags);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    }
}
