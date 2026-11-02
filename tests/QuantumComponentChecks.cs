#if PAVISE_SELFTEST && PAVISE_COMPONENT_LIBRARY
using System;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;

namespace PaviseApp
{
    // Uses the component runner's hidden host; no native popup is shown in self-test mode.
    internal static class QuantumComponentChecks
    {
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        private static int checks;
        private static void Check(bool value, string reason)
        { if (!value) throw new InvalidOperationException("Quantum component: " + reason); checks++; }
        private static T Field<T>(object owner, string name)
        { return (T)owner.GetType().GetField(name, Hidden).GetValue(owner); }
        private static object Call(object owner, string name, params object[] args)
        { return owner.GetType().GetMethod(name, Hidden).Invoke(owner, args); }
        private static void Pump() { for (int i = 0; i < 3; i++) Application.DoEvents(); }

        internal static int Run(Control hiddenHost)
        {
            int before = checks, selections = 0, created = 0;
            Action<ToolStripDropDown> previous = QuantumProfilePicker.PopupCreatedForTest;
            QuantumProfilePicker.PopupCreatedForTest = delegate(ToolStripDropDown popup) {
                Check(!popup.Visible, "Test popup became visible"); created++;
            };
            try
            {
                using (var parent = new Panel { Bounds = new Rectangle(0, 0, Theme.S(700), Theme.S(240)) })
                using (var picker = new QuantumProfilePicker { Location = new Point(Theme.S(20), Theme.S(40)) })
                {
                    hiddenHost.Controls.Add(parent); parent.Controls.Add(picker);
                    picker.SetSilently(1); picker.SelectionChanged += delegate { selections++; };
                    var host = Field<DropdownHost>(picker, "dropdown");
                    Action click = delegate {
                        Call(picker, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, 10, 10, 0));
                        Call(picker, "OnClick", EventArgs.Empty);
                        Call(picker, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1, 10, 10, 0));
                        Pump();
                    };
                    Func<Control> choices = delegate { return Field<Control>(picker, "list"); };
                    Func<Keys, bool> key = delegate(Keys value) {
                        bool handled = (bool)Call(choices(), "ProcessCmdKey", new Message(), value); Pump(); return handled;
                    };
                    Check(picker.Size == new Size(Theme.S(230), Theme.S(38)) && picker.TabStop,
                        "Picker dimensions or tab accessibility changed");
                    click(); Check(host.IsCreated && !host.IsVisible && created == 1 && selections == 0,
                        "First click failed to create one hidden hosted menu");
                    Check(host.Menu.Items.Count == 1 && host.Menu.Items[0] is ToolStripControlHost,
                        "Menu lost the descriptive profile list");
                    Control list = choices();
                    Check(list.Width <= Theme.S(460) && Field<int>(list, "row") == Theme.S(56)
                        && Field<int>(list, "header") == Theme.S(32) && Field<int>(list, "footer") == Theme.S(28),
                        "Profile list row, heading or footer dimensions changed");
                    Check(Screen.FromControl(picker).WorkingArea.Contains(host.LastPlacement), "Popup exceeds the working area");
                    Check(picker.AccessibleDescription == QuantumProfiles.Description(1), "Current profile description is missing");
                    ToolStripDropDown first = host.Menu;
                    click(); Check(!host.IsCreated && first.IsDisposed && selections == 0, "Second click failed to close and release");
                    click(); Check(key(Keys.Down) && selections == 0 && host.IsCreated, "Navigation prematurely selected a profile");
                    Check(key(Keys.Enter) && picker.SelectedIndex == 8 && selections == 1 && !host.IsCreated,
                        "Keyboard selection failed or notified more than once");
                    Check(picker.AccessibleDescription == QuantumProfiles.Description(8), "Selected profile description was not refreshed");

                    click(); list = choices();
                    Action<int> delayedPick = Field<Action<int>>(list, "Picked");
                    picker.Busy = true; Pump();
                    Check(!host.IsCreated, "Busy did not close the menu");
                    click(); delayedPick(2);
                    Check(!host.IsCreated && selections == 1 && picker.SelectedIndex == 8,
                        "Busy picker accepted a mouse activation or stale list callback");
                    picker.Busy = false; delayedPick(2);
                    Check(selections == 1 && picker.SelectedIndex == 8, "A closed list changed the restored picker");
                    click(); picker.Enabled = false; Pump();
                    Check(!host.IsCreated, "Disable did not close the menu");
                    click(); Check(!host.IsCreated && selections == 1, "Disabled picker accepted a click");
                    picker.Enabled = true; click(); parent.Width += Theme.S(10); Pump();
                    Check(!host.IsCreated, "Ancestor resize did not close the menu");
                    click(); Check(key(Keys.Escape) && !host.IsCreated && selections == 1, "Escape changed the profile or retained the menu");
                    click(); Check(key(Keys.Tab) && !host.IsCreated && selections == 1, "Tab failed to close without selecting");
                    click(); Check(key(Keys.Shift | Keys.Tab) && !host.IsCreated && selections == 1,
                        "Reverse Tab failed to close without selecting");

                    typeof(DropdownHost).GetField("closeOnClick", Hidden).SetValue(host, true);
                    click(); Check(!host.IsCreated && selections == 1, "Owner close handshake immediately reopened the menu");
                    typeof(DropdownHost).GetField("closeOnClick", Hidden).SetValue(host, true);
                    Call(picker, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1, -10, -10, 0));
                    click(); Check(host.IsCreated, "Drag-release consumed the next click");
                    ToolStripDropDown last = host.Menu;
                    picker.Dispose(); Pump();
                    Check(!host.IsCreated && last.IsDisposed, "Disposed picker retained its menu");
                    hiddenHost.Controls.Remove(parent);
                }
            }
            finally { QuantumProfilePicker.PopupCreatedForTest = previous; }
            return checks - before;
        }
    }
}
#endif
