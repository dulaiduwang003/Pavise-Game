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
        private const int DlgW = 560;
        // 正文短就矮 长就撑到上限之后交给滚动条 别让短公告拖一大片空白
        private const int BodyTop = 126, BottomBar = 74;
        private const int MinBodyH = 90, MaxBodyH = 300;
        private const int PadX = 30;

        private int dlgH;

        private readonly NoticeInfo notice;

        public NoticeDialog(NoticeInfo n)
        {
            notice = n;
            Text = Lang.T("v230.notice.title");
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            BackColor = Theme.Bg; ForeColor = Theme.Fg; Font = Theme.UI(9.5f, false);
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);

            Paint += PaintChrome;
            MouseDown += DragMove;

            var close = new Label();
            close.Text = "✕";
            close.ForeColor = Theme.Faint; close.BackColor = Color.Transparent;
            close.Font = Theme.UI(10f, false);
            close.TextAlign = ContentAlignment.MiddleCenter;
            close.Cursor = Cursors.Hand;
            close.SetBounds(Theme.S(DlgW - 42), Theme.S(12), Theme.S(26), Theme.S(26));
            close.MouseEnter += delegate { close.ForeColor = Theme.Accent; };
            close.MouseLeave += delegate { close.ForeColor = Theme.Faint; };
            close.Click += delegate { Close(); };
            Controls.Add(close);

            // 自绘只读文本区 原生 TextBox 的滚动条是系统灰 跟这套界面不搭
            var bodyBox = new NoticeBodyView();
            bodyBox.Text = notice != null && !string.IsNullOrEmpty(notice.Body)
                ? notice.Body : Lang.T("v230.notice.empty");
            int bodyW = Theme.S(DlgW - PadX * 2);
            int wanted = bodyBox.MeasureHeight(bodyW);
            int bodyH = Math.Max(Theme.S(MinBodyH), Math.Min(Theme.S(MaxBodyH), wanted));
            dlgH = Theme.S(BodyTop) + bodyH + Theme.S(BottomBar);
            ClientSize = new Size(Theme.S(DlgW), dlgH);
            bodyBox.SetBounds(Theme.S(PadX), Theme.S(BodyTop), bodyW, bodyH);
            Controls.Add(bodyBox);

            int btnY = dlgH - Theme.S(56);
            var ok = new PillButton(Lang.T("v230.notice.ok"), BtnKind.Primary);
            ok.SetBounds(Theme.S(DlgW - 158), btnY, Theme.S(130), Theme.S(36));
            ok.Click += delegate { Close(); };
            Controls.Add(ok);

            if (notice != null && !string.IsNullOrEmpty(notice.Url))
            {
                var open = new PillButton(Lang.T("v230.notice.open"), BtnKind.Normal);
                open.SetBounds(Theme.S(DlgW - 308), btnY, Theme.S(140), Theme.S(36));
                open.Click += delegate { OpenNoticeUrl(); };
                Controls.Add(open);
            }
        }

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
            var frame = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var pen = new Pen(Theme.Stroke)) g.DrawRectangle(pen, frame);
            using (var accent = new Pen(Theme.Accent, Math.Max(2f, Theme.S(2))))
                g.DrawLine(accent, 0, Theme.S(1), Theme.S(96), Theme.S(1));

            TextRenderer.DrawText(g, Lang.T("v230.notice.title"), Theme.Mono(6f),
                new Rectangle(Theme.S(PadX), Theme.S(26), Theme.S(DlgW - PadX * 2), Theme.S(16)),
                Theme.Faint, TextFormatFlags.Left | TextFormatFlags.NoPadding);

            string title = notice != null ? notice.Title : "";
            TextRenderer.DrawText(g, title, Theme.UI(13.5f, true),
                new Rectangle(Theme.S(PadX), Theme.S(48), Theme.S(DlgW - PadX * 2), Theme.S(56)),
                Theme.Fg, TextFormatFlags.Left | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);

            var line = new Rectangle(Theme.S(PadX), Theme.S(112), Theme.S(DlgW - PadX * 2), Math.Max(1, Theme.S(1)));
            using (var b = new SolidBrush(Theme.Stroke)) g.FillRectangle(b, line);

            if (notice != null && !string.IsNullOrEmpty(notice.Url))
                TextRenderer.DrawText(g, notice.Url, Theme.UI(7.6f, false),
                    new Rectangle(Theme.S(PadX), dlgH - Theme.S(48), Theme.S(DlgW - 330), Theme.S(20)),
                    Theme.Faint, TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }

        private void DragMove(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            Native.ReleaseCapture();
            Native.SendMessage(Handle, Native.WM_NCLBUTTONDOWN, (IntPtr)Native.HT_CAPTION, IntPtr.Zero);
        }
    }
}
