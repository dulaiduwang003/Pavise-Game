// @author bdth 2074055628@qq.com
// 文件用途 扫描本机已安装的游戏并批量勾选加入目标库

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace PaviseApp
{
    internal class GameScanDialog : Form
    {
        private class Row
        {
            public string Name;
            public string Path;
            public string Root;
            public bool Checked;
            public bool Already;
        }

        public readonly List<ScanHit> Selected = new List<ScanHit>();

        private readonly HashSet<string> existing;
        private readonly List<Row> rows = new List<Row>();
        private readonly List<Row> shown = new List<Row>();
        private ListBox lst;
        private TextBox tbFilter;
        private Label lblInfo;
        private PillButton btnAdd, btnDeep, btnAll;
        private volatile bool closed;
        private volatile bool scanning;
        private int hover = -1;

        public GameScanDialog(IEnumerable<string> alreadyInLibrary)
        {
            existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (alreadyInLibrary != null)
                foreach (string p in alreadyInLibrary)
                    if (!string.IsNullOrEmpty(p)) existing.Add(p);

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(Theme.S(560), Theme.S(560));
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
            lblClose.SetBounds(Theme.S(524), Theme.S(10), Theme.S(26), Theme.S(26));
            lblClose.TextAlign = ContentAlignment.MiddleCenter;
            lblClose.Cursor = Cursors.Hand;
            lblClose.Click += delegate { DialogResult = DialogResult.Cancel; };

            tbFilter = Theme.MakeTextBox(Theme.S(16), Theme.S(44), Theme.S(430));
            tbFilter.TextChanged += delegate { Refill(); };

            btnAll = new PillButton(Lang.T("scan.all"));
            btnAll.SetBounds(Theme.S(454), Theme.S(44), Theme.S(94), Theme.S(30));
            btnAll.Click += delegate { ToggleAll(); };

            var listWrap = new RoundPanel();
            listWrap.SetBounds(Theme.S(16), Theme.S(84), Theme.S(532), Theme.S(392));
            listWrap.BackColor = Theme.Bg; listWrap.Fill = Theme.Card; listWrap.Border = Theme.Stroke; listWrap.Radius = Theme.S(12);
            listWrap.Padding = new Padding(Theme.S(6));
            lst = new ListBox();
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
                if (idx != hover) { hover = idx; lst.Invalidate(); }
            };
            lst.MouseLeave += delegate { if (hover != -1) { hover = -1; lst.Invalidate(); } };
            lst.MouseClick += delegate(object s, MouseEventArgs e)
            {
                int idx = lst.IndexFromPoint(e.Location);
                if (idx >= 0) ToggleAt(idx);
            };
            listWrap.Controls.Add(lst);

            lblInfo = new Label();
            lblInfo.Text = Lang.T("scan.busy");
            lblInfo.ForeColor = Theme.Dim;
            lblInfo.BackColor = Theme.Bg;
            lblInfo.Font = Theme.UI(8.25f, false);
            lblInfo.TextAlign = ContentAlignment.MiddleLeft;
            lblInfo.SetBounds(Theme.S(16), Theme.S(490), Theme.S(240), Theme.S(28));

            btnDeep = new PillButton(Lang.T("scan.deep"));
            btnDeep.SetBounds(Theme.S(262), Theme.S(486), Theme.S(112), Theme.S(34));
            btnDeep.Click += delegate { DeepScan(); };

            btnAdd = new PillButton(Lang.T("btn.add"), BtnKind.Primary);
            btnAdd.SetBounds(Theme.S(382), Theme.S(486), Theme.S(80), Theme.S(34));
            btnAdd.Click += delegate { Accept(); };

            var btnCancel = new PillButton(Lang.T("btn.cancel"));
            btnCancel.SetBounds(Theme.S(470), Theme.S(486), Theme.S(78), Theme.S(34));
            btnCancel.Click += delegate { DialogResult = DialogResult.Cancel; };

            Controls.AddRange(new Control[] { title, lblClose, tbFilter, btnAll, listWrap, lblInfo, btnDeep, btnAdd, btnCancel });
            Load += delegate { StartScan(null); };
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
                Post(delegate { Merge(hits); });
            });
            worker.IsBackground = true;
            worker.Start();
        }

        private bool IsClosed()
        {
            return closed;
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

        private void Merge(List<ScanHit> hits)
        {
            scanning = false;
            btnDeep.Enabled = true;

            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Row r in rows) known.Add(r.Path);

            int added = 0;
            foreach (ScanHit h in hits)
            {
                if (h == null || string.IsNullOrEmpty(h.Root)) continue;
                string exe = ResolveExe(h);
                if (exe == null || !known.Add(exe)) continue;
                rows.Add(new Row
                {
                    Name = string.IsNullOrEmpty(h.Name) ? Path.GetFileNameWithoutExtension(exe) : h.Name,
                    Path = exe,
                    Root = h.Root,
                    Already = existing.Contains(exe),
                    Checked = false
                });
                added++;
            }

            rows.Sort(delegate(Row a, Row b)
            {
                if (a.Already != b.Already) return a.Already ? 1 : -1;
                return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
            });

            int fresh = 0;
            foreach (Row r in rows) if (!r.Already) fresh++;
            lblInfo.Text = rows.Count == 0
                ? Lang.T("scan.none")
                : Lang.F("scan.count", rows.Count, fresh);
            Refill();
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
