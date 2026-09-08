// @author bdth 2074055628@qq.com
// 文件用途 公告正文的自绘只读文本区 自己换行自己滚 滚动条沿用选择器那套装甲画法
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
    // 原生 TextBox 的滚动条是系统灰 跟整个界面不是一套东西 所以这里自己画
    //   只读展示 不接收输入 不做选中复制 公告本来就短
    internal sealed class NoticeBodyView : Control
    {
        private const int RailW = 14;

        // 内边距由调用方定 正文直接排在弹窗背景上时留 0 才能跟标题左对齐
        internal int InsetX = 14, InsetY = 12;

        // 行首禁则 这些字符不能落在一行开头 断行时往回收一个字
        private const string NoLineStart = "，。、；：？！）〉》」』】’”…·%";

        private readonly List<string> lines = new List<string>();
        private string text = "";
        private int lineHeight;
        private int top;
        private bool railHot;
        private bool dragging;
        private int dragOffset;

        public NoticeBodyView()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            TabStop = false;
            BackColor = Theme.Card;
            ForeColor = Theme.Fg;
            Font = Theme.UI(9.5f, false);
        }

        public override string Text
        {
            get { return text; }
            set
            {
                text = value ?? "";
                top = 0;
                Relayout();
                Invalidate();
            }
        }

        // 空格连字符和 CJK 都可以断行 拉丁字母数字不行
        private static bool Breakable(char c)
        {
            return c == ' ' || c == '\t' || c == '-' || c == '/' || c >= 0x2E80;
        }

        private int ViewportH { get { return Math.Max(0, ClientSize.Height - Theme.S(InsetY) * 2); } }

        // 按给定宽度量出这段文字要多高 供对话框决定窗口高度
        public int MeasureHeight(int width)
        {
            int keep = ClientSize.Width;
            if (keep != width) { Width = width; Relayout(); }
            return ContentH + Theme.S(InsetY) * 2;
        }

        private int ContentH { get { return lines.Count * lineHeight; } }

        private bool NeedScroll { get { return ContentH > ViewportH && ViewportH > 0; } }

        private int MaxTop { get { return Math.Max(0, ContentH - ViewportH); } }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            Relayout();
        }

        // 按当前宽度重新断行 中文没有空格 只能逐字量
        private void Relayout()
        {
            lines.Clear();
            if (IsDisposed) return;
            int wrapW = ClientSize.Width - Theme.S(InsetX) * 2 - Theme.S(RailW);
            if (wrapW <= 0) return;
            using (Graphics g = CreateGraphics())
            {
                lineHeight = TextRenderer.MeasureText(g, "字Ay", Font).Height + Theme.S(4);
                foreach (string paragraph in text.Split('\n'))
                {
                    if (paragraph.Length == 0) { lines.Add(""); continue; }
                    int start = 0;
                    while (start < paragraph.Length)
                    {
                        int take = 1;
                        while (start + take <= paragraph.Length)
                        {
                            string probe = paragraph.Substring(start, take);
                            if (TextRenderer.MeasureText(g, probe, Font).Width > wrapW) { take--; break; }
                            if (start + take == paragraph.Length) break;
                            take++;
                        }
                        if (take < 1) take = 1;
                        // 拉丁文字断在词边界 别把一个单词劈成两半
                        //   中文没有空格 回退找不到就保持逐字断 回退过头也放弃
                        if (start + take < paragraph.Length
                            && !Breakable(paragraph[start + take]) && !Breakable(paragraph[start + take - 1]))
                        {
                            int back = take;
                            while (back > 1 && !Breakable(paragraph[start + back - 1])) back--;
                            if (back > take / 2) take = back;
                        }
                        // 断在标点前面就往回收一个字 别让标点顶到下一行开头
                        //   一路都是禁则字符时收到 1 就停 宁可标点在行首也不能死循环
                        while (start + take < paragraph.Length && take > 1
                            && NoLineStart.IndexOf(paragraph[start + take]) >= 0) take--;
                        lines.Add(paragraph.Substring(start, take));
                        start += take;
                        // 断点落在空格上时别把它带到下一行开头
                        while (start < paragraph.Length && paragraph[start] == ' ') start++;
                    }
                }
            }
            if (top > MaxTop) top = MaxTop;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            using (var back = new SolidBrush(BackColor)) g.FillRectangle(back, ClientRectangle);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            if (lineHeight <= 0) Relayout();
            int textW = ClientSize.Width - Theme.S(InsetX) * 2 - Theme.S(RailW);
            int first = Math.Max(0, top / Math.Max(1, lineHeight));
            int y = Theme.S(InsetY) - (top - first * lineHeight);
            for (int i = first; i < lines.Count && y < ClientSize.Height; i++, y += lineHeight)
            {
                if (lines[i].Length == 0) continue;
                TextRenderer.DrawText(g, lines[i], Font,
                    new Rectangle(Theme.S(InsetX), y, Math.Max(1, textW), lineHeight), ForeColor,
                    TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            }

            if (NeedScroll) PaintRail(g);
        }

        // 轨道一条细线 拇指是药丸 悬停加宽变亮 和选择器那套一致
        private void PaintRail(Graphics g)
        {
            Rectangle track = TrackRect();
            int trackW = Math.Max(1, Theme.S(2));
            var line = new Rectangle(track.Left + (track.Width - trackW) / 2, track.Top, trackW, track.Height);
            FillPill(g, line, Col.Lerp(BackColor, Theme.Stroke, 0.55f));

            Rectangle thumb = ThumbRect();
            if (thumb.IsEmpty) return;
            Color color = dragging ? Theme.Accent
                : railHot ? Col.Lerp(Theme.StrokeHi, Theme.Accent, 0.7f) : Theme.StrokeHi;
            FillPill(g, thumb, color);
        }

        private Rectangle TrackRect()
        {
            int w = Theme.S(RailW);
            int inset = Theme.S(6);
            return new Rectangle(ClientSize.Width - w, inset, w, Math.Max(0, ClientSize.Height - inset * 2));
        }

        private Rectangle ThumbRect()
        {
            Rectangle track = TrackRect();
            if (!NeedScroll || track.Height <= 0) return Rectangle.Empty;
            int h = (int)Math.Round(track.Height * (double)ViewportH / Math.Max(1, ContentH));
            h = Math.Min(track.Height, Math.Max(Theme.S(32), h));
            int y = track.Top + (int)Math.Round((track.Height - h) * (double)top / Math.Max(1, MaxTop));
            int w = Math.Min(track.Width, Theme.S(railHot || dragging ? 8 : 6));
            return new Rectangle(track.Left + (track.Width - w) / 2, y, w, h);
        }

        private static void FillPill(Graphics g, Rectangle bounds, Color color)
        {
            if (bounds.Width <= 0 || bounds.Height <= 0) return;
            int d = Math.Min(bounds.Width, bounds.Height);
            using (var path = new GraphicsPath())
            using (var brush = new SolidBrush(color))
            {
                path.AddArc(bounds.Left, bounds.Top, d, d, 180, 180);
                path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 0, 180);
                path.CloseFigure();
                g.FillPath(brush, path);
            }
        }

        private void ScrollTo(int value)
        {
            int clamped = Math.Max(0, Math.Min(MaxTop, value));
            if (clamped == top) return;
            top = clamped;
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (!NeedScroll) return;
            ScrollTo(top - Math.Sign(e.Delta) * lineHeight * 3);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragging)
            {
                Rectangle track = TrackRect();
                Rectangle thumb = ThumbRect();
                int span = Math.Max(1, track.Height - thumb.Height);
                ScrollTo((int)Math.Round((e.Y - track.Top - dragOffset) * (double)MaxTop / span));
                return;
            }
            bool hot = NeedScroll && e.X >= TrackRect().Left;
            if (hot != railHot) { railHot = hot; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (railHot) { railHot = false; Invalidate(); }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || !NeedScroll) return;
            Rectangle thumb = ThumbRect();
            if (thumb.Contains(e.Location)) { dragging = true; dragOffset = e.Y - thumb.Top; Invalidate(); return; }
            if (e.X >= TrackRect().Left) ScrollTo(top + (e.Y < thumb.Top ? -ViewportH : ViewportH));
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (!dragging) return;
            dragging = false;
            Invalidate();
        }
    }
}
