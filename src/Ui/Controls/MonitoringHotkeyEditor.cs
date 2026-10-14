using System;
using System.Drawing;
using System.Windows.Forms;

namespace PaviseApp
{
    internal sealed class MonitoringHotkeyEditor : DBPanel
    {
        private readonly Func<string> shortcutText, statusText;
        private readonly Func<Keys, bool> save;
        private readonly Func<bool> clear, reset;
        private readonly Func<IDisposable> suspend;
        private readonly Label title, status;
        private readonly ToolTip tips = new ToolTip();
        private readonly CaptureButton capture;
        private readonly PillButton clearButton, resetButton;
        private IDisposable captureLease;
        private bool layingOut, failed;
        private int captureGeneration;

        internal MonitoringHotkeyEditor(Func<string> shortcutText, Func<string> statusText,
            Func<Keys, bool> save, Func<bool> clear, Func<bool> reset, Func<IDisposable> suspend)
        {
            this.shortcutText = shortcutText; this.statusText = statusText;
            this.save = save; this.clear = clear; this.reset = reset; this.suspend = suspend;
            BackColor = Theme.Card;
            title = LabelFor("monitoringHotkeyTitle", 10f, true, Theme.Fg);
            status = LabelFor("monitoringHotkeyStatus", 8.5f, false, Theme.Dim);
            title.Text = TextFor("显示 / 隐藏快捷键", "Show / hide shortcut");
            capture = new CaptureButton { Name = "monitoringHotkeyCapture", Bg = Theme.Card };
            capture.Click += delegate { if (capture.Capturing) CancelCapture(); else BeginCapture(); };
            capture.Candidate = SaveShortcut;
            capture.Cancelled = CancelCapture;
            clearButton = new PillButton(TextFor("清除", "Clear")) { Name = "monitoringHotkeyClear", Bg = Theme.Card };
            resetButton = new PillButton(TextFor("恢复默认", "Reset default")) { Name = "monitoringHotkeyReset", Bg = Theme.Card };
            clearButton.Click += delegate { Change(clear); };
            resetButton.Click += delegate { Change(reset); };
            Controls.Add(title); Controls.Add(status);
            Controls.Add(capture); Controls.Add(clearButton); Controls.Add(resetButton);
            RefreshState();
        }

        private static string TextFor(string zh, string en) { return AutomaticPolicy.Text(zh, en); }
        private static Label LabelFor(string name, float size, bool bold, Color color)
        {
            return new Label { Name = name, Font = Theme.UI(size, bold), ForeColor = color,
                BackColor = Color.Transparent, AutoEllipsis = true, UseMnemonic = false };
        }

        internal void RefreshState()
        {
            if (IsDisposed || Disposing) return;
            if (!capture.Capturing) capture.Text = shortcutText();
            capture.AccessibleName = title.Text + ": " + capture.Text;
            capture.AccessibleDescription = TextFor("点击后按组合键，Esc 取消，Tab 离开。", "Click and press a shortcut. Esc cancels; Tab leaves the field.");
            string help = TextFor("仅切换悬浮栏显示，监测继续。默认 Ctrl+F10。", "Only toggles the overlay; monitoring continues. Default: Ctrl+F10.");
            title.AccessibleDescription = help;
            tips.SetToolTip(title, help); tips.SetToolTip(capture, help + "\n" + capture.AccessibleDescription);
            status.Text = capture.Capturing && !failed
                ? TextFor("请按组合键 · Esc 取消 · Tab 离开", "Press a shortcut · Esc cancels · Tab leaves the field")
                : statusText();
            status.ForeColor = failed ? Theme.Warning : Theme.Dim;
            status.AccessibleDescription = status.Text;
            tips.SetToolTip(status, status.Text + "\n" + help);
            Relayout();
        }

        internal void RefreshFromBackend()
        {
            if (IsDisposed || Disposing || !IsHandleCreated) return;
            if (!InvokeRequired) { RefreshState(); return; }
            try { BeginInvoke((Action)RefreshState); }
            catch (InvalidOperationException) { }
        }

        internal void CaptureRegisteredShortcut(Keys shortcut)
        {
            if (IsDisposed || Disposing || !IsHandleCreated || !capture.Capturing) return;
            int expected = captureGeneration;
            Action apply = delegate {
                if (!IsDisposed && !Disposing && expected == captureGeneration) SaveShortcut(shortcut);
            };
            if (!InvokeRequired) { apply(); return; }
            try { BeginInvoke(apply); } catch (InvalidOperationException) { }
        }

        private void BeginCapture()
        {
            if (!Enabled || IsDisposed || Disposing || capture.Capturing) return;
            capture.Focus();
            captureLease = suspend();
            captureGeneration++;
            capture.Capturing = true;
            capture.Text = TextFor("请按组合键…", "Press a shortcut…");
            failed = false;
            RefreshState();
        }

        internal void CancelCapture()
        {
            if (capture == null) return;
            captureGeneration++;
            capture.Capturing = false;
            IDisposable lease = captureLease; captureLease = null;
            if (lease != null) lease.Dispose();
            RefreshState();
        }

        private void SaveShortcut(Keys shortcut)
        {
            if (!Enabled || !capture.Capturing) return;
            failed = !save(shortcut);
            if (!failed) CancelCapture();
            else RefreshState();
        }

        private void Change(Func<bool> operation)
        {
            if (!Enabled || IsDisposed || Disposing) return;
            CancelCapture();
            failed = !operation();
            RefreshState();
        }

        private void Relayout()
        {
            if (layingOut || resetButton == null) return;
            layingOut = true;
            try {
                int gap = Theme.S(8), row = Theme.S(32), width = Math.Max(1, ClientSize.Width);
                int clearWidth = Theme.S(72), resetWidth = Theme.S(122), captureWidth = Theme.S(212);
                int controlsWidth = captureWidth + clearWidth + resetWidth + gap * 2;
                int titleWidth = TextRenderer.MeasureText(title.Text, title.Font).Width + gap;
                bool wrap = titleWidth + controlsWidth + gap > width;
                title.SetBounds(0, 0, wrap ? width : width - controlsWidth - gap, row);
                int x = wrap ? 0 : width - controlsWidth, y = wrap ? row + gap : 0;
                if (controlsWidth > width) {
                    capture.SetBounds(0, y, width, row); y += row + gap;
                    clearButton.SetBounds(0, y, Math.Min(clearWidth, width), row);
                    resetButton.SetBounds(Math.Min(clearWidth + gap, width), y,
                        Math.Max(1, Math.Min(resetWidth, width - clearWidth - gap)), row);
                } else {
                    capture.SetBounds(x, y, captureWidth, row); x += captureWidth + gap;
                    clearButton.SetBounds(x, y, clearWidth, row); x += clearWidth + gap;
                    resetButton.SetBounds(x, y, resetWidth, row);
                }
                y += row + Theme.S(6);
                status.SetBounds(0, y, width, Theme.S(20));
                Height = status.Bottom;
            } finally { layingOut = false; }
        }

        protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); Relayout(); }
        protected override void OnEnabledChanged(EventArgs e)
        { base.OnEnabledChanged(e); if (!Enabled) CancelCapture(); }
        protected override void OnVisibleChanged(EventArgs e)
        { base.OnVisibleChanged(e); if (!Visible) CancelCapture(); }
        protected override void Dispose(bool disposing)
        { if (disposing) { CancelCapture(); tips.Dispose(); } base.Dispose(disposing); }

#if PAVISE_SELFTEST
        internal bool CapturingForTest { get { return capture.Capturing; } }
        internal bool KeyForTest(Keys value) { return capture.HandleCaptureKey(value); }
        internal void BlurForTest() { capture.BlurForTest(); }
#endif

        private sealed class CaptureButton : PillButton
        {
            internal bool Capturing;
            internal Action<Keys> Candidate;
            internal Action Cancelled;
            internal CaptureButton() : base("") { }
            internal bool HandleCaptureKey(Keys value)
            {
                if (!Capturing || !CanInteract) return false;
                Keys code = value & Keys.KeyCode;
                if (code == Keys.Escape || code == Keys.Tab) {
                    if (Cancelled != null) Cancelled();
                    return code == Keys.Escape;
                }
                if (code == Keys.None || code == Keys.ControlKey || code == Keys.ShiftKey || code == Keys.Menu
                    || code == Keys.LControlKey || code == Keys.RControlKey || code == Keys.LShiftKey || code == Keys.RShiftKey
                    || code == Keys.LMenu || code == Keys.RMenu || code == Keys.LWin || code == Keys.RWin) return true;
                if (Candidate != null) Candidate(value);
                return true;
            }
            protected override bool ProcessCmdKey(ref Message message, Keys keyData)
            { return HandleCaptureKey(keyData) || base.ProcessCmdKey(ref message, keyData); }
            protected override void OnKeyDown(KeyEventArgs e)
            {
                if (HandleCaptureKey(e.KeyData)) { e.SuppressKeyPress = true; e.Handled = true; return; }
                base.OnKeyDown(e);
            }
            protected override void OnLostFocus(EventArgs e)
            { if (Capturing && Cancelled != null) Cancelled(); base.OnLostFocus(e); }
#if PAVISE_SELFTEST
            internal void BlurForTest() { OnLostFocus(EventArgs.Empty); }
#endif
        }
    }
}
