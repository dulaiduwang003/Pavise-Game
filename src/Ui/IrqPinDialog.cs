// @author bdth 2074055628@qq.com
// 文件用途 选一台设备之后 在这里挑它的中断要落到哪几个核上
using System;
using System.Drawing;
using System.Windows.Forms;

namespace PaviseApp
{
    internal sealed class IrqPinDialog : Form
    {
        private const int DlgW = 620;

        private readonly CoreMatrix matrix;
        private readonly Label lblPick;
        private readonly PillButton btnOk;
        private readonly Toggle swPriority;
        private bool clockWasSuspended;

        public ulong Chosen { get; private set; }
        public bool RaisePriority { get { return swPriority != null && swPriority.Checked; } }

        public IrqPinDialog(IrqDevice d) : this(d, null) { }

        public IrqPinDialog(IrqDevice d, IrqCheckupDevice ck)
        {
            Text = Lang.T("irqpin.title");
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            BackColor = Theme.Bg; ForeColor = Theme.Fg; Font = Theme.UI(9.5f, false);
            DoubleBuffered = true;

            int y = 18;
            var title = new Label();
            title.Text = Lang.T("irqpin.title");
            title.ForeColor = Theme.Fg; title.BackColor = Theme.Bg; title.Font = Theme.UI(14f, true);
            title.UseCompatibleTextRendering = false;
            title.SetBounds(Theme.S(22), Theme.S(y), Theme.S(DlgW - 100), Theme.S(30));
            title.MouseDown += DragMove;
            Controls.Add(title);

            var close = new Label();
            close.Text = "✕";
            close.ForeColor = Theme.Dim; close.BackColor = Theme.Bg;
            close.TextAlign = ContentAlignment.MiddleCenter;
            close.Cursor = Cursors.Hand;
            close.SetBounds(Theme.S(DlgW - 46), Theme.S(16), Theme.S(26), Theme.S(26));
            close.Click += delegate { Close(); };
            Controls.Add(close);
            y += 34;

            Controls.Add(Line(d == null ? "" : d.Name, y, 22, Theme.UI(10f, true), Theme.Fg, 24));
            y += 26;
            string stat = d == null ? "" : d.Dpc > 0
                ? Lang.F("irqpin.stat", d.MaxUs.ToString("F0"),
                    IrqDeviceInventory.GradeText(IrqDeviceInventory.Grade(d)),
                    IrqRelocate.MaskText(d.SeenOnCpus))
                : Lang.T("irq.d.nointr");
            Controls.Add(Line(stat, y, 22, Theme.UI(8.8f, false), Theme.Dim, 22));
            y += 26;

            string worth = null;
            if (d != null && d.Dpc > 0)
            {
                IrqGrade gr = IrqDeviceInventory.Grade(d);
                if (gr == IrqGrade.Fine) worth = Lang.T("irqpin.notworth");
                else if (gr == IrqGrade.Long) worth = Lang.T("irqpin.maybe");
            }
            if (worth != null)
            {
                Controls.Add(Line(worth, y, 22, Theme.UI(8.3f, false), Theme.Faint, 22));
                y += 28;
            }

            if (ck != null && ck.SuggestMask != 0)
            {
                Controls.Add(Line(Lang.F("irqpin.suggest", IrqRelocate.MaskText(ck.SuggestMask)),
                    y, 22, Theme.UI(10f, true), Theme.Accent, 26));
                y += 28;
                if (ck.SuggestSinglePhysical)
                {
                    Controls.Add(Line(Lang.T("irqpin.suggest.smt"), y, 22,
                        Theme.UI(8.3f, false), Theme.Danger, 22));
                    y += 26;
                }
                if (ck.SuggestSharesBackground)
                {
                    Controls.Add(Line(Lang.T("irqpin.suggest.bg"), y, 22,
                        Theme.UI(8.3f, false), Theme.Danger, 22));
                    y += 26;
                }
            }
            else if (ck != null && ck.NoSuggestReason.Length > 0)
            {
                Controls.Add(Line(ck.NoSuggestReason, y, 22,
                    Theme.UI(8.3f, false), Theme.Danger, 22));
                y += 26;
            }

            if (d != null && d.InputRisk)
            {
                Controls.Add(Line(Lang.T("irq.d.input"), y, 22, Theme.UI(8.3f, false), Theme.Danger, 22));
                y += 26;
            }

            Controls.Add(Line(Lang.T("irqpin.howto"), y, 22, Theme.UI(8.3f, false), Theme.Faint, 22));
            y += 30;

            matrix = new CoreMatrix();
            matrix.PrimaryTag = Lang.T("irq.tag.target");
            matrix.MarkExclusive = false;
            int mtxW = Theme.S(DlgW - 44);
            int mtxH = matrix.LayoutFor(mtxW);
            matrix.SetBounds(Theme.S(22), Theme.S(y), mtxW, mtxH);
            ulong start = d != null && d.IsPinned ? d.Mask : 0UL;
            matrix.Selected = start;
            Chosen = start;
            matrix.SelectionChanged = OnPicked;
            Controls.Add(matrix);
            y += Dpi.U(mtxH) + 10;

            lblPick = Line("", y, 22, Theme.UI(8.8f, false), Theme.Dim, 22);
            Controls.Add(lblPick);
            y += 30;

            swPriority = new Toggle();
            swPriority.Checked = d != null
                && (d.DevicePriorityHigh || IrqPriorityTweak.AppliedTo(d.InstanceId));
            swPriority.Enabled = d != null && !d.DevicePriorityHigh;
            swPriority.Location = new Point(Theme.S(22), Theme.S(y));
            Controls.Add(swPriority);
            Controls.Add(Line(d != null && d.DevicePriorityHigh
                    ? Lang.T("irqpin.prio.already") : Lang.T("irqpin.prio"),
                y + 4, 76, Theme.UI(8.5f, false), Theme.Dim, 30));
            y += 38;


            var btnCancel = new PillButton(Lang.T("irqpin.cancel"), BtnKind.Normal);
            btnCancel.Bg = Theme.Bg;
            btnCancel.SetBounds(Theme.S(DlgW - 302), Theme.S(y), Theme.S(130), Theme.S(34));
            btnCancel.Click += delegate { Close(); };
            Controls.Add(btnCancel);

            btnOk = new PillButton(Lang.T("irqpin.ok"), BtnKind.Primary);
            btnOk.Bg = Theme.Bg;
            btnOk.SetBounds(Theme.S(DlgW - 162), Theme.S(y), Theme.S(140), Theme.S(34));
            btnOk.Click += delegate { DialogResult = DialogResult.OK; Close(); };
            Controls.Add(btnOk);
            y += 34;

            ClientSize = new Size(Theme.S(DlgW), Theme.S(y + 18));
            MouseDown += DragMove;
            OnPicked(Chosen);
        }

        private static Label Line(string text, int y, int x, Font f, Color c, int h)
        {
            var l = new Label();
            l.Text = text; l.Font = f; l.ForeColor = c; l.BackColor = Theme.Bg;
            l.UseCompatibleTextRendering = false;
            l.SetBounds(Theme.S(x), Theme.S(y), Theme.S(DlgW - x * 2), Theme.S(h));
            return l;
        }

        private void OnPicked(ulong mask)
        {
            ulong keep = IrqRelocate.Sanitize(mask);
            Chosen = keep;
            btnOk.Enabled = keep != 0;
            lblPick.Text = keep != 0
                ? Lang.F("irqpin.picked", IrqRelocate.MaskText(keep))
                : Lang.T("irqpin.badpick");
            lblPick.ForeColor = keep != 0 ? Theme.Accent : Theme.Danger;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.RoundCorners(Handle);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            clockWasSuspended = UiClock.Borrow();
            Fx.EnterForm(this);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            UiClock.Return(clockWasSuspended);
            base.OnFormClosed(e);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { Close(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void DragMove(object s, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                Native.ReleaseCapture();
                Native.SendMessage(Handle, Native.WM_NCLBUTTONDOWN, (IntPtr)Native.HT_CAPTION, IntPtr.Zero);
            }
        }
    }
}
