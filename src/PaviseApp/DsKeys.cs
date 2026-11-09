using System;
using System.Windows.Forms;

namespace PaviseApp
{
    internal static class DsKeys
    {
        public static bool IsActivation(Keys keyCode)
        {
            if (keyCode != Keys.Return)
            {
                return keyCode == Keys.Space;
            }
            return true;
        }

        public static bool IsActivationInput(Keys keyData)
        {
            return IsActivation(keyData & Keys.KeyCode);
        }

        public static bool IsPlainActivation(KeyEventArgs e)
        {
            if (e != null && !e.Handled && e.Modifiers == Keys.None)
            {
                return IsActivation(e.KeyCode);
            }
            return false;
        }

        public static bool IsHorizontalNavigation(Keys keyCode)
        {
            if (keyCode != Keys.Left && keyCode != Keys.Right && keyCode != Keys.Home)
            {
                return keyCode == Keys.End;
            }
            return true;
        }

        public static int NavigateHorizontal(Keys keyCode, int current, int count)
        {
            if (count <= 0)
            {
                return -1;
            }
            if (current < 0)
            {
                current = 0;
            }
            if (current >= count)
            {
                current = count - 1;
            }
            switch (keyCode)
            {
            case Keys.Left:
                return Math.Max(0, current - 1);
            case Keys.Right:
                return Math.Min(count - 1, current + 1);
            case Keys.Home:
                return 0;
            case Keys.End:
                return count - 1;
            default:
                return -1;
            }
        }

        public static bool IsTab(Keys keys)
        {
            if (keys != Keys.Tab)
            {
                return keys == (Keys.Tab | Keys.Shift);
            }
            return true;
        }

        public static bool IsDropdownOpen(Keys keys, bool menuButton)
        {
            if (keys == Keys.F4 || keys == (Keys.Down | Keys.Alt))
            {
                return true;
            }
            if (menuButton)
            {
                if (keys != Keys.Apps)
                {
                    return keys == (Keys.F10 | Keys.Shift);
                }
                return true;
            }
            if (keys != Keys.Space && keys != Keys.Return && keys != Keys.Down)
            {
                return keys == Keys.Up;
            }
            return true;
        }
    }
}
