// @author bdth 2074055628@qq.com
// 文件用途 统一的添加游戏对话框 打开即扫描已安装游戏并混排运行中的候选进程 浏览文件兜底
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace PaviseApp
{
    internal class AddGameDialog : Form
    {
        private enum RowKind { Installed, Running, Picked }

        // 工作线程先把可执行文件路径解析好 再把这些不带资源的快照投给界面
        private sealed class Candidate
        {
            public string Name;
            public string Path;
            public string Root;
            public long Memory;
            public int Count;
            // 手动给的路径 拖进来的 浏览的 文件夹里挑出来的 唯一命中时直接勾上
            public bool Pick;
        }

        private class Row
        {
            public string Name;
            public string Path;
            public string Root;
            public bool Checked;
            public bool Already;
            public bool Installed;
            public bool Running;
            // 用户自己给的条目 不随运行中列表的刷新被清掉
            public bool Pinned;
            public long Memory;
            public int Count;
            public double Gpu;
            public bool RendererLike;
        }

        public readonly List<ScanHit> Selected = new List<ScanHit>();

        private readonly bool allowGpuProbe;
        private readonly HashSet<string> existing;
        private readonly List<Row> rows = new List<Row>();
        private readonly List<Row> shown = new List<Row>();
        private readonly object iconGate = new object();
        private readonly Dictionary<string, Bitmap> iconCache = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> queuedIcons = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private ListBox lst;
        private TextBox tbFilter;
        private Label lblInfo;
        private PillButton btnAdd, btnAll, btnBrowse, btnFolder;
        private readonly List<string> seedPaths = new List<string>();
        private int pickBusy;
        private volatile bool closed;
        private volatile bool scanning;
        private volatile bool collectingRunning;
        private volatile bool probingGpu;
        private volatile bool refreshBusy;
        private int dots;
        private System.Windows.Forms.Timer infoTimer;
        private System.Windows.Forms.Timer runningTimer;
        private int hover = -1;
        private bool selectionEdited;

        public AddGameDialog(IEnumerable<string> alreadyInLibrary, bool allowGpuProbe)
            : this(alreadyInLibrary, allowGpuProbe, null)
        {
        }

        // seeds 是主窗口上拖进来的文件夹 打开即列出里面的候选程序
        public AddGameDialog(IEnumerable<string> alreadyInLibrary, bool allowGpuProbe, IEnumerable<string> seeds)
        {
            this.allowGpuProbe = allowGpuProbe;
            if (seeds != null)
                foreach (string seed in seeds)
                    if (!string.IsNullOrEmpty(seed)) seedPaths.Add(seed);
            existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (alreadyInLibrary != null)
                foreach (string p in alreadyInLibrary)
                    if (!string.IsNullOrEmpty(p)) existing.Add(p);

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            ClientSize = new Size(Theme.S(620), Theme.S(560));
            BackColor = Theme.Bg;
            Font = Theme.UI(9.5f, false);

            var title = new Label();
            title.Text = Lang.T("scan.title");
            title.ForeColor = Theme.Fg;
            title.Font = Theme.UI(10.5f, true);
            title.SetBounds(Theme.S(16), Theme.S(12), Theme.S(300), Theme.S(24));
            title.MouseDown += DragMove;

            var lblScanHint = new Label();
            lblScanHint.Text = Lang.T("scan.hint");
            lblScanHint.ForeColor = Theme.Dim;
            lblScanHint.BackColor = Theme.Bg;
            lblScanHint.Font = Theme.UI(8.25f, false);
            lblScanHint.SetBounds(Theme.S(16), Theme.S(42), Theme.S(588), Theme.S(36));

            var lblClose = new Label();
            lblClose.Text = "✕";
            lblClose.ForeColor = Theme.Dim;
            lblClose.SetBounds(Theme.S(584), Theme.S(10), Theme.S(26), Theme.S(26));
            lblClose.TextAlign = ContentAlignment.MiddleCenter;
            lblClose.Cursor = Cursors.Hand;
            lblClose.Click += delegate { DialogResult = DialogResult.Cancel; };

            tbFilter = Theme.MakeTextBox(Theme.S(16), Theme.S(88), Theme.S(488));
            tbFilter.TextChanged += delegate { Refill(); };

            btnAll = new PillButton(Lang.T("scan.all"));
            btnAll.SetBounds(Theme.S(512), Theme.S(88), Theme.S(92), Theme.S(30));
            btnAll.Click += delegate { ToggleAll(); };

            var listWrap = new RoundPanel();
            listWrap.SetBounds(Theme.S(16), Theme.S(128), Theme.S(588), Theme.S(348));
            listWrap.BackColor = Theme.Bg; listWrap.Fill = Theme.Card; listWrap.Border = Theme.Stroke; listWrap.Radius = Theme.S(12);
            listWrap.Padding = new Padding(Theme.S(6));
            lst = new TechListBox();
            lst.Dock = DockStyle.Fill;
            lst.BackColor = Theme.Card;
            lst.ForeColor = Theme.Fg;
            lst.BorderStyle = BorderStyle.None;
            lst.DrawMode = DrawMode.OwnerDrawFixed;
            lst.ItemHeight = Math.Min(255, Theme.S(52));
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
                if (e.Button != MouseButtons.Left) return;
                int idx = lst.IndexFromPoint(e.Location);
                if (idx >= 0) ToggleAt(idx);
            };
            lst.DoubleClick += delegate
            {
                int idx = lst.SelectedIndex;
                if (idx < 0 || idx >= shown.Count) return;
                Row r = shown[idx];
                if (r.Already) return;
                selectionEdited = true;
                r.Checked = true;
                Accept();
            };
            listWrap.Controls.Add(new TechListScrollHost((TechListBox)lst) { Dock = DockStyle.Fill });

            lblInfo = new Label();
            lblInfo.Text = Lang.T("scan.busy");
            lblInfo.ForeColor = Theme.Dim;
            lblInfo.BackColor = Theme.Bg;
            lblInfo.Font = Theme.UI(8.25f, false);
            lblInfo.TextAlign = ContentAlignment.MiddleLeft;
            lblInfo.SetBounds(Theme.S(16), Theme.S(480), Theme.S(588), Theme.S(20));
            lblInfo.AutoEllipsis = true;

            btnBrowse = new PillButton(Lang.T("scan.browse"));
            btnBrowse.SetBounds(Theme.S(16), Theme.S(506), Theme.S(122), Theme.S(34));
            btnBrowse.Click += delegate { BrowseFile(); };

            btnFolder = new PillButton(Lang.T("scan.folder"));
            btnFolder.SetBounds(Theme.S(146), Theme.S(506), Theme.S(122), Theme.S(34));
            btnFolder.Click += delegate { BrowseFolder(); };

            btnAdd = new PillButton(Lang.T("btn.add"), BtnKind.Primary);
            btnAdd.Enabled = false;
            btnAdd.SetBounds(Theme.S(392), Theme.S(506), Theme.S(110), Theme.S(34));
            btnAdd.Click += delegate { Accept(); };

            var btnCancel = new PillButton(Lang.T("btn.cancel"));
            btnCancel.SetBounds(Theme.S(514), Theme.S(506), Theme.S(90), Theme.S(34));
            btnCancel.Click += delegate { DialogResult = DialogResult.Cancel; };

            Controls.AddRange(new Control[] { title, lblScanHint, lblClose, tbFilter, btnAll, listWrap, lblInfo, btnBrowse, btnFolder, btnAdd, btnCancel });
            Load += delegate
            {
                StartTimers(); StartRunningCollect(true); StartScan();
                if (seedPaths.Count > 0) AddDroppedPaths(seedPaths.ToArray());
            };
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
            // 模态期间主窗口收不到拖放 这里自己收 EXE 快捷方式和文件夹都接
            Native.EnableElevatedFileDrop(Handle);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_DROPFILES)
            {
                AddDroppedPaths(Native.ReadDroppedFiles(m.WParam));
                m.Result = IntPtr.Zero;
                return;
            }
            base.WndProc(ref m);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Fx.EnterForm(this);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            StopWork();
            base.OnFormClosed(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) StopWork();
            base.Dispose(disposing);
        }

        private void StopWork()
        {
            closed = true;
            StopTimers();
            lock (iconGate)
            {
                foreach (Bitmap icon in iconCache.Values) if (icon != null) icon.Dispose();
                iconCache.Clear();
                queuedIcons.Clear();
            }
        }

        private void DragMove(object s, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                Native.ReleaseCapture();
                Native.SendMessage(Handle, Native.WM_NCLBUTTONDOWN, (IntPtr)Native.HT_CAPTION, IntPtr.Zero);
            }
        }

        private void StartTimers()
        {
            if (closed || infoTimer != null) return;
            infoTimer = new System.Windows.Forms.Timer();
            infoTimer.Interval = 420;
            infoTimer.Tick += delegate
            {
                if (!Busy) return;
                dots = (dots + 1) % 4;
                UpdateInfoLabel();
            };
            infoTimer.Start();

            runningTimer = new System.Windows.Forms.Timer();
            runningTimer.Interval = 3000;
            runningTimer.Tick += delegate { StartRunningCollect(false); };
            runningTimer.Start();
        }

        private void StopTimers()
        {
            try { if (infoTimer != null) { infoTimer.Stop(); infoTimer.Dispose(); infoTimer = null; } }
            catch { }
            try { if (runningTimer != null) { runningTimer.Stop(); runningTimer.Dispose(); runningTimer = null; } }
            catch { }
        }

        private bool Busy
        {
            get { return collectingRunning || probingGpu || scanning; }
        }

        private void StartRunningCollect(bool first)
        {
            if (closed || refreshBusy || collectingRunning) return;
            if (first) collectingRunning = true;
            else refreshBusy = true;
            bool probe = first && allowGpuProbe;
            if (first) UpdateInfoLabel();

            var worker = new Thread(delegate()
            {
                List<Candidate> hits = null;
                var pidsByPath = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    List<RunningProgram> programs = RunningProgramDiscovery.Scan(IsClosed);
                    if (programs != null)
                    {
                        hits = new List<Candidate>();
                        foreach (RunningProgram program in programs)
                        {
                            if (closed) return;
                            Candidate candidate = ResolveCandidate(program.Path, program.Title, null);
                            if (candidate == null) continue;
                            candidate.Memory = program.Memory;
                            candidate.Count = program.ProcessIds.Count;
                            hits.Add(candidate);
                            pidsByPath[candidate.Path] = program.ProcessIds;
                        }
                    }
                }
                catch { hits = null; }
                if (closed) return;
                bool sampling = probe && hits != null && pidsByPath.Count > 0;
                Post(delegate
                {
                    collectingRunning = false;
                    refreshBusy = false;
                    if (sampling) probingGpu = true;
                    Merge(hits, RowKind.Running);
                });
                if (!sampling) return;

                Dictionary<int, double> util = null;
                try
                {
                    util = GpuEvidence.Sample3D(
                        GpuEvidence.BurstRounds, GpuEvidence.BurstIntervalMs, IsClosed);
                }
                catch { }
                var utilByPath = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                if (util != null)
                    foreach (KeyValuePair<string, List<int>> kv in pidsByPath)
                    {
                        double highest = 0;
                        foreach (int pid in kv.Value)
                        {
                            double u;
                            if (util.TryGetValue(pid, out u)) highest = Math.Max(highest, u);
                        }
                        if (highest > 0) utilByPath[kv.Key] = highest;
                    }
                Post(delegate
                {
                    probingGpu = false;
                    if (utilByPath.Count > 0) ApplyGpuTags(utilByPath);
                    else UpdateInfoLabel();
                });
            });
            worker.IsBackground = true;
            worker.Start();
        }

        private void UpdateInfoLabel()
        {
            string stage = null;
            if (collectingRunning) stage = Lang.T("scan.stage.running");
            else if (probingGpu) stage = Lang.T("scan.stage.gpu");
            else if (scanning) stage = Lang.T("scan.busy");

            if (stage != null)
            {
                string tail = new string('·', dots);
                lblInfo.ForeColor = Theme.Accent;
                lblInfo.Text = rows.Count == 0
                    ? stage + tail
                    : Lang.F("scan.stage.count", stage + tail, rows.Count);
                return;
            }

            int fresh = 0;
            foreach (Row r in rows) if (!r.Already) fresh++;
            lblInfo.ForeColor = Theme.Dim;
            lblInfo.Text = rows.Count == 0
                ? Lang.T("scan.none")
                : Lang.F("scan.count", rows.Count, fresh) + "   " + Lang.T("scan.live");
        }

        private void ApplyGpuTags(Dictionary<string, double> utilByPath)
        {
            Row best = null;
            foreach (Row r in rows)
            {
                r.RendererLike = false;
                if (!r.Running) continue;
                double u;
                if (!utilByPath.TryGetValue(r.Path, out u)) continue;
                r.Gpu = u;
                if (best == null || u > best.Gpu) best = r;
            }
            if (best != null && best.Gpu >= GpuEvidence.MinElectUtilization)
            {
                best.RendererLike = true;
                if (!selectionEdited && !best.Already) best.Checked = true;
            }
            SortRows();
            Refill();
            UpdateInfoLabel();
        }

        private bool IsClosed()
        {
            return closed;
        }

        private void StartScan()
        {
            if (closed || scanning) return;
            scanning = true;
            UpdateInfoLabel();

            var worker = new Thread(delegate()
            {
                var hits = new List<Candidate>();
                try
                {
                    foreach (ScanHit hit in GameScan.RunManifests(IsClosed))
                    {
                        if (closed) return;
                        if (hit == null || string.IsNullOrEmpty(hit.Root)) continue;
                        Candidate candidate = ResolveCandidate(hit.Exe, hit.Name, hit.Root);
                        if (candidate != null) hits.Add(candidate);
                    }
                }
                catch { }
                Post(delegate { scanning = false; Merge(hits, RowKind.Installed); });
            });
            worker.IsBackground = true;
            worker.Start();
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

        private void BrowseFolder()
        {
            using (var dlg = new FolderBrowserDialog())
            {
                dlg.Description = Lang.T("fbd.game");
                dlg.ShowNewFolderButton = false;
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                AddDroppedPaths(new[] { dlg.SelectedPath });
            }
        }

        // 用户直接给的路径 文件解析成候选并勾上 文件夹先按选举规则挑唯一主程序
        //   挑不出唯一就把里面的候选都列出来让用户点 PE 读取在工作线程做 界面只收结果
        private void AddDroppedPaths(string[] paths)
        {
            if (closed || paths == null || paths.Length == 0) return;
            var files = new List<string>();
            var folders = new List<string>();
            foreach (string raw in paths)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                string path = raw.Trim().Trim('"');
                if (Directory.Exists(path)) folders.Add(path);
                else files.Add(path);
            }
            if (files.Count == 0 && folders.Count == 0) return;
            if (Interlocked.CompareExchange(ref pickBusy, 1, 0) != 0) return;
            var worker = new Thread(delegate()
            {
                var hits = new List<Candidate>();
                var emptyFolders = new List<string>();
                string firstError = null;
                try
                {
                    foreach (string file in files)
                    {
                        if (closed) return;
                        string resolved, error;
                        if (!GameExecutableResolver.TryResolve(file, out resolved, out error))
                        {
                            if (firstError == null && !string.IsNullOrEmpty(error)) firstError = error;
                            continue;
                        }
                        hits.Add(new Candidate
                        {
                            Name = Path.GetFileNameWithoutExtension(resolved),
                            Path = resolved,
                            Root = GameScan.InferGameRoot(resolved),
                            Pick = true
                        });
                    }
                    foreach (string folder in folders)
                    {
                        if (closed) return;
                        string folderName = Path.GetFileName(folder.TrimEnd('\\', '/'));
                        string main = ExecutableCandidateProbe.PickMainExecutable(folder);
                        if (main != null)
                        {
                            hits.Add(new Candidate { Name = folderName, Path = main, Root = folder, Pick = true });
                            continue;
                        }
                        List<ExecutableCandidateFacts> list = ExecutableCandidateProbe.ListCandidates(folder, FolderCandidateCap);
                        if (list.Count == 0) { emptyFolders.Add(folder); continue; }
                        foreach (ExecutableCandidateFacts facts in list)
                            hits.Add(new Candidate
                            {
                                Name = Path.GetFileNameWithoutExtension(facts.Path),
                                Path = facts.Path,
                                Root = folder,
                                Pick = false
                            });
                    }
                }
                catch { }
                finally { Interlocked.Exchange(ref pickBusy, 0); }
                if (closed) return;
                Post(delegate
                {
                    Merge(hits, RowKind.Picked);
                    if (emptyFolders.Count > 0)
                        PaviseDialog.Warn(this, App.DisplayName,
                            Lang.T("scan.folder.none") + "\r\n" + string.Join("\r\n", emptyFolders.ToArray()));
                    else if (firstError != null && hits.Count == 0)
                        PaviseDialog.Warn(this, App.DisplayName, firstError);
                });
            });
            worker.IsBackground = true;
            worker.Start();
        }

        private const int FolderCandidateCap = 24;

        private void Merge(List<Candidate> hits, RowKind kind)
        {
            // 快照失败不能当成所有正在运行的程序都退出了
            if (closed || hits == null) { if (!closed) UpdateInfoLabel(); return; }
            var byPath = new Dictionary<string, Row>(StringComparer.OrdinalIgnoreCase);
            foreach (Row row in rows) byPath[row.Path] = row;
            var runningPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool refill = false;
            foreach (Candidate hit in hits)
            {
                if (hit == null || string.IsNullOrEmpty(hit.Path)) continue;
                if (kind == RowKind.Installed && string.IsNullOrEmpty(hit.Root)) continue;
                Row row;
                if (!byPath.TryGetValue(hit.Path, out row))
                {
                    row = new Row { Path = hit.Path, Already = existing.Contains(hit.Path) };
                    rows.Add(row);
                    byPath[row.Path] = row;
                    refill = true;
                }
                int group = RowGroup(row);
                string previousName = row.Name;
                if (kind == RowKind.Installed || !row.Installed)
                {
                    row.Name = string.IsNullOrEmpty(hit.Name) ? Path.GetFileNameWithoutExtension(hit.Path) : hit.Name;
                    row.Root = hit.Root;
                }
                if (kind == RowKind.Installed) row.Installed = true;
                else if (kind == RowKind.Picked)
                {
                    row.Pinned = true;
                    if (hit.Pick && !row.Already && !row.Checked)
                    {
                        row.Checked = true;
                        selectionEdited = true;
                    }
                }
                else
                {
                    row.Running = true;
                    row.Memory = hit.Memory;
                    row.Count = hit.Count;
                    runningPaths.Add(row.Path);
                }
                if (group != RowGroup(row) || previousName != row.Name) refill = true;
            }

            if (kind == RowKind.Running)
                for (int i = rows.Count - 1; i >= 0; i--)
                {
                    Row row = rows[i];
                    if (runningPaths.Contains(row.Path)) continue;
                    if (row.Running)
                    {
                        row.Running = false;
                        row.Memory = 0;
                        row.Count = 0;
                        row.Gpu = 0;
                        row.RendererLike = false;
                        refill = true;
                    }
                    // 程序关掉之后 显式挑选的条目仍然保留 已安装的条目也留着 用户自己给的也留着
                    if (!row.Installed && !row.Checked && !row.Pinned)
                    {
                        rows.RemoveAt(i);
                        refill = true;
                    }
                }

            if (refill)
            {
                SortRows();
                Refill();
            }
            else lst.Invalidate();
            QueueIcons();
            UpdateInfoLabel();
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
            if (r.Pinned) return -1;
            if (r.RendererLike) return 0;
            return r.Running ? 1 : 2;
        }

        private static Candidate ResolveCandidate(string path, string name, string root)
        {
            string resolved, error;
            if (!GameExecutableResolver.TryResolve(path, out resolved, out error)) return null;
            return new Candidate
            {
                Name = string.IsNullOrEmpty(name) ? Path.GetFileNameWithoutExtension(resolved) : name,
                Path = resolved,
                Root = string.IsNullOrEmpty(root) ? GameScan.InferGameRoot(resolved) : root
            };
        }

        private void Refill()
        {
            string f = tbFilter.Text.Trim();
            int keepIndex = lst.SelectedIndex;
            Row keep = keepIndex >= 0 && keepIndex < shown.Count ? shown[keepIndex] : null;
            int top = lst.TopIndex;
            Row keepTop = top >= 0 && top < shown.Count ? shown[top] : null;

            lst.BeginUpdate();
            try
            {
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
                if (keep != null) lst.SelectedIndex = shown.IndexOf(keep);
                int topIndex = keepTop == null ? -1 : shown.IndexOf(keepTop);
                if (shown.Count > 0) lst.TopIndex = topIndex >= 0 ? topIndex : Math.Min(Math.Max(0, top), shown.Count - 1);
                hover = -1;
            }
            finally { lst.EndUpdate(); }
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
            selectionEdited = true;
            r.Checked = !r.Checked;
            lst.Invalidate();
            UpdateAddButton();
        }

        private void ToggleAll()
        {
            selectionEdited = true;
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

        private void QueueIcons()
        {
            if (closed || !IsHandleCreated) return;
            var paths = new List<string>();
            lock (iconGate)
                foreach (Row row in rows)
                    if (!iconCache.ContainsKey(row.Path) && queuedIcons.Add(row.Path)) paths.Add(row.Path);
            if (paths.Count == 0) return;
            ThreadPool.QueueUserWorkItem(delegate
            {
                foreach (string path in paths)
                {
                    if (closed) return;
                    Bitmap bitmap = null;
                    try { using (Icon icon = Icon.ExtractAssociatedIcon(path)) if (icon != null) bitmap = icon.ToBitmap(); }
                    catch { }
                    CacheIcon(path, bitmap);
                }
            });
        }

        // 就算正在关闭也要接管所有权 不能让某张位图只被排队中的界面回调持有
        private void CacheIcon(string path, Bitmap bitmap)
        {
            lock (iconGate)
            {
                queuedIcons.Remove(path);
                if (closed)
                {
                    if (bitmap != null) bitmap.Dispose();
                    return;
                }
                Bitmap previous;
                if (iconCache.TryGetValue(path, out previous) && previous != null) previous.Dispose();
                iconCache[path] = bitmap;
            }
            Post(delegate
            {
                for (int i = 0; i < shown.Count; i++)
                    if (string.Equals(shown[i].Path, path, StringComparison.OrdinalIgnoreCase)) InvalidateRow(i);
            });
        }

        private struct RowLayout
        {
            public Rectangle Check, Icon, Name, Path, Status, Details;
        }

        private static RowLayout LayoutRow(Rectangle bounds, string status, string details)
        {
            var layout = new RowLayout();
            int box = Theme.S(16), icon = Theme.S(26), gap = Theme.S(12);
            layout.Check = new Rectangle(bounds.Left + Theme.S(14), bounds.Top + (bounds.Height - box) / 2, box, box);
            layout.Icon = new Rectangle(layout.Check.Right + gap, bounds.Top + (bounds.Height - icon) / 2, icon, icon);
            int left = layout.Icon.Right + gap, right = bounds.Right - Theme.S(14);
            var measureFlags = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
            int statusWidth = string.IsNullOrEmpty(status) ? 0 : TextRenderer.MeasureText(status, Theme.UI(7.6f, true),
                new Size(int.MaxValue, int.MaxValue), measureFlags).Width;
            int detailsWidth = string.IsNullOrEmpty(details) ? 0 : TextRenderer.MeasureText(details, Theme.UI(7.6f, false),
                new Size(int.MaxValue, int.MaxValue), measureFlags).Width;
            int reserved = Math.Min(Math.Max(statusWidth, detailsWidth) + Theme.S(2), Math.Max(0, (right - left) / 2));
            if (statusWidth == 0 && detailsWidth == 0) reserved = 0;
            int textWidth = Math.Max(1, right - left - (reserved > 0 ? reserved + gap : 0));
            layout.Name = new Rectangle(left, bounds.Top + Theme.S(6), textWidth, Theme.S(20));
            layout.Path = new Rectangle(left, bounds.Top + Theme.S(28), textWidth, Theme.S(18));
            layout.Status = new Rectangle(right - reserved, layout.Name.Top, reserved, layout.Name.Height);
            layout.Details = new Rectangle(right - reserved, layout.Path.Top, reserved, layout.Path.Height);
            return layout;
        }

        private void DrawRow(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= shown.Count) return;
            Row r = shown[e.Index];
            bool selected = (e.State & DrawItemState.Selected) != 0;
            Graphics g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            Rectangle row = Rectangle.Inflate(e.Bounds, -Theme.S(4), -Theme.S(2));
            using (var back = new SolidBrush(Theme.Card)) g.FillRectangle(back, e.Bounds);
            Theme.FillRound(g, row, Theme.S(8),
                selected || r.Checked ? Theme.Sel : (e.Index == hover ? Theme.CardHover : Theme.Card));

            string status = r.Already ? Lang.T("scan.already")
                : r.RendererLike ? Lang.F("scan.renderer.tag", (int)r.Gpu)
                : r.Running ? Lang.T("scan.running.tag")
                : r.Pinned ? Lang.T("scan.picked.tag") : !r.Installed ? Lang.T("scan.stopped.tag") : "";
            string details = r.Running ? RunningProgram.FormatMemory(r.Memory) : "";
            if (r.Running && r.Count > 1)
                details += (details.Length > 0 ? " · " : "") + Lang.F("white.pick.procs", r.Count);
            RowLayout layout = LayoutRow(e.Bounds, status, details);
            Rectangle mark = layout.Check;
            using (var outline = Theme.Rounded(mark, Theme.S(4)))
            {
                using (var fill = new SolidBrush(r.Checked ? Theme.Accent : Theme.Bg)) g.FillPath(fill, outline);
                using (var pen = new Pen(r.Checked ? Theme.Accent : r.Already ? Theme.Faint : Theme.StrokeHi)) g.DrawPath(pen, outline);
            }
            if (r.Checked)
                using (var pen = new Pen(Theme.OnAccent, Math.Max(1.4f, Theme.S(2))))
                    g.DrawLines(pen, new[]
                    {
                        new Point(mark.Left + Theme.S(4), mark.Top + Theme.S(8)),
                        new Point(mark.Left + Theme.S(7), mark.Top + Theme.S(11)),
                        new Point(mark.Left + Theme.S(12), mark.Top + Theme.S(5))
                    });

            lock (iconGate)
            {
                Bitmap icon;
                if (iconCache.TryGetValue(r.Path, out icon) && icon != null) g.DrawImage(icon, layout.Icon);
                else Glyphs.Draw(g, "game", layout.Icon, r.Already ? Theme.Faint : Theme.Accent);
            }
            var flags = TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
            TextRenderer.DrawText(g, r.Name, Theme.UI(9.2f, true), layout.Name,
                r.Already ? Theme.Faint : Theme.Fg, flags | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, r.Path, Theme.UI(7.6f, false), layout.Path,
                Theme.Dim, flags | TextFormatFlags.PathEllipsis);
            Color statusColor = r.Already ? Theme.Faint : r.RendererLike ? Theme.Accent : r.Running ? Theme.Green : Theme.Dim;
            if (status.Length > 0)
                TextRenderer.DrawText(g, status, Theme.UI(7.6f, true), layout.Status,
                    statusColor, flags | TextFormatFlags.Right | TextFormatFlags.EndEllipsis);
            if (details.Length > 0)
                TextRenderer.DrawText(g, details, Theme.UI(7.6f, false), layout.Details,
                    Theme.Faint, flags | TextFormatFlags.Right | TextFormatFlags.EndEllipsis);
        }

        private void Post(Action a)
        {
            if (closed || !IsHandleCreated) return;
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
