// @author bdth 2074055628@qq.com
// 文件用途 核心分配的使用说明 编号步骤加一条脚注 高度按实际文本量算

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
    internal sealed class CoreGuide : Control
    {
        private sealed class Step
        {
            public string No;
            public string Title;
            public string Body;
            public Rectangle Rect;
            public int BodyH;
        }

        private const int PadX = 16;
        private const int HeadH = 34;
        private const int ChipW = 26;
        private const int ChipH = 17;
        private const int StepGap = 12;
        private const int NoteGap = 13;

        private readonly List<Step> steps = new List<Step>();
        private string title = "", tag = "", note = "";
        private int noteH;

        public CoreGuide()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bg;
        }

        public void SetContent(string head, string corner, string footer, string[][] rows)
        {
            title = head ?? ""; tag = corner ?? ""; note = footer ?? "";
            steps.Clear();
            if (rows != null)
                foreach (string[] r in rows)
                    if (r != null && r.Length >= 3)
                        steps.Add(new Step { No = r[0], Title = r[1], Body = r[2] });
            Invalidate();
        }

        public int LayoutFor(int width)
        {
            int textLeft = Theme.S(PadX) + Theme.S(ChipW) + Theme.S(11);
            int textW = width - textLeft - Theme.S(PadX);
            int y = Theme.S(HeadH);

            using (var bmp = new Bitmap(1, 1))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                foreach (Step s in steps)
                {
                    int bodyH = TextRenderer.MeasureText(g, s.Body, Theme.UI(8.3f, false),
                        new Size(textW, 0), TextFormatFlags.WordBreak).Height;
                    s.BodyH = bodyH;
                    s.Rect = new Rectangle(0, y, width, Theme.S(19) + bodyH);
                    y += s.Rect.Height + Theme.S(StepGap);
                }
                int noteW = width - Theme.S(PadX) * 2 - Theme.S(10);
                noteH = note.Length == 0 ? 0 : TextRenderer.MeasureText(g, note,
                    Theme.UI(8.1f, false), new Size(noteW, 0), TextFormatFlags.WordBreak).Height;
            }
            if (steps.Count > 0) y -= Theme.S(StepGap);
            if (noteH > 0) y += Theme.S(NoteGap) + noteH + Theme.S(4);
            return y + Theme.S(14);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            using (var back = new SolidBrush(BackColor)) g.FillRectangle(back, ClientRectangle);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            var body = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath path = Theme.TechPath(body, Theme.S(10)))
            {
                using (var b = new SolidBrush(Theme.Card)) g.FillPath(b, path);
                using (var p = new Pen(Theme.Stroke)) g.DrawPath(p, path);
            }
            using (var p = new Pen(Theme.Accent, Math.Max(1.8f, Theme.S(2))))
                g.DrawLine(p, Theme.S(2), 0, Theme.S(40), 0);

            const TextFormatFlags Line = TextFormatFlags.VerticalCenter
                | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
            var head = new Rectangle(Theme.S(PadX), 0, Width - Theme.S(PadX) * 2, Theme.S(HeadH));
            TextRenderer.DrawText(g, title, Theme.UI(9.5f, true), head, Theme.Fg,
                TextFormatFlags.Left | Line);
            TextRenderer.DrawText(g, tag, Theme.Mono(6.8f), head, Theme.Faint,
                TextFormatFlags.Right | Line);
            using (var p = new Pen(Theme.Stroke))
                g.DrawLine(p, Theme.S(PadX), Theme.S(HeadH) - Theme.S(6),
                    Width - Theme.S(PadX), Theme.S(HeadH) - Theme.S(6));

            int textLeft = Theme.S(PadX) + Theme.S(ChipW) + Theme.S(11);
            int textW = Width - textLeft - Theme.S(PadX);
            int lastBottom = Theme.S(HeadH);

            foreach (Step s in steps)
            {
                var chip = new Rectangle(Theme.S(PadX), s.Rect.Top + Theme.S(2),
                    Theme.S(ChipW), Theme.S(ChipH));
                using (GraphicsPath path = Theme.TechPath(chip, Theme.S(4)))
                {
                    using (var b = new SolidBrush(Col.Alpha(Theme.Accent, 40))) g.FillPath(b, path);
                    using (var p = new Pen(Col.Alpha(Theme.Accent, 190))) g.DrawPath(p, path);
                }
                TextRenderer.DrawText(g, s.No, Theme.Mono(7.2f), chip, Theme.Accent,
                    TextFormatFlags.HorizontalCenter | Line);

                TextRenderer.DrawText(g, s.Title, Theme.UI(8.8f, true),
                    new Rectangle(textLeft, s.Rect.Top, textW, Theme.S(19)), Theme.Fg,
                    TextFormatFlags.Left | Line | TextFormatFlags.EndEllipsis);
                TextRenderer.DrawText(g, s.Body, Theme.UI(8.3f, false),
                    new Rectangle(textLeft, s.Rect.Top + Theme.S(19), textW, s.BodyH), Theme.Dim,
                    TextFormatFlags.Left | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
                lastBottom = s.Rect.Bottom;
            }

            if (noteH <= 0) return;
            int ny = lastBottom + Theme.S(NoteGap);
            using (var b = new SolidBrush(Col.Alpha(Theme.Accent2, 190)))
                g.FillRectangle(b, new Rectangle(Theme.S(PadX), ny, Theme.S(3), noteH));
            TextRenderer.DrawText(g, note, Theme.UI(8.1f, false),
                new Rectangle(Theme.S(PadX) + Theme.S(10), ny,
                    Width - Theme.S(PadX) * 2 - Theme.S(10), noteH),
                Theme.Faint,
                TextFormatFlags.Left | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
        }
    }
}
