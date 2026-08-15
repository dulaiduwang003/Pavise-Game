// @author bdth 2074055628@qq.com
// 文件用途 下拉/浮层菜单的共享渲染件:菜单项徽标 MenuBadge 与统一深色菜单渲染器 TechMenuRenderer
// 原 TechSelect 切角下拉控件已随电源选择器移入标题栏 PowerFlyout 而下架 这里只留仍被复用的两个渲染类型

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{

    internal sealed class MenuBadge
    {
        public readonly string Text;
        public readonly bool Strong;
        public MenuBadge(string text, bool strong) { Text = text; Strong = strong; }
    }

    internal sealed class TechMenuRenderer : ToolStripRenderer
    {
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = e.AffectedBounds;
            r.Width -= 1; r.Height -= 1;
            using (GraphicsPath path = Theme.TechPath(r, Theme.S(8)))
            {
                using (var b = new SolidBrush(Theme.Card)) g.FillPath(b, path);
                using (var p = new Pen(Theme.Stroke)) g.DrawPath(p, path);
            }
            using (var p = new Pen(Theme.Accent, Math.Max(1.6f, Theme.S(2))))
                g.DrawLine(p, r.Left + Theme.S(2), r.Top, r.Left + Theme.S(26), r.Top);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e) { }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.None;
            var r = new Rectangle(0, 0, e.Item.Width, e.Item.Height);
            using (var b = new SolidBrush(Col.Alpha(Theme.Accent, 40))) g.FillRectangle(b, r);
            using (var b = new SolidBrush(Theme.Accent))
                g.FillRectangle(b, new Rectangle(0, 0, Theme.S(3), r.Height));
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            Graphics g = e.Graphics;
            var badge = e.Item.Tag as MenuBadge;
            var r = new Rectangle(Theme.S(12), 0, e.Item.Width - Theme.S(18), e.Item.Height);

            const TextFormatFlags Line = TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis;

            if (badge == null || string.IsNullOrEmpty(badge.Text))
            {
                TextRenderer.DrawText(g, e.Text, e.TextFont, r, e.TextColor, Line);
                return;
            }

            Size bs = TextRenderer.MeasureText(g, badge.Text, Theme.MonoFor(badge.Text, 7.0f));
            int bw = bs.Width + Theme.S(12);
            var pill = new Rectangle(r.Right - bw, (e.Item.Height - Theme.S(17)) / 2,
                bw, Theme.S(17));
            r.Width -= bw + Theme.S(8);
            TextRenderer.DrawText(g, e.Text, e.TextFont, r, e.TextColor, Line);

            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color fill = badge.Strong ? Theme.Accent : Theme.Inset;
            Color edge = badge.Strong ? Col.Alpha(Theme.Accent, 235) : Theme.Stroke;
            Color ink = badge.Strong ? Theme.OnAccent : Theme.Faint;
            using (GraphicsPath path = Theme.TechPath(pill, Theme.S(4)))
            {
                using (var b = new SolidBrush(fill)) g.FillPath(b, path);
                using (var p = new Pen(edge)) g.DrawPath(p, path);
            }
            TextRenderer.DrawText(g, badge.Text, Theme.MonoFor(badge.Text, 7.0f), pill, ink,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                    | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        }
    }
}
