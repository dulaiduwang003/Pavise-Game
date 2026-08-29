using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace PaviseApp
{
    // Persistent Windows preferences are explicit user actions, not game-session work.
    internal sealed class AppGpuPreferencesDialog : Form
    {
        private readonly AppGpuPreferenceManager manager;
        private readonly TechListBox list = new TechListBox();
        private readonly Label heading = new Label(), note = new Label(), notice = new Label(), status = new Label();
        private readonly RoundPanel wrap = new RoundPanel();
        private readonly PillButton browse, running, remove, refresh, restore, forget, close;
        private List<AppGpuPreferenceEntry> entries = new List<AppGpuPreferenceEntry>();
        private bool readable;

        internal AppGpuPreferencesDialog(AppGpuPreferenceManager manager)
        {
            if (manager == null) throw new ArgumentNullException("manager");
            this.manager = manager;
            Text = Lang.T("set.apppref");
            Name = "AppGpuPreferencesDialog";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            BackColor = Theme.Bg; ForeColor = Theme.Fg; Font = Theme.UI(9f, false);
            DoubleBuffered = true;
            AutoScaleMode = AutoScaleMode.None;
            AutoScroll = true;
            Size area = Screen.FromPoint(Cursor.Position).WorkingArea.Size;
            ClientSize = new Size(Math.Min(Theme.S(760), Math.Max(320, area.Width - Theme.S(40))),
                Math.Min(Theme.S(590), Math.Max(300, area.Height - Theme.S(40))));

            heading.Text = Text; heading.Font = Theme.UI(14f, true);
            heading.ForeColor = Theme.Fg; heading.BackColor = Theme.Bg;
            heading.MouseDown += DragMove;
            note.Text = Lang.T("set.apppref.n"); note.Font = Theme.UI(9f, false);
            note.ForeColor = Theme.Dim; note.BackColor = Theme.Bg;
            notice.Name = "AppGpuPreferencesNextLaunch";
            notice.Text = Lang.T("apppref.nextlaunch"); notice.Font = Theme.UI(9f, true);
            notice.ForeColor = Theme.Accent; notice.BackColor = Theme.Bg;
            status.Name = "AppGpuPreferencesStatus";
            status.ForeColor = Theme.Dim; status.BackColor = Theme.Bg;
            status.AutoEllipsis = true; status.Font = Theme.UI(8.5f, false);

            browse = Button("apppref.browse", "AppGpuPreferencesBrowse", Browse);
            running = Button("apppref.running", "AppGpuPreferencesRunning", PickRunning);
            remove = Button("apppref.remove", "AppGpuPreferencesRemove", RemoveSelected);
            refresh = Button("apppref.refresh", "AppGpuPreferencesRefresh", delegate { Reload(null); });
            restore = Button("apppref.restoreall", "AppGpuPreferencesRestoreAll", RestoreAll);
            forget = Button("apppref.forget", "AppGpuPreferencesForget", ForgetSelected);
            close = Button("notes.close", "AppGpuPreferencesClose", delegate { Close(); });

            wrap.BackColor = Theme.Bg; wrap.Fill = Theme.Card; wrap.Border = Theme.Stroke;
            wrap.Padding = new Padding(Theme.S(6)); wrap.Radius = Theme.S(12);
            list.Name = "AppGpuPreferencesList";
            Theme.StyleList(list, false);
            list.ItemHeight = Math.Min(255, Theme.S(74));
            list.IntegralHeight = false;
            list.DrawItem += DrawEntry;
            list.SelectedIndexChanged += delegate { UpdateButtons(); };
            wrap.Controls.Add(new TechListScrollHost(list) { Dock = DockStyle.Fill });

            Controls.AddRange(new Control[] { heading, note, notice, browse, running, remove, refresh,
                wrap, status, restore, forget, close });
            MouseDown += DragMove;
            KeyPreview = true;
            KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape) { e.Handled = true; Close(); }
            };
            Resize += delegate { LayoutContent(); };
            LayoutContent();
            Reload(null);
        }

        private static PillButton Button(string textKey, string name, Action click)
        {
            var button = new PillButton(Lang.T(textKey));
            button.Name = name;
            button.Click += delegate { click(); };
            return button;
        }

        private void LayoutContent()
        {
            if (browse == null) return;
            // On a small desktop or a very high DPI setting, scroll the complete
            // dialog instead of overlapping the list with confirmation buttons.
            int canvasWidth = Math.Max(ClientSize.Width, Theme.S(620));
            int canvasHeight = Math.Max(ClientSize.Height, Theme.S(460));
            AutoScrollMinSize = new Size(canvasWidth == ClientSize.Width ? 0 : canvasWidth,
                canvasHeight == ClientSize.Height ? 0 : canvasHeight);
            int originX = AutoScrollPosition.X, originY = AutoScrollPosition.Y;
            int pad = Theme.S(18), gap = Theme.S(8), width = Math.Max(1, canvasWidth - pad * 2);
            int left = pad + originX;
            heading.SetBounds(left, originY + Theme.S(16), width, Theme.S(32));
            int noteHeight = TextRenderer.MeasureText(note.Text, note.Font,
                new Size(width, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height;
            note.SetBounds(left, originY + Theme.S(54), width, noteHeight + Theme.S(4));
            int noticeHeight = Math.Max(Theme.S(24), TextRenderer.MeasureText(notice.Text, notice.Font,
                new Size(width, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height);
            notice.SetBounds(left, note.Bottom + Theme.S(5), width, noticeHeight);
            int toolbarY = notice.Bottom + Theme.S(10), buttonHeight = Theme.S(32);
            int unit = Math.Max(1, (width - gap * 3) / 4);
            PillButton[] toolbar = { browse, running, remove, refresh };
            for (int i = 0; i < toolbar.Length; i++)
                toolbar[i].SetBounds(left + i * (unit + gap), toolbarY,
                    i == toolbar.Length - 1 ? width - i * (unit + gap) : unit, buttonHeight);
            int bottomY = originY + canvasHeight - pad - buttonHeight;
            int closeWidth = Math.Min(Theme.S(104), width / 4);
            int actionWidth = Math.Max(1, (width - closeWidth - gap * 2) / 2);
            restore.SetBounds(left, bottomY, actionWidth, buttonHeight);
            forget.SetBounds(restore.Right + gap, bottomY, actionWidth, buttonHeight);
            close.SetBounds(left + width - closeWidth, bottomY, closeWidth, buttonHeight);
            status.SetBounds(left, bottomY - Theme.S(30), width, Theme.S(24));
            int listTop = toolbarY + buttonHeight + Theme.S(10);
            wrap.SetBounds(left, listTop, width, Math.Max(1, status.Top - listTop - Theme.S(8)));
        }

        private void Reload(string selectedPath)
        {
            if (selectedPath == null && Selected != null) selectedPath = Selected.ExePath;
            List<AppGpuPreferenceEntry> loaded;
            readable = manager.TryGetEntries(out loaded);
            entries = loaded ?? new List<AppGpuPreferenceEntry>();
            list.BeginUpdate();
            try
            {
                list.Items.Clear();
                foreach (AppGpuPreferenceEntry entry in entries) list.Items.Add(entry);
                for (int i = 0; i < entries.Count; i++)
                    if (string.Equals(entries[i].ExePath, selectedPath, StringComparison.OrdinalIgnoreCase))
                    { list.SelectedIndex = i; break; }
            }
            finally { list.EndUpdate(); }
            UpdateButtons();
            string text = !readable ? Lang.T("apppref.journalfailed")
                : !manager.Supported ? Lang.T("apppref.unsupported")
                : entries.Count == 0 ? Lang.T("apppref.empty") : Lang.F("apppref.count", entries.Count);
            SetStatus(text, !readable);
            list.Invalidate();
        }

        private AppGpuPreferenceEntry Selected { get { return list.SelectedItem as AppGpuPreferenceEntry; } }

        private void UpdateButtons()
        {
            browse.Enabled = running.Enabled = readable && manager.Supported;
            remove.Enabled = readable && Selected != null;
            restore.Enabled = readable && entries.Count > 0;
            forget.Enabled = readable && Selected != null && Selected.CanForget;
        }

        private void Browse()
        {
            using (var dialog = new OpenFileDialog())
            {
                dialog.Title = Lang.T("apppref.browse");
                dialog.Filter = Lang.T("apppref.exefilter");
                dialog.Multiselect = true; dialog.CheckFileExists = true;
                if (dialog.ShowDialog(this) == DialogResult.OK) AddPaths(dialog.FileNames);
            }
        }

        private void PickRunning()
        {
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (AppGpuPreferenceEntry entry in entries) known.Add(entry.ExePath);
            using (var dialog = new RunningPickerDialog(known, Lang.T("apppref.add"),
                Lang.T("apppref.pick.title"), Lang.T("apppref.pick.sub")))
                if (dialog.ShowDialog(this) == DialogResult.OK) AddPaths(dialog.Selected);
        }

        private void AddPaths(IEnumerable<string> paths)
        {
            int added = 0;
            AppGpuPreferenceResult last = AppGpuPreferenceResult.Success;
            string selected = null;
            foreach (string path in paths)
            {
                AppGpuPreferenceChange change;
                AppGpuPreferenceResult prepared = manager.Prepare(path, out change);
                if (prepared == AppGpuPreferenceResult.AlreadyPresent) { last = prepared; continue; }
                if (prepared != AppGpuPreferenceResult.Success && prepared != AppGpuPreferenceResult.NeedsConfirmation)
                { last = prepared; break; }
                if (change == null) { last = AppGpuPreferenceResult.ReadFailed; break; }
                bool confirmed = !change.NeedsConfirmation || PaviseDialog.Confirm(this,
                    Lang.T("set.apppref"), Lang.F("apppref.replace.confirm", change.Name,
                        PreferenceText(change.OriginalPreference), change.ExePath), DlgKind.Warn);
                if (!confirmed) break;
                last = IrqMutationBoundary.Run<AppGpuPreferenceResult>(
                    delegate { return manager.Apply(change, true); });
                if (last != AppGpuPreferenceResult.Success && last != AppGpuPreferenceResult.AlreadyPresent) break;
                if (last == AppGpuPreferenceResult.Success) added++;
                selected = change.ExePath;
            }
            Reload(selected);
            if (last != AppGpuPreferenceResult.Success && last != AppGpuPreferenceResult.AlreadyPresent)
                ShowResult(last);
            else if (added > 0) SetStatus(Lang.F("apppref.added", added), false);
            else if (last == AppGpuPreferenceResult.AlreadyPresent) ShowResult(last);
        }

        private void RemoveSelected()
        {
            AppGpuPreferenceEntry entry = Selected;
            if (entry == null || !PaviseDialog.Confirm(this, Lang.T("apppref.remove"),
                Lang.F("apppref.remove.confirm", entry.Name), DlgKind.Warn)) return;
            AppGpuPreferenceResult result = IrqMutationBoundary.Run<AppGpuPreferenceResult>(
                delegate { return manager.Remove(entry.ExePath); });
            Reload(null);
            ShowResult(result);
        }

        private void RestoreAll()
        {
            if (!PaviseDialog.Confirm(this, Lang.T("apppref.restoreall"),
                Lang.T("apppref.restoreall.confirm"), DlgKind.Warn)) return;
            bool restored = IrqMutationBoundary.Run<bool>(manager.RestoreAll);
            Reload(null);
            ShowResult(restored ? AppGpuPreferenceResult.Success : AppGpuPreferenceResult.RecoveryPending);
        }

        private void ForgetSelected()
        {
            AppGpuPreferenceEntry entry = Selected;
            if (entry == null || !entry.CanForget || !PaviseDialog.Confirm(this, Lang.T("apppref.forget"),
                Lang.F("apppref.forget.confirm", entry.Name), DlgKind.Warn)) return;
            AppGpuPreferenceResult result = manager.Forget(entry.ExePath);
            Reload(null);
            ShowResult(result);
        }

        private void SetStatus(string text, bool error)
        {
            status.Text = text;
            status.ForeColor = error ? Theme.Danger : Theme.Dim;
        }

        private void ShowResult(AppGpuPreferenceResult result)
        {
            string key;
            switch (result)
            {
                case AppGpuPreferenceResult.Success: key = "apppref.done"; break;
                case AppGpuPreferenceResult.AlreadyPresent: key = "apppref.present"; break;
                case AppGpuPreferenceResult.NeedsConfirmation: key = "apppref.needsconfirmation"; break;
                case AppGpuPreferenceResult.Unsupported: key = "apppref.unsupported"; break;
                case AppGpuPreferenceResult.InvalidPath: key = "apppref.invalidpath"; break;
                case AppGpuPreferenceResult.Changed: key = "apppref.changed"; break;
                case AppGpuPreferenceResult.Busy: key = "apppref.busy"; break;
                case AppGpuPreferenceResult.ReadFailed: key = "apppref.readfailed"; break;
                case AppGpuPreferenceResult.WriteFailed: key = "apppref.writefailed"; break;
                case AppGpuPreferenceResult.JournalFailed: key = "apppref.journalfailed"; break;
                case AppGpuPreferenceResult.NotFound: key = "apppref.notfound"; break;
                default: key = "apppref.pending"; break;
            }
            SetStatus(Lang.T(key), result != AppGpuPreferenceResult.Success
                && result != AppGpuPreferenceResult.AlreadyPresent);
        }

        internal static string PreferenceText(string preference)
        {
            if (preference == "1") return Lang.T("apppref.lowpower");
            if (preference == "2") return Lang.T("apppref.highperformance");
            if (string.IsNullOrEmpty(preference) || preference == "0") return Lang.T("apppref.systemdefault");
            return Lang.T("apppref.other");
        }

        internal static string StateText(AppGpuPreferenceEntry entry)
        {
            if (entry.ReadFailed) return Lang.T("apppref.state.unreadable");
            switch (entry.State)
            {
                case AppGpuPreferenceState.Owned: return Lang.T("apppref.state.owned");
                case AppGpuPreferenceState.AlreadyLowPower: return Lang.T("apppref.state.existing");
                case AppGpuPreferenceState.ExternalChange: return Lang.T("apppref.state.external");
                case AppGpuPreferenceState.PendingApply: return Lang.T("apppref.state.apply");
                case AppGpuPreferenceState.PendingRestore: return Lang.T("apppref.state.restore");
                default: return Lang.T("apppref.state.cleanup");
            }
        }

        private void DrawEntry(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= list.Items.Count) return;
            var entry = list.Items[e.Index] as AppGpuPreferenceEntry;
            if (entry == null) return;
            Rectangle bounds = e.Bounds;
            bool selected = (e.State & DrawItemState.Selected) != 0;
            using (var fill = new SolidBrush(selected ? Theme.CardHover : Theme.Card))
                e.Graphics.FillRectangle(fill, bounds);
            int pad = Theme.S(12);
            int width = Math.Max(1, bounds.Width - pad * 2);
            var flags = TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
            TextRenderer.DrawText(e.Graphics, entry.Name, Theme.UI(10f, true),
                new Rectangle(bounds.X + pad, bounds.Y + Theme.S(6), width, Theme.S(21)), Theme.Fg, flags);
            TextRenderer.DrawText(e.Graphics, entry.ExePath, Theme.UI(8.5f, false),
                new Rectangle(bounds.X + pad, bounds.Y + Theme.S(29), width, Theme.S(18)), Theme.Dim,
                TextFormatFlags.PathEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            Color ink = entry.State == AppGpuPreferenceState.Owned && !entry.ReadFailed ? Theme.Accent : Theme.Dim;
            TextRenderer.DrawText(e.Graphics, StateText(entry), Theme.UI(8.5f, false),
                new Rectangle(bounds.X + pad, bounds.Y + Theme.S(51), width, Theme.S(18)), ink, flags);
            using (var line = new Pen(Theme.Stroke))
                e.Graphics.DrawLine(line, bounds.Left + pad, bounds.Bottom - 1, bounds.Right - pad, bounds.Bottom - 1);
        }

        private void DragMove(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            Native.ReleaseCapture();
            Native.SendMessage(Handle, Native.WM_NCLBUTTONDOWN, (IntPtr)Native.HT_CAPTION, IntPtr.Zero);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var pen = new Pen(Theme.Accent))
                e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }

        protected override void OnShown(EventArgs e) { base.OnShown(e); Fx.EnterForm(this); }
        protected override void OnHandleCreated(EventArgs e)
        { base.OnHandleCreated(e); Native.Dark(this); Native.RoundCorners(Handle); }
    }
}
