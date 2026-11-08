using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
    internal sealed class RenderAffinityDialog : Form
    {
        private readonly Func<CriticalThreadRule, string> save;
        private readonly ulong all, allowed, reserved;
        private readonly ulong[] physical;
        private readonly bool trusted;
        private readonly string scopeError, topology;
        private readonly CoreMatrix matrix;
        private readonly PillButton[] modes;
        private readonly Label preview, error, sessionHint;
        private readonly LibraryDialogButton apply, cancel;
        private readonly DBPanel body;
        private readonly int bodyHeightLimit;
        private int mode;
        private int layoutMode = -1;

        internal RenderAffinityDialog(GameProfile profile, CriticalThreadRule initial, Func<CriticalThreadRule, string> saveRule, string conflict = null)
        {
            if (profile == null || saveRule == null) throw new ArgumentNullException();
            save = saveRule;
            Name = "RenderAffinityDialog";
            Text = T("渲染线程亲和性", "Render thread affinity");
            AccessibleName = Text;
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = MaximizeBox = false;
            DoubleBuffered = true;
            BackColor = Theme.Bg;
            ForeColor = Theme.Fg;
            Font = Theme.UI(9f, false);
            all = CpuTopology.AllMask;
            physical = CpuTopology.PhysicalCoreMasks();
            topology = ManualCorePlan.Stamp(all, physical);
            reserved = AutoCores.WholeCores(AutoCores.LiveAllocated() & ~CoreIsolationClient.ActiveMask, physical);
            trusted = IntPtr.Size == 8 && !CpuTopology.MultiGroup && CpuTopology.TopologySourcesAgree && !CpuTopology.AllMaskReconciled;
            CriticalThreadAllowedRange scope = CriticalThreadAllowedRange.For(profile);
            allowed = scope.Mask;
            scopeError = scope.Error;
            mode = initial == null ? 0 : (initial.ManualTarget || !initial.Automatic ? 2 : 1);

            Rectangle screen = Screen.FromPoint(Cursor.Position).WorkingArea;
            int width = Math.Min(Theme.S(680), Math.Max(Theme.S(300), screen.Width - Theme.S(40)));
            int pad = Theme.S(24), inner = width - pad * 2;
            Label title = LabelOf(Text, Theme.UI(15f, true), Theme.Fg);
            title.SetBounds(pad, Theme.S(20), inner, Measure(title, inner));
            Label game = LabelOf(profile.Name, Theme.UI(9f, true), Theme.Dim);
            game.SetBounds(pad, title.Bottom + Theme.S(8), inner, Measure(game, inner));
            body = new DBPanel { Name = "RenderAffinityBody", BackColor = Theme.Bg, AutoScroll = true };
            Native.Dark(body);
            int contentWidth = inner - SystemInformation.VerticalScrollBarWidth - Theme.S(4), y = 0;
            if (!string.IsNullOrEmpty(conflict)) AddText(Lang.T(conflict), Theme.Warning, contentWidth, ref y);
            AddText(T("自动识别关键线程；手动只指定落核。其他游戏线程避让整颗物理核，含超线程同胞。", "The critical thread is identified automatically; Manual chooses its destination only. Other game workers avoid the entire physical core, including its SMT sibling."), Theme.Dim, contentWidth, ref y);
            AddText(PolicyResolver.For(profile).ValueOf("GmAutoCoresV2") == "1"
                ? T("候选范围跟随核心调度：", "Candidates follow the game CPU range: ") + CpuTopology.DescribeMask(allowed)
                : T("候选范围：全部核心（核心调度未开启）。", "Candidate range: all CPUs (core scheduling is off)."), Theme.Fg, contentWidth, ref y);
            modes = new PillButton[3];
            string[] captions = { T("关闭", "Off"), T("自动", "Automatic"), T("手动", "Manual") };
            int gap = Theme.S(8), buttonWidth = (contentWidth - gap * 2) / 3;
            for (int i = 0; i < modes.Length; i++)
            {
                int selectedMode = i;
                modes[i] = new PillButton(captions[i]) { Name = "RenderAffinityMode" + i, TabIndex = i };
                modes[i].SetBounds(i * (buttonWidth + gap), y, buttonWidth, Theme.S(34));
                modes[i].Click += delegate { mode = selectedMode; RefreshChoice(); };
                body.Controls.Add(modes[i]);
            }
            y += Theme.S(48);
            matrix = new CoreMatrix { Name = "RenderAffinityMatrix", Editable = true, SingleSelection = true,
                MarkExclusive = false, BackColor = Theme.Bg, PrimaryTag = T("渲染", "Render"), TabIndex = 3,
                AccessibleName = T("选择一个渲染逻辑 CPU", "Choose one logical CPU for the render thread") };
            matrix.SetBounds(0, y, contentWidth, 1);
            matrix.Height = matrix.LayoutFor(contentWidth);
            matrix.DisabledMask = trusted && scopeError == null ? all & (~allowed | reserved) : all;
            matrix.ShowSelectionImmediately(initial != null && initial.CandidateCount > 0 ? initial.Targets[0] : 0);
            matrix.SelectionChanged += delegate { RefreshChoice(); };
            matrix.FocusedCellChanged += delegate { body.ScrollControlIntoView(matrix); };
            body.Controls.Add(matrix);
            y = matrix.Bottom + Theme.S(12);
            preview = LabelOf("", Theme.UI(9f, false), Theme.Dim);
            preview.Name = "RenderAffinityPreview";
            preview.SetBounds(0, y, contentWidth, Theme.S(86));
            body.Controls.Add(preview);
            y = preview.Bottom + Theme.S(12);
            sessionHint = AddText(T("开启、换核和范围调整下局生效；关闭会立即撤回当前绑定。退局自动恢复。", "Enabling, changing CPUs and range edits apply next session. Turning this off withdraws the current binding immediately. Original settings are restored on exit."), Theme.Dim, contentWidth, ref y);
            body.AutoScrollMinSize = new Size(0, y);
            error = LabelOf("", Theme.UI(9f, false), Theme.Warning);
            error.Name = "RenderAffinityError";
            int errorHeight = Theme.S(54);
            bodyHeightLimit = Math.Max(Theme.S(40), screen.Height - game.Bottom - Theme.S(188));
            body.SetBounds(pad, game.Bottom + Theme.S(16), inner, Math.Min(y, bodyHeightLimit));
            error.SetBounds(pad, body.Bottom + Theme.S(8), inner, errorHeight);
            cancel = new LibraryDialogButton(T("取消", "Cancel"), false) { Name = "RenderAffinityCancel", DialogResult = DialogResult.Cancel, TabIndex = 4 };
            cancel.SetBounds(width - pad - Theme.S(228), error.Bottom + Theme.S(10), Theme.S(108), Theme.S(36));
            apply = new LibraryDialogButton(T("保存", "Save"), true) { Name = "RenderAffinitySave", TabIndex = 5 };
            apply.SetBounds(width - pad - Theme.S(108), cancel.Top, Theme.S(108), Theme.S(36));
            apply.Click += delegate { SaveChoice(); };
            Controls.AddRange(new Control[] { title, game, body, error, cancel, apply });
            AcceptButton = apply;
            CancelButton = cancel;
            ClientSize = new Size(width, apply.Bottom + pad);
            RefreshChoice();
        }

        private static string T(string zh, string en) { return PerformancePolicy.Text(zh, en); }
        private static Label LabelOf(string text, Font font, Color ink)
        {
            return new Label { Text = text, Font = font, ForeColor = ink, BackColor = Color.Transparent, UseMnemonic = false, UseCompatibleTextRendering = false };
        }
        private static int Measure(Label label, int width)
        {
            return TextRenderer.MeasureText(label.Text, label.Font, new Size(Math.Max(1, width), int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height + Theme.S(4);
        }
        private Label AddText(string text, Color ink, int width, ref int y)
        {
            Label label = LabelOf(text, Theme.UI(9f, false), ink);
            label.SetBounds(0, y, width, Measure(label, width));
            body.Controls.Add(label);
            y = label.Bottom + Theme.S(12);
            return label;
        }
        private CriticalThreadRule Draft(out string failure)
        {
            failure = null;
            if (mode == 0) return null;
            if (scopeError != null) { failure = scopeError; return null; }
            if (mode == 2 && matrix.Selected == 0) { failure = T("请选择一个逻辑 CPU", "Choose one logical CPU"); return null; }
            CriticalThreadRule rule = mode == 1 ? CriticalThreadRule.Auto(true) : CriticalThreadRule.Manual(topology, matrix.Selected);
            failure = rule.Validate(all, physical, allowed, reserved, trusted);
            return rule;
        }
        private void RefreshChoice()
        {
            for (int i = 0; i < modes.Length; i++) { modes[i].Kind = i == mode ? BtnKind.Primary : BtnKind.Normal; modes[i].Invalidate(); }
            matrix.Editable = mode == 2;
            string failure;
            CriticalThreadRule rule = Draft(out failure);
            error.Text = failure == null ? "" : Lang.T(failure);
            apply.Enabled = failure == null;
            if (mode == 0) preview.Text = T("关闭渲染线程亲和性。", "Render thread affinity is off.");
            else if (mode == 1) preview.Text = T("自动选择范围内的合适核心；优先性能核、优选核与大缓存核心。至少保留两个物理核给其他工作线程。", "Automatically chooses a suitable CPU within the allowed range, preferring performance, favored and large-cache cores. At least two physical cores remain for other workers.");
            else
            {
                ulong whole = AutoCores.WholeCores(matrix.Selected, physical);
                preview.Text = T("渲染线程目标：", "Render destination: ") + CriticalThreadRule.ProcessorName(matrix.Selected)
                    + "\n" + T("整核避让：", "Whole-core avoidance: ") + CpuTopology.DescribeMask(whole)
                    + "\n" + T("其他游戏线程：", "Other game workers: ") + CpuTopology.DescribeMask(allowed & ~(whole | reserved));
            }
            AccessibleDescription = preview.Text + " " + error.Text;
            LayoutChoice();
        }
        private void LayoutChoice()
        {
            if (layoutMode == mode) return;
            layoutMode = mode;
            Rectangle previousBounds = Bounds;
            body.SuspendLayout();
            try
            {
                body.AutoScrollPosition = Point.Empty;
                bool manual = mode == 2;
                matrix.Visible = preview.Visible = manual;
                sessionHint.Top = manual ? preview.Bottom + Theme.S(12) : modes[0].Bottom + Theme.S(14);
                int contentHeight = sessionHint.Bottom + Theme.S(12);
                body.AutoScrollMinSize = new Size(0, contentHeight);
                body.Height = Math.Min(contentHeight, bodyHeightLimit);
                error.Top = body.Bottom + Theme.S(8);
                cancel.Top = apply.Top = error.Bottom + Theme.S(10);
                ClientSize = new Size(ClientSize.Width, apply.Bottom + Theme.S(24));
            }
            finally { body.ResumeLayout(); }
            Rectangle screen = Screen.FromControl(Owner ?? this).WorkingArea;
            if (Visible && previousBounds.IntersectsWith(screen))
                Top = Math.Max(screen.Top, Math.Min(Top, screen.Bottom - Height));
        }
        private void SaveChoice()
        {
            string failure;
            CriticalThreadRule rule = Draft(out failure);
            if (failure == null) failure = save(rule);
            if (failure != null) { error.Text = Lang.T(failure); return; }
            DialogResult = DialogResult.OK;
            Close();
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (GraphicsPath path = Theme.TechPath(new Rectangle(0, 0, Width - 1, Height - 1), Theme.S(12)))
            using (Pen pen = new Pen(Theme.StrokeHi)) e.Graphics.DrawPath(pen, path);
        }
    }
}
