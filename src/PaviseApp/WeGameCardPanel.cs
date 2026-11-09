using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace PaviseApp
{
    internal sealed class WeGameCardPanel : GameCardExtension
    {
        private readonly WeGameShellService service;
        private readonly GameProfile profile;
        private readonly System.Windows.Forms.Timer tick;
        private Color surface = Theme.Card;
        private WeGameShellSnapshot last;
        private int refreshQueued;
        private string stateError = "";
        private Control visibilityParent;

        public WeGameCardPanel(WeGameShellService service, GameProfile profile)
        {
            if (service == null) throw new ArgumentNullException("service");
            if (profile == null) throw new ArgumentNullException("profile");
            this.service = service;
            this.profile = profile.Clone();
            SetStyle(ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            TabStop = false;
            BackColor = surface;
            AccessibleRole = AccessibleRole.Grouping;
            tick = new System.Windows.Forms.Timer();
            tick.Interval = 1000;
            tick.Tick += delegate { if (Visible && FindForm() != null) RefreshState(); };
            service.Changed += OnServiceChanged;
            RefreshState();
        }

        private static string TitleText()
        {
            return PerformancePolicy.Text("WeGame 自动脱壳", "Automatic WeGame cleanup");
        }

        private static string DescriptionText()
        {
            return PerformancePolicy.Text("已观测的渲染进程启动后，自动退出 WeGame 界面；保留登录与运行组件", "When an observed renderer starts, automatically close WeGame. Login and runtime components are kept.");
        }

        private Rectangle TitleBoundsForWidth(int width)
        {
            return new Rectangle(Theme.S(4), Theme.S(10), Math.Max(1, width - Theme.S(8)), Theme.S(22));
        }

        private Rectangle DescriptionBoundsForWidth(int width)
        {
            int textWidth = Math.Max(1, width - Theme.S(8));
            int measuredHeight = TextRenderer.MeasureText(DescriptionText(), Theme.UI(8.2f, false), new Size(textWidth, int.MaxValue), TextFormatFlags.NoPadding | TextFormatFlags.WordBreak).Height;
            return new Rectangle(Theme.S(4), Theme.S(35), textWidth, Math.Max(Theme.S(20), measuredHeight));
        }

        private Rectangle StatusBoundsForWidth(int width)
        {
            Rectangle description = DescriptionBoundsForWidth(width);
            return new Rectangle(description.Left, description.Bottom + Theme.S(8), description.Width, Theme.S(38));
        }

        public override int PreferredHeight { get { return PreferredHeightForWidth(Width); } }

        public override int PreferredHeightForWidth(int width)
        {
            return StatusBoundsForWidth(width).Bottom + Theme.S(10);
        }

        public override void SetSurface(Color value)
        {
            surface = value;
            BackColor = value;
            Invalidate();
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            UpdatePolling();
        }

        protected override void OnParentChanged(EventArgs e)
        {
            if (visibilityParent != null) visibilityParent.VisibleChanged -= OnContainerVisibleChanged;
            base.OnParentChanged(e);
            visibilityParent = Parent;
            if (visibilityParent != null) visibilityParent.VisibleChanged += OnContainerVisibleChanged;
            UpdatePolling();
        }

        private void OnContainerVisibleChanged(object sender, EventArgs e)
        {
            UpdatePolling();
        }

        private void UpdatePolling()
        {
            if (tick == null) return;
            if (!IsDisposed && !Disposing && Visible && (visibilityParent == null || visibilityParent.Visible))
            {
                tick.Start();
                RefreshState();
            }
            else tick.Stop();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            RefreshState();
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            using (SolidBrush brush = new SolidBrush(surface)) e.Graphics.FillRectangle(brush, ClientRectangle);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (Pen pen = new Pen(Theme.Stroke)) e.Graphics.DrawLine(pen, 0, 0, Width, 0);
            DrawText(e.Graphics, TitleText(), Theme.UI(8.6f, true), TitleBoundsForWidth(Width), Theme.Fg, false);
            DrawText(e.Graphics, DescriptionText(), Theme.UI(8.2f, false), DescriptionBoundsForWidth(Width), Theme.Dim, true);
            string error = !string.IsNullOrEmpty(stateError) ? stateError : (last == null ? "" : last.Error);
            string status = string.IsNullOrEmpty(error) ? (last == null ? PerformancePolicy.Text("等待已观测的渲染进程启动", "Waiting for an observed renderer to start") : last.Status) : error;
            Color color = !string.IsNullOrEmpty(error) ? Theme.Warning : (last != null && last.Detached ? Theme.Green : (last != null && last.Busy ? Theme.Accent : Theme.Dim));
            DrawText(e.Graphics, status ?? "", Theme.UI(7.8f, false), StatusBoundsForWidth(Width), color, true);
        }

        private static void DrawText(Graphics graphics, string text, Font font, Rectangle bounds, Color color, bool wrap)
        {
            TextFormatFlags flags = TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis;
            flags |= wrap ? TextFormatFlags.WordBreak : TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter;
            TextRenderer.DrawText(graphics, text, font, bounds, color, flags);
        }

        private void RefreshState()
        {
            if (IsDisposed || Disposing) return;
            try
            {
                last = service.GetSnapshot(profile);
                stateError = "";
            }
            catch (Exception ex)
            {
                last = null;
                stateError = PerformancePolicy.Text("暂时无法读取游戏状态", "Game status is temporarily unavailable");
                Logger.Log("WeGame 自动脱壳 读取状态失败 " + ex.Message);
            }
            AccessibleName = TitleText();
            AccessibleDescription = DescriptionText() + " " + (last == null ? "" : last.Status) + " " + stateError + " " + (last == null ? "" : last.Error);
            Invalidate();
        }

        private void OnServiceChanged()
        {
            QueueRefresh();
        }

        private void QueueRefresh()
        {
            if (IsDisposed || Disposing || !IsHandleCreated || Interlocked.CompareExchange(ref refreshQueued, 1, 0) != 0) return;
            try
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    Interlocked.Exchange(ref refreshQueued, 0);
                    RefreshState();
                });
            }
            catch (InvalidOperationException) { Interlocked.Exchange(ref refreshQueued, 0); }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                service.Changed -= OnServiceChanged;
                if (visibilityParent != null) visibilityParent.VisibleChanged -= OnContainerVisibleChanged;
                tick.Stop();
                tick.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
