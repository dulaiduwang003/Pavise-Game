#if PAVISE_SELFTEST
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Runtime.Serialization;
using System.Windows.Forms;
namespace PaviseApp
{
    internal static partial class SelfTests
    {
        internal static void RunEnvironmentBundleLayoutRegressionTests()
        {
            RunQuantumPickerInteractionTests(); RunQuantumPickerPopupLifecycleTests();
            int previousLanguage = Lang.Cur;
            Settings.UseTransientStoreForCurrentProcess();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            PanelForm form = (PanelForm)FormatterServices.GetUninitializedObject(typeof(PanelForm));
            typeof(PanelForm).GetField("stackBase", flags).SetValue(form, new Dictionary<Control, Dictionary<Control, int>>());
            PerformanceTuning.ResetForTest();
            PerformanceTuning.BootForTest = delegate { return BootSessionIdentity.Create(new Guid("8746e9d4-5e94-42dd-80e7-2673d998076c"), 1); };
            try {
                foreach (int language in new[] { 0, 1, 2 }) {
                    Lang.Cur = language;
                    foreach (PerformanceAction action in PerformanceActionCatalog.All) if (action.Environment) PerformanceTuning.SetState(action.Key, "off");
                    PerformanceTuning.SetState(PerformanceTuning.EnvironmentVbsKey, "off");
                    using (var parent = new Panel { Width = 1100, Height = 1200 }) {
                        object[] args = { parent, 2 };
                        typeof(PanelForm).GetMethod("BuildPerformanceTuningEnvironment", flags).Invoke(form, args);
                        var card = (SettingCard)typeof(PanelForm).GetField("cardPerformanceEnvironmentBundle", flags).GetValue(form);
                        var quantum = (SettingCard)typeof(PanelForm).GetField("cardPerformanceEnvironmentQuantum", flags).GetValue(form);
                        var quantumToggle = (QuantumProfilePicker)typeof(PanelForm).GetField("swPerformanceEnvironmentQuantum", flags).GetValue(form);
                        Eq("environment" + PerformanceActionCatalog.Quantum, quantumToggle.Name);
                        Eq(0, quantumToggle.SelectedIndex);
                        Eq(true, quantum.Desc.Contains(language == 0 ? "游戏推荐" : "Gaming pick"));
                        Eq(true, quantumToggle.AccessibleName.Contains(language == 0 ? "需重启" : "restart"));
                        var next = new SettingCard { Top = quantum.Bottom + 8, Width = card.Width, Height = 80 }; parent.Controls.Add(next);
                        typeof(PanelForm).GetMethod("EnableCardCollapse", flags).Invoke(form, new object[] { parent, new[] { card } });
                        int originalHeight = card.Height, originalNext = next.Top;
                        foreach (PerformanceAction action in PerformanceActionCatalog.All) if (action.Environment) PerformanceTuning.SetState(action.Key, "failed");
                        PerformanceTuning.SetState(PerformanceTuning.EnvironmentVbsKey, "restore-failed");
                        typeof(PanelForm).GetMethod("SyncPerformanceTuningEnvironment", flags).Invoke(form, null);
                        Eq(true, card.Desc.Contains("VBS")); Eq(true, card.Desc.Contains("CPU"));
                        Eq(true, card.Height > originalHeight); Eq(next.Top - originalNext, card.Height - originalHeight);
                        card.Expanded = false; Eq(card.Bottom + 8, quantum.Top); Eq(quantum.Bottom + 8, next.Top);
                        card.Expanded = true; Eq(card.Bottom + 8, quantum.Top); Eq(quantum.Bottom + 8, next.Top);
                        foreach (PerformanceAction action in PerformanceActionCatalog.All) if (action.Environment) PerformanceTuning.SetState(action.Key, "off");
                        PerformanceTuning.SetState(PerformanceTuning.EnvironmentVbsKey, "off");
                        typeof(PanelForm).GetMethod("SyncPerformanceTuningEnvironment", flags).Invoke(form, null);
                        Eq(originalHeight, card.Height); Eq(originalNext, next.Top);
                        Settings.Save(PerformanceActionCatalog.Quantum, true);
                        PerformanceTuning.SetState(PerformanceActionCatalog.Quantum, "configured");
                        Settings.SaveStr(PerformanceActionCatalog.Quantum + "Boot", PerformanceTuning.BootForTest());
                        typeof(PanelForm).GetMethod("SyncPerformanceTuningEnvironment", flags).Invoke(form, null);
                        Eq(1, quantumToggle.SelectedIndex); Eq(false, PerformanceTuning.EnvironmentBundleEnabled);
                        Eq("pending-reboot", PerformanceTuning.State(PerformanceActionCatalog.Quantum));
                        Eq(originalHeight, card.Height); Eq(originalNext, next.Top);
                        Settings.Save(PerformanceActionCatalog.Quantum, false);
                        Eq(false, parent.IsHandleCreated);
                    }
                }
            } finally { PerformanceTuning.ResetForTest(); PerformanceTuning.BootForTest = null; Lang.Cur = previousLanguage; }
        }
        private static void RunQuantumPickerInteractionTests()
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            Type type = typeof(QuantumProfilePicker).GetNestedType("ProfileList", BindingFlags.NonPublic);
            using (var list = (Control)Activator.CreateInstance(type, flags, null, new object[] { 1 }, null)) {
                int picked = -1, count = 0, cancelled = 0; bool forward = false;
                type.GetField("Picked", flags).SetValue(list, (Action<int>)delegate(int index) { picked = index; count++; });
                type.GetField("Cancelled", flags).SetValue(list, (Action)delegate { cancelled++; });
                type.GetField("Tabbed", flags).SetValue(list, (Action<bool>)delegate(bool value) { forward = value; });
                Func<Keys, bool> key = delegate(Keys value) { return (bool)type.GetMethod("ProcessCmdKey", flags).Invoke(list, new object[] { new Message(), value }); };
                Action<string, int, MouseButtons> mouse = delegate(string method, int y, MouseButtons button) {
                    type.GetMethod(method, flags).Invoke(list, new object[] { new MouseEventArgs(button, 1, Theme.S(20), y, 0) });
                };
                int row = Theme.S(56), header = Theme.S(32), yBackground = header + 2 * row + row / 2;
                mouse("OnMouseMove", yBackground, MouseButtons.None); Eq(0, count);
                Eq(1, (int)type.GetField("selected", flags).GetValue(list));
                Eq(true, key(Keys.Down)); Eq(0, count);
                Eq(true, key(Keys.Enter)); Eq(8, picked); Eq(1, count);
                Eq(true, key(Keys.Escape)); Eq(1, cancelled); Eq(1, count);
                Eq(true, key(Keys.Tab)); Eq(true, forward);
                Eq(true, key(Keys.Shift | Keys.Tab)); Eq(false, forward);
                mouse("OnMouseUp", yBackground, MouseButtons.Left); Eq(1, count);
                mouse("OnMouseDown", header + row / 2, MouseButtons.Left);
                mouse("OnMouseUp", yBackground, MouseButtons.Left); Eq(1, count);
                mouse("OnMouseDown", yBackground, MouseButtons.Left);
                mouse("OnMouseUp", yBackground, MouseButtons.Left); Eq(8, picked); Eq(2, count);
                mouse("OnMouseDown", header / 2, MouseButtons.Left);
                mouse("OnMouseUp", header / 2, MouseButtons.Left); Eq(2, count);
                list.Height = header + 2 * row + Theme.S(28);
                Eq(true, key(Keys.End)); Eq(true, (int)type.GetField("offset", flags).GetValue(list) > 0); Eq(2, count);
                Eq(true, key(Keys.Enter)); Eq(7, picked);
                Eq(true, key(Keys.Home)); Eq(0, (int)type.GetField("offset", flags).GetValue(list));
                var wheel = new HandledMouseEventArgs(MouseButtons.None, 0, Theme.S(20), header + row / 2, -120);
                type.GetMethod("OnMouseWheel", flags).Invoke(list, new object[] { wheel });
                Eq(true, wheel.Handled); Eq(true, (int)type.GetField("offset", flags).GetValue(list) > 0); Eq(3, count);
            }
        }
        private static void RunQuantumPickerPopupLifecycleTests()
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            int created = 0;
            QuantumProfilePicker.PopupCreatedForTest = delegate(ToolStripDropDown popup) { created++; Eq(false, popup.Visible); };
            try {
                using (var form = new Form { ShowInTaskbar = false, Opacity = 0, Size = new Size(640, 480),
                    StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000) })
                using (var picker = new QuantumProfilePicker { Location = new Point(200, 100) }) {
                    form.Controls.Add(picker); picker.SetSilently(1);
                    int selections = 0; picker.SelectionChanged += delegate { selections++; };
                    // Set managed visibility without showing or activating any native window.
                    MethodInfo createControl = typeof(Control).GetMethod("CreateControl", flags, null, new[] { typeof(bool) }, null);
                    createControl.Invoke(form, new object[] { true });
                    createControl.Invoke(picker, new object[] { true });
                    MethodInfo setState = typeof(Control).GetMethod("SetState", flags);
                    Type stateType = setState.GetParameters()[0].ParameterType;
                    object visible = stateType.IsEnum ? Enum.Parse(stateType, "Visible")
                        : typeof(Control).GetField("STATE_VISIBLE", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public).GetRawConstantValue();
                    setState.Invoke(form, new object[] { visible, true });
                    Action click = delegate {
                        typeof(QuantumProfilePicker).GetMethod("OnMouseDown", flags).Invoke(picker, new object[] { new MouseEventArgs(MouseButtons.Left, 1, 12, 12, 0) });
                        typeof(QuantumProfilePicker).GetMethod("OnClick", flags).Invoke(picker, new object[] { EventArgs.Empty });
                        typeof(QuantumProfilePicker).GetMethod("OnMouseUp", flags).Invoke(picker, new object[] { new MouseEventArgs(MouseButtons.Left, 1, 12, 12, 0) });
                        Application.DoEvents();
                    };
                    Func<ToolStripDropDown> popup = delegate { return (ToolStripDropDown)typeof(QuantumProfilePicker).GetField("popup", flags).GetValue(picker); };
                    var host = (DropdownHost)typeof(QuantumProfilePicker).GetField("dropdown", flags).GetValue(picker);
                    Action<Keys> key = delegate(Keys value) {
                        var list = (Control)typeof(QuantumProfilePicker).GetField("list", flags).GetValue(picker);
                        Eq(true, (bool)list.GetType().GetMethod("ProcessCmdKey", flags).Invoke(list, new object[] { new Message(), value }));
                        Application.DoEvents();
                    };
                    click(); Eq(true, host.IsCreated); Eq(false, popup().Visible); Eq(0, selections); Eq(1, created);
                    Eq(true, Screen.FromControl(picker).WorkingArea.Contains(host.LastPlacement));
                    Eq(QuantumProfiles.Description(1), picker.AccessibleDescription);
                    ToolStripDropDown first = popup();
                    click(); Eq(false, host.IsCreated); Eq(true, first.IsDisposed); Eq(0, selections);
                    click(); Eq(true, host.IsCreated);
                    key(Keys.Down); Eq(true, host.IsCreated); Eq(0, selections);
                    key(Keys.Enter); Eq(false, host.IsCreated); Eq(8, picker.SelectedIndex); Eq(1, selections);
                    Eq(QuantumProfiles.Description(8), picker.AccessibleDescription);
                    click(); Eq(true, host.IsCreated);
                    key(Keys.Escape); Eq(false, host.IsCreated); Eq(8, picker.SelectedIndex); Eq(1, selections);
                    click(); Eq(true, host.IsCreated);
                    form.Width += 10; Application.DoEvents(); Eq(false, host.IsCreated);
                    click(); Eq(true, host.IsCreated);
                    picker.Enabled = false; Application.DoEvents(); Eq(false, host.IsCreated);
                    click(); Eq(false, host.IsCreated); Eq(1, selections);
                    picker.Enabled = true; click(); Eq(true, host.IsCreated);
                    picker.Busy = true; Application.DoEvents(); Eq(false, host.IsCreated);
                    click(); Eq(false, host.IsCreated); Eq(1, selections);
                    picker.Busy = false;
                    // An owner click which closed a menu is consumed once, never reopened.
                    typeof(DropdownHost).GetField("closeOnClick", flags).SetValue(host, true);
                    click(); Eq(false, host.IsCreated); Eq(1, selections);
                    // A drag-release without Click clears the handshake so the next click opens.
                    typeof(DropdownHost).GetField("closeOnClick", flags).SetValue(host, true);
                    typeof(QuantumProfilePicker).GetMethod("OnMouseUp", flags).Invoke(picker,
                        new object[] { new MouseEventArgs(MouseButtons.Left, 1, -12, -12, 0) });
                    click(); Eq(true, host.IsCreated); Eq(1, selections);
                    ToolStripDropDown final = popup(); picker.Dispose(); Application.DoEvents();
                    Eq(false, host.IsCreated); Eq(true, final.IsDisposed);
                }
            } finally { QuantumProfilePicker.PopupCreatedForTest = null; }
        }
    }
}
#endif
