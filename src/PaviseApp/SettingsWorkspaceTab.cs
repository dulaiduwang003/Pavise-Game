using System;
using System.Windows.Forms;

namespace PaviseApp
{
    internal sealed class SettingsWorkspaceTab : PerformanceActionButton
    {
        private sealed class TabAccessibility : FxAccessibleObject
        {
            private readonly SettingsWorkspaceTab owner;

            public override string DefaultAction
            {
                get
                {
                    return PerformancePolicy.Text("切换分类", "Switch category");
                }
            }

            public override AccessibleStates State
            {
                get
                {
                    return base.State | (owner.Selected ? AccessibleStates.Selected : AccessibleStates.None);
                }
            }

            internal TabAccessibility(SettingsWorkspaceTab button)
                : base(button)
            {
                owner = button;
            }

            public override void DoDefaultAction()
            {
                owner.PerformClick();
            }
        }

        internal bool Selected
        {
            get { return CurrentCategory; }
            set { CurrentCategory = value; }
        }

        internal Action<Keys> NavigationRequested;

        internal SettingsWorkspaceTab(string text)
            : base(text)
        {
            IsCategoryTab = true;
            base.AccessibleRole = AccessibleRole.PageTab;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (!e.Handled && e.Modifiers == Keys.None && DsKeys.IsHorizontalNavigation(e.KeyCode))
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                if (base.CanInteract && NavigationRequested != null)
                {
                    NavigationRequested(e.KeyCode);
                }
            }
        }

        protected override AccessibleObject CreateAccessibilityInstance()
        {
            return new TabAccessibility(this);
        }

    }
}
