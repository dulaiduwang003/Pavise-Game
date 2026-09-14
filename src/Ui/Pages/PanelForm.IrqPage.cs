// @author bdth 2074055628@qq.com
// File purpose Interrupt page: record real matches, pick a device, then assign cores
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
        private PillButton btnIrqApply, btnIrqRevert, btnIrqRestoreSelected;
        private Toggle swIrqProbePage;
        private SettingCard cardIrqProbe;
        private TechListBox lstIrqDevices;
        private TextBox txtIrqSearch;
        private PillButton btnIrqClearSearch;
        private Label lblIrqDeviceCount, lblIrqEmpty, lblIrqSelected, lblIrqSelectionHint;
        private string irqSelectionId;
        private bool irqFiltering;
        private List<IrqDevice> irqDevices = new List<IrqDevice>();
        private List<IrqSessionRecord> irqSessions = new List<IrqSessionRecord>();
        private int irqUsedSessions;
        private int irqDisplaySessions;
        private string irqReadIssue = "";
        private string irqDevicesIssue = "";
        private int irqRefreshQueued;
        private string irqFlash = "";
        private Color irqFlashColor;
        private PillButton btnIrqNext;
        private Label lblIrqNext;
        private string irqNextAction = "observe";

        private void BuildIrqPage()
        {
            int top = PageHeader(pageIrq, Lang.T("nav.irq"), Lang.T("irq.page.flow"), 1);

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
            swIrqProbePage.AccessibleName = Lang.T("irq.sec.1");
            swIrqProbePage.TabIndex = 0;
            cardIrqProbe = MakeAutoCard(scroll, 0, y, InnerW, 78,
                Lang.T("irq.sec.1"), Lang.T("irq.page.probe"), swIrqProbePage,
                out irqProbeCardH);
            y += irqProbeCardH + 6;
            // The observation switch only shows capture state; data results are shown in this one place, no longer duplicated in the banner
            lblIrqState = CardLabel(scroll, "", 4, y, InnerW - 8, 34, 8.2f, false, Theme.Dim);
            lblIrqState.TextAlign = ContentAlignment.MiddleLeft;
            y += 40;
            lblIrqNext = CardLabel(scroll,"",4,y,InnerW - 232,44,9f,false,Theme.Dim);
            lblIrqNext.Name = "irqNextHint";
            btnIrqNext = new PillButton("",BtnKind.Primary) { Name = "irqNextAction" };
            btnIrqNext.SetBounds(Theme.S(InnerW - 220),Theme.S(y + 4),Theme.S(220),Theme.S(34));
            btnIrqNext.Click += OnIrqNextAction;
            scroll.Controls.Add(btnIrqNext);
            y += 52;

            lstIrqDevices = new TechListBox();
            int availH = PageH - top - 8;
            int listH = Math.Max(180, availH - y - 54 - 116 - 40);
            var deviceDeck = MakeConsolePanel(scroll, 0, y, InnerW, listH + 54, false);
            deviceDeck.Name = "irqDeviceDeck";
            deviceDeck.TabIndex = 1;
            CardLabel(deviceDeck, Lang.T("irq.sec.2"), 16, 7, InnerW - 444, 21, 10f, true, Theme.Fg);
            lblIrqDeviceCount = CardLabel(deviceDeck, "", 16, 29, InnerW - 444, 18, 8f, false, Theme.Dim);
            CardLabel(deviceDeck, Lang.T("irq.page.search"), InnerW - 420, 12, 100, 26, 8.5f, false, Theme.Dim);
            txtIrqSearch = Theme.MakeTextBox(Theme.S(InnerW - 318), Theme.S(12), Theme.S(260));
            txtIrqSearch.Name = "irqDeviceSearch";
            txtIrqSearch.AccessibleName = Lang.T("irq.page.search");
            txtIrqSearch.TabIndex = 0;
            txtIrqSearch.TextChanged += delegate { ApplyIrqDeviceFilter(); };
            txtIrqSearch.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape && txtIrqSearch.TextLength > 0)
                { txtIrqSearch.Clear(); e.SuppressKeyPress = true; }
                else if (e.KeyCode == Keys.Down && lstIrqDevices.Items.Count > 0)
                {
                    lstIrqDevices.Focus();
                    if (lstIrqDevices.SelectedIndex < 0) lstIrqDevices.SelectedIndex = 0;
                    e.SuppressKeyPress = true;
                }
            };
            deviceDeck.Controls.Add(txtIrqSearch);
            btnIrqClearSearch = new PillButton("×", BtnKind.Normal);
            btnIrqClearSearch.SetBounds(Theme.S(InnerW - 48), Theme.S(11), Theme.S(32), Theme.S(28));
            btnIrqClearSearch.AccessibleName = Lang.T("irq.page.clearsearch");
            btnIrqClearSearch.TabIndex = 1;
            btnIrqClearSearch.Click += delegate { txtIrqSearch.Clear(); txtIrqSearch.Focus(); };
            deviceDeck.Controls.Add(btnIrqClearSearch);
            lstIrqDevices.SetBounds(Theme.S(8), Theme.S(50), Theme.S(InnerW - 16), Theme.S(listH));
            Theme.StyleList(lstIrqDevices, false);
            lstIrqDevices.DrawItem += DrawIrqRow;
            lstIrqDevices.ItemHeight = Theme.S(52);
            lstIrqDevices.Font = Theme.UI(9.5f, false);
            lstIrqDevices.AccessibleName = Lang.T("irq.sec.2");
            lstIrqDevices.TabIndex = 2;
            lstIrqDevices.SelectedIndexChanged += delegate
            {
                if (irqFiltering) return;
                IrqDevice selected = SelectedIrqDevice();
                if (selected != null) irqSelectionId = selected.InstanceId;
                UpdateIrqApplyEnabled();
            };
            lstIrqDevices.MouseDoubleClick += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left && lstIrqDevices.IndexFromPoint(e.Location) >= 0)
                    OnIrqApply(sender, EventArgs.Empty);
            };
            lstIrqDevices.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter)
                { OnIrqApply(sender, EventArgs.Empty); e.SuppressKeyPress = true; }
            };
            deviceDeck.Controls.Add(lstIrqDevices);
            lblIrqEmpty = CardLabel(deviceDeck, "", 24, 50 + (listH - 40) / 2,
                InnerW - 48, 40, 9f, false, Theme.Dim);
            lblIrqEmpty.TextAlign = ContentAlignment.MiddleCenter;
            lblIrqEmpty.BackColor = Theme.Card;
            y += listH + 62;

            var actionDeck = MakeConsolePanel(scroll, 0, y, InnerW, 108, true);
            actionDeck.Name = "irqSelectedActions";
            actionDeck.TabIndex = 2;
            lblIrqSelected = CardLabel(actionDeck, "", 16, 10, InnerW - 32, 23, 10f, true, Theme.Fg);
            lblIrqSelectionHint = CardLabel(actionDeck, "", 16, 35, InnerW - 32, 20, 8.5f, false, Theme.Dim);
            lblIrqSelected.AutoEllipsis = true;
            lblIrqSelectionHint.AutoEllipsis = true;
            btnIrqApply = new PillButton(Lang.T("irq.page.configure"), BtnKind.Primary);
            btnIrqApply.SetBounds(Theme.S(16), Theme.S(65), Theme.S(220), Theme.S(32));
            btnIrqApply.TabIndex = 0;
            btnIrqApply.Click += OnIrqApply;
            actionDeck.Controls.Add(btnIrqApply);
            btnIrqRestoreSelected = new PillButton(Lang.T("irq.restore.selected"), BtnKind.Normal);
            btnIrqRestoreSelected.SetBounds(Theme.S(246), Theme.S(65), Theme.S(180), Theme.S(32));
            btnIrqRestoreSelected.TabIndex = 1;
            btnIrqRestoreSelected.Click += OnIrqRestoreSelected;
            actionDeck.Controls.Add(btnIrqRestoreSelected);
            y += 116;
            CardLabel(scroll, Lang.T("irq.page.restorehint"), 4, y + 3, InnerW - 260, 24, 8f, false, Theme.Faint);
            btnIrqRevert = new PillButton(Lang.T("irq.btn.revert"), BtnKind.Normal);
            btnIrqRevert.SetBounds(Theme.S(InnerW - 240), Theme.S(y), Theme.S(240), Theme.S(28));
            btnIrqRevert.Font = Theme.UI(8f, false);
            btnIrqRevert.TabIndex = 3;
            btnIrqRevert.Click += OnIrqRevert;
            scroll.Controls.Add(btnIrqRevert);

            RefreshIrqPage();
        }

        private void RefreshIrqPage()
        {
            if (lstIrqDevices == null) return;

            // Remember the selection by device ID before reordering; restoring by old index, after a Worth pin-to-top or a measurement change,
            // would silently select another device, and a later Select cores click could land on the wrong target
            IrqDevice oldDevice = SelectedIrqDevice();
            if (oldDevice != null) irqSelectionId = oldDevice.InstanceId;

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
                    // Complete short matches show raw measurements immediately; suggestions still count only the original >=60 second qualifying matches
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
                string adjustmentIssue;
                var adjustments = IrqAdjustmentLedger.Load(out adjustmentIssue);
                foreach (var device in irqDevices)
                {
                    var current = IrqAdjustmentHistory.Current(adjustments,device.InstanceId);
                    if (current != null)
                        device.AdjustmentPlacement = IrqAdjustmentVerification.Placement(current,device,irqSessions,
                            IrqAffinityEngine.BootStamp(),CpuTopology.TopologyStamp());
                }
                if (adjustmentIssue.Length > 0)
                {
                    irqReadIssue = adjustmentIssue;
                    foreach (var device in irqDevices) device.AdjustmentPlacement = "unknown";
                }
                IrqDeviceInventory.Sort(irqDevices);
            }
            catch
            {
                irqDevices = new List<IrqDevice>();
                irqDevicesIssue = Lang.T("irq.state.devicesfailed");
            }

            int withIntr = 0, pending = 0, unverified = 0, mismatch = 0;
            foreach (IrqDevice d in irqDevices)
            {
                if (d.Dpc > 0) withIntr++;
                if (d.AwaitingReboot) pending++;
                else if (d.PlacementMismatch) mismatch++;
                else if (d.Unverified) unverified++;
            }
            ApplyIrqDeviceFilter();

            bool admin = false;
            try { admin = Native.IsElevated(); } catch { }
            btnIrqRevert.Enabled = admin && (IrqRelocate.HasResidue || IrqPriorityTweak.HasResidue);
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
                Color probeColor = !admin || captureWarning ? Theme.Warning
                    : IrqSessionProbe.EnabledSetting ? Theme.Accent : Theme.Dim;
                cardIrqProbe.SetStatus(probeText, probeColor);
            }

            SetIrqState(admin, pending, unverified, mismatch, withIntr, captureText, captureWarning);
            irqNextAction = IrqUiState.NextAction(admin,IrqSessionProbe.EnabledSetting,irqDisplaySessions,pending);
            btnIrqNext.Text = Lang.T("irq.flow.action." + irqNextAction);
            lblIrqNext.Text = Lang.T("irq.flow.hint." + irqNextAction);
            lblIrqNext.ForeColor = irqNextAction == "restart" || irqNextAction == "admin" ? Theme.Warning : Theme.Dim;
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
            if (!admin) { t = Lang.T("irq.state.needadmin"); c = Theme.Warning; }
            else if (!IrqSessionProbe.EnabledSetting) { t = Lang.T("irq.state.probeoff"); c = Theme.Accent; }
            else if (dataWarning) { t = dataText; c = Theme.Danger; }
            else if (mismatch > 0) { t = Lang.F("irq.state.mismatch", mismatch); c = Theme.Danger; }
            else if (pending > 0) { t = Lang.F("irq.state.pending", pending); c = Theme.Warning; }
            else if (irqUsedSessions >= IrqSessionLedger.MinSessionsForVerdict && IrqWorthCount() > 0)
            { t = Lang.F("irq.state.suggest", IrqWorthCount()); c = Theme.Accent; }
            else if (unverified > 0) { t = Lang.F("irq.state.unverified", unverified); c = Theme.Warning; }
            else t = dataText;
            t = IrqPageStatus.CountedText(t, irqSessions.Count, irqUsedSessions, irqReadIssue);
            lblIrqState.Text = t;
            lblIrqState.ForeColor = c;
        }

        private void Flash(string text, Color c) { irqFlash = text; irqFlashColor = c; }

        // Count only Worth driver verdicts already mapped to a clickable device; if one cannot be mapped, do not tell the user there is
        // a suggestion with no entry point to be found
        private int IrqWorthCount()
        {
            int n = 0;
            if (irqDevices != null)
                foreach (IrqDevice d in irqDevices)
                    if (d != null && d.ActionableWorth) n++;
            return n;
        }

        // Driven by GameMode's match-end suggestion event, arrives cross-thread; refreshes the interrupt page state and suggestions
        //   Never edits the registry automatically; only steers the user to the interrupt page's existing manual flow; safely skipped when the page is not built yet
        public void NotifyIrqSuggestions(int count)
        {
            if (count > 0) NotifyIrqObservationUpdated();
        }

        // Refresh on every match completion/failure, no longer requires an IRQ core move suggestion; merged with the suggestion notification; does not enumerate devices while hidden
        // Page re-activation still goes through RefreshIrqPage, so a first match with zero suggestions never shows a stale empty state forever
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

            int x = e.Bounds.X + Theme.S(14);
            int width = e.Bounds.Width - Theme.S(28);
            int statusWidth = Math.Min(Theme.S(174), width / 3);
            Color statusColor;
            string status = IrqDeviceStatus(d, out statusColor);
            if (sel)
                using (var marker = new SolidBrush(Theme.Accent))
                    g.FillRectangle(marker, e.Bounds.X + Theme.S(2), e.Bounds.Y + Theme.S(10),
                        Theme.S(3), e.Bounds.Height - Theme.S(20));
            TextRenderer.DrawText(g, d.Name, Theme.UI(9.5f, true),
                new Rectangle(x, e.Bounds.Y + Theme.S(5), width - statusWidth - Theme.S(12), Theme.S(22)),
                Theme.Fg, TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, status, Theme.UI(8.5f, false),
                new Rectangle(e.Bounds.Right - Theme.S(14) - statusWidth, e.Bounds.Y + Theme.S(5), statusWidth, Theme.S(22)),
                statusColor, TextFormatFlags.Right | TextFormatFlags.VerticalCenter
                | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, IrqDeviceDetail(d), Theme.UI(8.5f, false),
                new Rectangle(x, e.Bounds.Y + Theme.S(27), width, Theme.S(19)),
                Theme.Dim, TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            if (sel && lb.Focused) e.DrawFocusRectangle();
        }

        internal static string IrqDeviceDetail(IrqDevice d)
        {
            string observation = d.Dpc > 0
                ? Lang.F("irq.page.observed", d.MaxUs.ToString("F0"),
                    IrqDeviceInventory.GradeText(IrqDeviceInventory.Grade(d)))
                : Lang.T("irq.page.notobserved");
            string detail = IrqDeviceKind(d) + "  ·  " + observation;
            if (d.Dpc > 0 && (d.SharedStats || d.FrameworkStats))
                detail += "  ·  " + Lang.T("irq.page.shared");
            if (d.IsPinned && d.ActionableWorth) detail += "  ·  " + Lang.T("irq.tag.matchsuggest").Trim('[', ']');
            return detail;
        }

        private static string IrqDeviceStatus(IrqDevice d, out Color color)
        {
            string state = IrqUiState.Placement(d);
            color = IrqUiState.ColorFor(state);
            if (state == "none" && d.ActionableWorth)
            { color = Theme.Accent; return Lang.T("irq.tag.matchsuggest").Trim('[', ']'); }
            return IrqUiState.Text(state);
        }

        private static string IrqDeviceKind(IrqDevice d)
        {
            if (d.InputRisk) return Lang.T("irq.page.kind.input");
            if (IrqDeviceInventory.LooksStorage(d)) return Lang.T("irq.page.kind.storage");
            if (string.Equals(d.ClassGuid, "{4d36e972-e325-11ce-bfc1-08002be10318}", StringComparison.OrdinalIgnoreCase))
                return Lang.T("irq.page.kind.network");
            if (string.Equals(d.ClassGuid, "{4d36e968-e325-11ce-bfc1-08002be10318}", StringComparison.OrdinalIgnoreCase))
                return Lang.T("irq.page.kind.display");
            if (string.Equals(d.ClassGuid, "{4d36e96c-e325-11ce-bfc1-08002be10318}", StringComparison.OrdinalIgnoreCase))
                return Lang.T("irq.page.kind.audio");
            return string.IsNullOrEmpty(d.Bus) ? Lang.T("irq.page.kind.other") : d.Bus;
        }

        internal static bool IrqDeviceMatches(IrqDevice device, string query)
        {
            return device != null && GameSearchMatcher.Matches(query, device.Name,
                device.Service + " " + device.Bus + " " + device.InstanceId + " " + IrqDeviceKind(device));
        }

        // Search only filters the enumerated snapshot; actions are disabled while the selection is hidden; after clearing the filter the item is found again by device ID
        private void ApplyIrqDeviceFilter()
        {
            if (lstIrqDevices == null) return;
            IrqDevice old = SelectedIrqDevice();
            if (old != null) irqSelectionId = old.InstanceId;
            string query = txtIrqSearch == null ? "" : txtIrqSearch.Text;
            irqFiltering = true;
            lstIrqDevices.BeginUpdate();
            try
            {
                lstIrqDevices.Items.Clear();
                foreach (IrqDevice device in irqDevices)
                    if (IrqDeviceMatches(device, query))
                    {
                        int index = lstIrqDevices.Items.Add(device);
                        if (!string.IsNullOrEmpty(irqSelectionId)
                            && string.Equals(device.InstanceId, irqSelectionId, StringComparison.OrdinalIgnoreCase))
                            lstIrqDevices.SelectedIndex = index;
                    }
            }
            finally { lstIrqDevices.EndUpdate(); irqFiltering = false; }
            if (lblIrqDeviceCount != null)
                lblIrqDeviceCount.Text = string.IsNullOrWhiteSpace(query)
                    ? Lang.F("irq.page.devices", irqDevices.Count)
                    : Lang.F("irq.page.filtered", lstIrqDevices.Items.Count, irqDevices.Count);
            if (btnIrqClearSearch != null) btnIrqClearSearch.Enabled = query.Length > 0;
            if (lblIrqEmpty != null)
            {
                lblIrqEmpty.Text = Lang.T(irqDevices.Count == 0 ? "irq.page.nodevices" : "irq.page.nomatches");
                lblIrqEmpty.Visible = lstIrqDevices.Items.Count == 0;
                if (lblIrqEmpty.Visible) lblIrqEmpty.BringToFront();
            }
            UpdateIrqApplyEnabled();
        }

        private IrqDevice SelectedIrqDevice()
        {
            return lstIrqDevices == null ? null : lstIrqDevices.SelectedItem as IrqDevice;
        }

        // The action entry always follows the currently visible selection; state the target before selecting; detailed evidence stays in the dialog
        private void UpdateIrqApplyEnabled()
        {
            if (btnIrqApply == null) return;
            IrqDevice d = SelectedIrqDevice();
            bool admin = false;
            try { admin = Native.IsElevated(); } catch { }
            btnIrqApply.Enabled = d != null;
            btnIrqApply.Text = Lang.T(d != null && admin && !d.ManagedElsewhere
                ? "irq.page.configure" : "irq.flow.action.review");
            if (btnIrqRestoreSelected != null)
                btnIrqRestoreSelected.Enabled = d != null && admin && !d.ManagedElsewhere && IrqRelocate.Owns(d.InstanceId);
            if (lblIrqSelected != null)
                lblIrqSelected.Text = d == null ? Lang.T("irq.page.pick") : Lang.F("irq.page.selected", d.Name);
            if (lblIrqSelectionHint != null)
            {
                string key = d == null ? "irq.page.pickhint"
                    : !admin ? "irq.flow.readonly"
                    : d.ManagedElsewhere ? "irq.page.elsewhere"
                    : d.AwaitingReboot ? "irq.page.reboothint"
                    : "irq.page.reviewhint";
                lblIrqSelectionHint.Text = Lang.T(key);
                lblIrqSelectionHint.ForeColor = d != null && !admin ? Theme.Warning : Theme.Dim;
            }
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
        { OpenIrqDevice(false); }

        private void OnIrqNextAction(object sender, EventArgs e)
        {
            if (irqNextAction == "enable")
            {
                swIrqProbePage.Checked = true;
                return;
            }
            if (irqNextAction == "review")
            {
                if (SelectedIrqDevice() == null && lstIrqDevices.Items.Count > 0) lstIrqDevices.SelectedIndex = 0;
                if (SelectedIrqDevice() != null) { OpenIrqDevice(true); return; }
            }
            PaviseDialog.Info(this,Lang.T("nav.irq"),Lang.T("irq.flow.hint." + irqNextAction));
        }

        private void OpenIrqDevice(bool evidence)
        {
            IrqDevice d = SelectedIrqDevice();
            if (d == null) return;
            bool admin = false;
            try { admin = Native.IsElevated(); } catch { }
            ulong pick = 0;
            bool raisePriority = false;
            var versionReader = IrqSessionProbe.CreateDriverVersionReader();
            var history = IrqPinSession.History(irqSessions,d,IrqAffinityEngine.BootStamp(),
                CpuTopology.TopologyStamp(),versionReader);
            IrqPinSession session = history.Count == 0 ? new IrqPinSession() : history[history.Count - 1];
            using (var dlg = new IrqPinDialog(d, session, history, irqSessions))
            {
                if (!admin || d.ManagedElsewhere)
                    dlg.SetReadOnly(Lang.T(!admin ? "irq.flow.readonly" : "irq.page.elsewhere"));
                if (evidence || !admin || d.ManagedElsewhere) dlg.SelectPage(1);
                if (dlg.ShowDialog(this) == DialogResult.OK)
                { pick = dlg.Chosen; raisePriority = dlg.RaisePriority; session = dlg.CurrentSession; }
            }
            if (pick == 0 || !admin || d.ManagedElsewhere) return;

            IrqAdjustment adjustment = IrqAdjustmentLedger.Prepare(d,session,pick,raisePriority,irqSessions);
            if (!IrqAdjustmentLedger.Save(adjustment))
            { Flash(Lang.T("irq.adjust.savefailed"),Theme.Danger); RefreshIrqPage(); return; }
            IrqChangeResult result = null;
            bool priorityUnchanged = false;
            IrqMutationBoundary.Run(delegate
            {
                result = IrqChangeResult.Run(delegate { return IrqRelocate.ApplyDevice(d.InstanceId,pick); },
                    raisePriority ? (Func<bool>)delegate { return IrqPriorityTweak.Apply(d.InstanceId,out priorityUnchanged); } : null,false);
            });
            IrqAdjustmentHistory.RecordWrite(adjustment,result,IrqRelocate.LastWriteResult,priorityUnchanged);
            bool saved = IrqAdjustmentLedger.Save(adjustment);
            string message = Lang.T("irq.phase." + adjustment.Phase) + " · " + result.Describe(false) + " · " + IrqRelocate.LastWriteResult.Describe();
            if (!saved) message += " · " + Lang.T("irq.adjust.savefailed");
            Flash(message,result.Success && saved ? Theme.Warning : Theme.Danger);
            RefreshIrqPage();
        }

        private void OnIrqRestoreSelected(object sender, EventArgs e)
        {
            var d = SelectedIrqDevice();
            if (d == null || d.ManagedElsewhere || !Native.IsElevated()) return;
            RestoreIrq(d.InstanceId);
        }

        private void OnIrqRevert(object sender, EventArgs e)
        {
            if (!Native.IsElevated()) return;
            RestoreIrq(null);
        }

        private void RestoreIrq(string id)
        {
            IrqRestoreResult result = null;
            IrqMutationBoundary.Run(delegate
            {
                result = IrqRelocate.RestoreDevices(id);
            });
            string issue; var changes = IrqAdjustmentLedger.Load(out issue);
            bool saved = issue.Length == 0;
            foreach (var change in IrqAdjustmentHistory.RecordRestore(changes,result,DateTime.UtcNow.Ticks))
                if (!IrqAdjustmentLedger.Save(change)) saved = false;
            Flash(result.Describe() + (saved ? "" : " · " + Lang.T("irq.adjust.savefailed")),
                result.Success && saved ? Theme.Accent : Theme.Danger);
            RefreshIrqPage();
        }

    }

    // State selection only, no window; a fake observation with device enumeration or ETW available covers every branch of the blank page
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
            // On a read failure the count is unknown; do not show the empty failure return as 0 matches recorded
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
                case IrqSessionExclusion.UnmappedEvents: return Lang.T("irq.compare.reason.sample");
                case IrqSessionExclusion.NoDuration: return Lang.T("irq.record.noduration");
                default: return Lang.T("irq.record.unavailable");
            }
        }
    }
}
