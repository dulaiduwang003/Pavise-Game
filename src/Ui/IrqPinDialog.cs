// @author bdth 2074055628@qq.com
// 文件用途 选一台设备之后 在这里挑它的中断要落到哪几个核上
//
// 为什么把选核做成弹窗
//   之前目标核是页面上一整块 而且是全局一个 显卡和 USB 主控共用一组核 本来就没道理
//   挪到弹窗里之后 目标核变成每台设备自己的事 页面上那一整块可以整个删掉
//   用户的动线也顺了 挑一台 选核 确定 三下走完 不用在页面上来回找
//
// 默认值只是默认值
//   推荐掩码在不同架构上给的不是同一种东西 大小核给性能核 其余架构给后台核
//   哪种对没有实测证据 所以它只能预选 不能替用户决定 用户改了就按用户的来
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
        private bool clockWasSuspended;

        public ulong Chosen { get; private set; }

        public IrqPinDialog(IrqDevice d)
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

            // 这台设备是谁 现在什么样 用户得看着这些数字做决定
            Controls.Add(Line(d == null ? "" : d.Name, y, 22, Theme.UI(10f, true), Theme.Fg, 24));
            y += 26;
            string stat = d == null ? "" : d.Dpc > 0
                ? Lang.F("irqpin.stat", d.MaxUs.ToString("F0"),
                    IrqDeviceInventory.GradeText(IrqDeviceInventory.Grade(d)),
                    IrqRelocate.MaskText(d.SeenOnCpus))
                : Lang.T("irq.d.nointr");
            Controls.Add(Line(stat, y, 22, Theme.UI(8.8f, false), Theme.Dim, 22));
            y += 26;

            // 分档就是拿来帮着做决定的 那就得在做决定的地方说
            //   之前它只在列表和明细里显示 弹窗里一声不吭
            //   结果是一台 80us 分档正常的设备 用户照样钉 而这多半看不出区别
            //   不拦着 拦着等于替他决定 但必须在按确定之前把话说明白
            string worth = null;
            if (d != null && d.Dpc > 0)
            {
                IrqGrade gr = IrqDeviceInventory.Grade(d);
                if (gr == IrqGrade.Fine) worth = Lang.T("irqpin.notworth");
                else if (gr == IrqGrade.Long) worth = Lang.T("irqpin.maybe");
            }
            if (worth != null)
            {
                Controls.Add(Line(worth, y, 22, Theme.UI(8.3f, false), Theme.Faint, 32));
                y += 36;
            }

            // 键鼠挂在 USB 主控下面时 得在按确定之前说 按完就来不及了
            if (d != null && d.InputRisk)
            {
                Controls.Add(Line(Lang.T("irq.d.input"), y, 22, Theme.UI(8.3f, false), Theme.Danger, 46));
                y += 50;
            }

            Controls.Add(Line(Lang.T("irqpin.howto"), y, 22, Theme.UI(8.3f, false), Theme.Faint, 46));
            y += 50;

            matrix = new CoreMatrix();
            matrix.PrimaryTag = Lang.T("irq.tag.target");
            // 独占 是挑游戏核那边的概念 中断只落在一个逻辑处理器上 这里没这回事
            matrix.MarkExclusive = false;
            int mtxW = Theme.S(DlgW - 44);
            int mtxH = matrix.LayoutFor(mtxW);
            matrix.SetBounds(Theme.S(22), Theme.S(y), mtxW, mtxH);
            // 已经钉过就显示它现在写着的那组核 没钉过就预选推荐值
            ulong start = d != null && d.IsPinned ? d.Mask : IrqRelocate.AutoMask();
            matrix.Selected = start;
            Chosen = start;
            matrix.SelectionChanged = OnPicked;
            Controls.Add(matrix);
            y += Dpi.U(mtxH) + 10;

            lblPick = Line("", y, 22, Theme.UI(8.8f, false), Theme.Dim, 22);
            Controls.Add(lblPick);
            y += 30;

            var btnAuto = new PillButton(Lang.T("irqpin.auto"), BtnKind.Normal);
            btnAuto.Bg = Theme.Bg;
            btnAuto.SetBounds(Theme.S(22), Theme.S(y), Theme.S(150), Theme.S(34));
            btnAuto.Click += delegate { matrix.Selected = IrqRelocate.AutoMask(); OnPicked(matrix.Selected); };
            Controls.Add(btnAuto);

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

        // 一个核都不选和全选都是非法的 引擎见到全核掩码会跳过不写掩码
        // 与其让用户按下确定之后什么都没发生 不如当场把确定按钮关掉并说清为什么
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
