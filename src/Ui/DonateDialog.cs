// @author bdth 2074055628@qq.com
// File purpose Donate dialog, WeChat QR code, buy the author a milk tea
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
    internal sealed class DonateDialog : Form
    {
        private const int DlgW = 420;
        private const int PadX = 26;
        private const int TagTop = 17, TagHeight = 16, TagWidth = 74;
        private const int TitleTop = 43;
        private const int QrSize = 236, QrPad = 12;
        private const int BtnH = 34, BottomPad = 22;

        private Bitmap image;
        private string status;
        private float sweep;
        private int bodyTop, bodyH, qrTop, hintTop;

        // The bitmap is owned by the dialog; the old one is released when the image is swapped, and everything is released on close
        public DonateDialog(Bitmap qr, bool loading)
        {
            image = qr;
            status = qr != null ? null : Lang.T(loading ? "donate.loading" : "donate.failed");
            Text = Lang.T("donate.entry");
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
            bodyTop = Theme.S(TitleTop) + MeasureLine(TitleFont) + Theme.S(12);
            bodyH = MeasureBody(contentW);
            qrTop = bodyTop + bodyH + Theme.S(16);
            hintTop = qrTop + Theme.S(QrSize + QrPad * 2) + Theme.S(10);
            int h = hintTop + Theme.S(22) + Theme.S(18) + Theme.S(BtnH) + Theme.S(BottomPad);
            ClientSize = new Size(w, h);

            var close = new Label();
            close.Text = "✕";
            close.ForeColor = Theme.Faint; close.BackColor = Color.Transparent;
            close.Font = Theme.UI(10f, false);
            close.TextAlign = ContentAlignment.MiddleCenter;
            close.Cursor = Cursors.Hand;
            close.SetBounds(w - Theme.S(42), Theme.S(12), Theme.S(26), Theme.S(26));
            close.MouseEnter += delegate { close.ForeColor = Theme.Accent; };
            close.MouseLeave += delegate { close.ForeColor = Theme.Faint; };
            close.Click += delegate { Close(); };
            Controls.Add(close);

            var ok = new PillButton(Lang.T("v230.notice.ok"), BtnKind.Primary);
            ok.Size = new Size(Theme.S(108), Theme.S(BtnH));
            ok.Location = new Point(w - Theme.S(PadX) - ok.Width, h - Theme.S(BottomPad) - Theme.S(BtnH));
            ok.Click += delegate { Close(); };
            Controls.Add(ok);

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
            Disposed += delegate
            {
                timer.Stop(); timer.Dispose();
                try { if (image != null) image.Dispose(); } catch { }
            };
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Fx.EnterForm(this);
        }

        // Swap in the new image when it arrives and release the old one; if the fetch fails but an old image is still held change nothing, the user still sees a scannable code
        public void SetImage(Bitmap qr)
        {
            if (qr == null) { MarkFailed(); return; }
            Bitmap old = image;
            image = qr; status = null;
            try { if (old != null) old.Dispose(); } catch { }
            Invalidate();
        }

        public void MarkFailed()
        {
            if (image != null) return;
            status = Lang.T("donate.failed");
            Invalidate();
        }

        private int MeasureLine(Font font)
        {
            using (Graphics g = CreateGraphics())
                return TextRenderer.MeasureText(g, "字Ay", font).Height;
        }

        private int MeasureBody(int width)
        {
            using (Graphics g = CreateGraphics())
                return TextRenderer.MeasureText(g, Lang.T("donate.body"), BodyFont,
                    new Size(width, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding).Height;
        }

        private static Font TitleFont { get { return Theme.UI(12.5f, true); } }
        private static Font BodyFont { get { return Theme.UI(9.2f, false); } }

        private void PaintChrome(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color accent = Theme.Accent;
            int padX = Theme.S(PadX);

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

            int skew = Theme.S(9);
            string tagText = Lang.T("donate.entry");
            Font tagFont = Theme.UI(7.2f, true);
            int tagW = Math.Max(Theme.S(TagWidth),
                TextRenderer.MeasureText(g, tagText, tagFont, Size.Empty, TextFormatFlags.NoPadding).Width + skew * 3);
            var tag = new[]
            {
                new Point(padX + skew, Theme.S(TagTop)),
                new Point(padX + tagW + skew, Theme.S(TagTop)),
                new Point(padX + tagW - skew, Theme.S(TagTop + TagHeight)),
                new Point(padX - skew, Theme.S(TagTop + TagHeight))
            };
            using (var fill = new SolidBrush(Color.FromArgb(38, accent))) g.FillPolygon(fill, tag);
            using (var pen = new Pen(Color.FromArgb(120, accent), Math.Max(1f, Theme.S(1))))
                g.DrawPolygon(pen, tag);
            using (var text = new SolidBrush(accent))
            using (var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString(tagText, tagFont, text,
                    new RectangleF(padX, Theme.S(TagTop), tagW, Theme.S(TagHeight)), fmt);

            int contentW = Width - padX * 2;
            TextRenderer.DrawText(g, Lang.T("donate.title"), TitleFont,
                new Rectangle(padX, Theme.S(TitleTop), contentW, bodyTop - Theme.S(TitleTop)),
                Theme.Fg, TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, Lang.T("donate.body"), BodyFont,
                new Rectangle(padX, bodyTop, contentW, bodyH),
                Theme.Dim, TextFormatFlags.Left | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);

            // Pad the QR code with a white background so scanning is more reliable in the dark theme; scale the image by an integer factor so the modules do not blur
            int boxSide = Theme.S(QrSize + QrPad * 2);
            var box = new Rectangle((Width - boxSide) / 2, qrTop, boxSide, boxSide);
            using (GraphicsPath card = Theme.TechPath(box, Theme.S(10)))
            {
                using (var fill = new SolidBrush(Color.White)) g.FillPath(fill, card);
                using (var pen = new Pen(Col.Alpha(accent, 110), Math.Max(1f, Theme.S(1)))) g.DrawPath(pen, card);
            }
            var slot = new Rectangle(box.Left + Theme.S(QrPad), box.Top + Theme.S(QrPad), Theme.S(QrSize), Theme.S(QrSize));
            if (image != null)
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(image, slot);
            }
            else if (!string.IsNullOrEmpty(status))
                TextRenderer.DrawText(g, status, Theme.UI(9f, false), slot, Color.FromArgb(120, 120, 120),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);

            TextRenderer.DrawText(g, Lang.T("donate.hint"), Theme.UI(8.5f, false),
                new Rectangle(padX, hintTop, contentW, Theme.S(22)),
                Theme.Faint, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

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
