// 作者：bdth 2074055628@qq.com
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
    // 卡片菜单按钮：菜单生命周期交给 DropdownHost（右对齐、零边距、5 像素间隙）；禁用或忙碌时不显示菜单。
    internal sealed class GameCardMenuButton : PillButton
    {
        private const int MenuGap = 5;
        private readonly DropdownHost host;

        internal GameCardMenuButton() : base("")
        {
            host = new DropdownHost(this);
            host.AlignRight = true;
            host.GapLogical = MenuGap;
            host.EdgeMarginLogical = 0;
        }

        internal bool MenuVisible { get { return host.IsVisible; } }
        // 当前托管的菜单与最近一次定位，供测试与诊断。
        internal ContextMenuStrip OwnedMenu { get { return host.Menu; } }
        internal Rectangle OwnedMenuPlacement { get { return host.LastPlacement; } }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            float diameter = Math.Max(2, Theme.S(3)), step = Theme.S(5);
            float left = (Width - diameter) / 2f, top = (Height - diameter) / 2f;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var dot = new SolidBrush(Enabled ? Theme.Fg : Theme.Faint))
                for (int index = -1; index <= 1; index++)
                    e.Graphics.FillEllipse(dot, left + index * step, top, diameter, diameter);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && CanInteract) { host.NoteMouseDown(); Focus(); }
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left) host.NoteMouseUp();
        }

        protected override void OnClick(EventArgs e)
        {
            if (!CanInteract || IsDisposed) return;
            if (host.ConsumeCloseClick()) return;
            base.OnClick(e);
        }

        protected override bool ProcessCmdKey(ref Message message, Keys keys)
        {
            if (host.HandleCommandKey(keys, true, PerformClick)) return true;
            return base.ProcessCmdKey(ref message, keys);
        }

        internal void ShowMenu(ContextMenuStrip next) { host.Show(next); }
        internal void CloseMenu() { host.Close(); }

        protected override void Dispose(bool disposing)
        {
            if (disposing) host.Dispose();
            base.Dispose(disposing);
        }
    }
}
