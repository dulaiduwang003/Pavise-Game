// @author bdth 2074055628@qq.com
// 文件用途 每次开启家族压制的风险确认 默认保持关闭 完整路径可复制
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
    internal sealed class FamilySuppressionDialog : Form
    {
        private readonly LibraryDialogButton keepClosed;
        internal readonly LibraryDialogButton EnableAnyway;
        internal readonly TextBox ExecutableBox;
        internal FamilySuppressionDialog(string gameName, string executablePath)
        {
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None; StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false; MaximizeBox = false; ShowInTaskbar = false;
            BackColor = Theme.Bg; ForeColor = Theme.Fg; Font = Theme.UI(9f, false);
            DoubleBuffered = true;
            Text = Lang.T("lib.family.warning.title"); AccessibleName = Text;
            int width = Theme.S(544), pad = Theme.S(26), inner = width - pad * 2;
            var kicker = MakeLabel("FAMILY // CAUTION", Theme.Mono(7.3f), Theme.Accent);
            kicker.SetBounds(pad, Theme.S(21), inner, Theme.S(19));
            var title = MakeLabel(Text, Theme.UI(15f, true), Theme.Fg);
            title.SetBounds(pad, Theme.S(48), inner, Theme.S(34));
            var game = MakeLabel(gameName ?? "", Theme.UI(9.2f, true), Theme.Dim);
            game.AutoEllipsis = true; game.SetBounds(pad, Theme.S(92), inner, Theme.S(24));
            var current = MakeLabel(Lang.T("lib.family.warning.target"), Theme.UI(8f, false), Theme.Dim);
            current.SetBounds(pad, Theme.S(126), inner, Theme.S(22));
            ExecutableBox = new TextBox();
            ExecutableBox.ReadOnly = true; ExecutableBox.Multiline = true;
            ExecutableBox.WordWrap = true; ExecutableBox.ScrollBars = ScrollBars.Vertical;
            ExecutableBox.BorderStyle = BorderStyle.FixedSingle;
            ExecutableBox.BackColor = Theme.Inset; ExecutableBox.ForeColor = Theme.Fg;
            ExecutableBox.Font = Theme.UI(8.5f, false); ExecutableBox.TabIndex = 2;
            ExecutableBox.AccessibleName = Lang.T("lib.family.warning.target");
            Native.Dark(ExecutableBox);
            ExecutableBox.Text = string.IsNullOrEmpty(executablePath) ? Lang.T("lib.family.warning.noexe") : executablePath;
            ExecutableBox.SetBounds(pad, Theme.S(150), inner, Theme.S(70));

            string body = Lang.T("lib.family.warning.body");
            int bodyHeight = TextRenderer.MeasureText(body, Theme.UI(9.2f, false),
                new Size(inner, int.MaxValue), TextFormatFlags.WordBreak).Height + Theme.S(12);
            var message = MakeLabel(body, Theme.UI(9.2f, false), Theme.Dim);
            int messageY = Theme.S(240);
            message.SetBounds(pad, messageY, inner, bodyHeight);
            int buttonY = message.Bottom + Theme.S(25);
            int buttonH = Theme.S(40);
            keepClosed = new LibraryDialogButton(Lang.T("lib.family.warning.cancel"), true);
            keepClosed.DialogResult = DialogResult.Cancel; keepClosed.TabIndex = 0;
            EnableAnyway = new LibraryDialogButton(Lang.T("lib.family.warning.enable"), false);
            EnableAnyway.DialogResult = DialogResult.OK; EnableAnyway.TabIndex = 1;
            int proceedW = Theme.S(152), cancelW = Theme.S(196), gap = Theme.S(12);
            keepClosed.SetBounds(width - pad - proceedW - gap - cancelW, buttonY, cancelW, buttonH);
            EnableAnyway.SetBounds(width - pad - proceedW, buttonY, proceedW, buttonH);
            ClientSize = new Size(width, buttonY + buttonH + Theme.S(24));
            Controls.AddRange(new Control[] { kicker, title, game, current, ExecutableBox, message, keepClosed, EnableAnyway });
            // 与通用提示窗不同 Enter/Escape 都先保持关闭 Tab 后仍可明确选择开启
            AcceptButton = keepClosed; CancelButton = keepClosed; ActiveControl = keepClosed;
        }
        private static Label MakeLabel(string text, Font font, Color color)
        {
            return new Label { Text = text, Font = font, ForeColor = color,
                BackColor = Color.Transparent, UseCompatibleTextRendering = false, UseMnemonic = false };
        }
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e); keepClosed.Focus();
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            var frame = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath path = Theme.TechPath(frame, Theme.S(12)))
            using (var edge = new Pen(Theme.StrokeHi)) g.DrawPath(edge, path);
            using (var rail = new Pen(Theme.Accent, Math.Max(1, Theme.S(2))))
            {
                g.DrawLine(rail, Theme.S(1), Theme.S(1), Theme.S(116), Theme.S(1));
                g.DrawLine(rail, Width - Theme.S(13), Theme.S(1), Width - Theme.S(1), Theme.S(13));
            }
            int lineY = keepClosed.Top - Theme.S(13);
            using (var line = new Pen(Theme.Stroke)) g.DrawLine(line, Theme.S(26), lineY, Width - Theme.S(26), lineY);
        }
    }

    // Button 本身实现 IButtonControl 不像普通自绘 Control 它正确支持 Enter/Space
    // 默认按钮 DialogResult Tab 焦点及辅助技术的 Invoke 操作
    internal sealed class LibraryDialogButton : Button
    {
        private readonly bool primary;
        private bool hover;
        internal LibraryDialogButton(string text, bool emphasized)
        {
            primary = emphasized; Text = text; Font = Theme.UI(9f, true);
            FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; TabStop = true;
            Cursor = Cursors.Hand; AccessibleName = text;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Invalidate(); }
        protected override void OnEnter(EventArgs e) { base.OnEnter(e); Invalidate(); }
        protected override void OnLeave(EventArgs e) { base.OnLeave(e); Invalidate(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var bg = new SolidBrush(Theme.Bg)) g.FillRectangle(bg, ClientRectangle);
            var frame = new Rectangle(0, 0, Width - 1, Height - 1);
            Color fill = primary ? Theme.Accent : hover ? Theme.CardHover : Theme.Card;
            using (GraphicsPath path = Theme.TechPath(frame, Theme.S(7)))
            {
                using (var b = new SolidBrush(fill)) g.FillPath(b, path);
                using (var pen = new Pen(primary ? Theme.Accent : Theme.StrokeHi)) g.DrawPath(pen, path);
            }
            Color fg = primary ? Theme.OnAccent : Theme.Fg;
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, fg,
                TextFormatFlags.SingleLine | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            if (Focused && ShowFocusCues)
                ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(frame, -Theme.S(5), -Theme.S(5)), fg, fill);
        }
    }
}
