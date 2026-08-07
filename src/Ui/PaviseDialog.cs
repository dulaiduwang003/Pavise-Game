// @author bdth 2074055628@qq.com
// 文件用途 统一的自绘弹窗 机能面板风格 取代系统 MessageBox
// 全项目只经此组件出弹窗 不再直接调用 MessageBox.Show

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
    internal enum DlgKind { Info, Warn, Danger, Success }

    internal sealed class PaviseDialog : Form
    {
        private const int DlgW = 468;
        private const int PadX = 26;
        private const int TitleTop = 30;
        private const int BodyTop = 62;
        private const int BtnH = 34;
        private const int BtnGap = 10;
        private const int BottomPad = 22;

        private readonly string title;
        private readonly string body;
        private readonly DlgKind kind;
        private readonly bool cancellable;
        private float sweep;
        private Rectangle bodyRect;

        private PaviseDialog(string dlgTitle, string dlgBody, DlgKind dlgKind,
            string okText, string cancelText)
        {
            title = dlgTitle ?? "";
            body = dlgBody ?? "";
            kind = dlgKind;
            cancellable = cancelText != null;

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            BackColor = Theme.Bg; ForeColor = Theme.Fg; Font = Theme.UI(9.5f, false);
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            KeyPreview = true;

            int w = Theme.S(DlgW);
            int textW = w - Theme.S(PadX) * 2;
            int textH = MeasureBody(textW);
            int h = Theme.S(BodyTop) + textH + Theme.S(BtnH) + Theme.S(BottomPad) + Theme.S(16);
            ClientSize = new Size(w, h);
            bodyRect = new Rectangle(Theme.S(PadX), Theme.S(BodyTop), textW, textH);

            var ok = new PillButton(okText ?? Lang.T("dlg.ok"),
                kind == DlgKind.Danger ? BtnKind.Danger : BtnKind.Primary);
            ok.Size = new Size(Theme.S(108), Theme.S(BtnH));
            ok.Click += delegate { DialogResult = DialogResult.OK; Close(); };
            Controls.Add(ok);

            PillButton cancel = null;
            if (cancellable)
            {
                cancel = new PillButton(cancelText, BtnKind.Normal);
                cancel.Bg = Theme.Card;
                cancel.Size = new Size(Theme.S(96), Theme.S(BtnH));
                cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
                Controls.Add(cancel);
            }

            int by = h - Theme.S(BottomPad) - Theme.S(BtnH);
            int rx = w - Theme.S(PadX) - ok.Width;
            ok.Location = new Point(rx, by);
            if (cancel != null)
                cancel.Location = new Point(rx - Theme.S(BtnGap) - cancel.Width, by);

            AcceptButton = null;
            KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape)
                {
                    DialogResult = cancellable ? DialogResult.Cancel : DialogResult.OK;
                    Close();
                }
                else if (e.KeyCode == Keys.Enter)
                {
                    DialogResult = DialogResult.OK;
                    Close();
                }
            };

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

        private int MeasureBody(int width)
        {
            using (var g = CreateGraphics())
            using (Font f = Theme.UI(9.5f, false))
            {
                SizeF sz = g.MeasureString(body, f, width);
                return Math.Max(Theme.S(28), (int)Math.Ceiling(sz.Height) + Theme.S(10));
            }
        }

        private Color KindColor
        {
            get
            {
                switch (kind)
                {
                    case DlgKind.Danger: return Theme.Danger;
                    case DlgKind.Warn: return Color.FromArgb(255, 176, 32);
                    case DlgKind.Success: return Theme.Green;
                    default: return Theme.Accent;
                }
            }
        }

        private string KindTag
        {
            get
            {
                switch (kind)
                {
                    case DlgKind.Danger: return Lang.T("dlg.tag.danger");
                    case DlgKind.Warn: return Lang.T("dlg.tag.warn");
                    case DlgKind.Success: return Lang.T("dlg.tag.done");
                    default: return Lang.T("dlg.tag.info");
                }
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color accent = KindColor;
            var full = new Rectangle(0, 0, Width, Height);

            using (var bg = new SolidBrush(Theme.Bg)) g.FillRectangle(bg, full);

            int barH = Theme.S(4);
            using (var bar = new SolidBrush(accent))
                g.FillRectangle(bar, 0, 0, Width, barH);
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

            int skew = Theme.S(9);
            var tagPts = new[]
            {
                new Point(Theme.S(PadX) + skew, Theme.S(14)),
                new Point(Theme.S(PadX) + Theme.S(74) + skew, Theme.S(14)),
                new Point(Theme.S(PadX) + Theme.S(74) - skew, Theme.S(14) + Theme.S(15)),
                new Point(Theme.S(PadX) - skew, Theme.S(14) + Theme.S(15))
            };
            using (var tagFill = new SolidBrush(Color.FromArgb(38, accent)))
                g.FillPolygon(tagFill, tagPts);
            using (var tagPen = new Pen(Color.FromArgb(120, accent), Math.Max(1f, Theme.S(1))))
                g.DrawPolygon(tagPen, tagPts);
            using (var tf = Theme.UI(7.2f, true))
            using (var tb = new SolidBrush(accent))
            using (var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString(KindTag, tf, tb,
                    new RectangleF(Theme.S(PadX), Theme.S(14), Theme.S(74), Theme.S(15)), fmt);

            using (var titleFont = Theme.UI(12.5f, true))
            using (var tb = new SolidBrush(Theme.Fg))
                g.DrawString(title, titleFont, tb, Theme.S(PadX) - Theme.S(2), Theme.S(TitleTop));

            using (var bodyFont = Theme.UI(9.5f, false))
            using (var bb = new SolidBrush(Theme.Dim))
                g.DrawString(body, bodyFont, bb, bodyRect);

            int lineY = ClientSize.Height - Theme.S(BottomPad) - Theme.S(BtnH) - Theme.S(14);
            using (var sep = new Pen(Theme.Stroke))
                g.DrawLine(sep, Theme.S(PadX), lineY, Width - Theme.S(PadX), lineY);

            using (var edge = new Pen(Theme.StrokeHi))
                g.DrawRectangle(edge, 0, 0, Width - 1, Height - 1);
        }

        private static DialogResult Run(IWin32Window owner, string title, string body,
            DlgKind kind, string okText, string cancelText)
        {
            using (var dlg = new PaviseDialog(title, body, kind, okText, cancelText))
            {
                var form = owner as Form;
                if (form == null || !form.Visible) dlg.StartPosition = FormStartPosition.CenterScreen;
                return owner != null ? dlg.ShowDialog(owner) : dlg.ShowDialog();
            }
        }

        public static void Info(IWin32Window owner, string title, string body)
        {
            Run(owner, title, body, DlgKind.Info, null, null);
        }

        public static void Warn(IWin32Window owner, string title, string body)
        {
            Run(owner, title, body, DlgKind.Warn, null, null);
        }

        public static void Error(IWin32Window owner, string title, string body)
        {
            Run(owner, title, body, DlgKind.Danger, null, null);
        }

        public static void Success(IWin32Window owner, string title, string body)
        {
            Run(owner, title, body, DlgKind.Success, null, null);
        }

        public static bool Confirm(IWin32Window owner, string title, string body, DlgKind kind)
        {
            return Run(owner, title, body, kind, Lang.T("dlg.confirm"), Lang.T("dlg.cancel"))
                == DialogResult.OK;
        }
    }
}
