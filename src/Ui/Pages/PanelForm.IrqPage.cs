// @author bdth 2074055628@qq.com
// 文件用途 中断页 记录真实对局 挑一台设备 再指定核心
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
        private Label lblIrqState;
        private ModuleBanner irqBanner;
        private PillButton btnIrqApply, btnIrqRevert;
        private Toggle swIrqProbePage;
        private SettingCard cardIrqProbe;
        private TechListBox lstIrqDevices;
        private List<IrqDevice> irqDevices = new List<IrqDevice>();
        private List<IrqSessionRecord> irqSessions = new List<IrqSessionRecord>();
        private int irqUsedSessions;
        private int irqDisplaySessions;
        private string irqReadIssue = "";
        private string irqDevicesIssue = "";
        private int irqRefreshQueued;
        private string irqFlash = "";
        private Color irqFlashColor;

        private void BuildIrqPage()
        {
            int top = PageHeader(pageIrq, Lang.T("nav.irq"), Lang.T("irq.sub"), 2);

            irqBanner = new ModuleBanner();
            irqBanner.SetBounds(Theme.S(ContentX), Theme.S(top), Theme.S(ContentW), Theme.S(72));
            irqBanner.Code = "INTERRUPT ROUTING // 06";
            irqBanner.TitleText = Lang.T("nav.irq");
            irqBanner.Detail = Lang.T("irq.sub").Replace("\r\n", " ");
            irqBanner.State = "DEVICE MAP STANDBY";
            irqBanner.StateColor = Theme.Faint;
            irqBanner.Glyph = "chip";
            pageIrq.Controls.Add(irqBanner);
            top += 84;

            var scroll = new WorkspacePanel();
            scroll.SetBounds(Theme.S(ContentX), Theme.S(top),
                Theme.S(ContentW + 12), Theme.S(PageH - top - 8));
            const int InnerW = ContentW - 12;
            scroll.BackColor = Theme.Bg;
            scroll.AutoScroll = true;
            scroll.Name = "irqScroll";
            Native.Dark(scroll);
            pageIrq.Controls.Add(scroll);
            int y = 2;

            swIrqProbePage = MakeSwitch(IrqSessionProbe.EnabledSetting, OnIrqProbePageToggle);
            int irqProbeCardH;
            cardIrqProbe = MakeAutoCard(scroll, 0, y, InnerW, 66,
                Lang.T("irq.sec.1"), Lang.T("irq.probe.sub"), swIrqProbePage,
                out irqProbeCardH);
            y += irqProbeCardH + 10;

            var actionDeck = MakeConsolePanel(scroll, 0, y, InnerW, 96, true);
            btnIrqApply = new PillButton(Lang.T("irq.btn.apply"), BtnKind.Primary);
            btnIrqApply.SetBounds(Theme.S(14), Theme.S(15), Theme.S(250), Theme.S(34));
            btnIrqApply.Click += OnIrqApply;
            actionDeck.Controls.Add(btnIrqApply);
            btnIrqRevert = new PillButton(Lang.T("irq.btn.revert"), BtnKind.Normal);
            btnIrqRevert.SetBounds(Theme.S(272), Theme.S(15), Theme.S(250), Theme.S(34));
            btnIrqRevert.Click += OnIrqRevert;
            actionDeck.Controls.Add(btnIrqRevert);
            // 失败原因放独立的全宽行 不能缩在两个按钮右边而只剩“暂无数据”
            lblIrqState = CardLabel(actionDeck, "", 14, 54, InnerW - 28, 30, 8.2f, true, Theme.Dim);
            lblIrqState.TextAlign = ContentAlignment.MiddleLeft;
            lblIrqState.AutoEllipsis = true;
            y += 106;

            lstIrqDevices = new TechListBox();
            int availH = PageH - top - 8;
            // 详细描述已搬进选核弹窗 这里列表撑满原来留给描述区的高度 只给列表下方图例留位
            int listH = Math.Max(145, availH - y - 74);
            var deviceDeck = MakeConsolePanel(scroll, 0, y, InnerW, listH + 40, false);
            CardLabel(deviceDeck, Lang.T("irq.sec.2").ToUpperInvariant(), 16, 8, InnerW - 32, 20, 7f, true, Theme.Faint);
            lstIrqDevices.SetBounds(Theme.S(8), Theme.S(32), Theme.S(InnerW - 16), Theme.S(listH));
            Theme.StyleList(lstIrqDevices, false);
            lstIrqDevices.DrawItem += DrawIrqRow;
            lstIrqDevices.ItemHeight = Theme.S(26);
            lstIrqDevices.Font = Theme.UI(8.5f, false);
            lstIrqDevices.SelectedIndexChanged += delegate { UpdateIrqApplyEnabled(); };
            lstIrqDevices.DoubleClick += OnIrqApply;
            deviceDeck.Controls.Add(lstIrqDevices);
            y += listH + 46;
            CardLabel(scroll, Lang.T("irq.legend"), 4, y, InnerW - 8, 18, 8f, false, Theme.Faint);
            y += 26;

            RefreshIrqPage();
        }

        private void RefreshIrqPage()
        {
            if (lstIrqDevices == null) return;

            // 重排前按设备 ID 记住选中项 按旧索引恢复会在 Worth 置顶或实测变化后
            // 悄悄选中另一台设备 随后“选择核心”可能打开错误目标
            string keepId = null;
            int oldIndex = lstIrqDevices.SelectedIndex;
            if (oldIndex >= 0 && oldIndex < lstIrqDevices.Items.Count)
            {
                IrqDevice oldDevice = lstIrqDevices.Items[oldIndex] as IrqDevice;
                if (oldDevice != null) keepId = oldDevice.InstanceId;
            }

            irqReadIssue = "";
            irqDevicesIssue = "";
            irqUsedSessions = 0;
            irqDisplaySessions = 0;
            irqSessions = new List<IrqSessionRecord>();
            var verdicts = new List<IrqDriverVerdict>();
            try { irqSessions = IrqSessionLedger.Load(out irqReadIssue); }
            catch { irqReadIssue = Lang.T("irq.ledger.readfailed"); }
            if (irqReadIssue.Length == 0)
            {
                try
                {
                    verdicts = IrqVerdict.EvaluateForDisplay(irqSessions, IrqPageRefreshHz(),
                        out irqUsedSessions, out irqDisplaySessions);
                    // 完整短局立即展示原始实测 建议仍只计原有 >=60 秒的合格局
                    if (irqUsedSessions < IrqSessionLedger.MinSessionsForVerdict)
                        foreach (IrqDriverVerdict v in verdicts)
                            if (v != null) v.Worth = false;
                }
                catch
                {
                    irqUsedSessions = 0;
                    irqDisplaySessions = 0;
                    verdicts = new List<IrqDriverVerdict>();
                    irqReadIssue = Lang.T("irq.state.evaluatefailed");
                }
            }
            try
            {
                irqDevices = IrqDeviceInventory.Enumerate();
                IrqDeviceInventory.AttachVerdicts(irqDevices, verdicts);
                IrqDeviceInventory.MarkOwnership(irqDevices);
                IrqDeviceInventory.Sort(irqDevices);
            }
            catch
            {
                irqDevices = new List<IrqDevice>();
                irqDevicesIssue = Lang.T("irq.state.devicesfailed");
            }

            lstIrqDevices.BeginUpdate();
            lstIrqDevices.Items.Clear();
            int withIntr = 0, pending = 0, unverified = 0, mismatch = 0;
            foreach (IrqDevice d in irqDevices)
            {
                if (d.Dpc > 0) withIntr++;
                if (d.AwaitingReboot) pending++;
                else if (d.PlacementMismatch) mismatch++;
                else if (d.Unverified) unverified++;
                lstIrqDevices.Items.Add(d);
            }
            lstIrqDevices.EndUpdate();
            if (!string.IsNullOrEmpty(keepId))
                for (int i = 0; i < irqDevices.Count; i++)
                    if (string.Equals(irqDevices[i].InstanceId, keepId,
                        StringComparison.OrdinalIgnoreCase))
                    { lstIrqDevices.SelectedIndex = i; break; }

            bool admin = false;
            try { admin = Native.IsElevated(); } catch { }
            btnIrqRevert.Enabled = admin && IrqRelocate.HasResidue;
            if (swIrqProbePage != null)
            {
                swIrqProbePage.SetSilently(IrqSessionProbe.EnabledSetting);
                swIrqProbePage.Enabled = admin;
            }
            string captureText = gameMode.IrqObservationStatusText ?? "";
            bool captureWarning = gameMode.IrqObservationStatusWarning;
            if (cardIrqProbe != null)
            {
                string probeText = !admin ? Lang.T("irq.probe.needadmin")
                    : IrqSessionProbe.EnabledSetting ? Lang.F("irq.probe.actual",
                        captureText.Length == 0 ? Lang.T("irq.capture.waiting") : captureText)
                    : Lang.T("irq.probe.off");
                Color probeColor = !admin ? Theme.Danger
                    : IrqSessionProbe.EnabledSetting ? (captureWarning ? Theme.Danger : Theme.Green)
                    : Theme.Accent;
                cardIrqProbe.SetStatus(probeText, probeColor);
            }

            SetIrqState(admin, pending, unverified, mismatch, withIntr, captureText, captureWarning);
            UpdateIrqApplyEnabled();
        }

        private static int IrqPageRefreshHz()
        {
            try { return DisplayGuard.CurrentRefreshRate(); }
            catch { return 0; }
        }

        private void SetIrqState(bool admin, int pending, int unverified, int mismatch, int withIntr,
            string captureText, bool captureWarning)
        {
            if (irqFlash.Length > 0)
            {
                lblIrqState.Text = IrqPageStatus.CountedText(irqFlash,
                    irqSessions.Count, irqUsedSessions, irqReadIssue);
                lblIrqState.ForeColor = irqFlashColor;
                irqFlash = "";
                return;
            }
            string exclusion = "";
            if (irqSessions.Count > 0 && irqDisplaySessions == 0 && irqReadIssue.Length == 0)
            {
                IrqSessionRecord latest = irqSessions[irqSessions.Count - 1];
                try
                {
                    exclusion = IrqPageStatus.ExclusionText(latest == null
                        ? IrqSessionExclusion.NoDrivers : latest.DisplayExclusion(null, null));
                }
                catch { exclusion = Lang.T("irq.record.unavailable"); }
            }
            bool dataWarning;
            string dataText = IrqPageStatus.ResolveData(irqSessions.Count, irqDisplaySessions,
                irqUsedSessions, withIntr,
                captureText, captureWarning, irqReadIssue, irqDevicesIssue, exclusion, out dataWarning);
            string t;
            Color c = Theme.Dim;
            if (!admin) { t = Lang.T("irq.state.needadmin"); c = Theme.Danger; }
            else if (!IrqSessionProbe.EnabledSetting) { t = Lang.T("irq.state.probeoff"); c = Theme.Accent; }
            else if (dataWarning) { t = dataText; c = Theme.Danger; }
            else if (mismatch > 0) { t = Lang.F("irq.state.mismatch", mismatch); c = Theme.Danger; }
            else if (pending > 0) { t = Lang.F("irq.state.pending", pending); c = Theme.Accent; }
            else if (irqUsedSessions >= IrqSessionLedger.MinSessionsForVerdict && IrqWorthCount() > 0)
            { t = Lang.F("irq.state.suggest", IrqWorthCount()); c = Theme.Accent; }
            else if (unverified > 0) { t = Lang.F("irq.state.unverified", unverified); c = Theme.Dim; }
            else t = dataText;
            // 横幅状态舱是短状态位 只放当前状态本身 带局数的完整句在下方状态条
            string banner = t;
            t = IrqPageStatus.CountedText(t, irqSessions.Count, irqUsedSessions, irqReadIssue);
            lblIrqState.Text = t;
            lblIrqState.ForeColor = c;
            if (irqBanner != null)
            {
                irqBanner.State = banner;
                irqBanner.StateColor = c;
            }
        }

        private void Flash(string text, Color c) { irqFlash = text; irqFlashColor = c; }

        // 只统计已经映射到可点击设备的 Worth 驱动判定若无法映射 不能告诉用户有一条
        // 根本找不到入口的建议
        private int IrqWorthCount()
        {
            int n = 0;
            if (irqDevices != null)
                foreach (IrqDevice d in irqDevices)
                    if (d != null && d.ActionableWorth) n++;
            return n;
        }

        // 由 GameMode 的对局结束建议事件驱动 跨线程进来 刷新中断页让状态条/横幅亮起建议
        //   不自动改注册表 只把用户引到中断页走已有手动流程 页面未建好时安全跳过
        public void NotifyIrqSuggestions(int count)
        {
            if (count > 0) NotifyIrqObservationUpdated();
        }

        // 每局完成/失败都刷新 不再要求有挪核建议 与建议通知合并 隐藏时不枚举设备
        // 页面重新激活仍走 RefreshIrqPage 故首局零建议也不会永久显示旧的空状态
        public void NotifyIrqObservationUpdated()
        {
            try
            {
                if (!IsHandleCreated || IsDisposed) return;
                if (Interlocked.CompareExchange(ref irqRefreshQueued, 1, 0) != 0) return;
                BeginInvoke((MethodInvoker)delegate
                {
                    Interlocked.Exchange(ref irqRefreshQueued, 0);
                    if (IsDisposed || !UiActive || curPage != pageIrq) return;
                    RefreshIrqPage();
                });
            }
            catch { Interlocked.Exchange(ref irqRefreshQueued, 0); }
        }

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

            string tag = "";
            Color tagColor = Theme.Faint;
            if (d.ManagedElsewhere) tag = Lang.T("irq.tag.owned");
            else if (d.IsPinned)
            {
                if (d.Effective) { tag = Lang.T("irq.tag.live"); tagColor = Theme.Accent; }
                else if (d.PlacementMismatch) { tag = Lang.T("irq.tag.mismatch"); tagColor = Theme.Danger; }
                else if (d.Unverified) { tag = Lang.T("irq.tag.unverified"); tagColor = Theme.Faint; }
                else { tag = Lang.T("irq.tag.pending"); tagColor = Theme.Danger; }
            }
            Cell(g, tag, x, ty, Dpi.S(58), tagColor, false);
            x += Dpi.S(58);

            IrqGrade grade = IrqDeviceInventory.Grade(d);
            Color gc = GradeColor(grade);
            string us = d.Dpc > 0
                ? d.MaxUs.ToString("F0") + "us " + IrqDeviceInventory.GradeText(grade)
                : "-";
            Cell(g, us, x, ty, Dpi.S(116), d.Dpc > 0 ? gc : Theme.Faint, false);
            x += Dpi.S(116);

            Cell(g, d.SeenOnCpus != 0 ? IrqRelocate.MaskText(d.SeenOnCpus) : "-",
                x, ty, Dpi.S(120), Theme.Dim, true);
            x += Dpi.S(120);

            // 建议标签放在设备名前面 长 PnP 名称被省略号截断时仍然可见
            string name = (d.ActionableWorth ? Lang.T("irq.tag.matchsuggest") + "  " : "")
                + d.Name
                + (d.InputRisk ? "  " + Lang.T("irq.tag.input") : "");
            Cell(g, name, x, ty, e.Bounds.Right - x - Dpi.S(8),
                d.ActionableWorth ? Theme.Accent : Theme.Fg, false);
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

        // 选中变化只算挪核按钮的可用性 设备详细描述已搬进选核弹窗(IrqPinDialog)不再在页面显示
        private void UpdateIrqApplyEnabled()
        {
            if (btnIrqApply == null) return;
            IrqDevice d = SelectedIrqDevice();
            bool admin = false;
            try { admin = Native.IsElevated(); } catch { }
            btnIrqApply.Enabled = d != null && admin && !d.ManagedElsewhere;
        }

        private void OnIrqProbePageToggle(object sender, EventArgs e)
        {
            if (swIrqProbePage == null) return;
            bool on = swIrqProbePage.Checked;
            if (on && !PaviseDialog.Confirm(this, Lang.T("irq.probe.warn.title"),
                    Lang.T("irq.probe.warn"), DlgKind.Warn))
            {
                swIrqProbePage.SetSilently(false);
                return;
            }
            IrqMutationBoundary.Run(delegate
            {
                IrqSessionProbe.EnabledSetting = on;
                gameMode.RequestIrqObservationSettingChanged();
            });
            RefreshIrqPage();
        }

        private void OnIrqApply(object sender, EventArgs e)
        {
            IrqDevice d = SelectedIrqDevice();
            if (d == null || d.ManagedElsewhere) return;
            bool admin = false;
            try { admin = Native.IsElevated(); } catch { }
            // 双击列表绕过了按钮的置灰 未提权时这里不能一声不吭地退掉
            if (!admin) { Flash(Lang.T("irqmove.needadmin"), Theme.Danger); RefreshIrqPage(); return; }

            ulong pick = 0;
            bool raisePriority = false;
            IrqPinSession session = IrqPinSession.FromLatest(irqSessions, d,
                IrqAffinityEngine.BootStamp(), CpuTopology.TopologyStamp(), IrqSessionProbe.DriverVersionOf);
            using (var dlg = new IrqPinDialog(d, session))
                if (dlg.ShowDialog(this) == DialogResult.OK)
                { pick = dlg.Chosen; raisePriority = dlg.RaisePriority; }
            if (pick == 0) return;

            bool ok = false;
            IrqMutationBoundary.Run(delegate
            {
                try { ok = IrqRelocate.ApplyDevice(d.InstanceId, pick); } catch { }
                if (ok && raisePriority && !d.DevicePriorityHigh)
                    try { IrqPriorityTweak.Apply(d.InstanceId); } catch { }
            });
            Flash(ok ? Lang.F("irq.done.written", IrqRelocate.MaskText(pick)) : Lang.T("irq.done.fail"),
                ok ? Theme.Accent : Theme.Danger);
            RefreshIrqPage();
        }

        private void OnIrqRevert(object sender, EventArgs e)
        {
            bool ok = false;
            IrqMutationBoundary.Run(delegate
            {
                try { ok = IrqRelocate.Revert(); } catch { }
                try { if (!IrqPriorityTweak.RestoreAll()) ok = false; } catch { }
            });
            Flash(ok ? Lang.T("irq.done.revert") : Lang.T("irq.done.fail"),
                ok ? Theme.Accent : Theme.Danger);
            RefreshIrqPage();
        }

    }

    // 只做状态选择 无窗口 设备枚举或 ETW 可用假观测覆盖空白页的所有分支
    internal static class IrqPageStatus
    {
        internal static string ResolveData(int recorded, int displayable, int usable, int devicesWithInterrupts,
            string captureText, bool captureWarning, string readIssue, string devicesIssue,
            string exclusion, out bool warning)
        {
            warning = true;
            if (!string.IsNullOrEmpty(readIssue)) return readIssue;
            if (!string.IsNullOrEmpty(devicesIssue)) return devicesIssue;
            if (captureWarning && !string.IsNullOrEmpty(captureText)) return captureText;
            if (recorded > 0 && displayable == 0)
                return Lang.F("irq.state.unusable", recorded,
                    string.IsNullOrEmpty(exclusion) ? Lang.T("irq.record.unavailable") : exclusion);
            warning = false;
            if (recorded == 0)
                return string.IsNullOrEmpty(captureText) ? Lang.T("irq.capture.waiting") : captureText;
            if (devicesWithInterrupts == 0) return Lang.T("irq.state.nomapped");
            if (usable < IrqSessionLedger.MinSessionsForVerdict)
            {
                if (displayable > usable)
                    return Lang.F("irq.state.rawshort", IrqSessionRecord.MinUsableSeconds,
                        IrqSessionLedger.MinSessionsForVerdict);
                return Lang.F("irq.state.fewsessions", usable, IrqSessionLedger.MinSessionsForVerdict);
            }
            return Lang.F("irq.state.done", devicesWithInterrupts, usable);
        }

        internal static string CountedText(string detail, int recorded, int usable, string readIssue)
        {
            // 读失败时数量未知 不把空的失败返回值显示成“已记录 0 局”
            if (!string.IsNullOrEmpty(readIssue)) return detail;
            string counts = Lang.F("irq.state.counts", recorded, usable);
            return string.IsNullOrEmpty(detail) ? counts : counts + " · " + detail;
        }

        internal static string ExclusionText(IrqSessionExclusion issue)
        {
            switch (issue)
            {
                case IrqSessionExclusion.TooShort: return Lang.T("irq.record.short");
                case IrqSessionExclusion.MissingSystemMask: return Lang.T("irq.record.mask");
                case IrqSessionExclusion.LostEvents: return Lang.T("irq.record.lost");
                case IrqSessionExclusion.NoDrivers: return Lang.T("irq.record.nodrivers");
                case IrqSessionExclusion.DifferentBoot: return Lang.T("irq.record.boot");
                case IrqSessionExclusion.DifferentTopology: return Lang.T("irq.record.topology");
                case IrqSessionExclusion.NoDuration: return Lang.T("irq.record.noduration");
                default: return Lang.T("irq.record.unavailable");
            }
        }
    }
}
