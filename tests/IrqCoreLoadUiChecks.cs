#if PAVISE_UI_TEST && PAVISE_SELFTEST
using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace PaviseApp
{
    internal static class IrqCoreLoadUiChecks
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private static int checks;
        private static void Check(bool good, string message)
        { if (!good) throw new Exception(message); checks++; }
        private static void CreateHiddenTree(Control control)
        {
            typeof(Control).GetMethod("CreateControl", Private, null, new[] { typeof(bool) }, null)
                .Invoke(control, new object[] { true });
            foreach (Control child in control.Controls) CreateHiddenTree(child);
        }

        private static T DialogField<T>(IrqPinDialog dialog, string name) where T : class
        { return (T)typeof(IrqPinDialog).GetField(name, Private).GetValue(dialog); }

        private static void Click(Control control)
        { typeof(Control).GetMethod("OnClick", Private).Invoke(control, new object[] { EventArgs.Empty }); }

        private static Control Named(Control parent, string name)
        {
            Control[] matches = parent.Controls.Find(name, true);
            Check(matches.Length == 1, "Missing or duplicate UI content: " + name);
            return matches[0];
        }

        private static void CheckContent(Control parent, Control skip, string context)
        {
            foreach (Control child in parent.Controls)
            {
                if (child == skip) continue;
                Check(child.Left >= 0 && child.Right <= parent.ClientSize.Width,
                    context + " content clipped horizontally: " + child.Name);
                var label = child as Label;
                if (label != null && !label.AutoEllipsis && !string.IsNullOrEmpty(label.Text))
                {
                    int needed = TextRenderer.MeasureText(label.Text, label.Font,
                        new Size(label.Width, int.MaxValue), TextFormatFlags.WordBreak).Height;
                    Check(needed <= label.Height, context + " label clipped: " + label.Name + " " + label.Text);
                }
                if (child is Panel) CheckContent(child, skip, context);
            }
        }

        private static void CheckDialogLayout(IrqPinDialog dialog, string context)
        {
            var body = DialogField<Panel>(dialog, "scrollBody");
            var page = DialogField<Panel>(dialog, dialog.SelectedPage == 0 ? "corePage" : "evidencePage");
            var advanced = DialogField<Panel>(dialog, "advancedPanel");
            var pick = DialogField<Label>(dialog, "lblPick");
            var ok = DialogField<Control>(dialog, "btnOk");
            var cancel = DialogField<Control>(dialog, "btnCancel");
            Check(!body.HorizontalScroll.Visible, context + " requires horizontal scrolling");
            Check(page.Left >= 0 && page.Right <= body.ClientSize.Width, context + " page clipped horizontally");
            CheckContent(page, dialog.AdvancedExpanded ? null : advanced, context);
            foreach (Control control in dialog.Controls)
                if (control is PillButton || control == pick)
                    Check(dialog.ClientRectangle.Contains(control.Bounds), context + " fixed action or reason clipped");
            Check(body.Bottom <= pick.Top && pick.Bottom <= ok.Top,
                context + " selection reason is not fixed above the actions");
            Check(cancel.Right <= ok.Left, context + " footer actions overlap");
            Check(!string.IsNullOrEmpty(pick.Text), context + " lacks an apply state explanation");
            if (dialog.SelectedPage == 0)
                Check(DialogField<Label>(dialog,"candidateHint").Bottom <= DialogField<Label>(dialog,"coreHeading").Top,
                    context + " candidate guidance must precede the manual CPU map");
        }

        private static void SaveDialog(IrqPinDialog dialog, string output, string name)
        {
            using (var bmp = new Bitmap(dialog.Width, dialog.Height))
            { dialog.DrawToBitmap(bmp, dialog.ClientRectangle); bmp.Save(Path.Combine(output, name + ".png"), ImageFormat.Png); }
        }

        internal static int Run(string output)
        {
            checks = 0;
            var topology = CpuTopology.CaptureTopologyForTest();
            ulong strict = CpuTopology.StrictBoostMask;
            bool groupsBefore = CpuTopology.MultiGroup;
            float scaleBefore = Dpi.Scale; int langBefore = Lang.Cur; bool lightBefore = Theme.LightMode;
            try
            {
                CheckProbeWarning(output);
                CheckDevicePage(output);
                CheckHistorySwitch(output);
                CheckWorkflowThemes(output);
                Check(typeof(CoreMatrix).GetMethod("GlowText", Private | BindingFlags.Static) == null,
                    "Percentage text must not be repainted as offset glow copies");
                Check(typeof(IrqPinDialog).GetMethod("StartLoadProbe", Private) == null,
                    "Opening the dialog must not sample desktop utilization");
                foreach (int cpuCount in new[] { 12, 64 })
                foreach (float scale in new[] { 1f, 1.25f, 1.5f, 2f, 3f })
                foreach (int language in new[] { 0, 1 })
                foreach (bool light in new[] { false, true })
                foreach (bool missing in new[] { false, true })
                {
                    Dpi.Scale = scale; Theme.DropFontCache(); Lang.Cur = language; Theme.SetLight(light);
                    ulong all = cpuCount == 64 ? ulong.MaxValue : (1UL << cpuCount) - 1;
                    var cores = new ulong[cpuCount / 2];
                    for (int i = 0; i < cores.Length; i++) cores[i] = 3UL << (i * 2);
                    CpuTopology.InjectTopologyForTest(all, cores, null, all, 0, 0, 0, false, false);
                    CpuTopology.StrictBoostMask = all; // Must not leak into historical labels
                    var view = new IrqPinSession { Available = true, GameName = "台架对局 / Synthetic match",
                        StartUtcTicks = new DateTime(2026, 8, 27, 12, 0, 0, DateTimeKind.Utc).Ticks,
                        DurationSeconds = 300, GameMask = missing ? 0UL : 0xFCUL, SeenMask = 3,
                        MinimumCoverage = 98, MaximumCoverage = 100,
                        Driver = new IrqDriverRecord { Driver = "fixture.sys", Dpc = 1000, DpcMaxNs = 967000, Over500Us = 1 } };
                    int[] values = { 56, 38, 81, 31, 50, 31, 50, 25, 63, 38, 0, 100 };
                    if (!missing) for (int cpu = 0; cpu < cpuCount; cpu++) view.Loads[cpu] = values[cpu % values.Length];
                    var device = new IrqDevice { InstanceId = "IRQ_LOAD_UI_FIXTURE", Name = "NVIDIA GeForce RTX 2070 with Max-Q Design",
                        Service = "fixture", DevicePriorityHigh = true, Dpc = 99999, MaxUs = 99999, SeenOnCpus = all };
                    using (var dialog = new IrqPinDialog(device, view))
                    {
                        dialog.StartPosition = FormStartPosition.Manual; dialog.Location = new Point(-20000, -20000);
                        CreateHiddenTree(dialog);
                        dialog.FitToWorkingArea(new Rectangle(-20000, -20000, Math.Min(1920, Theme.S(620)), Math.Min(1080, Theme.S(700))));
                        var matrix = DialogField<CoreMatrix>(dialog, "matrix");
                        var body = DialogField<Panel>(dialog, "scrollBody");
                        Check(matrix.SeenMask == 3 && matrix.ObservedGameMask == view.GameMask, "Mixed-session core markers");
                        var loads = (Dictionary<int, double>)typeof(CoreMatrix).GetField("loads", Private).GetValue(matrix);
                        Check(loads.Count == (missing ? 0 : cpuCount), "Unexpected displayed load count");
                        Check(dialog.SelectedPage == 0 && !dialog.AdvancedExpanded,
                            "Dialog must start with core selection and collapsed advanced settings");
                        Check(DialogField<Panel>(dialog, "evidencePage").Contains(Named(body, "irqVerification")),
                            "Detailed verification is exposed in the default core task");
                        Check(!DialogField<Control>(dialog, "btnOk").Enabled,
                            "A new unselected device can apply before choosing any core");
                        string context = "Core dialog: scale=" + scale + " lang=" + language + " cpus=" + cpuCount;
                        CheckDialogLayout(dialog, context);
                        Click(DialogField<Control>(dialog, "advancedToggle"));
                        Check(dialog.AdvancedExpanded && dialog.RaisePriority,
                            "Advanced priority is inaccessible or loses the existing High state");
                        CheckDialogLayout(dialog, context + " expanded");
                        var expandedPanel = DialogField<Panel>(dialog,"advancedPanel");
                        Point expandedTop = body.PointToClient(expandedPanel.PointToScreen(Point.Empty));
                        Check(expandedPanel.Height > body.ClientSize.Height || (expandedTop.Y >= 0
                            && expandedTop.Y + expandedPanel.Height <= body.ClientSize.Height),
                            "Expanded advanced controls remain outside the scroll viewport");
                        Click(DialogField<Control>(dialog, "advancedToggle"));
                        Check(!dialog.AdvancedExpanded && dialog.RaisePriority, "Collapsing advanced settings changed priority");
                        matrix.Selected = 0x300;
                        typeof(IrqPinDialog).GetMethod("OnPicked", Private).Invoke(dialog, new object[] { 0x300UL });
                        Check(dialog.Chosen == 0x300 && DialogField<Control>(dialog, "btnOk").Enabled,
                            "Manual core selection did not enable applying");
                        var evidenceKey = new KeyEventArgs(Keys.Enter);
                        typeof(Control).GetMethod("OnKeyDown",Private).Invoke(DialogField<Control>(dialog,"tabEvidence"),
                            new object[] { evidenceKey });
                        Check(dialog.SelectedPage == 1 && dialog.Chosen == 0x300 && matrix.Selected == 0x300,
                            "Keyboard activation did not open evidence or changed the manual target");
                        Check(evidenceKey.SuppressKeyPress,"Tab keyboard activation leaks its Enter keystroke");
                        CheckDialogLayout(dialog, context + " evidence");
                        Click(DialogField<Control>(dialog, "tabCores"));
                        Check(dialog.SelectedPage == 0 && dialog.Chosen == 0x300 && matrix.Selected == 0x300,
                            "Returning to core selection changed the manual target");
                        CheckDialogLayout(dialog, context + " returned");
                        if (cpuCount == 12 && scale == 1.25f && language == 0)
                        {
                            string name = "irq-" + (light ? "light" : "dark") + (missing ? "-missing" : "-session");
                            SaveDialog(dialog, output, name);
                            Click(DialogField<Control>(dialog, "tabEvidence"));
                            SaveDialog(dialog, output, name + "-details");
                            Click(DialogField<Control>(dialog, "tabCores"));
                        }
                        if (!missing) { view.Loads[0] = 1; Check(loads[0] == 56, "Matrix retained mutable source dictionary"); }
                        // A missing sibling logical core must not mark the whole physical core as low load
                        matrix.ObservedGameMask = 0;
                        matrix.SetLoads(new Dictionary<int, double> { {0, 0}, {2, double.NaN}, {3, double.PositiveInfinity}, {64, 10} });
                        loads = (Dictionary<int, double>)typeof(CoreMatrix).GetField("loads", Private).GetValue(matrix);
                        Check(loads.Count == 1 && loads[0] == 0, "Invalid/missing values turned into loads");
                        var bands = (IList)typeof(CoreMatrix).GetField("bands", Private).GetValue(matrix);
                        var groupList = (IList)bands[0].GetType().GetField("Groups").GetValue(bands[0]);
                        object kind = typeof(CoreMatrix).GetMethod("KindOf", Private).Invoke(matrix, new object[] { groupList[0] });
                        Check(kind.ToString() == "Perf", "Missing sibling or current preset produced a false core label");
                    }
                }
            }
            finally
            {
                CpuTopology.RestoreTopologyForTest(topology); CpuTopology.StrictBoostMask = strict;
                CpuTopology.MultiGroup = groupsBefore;
                Dpi.Scale = scaleBefore; Lang.Cur = langBefore; Theme.SetLight(lightBefore); Theme.DropFontCache();
            }
            return checks;
        }

        private static void CheckProbeWarning(string output)
        {
            bool suppressed = PanelForm.SuppressUiWorkersForTest;
            PanelForm.SuppressUiWorkersForTest = true;
            try
            {
                foreach (int language in new[] { 0, 1 })
                {
                    Settings.UseTransientStoreForCurrentProcess();
                    Lang.Cur = language; Dpi.Scale = 1f; Theme.DropFontCache(); Theme.SetLight(false);
                    string data = Path.Combine(output, "irq-warning-" + language);
                    Directory.CreateDirectory(data);
                    var core = new SuppressionCore();
                    var tamer = new Tamer(core);
                    var mode = new GameMode(data, core); // Never start workers or real capture
                    using (var icon = IconArt.MakeIcon(24))
                    using (var form = new PanelForm(tamer, mode, icon, true))
                    {
                        form.ShowInTaskbar = false; form.StartPosition = FormStartPosition.Manual;
                        form.Location = new Point(-20000, -20000); form.Show();
                        var toggle = (Toggle)typeof(PanelForm).GetField("swIrqProbePage", Private).GetValue(form);
                        var apply = (Control)typeof(PanelForm).GetField("btnIrqApply",Private).GetValue(form);
                        var selected = (Control)typeof(PanelForm).GetField("btnIrqRestoreSelected",Private).GetValue(form);
                        var restoreAll = (Control)typeof(PanelForm).GetField("btnIrqRevert",Private).GetValue(form);
                        Check(apply.Parent == selected.Parent && apply.Right <= selected.Left,
                            "Selected-device actions overlap or lost their shared context");
                        foreach (Control action in new[] { apply,selected,restoreAll })
                            Check(action.Parent.ClientRectangle.Contains(action.Bounds),"IRQ action clipped in its container");
                        Check(restoreAll.Parent != apply.Parent && apply.Parent.Bottom <= restoreAll.Top,
                            "Restore-all is not separated from the selected-device actions");
                        Check(!toggle.Checked && !IrqSessionProbe.EnabledSetting, "Observation must default off");
                        CheckProbeToggle(form, mode, toggle, true, "cancel", data);
                        CheckProbeToggle(form, mode, toggle, true, "confirm", data);
                        CheckProbeToggle(form, mode, toggle, false, "none", data);
                        CheckProbeToggle(form, mode, toggle, true, "confirm", data);
                        CheckProbeToggle(form, mode, toggle, false, "none", data);
                        CheckProbeToggle(form, mode, toggle, true, "escape", data);
                        CheckProbeToggle(form, mode, toggle, true, "close", data);
                        Check(!form.UiActive && typeof(GameMode).GetField("worker", Private).GetValue(mode) == null
                            && typeof(Tamer).GetField("worker", Private).GetValue(tamer) == null,
                            "Warning checks must not start runtime workers");
                        form.Hide();
                    }
                }
            }
            finally
            {
                IrqMutationBoundary.Configure(null, null);
                PanelForm.SuppressUiWorkersForTest = suppressed;
            }
        }

        private static void CheckHistorySwitch(string output)
        {
            foreach (int cpus in new[] { 12,64 })
            foreach (float scale in new[] { 1f,1.25f,1.5f,2f,3f })
            foreach (int language in new[] { 0,1 })
            foreach (bool light in new[] { false,true })
            {
                CpuTopology.MultiGroup = false;
                Dpi.Scale = scale; Lang.Cur = language; Theme.SetLight(light); Theme.DropFontCache();
                ulong all = cpus == 64 ? ulong.MaxValue : (1UL << cpus) - 1;
                var physical = new ulong[cpus / 2];
                for (int i = 0; i < physical.Length; i++) physical[i] = 3UL << (i * 2);
                CpuTopology.InjectTopologyForTest(all,physical,null,all,0,0,0,false,false);
                string work = Path.Combine(output,"history-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(work); IrqSessionLedger.Bind(work);
                long now = DateTime.UtcNow.Ticks; string boot = IrqAffinityEngine.BootStamp();
                var old = SelfTests.EnhancedIrqRecord(now - 600 * TimeSpan.TicksPerSecond,boot,true);
                old.GameName = "History A"; old.GameMask = 12; old.CoreLoads[0].AveragePercent = 7;
                old.Drivers[0].Cores[0].Cpu = 1; old.Drivers[0].CpuMask = 6;
                var latest = SelfTests.EnhancedIrqRecord(now - 200 * TimeSpan.TicksPerSecond,boot,false);
                latest.GameName = "History B";
                Check(IrqSessionLedger.Append(old) && IrqSessionLedger.Append(latest),"Cannot seed history UI");
                var records = new List<IrqSessionRecord> { old,latest };
                var device = new IrqDevice { InstanceId = "fixture-device",Service = "fixture",Name = "Fixture GPU",
                    DriverVersion = "package-v1",ParentController = "PCI\\CONTROLLER",Location = "PCI bus 1, device 0",
                    ConfigurationKnown = true,Policy = 4,Mask = 4 };
                var history = IrqPinSession.History(records,device,boot,CpuTopology.TopologyStamp(),delegate { return "v1"; });
                var adjustment = IrqAdjustmentLedger.Prepare(device,history[1],4,false,records);
                adjustment.Phase = "written"; Check(IrqAdjustmentLedger.Save(adjustment),"Cannot seed adjustment UI");
                var noOp = new IrqAdjustment { Device = device.InstanceId, Target = 4,
                    WrittenUtc = adjustment.WrittenUtc + 1, Phase = "unchanged", NoDeviceWrite = true };
                Check(IrqAdjustmentLedger.Save(noOp),"Cannot seed no-op UI history");
                using (var dialog = new IrqPinDialog(device,history[1],history,records))
                {
                    dialog.StartPosition = FormStartPosition.Manual; dialog.Location = new Point(-20000,-20000);
                    CreateHiddenTree(dialog);
                    dialog.FitToWorkingArea(new Rectangle(-20000,-20000,Math.Min(1920,Theme.S(700)),Math.Min(1080,Theme.S(720))));
                    var picker = DialogField<ContextMenuStrip>(dialog, "historyMenu");
                    var matrix = DialogField<CoreMatrix>(dialog, "matrix");
                    var body = DialogField<Panel>(dialog, "scrollBody");
                    var verification = Named(body, "irqVerification");
                    Check(verification.Text.Contains(Lang.F("irq.adjust.latest",Lang.T("irq.phase.unchanged")))
                        && verification.Text.Contains(Lang.T("irq.adjust.current"))
                        && verification.Text.Contains(Lang.T("irq.phase.written")),"No-op hid the current adjustment in dialog");
                    Check(picker.Items.Count == 2 && dialog.SelectedHistoryIndex == 1,"Wrong default history");
                    Check(dialog.SelectedPage == 0 && !dialog.AdvancedExpanded,"History opened outside the core task");
                    CheckDialogLayout(dialog,"Default history");
                    matrix.Selected = 0x300;
                    typeof(IrqPinDialog).GetMethod("OnPicked",Private).Invoke(dialog,new object[] { 0x300UL });
                    Click(DialogField<Control>(dialog,"tabEvidence"));
                    Check(dialog.SelectedPage == 1,"History evidence tab did not open");
                    picker.Items[0].PerformClick();
                    var loads = (Dictionary<int,double>)typeof(CoreMatrix).GetField("loads",Private).GetValue(matrix);
                    Check(matrix.ObservedGameMask == 12 && matrix.SeenMask == 6 && loads[0] == 7,"History selection mixed observations");
                    Check(dialog.Chosen == 0x300 && matrix.Selected == 0x300 && dialog.SelectedPage == 1,
                        "Choosing historical evidence changed the manual target or page");
                    picker.Items[1].PerformClick();
                    loads = (Dictionary<int,double>)typeof(CoreMatrix).GetField("loads",Private).GetValue(matrix);
                    Check(matrix.ObservedGameMask == 3 && matrix.SeenMask == 5 && loads[0] == 20,"Second history did not refresh");
                    Check(dialog.Chosen == 0x300 && matrix.Selected == 0x300,"History refresh discarded the chosen target");
                    CheckDialogLayout(dialog,"Historical evidence");
                    Click(DialogField<Control>(dialog,"tabCores"));
                    Check(dialog.SelectedPage == 0 && dialog.Chosen == 0x300,"Returning from history discarded the chosen target");
                    var options = DialogField<PillButton[]>(dialog,"optionButtons");
                    Check(options.Length == 3 && options[0].Enabled,"Qualified physical core candidate is not selectable");
                    int mutations = 0;
                    IrqMutationBoundary.Configure(delegate { mutations++; },null);
                    try
                    {
                        Click(options[0]);
                        Check(dialog.Chosen == 12 && matrix.Selected == 12,"Candidate did not select the whole physical core");
                        Check(mutations == 0 && dialog.DialogResult == DialogResult.None && !dialog.IsDisposed
                            && device.Mask == 4 && device.Policy == 4,"Candidate click applied device settings without confirmation");
                    }
                    finally { IrqMutationBoundary.Configure(null,null); }
                    CheckDialogLayout(dialog,"Candidate selected");
                    if (cpus == 12 && scale == 1.25f)
                    {
                        string prefix = "history-" + language + (light ? "-light" : "-dark");
                        SaveDialog(dialog,output,prefix);
                        Click(DialogField<Control>(dialog,"tabEvidence"));
                        SaveDialog(dialog,output,prefix + "-details");
                        body.AutoScrollPosition = new Point(0,body.DisplayRectangle.Height);
                        SaveDialog(dialog,output,prefix + "-verification");
                        Click(DialogField<Control>(dialog,"tabCores"));
                        var matched = new List<IrqSessionRecord> {
                            SelfTests.EnhancedIrqRecord(now - 500 * TimeSpan.TicksPerSecond,boot,true),
                            SelfTests.EnhancedIrqRecord(now - 350 * TimeSpan.TicksPerSecond,boot,true),
                            SelfTests.EnhancedIrqRecord(now - 200 * TimeSpan.TicksPerSecond,boot,true) };
                        matched[0].CoreLoads[2].AveragePercent = 45;
                        var matchedHistory = IrqPinSession.History(matched,device,boot,CpuTopology.TopologyStamp(),delegate { return "v1"; });
                        using (var multi = new IrqPinDialog(device,matchedHistory[2],matchedHistory,matched))
                        {
                            multi.StartPosition = FormStartPosition.Manual; multi.Location = new Point(-20000,-20000);
                            CreateHiddenTree(multi);
                            multi.FitToWorkingArea(new Rectangle(-20000,-20000,Theme.S(700),Math.Min(1080,Theme.S(720))));
                            Check(DialogField<Label>(multi,"candidateHeading").Text == Lang.T("irq.plan.stable"),
                                "Matching sessions did not produce a visible multi-session assessment");
                            CheckDialogLayout(multi,"Multi-session candidate plan");
                            SaveDialog(multi,output,"plan-" + language + (light ? "-light" : "-dark"));
                            multi.SelectPage(1); CheckDialogLayout(multi,"Multi-session plan details");
                            SaveDialog(multi,output,"plan-" + language + (light ? "-light" : "-dark") + "-details");
                        }
                    }
                    CpuTopology.MultiGroup = true;
                    typeof(IrqPinDialog).GetMethod("OnPicked",Private).Invoke(dialog,new object[] { 4UL });
                    Check(!((Control)typeof(IrqPinDialog).GetField("btnOk",Private).GetValue(dialog)).Enabled,"Multi-group pinning enabled");
                    Check(DialogField<Label>(dialog,"lblPick").Text == Lang.T("irq.exact.unsupported"),
                        "Unsupported processor groups lack an apply explanation");
                    CheckDialogLayout(dialog,"Unsupported processor groups");
                }
                CpuTopology.MultiGroup = false;
                IrqSessionLedger.Bind(output);
            }
        }

        private static void CheckDevicePage(string output)
        {
            bool suppressed = PanelForm.SuppressUiWorkersForTest;
            PanelForm.SuppressUiWorkersForTest = true;
            try
            {
                foreach (float scale in new[] { 1f,1.25f,1.5f,2f,3f })
                foreach (int language in new[] { 0,1 })
                foreach (bool light in new[] { false,true })
                {
                    Settings.UseTransientStoreForCurrentProcess();
                    Dpi.Scale = scale; Lang.Cur = language; Theme.SetLight(light); Theme.DropFontCache();
                    CpuTopology.MultiGroup = false;
                    string work = Path.Combine(output,"page-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(work);
                    var core = new SuppressionCore();
                    var tamer = new Tamer(core);
                    var mode = new GameMode(work,core);
                    using (var icon = IconArt.MakeIcon(24))
                    using (var form = new PanelForm(tamer,mode,icon,true))
                    {
                        form.ShowInTaskbar = false; form.StartPosition = FormStartPosition.Manual;
                        form.Location = new Point(-20000,-20000);
                        CreateHiddenTree(form); form.ShowPageForShot(PageId.Interrupt);
                        var network = new IrqDevice { Name = "Intel Ethernet I225-V",Service = "e2fexpress",
                            InstanceId = "PCI\\UI-NETWORK",Bus = "PCI",Dpc = 22,MaxUs = 110,
                            ClassGuid = "{4d36e972-e325-11ce-bfc1-08002be10318}" };
                        var graphics = new IrqDevice { Name = "NVIDIA GeForce RTX 2070 with Max-Q Design",
                            Service = "nvlddmkm",InstanceId = "PCI\\UI-GRAPHICS",Bus = "PCI",Dpc = 320,MaxUs = 760,
                            Policy = 4,Mask = 12,Verdict = new IrqDriverVerdict { Worth = true,VersionVerified = true },
                            ClassGuid = "{4d36e968-e325-11ce-bfc1-08002be10318}" };
                        var audioDevice = new IrqDevice { Name = "Realtek High Definition Audio",Service = "RTKVHD64",
                            InstanceId = "HDAUDIO\\UI-AUDIO",Bus = "HDAUDIO",ManagedElsewhere = true,
                            ClassGuid = "{4d36e96c-e325-11ce-bfc1-08002be10318}" };
                        var devices = new List<IrqDevice> { network,graphics,audioDevice };
                        typeof(PanelForm).GetField("irqDevices",Private).SetValue(form,devices);
                        typeof(PanelForm).GetField("irqSelectionId",Private).SetValue(form,null);
                        var search = (TextBox)typeof(PanelForm).GetField("txtIrqSearch",Private).GetValue(form);
                        var list = (ListBox)typeof(PanelForm).GetField("lstIrqDevices",Private).GetValue(form);
                        var apply = (Control)typeof(PanelForm).GetField("btnIrqApply",Private).GetValue(form);
                        var restore = (Control)typeof(PanelForm).GetField("btnIrqRestoreSelected",Private).GetValue(form);
                        var selected = (Control)typeof(PanelForm).GetField("lblIrqSelected",Private).GetValue(form);
                        var empty = (Control)typeof(PanelForm).GetField("lblIrqEmpty",Private).GetValue(form);
                        var count = (Control)typeof(PanelForm).GetField("lblIrqDeviceCount",Private).GetValue(form);
                        var clear = (Control)typeof(PanelForm).GetField("btnIrqClearSearch",Private).GetValue(form);
                        var selectedMethod = typeof(PanelForm).GetMethod("SelectedIrqDevice",Private);
                        typeof(PanelForm).GetMethod("ApplyIrqDeviceFilter",Private).Invoke(form,null);
                        Check(list.Items.Count == 3 && list.SelectedIndex == -1 && !apply.Enabled && !restore.Enabled,
                            "Device page chooses an action target without user selection");
                        Check(PanelForm.IrqDeviceMatches(graphics,"GEFORCE nvlddmkm")
                            && PanelForm.IrqDeviceMatches(graphics,"ui-graphics")
                            && PanelForm.IrqDeviceMatches(network,Lang.T("irq.page.kind.network"))
                            && !PanelForm.IrqDeviceMatches(graphics,"missing-device"),
                            "Device filtering lost names, drivers, identifiers or localized categories");
                        Check(graphics.IsPinned && graphics.ActionableWorth
                            && PanelForm.IrqDeviceDetail(graphics).Contains(Lang.T("irq.tag.matchsuggest").Trim('[',']')),
                            "A configured device hides its independent optimization suggestion");
                        int mutations = 0;
                        IrqMutationBoundary.Configure(delegate { mutations++; },null);
                        try
                        {
                            list.SelectedIndex = 1;
                            Check(selected.Text.Contains(graphics.Name),"Selected-device actions do not identify their target");
                            search.Text = "nvlddmkm";
                            Check(list.Items.Count == 1 && list.SelectedIndex == 0
                                && ReferenceEquals(selectedMethod.Invoke(form,null),graphics),
                                "Filtering changed the selected device when its row index moved");
                            Check(count.Text == Lang.F("irq.page.filtered",1,3) && clear.Enabled,
                                "Filtered count or clear-search action is missing");
                            search.Text = "Intel";
                            Check(list.Items.Count == 1 && list.SelectedIndex == -1
                                && selectedMethod.Invoke(form,null) == null && !apply.Enabled && !restore.Enabled,
                                "A hidden selected device still has active actions or changed target implicitly");
                            search.Text = "missing-device";
                            Check(list.Items.Count == 0 && empty.Text == Lang.T("irq.page.nomatches")
                                && !apply.Enabled && !restore.Enabled,"Empty search lacks a recovery state");
                            Click(clear);
                            Check(search.Text.Length == 0 && list.Items.Count == 3 && list.SelectedIndex == 1
                                && ReferenceEquals(selectedMethod.Invoke(form,null),graphics) && !clear.Enabled,
                                "Clear search did not recover the original device by identity");
                            search.Text = "ui-graphics";
                            var escape = new KeyEventArgs(Keys.Escape);
                            typeof(Control).GetMethod("OnKeyDown",Private).Invoke(search,new object[] { escape });
                            Check(search.Text.Length == 0 && escape.SuppressKeyPress
                                && ReferenceEquals(selectedMethod.Invoke(form,null),graphics),
                                "Escape did not clear search while retaining the device target");
                            list.SelectedIndex = 2;
                            Check(apply.Enabled && !restore.Enabled
                                && apply.Text == Lang.T("irq.flow.action.review"),"A managed device must offer viewing only");
                            list.SelectedIndex = 1;
                            Check(mutations == 0 && !form.UiActive
                                && typeof(GameMode).GetField("worker",Private).GetValue(mode) == null
                                && typeof(Tamer).GetField("worker",Private).GetValue(tamer) == null,
                                "Device search or selection started workers or changed device configuration");
                        }
                        finally { IrqMutationBoundary.Configure(null,null); }
                        var page = (Control)typeof(PanelForm).GetField("pageIrq",Private).GetValue(form);
                        CheckContent(page,null,"Device page scale=" + scale + " lang=" + language);
                        Check(apply.Parent == restore.Parent && apply.Parent.ClientRectangle.Contains(apply.Bounds)
                            && restore.Parent.ClientRectangle.Contains(restore.Bounds) && apply.Right <= restore.Left,
                            "Selected-device actions overlap or leave their card");
                        if (scale == 1.25f)
                            using (var bmp = new Bitmap(form.Width,form.Height))
                            {
                                form.DrawToBitmap(bmp,form.ClientRectangle);
                                bmp.Save(Path.Combine(output,"devices-" + language + (light ? "-light" : "-dark") + ".png"),ImageFormat.Png);
                            }
                    }
                }
            }
            finally
            {
                IrqMutationBoundary.Configure(null,null);
                PanelForm.SuppressUiWorkersForTest = suppressed;
            }
        }

        private static void CheckWorkflowThemes(string output)
        {
            bool suppressed = PanelForm.SuppressUiWorkersForTest;
            bool oldOverride = Theme.HasModeColorOverride(PerformancePreset.Standard);
            Color oldColor = Theme.ModeColor(PerformancePreset.Standard);
            PerformancePreset oldMode = Theme.CurrentMode;
            string oldLog = Logger.LogPath;
            PanelForm.SuppressUiWorkersForTest = true;
            try
            {
                foreach (int language in new[] { 0,1 })
                foreach (bool light in new[] { false,true })
                {
                    Settings.UseTransientStoreForCurrentProcess();
                    Dpi.Scale = 1.25f; Lang.Cur = language; Theme.SetLight(light); Theme.DropFontCache();
                    Color accent = light ? Color.FromArgb(24,118,190) : Color.FromArgb(169,119,235);
                    Theme.SetModeColorOverride(PerformancePreset.Standard,accent);
                    Theme.SetMode(PerformancePreset.Standard,false);
                    string work = Path.Combine(output,"workflow-" + language + "-" + light);
                    Directory.CreateDirectory(work);
                    Logger.LogPath = Path.Combine(work,"sample.log");
                    File.WriteAllText(Logger.LogPath,"2026-09-14 01:02:03  PASS UAGame priority applied\r\n"
                        + "2026-09-14 01:02:04  WARN UAGame placement unconfirmed\r\n");
                    var core = new SuppressionCore(); var mode = new GameMode(work,core); var tamer = new Tamer(core);
                    using (var icon = IconArt.MakeIcon(24))
                    using (var form = new PanelForm(tamer,mode,icon,true))
                    {
                        form.ShowInTaskbar = false; form.StartPosition = FormStartPosition.Manual;
                        form.Location = new Point(-20000,-20000); CreateHiddenTree(form);
                        string copied = null; form.CopyDiagnosticForTest = delegate(string text) { copied = text; };
                        typeof(PanelForm).GetMethod("CopyDiagnosticSummary",Private).Invoke(form,null);
                        Check(copied != null && copied.Contains(App.Version) && copied.Contains("Global policy:")
                            && copied.Contains("Saved global core plan") && copied.Contains("UAGame placement unconfirmed"),
                            "One-click diagnostics lost version, settings or issue context");
                        foreach (PageId page in new[] { PageId.Overview,PageId.Library,PageId.Log })
                        {
                            form.ShowPageForShot(page);
                            Check(Theme.Accent == accent,"Opening a page reset the user-selected theme color");
                            if (page == PageId.Library)
                            {
                                var more = (Control)typeof(PanelForm).GetField("btnGameMore",Private).GetValue(form);
                                var settings = (Control)typeof(PanelForm).GetField("btnGameConfig",Private).GetValue(form);
                                Check(!more.Enabled && !settings.Enabled,"Empty library exposes selected-game actions");
                                Check(more.Top >= settings.Bottom,"Library settings and more actions overlap");
                            }
                            if (page == PageId.Overview)
                                Check(!string.IsNullOrWhiteSpace(((Label)typeof(PanelForm).GetField("lblStatus",Private).GetValue(form)).Text),
                                    "The current game must be populated before the first refresh tick");
                            using (var bitmap = new Bitmap(form.Width,form.Height))
                            { form.DrawToBitmap(bitmap,form.ClientRectangle); bitmap.Save(Path.Combine(output,
                                "workflow-" + page + "-" + language + (light ? "-light" : "-dark") + ".png")); }
                        }
                        Check(!form.UiActive && typeof(GameMode).GetField("worker",Private).GetValue(mode) == null,
                            "Workflow previews or diagnostics started the tuning runtime");
                    }
                    var device = new IrqDevice { Name = "Fixture GPU", Policy = 4, Mask = 4,
                        RebootState = IrqRebootState.AwaitingReboot };
                    using (var dialog = new IrqPinDialog(device))
                    {
                        dialog.StartPosition = FormStartPosition.Manual; dialog.Location = new Point(-20000,-20000);
                        CreateHiddenTree(dialog); dialog.FitToWorkingArea(new Rectangle(-20000,-20000,1000,1000));
                        CheckDialogLayout(dialog,"Custom theme workflow");
                        Check(DialogField<Label>(dialog,"metricStatus").ForeColor == Theme.Warning,
                            "Awaiting restart used the user's accent as a success/status color");
                        Check(DialogField<Label>(dialog,"lblPick").Text.Contains(IrqUiState.LogicalCpus(4)),
                            "The fixed selection summary omits CPU identifiers or their count");
                        dialog.SetReadOnly(Lang.T("irq.flow.readonly"));
                        Check(!DialogField<Control>(dialog,"btnOk").Enabled && !DialogField<Control>(dialog,"swPriority").Enabled,
                            "Read-only review exposes a device mutation");
                        SaveDialog(dialog,output,"workflow-dialog-" + language + (light ? "-light" : "-dark"));
                    }
                }
            }
            finally
            {
                Logger.LogPath = oldLog;
                if (oldOverride) Theme.SetModeColorOverride(PerformancePreset.Standard,oldColor);
                else Theme.ClearModeColorOverride(PerformancePreset.Standard);
                Theme.SetMode(oldMode,false);
                PanelForm.SuppressUiWorkersForTest = suppressed;
            }
        }

        private static void CheckProbeToggle(PanelForm form, GameMode mode, Toggle toggle,
            bool on, string action, string output)
        {
            int prompts = 0, mutations = 0;
            Exception callbackError = null;
            FieldInfo changed = typeof(GameMode).GetField("irqSettingChanged", Private);
            changed.SetValue(mode, 0);
            IrqMutationBoundary.Configure(delegate { mutations++; }, null);
            using (var timer = new Timer { Interval = 25 })
            {
                timer.Tick += delegate
                {
                    PaviseDialog warning = null;
                    foreach (Form open in Application.OpenForms)
                        if (open is PaviseDialog) { warning = (PaviseDialog)open; break; }
                    if (warning == null) return;
                    timer.Stop(); prompts++;
                    try
                    {
                        warning.Location = new Point(-20000, -20000);
                        Check(!IrqSessionProbe.EnabledSetting && mutations == 0 && (int)changed.GetValue(mode) == 0,
                            "Capture changed before the user confirmed");
                        string body = (string)typeof(PaviseDialog).GetField("body", Private).GetValue(warning);
                        Check(body == Lang.T("irq.probe.warn")
                            && body.Contains(Lang.Cur == 0 ? "帧率损耗" : "reduces frame rate")
                            && body.Contains(Lang.Cur == 0 ? "正常打游戏请勿开启" : "Keep it off during normal gameplay"),
                            "Missing performance or debug-only warning");
                        Check((DlgKind)typeof(PaviseDialog).GetField("kind", Private).GetValue(warning) == DlgKind.Warn,
                            "Observation must use warning styling");
                        foreach (Control control in warning.Controls)
                            Check(warning.ClientRectangle.Contains(control.Bounds), "Warning action is clipped");
                        if (action == "cancel")
                            using (var image = new Bitmap(warning.Width, warning.Height))
                            {
                                warning.DrawToBitmap(image, warning.ClientRectangle);
                                image.Save(Path.Combine(output, "warning.png"), ImageFormat.Png);
                            }
                        if (action == "escape")
                            typeof(Form).GetMethod("OnKeyDown", Private).Invoke(warning, new object[] { new KeyEventArgs(Keys.Escape) });
                        else if (action == "close") warning.Close();
                        else
                        {
                            // Refreshing the page while the modal is open must not wipe an intent already accepted
                            if (action == "confirm") toggle.SetSilently(false);
                            Control button = null;
                            foreach (Control control in warning.Controls)
                                if (control is PillButton && control.Text == Lang.T(action == "confirm" ? "dlg.confirm" : "dlg.cancel"))
                                    button = control;
                            Check(button != null, "Missing confirmation or cancellation action");
                            typeof(Control).GetMethod("OnClick", Private).Invoke(button, new object[] { EventArgs.Empty });
                        }
                    }
                    catch (Exception error)
                    {
                        callbackError = error;
                        warning.DialogResult = DialogResult.Cancel; warning.Close();
                    }
                };
                timer.Start();
                toggle.Checked = on;
                timer.Stop();
            }
            if (callbackError != null) throw callbackError;
            bool expected = on && action == "confirm";
            Check(prompts == (on ? 1 : 0), "Wrong warning count for " + action);
            Check(toggle.Checked == expected && IrqSessionProbe.EnabledSetting == expected,
                "Wrong observation state after " + action);
            Check(mutations == (!on || expected ? 1 : 0) && (int)changed.GetValue(mode) == mutations,
                "Canceled observation changed the capture lifecycle");
        }
    }
}
#endif
