// @author bdth 2074055628@qq.com
// 文件用途 启动时的联系方式弹窗 提供反馈渠道并可选择不再提示

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
    internal sealed class ContactDialog : Form
    {
        private const int DlgW = 560, DlgH = 398;
        private const string SeenKey = "ContactPromptHidden";

        public const string QqGroup = "1051472054";
        public const string WeChat = "Ssssssstyle";

        private bool dontShow;

        public static bool ShouldShow()
        {
            return !Settings.Load(SeenKey, false);
        }

        public static void MarkHidden()
        {
            Settings.Save(SeenKey, true);
        }

        public static void ResetHidden()
        {
            Settings.Save(SeenKey, false);
        }

        public ContactDialog()
        {
            Text = Lang.T("contact.title");
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
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
            close.SetBounds(Theme.S(DlgW - 42), Theme.S(14), Theme.S(26), Theme.S(26));
            close.MouseEnter += delegate { close.ForeColor = Theme.Accent; };
            close.MouseLeave += delegate { close.ForeColor = Theme.Faint; };
            close.Click += delegate { Finish(); };
            Controls.Add(close);

            int y = 108;
            AddChannel(Theme.S(34), Theme.S(y), Lang.T("contact.wechat"), WeChat, Lang.T("contact.wechat.note"), false);
            y += 84;
            AddChannel(Theme.S(34), Theme.S(y), Lang.T("contact.qq"), QqGroup, Lang.T("contact.qq.note"), true);
            y += 84;

            var free = new RoundPanel();
            free.SetBounds(Theme.S(34), Theme.S(y), Theme.S(DlgW - 68), Theme.S(58));
            free.BackColor = Theme.Bg; free.Fill = Theme.Inset; free.Border = Theme.Stroke;
            free.Radius = Theme.S(12); free.AccentEdge = true;
            Controls.Add(free);

            var freeTitle = new Label();
            freeTitle.Text = Lang.T("contact.free");
            freeTitle.ForeColor = Theme.Accent; freeTitle.BackColor = Color.Transparent;
            freeTitle.Font = Theme.UI(9f, true);
            freeTitle.UseCompatibleTextRendering = false;
            freeTitle.SetBounds(Theme.S(18), Theme.S(10), Theme.S(DlgW - 110), Theme.S(20));
            free.Controls.Add(freeTitle);

            var freeNote = new Label();
            freeNote.Text = Lang.T("contact.free.n");
            freeNote.ForeColor = Theme.Dim; freeNote.BackColor = Color.Transparent;
            freeNote.Font = Theme.UI(7.9f, false);
            freeNote.UseCompatibleTextRendering = false;
            freeNote.SetBounds(Theme.S(18), Theme.S(31), Theme.S(DlgW - 110), Theme.S(18));
            free.Controls.Add(freeNote);

            var chk = new Label();
            chk.Text = "☐  " + Lang.T("contact.dontshow");
            chk.ForeColor = Theme.Faint; chk.BackColor = Color.Transparent;
            chk.Font = Theme.UI(8.4f, false);
            chk.UseCompatibleTextRendering = false;
            chk.Cursor = Cursors.Hand;
            chk.SetBounds(Theme.S(34), Theme.S(DlgH - 56), Theme.S(260), Theme.S(24));
            chk.Click += delegate
            {
                dontShow = !dontShow;
                chk.Text = (dontShow ? "☑  " : "☐  ") + Lang.T("contact.dontshow");
                chk.ForeColor = dontShow ? Theme.Accent : Theme.Faint;
            };
            Controls.Add(chk);

            var ok = new PillButton(Lang.T("contact.enter"), BtnKind.Primary);
            ok.SetBounds(Theme.S(DlgW - 174), Theme.S(DlgH - 62), Theme.S(140), Theme.S(36));
            ok.Click += delegate { Finish(); };
            Controls.Add(ok);
        }

        private void Finish()
        {
            if (dontShow) MarkHidden();
            DialogResult = DialogResult.OK;
            Close();
        }

        private void AddChannel(int x, int y, string label, string value, string note, bool copyable)
        {
            int w = Theme.S(DlgW - 68), h = Theme.S(68);
            var card = new RoundPanel();
            card.SetBounds(x, y, w, h);
            card.BackColor = Theme.Bg; card.Fill = Theme.Card; card.Border = Theme.Stroke;
            card.Radius = Theme.S(12); card.AccentEdge = true;
            Controls.Add(card);

            var lbl = new Label();
            lbl.Text = label;
            lbl.ForeColor = Theme.Faint; lbl.BackColor = Color.Transparent;
            lbl.Font = Theme.UI(7.6f, true);
            lbl.UseCompatibleTextRendering = false;
            lbl.SetBounds(Theme.S(18), Theme.S(12), Theme.S(200), Theme.S(16));
            card.Controls.Add(lbl);

            int valW = Theme.S(178);
            var val = new Label();
            val.Text = value;
            val.ForeColor = Theme.Fg; val.BackColor = Color.Transparent;
            val.Font = Theme.UI(13f, true);
            val.UseCompatibleTextRendering = false;
            val.AutoEllipsis = true;
            val.SetBounds(Theme.S(18), Theme.S(30), valW, Theme.S(26));
            card.Controls.Add(val);

            int hintLeft = Theme.S(18) + valW + Theme.S(10);
            int hintRight = copyable ? Theme.S(112) : Theme.S(18);
            var hint = new Label();
            hint.Text = note;
            hint.ForeColor = Theme.Dim; hint.BackColor = Color.Transparent;
            hint.Font = Theme.UI(7.8f, false);
            hint.UseCompatibleTextRendering = false;
            hint.TextAlign = ContentAlignment.MiddleRight;
            hint.SetBounds(hintLeft, Theme.S(38), Math.Max(Theme.S(60), w - hintLeft - hintRight), Theme.S(18));
            card.Controls.Add(hint);

            if (!copyable) return;
            var copy = new PillButton(Lang.T("contact.copy"));
            copy.SetBounds(w - Theme.S(104), Theme.S(17), Theme.S(86), Theme.S(34));
            copy.Click += delegate
            {
                try
                {
                    Clipboard.SetText(value);
                    copy.Text = Lang.T("contact.copied");
                }
                catch { copy.Text = Lang.T("contact.copyfail"); }
            };
            card.Controls.Add(copy);
        }

        private void PaintChrome(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
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

            using (var pen = new Pen(Theme.Accent, Theme.S(2)))
            {
                g.DrawLine(pen, 0, cut, cut, 0);
                g.DrawLine(pen, cut, 0, cut + Theme.S(120), 0);
                g.DrawLine(pen, 0, cut, 0, cut + Theme.S(70));
            }

            var band = new Rectangle(0, Theme.S(68), w, Theme.S(3));
            using (var grad = new LinearGradientBrush(band, Theme.Accent, Col.Alpha(Theme.Accent, 0), LinearGradientMode.Horizontal))
                g.FillRectangle(grad, band);

            using (var pen = new Pen(Col.Alpha(Theme.Accent, 26), Theme.S(1)))
                for (int i = 0; i < Theme.S(150); i += Theme.S(7))
                    g.DrawLine(pen, w - Theme.S(190) + i, Theme.S(14), w - Theme.S(150) + i, Theme.S(50));

            string title = Lang.T("contact.title");
            using (var brush = new SolidBrush(Theme.Fg))
                g.DrawString(title, Theme.UI(15f, true), brush, Theme.S(32), Theme.S(18));
            using (var brush = new SolidBrush(Theme.Accent))
                g.DrawString("// " + App.VersionTag, Theme.UI(8f, true), brush, Theme.S(35), Theme.S(46));
            using (var brush = new SolidBrush(Theme.Dim))
                g.DrawString(Lang.T("contact.sub"), Theme.UI(8.6f, false), brush, Theme.S(34), Theme.S(80));
        }

        private void DragMove(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            Native.ReleaseCapture();
            Native.SendMessage(Handle, Native.WM_NCLBUTTONDOWN, (IntPtr)Native.HT_CAPTION, IntPtr.Zero);
        }
    }
}
