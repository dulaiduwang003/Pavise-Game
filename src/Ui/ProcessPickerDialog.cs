// @author bdth 2074055628@qq.com
// 文件用途 选择需要加入名单的运行进程

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace AegisApp
{
    internal class ProcessPickerDialog : Form
    {
        public string SelectedName;
        public string SelectedPath;
        private ListBox lst;
        private TextBox tbFilter;
        private Label lblInfo;
        private readonly List<string> names = new List<string>();
        private readonly List<string> display = new List<string>();
        private readonly List<string> shown = new List<string>();
        private readonly List<string> paths = new List<string>();
        private readonly List<string> shownPaths = new List<string>();
        private volatile bool closed;

        private const int SampleMs = 400;

        private class Row
        {
            public string Name;
            public long Mem;
            public long Cpu0;
            public long Cpu1;
            public double Cpu;
            public string Title = "";
            public int Session = -1;
            public string Path;
        }

        public ProcessPickerDialog()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(Theme.S(460), Theme.S(520));
            BackColor = Theme.Bg;
            Font = Theme.UI(9.5f, false);

            var title = new Label();
            title.Text = Lang.T("dlg.pickproc");
            title.ForeColor = Theme.Fg;
            title.Font = Theme.UI(10.5f, true);
            title.SetBounds(Theme.S(16), Theme.S(12), Theme.S(200), Theme.S(24));
            title.MouseDown += DragMove;

            var lblClose = new Label();
            lblClose.Text = "✕";
            lblClose.ForeColor = Theme.Dim;
            lblClose.SetBounds(Theme.S(424), Theme.S(10), Theme.S(26), Theme.S(26));
            lblClose.TextAlign = ContentAlignment.MiddleCenter;
            lblClose.Cursor = Cursors.Hand;
            lblClose.Click += (s, e) => DialogResult = DialogResult.Cancel;

            tbFilter = Theme.MakeTextBox(Theme.S(16), Theme.S(44), Theme.S(428));
            tbFilter.TextChanged += (s, e) => Refill();

            var listWrap = new RoundPanel();
            listWrap.SetBounds(Theme.S(16), Theme.S(80), Theme.S(428), Theme.S(384));
            listWrap.BackColor = Theme.Bg; listWrap.Fill = Theme.Card; listWrap.Border = Theme.Stroke; listWrap.Radius = Theme.S(12);
            listWrap.Padding = new Padding(Theme.S(6));
            lst = new ListBox();
            lst.Dock = DockStyle.Fill;
            lst.HorizontalScrollbar = true;
            Theme.StyleList(lst);
            lst.DoubleClick += (s, e) => Accept();
            listWrap.Controls.Add(lst);

            lblInfo = new Label();
            lblInfo.Text = Lang.T("pick.busy");
            lblInfo.ForeColor = Theme.Dim;
            lblInfo.BackColor = Theme.Bg;
            lblInfo.Font = Theme.UI(8.25f, false);
            lblInfo.TextAlign = ContentAlignment.MiddleLeft;
            lblInfo.SetBounds(Theme.S(16), Theme.S(480), Theme.S(210), Theme.S(28));

            var btnOk = new PillButton(Lang.T("btn.add"), BtnKind.Primary);
            btnOk.SetBounds(Theme.S(240), Theme.S(474), Theme.S(100), Theme.S(34));
            btnOk.Click += (s, e) => Accept();
            var btnCancel = new PillButton(Lang.T("btn.cancel"));
            btnCancel.SetBounds(Theme.S(348), Theme.S(474), Theme.S(96), Theme.S(34));
            btnCancel.Click += (s, e) => DialogResult = DialogResult.Cancel;

            Controls.AddRange(new Control[] { title, lblClose, tbFilter, listWrap, lblInfo, btnOk, btnCancel });
            Load += OnLoadList;
            FormClosed += (s, e) => closed = true;
            MouseDown += DragMove;
            KeyPreview = true;
            KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Escape) DialogResult = DialogResult.Cancel;
                else if (e.KeyCode == Keys.Enter) { Accept(); e.SuppressKeyPress = true; }
            };
        }

        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ClassStyle |= 0x20000 ; return cp; }
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

        private void OnLoadList(object s, EventArgs e)
        {
            var worker = new Thread(SampleAndFill);
            worker.IsBackground = true;
            worker.Start();
        }

        private void SampleAndFill()
        {
            List<Row> rows = Collect();
            Post(() =>
            {
                names.Clear();
                display.Clear();
                paths.Clear();
                foreach (Row r in rows)
                {
                    names.Add(r.Name);
                    display.Add(r.Name + "   —   " + UsageText(r)
                        + (string.IsNullOrEmpty(r.Path) ? "" : "    " + r.Path));
                    paths.Add(r.Path);
                }
                lblInfo.Text = Lang.F("pick.count", rows.Count);
                Refill();
            });
        }

        private static List<Row> Collect()
        {
            int currentSession = -1;
            try
            {
                using (Process current = Process.GetCurrentProcess())
                    currentSession = current.SessionId;
            }
            catch { }
            var cpu0 = new Dictionary<int, long>();
            SnapCpu(cpu0, currentSession);

            var sw = Stopwatch.StartNew();
            Thread.Sleep(SampleMs);
            sw.Stop();
            long elapsed = sw.Elapsed.Ticks;
            int cores = Environment.ProcessorCount;
            if (cores <= 0) cores = 1;

            var agg = new Dictionary<string, Row>(StringComparer.OrdinalIgnoreCase);
            Process[] all;
            try { all = Process.GetProcesses(); } catch { return new List<Row>(); }
            foreach (Process p in all)
            {
                try
                {
                    if (p.Id <= 4) continue;
                    string nm = p.ProcessName;
                    int session = -1;
                    try { session = p.SessionId; } catch { }
                    if (currentSession < 0 || session != currentSession) continue;
                    string path = null;
                    IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, p.Id);
                    if (h != IntPtr.Zero)
                    {
                        try { path = Native.ImagePath(h); }
                        finally { Native.CloseHandle(h); }
                    }
                    string normalizedPath = WhitelistRule.NormalizeImagePath(path);
                    string key = normalizedPath.Length > 0 ? "P:" + normalizedPath
                        : "N:" + nm + "|S:" + session;

                    Row r;
                    if (!agg.TryGetValue(key, out r))
                    {
                        r = new Row { Name = nm, Path = normalizedPath, Session = session };
                        agg[key] = r;
                    }

                    try { r.Mem += p.WorkingSet64; } catch { }
                    long first;
                    if (cpu0.TryGetValue(p.Id, out first)) r.Cpu0 += first;
                    try { r.Cpu1 += p.TotalProcessorTime.Ticks; } catch { }
                    if (r.Title.Length == 0)
                    {
                        try { string t = p.MainWindowTitle; if (!string.IsNullOrEmpty(t)) r.Title = t; }
                        catch { }
                    }
                }
                catch { }
                finally { p.Dispose(); }
            }

            var rows = new List<Row>(agg.Values);
            foreach (Row r in rows)
            {
                long delta = r.Cpu1 - r.Cpu0;
                if (delta < 0) delta = 0;
                r.Cpu = elapsed > 0 ? delta * 100.0 / ((double)elapsed * cores) : 0;
            }

            rows.Sort((a, b) =>
            {
                if (a.Mem != b.Mem) return b.Mem.CompareTo(a.Mem);
                if (a.Cpu != b.Cpu) return b.Cpu.CompareTo(a.Cpu);
                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });
            return rows;
        }

        private static void SnapCpu(Dictionary<int, long> into, int currentSession)
        {
            Process[] all;
            try { all = Process.GetProcesses(); } catch { return; }
            foreach (Process p in all)
            {
                try
                {
                    if (p.Id <= 4) continue;
                    int session;
                    try { session = p.SessionId; } catch { continue; }
                    if (currentSession < 0 || session != currentSession) continue;
                    long t = 0;
                    try { t = p.TotalProcessorTime.Ticks; } catch { }
                    into[p.Id] = t;
                }
                catch { }
                finally { p.Dispose(); }
            }
        }

        private static string UsageText(Row r)
        {
            int pct = (int)Math.Round(r.Cpu);
            if (pct < 0) pct = 0; else if (pct > 100) pct = 100;
            string s = FmtMem(r.Mem) + "    CPU " + pct + "%";
            if (r.Title.Length > 0) s += "    " + r.Title;
            else if (r.Session == 0) s += "    " + Lang.T("dlg.bgsvc");
            return s;
        }

        private static string FmtMem(long b)
        {
            if (b >= (1L << 30)) return (b / (double)(1L << 30)).ToString("0.0") + " GB";
            if (b >= (1L << 20)) return (b / (1L << 20)) + " MB";
            if (b >= (1L << 10)) return (b / (1L << 10)) + " KB";
            return b + " B";
        }

        private void Post(Action a)
        {
            if (closed) return;
            try { BeginInvoke((MethodInvoker)(() => { if (!closed && !IsDisposed) a(); })); }
            catch { }
        }

        private void Refill()
        {
            string f = tbFilter.Text.Trim();
            lst.BeginUpdate();
            lst.Items.Clear();
            shown.Clear();
            shownPaths.Clear();
            for (int i = 0; i < names.Count; i++)
                if (f.Length == 0 || display[i].IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    lst.Items.Add(display[i]);
                    shown.Add(names[i]);
                    shownPaths.Add(paths[i]);
                }
            lst.EndUpdate();
        }

        private void Accept()
        {
            int i = lst.SelectedIndex;
            if (i < 0 || i >= shown.Count) return;
            SelectedName = shown[i];
            SelectedPath = i < shownPaths.Count ? shownPaths[i] : null;
            DialogResult = DialogResult.OK;
        }
    }

}
