// 作者：bdth 2074055628@qq.com
using System;
using System.Windows.Forms;

namespace PaviseApp
{
    // 忙碌交互契约：控件执行或等待动作期间拒绝一切激活，但保留可见与可读状态。
    internal interface IBusyInteractive
    {
        bool Busy { get; set; }
        bool CanInteract { get; }
    }

    // 控件绘制时的瞬时状态快照，由控件自身填充。
    internal struct DsState
    {
        public bool Enabled;
        public bool Busy;
        public bool Focused;
        public float Hover;
        public float Press;

        public bool Interactive { get { return Enabled && !Busy; } }
        public float HoverIfEnabled { get { return Enabled ? Hover : 0f; } }
    }

    internal static class DsInteraction
    {
        // 统一的激活门禁：已释放、禁用或忙碌的控件不响应点击与键盘。
        public static bool CanActivate(Control control, bool busy)
        {
            return control != null && !control.IsDisposed && control.Enabled && !busy;
        }

        public static bool CanActivate(Control control)
        {
            var busyAware = control as IBusyInteractive;
            return CanActivate(control, busyAware != null && busyAware.Busy);
        }
    }

    // 设计系统内部的双语短文本，不进入 Lang 资源表。
    internal static class DsLabels
    {
        public static string Pick(string zh, string en) { return Lang.Cur == 0 ? zh : en; }
    }

    // 激活键判定集中于一处，所有按钮类控件共享同一套键盘契约。
    internal static class DsKeys
    {
        public static bool IsActivation(Keys keyCode)
        {
            return keyCode == Keys.Enter || keyCode == Keys.Space;
        }

        public static bool IsActivationInput(Keys keyData)
        {
            return IsActivation(keyData & Keys.KeyCode);
        }

        // 无修饰键的 Enter/Space 才算激活，避免吞掉 Ctrl+Enter 等组合。
        public static bool IsPlainActivation(KeyEventArgs e)
        {
            return e != null && !e.Handled && e.Modifiers == Keys.None && IsActivation(e.KeyCode);
        }

        public static bool IsHorizontalNavigation(Keys keyCode)
        {
            return keyCode == Keys.Left || keyCode == Keys.Right || keyCode == Keys.Home || keyCode == Keys.End;
        }

        // 水平导航：返回新索引，非导航键返回 -1。
        public static int NavigateHorizontal(Keys keyCode, int current, int count)
        {
            if (count <= 0) return -1;
            if (current < 0) current = 0;
            if (current >= count) current = count - 1;
            switch (keyCode)
            {
                case Keys.Left: return Math.Max(0, current - 1);
                case Keys.Right: return Math.Min(count - 1, current + 1);
                case Keys.Home: return 0;
                case Keys.End: return count - 1;
                default: return -1;
            }
        }

        public static bool IsTab(Keys keys)
        {
            return keys == Keys.Tab || keys == (Keys.Shift | Keys.Tab);
        }

        // 下拉展开键：F4、Alt+Down、上下方向键；按钮型菜单另含 Apps 与 Shift+F10。
        public static bool IsDropdownOpen(Keys keys, bool menuButton)
        {
            if (keys == Keys.F4 || keys == (Keys.Alt | Keys.Down)) return true;
            if (menuButton) return keys == Keys.Apps || keys == (Keys.Shift | Keys.F10);
            return keys == Keys.Space || keys == Keys.Enter || keys == Keys.Down || keys == Keys.Up;
        }
    }

    // 忙碌门禁：进入时置忙并按需禁用，离开时恢复进入前的 Enabled；重复进入直接失败。
    internal sealed class BusyGate
    {
        private readonly Control control;
        private readonly IBusyInteractive target;
        private readonly bool disableWhileBusy;
        private bool held, wasEnabled;

        public BusyGate(Control control, bool disableWhileBusy)
        {
            if (control == null) throw new ArgumentNullException("control");
            this.control = control;
            target = control as IBusyInteractive;
            this.disableWhileBusy = disableWhileBusy;
        }

        public bool Held { get { return held; } }

        public bool TryEnter()
        {
            if (held || !DsInteraction.CanActivate(control)) return false;
            held = true;
            wasEnabled = control.Enabled;
            if (target != null) target.Busy = true;
            if (disableWhileBusy) control.Enabled = false;
            return true;
        }

        public void Leave()
        {
            if (!held) return;
            held = false;
            if (control.IsDisposed) return;
            if (target != null) target.Busy = false;
            if (disableWhileBusy) control.Enabled = wasEnabled;
        }
    }
}
