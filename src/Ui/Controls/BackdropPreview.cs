// @author bdth 2074055628@qq.com
// File purpose Live backdrop preview inside the window appearance group on the Settings page
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
    internal sealed class BackdropPreview : Control
    {
        public string EmptyText = "";

        public BackdropPreview()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Card;
            TabStop = false;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            // Chamfers land on the card and must match the card face, with the backdrop on the card is translucent and a solid patch would show four corners
            if (Backdrop.AppliesTo(this)) Backdrop.PaintOnCard(g, this, ClientRectangle);
            else using (var bg = new SolidBrush(BackColor)) g.FillRectangle(bg, ClientRectangle);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle frame = new Rectangle(0, 0, Width - 1, Height - 1);
            if (frame.Width <= 0 || frame.Height <= 0) return;

            using (GraphicsPath path = Theme.TechPath(frame, Theme.S(8)))
            {
                GraphicsState state = g.Save();
                g.SetClip(path);
                if (Backdrop.Active) Backdrop.PaintPreview(g, frame);
                else
                {
                    using (var fill = new LinearGradientBrush(frame,
                        Col.Lerp(Theme.Inset, Theme.Card, 0.18f), Theme.Inset,
                        LinearGradientMode.ForwardDiagonal))
                        g.FillRectangle(fill, frame);
                    using (var wash = new SolidBrush(Col.Alpha(Theme.Accent, Theme.LightMode ? 10 : 7)))
                        g.FillPolygon(wash, new[]
                        {
                            new Point(frame.Left, frame.Bottom),
                            new Point(frame.Right, frame.Top),
                            new Point(frame.Right, frame.Bottom)
                        });
                }
                g.Restore(state);

                using (var edge = new Pen(Backdrop.Active
                    ? Col.Alpha(Theme.Accent, 158) : Theme.StrokeHi))
                    g.DrawPath(edge, path);
            }

            if (!Backdrop.Active)
            {
                int icon = Theme.S(24);
                Glyphs.Draw(g, "settings",
                    new Rectangle((Width - icon) / 2, Theme.S(22), icon, icon), Theme.Faint);
                TextRenderer.DrawText(g, EmptyText ?? "", Theme.Mono(7.0f),
                    new Rectangle(Theme.S(8), Theme.S(56), Width - Theme.S(16), Theme.S(20)),
                    Theme.Faint, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                        | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            }

            TextRenderer.DrawText(g, "LIVE PREVIEW", Theme.Mono(5.9f),
                new Rectangle(Theme.S(8), Theme.S(6), Width - Theme.S(16), Theme.S(14)),
                Backdrop.Active ? Theme.Accent : Theme.Faint,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }
}
