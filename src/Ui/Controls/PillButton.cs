// 作者：bdth 2074055628@qq.com
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
    internal enum BtnKind { Normal, Primary, Danger }

    // 切角按钮：面、墨色、焦点环与忙碌轨均来自设计系统，激活统一经 CanInteract 门禁。
    internal class PillButton : FxControl
    {
        public BtnKind Kind = BtnKind.Normal;

        public PillButton(string text)
        {
            Text = text; Height = Theme.S(DsMetrics.Button.Height);
            Font = DsType.Font(DsText.Body); ForeColor = Theme.Fg;
            TabStop = true; AccessibleRole = AccessibleRole.PushButton;
        }
        public PillButton(string text, BtnKind kind) : this(text) { Kind = kind; }

        public void PerformClick()
        {
            if (CanInteract) OnClick(EventArgs.Empty);
        }

        protected override bool IsInputKey(Keys keyData)
        {
            if (DsKeys.IsActivation(keyData)) return true;
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (!DsKeys.IsPlainActivation(e) || !CanInteract) return;
            e.SuppressKeyPress = true;
            PerformClick();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            FillBg(g);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            DsState state = State;
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            int cut = Math.Max(Theme.S(DsMetrics.Button.MinCut), Height / 4);

            using (var path = Theme.TechPath(r, cut))
            {
                DsPaint.ButtonFace(g, path, r, cut, Size, Kind, state, EffBg);
                PaintBusy(g, r, path);
            }

            TextRenderer.DrawText(g, Text, Kind == BtnKind.Primary ? DsType.Font(DsText.Body, true) : Font,
                ClientRectangle, DsPaint.ButtonInk(Kind, state),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            if (WantsFocusCue)
                DsFocus.Draw(g, DsFocus.Inset(ClientRectangle, DsMetrics.Button.FocusInset),
                    Kind == BtnKind.Primary ? Theme.OnAccent : Theme.Fg, EffBg);
        }
    }

}
