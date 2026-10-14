using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace PaviseApp
{
    internal interface IMonitoringHotkeyBackend : IDisposable
    {
        bool Register(int id, Keys shortcut);
        bool Unregister(int id);
    }

    internal enum MonitoringHotkeyIssue { None, Invalid, Conflict, SaveFailed, Unavailable }

    internal sealed class MonitoringHotkeyBinding : IDisposable
    {
        private readonly IMonitoringHotkeyBackend backend;
        private readonly Func<Keys, bool> save;
        private readonly Func<bool> enabled;
        private readonly Action toggle;
        private int registeredId, captureDepth;
        private bool disposed;
        internal Keys Shortcut { get; private set; }
        internal MonitoringHotkeyIssue Issue { get; private set; }
        internal bool Registered { get { return registeredId != 0; } }
        internal Action<Keys> Captured;

        internal MonitoringHotkeyBinding(IMonitoringHotkeyBackend backend, Keys initial,
            Func<Keys, bool> save, Func<bool> enabled, Action toggle)
        {
            this.backend = backend; this.save = save; this.enabled = enabled; this.toggle = toggle;
            Shortcut = Valid(initial) ? initial : MonitoringHotkey.DefaultShortcut;
            if (Shortcut != Keys.None)
            {
                if (backend.Register(1, Shortcut)) registeredId = 1;
                else Issue = MonitoringHotkeyIssue.Conflict;
            }
        }

        internal static bool Valid(Keys value)
        {
            if (value == Keys.None) return true;
            if ((value & ~(Keys.KeyCode | Keys.Control | Keys.Alt | Keys.Shift)) != 0) return false;
            Keys key = value & Keys.KeyCode;
            bool function = key >= Keys.F1 && key <= Keys.F24 && key != Keys.F12;
            bool ordinary = (key >= Keys.A && key <= Keys.Z) || (key >= Keys.D0 && key <= Keys.D9)
                || (key >= Keys.NumPad0 && key <= Keys.NumPad9) || key == Keys.Home || key == Keys.End
                || key == Keys.PageUp || key == Keys.PageDown || key == Keys.Insert;
            return function || (ordinary && (value & (Keys.Control | Keys.Alt)) != 0);
        }

        internal bool TrySet(Keys next)
        {
            if (disposed) { Issue = MonitoringHotkeyIssue.Unavailable; return false; }
            if (!Valid(next)) { Issue = MonitoringHotkeyIssue.Invalid; return false; }
            if (next == Shortcut && (Registered || next == Keys.None))
            { Issue = MonitoringHotkeyIssue.None; return true; }
            int nextId = registeredId == 1 ? 2 : 1;
            // 新组合注册成功后再释放旧组合，冲突不会丢掉原快捷键。
            if (next != Keys.None && !backend.Register(nextId, next))
            { Issue = MonitoringHotkeyIssue.Conflict; return false; }
            if (!save(next))
            {
                if (next != Keys.None) backend.Unregister(nextId);
                Issue = MonitoringHotkeyIssue.SaveFailed; return false;
            }
            if (registeredId != 0) backend.Unregister(registeredId);
            registeredId = next == Keys.None ? 0 : nextId;
            Shortcut = next; Issue = MonitoringHotkeyIssue.None;
            return true;
        }

        internal void Dispatch(int id, Keys shortcut)
        {
            if (disposed || registeredId == 0 || id != registeredId || shortcut != Shortcut) return;
            if (Volatile.Read(ref captureDepth) != 0)
            { if (Captured != null) Captured(shortcut); return; }
            if (!enabled()) return;
            toggle();
        }

        internal IDisposable SuspendCapture()
        { Interlocked.Increment(ref captureDepth); return new CaptureLease(this); }

        private sealed class CaptureLease : IDisposable
        {
            private MonitoringHotkeyBinding owner;
            internal CaptureLease(MonitoringHotkeyBinding value) { owner = value; }
            public void Dispose()
            {
                MonitoringHotkeyBinding value = Interlocked.Exchange(ref owner, null);
                if (value != null) Interlocked.Decrement(ref value.captureDepth);
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (registeredId != 0) backend.Unregister(registeredId);
            registeredId = 0;
            backend.Dispose();
        }
    }

    internal static class MonitoringHotkey
    {
        internal const string SettingsKey = "PerfMonitorHotkeyV1";
        internal const Keys DefaultShortcut = Keys.Control | Keys.F10;
        private static MonitoringHotkeyBinding binding;
        private static MonitoringHotkeyIssue startupIssue;
        internal static event EventHandler Changed;
        internal static event Action<Keys> Captured;
        internal static Keys Shortcut { get { return binding == null ? ReadShortcut() : binding.Shortcut; } }
        internal static string ShortcutText { get { return Format(Shortcut); } }
        internal static string Format(Keys value)
        {
            if (value == Keys.None) return AutomaticPolicy.Text("未设置", "Not set");
            return ((value & Keys.Control) != 0 ? "Ctrl + " : "")
                + ((value & Keys.Alt) != 0 ? "Alt + " : "")
                + ((value & Keys.Shift) != 0 ? "Shift + " : "") + (value & Keys.KeyCode).ToString();
        }
        internal static string Status
        {
            get
            {
                MonitoringHotkeyIssue issue = binding == null ? startupIssue : binding.Issue;
                if (issue == MonitoringHotkeyIssue.Invalid)
                    return AutomaticPolicy.Text("请选择功能键，或 Ctrl／Alt 与字母、数字的组合；F12 由系统保留。", "Use a function key or Ctrl/Alt with a letter or number. F12 is reserved by Windows.");
                if (issue == MonitoringHotkeyIssue.Conflict)
                    return AutomaticPolicy.Text("快捷键注册失败，可能已被其他程序占用；请更换组合。", "Could not register the shortcut; another app may be using it. Choose another combination.");
                if (issue == MonitoringHotkeyIssue.SaveFailed)
                    return AutomaticPolicy.Text("快捷键未能保存，原设置保留。", "Could not save the shortcut. The previous setting was retained.");
                if (issue == MonitoringHotkeyIssue.Unavailable)
                    return AutomaticPolicy.Text("快捷键服务尚未就绪。", "The shortcut service is not ready.");
                if (Shortcut == Keys.None) return AutomaticPolicy.Text("快捷键已关闭。", "Shortcut disabled.");
                if (binding == null) return AutomaticPolicy.Text("快捷键服务尚未就绪。", "The shortcut service is not ready.");
                if (!MonitoringPreferences.Enabled) return AutomaticPolicy.Text("启用性能监测后生效。", "Available when performance monitoring is enabled.");
                return MonitoringPreferences.OverlayVisible
                    ? AutomaticPolicy.Text("悬浮栏允许显示，按快捷键隐藏。", "Overlay visible when connected. Press the shortcut to hide it.")
                    : AutomaticPolicy.Text("悬浮栏已隐藏，按快捷键恢复显示。", "Overlay hidden. Press the shortcut to show it.");
            }
        }
        private static Keys ReadShortcut()
        {
            int value;
            return int.TryParse(Settings.LoadStr(SettingsKey, ((int)DefaultShortcut).ToString(CultureInfo.InvariantCulture)), out value)
                && MonitoringHotkeyBinding.Valid((Keys)value) ? (Keys)value : DefaultShortcut;
        }
        internal static void Start()
        {
            if (binding != null) return;
            HotkeyWindow window = null;
            try
            {
                window = new HotkeyWindow();
                binding = new MonitoringHotkeyBinding(window, ReadShortcut(),
                    delegate(Keys value) { return Settings.SaveStr(SettingsKey, ((int)value).ToString(CultureInfo.InvariantCulture)); },
                    delegate { return MonitoringPreferences.Enabled; }, MonitoringPreferences.ToggleOverlayVisibility);
                window.Receive = binding.Dispatch;
                binding.Captured = delegate(Keys value) { Action<Keys> handler = Captured; if (handler != null) handler(value); };
                MonitoringPreferences.VisibilityChanged += OnPreferencesChanged;
                MonitoringPreferences.Changed += OnPreferencesChanged;
                startupIssue = MonitoringHotkeyIssue.None;
            }
            catch
            {
                if (window != null) window.Dispose();
                binding = null; startupIssue = MonitoringHotkeyIssue.Unavailable;
            }
            Notify();
        }
        internal static void Stop()
        {
            MonitoringPreferences.VisibilityChanged -= OnPreferencesChanged;
            MonitoringPreferences.Changed -= OnPreferencesChanged;
            if (binding != null) { binding.Dispose(); binding = null; }
        }
        internal static bool TrySet(Keys value)
        {
            bool result = binding != null && binding.TrySet(value);
            if (binding == null) startupIssue = MonitoringHotkeyIssue.Unavailable;
            Notify(); return result;
        }
        internal static bool Clear() { return TrySet(Keys.None); }
        internal static bool ResetDefault() { return TrySet(DefaultShortcut); }
        internal static IDisposable SuspendCapture()
        { return binding == null ? (IDisposable)new EmptyLease() : binding.SuspendCapture(); }
        private sealed class EmptyLease : IDisposable { public void Dispose() { } }
        private static void OnPreferencesChanged(object sender, EventArgs args) { Notify(); }
        private static void Notify()
        { EventHandler handler = Changed; if (handler != null) handler(null, EventArgs.Empty); }

        private sealed class HotkeyWindow : NativeWindow, IMonitoringHotkeyBackend
        {
            internal Action<int, Keys> Receive;
            internal HotkeyWindow()
            { CreateHandle(new CreateParams { Parent = new IntPtr(-3), Caption = "Pavise.MonitoringHotkey" }); }
            public bool Register(int id, Keys shortcut)
            {
                uint modifiers = 0x4000;
                if ((shortcut & Keys.Alt) != 0) modifiers |= 1;
                if ((shortcut & Keys.Control) != 0) modifiers |= 2;
                if ((shortcut & Keys.Shift) != 0) modifiers |= 4;
                return RegisterHotKey(Handle, id, modifiers, (uint)(shortcut & Keys.KeyCode));
            }
            public bool Unregister(int id) { return UnregisterHotKey(Handle, id); }
            protected override void WndProc(ref Message message)
            {
                if (message.Msg == 0x0312 && Receive != null)
                {
                    long data = message.LParam.ToInt64();
                    Keys value = (Keys)((data >> 16) & 0xffff);
                    if ((data & 1) != 0) value |= Keys.Alt;
                    if ((data & 2) != 0) value |= Keys.Control;
                    if ((data & 4) != 0) value |= Keys.Shift;
                    Receive(message.WParam.ToInt32(), value);
                }
                base.WndProc(ref message);
            }
            public void Dispose() { Receive = null; if (Handle != IntPtr.Zero) DestroyHandle(); }
            [DllImport("user32.dll", SetLastError = true)]
            private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
            [DllImport("user32.dll", SetLastError = true)]
            private static extern bool UnregisterHotKey(IntPtr window, int id);
        }
    }
}
