using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
    internal sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer()
            : base(new DarkMenuColors())
        {
            base.RoundedEdges = false;
        }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using (SolidBrush brush = new SolidBrush(Theme.Card))
            {
                e.Graphics.FillRectangle(brush, e.AffectedBounds);
            }
        }

        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
        {
            using (SolidBrush brush = new SolidBrush(Theme.Card))
            {
                e.Graphics.FillRectangle(brush, e.AffectedBounds);
            }
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            Rectangle rect = new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
            using (Pen pen = new Pen(Theme.Stroke))
            {
                e.Graphics.DrawRectangle(pen, rect);
            }
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            Graphics graphics = e.Graphics;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            ToolStripItem currentItem = e.Item;
            bool submenuOpen = currentItem is ToolStripMenuItem && ((ToolStripMenuItem)currentItem).DropDown.Visible;
            if (currentItem.Selected || submenuOpen)
            {
                Rectangle r = new Rectangle(Theme.S(4), Theme.S(2), currentItem.Width - Theme.S(8), currentItem.Height - Theme.S(4));
                Theme.FillRound(graphics, r, Theme.S(7), submenuOpen ? Theme.Sel : Theme.CardHover);
            }
            ToolStripMenuItem toolStripMenuItem = currentItem as ToolStripMenuItem;
            if (toolStripMenuItem != null && toolStripMenuItem.Checked)
            {
                int scaledDimension = Theme.S(13);
                int x = currentItem.Width - Theme.S(14) - scaledDimension;
                DrawCheck(graphics, new Rectangle(x, (currentItem.Height - scaledDimension) / 2, scaledDimension, scaledDimension));
            }
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
        }

        internal static void DrawCheck(Graphics g, Rectangle rect)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (Pen pen = new Pen(Theme.Accent, Math.Max(2f, Theme.S(2))))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;
                PointF pointF = new PointF((float)rect.X + (float)rect.Width * 0.12f, (float)rect.Y + (float)rect.Height * 0.52f);
                PointF currentPointF = new PointF((float)rect.X + (float)rect.Width * 0.4f, (float)rect.Y + (float)rect.Height * 0.78f);
                PointF candidatePointF = new PointF((float)rect.X + (float)rect.Width * 0.86f, (float)rect.Y + (float)rect.Height * 0.24f);
                g.DrawLines(pen, new PointF[3] { pointF, currentPointF, candidatePointF });
            }
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = (e.Item.Enabled ? Theme.Fg : Theme.Faint);
            Rectangle textRectangle = e.TextRectangle;
            if (textRectangle.Height > 0 && textRectangle.Height < e.Item.Height)
            {
                int ratio = (e.Item.Height - textRectangle.Height) / 2;
                if (ratio != textRectangle.Y)
                {
                    e.TextRectangle = new Rectangle(textRectangle.X, ratio, textRectangle.Width, textRectangle.Height);
                }
            }
            base.OnRenderItemText(e);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int top = e.Item.Height / 2;
            using (Pen pen = new Pen(Col.Alpha(Theme.Stroke, 150)))
            {
                e.Graphics.DrawLine(pen, Theme.S(12), top, e.Item.Width - Theme.S(12), top);
            }
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = (e.Item.Selected ? Theme.Fg : Theme.Dim);
            base.OnRenderArrow(e);
        }
    }
}
