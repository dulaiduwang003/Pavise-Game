// @author bdth 2074055628@qq.com
// 文件用途 列出当前运行的用户程序 供白名单批量选取

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace PaviseApp
{
    internal sealed class RunningPickerDialog : Form
    {
        internal sealed class Entry
        {
            public string Path;
            public string Title;
            public long Memory;
            public int Count;
            public bool Checked;
            public Bitmap Icon;
        }

        private readonly HashSet<string> known;
        private readonly List<Entry> all = new List<Entry>();
        private readonly List<Entry> shown = new List<Entry>();
        private readonly ListBox list = new TechListBox();
        private readonly TextBox search = new TextBox();
        private readonly Label status = new Label();
        private readonly PillButton confirm;

        public readonly List<string> Selected = new List<string>();

        public RunningPickerDialog(HashSet<string> alreadyListed)
        {
            known = alreadyListed ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Text = Lang.T("white.pick.title");
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            ClientSize = new Size(Theme.S(700), Theme.S(540));
            BackColor = Theme.Bg; ForeColor = Theme.Fg; Font = Theme.UI(9.5f, false);

            var title = new Label();
            title.Text = Lang.T("white.pick.title");
            title.ForeColor = Theme.Fg; title.BackColor = Theme.Bg; title.Font = Theme.UI(14f, true);
            title.SetBounds(Theme.S(22), Theme.S(18), Theme.S(600), Theme.S(30));
            title.MouseDown += DragMove;

            var close = new Label();
            close.Text = "✕"; close.ForeColor = Theme.Dim; close.BackColor = Theme.Bg;
            close.SetBounds(Theme.S(662), Theme.S(12), Theme.S(26), Theme.S(26));
            close.TextAlign = ContentAlignment.MiddleCenter;
            close.Cursor = Cursors.Hand;
            close.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };

            var note = new Label();
            note.Text = Lang.T("white.pick.sub");
            note.ForeColor = Theme.Dim; note.BackColor = Theme.Bg; note.Font = Theme.UI(8.5f, false);
            note.SetBounds(Theme.S(22), Theme.S(50), Theme.S(656), Theme.S(22));

            search.SetBounds(Theme.S(22), Theme.S(80), Theme.S(656), Theme.S(30));
            search.BackColor = Theme.Card; search.ForeColor = Theme.Fg;
            search.BorderStyle = BorderStyle.FixedSingle;
            search.TextChanged += delegate { ApplyFilter(); };

            var wrap = new RoundPanel();
            wrap.SetBounds(Theme.S(22), Theme.S(120), Theme.S(656), Theme.S(340));
            wrap.BackColor = Theme.Bg; wrap.Fill = Theme.Card; wrap.Border = Theme.Stroke;
            wrap.Radius = Theme.S(12); wrap.Padding = new Padding(Theme.S(8));
            list.Dock = DockStyle.Fill;
            Theme.StyleList(list, false);
            list.ItemHeight = Math.Min(255, Theme.S(52));
            list.DrawItem += DrawEntry;
            list.MouseDown += OnListMouseDown;
            wrap.Controls.Add(list);

            status.SetBounds(Theme.S(22), Theme.S(470), Theme.S(400), Theme.S(24));
            status.ForeColor = Theme.Dim; status.BackColor = Theme.Bg; status.Font = Theme.UI(8.5f, false);
            status.Text = Lang.T("white.pick.busy");

            confirm = new PillButton(Lang.T("white.pick.add"), BtnKind.Primary);
            confirm.SetBounds(Theme.S(478), Theme.S(492), Theme.S(200), Theme.S(36));
            confirm.Enabled = false;
            confirm.Click += delegate
            {
                foreach (Entry entry in all) if (entry.Checked) Selected.Add(entry.Path);
                DialogResult = DialogResult.OK;
                Close();
            };

            var cancel = new PillButton(Lang.T("btn.cancel"));
            cancel.SetBounds(Theme.S(262), Theme.S(492), Theme.S(200), Theme.S(36));
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };

            Controls.AddRange(new Control[] { title, close, note, search, wrap, status, confirm, cancel });
            MouseDown += DragMove;
            KeyPreview = true;
            KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); }
            };
            Shown += delegate { BeginScan(); };
        }

        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ClassStyle |= 0x20000; return cp; }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var pen = new Pen(Theme.Accent))
                e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.RoundCorners(Handle);
        }

        private void DragMove(object s, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            Native.ReleaseCapture();
            Native.SendMessage(Handle, Native.WM_NCLBUTTONDOWN, (IntPtr)Native.HT_CAPTION, IntPtr.Zero);
        }

#if PAVISE_SELFTEST
        internal void CreateControlForShot() { CreateControl(); GC.KeepAlive(Handle); }

        internal void PrimeForShot()
        {
            all.Clear();
            List<Entry> found = null;
            try { found = Scan(known); }
            catch { }
            if (found != null) all.AddRange(found);
            foreach (Entry entry in all) entry.Icon = LoadIcon(entry.Path);
            for (int i = 0; i < all.Count && i < 3; i++) all[i].Checked = i == 1;
            status.Text = all.Count == 0
                ? Lang.T("white.pick.none") : Lang.F("white.pick.count", all.Count);
            confirm.Enabled = all.Count > 0;
            ApplyFilter();
        }
#endif

        private void BeginScan()
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                List<Entry> found = null;
                try { found = Scan(known); }
                catch { }
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        if (IsDisposed) return;
                        all.Clear();
                        if (found != null) all.AddRange(found);
                        foreach (Entry entry in all) entry.Icon = LoadIcon(entry.Path);
                        status.Text = all.Count == 0
                            ? Lang.T("white.pick.none") : Lang.F("white.pick.count", all.Count);
                        confirm.Enabled = all.Count > 0;
                        ApplyFilter();
                    });
                }
                catch { }
            });
        }

        internal static List<Entry> Scan(HashSet<string> exclude)
        {
            var map = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            int self = 0;
            int session = -1;
            try
            {
                using (Process me = Process.GetCurrentProcess()) { self = me.Id; session = me.SessionId; }
            }
            catch { }

            Process[] processes;
            try { processes = Process.GetProcesses(); }
            catch { return new List<Entry>(); }

            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string windowsPrefix = string.IsNullOrEmpty(windows) ? @"C:\Windows\" : windows.TrimEnd('\\') + "\\";

            foreach (Process process in processes)
            {
                try
                {
                    int pid = process.Id;
                    if (pid <= 4 || pid == self) continue;
                    if (process.SessionId != session) continue;
                    if (process.MainWindowHandle == IntPtr.Zero) continue;

                    string name = process.ProcessName;
                    if (GameSessionDetector.IsAntiCheatLikeName(name)) continue;

                    IntPtr handle = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                    if (handle == IntPtr.Zero) continue;
                    string path;
                    try { path = Native.ImagePath(handle); }
                    finally { Native.CloseHandle(handle); }
                    if (string.IsNullOrEmpty(path)) continue;
                    if (path.StartsWith(windowsPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                    if (exclude != null && exclude.Contains(path)) continue;

                    Entry entry;
                    if (!map.TryGetValue(path, out entry))
                    {
                        entry = new Entry { Path = path, Title = TitleOf(path, name) };
                        map[path] = entry;
                    }
                    entry.Count++;
                    try { entry.Memory += process.WorkingSet64; }
                    catch { }
                }
                catch { }
                finally { try { process.Dispose(); } catch { } }
            }

            var result = new List<Entry>(map.Values);
            result.Sort(delegate(Entry a, Entry b) { return b.Memory.CompareTo(a.Memory); });
            return result;
        }

        private static string TitleOf(string path, string fallback)
        {
            try
            {
                FileVersionInfo info = FileVersionInfo.GetVersionInfo(path);
                string title = string.IsNullOrEmpty(info.FileDescription) ? info.ProductName : info.FileDescription;
                if (!string.IsNullOrEmpty(title)) return title.Trim();
            }
            catch { }
            return fallback;
        }

        private static Bitmap LoadIcon(string path)
        {
            try { using (Icon icon = Icon.ExtractAssociatedIcon(path)) return icon.ToBitmap(); }
            catch { return null; }
        }

        private void ApplyFilter()
        {
            string needle = search.Text.Trim();
            shown.Clear();
            foreach (Entry entry in all)
            {
                if (needle.Length > 0
                    && entry.Title.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0
                    && entry.Path.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0) continue;
                shown.Add(entry);
            }
            list.BeginUpdate();
            list.Items.Clear();
            foreach (Entry entry in shown) list.Items.Add(entry);
            list.EndUpdate();
        }

        private void OnListMouseDown(object sender, MouseEventArgs e)
        {
            int index = list.IndexFromPoint(e.Location);
            if (index < 0 || index >= shown.Count) return;
            shown[index].Checked = !shown[index].Checked;
            list.Invalidate();
            int picked = 0;
            foreach (Entry entry in all) if (entry.Checked) picked++;
            confirm.Text = picked > 0 ? Lang.F("white.pick.add.n", picked) : Lang.T("white.pick.add");
            confirm.Enabled = picked > 0;
            confirm.Invalidate();
        }

        private void DrawEntry(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= shown.Count) return;
            Entry entry = shown[e.Index];
            Graphics g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            Rectangle r = e.Bounds;
            bool hot = (e.State & DrawItemState.Selected) != 0 || e.Index == Theme.HoverIndex(list);

            using (var b = new SolidBrush(entry.Checked ? Theme.Sel : (hot ? Theme.CardHover : Theme.Card)))
                g.FillRectangle(b, r);

            int box = Theme.S(16);
            var boxRect = new Rectangle(r.Left + Theme.S(14), r.Top + (r.Height - box) / 2, box, box);
            using (var path = Theme.Rounded(boxRect, Theme.S(4)))
            {
                using (var b = new SolidBrush(entry.Checked ? Theme.Accent : Theme.Bg)) g.FillPath(b, path);
                using (var p = new Pen(entry.Checked ? Theme.Accent : Theme.StrokeHi)) g.DrawPath(p, path);
            }
            if (entry.Checked)
                using (var p = new Pen(Theme.OnAccent, Math.Max(1.4f, Theme.S(2))))
                    g.DrawLines(p, new[]
                    {
                        new Point(boxRect.Left + Theme.S(4), boxRect.Top + Theme.S(8)),
                        new Point(boxRect.Left + Theme.S(7), boxRect.Top + Theme.S(11)),
                        new Point(boxRect.Left + Theme.S(12), boxRect.Top + Theme.S(5))
                    });

            int iconSize = Theme.S(26);
            var iconRect = new Rectangle(boxRect.Right + Theme.S(12), r.Top + (r.Height - iconSize) / 2, iconSize, iconSize);
            if (entry.Icon != null) g.DrawImage(entry.Icon, iconRect);

            int memW = Theme.S(90);
            int textLeft = iconRect.Right + Theme.S(12);
            int textW = r.Right - textLeft - memW - Theme.S(24);
            TextRenderer.DrawText(g, entry.Title, Theme.UI(9.2f, true),
                new Rectangle(textLeft, r.Top + Theme.S(6), textW, Theme.S(20)), Theme.Fg,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, entry.Path, Theme.UI(7.6f, false),
                new Rectangle(textLeft, r.Top + Theme.S(25), textW, Theme.S(18)), Theme.Faint,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.PathEllipsis);

            string memory = FormatMemory(entry.Memory)
                + (entry.Count > 1 ? " · " + Lang.F("white.pick.procs", entry.Count) : "");
            TextRenderer.DrawText(g, memory, Theme.UI(7.8f, false),
                new Rectangle(r.Right - memW - Theme.S(14), r.Top, memW, r.Height), Theme.Dim,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        }

        internal static string FormatMemory(long bytes)
        {
            if (bytes >= 1073741824L) return (bytes / 1073741824.0).ToString("0.0") + " GB";
            if (bytes >= 1048576L) return (bytes / 1048576.0).ToString("0") + " MB";
            if (bytes <= 0) return "—";
            return (bytes / 1024.0).ToString("0") + " KB";
        }
    }
}
