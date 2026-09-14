// File purpose Core selection and observation details shown separately; action results and the apply entry stay visible
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace PaviseApp
{
    internal sealed class IrqPinDialog : Form
    {
        private const int DlgW = 700, DlgH = 690;
        private readonly CoreMatrix matrix;
        private readonly Label lblPick, footerHint, titleLabel, deviceLabel, closeLabel;
        private readonly PillButton btnOk, btnCancel, tabCores, tabEvidence, advancedToggle;
        private readonly PillButton clearSelection, resetSelection;
        private readonly Toggle swPriority;
        private readonly Panel scrollBody, corePage, evidencePage, summaryCard, advancedPanel;
        private readonly Panel observationCard, verificationCard, deviceCard;
        private readonly Label metricDpc, metricCurrent, metricStatus, benefitLabel, riskLabel, howto;
        private readonly Label coreHeading, candidateHeading, candidateHint, priorityLabel;
        private readonly Label sourceLabel, coverageLabel, statLabel, extraLabel, coreDetails, candidateDetails;
        private readonly Label verificationLabel, deviceDetails;
        private readonly PillButton historyPicker;
        private readonly ContextMenuStrip historyMenu;
        private readonly PillButton[] optionButtons = new PillButton[3];
        private readonly List<IrqCoreOption> options = new List<IrqCoreOption>();
        private readonly ToolTip hints = new ToolTip();
        private readonly IrqDevice device;
        private readonly IList<IrqPinSession> history;
        private readonly IList<IrqSessionRecord> allSessions;
        private int bodyContentHeight;
        private bool fittedToWorkArea, clockWasSuspended, layingOut;
        private string readOnlyReason;

        public ulong Chosen { get; private set; }
        public bool RaisePriority { get { return swPriority.Checked; } }
        internal int SelectedHistoryIndex { get; private set; }
        internal int SelectedPage { get; private set; }
        internal bool AdvancedExpanded { get; private set; }
        internal IrqPinSession CurrentSession { get; private set; }

        public IrqPinDialog(IrqDevice d) : this(d,new IrqPinSession()) { }
        internal IrqPinDialog(IrqDevice d, IrqPinSession session) : this(d,session,null,null) { }
        internal IrqPinDialog(IrqDevice d, IrqPinSession session,
            IList<IrqPinSession> choices, IList<IrqSessionRecord> records)
        {
            device = d; allSessions = records;
            session = session ?? new IrqPinSession();
            history = choices ?? new List<IrqPinSession> { session };
            SelectedHistoryIndex = history.Count - 1;
            Text = Lang.T("irq.ui.dialog");
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = MinimizeBox = ShowInTaskbar = false;
            AutoScaleMode = AutoScaleMode.None;
            DoubleBuffered = true;
            BackColor = Theme.Bg; ForeColor = Theme.Fg; Font = Theme.UI(9.5f,false);
            titleLabel = LabelFor(this,"",Text,15,true,Theme.Fg);
            deviceLabel = LabelFor(this,"irqDeviceName",d == null ? "" : d.Name,9,false,Theme.Dim);
            deviceLabel.AutoEllipsis = true;
            hints.SetToolTip(deviceLabel,deviceLabel.Text);
            closeLabel = LabelFor(this,"","×",17,false,Theme.Dim);
            closeLabel.TextAlign = ContentAlignment.MiddleCenter; closeLabel.Cursor = Cursors.Hand;
            closeLabel.Click += delegate { Close(); };
            titleLabel.MouseDown += DragMove; deviceLabel.MouseDown += DragMove;
            tabCores = ButtonFor(this,"irqTabCores",Lang.T("irq.ui.tab.cores"),delegate { SelectPage(0); });
            tabEvidence = ButtonFor(this,"irqTabEvidence",Lang.T("irq.ui.tab.evidence"),delegate { SelectPage(1); });
            historyMenu = new ContextMenuStrip { Renderer = new TechMenuRenderer(),
                ShowImageMargin = false, Font = Theme.UI(9,false) };
            historyPicker = ButtonFor(this,"irqSessionPicker","",delegate {
                historyMenu.Show(historyPicker,new Point(0,historyPicker.Height)); });
            for (int i = 0; i < history.Count; i++)
            {
                int index = i;
                var choice = new ToolStripMenuItem(SessionTitle(history[i]));
                choice.ForeColor = Theme.Fg;
                choice.Click += delegate { SelectHistory(index); };
                historyMenu.Items.Add(choice);
            }
            historyPicker.Enabled = history.Count > 0;
            scrollBody = new Panel { Name = "irqPinScrollBody", BackColor = Theme.Bg, AutoScroll = true };
            Native.Dark(scrollBody);
            Controls.Add(scrollBody);
            corePage = new Panel { Name = "irqCorePage", BackColor = Theme.Bg };
            evidencePage = new Panel { Name = "irqEvidencePage", BackColor = Theme.Bg };
            scrollBody.Controls.Add(corePage); scrollBody.Controls.Add(evidencePage);
            summaryCard = Card(corePage,"irqSummary");
            LabelFor(summaryCard,"metricDpcTitle",Lang.T("irq.ui.metric.dpc"),8,false,Theme.Dim);
            LabelFor(summaryCard,"metricCurrentTitle",Lang.T("irq.ui.metric.current"),8,false,Theme.Dim);
            LabelFor(summaryCard,"metricStatusTitle",Lang.T("irq.ui.metric.status"),8,false,Theme.Dim);
            metricDpc = LabelFor(summaryCard,"irqMetricDpc","—",13,true,Theme.Fg);
            metricCurrent = LabelFor(summaryCard,"irqMetricCurrent","—",11,true,Theme.Fg);
            metricStatus = LabelFor(summaryCard,"irqMetricStatus","—",10,true,Theme.Accent);
            benefitLabel = LabelFor(summaryCard,"irqBenefit",Lang.T("irq.flow.benefit"),8.3f,false,Theme.Dim);
            riskLabel = LabelFor(corePage,"irqRisk","",8.5f,false,Theme.Danger);
            coreHeading = LabelFor(corePage,"",Lang.T("irq.ui.coreheading"),10,true,Theme.Fg);
            resetSelection = ButtonFor(corePage,"irqResetSelection",Lang.T("irq.ui.reset"),delegate {
                SetSelection(device != null && device.IsPinned ? device.Mask : 0); });
            clearSelection = ButtonFor(corePage,"irqClearSelection",Lang.T("irq.ui.clear"),delegate { SetSelection(0); });
            resetSelection.Enabled = d != null && d.IsPinned;
            howto = LabelFor(corePage,"irqSessionHowto",Lang.T("irq.ui.howto"),8.5f,false,Theme.Dim);
            matrix = new CoreMatrix { Name = "irqCoreMatrix", Annotate = true, BackColor = Theme.Bg };
            matrix.LayoutFor(Theme.S(DlgW - 62));
            matrix.Selected = d != null && d.IsPinned ? d.Mask : 0;
            Chosen = matrix.Selected; matrix.SelectionChanged = OnPicked;
            corePage.Controls.Add(matrix);
            candidateHeading = LabelFor(corePage,"",Lang.T("irq.ui.candidates"),9,true,Theme.Fg);
            candidateHint = LabelFor(corePage,"irqCandidateHint","",8.3f,false,Theme.Dim);
            for (int i = 0; i < optionButtons.Length; i++)
            {
                int index = i;
                optionButtons[i] = ButtonFor(corePage,"irqOption" + i,"",delegate {
                    if (index < options.Count) SetSelection(options[index].Mask); });
            }
            advancedToggle = ButtonFor(corePage,"irqAdvancedToggle","",delegate { SetAdvanced(!AdvancedExpanded); });
            advancedPanel = Card(corePage,"irqAdvancedPanel");
            swPriority = new Toggle();
            swPriority.Checked = d != null && (d.DevicePriorityHigh || IrqPriorityTweak.AppliedTo(d.InstanceId));
            swPriority.Enabled = d != null && !d.DevicePriorityHigh;
            advancedPanel.Controls.Add(swPriority);
            priorityLabel = LabelFor(advancedPanel,"",Lang.T("irqpin.prio"),9,false,Theme.Fg);

            observationCard = Card(evidencePage,"irqObservationCard");
            LabelFor(observationCard,"cardTitle",Lang.T("irq.ui.observation"),10,true,Theme.Fg);
            sourceLabel = LabelFor(observationCard,"irqLoadSource","",9,true,Theme.Fg);
            coverageLabel = LabelFor(observationCard,"irqLoadCoverage","",8.5f,false,Theme.Dim);
            statLabel = LabelFor(observationCard,"irqSessionStat","",8.5f,false,Theme.Dim);
            extraLabel = LabelFor(observationCard,"irqSessionExtra","",8.5f,false,Theme.Dim);
            coreDetails = LabelFor(observationCard,"irqCoreDetails","",8.5f,false,Theme.Dim);
            candidateDetails = LabelFor(observationCard,"irqCandidate","",8.5f,false,Theme.Dim);
            verificationCard = Card(evidencePage,"irqVerificationCard");
            LabelFor(verificationCard,"cardTitle",Lang.T("irq.ui.verification"),10,true,Theme.Fg);
            verificationLabel = LabelFor(verificationCard,"irqVerification","",8.5f,false,Theme.Dim);
            deviceCard = Card(evidencePage,"irqDeviceCard");
            LabelFor(deviceCard,"cardTitle",Lang.T("irq.ui.device"),10,true,Theme.Fg);
            deviceDetails = LabelFor(deviceCard,"irqDeviceDetails","",8.5f,false,Theme.Dim);
            lblPick = LabelFor(this,"irqChosenSummary","",9,true,Theme.Fg);
            footerHint = LabelFor(this,"irqApplyHint",Lang.T("irq.ui.apply.hint"),8.3f,false,Theme.Dim);
            btnCancel = ButtonFor(this,"irqCancel",Lang.T("irqpin.cancel"),delegate { Close(); });
            btnOk = ButtonFor(this,"irqApply",Lang.T("irq.ui.apply"),delegate {
                if (!btnOk.Enabled) return;
                DialogResult = DialogResult.OK; Close(); });
            btnOk.Kind = BtnKind.Primary;
            swPriority.CheckedChanged += delegate {
                advancedToggle.Text = (AdvancedExpanded ? "−  " : "+  ") + Lang.T("irq.ui.advanced")
                    + (RaisePriority ? "  ·  " + Lang.T("irq.ui.priority.on") : ""); };
            ClientSize = new Size(Theme.S(DlgW),Theme.S(DlgH));
            UpdateSession(session); SelectPage(0); SetAdvanced(false); OnPicked(Chosen);
        }

        private static Label LabelFor(Control parent, string name, string text, float size, bool bold, Color color)
        {
            var label = new Label { Name = name, Text = text, Font = Theme.UI(size,bold),
                ForeColor = color, BackColor = parent.BackColor, UseCompatibleTextRendering = false };
            parent.Controls.Add(label); return label;
        }
        private static PillButton ButtonFor(Control parent, string name, string text, Action click)
        {
            var button = new PillButton(text,BtnKind.Normal) { Name = name, Bg = parent.BackColor,
                AccessibleRole = AccessibleRole.PushButton };
            button.Click += delegate { click(); };
            parent.Controls.Add(button); return button;
        }
        private static Panel Card(Control parent, string name)
        {
            var panel = new DBPanel { Name = name, BackColor = Theme.Card };
            panel.Paint += delegate(object sender,PaintEventArgs e) {
                using (var pen = new Pen(Theme.Stroke))
                    e.Graphics.DrawRectangle(pen,0,0,Math.Max(0,panel.Width - 1),Math.Max(0,panel.Height - 1)); };
            parent.Controls.Add(panel); return panel;
        }
        private static string SessionTitle(IrqPinSession session)
        {
            if (session == null || !session.Available) return Lang.T("irqpin.session.none");
            string time = session.StartUtcTicks > 0
                ? new DateTime(session.StartUtcTicks,DateTimeKind.Utc).ToLocalTime().ToString("MM-dd HH:mm") : "—";
            return session.GameName + "  ·  " + time + "  ·  " + session.DurationSeconds + "s";
        }

        internal void SelectPage(int page)
        {
            if (page < 0 || page > 1) return;
            SelectedPage = page;
            corePage.Visible = page == 0; evidencePage.Visible = page == 1;
            tabCores.Kind = page == 0 ? BtnKind.Primary : BtnKind.Normal;
            tabEvidence.Kind = page == 1 ? BtnKind.Primary : BtnKind.Normal;
            tabCores.Invalidate(); tabEvidence.Invalidate();
            LayoutViewportAndFooter(0);
        }
        internal void SetAdvanced(bool expanded)
        {
            int scroll = Math.Max(0,-scrollBody.AutoScrollPosition.Y);
            AdvancedExpanded = expanded; advancedPanel.Visible = expanded;
            advancedToggle.Text = (expanded ? "−  " : "+  ") + Lang.T("irq.ui.advanced")
                + (RaisePriority ? "  ·  " + Lang.T("irq.ui.priority.on") : "");
            LayoutViewportAndFooter(scroll);
            if (expanded && SelectedPage == 0 && advancedPanel.Bottom > scroll + scrollBody.ClientSize.Height)
                scrollBody.AutoScrollPosition = new Point(0,advancedPanel.Bottom - scrollBody.ClientSize.Height + Theme.S(10));
        }
        internal void SelectHistory(int index)
        {
            if (index < 0 || index >= history.Count) return;
            SelectedHistoryIndex = index; UpdateSession(history[index]);
        }

        private void UpdateSession(IrqPinSession session)
        {
            CurrentSession = session;
            historyPicker.Text = Lang.T("irq.ui.reference") + "  " + SessionTitle(session)
                + (history.Count > 0 ? "  ▾" : "");
            hints.SetToolTip(historyPicker,historyPicker.Text);
            for (int i = 0; i < historyMenu.Items.Count; i++)
                ((ToolStripMenuItem)historyMenu.Items[i]).Checked = i == SelectedHistoryIndex;
            matrix.SeenMask = session.SeenMask; matrix.ObservedGameMask = session.GameMask;
            matrix.SetLoads(session.Loads);
            var driver = session.Driver;
            metricDpc.Text = driver == null ? "—" : driver.DpcMaxUs.ToString("F0") + " µs";
            metricCurrent.Text = IrqUiState.LogicalCpus(device != null && device.IsPinned ? device.Mask : 0);
            sourceLabel.Text = SessionTitle(session)
                + (session.CurrentBoot || !session.Available ? "" : "\n" + Lang.T("irq.session.previousboot"));
            coverageLabel.Text = session.Loads.Count == 0 ? Lang.T("irqpin.session.noload")
                : Lang.F("irqpin.session.coverage",session.MinimumCoverage.ToString("F0"),session.MaximumCoverage.ToString("F0"));
            statLabel.Text = driver == null ? Lang.T("irqpin.session.nodevice")
                : Lang.F("irqpin.stat",driver.DpcMaxUs.ToString("F0"),
                    IrqDeviceInventory.GradeText(IrqDeviceInventory.Grade(new IrqDevice { Dpc = driver.Dpc,MaxUs = driver.DpcMaxUs })),
                    IrqRelocate.MaskText(session.SeenMask));
            extraLabel.Text = driver == null ? "" : IrqDeviceInventory.BudgetText(driver.DpcMaxUs)
                + " · " + Lang.F("irq.d.over500",driver.Over500Us)
                + (device != null && device.SharedStats ? " · " + Lang.T("irq.d.shared") : "");
            coreDetails.Text = (driver == null ? "" : Lang.F("irq.driver.identity",driver.Driver,driver.DriverVersion) + "\n")
                + IrqCoreRecommendation.CoreDetails(session);
            var plan = IrqCorePlan.Build(session,history,device,CpuTopology.PhysicalCoreMasks(),CpuTopology.MultiGroup);
            candidateDetails.Text = plan.Details;
            options.Clear();
            options.AddRange(plan.Options);
            candidateHeading.Text = plan.Heading;
            for (int i = 0; i < optionButtons.Length; i++)
            {
                optionButtons[i].Visible = i < options.Count;
                if (i >= options.Count) continue;
                var option = options[i];
                optionButtons[i].Text = Lang.F("irq.plan.option",IrqRelocate.MaskText(option.Mask),
                    option.AveragePercent.ToString("F0"),option.EvidenceSessions);
                hints.SetToolTip(optionButtons[i],Lang.F("irq.plan.option.detail",IrqRelocate.MaskText(option.Mask),
                    option.EvidenceSessions,option.AveragePercent.ToString("F0"),option.BusyPercent.ToString("F0")));
            }
            candidateHint.Text = plan.Summary;
            riskLabel.Text = RiskText();
            deviceDetails.Text = device == null ? "" : device.Name + "\n"
                + Lang.F("irq.device.identity",EmptyAsUnknown(device.DriverVersion),
                    EmptyAsUnknown(device.ParentController),EmptyAsUnknown(device.Location)) + "\n" + device.Attribution
                + (device.FrameworkStats ? "\n" + Lang.F("irq.d.framework",device.StatsDriver) : "");
            string issue; var changes = IrqAdjustmentLedger.Load(out issue);
            string verification = issue;
            IrqAdjustment current = null;
            if (issue.Length == 0)
            {
                verification = IrqAdjustmentVerification.DescribeHistory(changes,device,allSessions,
                    IrqAffinityEngine.BootStamp(),CpuTopology.TopologyStamp(),out current);
                if (current != null && !IrqAdjustmentLedger.Save(current)) verification += "\n" + Lang.T("irq.adjust.savefailed");
            }
            string placement = current == null ? device == null ? null : device.AdjustmentPlacement : current.Placement;
            string state = issue.Length > 0 ? "unknown" : placement ?? IrqUiState.Placement(device);
            metricStatus.Text = IrqUiState.Text(state);
            metricStatus.ForeColor = IrqUiState.ColorFor(state);
            benefitLabel.Text = current == null || string.IsNullOrEmpty(current.Performance)
                ? Lang.T("irq.flow.benefit") : Lang.T("irq.performance." + current.Performance);
            if (verification.Length == 0) verification = Lang.T("irq.ui.noadjustment");
            if (session.Record != null && session.Record.Frames != null)
            {
                var f = session.Record.Frames;
                verification += "\n\n" + Lang.F("irq.frame.clue",f.P99Ms.ToString("F2"),f.P999Ms.ToString("F2"),
                    f.LongFrames,f.Intervals,(100 * Math.Min(1,f.Seconds / session.DurationSeconds)).ToString("F0"),
                    f.AlignmentModule,f.AlignmentHits)
                    + (verification.Contains(Lang.T("irq.compare.caution")) ? "" : "\n" + Lang.T("irq.compare.caution"))
                    + (f.AlignmentComplete ? "" : "\n" + Lang.T("irq.frame.incomplete"));
            }
            verificationLabel.Text = verification;
            OnPicked(Chosen); LayoutViewportAndFooter(0);
        }
        private static string EmptyAsUnknown(string value) { return string.IsNullOrEmpty(value) ? "—" : value; }
        private string RiskText()
        {
            var lines = new List<string>();
            if (device != null)
            {
                if (device.ManagedElsewhere) lines.Add(Lang.T("irq.d.owned"));
                if (device.InputRisk) lines.Add(Lang.T("irq.d.input"));
                if (device.MultiMessageRisk) lines.Add(device.MessageCount > 1
                    ? Lang.F("irq.d.multimsg",device.MessageCount) : Lang.T("irq.d.multimsg.unknown"));
                if (device.CompletionFollowsIssuer) lines.Add(Lang.T("irq.d.storagedpc"));
                if (device.SharedStats) lines.Add(Lang.T("irq.ui.shared"));
            }
            return string.Join("\n",lines.ToArray());
        }
        private void SetSelection(ulong mask) { matrix.Selected = mask; OnPicked(matrix.Selected); }
        internal void SetReadOnly(string reason)
        {
            readOnlyReason = reason;
            swPriority.Enabled = false;
            OnPicked(Chosen);
        }
        private void OnPicked(ulong mask)
        {
            Chosen = IrqRelocate.Sanitize(mask);
            btnOk.Enabled = readOnlyReason == null && Chosen != 0 && !CpuTopology.MultiGroup && (device == null || !device.ManagedElsewhere);
            lblPick.Text = CpuTopology.MultiGroup ? Lang.T("irq.exact.unsupported")
                : Lang.F("irq.flow.change", IrqUiState.LogicalCpus(device != null && device.IsPinned ? device.Mask : 0),
                    Chosen == 0 ? Lang.T("irq.ui.choose") : IrqUiState.LogicalCpus(Chosen));
            hints.SetToolTip(lblPick,lblPick.Text);
            lblPick.ForeColor = CpuTopology.MultiGroup ? Theme.Danger : Chosen == 0 ? Theme.Dim : Theme.Fg;
            footerHint.Text = readOnlyReason ?? (CurrentSession != null && (Chosen & CurrentSession.GameMask) != 0
                ? Lang.T("irq.ui.overlap") : Lang.T("irq.ui.apply.hint"));
            foreach (var button in optionButtons) button.Kind = BtnKind.Normal;
            for (int i = 0; i < Math.Min(options.Count,optionButtons.Length); i++)
                if (options[i].Mask == Chosen) optionButtons[i].Kind = BtnKind.Primary;
            foreach (var button in optionButtons) button.Invalidate();
        }

        private static int PlaceLabel(Label label, int x, int y, int width, int minimum)
        {
            int h = Math.Max(Theme.S(minimum),TextRenderer.MeasureText(label.Text,label.Font,
                new Size(Math.Max(1,width),int.MaxValue),TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height);
            label.SetBounds(x,y,Math.Max(1,width),h); return y + h;
        }
        private void LayoutViewportAndFooter(int keepScrollY)
        {
            if (layingOut || matrix == null || btnOk == null || ClientSize.Width == 0) return;
            layingOut = true;
            try
            {
                int margin = Theme.S(22), width = Math.Max(1,ClientSize.Width - margin * 2);
                titleLabel.SetBounds(margin,Theme.S(16),Math.Max(1,width - Theme.S(34)),Theme.S(30));
                deviceLabel.SetBounds(margin,Theme.S(49),width,Theme.S(23));
                closeLabel.SetBounds(ClientSize.Width - Theme.S(48),Theme.S(12),Theme.S(30),Theme.S(32));
                int tabWidth = Math.Max(1,(width - Theme.S(8)) / 2);
                tabCores.SetBounds(margin,Theme.S(81),tabWidth,Theme.S(32));
                tabEvidence.SetBounds(margin + tabWidth + Theme.S(8),Theme.S(81),tabWidth,Theme.S(32));
                historyPicker.SetBounds(margin,Theme.S(122),width,Theme.S(32));
                int footerH = Theme.S(112), footerTop = Math.Max(Theme.S(166),ClientSize.Height - footerH);
                scrollBody.SetBounds(0,Theme.S(166),ClientSize.Width,Math.Max(1,footerTop - Theme.S(166)));
                lblPick.SetBounds(margin,footerTop + Theme.S(8),width,Theme.S(45));
                int buttonW = Math.Min(Theme.S(130),width / 3), gap = Theme.S(8);
                btnOk.SetBounds(ClientSize.Width - margin - buttonW,footerTop + Theme.S(65),buttonW,Theme.S(35));
                btnCancel.SetBounds(btnOk.Left - gap - buttonW,btnOk.Top,buttonW,btnOk.Height);
                footerHint.SetBounds(margin,btnOk.Top,Math.Max(1,btnCancel.Left - margin - Theme.S(12)),Theme.S(40));
                scrollBody.SuspendLayout(); scrollBody.AutoScrollPosition = Point.Empty;
                int pageW = Math.Max(1,scrollBody.Width - SystemInformation.VerticalScrollBarWidth);
                int coreH = LayoutCorePage(pageW), evidenceH = LayoutEvidencePage(pageW);
                corePage.SetBounds(0,0,pageW,coreH); evidencePage.SetBounds(0,0,pageW,evidenceH);
                bodyContentHeight = SelectedPage == 0 ? coreH : evidenceH;
                scrollBody.AutoScrollMinSize = new Size(0,bodyContentHeight);
                scrollBody.ResumeLayout(true); scrollBody.PerformLayout();
                scrollBody.AutoScrollPosition = new Point(0,Math.Min(Math.Max(0,keepScrollY),
                    Math.Max(0,bodyContentHeight - scrollBody.ClientSize.Height)));
            }
            finally { layingOut = false; }
        }
        private int LayoutCorePage(int pageWidth)
        {
            int x = Theme.S(22), w = Math.Max(1,pageWidth - x * 2), y = Theme.S(10), pad = Theme.S(14);
            int column = Math.Max(1,(w - pad * 2) / 3), summaryH = 0;
            string[] titles = { "metricDpcTitle","metricCurrentTitle","metricStatusTitle" };
            Label[] metrics = { metricDpc,metricCurrent,metricStatus };
            for (int i = 0; i < 3; i++)
            {
                int left = pad + column * i;
                int titleBottom = PlaceLabel((Label)summaryCard.Controls[titles[i]],left,Theme.S(11),column - Theme.S(8),16);
                summaryH = Math.Max(summaryH,PlaceLabel(metrics[i],left,titleBottom + Theme.S(7),column - Theme.S(8),25));
            }
            summaryH = PlaceLabel(benefitLabel,pad,summaryH + Theme.S(8),w - pad * 2,18);
            summaryCard.SetBounds(x,y,w,summaryH + Theme.S(12)); y = summaryCard.Bottom + Theme.S(12);
            riskLabel.Visible = riskLabel.Text.Length > 0;
            if (riskLabel.Text.Length > 0) y = PlaceLabel(riskLabel,x,y,w,20) + Theme.S(10);
            y = PlaceLabel(candidateHeading,x,y,w,20) + Theme.S(5);
            int count = Math.Min(optionButtons.Length,options.Count);
            int optionW = count == 0 ? 1 : (w - Theme.S(8) * (count - 1)) / count;
            for (int i = 0; i < count; i++)
                optionButtons[i].SetBounds(x + i * (optionW + Theme.S(8)),y,optionW,Theme.S(36));
            if (count > 0) y += Theme.S(44);
            y = PlaceLabel(candidateHint,x,y,w,20) + Theme.S(16);
            int resetW = Theme.S(104), clearW = Theme.S(64);
            resetSelection.SetBounds(x + w - resetW - clearW - Theme.S(8),y,resetW,Theme.S(28));
            clearSelection.SetBounds(x + w - clearW,y,clearW,Theme.S(28));
            coreHeading.SetBounds(x,y,Math.Max(1,w - resetW - clearW - Theme.S(18)),Theme.S(28));
            y += Theme.S(35); y = PlaceLabel(howto,x,y,w,20) + Theme.S(8);
            int matrixH = matrix.LayoutFor(w); matrix.SetBounds(x,y,w,matrixH); y += matrixH + Theme.S(12);
            advancedToggle.SetBounds(x,y,w,Theme.S(31)); y += Theme.S(39);
            if (AdvancedExpanded)
            {
                swPriority.Location = new Point(pad,Theme.S(15));
                int end = PlaceLabel(priorityLabel,Theme.S(80),Theme.S(13),Math.Max(1,w - Theme.S(94)),35);
                advancedPanel.SetBounds(x,y,w,Math.Max(Theme.S(60),end + Theme.S(12)));
                y = advancedPanel.Bottom + Theme.S(10);
            }
            return y + Theme.S(10);
        }
        private static int FlowCard(Panel card, int x, int y, int width, params Label[] labels)
        {
            int pad = Theme.S(14), cy = Theme.S(12);
            cy = PlaceLabel((Label)card.Controls["cardTitle"],pad,cy,width - pad * 2,23) + Theme.S(9);
            foreach (var label in labels)
            {
                label.Visible = label.Text.Length > 0;
                if (label.Text.Length > 0) cy = PlaceLabel(label,pad,cy,width - pad * 2,20) + Theme.S(9);
            }
            card.SetBounds(x,y,width,cy + Theme.S(4)); return card.Bottom + Theme.S(12);
        }
        private int LayoutEvidencePage(int pageWidth)
        {
            int x = Theme.S(22), w = Math.Max(1,pageWidth - x * 2), y = Theme.S(10);
            y = FlowCard(observationCard,x,y,w,sourceLabel,coverageLabel,statLabel,extraLabel,coreDetails,candidateDetails);
            y = FlowCard(verificationCard,x,y,w,verificationLabel);
            y = FlowCard(deviceCard,x,y,w,deviceDetails); return y + Theme.S(8);
        }
        internal void FitToWorkingArea(Rectangle workArea)
        {
            if (workArea.Width <= 0 || workArea.Height <= 0) return;
            int scroll = fittedToWorkArea ? Math.Max(0,-scrollBody.AutoScrollPosition.Y) : 0;
            if (!fittedToWorkArea) { try { ActiveControl = btnCancel; btnCancel.Select(); } catch { } }
            ClientSize = new Size(Math.Min(Theme.S(DlgW),workArea.Width),Math.Min(Theme.S(DlgH),workArea.Height));
            LayoutViewportAndFooter(scroll);
            int left = Math.Max(workArea.Left,Math.Min(Left,workArea.Right - Width));
            int top = Math.Max(workArea.Top,Math.Min(Top,workArea.Bottom - Height));
            Location = new Point(left,top); fittedToWorkArea = true;
        }
        protected override void OnHandleCreated(EventArgs e)
        { base.OnHandleCreated(e); Native.RoundCorners(Handle); }
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            try { ActiveControl = btnCancel; btnCancel.Select(); } catch { }
            if (StartPosition != FormStartPosition.Manual) ClampToWorkArea();
            clockWasSuspended = UiClock.Borrow(); Fx.EnterForm(this);
        }
        private void ClampToWorkArea()
        { try { FitToWorkingArea(Screen.FromControl(this).WorkingArea); } catch { } }
        protected override void OnFormClosed(FormClosedEventArgs e)
        { historyMenu.Dispose(); hints.Dispose(); UiClock.Return(clockWasSuspended); base.OnFormClosed(e); }
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { Close(); return true; }
            if (keyData == (Keys.Control | Keys.Tab)) { SelectPage(1 - SelectedPage); return true; }
            return base.ProcessCmdKey(ref msg,keyData);
        }
        private void DragMove(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            Native.ReleaseCapture(); Native.SendMessage(Handle,Native.WM_NCLBUTTONDOWN,(IntPtr)Native.HT_CAPTION,IntPtr.Zero);
            if (StartPosition != FormStartPosition.Manual) ClampToWorkArea();
        }
    }
}
