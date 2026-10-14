// 作者：bdth 2074055628@qq.com
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading;
using System.Windows.Forms;

namespace PaviseApp
{
    internal sealed class IrqCheckDialog : Form
    {
        private const int DlgW = 540;
        private const int PadX = 26;
        private const int ScanTop = 78;
        private const int ScanH = 320;
        private const int BtnH = 34;
        private const int BottomPad = 22;

        private readonly ScanView scan;
        private readonly Label hint;
        private readonly PillButton cancel;
        private readonly Func<Func<bool>, Action<float>, GpuIrqResult> work;
        private volatile bool cancelRequested;
        private bool running;
        private int lastSeconds = -1;

        public GpuIrqResult Result { get; private set; }

        public IrqCheckDialog(Func<Func<bool>, Action<float>, GpuIrqResult> work)
        {
            this.work = work;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            BackColor = Theme.Bg; ForeColor = Theme.Fg; Font = Theme.UI(9.5f, false);
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            KeyPreview = true;

            int w = Theme.S(DlgW);
            int hintTop = ScanTop + ScanH + 12;
            int h = Theme.S(hintTop + 44 + BtnH + BottomPad + 14);
            ClientSize = new Size(w, h);

            scan = new ScanView();
            scan.Name = "irqCheckScan";
            scan.SetBounds(Theme.S(PadX), Theme.S(ScanTop), w - Theme.S(PadX) * 2, Theme.S(ScanH));
            Controls.Add(scan);

            hint = new Label();
            hint.Name = "irqCheckHint";
            hint.Text = Lang.F("irqcheck.dialog.hint", GpuIrqCheck.CheckSeconds);
            hint.ForeColor = Theme.Warning; hint.BackColor = Color.Transparent;
            hint.Font = Theme.UI(9.5f, true);
            hint.TextAlign = ContentAlignment.MiddleCenter;
            hint.UseCompatibleTextRendering = false;
            hint.SetBounds(Theme.S(PadX), Theme.S(hintTop), w - Theme.S(PadX) * 2, Theme.S(40));
            Controls.Add(hint);

            cancel = new PillButton(Lang.T("dlg.cancel"), BtnKind.Normal);
            cancel.Name = "irqCheckCancel";
            cancel.Bg = Theme.Card;
            cancel.Size = new Size(Theme.S(108), Theme.S(BtnH));
            cancel.Location = new Point(w - Theme.S(PadX) - cancel.Width, h - Theme.S(BottomPad) - cancel.Height);
            cancel.Click += delegate { RequestCancel(); };
            Controls.Add(cancel);

            KeyDown += delegate(object s, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) RequestCancel(); };
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Fx.EnterForm(this);
            scan.BeginScan(Lang.F("irqcheck.dialog.caption", GpuIrqCheck.CheckSeconds));
            running = true;
            var thread = new Thread(Run);
            thread.IsBackground = true;
            thread.Priority = ThreadPriority.BelowNormal;
            thread.Start();
        }

        private void Run()
        {
            GpuIrqResult result;
            try { result = work(delegate { return cancelRequested; }, OnProgress); }
            catch (Exception ex) { result = new GpuIrqResult { Outcome = GpuIrqOutcome.Failed, Detail = ex.GetType().Name }; }
            try { BeginInvoke((MethodInvoker)delegate { Finish(result); }); }
            catch { }
        }

        private void OnProgress(float value)
        {
            int left = (int)Math.Ceiling(GpuIrqCheck.CheckSeconds * (1f - value));
            if (left < 0) left = 0;
            try
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    if (IsDisposed) return;
                    string caption = cancelRequested ? Lang.T("irqcheck.dialog.stopping")
                        : value >= 1f ? Lang.T("irqcheck.dialog.analyzing")
                        : left != lastSeconds ? Lang.F("irqcheck.dialog.caption", left) : null;
                    lastSeconds = left;
                    scan.ReportProgress(value, caption);
                });
            }
            catch { }
        }

        private void Finish(GpuIrqResult result)
        {
            running = false;
            Result = result;
            scan.Stop();
            DialogResult = result != null && result.Outcome == GpuIrqOutcome.Cancelled ? DialogResult.Cancel : DialogResult.OK;
            Close();
        }

        private void RequestCancel()
        {
            if (!running || cancelRequested) return;
            cancelRequested = true;
            cancel.Enabled = false;
            scan.ReportProgress(0f, Lang.T("irqcheck.dialog.stopping"));
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (running)
            {
                RequestCancel();
                e.Cancel = true;
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color accent = Theme.Accent;
            using (var bg = new SolidBrush(Theme.Bg)) g.FillRectangle(bg, new Rectangle(0, 0, Width, Height));
            using (var bar = new SolidBrush(accent)) g.FillRectangle(bar, 0, 0, Width, Theme.S(4));

            int skew = Theme.S(9);
            int tagTop = Theme.S(17), tagH = Theme.S(16), tagW = Theme.S(92), x = Theme.S(PadX);
            var tagPts = new[]
            {
                new Point(x + skew, tagTop), new Point(x + tagW + skew, tagTop),
                new Point(x + tagW - skew, tagTop + tagH), new Point(x - skew, tagTop + tagH)
            };
            using (var tagFill = new SolidBrush(Color.FromArgb(38, accent))) g.FillPolygon(tagFill, tagPts);
            using (var tagPen = new Pen(Color.FromArgb(120, accent), Math.Max(1f, Theme.S(1)))) g.DrawPolygon(tagPen, tagPts);
            using (var tb = new SolidBrush(accent))
            using (var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString("IRQ // CHECK", Theme.Mono(6.4f), tb, new RectangleF(x, tagTop, tagW, tagH), fmt);
            using (var tb = new SolidBrush(Theme.Fg))
                g.DrawString(Lang.T("irqcheck.dialog.title"), Theme.UI(12.5f, true), tb, x - Theme.S(2), Theme.S(43));

            int lineY = ClientSize.Height - Theme.S(BottomPad) - Theme.S(BtnH) - Theme.S(14);
            using (var sep = new Pen(Theme.Stroke)) g.DrawLine(sep, x, lineY, Width - x, lineY);
            using (var edge = new Pen(Theme.StrokeHi)) g.DrawRectangle(edge, 0, 0, Width - 1, Height - 1);
        }
    }
}
