// @author bdth 2074055628@qq.com
// 文件用途 中断页 扫一次 挑一台 钉住 就这三步
//
// 上一版做成了 5 个按钮 5 个状态标签 4 个分区 7 列表格 自己都看不懂
//   问题不在信息不够 而在没有一个地方告诉用户下一步该干什么
//   四个标签各说各的 用户不知道该看哪个
// 这一版的规矩
//   只有一条状态行 它永远只回答一件事 现在该干什么
//   选中设备的全部细节集中在明细里 别的地方不重复
//   挑核放进弹窗 页面上没有目标核那一块了
//   而且目标核变成每台设备自己的 显卡和 USB 主控没道理共用一组核
//
// 按设备列不按驱动列
//   驱动名到设备那条路只对即插即用设备驱动成立 框架层没有自己的设备
//   实测一台机器上超过一半的 DPC 时长落在它们头上 反过来枚举设备才够得着
//
// 已写入和已生效必须分开
//   设备是启动分配中断资源时读亲和策略的 写完注册表落点不会立刻变
//   实测写入成功 回读校验也通过 DPC 仍然落在原来的核上 只有重启才换
//   没观测到中断的设备一律不说生效 没有证据不能当成功
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace PaviseApp
{
    internal partial class PanelForm : Form
    {
        private DBPanel pageIrq;
        private Label lblIrqState, lblIrqDetail;
        private PillButton btnIrqScan, btnIrqApply, btnIrqRevert;
        private TechListBox lstIrqDevices;
        private List<IrqDevice> irqDevices = new List<IrqDevice>();
        private IrqScanResult irqScan;
        private bool irqScanning;
        // 刚做完的动作要盖住常规提示一次 不然用户按完看不到结果
        private string irqFlash = "";
        private Color irqFlashColor;

        private void BuildIrqPage()
        {
            int top = PageHeader(pageIrq, Lang.T("nav.irq"), Lang.T("irq.sub"), 2);

            var scroll = new DBPanel();
            scroll.SetBounds(Theme.S(ContentX), Theme.S(top),
                Theme.S(ContentW + 12), Theme.S(PageH - top - 8));
            const int InnerW = ContentW - 12;
            scroll.BackColor = Theme.Bg;
            scroll.AutoScroll = true;
            // 自检靠这个名字找到这一页 排版重叠一次就够坑一轮 得有守卫
            scroll.Name = "irqScroll";
            Native.Dark(scroll);
            pageIrq.Controls.Add(scroll);
            int y = 2;

            Section(scroll, Lang.T("irq.sec.1"), 0, y);
            y += 22;
            btnIrqScan = new PillButton(Lang.T("irq.btn.scan"), BtnKind.Primary);
            btnIrqScan.SetBounds(Theme.S(0), Theme.S(y), Theme.S(190), Theme.S(32));
            btnIrqScan.Click += OnIrqScan;
            scroll.Controls.Add(btnIrqScan);
            btnIrqApply = new PillButton(Lang.T("irq.btn.apply"), BtnKind.Primary);
            btnIrqApply.SetBounds(Theme.S(198), Theme.S(y), Theme.S(210), Theme.S(32));
            btnIrqApply.Click += OnIrqApply;
            scroll.Controls.Add(btnIrqApply);
            btnIrqRevert = new PillButton(Lang.T("irq.btn.revert"), BtnKind.Normal);
            btnIrqRevert.SetBounds(Theme.S(416), Theme.S(y), Theme.S(160), Theme.S(32));
            btnIrqRevert.Click += OnIrqRevert;
            scroll.Controls.Add(btnIrqRevert);
            // 唯一的状态行 永远只回答 现在该干什么
            lblIrqState = CardLabel(scroll, "", 2, y + 38, InnerW - 4, 30, 8.5f, false, Theme.Dim);
            y += 74;

            Section(scroll, Lang.T("irq.sec.2"), 0, y);
            y += 22;
            lstIrqDevices = new TechListBox();
            int availH = PageH - top - 8;
            int listH = Math.Max(160, availH - y - (26 + 22 + 76 + 10));
            lstIrqDevices.SetBounds(Theme.S(0), Theme.S(y), Theme.S(InnerW), Theme.S(listH));
            // 自绘 因为要按像素定位列
            //   空格填充只在等宽字体下对得齐 而设备名是中文与厂商名混排 不能用等宽
            //   于是数字列用等宽 名称列用 UI 字体 各自画在固定的 x 上
            Theme.StyleList(lstIrqDevices, false);
            lstIrqDevices.DrawItem += DrawIrqRow;
            lstIrqDevices.ItemHeight = Theme.S(26);
            lstIrqDevices.Font = Theme.UI(8.5f, false);
            lstIrqDevices.SelectedIndexChanged += delegate { RefreshIrqDetail(); };
            lstIrqDevices.DoubleClick += OnIrqApply;
            scroll.Controls.Add(lstIrqDevices);
            y += listH + 4;
            CardLabel(scroll, Lang.T("irq.legend"), 4, y, InnerW - 8, 18, 8f, false, Theme.Faint);
            y += 26;

            Section(scroll, Lang.T("irq.sec.3"), 0, y);
            y += 22;
            // 选中设备的全部细节都在这里 别的地方不重复说
            lblIrqDetail = CardLabel(scroll, "", 4, y, InnerW - 8, 76, 9f, false, Theme.Fg);
            y += 86;

            RefreshIrqPage();
        }

        // 只枚举 不扫描 枚举是读注册表 毫秒级 扫描要开内核会话占满 8 秒
        private void RefreshIrqPage()
        {
            if (lstIrqDevices == null) return;

            try
            {
                irqDevices = IrqDeviceInventory.Enumerate();
                IrqDeviceInventory.Attach(irqDevices, irqScan);
                IrqDeviceInventory.MarkOwnership(irqDevices);
                IrqDeviceInventory.Sort(irqDevices);
            }
            catch { irqDevices = new List<IrqDevice>(); }

            int keep = lstIrqDevices.SelectedIndex;
            lstIrqDevices.BeginUpdate();
            lstIrqDevices.Items.Clear();
            int withIntr = 0, pending = 0;
            foreach (IrqDevice d in irqDevices)
            {
                if (d.Dpc > 0) withIntr++;
                if (d.IsPinned && !d.Effective) pending++;
                lstIrqDevices.Items.Add(d);
            }
            lstIrqDevices.EndUpdate();
            if (keep >= 0 && keep < lstIrqDevices.Items.Count) lstIrqDevices.SelectedIndex = keep;

            bool admin = false;
            try { admin = Native.IsElevated(); } catch { }
            btnIrqScan.Enabled = admin && !irqScanning;
            btnIrqRevert.Enabled = admin && IrqRelocate.HasResidue;
            // 写进去了但还没验证过的 得一直提着 不然用户以为按完就完事了
            btnIrqScan.Text = irqScanning ? Lang.T("irq.btn.scanning")
                : pending > 0 ? Lang.T("irq.btn.verify") : Lang.T("irq.btn.scan");

            SetIrqState(admin, pending, withIntr);
            RefreshIrqDetail();
        }

        // 这一页只有这一条状态行 它永远只回答一件事 现在该干什么
        private void SetIrqState(bool admin, int pending, int withIntr)
        {
            if (irqFlash.Length > 0)
            {
                lblIrqState.Text = irqFlash;
                lblIrqState.ForeColor = irqFlashColor;
                irqFlash = "";
                return;
            }
            string t;
            Color c = Theme.Dim;
            if (!admin) { t = Lang.T("irq.state.needadmin"); c = Theme.Danger; }
            else if (irqScanning) t = Lang.T("irq.state.scanning");
            else if (pending > 0) { t = Lang.F("irq.state.pending", pending); c = Theme.Accent; }
            else if (irqScan != null && !irqScan.Ok) { t = Lang.F("irq.state.fail", irqScan.Error); c = Theme.Danger; }
            else if (irqScan == null) t = Lang.F("irq.state.idle", irqDevices.Count);
            else if (withIntr == 0) t = Lang.T("irq.state.nointr");
            else t = Lang.F("irq.state.done", withIntr, irqScan.Seconds);
            lblIrqState.Text = t;
            lblIrqState.ForeColor = c;
        }

        private void Flash(string text, Color c) { irqFlash = text; irqFlashColor = c; }

        private void DrawIrqRow(object sender, DrawItemEventArgs e)
        {
            var lb = (ListBox)sender;
            if (e.Index < 0 || e.Index >= lb.Items.Count) return;
            var d = lb.Items[e.Index] as IrqDevice;
            Graphics g = e.Graphics;
            bool sel = (e.State & DrawItemState.Selected) != 0;
            int hover = lb.Tag is int ? (int)lb.Tag : -1;
            using (var bg = new SolidBrush(sel ? Theme.Sel : (e.Index == hover ? Theme.CardHover : Theme.Card)))
                g.FillRectangle(bg, e.Bounds);
            if (d == null) return;

            int x = e.Bounds.X + Dpi.S(8);
            int ty = e.Bounds.Y + (e.Bounds.Height - Dpi.S(15)) / 2;

            // 一 钉住的状态 只说钉没钉 别的标记不占这一格
            string tag = "";
            Color tagColor = Theme.Faint;
            if (d.ManagedElsewhere) tag = Lang.T("irq.tag.owned");
            else if (d.IsPinned)
            {
                tag = Lang.T(d.Effective ? "irq.tag.live" : "irq.tag.pending");
                tagColor = d.Effective ? Theme.Accent : Theme.Danger;
            }
            Cell(g, tag, x, ty, Dpi.S(58), tagColor, false);
            x += Dpi.S(58);

            // 二 中断有多长 数字和分档合成一格 看一眼就知道该不该管它
            IrqGrade grade = IrqDeviceInventory.Grade(d);
            Color gc = GradeColor(grade);
            string us = d.Dpc > 0
                ? d.MaxUs.ToString("F0") + "us " + IrqDeviceInventory.GradeText(grade) : "-";
            Cell(g, us, x, ty, Dpi.S(116), d.Dpc > 0 ? gc : Theme.Faint, false);
            x += Dpi.S(116);

            // 三 现在落在哪几个核 这是判断有没有生效的唯一证据
            Cell(g, d.SeenOnCpus != 0 ? IrqRelocate.MaskText(d.SeenOnCpus) : "-",
                x, ty, Dpi.S(120), Theme.Dim, true);
            x += Dpi.S(120);

            // 四 名称 键鼠挂在这台设备下面时跟在后面说一声
            string name = d.Name + (d.InputRisk ? "  " + Lang.T("irq.tag.input") : "");
            Cell(g, name, x, ty, e.Bounds.Right - x - Dpi.S(8), Theme.Fg, false);
        }

        private static Color GradeColor(IrqGrade g)
        {
            switch (g)
            {
                case IrqGrade.Fine: return Theme.Green;
                case IrqGrade.Long: return Theme.Dim;
                case IrqGrade.Heavy: return Theme.Fg;
                case IrqGrade.Severe: return Theme.Danger;
                default: return Theme.Dim;
            }
        }

        private static void Cell(Graphics g, string text, int x, int y, int w, Color c, bool mono)
        {
            if (string.IsNullOrEmpty(text) || w <= 0) return;
            TextRenderer.DrawText(g, text, mono ? Theme.MonoFor(text, 8f) : Theme.UI(8.5f, false),
                new Rectangle(x, y - Dpi.S(2), w, Dpi.S(20)), c,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis
                | TextFormatFlags.NoPrefix);
        }

        private IrqDevice SelectedIrqDevice()
        {
            int i = lstIrqDevices == null ? -1 : lstIrqDevices.SelectedIndex;
            if (i < 0 || i >= irqDevices.Count) return null;
            return irqDevices[i];
        }

        // 选中设备的全部细节集中在这里 状态行不重复说
        private void RefreshIrqDetail()
        {
            if (lblIrqDetail == null) return;
            IrqDevice d = SelectedIrqDevice();
            if (d == null)
            {
                lblIrqDetail.Text = Lang.T("irq.pick");
                lblIrqDetail.ForeColor = Theme.Faint;
                btnIrqApply.Enabled = false;
                return;
            }

            var sb = new System.Text.StringBuilder();
            sb.Append(d.Name);
            if (d.Dpc > 0)
            {
                sb.Append(Environment.NewLine);
                sb.Append(Lang.F("irq.d.stat", d.MaxUs.ToString("F0"), d.Dpc,
                    IrqRelocate.MaskText(d.SeenOnCpus)));
                sb.Append("  ").Append(IrqDeviceInventory.BudgetText(d.MaxUs));
                if (d.Over1Ms > 0) sb.Append("  ").Append(Lang.F("irq.d.over1ms", d.Over1Ms));
                else if (d.Over500Us > 0) sb.Append("  ").Append(Lang.F("irq.d.over500", d.Over500Us));
                if (d.SharedStats) sb.Append("  ").Append(Lang.T("irq.d.shared"));
            }
            else { sb.Append(Environment.NewLine); sb.Append(Lang.T("irq.d.nointr")); }

            Color c = Theme.Fg;
            // 键鼠挂在 USB 主控下面 钉主控就是把这条总线上所有输入设备一起挪走
            // 挪到跑得慢的核上 DPC 会变长 手感是会变的 必须在按之前说
            if (d.InputRisk) { sb.Append(Environment.NewLine); sb.Append(Lang.T("irq.d.input")); }
            if (d.ManagedElsewhere)
            { sb.Append(Environment.NewLine); sb.Append(Lang.T("irq.d.owned")); c = Theme.Faint; }
            else if (d.IsPinned)
            {
                sb.Append(Environment.NewLine);
                sb.Append(Lang.F(d.Effective ? "irq.d.effective" : "irq.d.written",
                    IrqRelocate.MaskText(d.Mask)));
                c = d.Effective ? Theme.Accent : Theme.Danger;
            }
            lblIrqDetail.Text = sb.ToString();
            lblIrqDetail.ForeColor = c;

            bool admin = false;
            try { admin = Native.IsElevated(); } catch { }
            btnIrqApply.Enabled = admin && !d.ManagedElsewhere;
        }

        private void OnIrqScan(object sender, EventArgs e)
        {
            if (irqScanning) return;
            irqScanning = true;
            RefreshIrqPage();
            const int secs = 8;
            var t = new Thread(delegate ()
            {
                IrqScanResult r = null;
                try { r = IrqRelocate.Scan(secs); }
                catch { }
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        irqScan = r;
                        irqScanning = false;
                        RefreshIrqPage();
                    });
                }
                catch { irqScanning = false; }
            });
            t.IsBackground = true;
            t.Start();
        }

        // 挑核放在弹窗里 页面上没有目标核这一块了
        //   每台设备钉到哪几个核是它自己的事 显卡和 USB 主控没道理共用一组
        private void OnIrqApply(object sender, EventArgs e)
        {
            IrqDevice d = SelectedIrqDevice();
            if (d == null || d.ManagedElsewhere) return;
            bool admin = false;
            try { admin = Native.IsElevated(); } catch { }
            if (!admin) return;

            ulong pick = 0;
            using (var dlg = new IrqPinDialog(d))
                if (dlg.ShowDialog(this) == DialogResult.OK) pick = dlg.Chosen;
            if (pick == 0) return;

            bool ok = false;
            try { ok = IrqRelocate.ApplyDevice(d.InstanceId, pick); } catch { }
            Flash(ok ? Lang.F("irq.done.written", IrqRelocate.MaskText(pick)) : Lang.T("irq.done.fail"),
                ok ? Theme.Accent : Theme.Danger);
            RefreshIrqPage();
        }

        private void OnIrqRevert(object sender, EventArgs e)
        {
            bool ok = false;
            try { ok = IrqRelocate.Revert(); } catch { }
            Flash(ok ? Lang.T("irq.done.revert") : Lang.T("irq.done.fail"),
                ok ? Theme.Accent : Theme.Danger);
            RefreshIrqPage();
        }

    }
}
