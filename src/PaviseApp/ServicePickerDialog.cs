using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace PaviseApp
{
    internal sealed class ServicePickerDialog : Form
    {
        internal sealed class Entry
        {
            internal readonly ServiceListEntry Service;
            internal bool Checked;
            internal Entry(ServiceListEntry service) { Service = service; }
            public override string ToString()
            {
                return Service.Name + " · " + (Service.DisplayName ?? "") + " · " + StateText(Service.State);
            }
        }

        private readonly HashSet<string> known;
        private readonly List<Entry> all = new List<Entry>();
        private readonly List<Entry> shown = new List<Entry>();
        private readonly TextBox search = new TextBox();
        private readonly ListBox list = new TechListBox();
        private readonly Label status = new Label();
        private readonly PillButton confirm;
        private readonly object scanGate = new object();
        private bool closed, scanStarted;
        private bool scanFinished, scanFailed;
        private int generation;
        public readonly List<string> Selected = new List<string>();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr parameter, string text);

        internal ServicePickerDialog(HashSet<string> alreadyListed)
        {
            known = alreadyListed == null ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(alreadyListed, StringComparer.OrdinalIgnoreCase);
            Text = Lang.T("white.service.title");
            Name = "ServicePicker";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            ClientSize = new Size(Theme.S(700), Theme.S(584));
            BackColor = Theme.Bg; ForeColor = Theme.Fg; Font = Theme.UI(9.5f, false);
            var title = new Label { Text = Text, ForeColor = Theme.Fg, BackColor = Theme.Bg, Font = Theme.UI(14f, true) };
            title.SetBounds(Theme.S(22), Theme.S(18), Theme.S(600), Theme.S(30));
            title.MouseDown += DragMove;
            var close = new Label { Text = "✕", ForeColor = Theme.Dim, BackColor = Theme.Bg, TextAlign = ContentAlignment.MiddleCenter, Cursor = Cursors.Hand };
            close.SetBounds(Theme.S(662), Theme.S(12), Theme.S(26), Theme.S(26));
            close.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            var description = new Label { Text = Lang.T("white.service.scope"), ForeColor = Theme.Dim, BackColor = Theme.Bg, Font = Theme.UI(8.5f, false) };
            description.SetBounds(Theme.S(22), Theme.S(54), Theme.S(656), Theme.S(44));
            search.Name = "ServiceSearch"; search.AccessibleName = Lang.T("white.service.search");
            search.SetBounds(Theme.S(22), Theme.S(108), Theme.S(656), Theme.S(30));
            search.BackColor = Theme.Card; search.ForeColor = Theme.Fg; search.BorderStyle = BorderStyle.FixedSingle;
            search.HandleCreated += delegate { SendMessage(search.Handle, 0x1501, new IntPtr(1), Lang.T("white.service.search")); };
            search.TextChanged += delegate { ApplyFilter(); };
            var surface = new RoundPanel { BackColor = Theme.Bg, Fill = Theme.Card, Border = Theme.Stroke, Radius = Theme.S(12), Padding = new Padding(Theme.S(8)) };
            surface.SetBounds(Theme.S(22), Theme.S(150), Theme.S(656), Theme.S(338));
            list.Name = "ServiceList"; list.AccessibleName = Text; list.Dock = DockStyle.Fill;
            Theme.StyleList(list, false); list.ItemHeight = Math.Min(255, Theme.S(52));
            list.DrawItem += DrawEntry;
            list.MouseDown += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) Toggle(list.IndexFromPoint(e.Location));
            };
            list.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Space) { Toggle(list.SelectedIndex); e.Handled = true; e.SuppressKeyPress = true; }
            };
            surface.Controls.Add(new TechListScrollHost((TechListBox)list) { Dock = DockStyle.Fill });
            status.SetBounds(Theme.S(22), Theme.S(496), Theme.S(656), Theme.S(24));
            status.ForeColor = Theme.Dim; status.BackColor = Theme.Bg; status.Font = Theme.UI(8.5f, false);
            status.Text = Lang.T("white.service.busy");
            confirm = new PillButton(Lang.T("white.pick.add"), BtnKind.Primary) { Enabled = false };
            confirm.SetBounds(Theme.S(478), Theme.S(536), Theme.S(200), Theme.S(36));
            confirm.Click += delegate { ConfirmSelection(); };
            var cancel = new PillButton(Lang.T("btn.cancel"));
            cancel.SetBounds(Theme.S(262), Theme.S(536), Theme.S(200), Theme.S(36));
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            Controls.AddRange(new Control[] { title, close, description, search, surface, status, confirm, cancel });
            MouseDown += DragMove;
            KeyPreview = true;
            KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); }
            };
            Shown += delegate { BeginScan(); };
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Native.RoundCorners(Handle); }
        protected override void OnShown(EventArgs e) { base.OnShown(e); Fx.EnterForm(this); }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var pen = new Pen(Theme.Accent)) e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }
        private void DragMove(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            Native.ReleaseCapture(); Native.SendMessage(Handle, 161, new IntPtr(2), IntPtr.Zero);
        }
        private void CancelScan() { lock (scanGate) { closed = true; generation++; } }
        private bool Canceled(int expected) { lock (scanGate) return closed || generation != expected; }
        protected override void OnFormClosed(FormClosedEventArgs e) { CancelScan(); base.OnFormClosed(e); }
        protected override void Dispose(bool disposing) { if (disposing) CancelScan(); base.Dispose(disposing); }

        private void BeginScan()
        {
            int expected;
            lock (scanGate)
            {
                if (closed || scanStarted) return;
                scanStarted = true; expected = ++generation;
            }
            try
            {
                if (!ThreadPool.QueueUserWorkItem(delegate
                {
                    List<ServiceListEntry> services = null;
                    try { services = ServiceCatalog.ListServices(() => Canceled(expected)); }
                    catch { }
                    QueueScanResult(expected, services);
                })) QueueScanResult(expected, null);
            }
            catch { QueueScanResult(expected, null); }
        }

        private void QueueScanResult(int expected, List<ServiceListEntry> services)
        {
            lock (scanGate)
            {
                if (closed || generation != expected) return;
                try { BeginInvoke((MethodInvoker)delegate { ApplyScanResult(expected, services); }); }
                catch (InvalidOperationException) { }
            }
        }

        internal static List<Entry> PrepareEntries(IEnumerable<ServiceListEntry> services, HashSet<string> exclude)
        {
            var result = new List<Entry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ServiceListEntry service in services)
            {
                if (service == null || string.IsNullOrEmpty(service.Name) || !seen.Add(service.Name)
                    || (exclude != null && exclude.Contains(service.Name))) continue;
                result.Add(new Entry(service));
            }
            result.Sort(delegate(Entry first, Entry second)
            {
                int order = StringComparer.OrdinalIgnoreCase.Compare(first.Service.DisplayName, second.Service.DisplayName);
                return order != 0 ? order : StringComparer.OrdinalIgnoreCase.Compare(first.Service.Name, second.Service.Name);
            });
            return result;
        }

        private void ApplyScanResult(int expected, List<ServiceListEntry> services)
        {
            if (Canceled(expected) || IsDisposed || Disposing) return;
            scanFinished = true; scanFailed = services == null;
            all.Clear(); shown.Clear(); list.Items.Clear();
            if (services == null) { status.Text = Lang.T("white.service.failed"); confirm.Enabled = false; return; }
            all.AddRange(PrepareEntries(services, known));
            ApplyFilter();
        }

        private void ApplyFilter()
        {
            string query = search.Text.Trim();
            shown.Clear();
            foreach (Entry entry in all)
                if (query.Length == 0 || entry.Service.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
                    || (!string.IsNullOrEmpty(entry.Service.DisplayName) && entry.Service.DisplayName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)) shown.Add(entry);
            list.BeginUpdate();
            try { list.Items.Clear(); foreach (Entry entry in shown) list.Items.Add(entry); }
            finally { list.EndUpdate(); }
            status.Text = !scanFinished ? Lang.T("white.service.busy") : scanFailed ? Lang.T("white.service.failed")
                : all.Count == 0 ? Lang.T("white.service.none") : Lang.F("white.service.count", shown.Count, all.Count);
            UpdateSelection();
        }

        private void Toggle(int index)
        {
            if (index < 0 || index >= shown.Count) return;
            shown[index].Checked = !shown[index].Checked; UpdateSelection(); list.Invalidate();
        }
        private void UpdateSelection()
        {
            int count = 0; foreach (Entry entry in all) if (entry.Checked) count++;
            confirm.Enabled = count > 0;
            confirm.Text = count > 0 ? Lang.F("white.pick.add.n", count) : Lang.T("white.pick.add");
        }
        private void ConfirmSelection()
        {
            Selected.Clear(); foreach (Entry entry in all) if (entry.Checked) Selected.Add(entry.Service.Name);
            if (Selected.Count == 0) return;
            DialogResult = DialogResult.OK; Close();
        }
        internal static string StateText(uint state)
        {
            string key;
            switch (state)
            {
                case 1: key = "stopped"; break; case 2: key = "starting"; break; case 3: key = "stopping"; break;
                case 4: key = "running"; break; case 5: key = "resuming"; break; case 6: key = "pausing"; break;
                case 7: key = "paused"; break; default: key = "unknown"; break;
            }
            return Lang.T("white.service.state." + key);
        }

        private void DrawEntry(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= shown.Count) return;
            Entry entry = shown[e.Index]; Rectangle bounds = e.Bounds; Graphics graphics = e.Graphics;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            bool hover = (e.State & DrawItemState.Selected) != 0 || e.Index == Theme.HoverIndex(list);
            using (var brush = new SolidBrush(entry.Checked ? Theme.Sel : hover ? Theme.CardHover : Theme.Card)) graphics.FillRectangle(brush, bounds);
            var box = new Rectangle(bounds.Left + Theme.S(14), bounds.Top + (bounds.Height - Theme.S(16)) / 2, Theme.S(16), Theme.S(16));
            using (GraphicsPath path = Theme.Rounded(box, Theme.S(4)))
            {
                using (var brush = new SolidBrush(entry.Checked ? Theme.Accent : Theme.Bg)) graphics.FillPath(brush, path);
                using (var pen = new Pen(entry.Checked ? Theme.Accent : Theme.StrokeHi)) graphics.DrawPath(pen, path);
            }
            if (entry.Checked)
                using (var pen = new Pen(Theme.OnAccent, Math.Max(1.4f, Theme.S(2))))
                    graphics.DrawLines(pen, new[] { new Point(box.Left + Theme.S(4), box.Top + Theme.S(8)), new Point(box.Left + Theme.S(7), box.Top + Theme.S(11)), new Point(box.Left + Theme.S(12), box.Top + Theme.S(5)) });
            int left = box.Right + Theme.S(14), stateWidth = Theme.S(100);
            int textWidth = Math.Max(1, bounds.Right - left - stateWidth - Theme.S(20));
            string display = string.IsNullOrEmpty(entry.Service.DisplayName) ? entry.Service.Name : entry.Service.DisplayName;
            TextRenderer.DrawText(graphics, display, Theme.UI(9.2f, true), new Rectangle(left, bounds.Top + Theme.S(6), textWidth, Theme.S(20)), Theme.Fg, TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(graphics, entry.Service.Name, Theme.UI(7.6f, false), new Rectangle(left, bounds.Top + Theme.S(26), textWidth, Theme.S(18)), Theme.Faint, TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(graphics, StateText(entry.Service.State), Theme.UI(7.8f, false), new Rectangle(bounds.Right - stateWidth - Theme.S(14), bounds.Top, stateWidth, bounds.Height), entry.Service.State == 4 ? Theme.Green : Theme.Dim, TextFormatFlags.NoPrefix | TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        }
    }
}
