// @author bdth 2074055628@qq.com
// 文件用途 显示更新清单里带下来的公告 纯文本 不渲染富文本
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
    // 公告正文是从网上取回来的 这里只当死字符串画出来
    //   不解析 HTML 不解析 Markdown 不执行任何东西 长度在解析那层就已经截断
    //   链接必须先过白名单 过不了就连按钮都不给 免得把任意地址塞给用户点
    internal sealed class NoticeDialog : Form
    {
        // 版式跟统一弹窗同一套 标题高度随实际行数走 短标题不再压着一片空白
        private const int DlgW = 520;
        private const int PadX = 26;
        private const int TagTop = 17, TagHeight = 16, TagWidth = 74;
        private const int TitleTop = 43;
        private const int MinBodyH = 28, MaxBodyH = 320;
        private const int BtnH = 34, BtnGap = 10, BottomPad = 22;

        private readonly NoticeInfo notice;
        private int lineY;
        private int buttonsLeft;
        private float sweep;

        public NoticeDialog(NoticeInfo n)
        {
            notice = n;
            Text = Lang.T("v230.notice.title");
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            BackColor = Theme.Bg; ForeColor = Theme.Fg; Font = Theme.UI(9.5f, false);
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            KeyPreview = true;

            Paint += PaintChrome;
            MouseDown += DragMove;

            int w = Theme.S(DlgW);
            int contentW = w - Theme.S(PadX) * 2;
            int titleH = MeasureTitle(contentW);
            int bodyTop = Theme.S(TitleTop) + titleH + Theme.S(20);

            // 正文直接排在弹窗背景上 不给它单独的底色块
            var bodyBox = new NoticeBodyView();
            bodyBox.BackColor = BackColor;
            bodyBox.InsetX = 0; bodyBox.InsetY = 2;
            bodyBox.ForeColor = Theme.Dim;
            bodyBox.Text = notice != null && !string.IsNullOrEmpty(notice.Body)
                ? notice.Body : Lang.T("v230.notice.empty");
            int wanted = bodyBox.MeasureHeight(contentW);
            int bodyH = Math.Max(Theme.S(MinBodyH), Math.Min(Theme.S(MaxBodyH), wanted));
            lineY = bodyTop + bodyH + Theme.S(18);
            int h = lineY + Theme.S(16) + Theme.S(BtnH) + Theme.S(BottomPad);
            ClientSize = new Size(w, h);
            bodyBox.SetBounds(Theme.S(PadX), bodyTop, contentW, bodyH);
            Controls.Add(bodyBox);

            var close = new Label();
            close.Text = "✕";
            close.ForeColor = Theme.Faint; close.BackColor = Color.Transparent;
            close.Font = Theme.UI(10f, false);
            close.TextAlign = ContentAlignment.MiddleCenter;
            close.Cursor = Cursors.Hand;
            close.SetBounds(w - Theme.S(PadX) - Theme.S(24), Theme.S(13), Theme.S(24), Theme.S(24));
            close.MouseEnter += delegate
            { close.ForeColor = Theme.Accent; close.BackColor = Col.Lerp(Theme.Bg, Theme.Accent, 0.16f); };
            close.MouseLeave += delegate
            { close.ForeColor = Theme.Faint; close.BackColor = Color.Transparent; };
            close.Click += delegate { Close(); };
            Controls.Add(close);

            int by = h - Theme.S(BottomPad) - Theme.S(BtnH);
            var ok = new PillButton(Lang.T("v230.notice.ok"), BtnKind.Primary);
            ok.Size = new Size(Theme.S(108), Theme.S(BtnH));
            ok.Location = new Point(w - Theme.S(PadX) - ok.Width, by);
            ok.Click += delegate { Close(); };
            Controls.Add(ok);
            buttonsLeft = ok.Left;

            if (notice != null && !string.IsNullOrEmpty(notice.Url))
            {
                var open = new PillButton(Lang.T("v230.notice.open"), BtnKind.Normal);
                open.Bg = Theme.Card;
                open.Size = new Size(Theme.S(120), Theme.S(BtnH));
                open.Location = new Point(ok.Left - Theme.S(BtnGap) - open.Width, by);
                open.Click += delegate { OpenNoticeUrl(); };
                Controls.Add(open);
                buttonsLeft = open.Left;
            }

            KeyDown += delegate(object sender, KeyEventArgs e)
            { if (e.KeyCode == Keys.Escape || e.KeyCode == Keys.Enter) Close(); };

            var timer = new Timer();
            timer.Interval = 33;
            timer.Tick += delegate
            {
                sweep += 0.012f;
                if (sweep > 1f) sweep -= 1f;
                Invalidate(new Rectangle(0, 0, Width, Theme.S(4)));
            };
            timer.Start();
            Disposed += delegate { timer.Stop(); timer.Dispose(); };
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Fx.EnterForm(this);
        }

        // 标题占几行由实际文字决定 最多两行 再长交给省略
        private int MeasureTitle(int width)
        {
            string title = notice != null ? notice.Title : "";
            if (string.IsNullOrEmpty(title)) return Theme.S(4);
            using (Graphics g = CreateGraphics())
            {
                int line = TextRenderer.MeasureText(g, "字Ay", TitleFont).Height;
                int measured = TextRenderer.MeasureText(g, title, TitleFont,
                    new Size(width, line * 4), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding).Height;
                return Math.Max(line, Math.Min(line * 2, measured));
            }
        }

        private static Font TitleFont { get { return Theme.UI(12.5f, true); } }

        // 地址在解析那层已经过了白名单 这里再核一遍 中间任何环节改过都不放行
        private void OpenNoticeUrl()
        {
            string url = notice == null ? null : notice.Url;
            if (!UpdateChecker.IsTrustedNoticeUrl(url)) return;
            try { using (System.Diagnostics.Process.Start(url)) { } }
            catch { PaviseDialog.Warn(this, App.DisplayName, Lang.T("v211.link.failed")); }
        }

        private void PaintChrome(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color accent = Theme.Accent;

            int barH = Theme.S(4);
            using (var bar = new SolidBrush(accent)) g.FillRectangle(bar, 0, 0, Width, barH);
            using (var glow = new LinearGradientBrush(
                new Rectangle(0, 0, Math.Max(1, Width), barH),
                Color.FromArgb(0, 255, 255, 255), Color.FromArgb(150, 255, 255, 255),
                LinearGradientMode.Horizontal))
            {
                int bw = Theme.S(150);
                var band = new Rectangle((int)(sweep * (Width + bw)) - bw, 0, bw, barH);
                Region old = g.Clip;
                g.SetClip(new Rectangle(0, 0, Width, barH), CombineMode.Replace);
                g.FillRectangle(glow, band);
                g.Clip = old;
            }

            // 左上角的斜切标签 跟统一弹窗一个形状
            //   宽度跟着文字走 英日的公告一词比中文长 写死会被切掉
            int skew = Theme.S(9);
            string tagText = Lang.T("v230.notice.title");
            Font tagFont = Theme.UI(7.2f, true);
            int tagW = Math.Max(Theme.S(TagWidth),
                TextRenderer.MeasureText(g, tagText, tagFont, Size.Empty, TextFormatFlags.NoPadding).Width + skew * 3);
            var tag = new[]
            {
                new Point(Theme.S(PadX) + skew, Theme.S(TagTop)),
                new Point(Theme.S(PadX) + tagW + skew, Theme.S(TagTop)),
                new Point(Theme.S(PadX) + tagW - skew, Theme.S(TagTop + TagHeight)),
                new Point(Theme.S(PadX) - skew, Theme.S(TagTop + TagHeight))
            };
            using (var fill = new SolidBrush(Color.FromArgb(38, accent))) g.FillPolygon(fill, tag);
            using (var pen = new Pen(Color.FromArgb(120, accent), Math.Max(1f, Theme.S(1))))
                g.DrawPolygon(pen, tag);
            using (var text = new SolidBrush(accent))
            using (var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString(tagText, tagFont, text,
                    new RectangleF(Theme.S(PadX), Theme.S(TagTop), tagW, Theme.S(TagHeight)), fmt);

            string title = notice != null ? notice.Title : "";
            if (!string.IsNullOrEmpty(title))
                TextRenderer.DrawText(g, title, TitleFont,
                    new Rectangle(Theme.S(PadX), Theme.S(TitleTop), Width - Theme.S(PadX) * 2,
                        ClientSize.Height - Theme.S(TitleTop)),
                    Theme.Fg, TextFormatFlags.Left | TextFormatFlags.WordBreak
                        | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);

            using (var sep = new Pen(Theme.Stroke))
                g.DrawLine(sep, Theme.S(PadX), lineY, Width - Theme.S(PadX), lineY);

            // 地址跟按钮同一条基线居中 长地址收省略号 不去挤按钮
            if (notice != null && !string.IsNullOrEmpty(notice.Url))
            {
                int urlW = buttonsLeft - Theme.S(PadX) - Theme.S(12);
                if (urlW > Theme.S(40))
                    TextRenderer.DrawText(g, notice.Url, Theme.UI(8f, false),
                        new Rectangle(Theme.S(PadX), ClientSize.Height - Theme.S(BottomPad) - Theme.S(BtnH),
                            urlW, Theme.S(BtnH)),
                        Theme.Faint, TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                            | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            }

            using (var edge = new Pen(Theme.StrokeHi)) g.DrawRectangle(edge, 0, 0, Width - 1, Height - 1);
        }

        private void DragMove(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            Native.ReleaseCapture();
            Native.SendMessage(Handle, Native.WM_NCLBUTTONDOWN, (IntPtr)Native.HT_CAPTION, IntPtr.Zero);
        }
    }
}
