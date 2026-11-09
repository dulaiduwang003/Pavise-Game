using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
    internal sealed class LibraryDialogButton : Button
    {
        private readonly bool primary;

        private bool hover;

        internal LibraryDialogButton(string text, bool emphasized)
        {
            primary = emphasized;
            Text = text;
            Font = Theme.UI(9f, true);
            base.FlatStyle = FlatStyle.Flat;
            base.FlatAppearance.BorderSize = 0;
            base.TabStop = true;
            Cursor = Cursors.Hand;
            base.AccessibleName = text;
            SetStyle(ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            hover = true;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hover = false;
            Invalidate();
        }

        protected override void OnEnter(EventArgs e)
        {
            base.OnEnter(e);
            Invalidate();
        }

        protected override void OnLeave(EventArgs e)
        {
            base.OnLeave(e);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics graphics = e.Graphics;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (SolidBrush brush = new SolidBrush(Theme.Bg))
            {
                graphics.FillRectangle(brush, base.ClientRectangle);
            }
            Rectangle rectangle = new Rectangle(0, 0, base.Width - 1, base.Height - 1);
            Color color = (primary ? Theme.Accent : (hover ? Theme.CardHover : Theme.Card));
            using (GraphicsPath path = Theme.TechPath(rectangle, Theme.S(7)))
            {
                using (SolidBrush brush2 = new SolidBrush(color))
                {
                    graphics.FillPath(brush2, path);
                }
                using (Pen pen = new Pen(primary ? Theme.Accent : Theme.StrokeHi))
                {
                    graphics.DrawPath(pen, path);
                }
            }
            Color foreColor = (primary ? Theme.OnAccent : Theme.Fg);
            TextRenderer.DrawText(graphics, Text, Font, base.ClientRectangle, foreColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter);
            if (Focused && ShowFocusCues)
            {
                ControlPaint.DrawFocusRectangle(graphics, Rectangle.Inflate(rectangle, -Theme.S(5), -Theme.S(5)), foreColor, color);
            }
        }
    }
}
