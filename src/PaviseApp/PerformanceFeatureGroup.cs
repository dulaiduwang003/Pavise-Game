using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
    internal sealed class PerformanceFeatureGroup : DBPanel
    {
        private bool ready;

        private bool arranging;

        internal bool FullWidth { get; private set; }

        internal PerformanceFeatureGrid Content { get; private set; }

        internal Rectangle TitleBounds { get; private set; }

        internal PerformanceFeatureGroup(string name, string title, bool fullWidth = false)
        {
            Name = name;
            Text = title;
            FullWidth = fullWidth;
            BackColor = Theme.Card;
            AutoScroll = false;
            TabStop = false;
            AccessibleRole = AccessibleRole.Grouping;
            AccessibleName = title;
            SetStyle(ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            Content = new PerformanceFeatureGrid
            {
                Name = name + "Content", AutoSizeContent = true, SingleColumn = true,
                BackColor = Theme.Card, TabIndex = 0
            };
            Content.ContentSizeChanged = ContentHeightChanged;
            Controls.Add(Content);
        }

        private Rectangle HeaderBounds(int width)
        {
            int inset = Theme.S(12);
            int titleWidth = Math.Max(1, width - inset * 2 - Theme.S(9));
            int height = TextRenderer.MeasureText(Text, Theme.UI(9.5f, true), new Size(titleWidth, int.MaxValue),
                TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding).Height;
            return new Rectangle(inset + Theme.S(9), Theme.S(10), titleWidth, Math.Max(Theme.S(22), height));
        }

        internal int PresentationHeight(int width)
        {
            // 测高只读布局数据，避免外层测量时触发内层尺寸事件。
            Rectangle heading = HeaderBounds(Math.Max(1, width));
            int inner = Math.Max(1, width - Theme.S(24));
            return heading.Bottom + Theme.S(8) + Content.MeasureContentHeight(inner) + Theme.S(12);
        }

        internal void Complete()
        {
            ready = true;
            arranging = true;
            try { Content.Complete(); }
            finally { arranging = false; }
            ContentHeightChanged();
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            ArrangeContent();
        }

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            AccessibleName = Text;
            if (Content != null) ContentHeightChanged();
            Invalidate();
        }

        private void ContentHeightChanged()
        {
            if (!ready || arranging || IsDisposed || Disposing) return;
            int height = PresentationHeight(Math.Max(1, Width));
            if (Height != height) Height = height;
            ArrangeContent();
            Invalidate();
        }

        private void ArrangeContent()
        {
            if (!ready || arranging || Content == null || IsDisposed || Disposing) return;
            arranging = true;
            try
            {
                TitleBounds = HeaderBounds(Math.Max(1, Width));
                int inner = Math.Max(1, Width - Theme.S(24));
                int contentHeight = Content.MeasureContentHeight(inner);
                Content.SetBounds(Theme.S(12), TitleBounds.Bottom + Theme.S(8), inner, contentHeight);
                Content.Reflow();
            }
            finally { arranging = false; }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            if (Backdrop.AppliesTo(this))
            {
                Backdrop.Paint(e.Graphics, this, e.ClipRectangle);
                return;
            }
            using (SolidBrush brush = new SolidBrush(Parent == null ? Theme.Bg : Parent.BackColor))
                e.Graphics.FillRectangle(brush, ClientRectangle);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (Width < 2 || Height < 2) return;
            Graphics graphics = e.Graphics;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle bounds = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath path = Theme.TechPath(bounds, Theme.S(8)))
            {
                using (SolidBrush brush = new SolidBrush(Backdrop.CardFill(this, Theme.Card))) graphics.FillPath(brush, path);
                using (Pen pen = new Pen(Theme.Stroke)) graphics.DrawPath(pen, path);
            }
            TitleBounds = HeaderBounds(Width);
            using (SolidBrush accent = new SolidBrush(Theme.Accent))
                graphics.FillRectangle(accent, Theme.S(12), TitleBounds.Top + Theme.S(4), Theme.S(3), Math.Max(Theme.S(3), TitleBounds.Height - Theme.S(8)));
            TextRenderer.DrawText(graphics, Text, Theme.UI(9.5f, true), TitleBounds, Theme.Fg,
                TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }
}
