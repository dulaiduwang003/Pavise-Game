// 作者：bdth 2074055628@qq.com
using System.Drawing;
using System.Windows.Forms;

namespace PaviseApp
{
    // 紧凑图标按钮：沿用 PillButton 的面、门禁与键盘契约，仅在中心绘制齿轮。
    internal sealed class SettingsButton : PillButton
    {
        private readonly ToolTip tips = new ToolTip();

        public SettingsButton(string name, string description) : base("")
        {
            AccessibleName = name;
            AccessibleDescription = description;
            tips.SetToolTip(this, description);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            int size = Theme.S(DsMetrics.Button.GlyphSize);
            Glyphs.Draw(e.Graphics, "gear", new Rectangle((Width - size) / 2, (Height - size) / 2, size, size),
                Enabled ? Col.Lerp(Theme.Dim, Theme.Fg, hover.Value) : Theme.Faint);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) tips.Dispose();
            base.Dispose(disposing);
        }
    }
}
