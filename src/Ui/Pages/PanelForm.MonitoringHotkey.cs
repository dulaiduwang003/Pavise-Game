using System;
using System.Windows.Forms;

namespace PaviseApp
{
    internal partial class PanelForm
    {
        private Control monitoringHotkeyPanel;
        private MonitoringHotkeyEditor monitoringHotkeyEditor;

        private void BuildMonitoringHotkey(Control parent)
        {
            var card = MakeConsolePanel(parent, 6, 0, ScrollContentW, 80, false);
            card.Name = "monitoringHotkeyCard"; monitoringHotkeyPanel = card;
            var editor = new MonitoringHotkeyEditor(
                delegate { return MonitoringHotkey.ShortcutText; }, delegate { return MonitoringHotkey.Status; },
                MonitoringHotkey.TrySet, MonitoringHotkey.Clear, MonitoringHotkey.ResetDefault, MonitoringHotkey.SuspendCapture);
            editor.Name = "monitoringHotkeyEditor"; monitoringHotkeyEditor = editor;
            editor.SetBounds(Theme.S(18), Theme.S(11), Theme.S(ScrollContentW - 36), editor.Height);
            card.Controls.Add(editor);
            EventHandler changed = delegate { editor.RefreshFromBackend(); };
            Action<Keys> captured = editor.CaptureRegisteredShortcut;
            MonitoringHotkey.Changed += changed;
            MonitoringHotkey.Captured += captured;
            editor.Disposed += delegate { MonitoringHotkey.Changed -= changed; MonitoringHotkey.Captured -= captured; };
            editor.SizeChanged += delegate { if (!card.IsDisposed) card.Height = editor.Bottom + Theme.S(11); };
            card.Height = editor.Bottom + Theme.S(11);
        }

        private void SyncMonitoringHotkey(bool available)
        {
            if (monitoringHotkeyEditor == null || monitoringHotkeyEditor.IsDisposed) return;
            monitoringHotkeyPanel.Enabled = available;
            monitoringHotkeyEditor.RefreshState();
        }
    }
}
