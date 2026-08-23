// @author bdth 2074055628@qq.com
// 文件用途 中断页 扫一次 挑一台 钉住 就这三步
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
        private ModuleBanner irqBanner;
        private PillButton btnIrqApply, btnIrqRevert, btnIrqCheckup;
        private IrqCheckupResult irqCheckup;
        private ScanView irqScan;
        private Toggle swIrqProbePage;
        private SettingCard cardIrqProbe;
        private TechListBox lstIrqDevices;
        private List<IrqDevice> irqDevices = new List<IrqDevice>();
        private List<IrqSessionRecord> irqSessions = new List<IrqSessionRecord>();
        private List<IrqDriverVerdict> irqVerdicts = new List<IrqDriverVerdict>();
        private int irqUsedSessions;
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
            cardIrqProbe = MakeCard(scroll, 0, y, InnerW, 66,
                Lang.T("irq.sec.1"), "", swIrqProbePage);
            y += 76;

            var actionDeck = MakeConsolePanel(scroll, 0, y, InnerW, 64, true);
            btnIrqApply = new PillButton(Lang.T("irq.btn.apply"), BtnKind.Primary);
            btnIrqApply.SetBounds(Theme.S(14), Theme.S(15), Theme.S(250), Theme.S(34));
            btnIrqApply.Click += OnIrqApply;
            actionDeck.Controls.Add(btnIrqApply);
            btnIrqRevert = new PillButton(Lang.T("irq.btn.revert"), BtnKind.Normal);
            btnIrqRevert.SetBounds(Theme.S(272), Theme.S(15), Theme.S(160), Theme.S(34));
            btnIrqRevert.Click += OnIrqRevert;
            actionDeck.Controls.Add(btnIrqRevert);
            btnIrqCheckup = new PillButton(Lang.T("irq.btn.checkup"), BtnKind.Normal);
            btnIrqCheckup.SetBounds(Theme.S(440), Theme.S(15), Theme.S(180), Theme.S(34));
            btnIrqCheckup.Click += OnIrqCheckup;
            actionDeck.Controls.Add(btnIrqCheckup);
            lblIrqState = CardLabel(actionDeck, "", 640, 15, InnerW - 658, 34, 8.2f, true, Theme.Dim);
            lblIrqState.TextAlign = ContentAlignment.MiddleRight;
            y += 74;

            lstIrqDevices = new TechListBox();
            int availH = PageH - top - 8;
            int listH = Math.Max(145, availH - y - 150);
            var deviceDeck = MakeConsolePanel(scroll, 0, y, InnerW, listH + 40, false);
            CardLabel(deviceDeck, Lang.T("irq.sec.2").ToUpperInvariant(), 16, 8, InnerW - 32, 20, 7f, true, Theme.Faint);
            lstIrqDevices.SetBounds(Theme.S(8), Theme.S(32), Theme.S(InnerW - 16), Theme.S(listH));
            Theme.StyleList(lstIrqDevices, false);
            lstIrqDevices.DrawItem += DrawIrqRow;
            lstIrqDevices.ItemHeight = Theme.S(26);
            lstIrqDevices.Font = Theme.UI(8.5f, false);
            lstIrqDevices.SelectedIndexChanged += delegate { RefreshIrqDetail(); };
            lstIrqDevices.DoubleClick += OnIrqApply;
            deviceDeck.Controls.Add(lstIrqDevices);
            y += listH + 46;
            CardLabel(scroll, Lang.T("irq.legend"), 4, y, InnerW - 8, 18, 8f, false, Theme.Faint);
            y += 26;

            var detailDeck = MakeConsolePanel(scroll, 0, y, InnerW, 86, false);
            CardLabel(detailDeck, Lang.T("irq.sec.3").ToUpperInvariant(), 16, 8, InnerW - 32, 20, 7f, true, Theme.Faint);
            lblIrqDetail = CardLabel(detailDeck, "", 16, 31, InnerW - 32, 44, 9f, false, Theme.Fg);
            y += 96;

            irqScan = new ScanView();
            irqScan.Name = "irqScanOverlay";
            irqScan.SetBounds(Theme.S(ContentX), Theme.S(top),
                Theme.S(ContentW), Theme.S(PageH - top - 8));
            irqScan.Visible = false;
            irqScan.TabStop = true;
            irqScan.KeyDown += delegate (object s2, KeyEventArgs ke)
            { if (ke.KeyCode == Keys.Escape) IrqCheckup.Cancel(); };
            pageIrq.Controls.Add(irqScan);
            irqScan.BringToFront();

            RefreshIrqPage();
        }

        private void RefreshIrqPage()
        {
            if (lstIrqDevices == null) return;

            try
            {
                irqSessions = IrqSessionLedger.Load();
                irqVerdicts = IrqVerdict.Evaluate(irqSessions,
                    CpuTopology.StrictBoostMask, IrqPageRefreshHz(), out irqUsedSessions);
                irqDevices = IrqDeviceInventory.Enumerate();
                IrqDeviceInventory.AttachVerdicts(irqDevices, irqVerdicts);
                IrqDeviceInventory.AttachCheckup(irqDevices, irqCheckup);
                IrqDeviceInventory.MarkOwnership(irqDevices);
                IrqDeviceInventory.Sort(irqDevices);
            }
            catch { irqDevices = new List<IrqDevice>(); }

            int keep = lstIrqDevices.SelectedIndex;
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
            if (keep >= 0 && keep < lstIrqDevices.Items.Count) lstIrqDevices.SelectedIndex = keep;

            bool admin = false;
            try { admin = Native.IsElevated(); } catch { }
            btnIrqRevert.Enabled = admin && IrqRelocate.HasResidue;
            if (swIrqProbePage != null)
            {
                swIrqProbePage.SetSilently(IrqSessionProbe.EnabledSetting);
                swIrqProbePage.Enabled = admin;
            }
            if (cardIrqProbe != null)
            {
                string probeText = !admin ? Lang.T("irq.probe.needadmin")
                    : IrqSessionProbe.EnabledSetting ? Lang.T("irq.probe.on")
                    : Lang.T("irq.probe.off");
                Color probeColor = !admin ? Theme.Danger
                    : IrqSessionProbe.EnabledSetting ? Theme.Green : Theme.Accent;
                cardIrqProbe.SetStatus(probeText, probeColor);
            }

            SetIrqState(admin, pending, unverified, mismatch, withIntr);
            RefreshIrqDetail();
        }

        private static int IrqPageRefreshHz()
        {
            try { return DisplayGuard.CurrentRefreshRate(); }
            catch { return 0; }
        }

        private void SetIrqState(bool admin, int pending, int unverified, int mismatch, int withIntr)
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
            else if (!IrqSessionProbe.EnabledSetting) { t = Lang.T("irq.state.probeoff"); c = Theme.Accent; }
            else if (mismatch > 0) { t = Lang.F("irq.state.mismatch", mismatch); c = Theme.Danger; }
            else if (pending > 0) { t = Lang.F("irq.state.pending", pending); c = Theme.Accent; }
            else if (unverified > 0) { t = Lang.F("irq.state.unverified", unverified); c = Theme.Dim; }
            else if (irqSessions.Count == 0) t = Lang.T("irq.state.nosessions");
            else if (irqUsedSessions < IrqSessionLedger.MinSessionsForVerdict)
                t = Lang.F("irq.state.fewsessions", irqUsedSessions, IrqSessionLedger.MinSessionsForVerdict);
            else if (withIntr == 0) t = Lang.T("irq.state.nointr");
            else t = Lang.F("irq.state.done", withIntr, irqUsedSessions);
            lblIrqState.Text = t;
            lblIrqState.ForeColor = c;
            if (irqBanner != null)
            {
                irqBanner.State = t;
                irqBanner.StateColor = c;
            }
        }

        private void Flash(string text, Color c) { irqFlash = text; irqFlashColor = c; }

        private IrqCheckupDevice CheckupFor(IrqDevice d)
        {
            if (irqCheckup == null || d == null || d.InstanceId.Length == 0) return null;
            foreach (IrqCheckupDevice c in irqCheckup.Devices)
                if (string.Equals(c.InstanceId, d.InstanceId, StringComparison.OrdinalIgnoreCase)) return c;
            return null;
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
            IrqCheckupDevice row = CheckupFor(d);
            string us = d.Dpc > 0
                ? (row != null
                    ? "p99 " + row.P99LoUs.ToString("F0") + "-" + row.P99Us.ToString("F0") + "us"
                    : d.MaxUs.ToString("F0") + "us " + IrqDeviceInventory.GradeText(grade))
                : "-";
            Cell(g, us, x, ty, Dpi.S(116), d.Dpc > 0 ? gc : Theme.Faint, false);
            x += Dpi.S(116);

            Cell(g, d.SeenOnCpus != 0 ? IrqRelocate.MaskText(d.SeenOnCpus) : "-",
                x, ty, Dpi.S(120), Theme.Dim, true);
            x += Dpi.S(120);

            string name = d.Name
                + (d.FromCheckup ? "  " + Lang.T("irq.tag.checkup") : "")
                + (row != null && row.SuggestMask != 0 ? "  " + Lang.T("irq.tag.suggest") : "")
                + (d.InputRisk ? "  " + Lang.T("irq.tag.input") : "");
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
                if (d.FrameworkStats)
                {
                    sb.Append(Environment.NewLine);
                    sb.Append(Lang.F("irq.d.framework", d.StatsDriver));
                }
            }
            else { sb.Append(Environment.NewLine); sb.Append(Lang.T("irq.d.nointr")); }

            Color c = Theme.Fg;
            if (d.InputRisk) { sb.Append(Environment.NewLine); sb.Append(Lang.T("irq.d.input")); }
            if (d.MultiMessageRisk)
            {
                sb.Append(Environment.NewLine);
                sb.Append(d.MessageCount > 1 ? Lang.F("irq.d.multimsg", d.MessageCount)
                                             : Lang.T("irq.d.multimsg.unknown"));
            }
            if (d.CompletionFollowsIssuer)
            { sb.Append(Environment.NewLine); sb.Append(Lang.T("irq.d.storagedpc")); }

            IrqCheckupDevice ck = CheckupFor(d);
            if (ck != null)
            {
                sb.Append(Environment.NewLine);
                sb.Append(Lang.F("irq.d.checkup",
                    ck.P99LoUs.ToString("F0") + "-" + ck.P99Us.ToString("F0"),
                    IrqRelocate.MaskText(ck.CpuMask),
                    ck.OnGameCores ? Lang.T("irq.d.checkup.hit") : Lang.T("irq.d.checkup.miss")));
                sb.Append(Environment.NewLine);
                if (ck.SuggestMask == 0) sb.Append(ck.NoSuggestReason);
                else
                {
                    sb.Append(Lang.F("irq.d.suggest", IrqRelocate.MaskText(ck.SuggestMask)));
                    if (ck.SuggestSinglePhysical)
                    { sb.Append(Environment.NewLine); sb.Append(Lang.T("irq.d.suggest.smt")); }
                    if (ck.SuggestSharesBackground)
                    { sb.Append(Environment.NewLine); sb.Append(Lang.T("irq.d.suggest.bg")); }
                }
            }
            if (d.ManagedElsewhere)
            { sb.Append(Environment.NewLine); sb.Append(Lang.T("irq.d.owned")); c = Theme.Faint; }
            else if (d.IsPinned)
            {
                sb.Append(Environment.NewLine);
                if (d.Effective)
                {
                    sb.Append(Lang.F("irq.d.effective", IrqRelocate.MaskText(d.Mask)));
                    c = Theme.Accent;
                }
                else if (d.PlacementMismatch)
                {
                    sb.Append(Lang.F("irq.d.written", IrqRelocate.MaskText(d.Mask)));
                    sb.Append(Environment.NewLine);
                    sb.Append(Lang.T("irq.tip.mismatch"));
                    c = Theme.Danger;
                }
                else if (d.Unverified)
                {
                    sb.Append(Lang.F("irq.d.written", IrqRelocate.MaskText(d.Mask)));
                    sb.Append(Environment.NewLine);
                    sb.Append(Lang.T("irq.tip.unverified"));
                    c = Theme.Faint;
                }
                else
                {
                    sb.Append(Lang.F("irq.d.written", IrqRelocate.MaskText(d.Mask)));
                    c = Theme.Danger;
                }
            }
            lblIrqDetail.Text = sb.ToString();
            lblIrqDetail.ForeColor = c;

            bool admin = false;
            try { admin = Native.IsElevated(); } catch { }
            btnIrqApply.Enabled = admin && !d.ManagedElsewhere;
        }


        private void OnIrqProbePageToggle(object sender, EventArgs e)
        {
            if (swIrqProbePage == null) return;
            IrqSessionProbe.EnabledSetting = swIrqProbePage.Checked;
            if (swIrqProbe != null) swIrqProbe.SetSilently(IrqSessionProbe.EnabledSetting);
            RefreshIrqPage();
        }

        private void OnIrqApply(object sender, EventArgs e)
        {
            IrqDevice d = SelectedIrqDevice();
            if (d == null || d.ManagedElsewhere) return;
            bool admin = false;
            try { admin = Native.IsElevated(); } catch { }
            if (!admin) return;

            ulong pick = 0;
            IrqCheckupDevice sug = CheckupFor(d);
            bool raisePriority = false;
            using (var dlg = new IrqPinDialog(d, sug))
                if (dlg.ShowDialog(this) == DialogResult.OK)
                { pick = dlg.Chosen; raisePriority = dlg.RaisePriority; }
            if (pick == 0) return;

            bool ok = false;
            try { ok = IrqRelocate.ApplyDevice(d.InstanceId, pick); } catch { }
            if (ok && raisePriority && !d.DevicePriorityHigh)
                try { IrqPriorityTweak.Apply(d.InstanceId); } catch { }
            Flash(ok ? Lang.F("irq.done.written", IrqRelocate.MaskText(pick)) : Lang.T("irq.done.fail"),
                ok ? Theme.Accent : Theme.Danger);
            RefreshIrqPage();
        }

        private void OnIrqRevert(object sender, EventArgs e)
        {
            bool ok = false;
            try { ok = IrqRelocate.Revert(); } catch { }
            try { if (!IrqPriorityTweak.RestoreAll()) ok = false; } catch { }
            Flash(ok ? Lang.T("irq.done.revert") : Lang.T("irq.done.fail"),
                ok ? Theme.Accent : Theme.Danger);
            RefreshIrqPage();
        }

        private void OnIrqCheckup(object sender, EventArgs e)
        {
            if (IrqCheckup.Busy) return;
            bool admin = false;
            try { admin = Native.IsElevated(); } catch { }
            if (!admin) { Flash(Lang.T("irqmove.needadmin"), Theme.Danger); return; }

            btnIrqCheckup.Enabled = false;
            btnIrqApply.Enabled = false;
            int total = IrqCheckup.DefaultSeconds;
            irqScan.Visible = true;
            irqScan.BringToFront();
            try { irqScan.Focus(); } catch { }
            irqScan.BeginScan(Lang.F("irq.checkup.running", 0, total));
            irqScan.Hint = Lang.T("irq.checkup.esc");
            ThreadPool.QueueUserWorkItem(delegate
            {
                IrqCheckupResult res;
                try
                {
                    res = IrqCheckup.Run(total, delegate (int done)
                    {
                        try
                        {
                            BeginInvoke((MethodInvoker)delegate
                            {
                                if (IsDisposed || lblIrqState == null) return;
                                string t = Lang.F("irq.checkup.running", done, total);
                                lblIrqState.Text = t;
                                lblIrqState.ForeColor = Theme.Dim;
                                if (irqScan != null)
                                    irqScan.ReportProgress((float)done / Math.Max(1, total), t);
                            });
                        }
                        catch { }
                    });
                }
                catch (Exception ex)
                {
                    res = new IrqCheckupResult();
                    res.Error = ex.GetType().Name;
                }
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        if (IsDisposed) return;
                        irqCheckup = res.Ok ? res : null;
                        if (irqScan != null) { irqScan.Stop(); irqScan.Visible = false; }
                        btnIrqCheckup.Enabled = true;
                        if (!res.Ok)
                            Flash(Lang.F("irq.checkup.fail", res.Error), Theme.Danger);
                        else if (res.Devices.Count == 0)
                            Flash(Lang.T("irq.checkup.none"), Theme.Dim);
                        else
                            Flash(Lang.F(res.Cancelled ? "irq.checkup.done.partial" : "irq.checkup.done",
                                res.Devices.Count, res.OnGameCoreCount, res.OverGateCount,
                                res.SuggestCount, res.Elapsed), Theme.Accent);
                        RefreshIrqPage();
                    });
                }
                catch { }
            });
        }

    }
}
