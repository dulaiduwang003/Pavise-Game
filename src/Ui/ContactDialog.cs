// @author bdth 2074055628@qq.com
// 文件用途 启动时的联系方式弹窗 反馈渠道与版本更新检查

using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
    internal sealed class ContactDialog : Form
    {
        private const int DlgW = 640, DlgH = 520;
        private const int RailW = 190;
        private const int RailSlant = 32;
        private const int BodyX = RailW + 26;
        private const int BodyW = DlgW - BodyX - 30;

        public const string QqGroup = "1051472054";
        public const string QqGroup2 = "1101249532";
        public const string QqGroup3 = "383761286";
        public const string WeChat = "Ssssssstyle";
        public const string Douyin = "44601770838";
        public const string BiliUrl = "https://space.bilibili.com/3461581985811085";

        private Bitmap logo;

        public ContactDialog()
        {
            Text = Lang.T("contact.title");
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = true;
            Icon taskbarIcon = IconArt.MakeIcon(Theme.S(24));
            Icon = taskbarIcon;
            ClientSize = new Size(Theme.S(DlgW), Theme.S(DlgH));
            BackColor = Theme.Bg; ForeColor = Theme.Fg; Font = Theme.UI(9.5f, false);
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            logo = IconArt.Render(Theme.S(58), Theme.CurrentMode, true);
            Disposed += delegate
            {
                try { taskbarIcon.Dispose(); } catch { }
                try { if (logo != null) logo.Dispose(); } catch { }
            };

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
            close.Click += delegate { Finish(); };
            Controls.Add(close);

            int half = (BodyW - 10) / 2;
            int rightX = BodyX + half + 10;
            int y = 102;

            AddRow(BodyX, y, BodyW, Lang.T("contact.wechat"), WeChat, CopyAction(WeChat), Lang.T("contact.copy"));
            y += 58;
            AddRow(BodyX, y, half, Lang.T("contact.qq"), QqGroup, CopyAction(QqGroup), Lang.T("contact.copy"));
            AddRow(rightX, y, half, Lang.T("contact.qq2"), QqGroup2, CopyAction(QqGroup2), Lang.T("contact.copy"));
            y += 58;
            AddRow(BodyX, y, half, Lang.T("contact.qq3"), QqGroup3, CopyAction(QqGroup3), Lang.T("contact.copy"));
            AddRow(rightX, y, half, Lang.T("contact.douyin"), Douyin, CopyAction(Douyin), Lang.T("contact.copy"));
            y += 58;
            AddRow(BodyX, y, BodyW, Lang.T("contact.bili") + " · " + Lang.T("contact.bili.note"),
                "space.bilibili.com", OpenAction(BiliUrl), Lang.T("contact.open"));
            y += 70;

            BuildUpdateArea(BodyX, y, BodyW);

            var freeNote = new Label();
            freeNote.Text = Lang.T("contact.free.n");
            freeNote.ForeColor = Theme.Dim; freeNote.BackColor = Theme.Bg;
            freeNote.Font = Theme.UI(7.5f, false);
            freeNote.UseCompatibleTextRendering = false;
            freeNote.SetBounds(Theme.S(BodyX + 2), Theme.S(DlgH - 104), Theme.S(BodyW - 4), Theme.S(38));
            Controls.Add(freeNote);

            var ok = new PillButton(Lang.T("contact.enter"), BtnKind.Primary);
            ok.SetBounds(Theme.S(DlgW - 176), Theme.S(DlgH - 58), Theme.S(146), Theme.S(38));
            ok.Click += delegate { Finish(); };
            Controls.Add(ok);
        }

        private Action<ChannelRow> CopyAction(string value)
        {
            return delegate(ChannelRow row)
            {
                try
                {
                    Clipboard.SetText(value);
                    row.SetHint(Lang.T("contact.copied"), true);
                }
                catch { row.SetHint(Lang.T("contact.copyfail"), true); }
            };
        }

        private Action<ChannelRow> OpenAction(string url)
        {
            return delegate(ChannelRow row)
            {
                try { using (Process.Start(url)) { } }
                catch { }
            };
        }

        private void AddRow(int x, int y, int w, string label, string value,
            Action<ChannelRow> action, string hint)
        {
            var row = new ChannelRow(label, value, hint, action);
            row.Bg = Theme.Bg;
            row.SetBounds(Theme.S(x), Theme.S(y), Theme.S(w), Theme.S(52));
            Controls.Add(row);
        }

        private void BuildUpdateArea(int x, int y, int w)
        {
            var ver = new Label();
            ver.Text = Lang.T("contact.update") + "  ·  " + App.VersionTag;
            ver.ForeColor = Theme.Fg; ver.BackColor = Theme.Bg;
            ver.Font = Theme.UI(9.5f, true);
            ver.UseCompatibleTextRendering = false;
            ver.SetBounds(Theme.S(x + 2), Theme.S(y + 8), Theme.S(180), Theme.S(22));
            Controls.Add(ver);

            int btnW = 96;
            var status = new Label();
            status.ForeColor = Theme.Dim; status.BackColor = Theme.Bg;
            status.Font = Theme.UI(7.6f, false);
            status.UseCompatibleTextRendering = false;
            status.AutoEllipsis = true;
            status.SetBounds(Theme.S(x + 2), Theme.S(y + 34), Theme.S(w - 4), Theme.S(16));
            Controls.Add(status);

            var btn = new PillButton(Lang.T("contact.update.check"));
            btn.Bg = Theme.Bg;
            btn.SetBounds(Theme.S(x + w - btnW), Theme.S(y + 2), Theme.S(btnW), Theme.S(32));
            string dlUrl = null;
            btn.Click += delegate
            {
                if (dlUrl != null)
                {
                    if (dlUrl.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase))
                        try { using (Process.Start(dlUrl)) { } } catch { }
                    return;
                }
                btn.Enabled = false;
                status.ForeColor = Theme.Dim;
                status.Text = Lang.T("upd.checking");
                UpdateChecker.CheckAsync(delegate(UpdateResult r)
                {
                    try
                    {
                        BeginInvoke((MethodInvoker)delegate
                        {
                            if (btn.IsDisposed) return;
                            btn.Enabled = true;
                            if (!r.Ok)
                            {
                                status.ForeColor = Theme.Dim;
                                status.Text = Lang.T("upd.fail");
                            }
                            else if (r.Newer)
                            {
                                dlUrl = r.Url;
                                btn.Text = Lang.T("btn.download");
                                status.ForeColor = Theme.Green;
                                status.Text = Lang.F("upd.newver", r.Latest, App.VersionTag);
                            }
                            else
                            {
                                status.ForeColor = Theme.Green;
                                status.Text = Lang.F("upd.latest", App.VersionTag);
                            }
                        });
                    }
                    catch { }
                });
            };
            Controls.Add(btn);
        }

        private void Finish()
        {
            DialogResult = DialogResult.OK;
            Close();
        }

        private void PaintChrome(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            int w = ClientSize.Width, h = ClientSize.Height;
            int cut = Theme.S(26);

            using (var path = new GraphicsPath())
            {
                path.AddLine(cut, 0, w, 0);
                path.AddLine(w, 0, w, h - cut);
                path.AddLine(w, h - cut, w - cut, h);
                path.AddLine(w - cut, h, 0, h);
                path.AddLine(0, h, 0, cut);
                path.CloseFigure();
                using (var fill = new SolidBrush(Theme.Bg)) g.FillPath(fill, path);
                using (var pen = new Pen(Theme.Stroke, Theme.S(1))) g.DrawPath(pen, path);
                Region = new Region(path);
            }

            int railW = Theme.S(RailW);
            int slant = Theme.S(RailSlant);
            using (var rail = new GraphicsPath())
            {
                rail.AddLine(cut, 0, railW, 0);
                rail.AddLine(railW, 0, railW - slant, h);
                rail.AddLine(railW - slant, h, 0, h);
                rail.AddLine(0, h, 0, cut);
                rail.CloseFigure();
                using (var fill = new SolidBrush(Theme.Nav)) g.FillPath(fill, rail);
            }
            using (var pen = new Pen(Theme.Accent, Theme.S(2)))
                g.DrawLine(pen, railW, 0, railW - slant, h);
            using (var pen = new Pen(Col.Alpha(Theme.Accent, 60), Theme.S(1)))
                g.DrawLine(pen, railW + Theme.S(5), 0, railW - slant + Theme.S(5), h);

            using (var pen = new Pen(Col.Alpha(Theme.Accent, 30), Theme.S(1)))
                for (int i = 0; i < 5; i++)
                {
                    int sx = Theme.S(24 + i * 10);
                    g.DrawLine(pen, sx, h - Theme.S(60), sx - Theme.S(12), h - Theme.S(16));
                }

            int lx = Theme.S(24);
            if (logo != null) g.DrawImageUnscaled(logo, Theme.S(50), Theme.S(44));
            using (var brush = new SolidBrush(Theme.Fg))
                g.DrawString("PAVISE", Theme.UI(15f, true), brush, Theme.S(42), Theme.S(112));
            using (var brush = new SolidBrush(Theme.Faint))
                g.DrawString("C O R E   C O N T R O L", Theme.Mono(6.4f), brush, Theme.S(38), Theme.S(142));
            using (var pen = new Pen(Theme.Accent, Theme.S(2)))
                g.DrawLine(pen, Theme.S(44), Theme.S(164), Theme.S(122), Theme.S(164));
            using (var brush = new SolidBrush(Theme.Accent))
                g.DrawString("//  " + App.VersionTag, Theme.Mono(7.5f), brush, Theme.S(56), Theme.S(176));

            using (var brush = new SolidBrush(Theme.Accent))
            {
                g.DrawString("Pavise 完全免费", Theme.UI(8.4f, true), brush, lx - Theme.S(6), h - Theme.S(132));
                g.DrawString("禁止倒卖", Theme.UI(8.4f, true), brush, lx - Theme.S(6), h - Theme.S(112));
            }

            using (var brush = new SolidBrush(Theme.Faint))
                g.DrawString("PAVISE  //  CONTACT", Theme.Mono(6.75f), brush, Theme.S(BodyX + 2), Theme.S(22));
            using (var brush = new SolidBrush(Theme.Fg))
                g.DrawString(Lang.T("contact.title"), Theme.UI(15f, true), brush, Theme.S(BodyX - 2), Theme.S(38));
            using (var brush = new SolidBrush(Theme.Dim))
                g.DrawString(Lang.T("contact.sub"), Theme.UI(8.2f, false), brush, Theme.S(BodyX), Theme.S(74));

            int sepY = Theme.S(102 + 58 * 3 + 60);
            using (var grad = new LinearGradientBrush(
                new Rectangle(Theme.S(BodyX), sepY, Theme.S(BodyW), Theme.S(2)),
                Theme.Accent, Col.Alpha(Theme.Accent, 0), LinearGradientMode.Horizontal))
                g.FillRectangle(grad, Theme.S(BodyX), sepY, Theme.S(BodyW), Theme.S(2));
        }

        private void DragMove(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            Native.ReleaseCapture();
            Native.SendMessage(Handle, Native.WM_NCLBUTTONDOWN, (IntPtr)Native.HT_CAPTION, IntPtr.Zero);
        }

        private sealed class ChannelRow : FxControl
        {
            private readonly string label;
            private readonly string value;
            private string hint;
            private bool hintAccent;
            private readonly Action<ChannelRow> action;

            public ChannelRow(string labelText, string valueText, string hintText, Action<ChannelRow> onClick)
            {
                label = labelText;
                value = valueText;
                hint = hintText;
                action = onClick;
            }

            public void SetHint(string text, bool accent)
            {
                hint = text;
                hintAccent = accent;
                Invalidate();
            }

            protected override void OnClick(EventArgs e)
            {
                base.OnClick(e);
                if (action != null) action(this);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                FillBg(g);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var r = new Rectangle(0, 0, Width - 1, Height - 1);
                using (GraphicsPath p = Theme.TechPath(r, Theme.S(8)))
                {
                    Color fill = Col.Lerp(Theme.Card, Theme.CardHover, hover.Value);
                    using (var b = new SolidBrush(fill)) g.FillPath(b, p);
                    using (var pen = new Pen(Col.Lerp(Theme.Stroke, Theme.Accent, hover.Value * 0.7f)))
                        g.DrawPath(pen, p);
                }
                using (var pen = new Pen(Theme.Accent, Math.Max(2, Theme.S(2))))
                    g.DrawLine(pen, 0, Theme.S(12), 0, Height - Theme.S(12));

                TextRenderer.DrawText(g, label, Theme.UI(7.3f, true),
                    new Rectangle(Theme.S(14), Theme.S(7), Width - Theme.S(28), Theme.S(15)),
                    Theme.Faint, TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
                TextRenderer.DrawText(g, value, Theme.UI(11.5f, true),
                    new Rectangle(Theme.S(14), Theme.S(22), Width - Theme.S(74), Theme.S(26)),
                    Theme.Fg, TextFormatFlags.Left | TextFormatFlags.EndEllipsis);

                Color hintColor = hintAccent ? Theme.Green
                    : Col.Lerp(Theme.Faint, Theme.Accent, hover.Value);
                TextRenderer.DrawText(g, hint, Theme.UI(7.4f, hintAccent),
                    new Rectangle(Width - Theme.S(66), Theme.S(24), Theme.S(54), Theme.S(20)),
                    hintColor, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            }
        }
    }
}
