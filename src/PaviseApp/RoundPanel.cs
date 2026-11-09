using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
    internal class RoundPanel : Panel
    {
        public int Radius = Dpi.S(10);

        public Color Fill = Theme.Card;

        public Color Border = Color.Empty;

        public bool AccentEdge;

        public bool UseBackdrop = true;

        public RoundPanel()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            BackColor = Theme.Bg;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            if (Backdrop.AppliesTo(this))
            {
                Backdrop.Paint(e.Graphics, this, e.ClipRectangle);
            }
            else
            {
                base.OnPaintBackground(e);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (base.Width < 2 || base.Height < 2)
            {
                return;
            }
            Graphics graphics = e.Graphics;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle rectangle = new Rectangle(0, 0, base.Width - 1, base.Height - 1);
            using (GraphicsPath graphicsPath = Theme.TechPath(rectangle, Math.Max(Theme.S(5), Radius / 2)))
            {
                if (Theme.LightMode)
                {
                    Color color = Backdrop.CardFill(this, Col.Lerp(Fill, Theme.Accent, 0.035f));
                    Color color2 = Backdrop.CardFill(this, Col.Lerp(Fill, Theme.Bg, 0.13f));
                    using (LinearGradientBrush brush = new LinearGradientBrush(rectangle, color, color2, LinearGradientMode.Vertical))
                    {
                        graphics.FillPath(brush, graphicsPath);
                    }
                }
                else
                {
                    using (SolidBrush brush2 = new SolidBrush(Backdrop.CardFill(this, Fill)))
                    {
                        graphics.FillPath(brush2, graphicsPath);
                    }
                }
                if (Theme.LightMode && AccentEdge)
                {
                    graphics.SetClip(graphicsPath);
                    Rectangle rect = new Rectangle(Math.Max(0, base.Width - Theme.S(220)), 0, Theme.S(220), Theme.S(104));
                    using (LinearGradientBrush brush3 = new LinearGradientBrush(rect, Col.Alpha(Theme.Accent, 0), Col.Alpha(Theme.Accent, 24), LinearGradientMode.BackwardDiagonal))
                    {
                        graphics.FillPolygon(brush3, new Point[3]
                        {
                            new Point(rect.Left, rect.Top),
                            new Point(rect.Right, rect.Top),
                            new Point(rect.Right, rect.Bottom)
                        });
                    }
                    graphics.ResetClip();
                }
                if (Border != Color.Empty)
                {
                    using (Pen pen = new Pen(Border))
                    {
                        graphics.DrawPath(pen, graphicsPath);
                    }
                }
                using (Pen pen2 = new Pen(Theme.LightMode ? Col.Alpha(Theme.Accent, 24) : Col.Alpha(Color.White, 13)))
                {
                    graphics.DrawLine(pen2, Theme.S(1), Theme.S(1), Math.Max(Theme.S(2), base.Width - Radius), Theme.S(1));
                }
                if (Theme.LightMode)
                {
                    using (Pen pen3 = new Pen(Col.Alpha(Theme.StrokeHi, 34)))
                    {
                        graphics.DrawLine(pen3, Theme.S(8), base.Height - Theme.S(2), base.Width - Theme.S(8), base.Height - Theme.S(2));
                        graphics.DrawLine(pen3, base.Width - Theme.S(2), Theme.S(10), base.Width - Theme.S(2), base.Height - Theme.S(10));
                    }
                }
            }
            if (!AccentEdge)
            {
                return;
            }
            using (Pen pen4 = new Pen(Theme.Accent, Math.Max(1f, Theme.S(1))))
            {
                graphics.DrawLine(pen4, Theme.S(1), Theme.S(1), Math.Min(base.Width - Theme.S(8), Theme.S(58)), Theme.S(1));
                graphics.DrawLine(pen4, base.Width - Theme.S(13), Theme.S(2), base.Width - Theme.S(2), Theme.S(13));
            }
        }
    }
}
