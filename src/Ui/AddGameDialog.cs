// @author bdth 2074055628@qq.com
// 文件用途 统一的添加游戏对话框 打开即扫描已安装游戏并混排运行中的候选进程 浏览文件兜底

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace PaviseApp
{
    internal class AddGameDialog : Form
    {
        private enum RowKind { Installed, Running }

        private class Row
        {
            public string Name;
            public string Path;
            public string Root;
            public bool Checked;
            public bool Already;
            public RowKind Kind;
            public double Gpu;
            public bool RendererLike;
        }

        public readonly List<ScanHit> Selected = new List<ScanHit>();

        private readonly bool allowGpuProbe;
        private readonly HashSet<string> existing;
        private readonly List<Row> rows = new List<Row>();
        private readonly List<Row> shown = new List<Row>();
        private ListBox lst;
        private TextBox tbFilter;
        private Label lblInfo;
        private PillButton btnAdd, btnDeep, btnAll, btnBrowse, btnRunning;
        private volatile bool closed;
        private volatile bool scanning;
        private int hover = -1;

        public AddGameDialog(IEnumerable<string> alreadyInLibrary, bool allowGpuProbe)
        {
            this.allowGpuProbe = allowGpuProbe;
            existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (alreadyInLibrary != null)
                foreach (string p in alreadyInLibrary)
                    if (!string.IsNullOrEmpty(p)) existing.Add(p);

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(Theme.S(620), Theme.S(560));
            BackColor = Theme.Bg;
            Font = Theme.UI(9.5f, false);

            var title = new Label();
            title.Text = Lang.T("scan.title");
            title.ForeColor = Theme.Fg;
            title.Font = Theme.UI(10.5f, true);
            title.SetBounds(Theme.S(16), Theme.S(12), Theme.S(300), Theme.S(24));
            title.MouseDown += DragMove;

            var lblClose = new Label();
            lblClose.Text = "✕";
            lblClose.ForeColor = Theme.Dim;
            lblClose.SetBounds(Theme.S(584), Theme.S(10), Theme.S(26), Theme.S(26));
            lblClose.TextAlign = ContentAlignment.MiddleCenter;
            lblClose.Cursor = Cursors.Hand;
            lblClose.Click += delegate { DialogResult = DialogResult.Cancel; };

            tbFilter = Theme.MakeTextBox(Theme.S(16), Theme.S(44), Theme.S(488));
            tbFilter.TextChanged += delegate { Refill(); };

            btnAll = new PillButton(Lang.T("scan.all"));
            btnAll.SetBounds(Theme.S(512), Theme.S(44), Theme.S(92), Theme.S(30));
            btnAll.Click += delegate { ToggleAll(); };

            var listWrap = new RoundPanel();
            listWrap.SetBounds(Theme.S(16), Theme.S(84), Theme.S(588), Theme.S(392));
            listWrap.BackColor = Theme.Bg; listWrap.Fill = Theme.Card; listWrap.Border = Theme.Stroke; listWrap.Radius = Theme.S(12);
            listWrap.Padding = new Padding(Theme.S(6));
            lst = new TechListBox();
            lst.Dock = DockStyle.Fill;
            lst.BackColor = Theme.Card;
            lst.ForeColor = Theme.Fg;
            lst.BorderStyle = BorderStyle.None;
            lst.DrawMode = DrawMode.OwnerDrawFixed;
            lst.ItemHeight = Math.Min(255, Theme.S(46));
            lst.IntegralHeight = false;
            lst.Font = Theme.UI(9.5f, false);
            lst.DrawItem += DrawRow;
            lst.MouseMove += delegate(object s, MouseEventArgs e)
            {
                int idx = lst.IndexFromPoint(e.Location);
                if (idx == hover) return;
                int was = hover;
                hover = idx;
                InvalidateRow(was);
                InvalidateRow(idx);
            };
            lst.MouseLeave += delegate
            {
                if (hover < 0) return;
                int was = hover;
                hover = -1;
                InvalidateRow(was);
            };
            lst.MouseClick += delegate(object s, MouseEventArgs e)
            {
                int idx = lst.IndexFromPoint(e.Location);
                if (idx >= 0) ToggleAt(idx);
            };
            lst.DoubleClick += delegate
            {
                int idx = lst.SelectedIndex;
                if (idx < 0 || idx >= shown.Count) return;
                Row r = shown[idx];
                if (r.Already) return;
                r.Checked = true;
                Accept();
            };
            listWrap.Controls.Add(lst);

            lblInfo = new Label();
            lblInfo.Text = Lang.T("scan.busy");
            lblInfo.ForeColor = Theme.Dim;
            lblInfo.BackColor = Theme.Bg;
            lblInfo.Font = Theme.UI(8.25f, false);
            lblInfo.TextAlign = ContentAlignment.MiddleLeft;
            lblInfo.SetBounds(Theme.S(16), Theme.S(490), Theme.S(146), Theme.S(28));
            lblInfo.AutoEllipsis = true;

            btnRunning = new PillButton(Lang.T("scan.running.btn"));
            btnRunning.SetBounds(Theme.S(166), Theme.S(486), Theme.S(94), Theme.S(34));
            btnRunning.Click += delegate { PickRunning(); };

            btnBrowse = new PillButton(Lang.T("scan.browse"));
            btnBrowse.SetBounds(Theme.S(266), Theme.S(486), Theme.S(90), Theme.S(34));
            btnBrowse.Click += delegate { BrowseFile(); };

            btnDeep = new PillButton(Lang.T("scan.deep"));
            btnDeep.SetBounds(Theme.S(362), Theme.S(486), Theme.S(90), Theme.S(34));
            btnDeep.Click += delegate { DeepScan(); };

            btnAdd = new PillButton(Lang.T("btn.add"), BtnKind.Primary);
            btnAdd.SetBounds(Theme.S(458), Theme.S(486), Theme.S(74), Theme.S(34));
            btnAdd.Click += delegate { Accept(); };

            var btnCancel = new PillButton(Lang.T("btn.cancel"));
            btnCancel.SetBounds(Theme.S(538), Theme.S(486), Theme.S(66), Theme.S(34));
            btnCancel.Click += delegate { DialogResult = DialogResult.Cancel; };

            Controls.AddRange(new Control[] { title, lblClose, tbFilter, btnAll, listWrap, lblInfo, btnRunning, btnBrowse, btnDeep, btnAdd, btnCancel });
            Load += delegate { StartRunningCollect(); StartScan(null); };
            FormClosed += delegate { closed = true; };
            MouseDown += DragMove;
            KeyPreview = true;
            KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape) DialogResult = DialogResult.Cancel;
                else if (e.KeyCode == Keys.Space && !tbFilter.Focused) { ToggleSelected(); e.SuppressKeyPress = true; }
                else if (e.KeyCode == Keys.Enter && !tbFilter.Focused) { Accept(); e.SuppressKeyPress = true; }
            };
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
            if (e.Button == MouseButtons.Left)
            {
                Native.ReleaseCapture();
                Native.SendMessage(Handle, Native.WM_NCLBUTTONDOWN, (IntPtr)Native.HT_CAPTION, IntPtr.Zero);
            }
        }

        private void StartRunningCollect()
        {
            var worker = new Thread(delegate()
            {
                List<ScanHit> hits;
                Dictionary<string, int> pidByPath;
                try { hits = CollectRunningCandidates(out pidByPath); }
                catch { hits = new List<ScanHit>(); pidByPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase); }
                Post(delegate { Merge(hits, RowKind.Running); });

                if (!allowGpuProbe || closed || pidByPath.Count == 0) return;
                Dictionary<int, double> util = null;
                try
                {
                    util = GpuEvidence.Sample3D(
                        GpuEvidence.BurstRounds, GpuEvidence.BurstIntervalMs, IsClosed);
                }
                catch { }
                if (util == null || closed) return;
                var utilByPath = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                foreach (KeyValuePair<string, int> kv in pidByPath)
                {
                    double u;
                    if (util.TryGetValue(kv.Value, out u) && u > 0) utilByPath[kv.Key] = u;
                }
                Post(delegate { ApplyGpuTags(utilByPath); });
            });
            worker.IsBackground = true;
            worker.Start();
        }

        private void ApplyGpuTags(Dictionary<string, double> utilByPath)
        {
            Row best = null;
            foreach (Row r in rows)
            {
                if (r.Kind != RowKind.Running) continue;
                double u;
                if (!utilByPath.TryGetValue(r.Path, out u)) continue;
                r.Gpu = u;
                if (best == null || u > best.Gpu) best = r;
            }
            if (best != null && best.Gpu >= GpuEvidence.MinElectUtilization)
            {
                best.RendererLike = true;
                if (!best.Already) best.Checked = true;
            }
            SortRows();
            Refill();
        }

        private static List<ScanHit> CollectRunningCandidates(out Dictionary<string, int> pidByPath)
        {
            pidByPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var hits = new List<ScanHit>();
            int session = -1, selfPid = 0;
            try
            {
                using (Process current = Process.GetCurrentProcess())
                {
                    session = current.SessionId;
                    selfPid = current.Id;
                }
            }
            catch { }
            if (session < 0) return hits;
            string windowsRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            windowsRoot = string.IsNullOrEmpty(windowsRoot) ? @"C:\Windows\" : windowsRoot.TrimEnd('\\') + "\\";
            HashSet<int> visible = GameSessionDetector.VisibleWindowPids(true);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Process[] all = null;
            try
            {
                all = Process.GetProcesses();
                foreach (Process p in all)
                {
                    try
                    {
                        if (p.Id <= 4 || p.Id == selfPid || !visible.Contains(p.Id)) continue;
                        int processSession = -1;
                        try { processSession = p.SessionId; } catch { }
                        if (processSession != session) continue;
                        string path = null;
                        IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, p.Id);
                        if (h != IntPtr.Zero)
                        {
                            try { path = Native.ImagePath(h); }
                            finally { Native.CloseHandle(h); }
                        }
                        if (string.IsNullOrEmpty(path) || !seen.Add(path)) continue;
                        string name = GameSessionDetector.ImageNameFromVerifiedPath(path);
                        if (!GameSessionDetector.IsLibraryCandidate(name, path, windowsRoot)) continue;
                        if (GamePlatformCatalog.IsPlatformProcess(name, path)) continue;
                        pidByPath[path] = p.Id;
                        hits.Add(new ScanHit
                        {
                            Name = DisplayNameOf(path, name),
                            Proc = name,
                            Root = GameScan.InferGameRoot(path),
                            Exe = path
                        });
                    }
                    catch { }
                }
            }
            catch { }
            finally { if (all != null) foreach (Process p in all) { try { p.Dispose(); } catch { } } }
            return hits;
        }

        private static string DisplayNameOf(string executablePath, string fallback)
        {
            try
            {
                FileVersionInfo info = FileVersionInfo.GetVersionInfo(executablePath);
                string value = !string.IsNullOrWhiteSpace(info.FileDescription) ? info.FileDescription : info.ProductName;
                if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
            }
            catch { }
            return fallback;
        }

        private bool IsClosed()
        {
            return closed;
        }

        private void StartScan(string deepRoot)
        {
            if (scanning) return;
            scanning = true;
            btnDeep.Enabled = false;
            lblInfo.Text = Lang.T(deepRoot == null ? "scan.busy" : "scan.busy.deep");

            var worker = new Thread(delegate()
            {
                List<ScanHit> hits;
                try
                {
                    hits = deepRoot == null
                        ? GameScan.RunManifests(IsClosed)
                        : GameScan.Run(deepRoot, IsClosed, null);
                }
                catch { hits = new List<ScanHit>(); }
                Post(delegate { scanning = false; btnDeep.Enabled = true; Merge(hits, RowKind.Installed); });
            });
            worker.IsBackground = true;
            worker.Start();
        }

        private void DeepScan()
        {
            using (var dlg = new FolderBrowserDialog())
            {
                dlg.Description = Lang.T("scan.deep.pick");
                dlg.ShowNewFolderButton = false;
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                if (string.IsNullOrEmpty(dlg.SelectedPath)) return;
                StartScan(dlg.SelectedPath);
            }
        }

        private void PickRunning()
        {
            using (var dlg = new RunningPickerDialog(existing, Lang.T("scan.pick.confirm")))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK || dlg.Selected.Count == 0) return;
                Selected.Clear();
                string firstError = null;
                foreach (string path in dlg.Selected)
                {
                    string resolved, error;
                    if (!GameExecutableResolver.TryResolve(path, out resolved, out error))
                    {
                        if (firstError == null) firstError = error;
                        continue;
                    }
                    Selected.Add(new ScanHit { Name = null, Root = GameScan.InferGameRoot(resolved), Exe = resolved });
                }
                if (Selected.Count == 0)
                {
                    if (!string.IsNullOrEmpty(firstError))
                        PaviseDialog.Warn(this, App.DisplayName, firstError);
                    return;
                }
                DialogResult = DialogResult.OK;
            }
        }

        private void BrowseFile()
        {
            using (var dlg = new OpenFileDialog())
            {
                dlg.Title = Lang.T("ofd.game");
                dlg.Filter = Lang.T("ofd.filter");
                dlg.CheckFileExists = false;
                dlg.DereferenceLinks = false;
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                string resolved, error;
                if (!GameExecutableResolver.TryResolve(dlg.FileName, out resolved, out error))
                {
                    if (!string.IsNullOrEmpty(error))
                        PaviseDialog.Warn(this, App.DisplayName, error);
                    return;
                }
                Selected.Clear();
                Selected.Add(new ScanHit { Name = null, Root = GameScan.InferGameRoot(resolved), Exe = resolved });
                DialogResult = DialogResult.OK;
            }
        }

        private void Merge(List<ScanHit> hits, RowKind kind)
        {
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Row r in rows) known.Add(r.Path);

            foreach (ScanHit h in hits)
            {
                if (h == null) continue;
                if (kind == RowKind.Installed && string.IsNullOrEmpty(h.Root)) continue;
                string exe = ResolveExe(h);
                if (exe == null || !known.Add(exe)) continue;
                rows.Add(new Row
                {
                    Name = string.IsNullOrEmpty(h.Name) ? Path.GetFileNameWithoutExtension(exe) : h.Name,
                    Path = exe,
                    Root = h.Root,
                    Already = existing.Contains(exe),
                    Checked = false,
                    Kind = kind
                });
            }

            SortRows();

            int fresh = 0;
            foreach (Row r in rows) if (!r.Already) fresh++;
            lblInfo.Text = rows.Count == 0
                ? Lang.T(scanning ? "scan.busy" : "scan.none")
                : Lang.F("scan.count", rows.Count, fresh);
            Refill();
        }

        private void SortRows()
        {
            rows.Sort(delegate(Row a, Row b)
            {
                int ga = RowGroup(a);
                int gb = RowGroup(b);
                if (ga != gb) return ga - gb;
                return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
            });
        }

        private static int RowGroup(Row r)
        {
            if (r.Already) return 3;
            if (r.RendererLike) return 0;
            return r.Kind == RowKind.Running ? 1 : 2;
        }

        private static string ResolveExe(ScanHit h)
        {
            if (string.IsNullOrEmpty(h.Exe)) return null;
            string resolved, error;
            return GameExecutableResolver.TryResolve(h.Exe, out resolved, out error) ? resolved : null;
        }

        private void Refill()
        {
            string f = tbFilter.Text.Trim();
            lst.BeginUpdate();
            lst.Items.Clear();
            shown.Clear();
            foreach (Row r in rows)
            {
                if (f.Length > 0
                    && r.Name.IndexOf(f, StringComparison.CurrentCultureIgnoreCase) < 0
                    && r.Path.IndexOf(f, StringComparison.OrdinalIgnoreCase) < 0) continue;
                shown.Add(r);
                lst.Items.Add(r.Name);
            }
            lst.EndUpdate();
            UpdateAddButton();
        }

        private void ToggleSelected()
        {
            ToggleAt(lst.SelectedIndex);
        }

        private void ToggleAt(int i)
        {
            if (i < 0 || i >= shown.Count) return;
            Row r = shown[i];
            if (r.Already) return;
            r.Checked = !r.Checked;
            lst.Invalidate();
            UpdateAddButton();
        }

        private void ToggleAll()
        {
            bool anyUnchecked = false;
            foreach (Row r in shown) if (!r.Already && !r.Checked) { anyUnchecked = true; break; }
            foreach (Row r in shown) if (!r.Already) r.Checked = anyUnchecked;
            lst.Invalidate();
            UpdateAddButton();
        }

        private void UpdateAddButton()
        {
            int n = 0;
            foreach (Row r in rows) if (r.Checked && !r.Already) n++;
            btnAdd.Text = n > 0 ? Lang.F("scan.add.n", n) : Lang.T("btn.add");
            btnAdd.Enabled = n > 0;
            btnAdd.Invalidate();
        }

        private void InvalidateRow(int index)
        {
            if (index < 0 || index >= lst.Items.Count) return;
            lst.Invalidate(lst.GetItemRectangle(index));
        }

        private void DrawRow(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= shown.Count) return;
            Row r = shown[e.Index];
            bool selected = (e.State & DrawItemState.Selected) != 0;
            Rectangle row = Rectangle.Inflate(e.Bounds, -Theme.S(4), -Theme.S(2));
            using (var back = new SolidBrush(Theme.Card)) e.Graphics.FillRectangle(back, e.Bounds);
            Theme.FillRound(e.Graphics, row, Theme.S(8),
                selected ? Theme.Sel : (e.Index == hover ? Theme.CardHover : Theme.Card));

            int box = Theme.S(15);
            int bx = e.Bounds.X + Theme.S(14), by = e.Bounds.Y + (e.Bounds.Height - box) / 2;
            var mark = new Rectangle(bx, by, box, box);
            if (r.Already)
            {
                using (var pen = new Pen(Theme.Faint)) e.Graphics.DrawRectangle(pen, mark);
            }
            else if (r.Checked)
            {
                Theme.FillRound(e.Graphics, mark, Theme.S(3), Theme.Accent);
                using (var pen = new Pen(Theme.Bg, Theme.S(2) < 1 ? 1 : Theme.S(2)))
                {
                    e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    e.Graphics.DrawLines(pen, new[]
                    {
                        new Point(mark.X + box / 4, mark.Y + box / 2),
                        new Point(mark.X + box / 2 - Theme.S(1), mark.Y + box - box / 3),
                        new Point(mark.Right - box / 5, mark.Y + box / 4)
                    });
                    e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.Default;
                }
            }
            else
            {
                using (var pen = new Pen(Theme.Stroke)) e.Graphics.DrawRectangle(pen, mark);
            }

            int tx = bx + box + Theme.S(12), right = Theme.S(76);
            Color nameColor = r.Already ? Theme.Faint : Theme.Fg;
            TextRenderer.DrawText(e.Graphics, r.Name, Theme.UI(9.6f, true),
                new Rectangle(tx, e.Bounds.Y + Theme.S(5), e.Bounds.Width - tx - right, Theme.S(20)),
                nameColor, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(e.Graphics, r.Path, Theme.UI(7.6f, false),
                new Rectangle(tx, e.Bounds.Y + Theme.S(24), e.Bounds.Width - tx - Theme.S(16), Theme.S(16)),
                Theme.Dim, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            if (r.Already)
                TextRenderer.DrawText(e.Graphics, Lang.T("scan.already"), Theme.UI(7.6f, true),
                    new Rectangle(e.Bounds.Right - right, e.Bounds.Y + Theme.S(6), right - Theme.S(14), Theme.S(18)),
                    Theme.Faint, TextFormatFlags.Right | TextFormatFlags.NoPadding);
            else if (r.RendererLike)
                TextRenderer.DrawText(e.Graphics, Lang.F("scan.renderer.tag", (int)r.Gpu), Theme.UI(7.6f, true),
                    new Rectangle(e.Bounds.Right - Theme.S(128), e.Bounds.Y + Theme.S(6), Theme.S(114), Theme.S(18)),
                    Theme.Accent, TextFormatFlags.Right | TextFormatFlags.NoPadding);
            else if (r.Kind == RowKind.Running)
                TextRenderer.DrawText(e.Graphics, Lang.T("scan.running.tag"), Theme.UI(7.6f, true),
                    new Rectangle(e.Bounds.Right - right, e.Bounds.Y + Theme.S(6), right - Theme.S(14), Theme.S(18)),
                    Theme.Green, TextFormatFlags.Right | TextFormatFlags.NoPadding);
        }

        private void Post(Action a)
        {
            if (closed) return;
            try { BeginInvoke((MethodInvoker)delegate { if (!closed && !IsDisposed) a(); }); }
            catch { }
        }

        private void Accept()
        {
            Selected.Clear();
            foreach (Row r in rows)
                if (r.Checked && !r.Already)
                    Selected.Add(new ScanHit { Name = r.Name, Root = r.Root, Exe = r.Path });
            if (Selected.Count == 0) return;
            DialogResult = DialogResult.OK;
        }
    }
}
