using System.Drawing;
using System.Windows.Forms;

namespace PaviseApp
{
    // 横向单选控件共用的交互 悬停高亮渐变 左键选中 左右方向键切换
    // 子类提供命中测试 选中动作 选项数和当前选项
    internal abstract class HorizontalChoiceControl : FxControl
    {
        protected int hoverIdx = -1;

        protected Motion[] glow = new Motion[0];

        protected abstract int ChoiceCount { get; }

        protected abstract int SelectedChoice { get; }

        protected abstract int HitIndex(Point p);

        protected abstract void Activate(int hit);

        protected bool StepGlow()
        {
            bool busy = false;
            for (int i = 0; i < glow.Length; i++)
            {
                if (glow[i].Step())
                {
                    busy = true;
                }
            }
            return busy;
        }

        protected void SyncGlow()
        {
            for (int i = 0; i < glow.Length; i++)
            {
                glow[i].To((i == hoverIdx) ? 1f : 0f);
            }
            UiClock.Wake();
        }

        protected float GlowAt(int i)
        {
            if (i < 0 || i >= glow.Length)
            {
                return 0f;
            }
            return glow[i].Value;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int hit = HitIndex(e.Location);
            if (hit != hoverIdx)
            {
                hoverIdx = hit;
                SyncGlow();
                Invalidate();
            }
        }

        protected override void OnMouseLeave(System.EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hoverIdx != -1)
            {
                hoverIdx = -1;
                SyncGlow();
                Invalidate();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left)
            {
                Activate(HitIndex(e.Location));
            }
        }

        protected override bool IsInputKey(Keys keyData)
        {
            if (DsKeys.IsHorizontalNavigation(keyData & Keys.KeyCode))
            {
                return true;
            }
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Handled || e.Modifiers != Keys.None)
            {
                return;
            }
            int next = DsKeys.NavigateHorizontal(e.KeyCode, SelectedChoice, ChoiceCount);
            if (next >= 0)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                Activate(next);
            }
        }
    }
}
