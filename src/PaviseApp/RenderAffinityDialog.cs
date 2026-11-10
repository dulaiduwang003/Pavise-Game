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
        private readonly Label title, game, scope, conflictHint, preview, error, sessionHint;
        private readonly LibraryDialogButton apply, cancel;
        private readonly DBPanel body;
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
            CriticalThreadAllowedRange range = CriticalThreadAllowedRange.For(profile);
            allowed = range.Mask;
            scopeError = range.Error;
            mode = initial == null ? 0 : (initial.ManualTarget || !initial.Automatic ? 2 : 1);

            title = LabelOf(Text, Theme.UI(15f, true), Theme.Fg);
            game = LabelOf(profile.Name, Theme.UI(9f, true), Theme.Dim);
            game.AutoEllipsis = true;
            conflictHint = LabelOf(string.IsNullOrEmpty(conflict) ? "" : Lang.T(conflict), Theme.UI(9f, false), Theme.Warning);
            scope = LabelOf(allowed == all
                ? T("可选核心：全部", "Available CPUs: all")
                : T("可选核心：", "Available CPUs: ") + CpuTopology.DescribeMask(allowed), Theme.UI(9f, false), Theme.Dim);
            body = new DBPanel { Name = "RenderAffinityBody", BackColor = Theme.Bg, AutoScroll = true, TabIndex = 3 };
            Native.Dark(body);
            modes = new PillButton[3];
            string[] captions = { T("关闭", "Off"), T("自动", "Automatic"), T("手动", "Manual") };
            for (int i = 0; i < modes.Length; i++)
            {
                int selectedMode = i;
                modes[i] = new PillButton(captions[i]) { Name = "RenderAffinityMode" + i, TabIndex = i };
                modes[i].Click += delegate { mode = selectedMode; RefreshChoice(); };
                Controls.Add(modes[i]);
            }
            matrix = new CoreMatrix { Name = "RenderAffinityMatrix", Editable = true, SingleSelection = true,
                MarkExclusive = false, ShowCoreTypeStatus = false, BackColor = Theme.Bg, PrimaryTag = T("渲染", "Render"), TabIndex = 0,
                AccessibleName = T("选择一个渲染逻辑 CPU", "Choose one logical CPU for the render thread") };
            matrix.DisabledMask = trusted && scopeError == null ? all & (~allowed | reserved) : all;
            matrix.ShowSelectionImmediately(initial != null && initial.CandidateCount > 0 ? initial.Targets[0] : 0);
            matrix.SelectionChanged += delegate { RefreshChoice(); };
            matrix.FocusedCellChanged += RevealCoreCell;
            matrix.GotFocus += delegate { RevealCoreCell(matrix.FocusedCellBounds); };
            body.Controls.Add(matrix);
            preview = LabelOf("", Theme.UI(9f, false), Theme.Dim);
            preview.Name = "RenderAffinityPreview";
            sessionHint = LabelOf(T("更改下局生效，关闭立即撤回。", "Changes apply next session; Off withdraws immediately."), Theme.UI(9f, false), Theme.Dim);
            error = LabelOf("", Theme.UI(9f, false), Theme.Warning);
            error.Name = "RenderAffinityError";
            cancel = new LibraryDialogButton(T("取消", "Cancel"), false) { Name = "RenderAffinityCancel", DialogResult = DialogResult.Cancel, TabIndex = 4 };
            apply = new LibraryDialogButton(T("保存", "Save"), true) { Name = "RenderAffinitySave", TabIndex = 5 };
            apply.Click += delegate { SaveChoice(); };
            Controls.AddRange(new Control[] { title, game, conflictHint, scope, body, preview, sessionHint, error, cancel, apply });
            AcceptButton = apply;
            CancelButton = cancel;
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
            Draft(out failure);
            error.Text = failure == null ? "" : Lang.T(failure);
            apply.Enabled = failure == null;
            preview.Text = "";
            if (mode == 2 && matrix.Selected != 0)
            {
                ulong whole = AutoCores.WholeCores(matrix.Selected, physical);
                preview.Text = T("渲染线程目标：", "Render destination: ") + CriticalThreadRule.ProcessorName(matrix.Selected)
                    + "\n" + T("整核避让：", "Whole-core avoidance: ") + CpuTopology.DescribeMask(whole);
            }
            AccessibleDescription = modes[mode].Text + " " + scope.Text + " " + preview.Text + " " + sessionHint.Text + " " + error.Text;
            LayoutChoice();
        }
        private void LayoutChoice()
        {
            bool manual = mode == 2;
            int scroll = layoutMode == mode ? Math.Max(0, -body.AutoScrollPosition.Y) : 0;
            layoutMode = mode;
            Rectangle previousBounds = Bounds;
            Rectangle screen = Screen.FromControl(Owner ?? this).WorkingArea;
            int pad = Theme.S(24), gap = Theme.S(8);
            int width = Math.Min(Theme.S(600), Math.Max(1, screen.Width - pad * 2));
            int inner = Math.Max(1, width - pad * 2);
            int maximumHeight = Math.Max(1, screen.Height - pad * 2);
            int y = Theme.S(20);
            title.SetBounds(pad, y, inner, Measure(title, inner));
            game.SetBounds(pad, title.Bottom + Theme.S(4), inner, Theme.S(20));
            y = game.Bottom + Theme.S(16);
            conflictHint.Visible = conflictHint.Text.Length > 0;
            if (conflictHint.Text.Length > 0)
            {
                conflictHint.SetBounds(pad, y, inner, Measure(conflictHint, inner));
                y = conflictHint.Bottom + gap;
            }
            int buttonWidth = (inner - gap * 2) / 3;
            for (int i = 0; i < modes.Length; i++) modes[i].SetBounds(pad + i * (buttonWidth + gap), y, buttonWidth, Theme.S(34));
            y = modes[0].Bottom + Theme.S(12);
            scope.Visible = mode != 0;
            if (mode != 0)
            {
                scope.SetBounds(pad, y, inner, Measure(scope, inner));
                y = scope.Bottom + gap;
            }
            int previewHeight = Theme.S(40);
            int hintHeight = Measure(sessionHint, inner);
            int errorHeight = Math.Max(Theme.S(28), Measure(error, inner));
            int footerHeight = hintHeight + gap + errorHeight + Theme.S(12) + Theme.S(36) + pad;
            body.SuspendLayout();
            try
            {
                body.AutoScrollPosition = Point.Empty;
                body.Visible = matrix.Visible = preview.Visible = manual;
                if (manual)
                {
                    int gridLimit = Math.Min(Theme.S(220), Math.Max(1, maximumHeight - y - footerHeight - previewHeight - Theme.S(18)));
                    int matrixWidth = inner;
                    int matrixHeight = matrix.LayoutFor(matrixWidth);
                    if (matrixHeight > gridLimit)
                    {
                        matrixWidth = Math.Max(1, inner - SystemInformation.VerticalScrollBarWidth - Theme.S(4));
                        matrixHeight = matrix.LayoutFor(matrixWidth);
                    }
                    body.SetBounds(pad, y, inner, Math.Min(matrixHeight, gridLimit));
                    matrix.SetBounds(0, 0, matrixWidth, matrixHeight);
                    body.AutoScrollMinSize = new Size(0, matrixHeight);
                    body.AutoScrollPosition = new Point(0, Math.Min(scroll, Math.Max(0, matrixHeight - body.ClientSize.Height)));
                    preview.SetBounds(pad, body.Bottom + Theme.S(10), inner, previewHeight);
                    y = preview.Bottom + gap;
                }
                sessionHint.SetBounds(pad, y, inner, hintHeight);
                error.SetBounds(pad, sessionHint.Bottom + gap, inner, errorHeight);
                cancel.SetBounds(width - pad - Theme.S(228), error.Bottom + Theme.S(12), Theme.S(108), Theme.S(36));
                apply.SetBounds(width - pad - Theme.S(108), cancel.Top, Theme.S(108), Theme.S(36));
                ClientSize = new Size(width, apply.Bottom + pad);
            }
            finally { body.ResumeLayout(); }
            if (Visible && previousBounds.IntersectsWith(screen))
            {
                Left = Math.Max(screen.Left, Math.Min(Left, screen.Right - Width));
                Top = Math.Max(screen.Top, Math.Min(Top, screen.Bottom - Height));
            }
        }
        private void RevealCoreCell(Rectangle cell)
        {
            if (cell.IsEmpty || !body.Visible) return;
            int scroll = Math.Max(0, -body.AutoScrollPosition.Y);
            int desired = scroll;
            if (cell.Top < scroll) desired = Math.Max(0, cell.Top - Theme.S(4));
            else if (cell.Bottom > scroll + body.ClientSize.Height) desired = cell.Bottom - body.ClientSize.Height + Theme.S(4);
            int limit = Math.Max(0, matrix.Height - body.ClientSize.Height);
            body.AutoScrollPosition = new Point(0, Math.Min(desired, limit));
        }
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            LayoutChoice();
            if (mode == 2) RevealCoreCell(matrix.FocusedCellBounds);
        }
        private void SaveChoice()
        {
            string failure;
            CriticalThreadRule rule = Draft(out failure);
            if (failure == null) failure = save(rule);
            if (failure != null) { error.Text = Lang.T(failure); LayoutChoice(); return; }
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
