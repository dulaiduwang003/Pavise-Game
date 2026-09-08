// @author bdth 2074055628@qq.com
// 文件用途 概览页底栏的帮助与反馈弹窗 收纳教程 问卷和 Bug 反馈三条外链
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
    // 三条外链原本平铺在底栏 占掉大半条 合成一个入口后底栏留给公告
    //   这里每条仍画外链标记 点了照样跳浏览器 不在窗口里套浏览器
    internal sealed class HelpDialog : Form
    {
        private const int DlgW = 520, DlgH = 330;
        private const int PadX = 28;

        public HelpDialog()
        {
            Text = Lang.T("v230.help.title");
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            ClientSize = new Size(Theme.S(DlgW), Theme.S(DlgH));
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

            int w = DlgW - PadX * 2;
            int y = 86;
            AddLink(y, w, Lang.T("v211.link.guide"), "GUIDE // 01", "info", App.GuideUrl);
            y += 60;
            AddLink(y, w, Lang.T("v211.link.survey"), "SURVEY // 02", "chart", App.SurveyUrl);
            y += 60;
            AddLink(y, w, Lang.T("v211.link.bug"), "REPORT // 03", "search", App.BugUrl);

            var ok = new PillButton(Lang.T("contact.close"), BtnKind.Primary);
            ok.SetBounds(Theme.S(DlgW - 158), Theme.S(DlgH - 56), Theme.S(130), Theme.S(36));
            ok.Click += delegate { Close(); };
            Controls.Add(ok);
        }

        private void AddLink(int y, int w, string text, string code, string glyph, string url)
        {
            var btn = new RogLinkButton(text, code, glyph);
            btn.Bg = Theme.Bg;
            btn.SetBounds(Theme.S(PadX), Theme.S(y), Theme.S(w), Theme.S(48));
            btn.Click += delegate { OpenExternal(url); };
            Controls.Add(btn);
        }

        // 和概览页同一条规矩 只放行写死在 App 里的 https 常量
        private void OpenExternal(string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
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

            TextRenderer.DrawText(g, Lang.T("v230.help.title"), Theme.UI(14f, true),
                new Rectangle(Theme.S(PadX), Theme.S(28), Theme.S(DlgW - PadX * 2), Theme.S(30)),
                Theme.Fg, TextFormatFlags.Left | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, Lang.T("v230.help.sub"), Theme.UI(8.4f, false),
                new Rectangle(Theme.S(PadX), Theme.S(58), Theme.S(DlgW - PadX * 2), Theme.S(20)),
                Theme.Dim, TextFormatFlags.Left | TextFormatFlags.NoPadding);
        }

        private void DragMove(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            Native.ReleaseCapture();
            Native.SendMessage(Handle, Native.WM_NCLBUTTONDOWN, (IntPtr)Native.HT_CAPTION, IntPtr.Zero);
        }
    }
}
