// @author bdth 2074055628@qq.com
// 文件用途 选择英雄联盟安装目录 体检附加层并在二次确认后直接删除

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace PaviseApp
{
    internal sealed class LolAddonDialog : Form
    {
        private readonly TextBox pathBox = new TextBox();
        private readonly Label status = new Label();
        private readonly ListBox list = new TechListBox();
        private readonly PillButton btnDelete;
        private readonly IList<string> hints;
        private string resolvedRoot;
        private int busy;

        public LolAddonDialog() : this(null) { }

        public LolAddonDialog(IList<string> rootHints)
        {
            hints = rootHints;
            Text = Lang.T("addon.title");
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            ClientSize = new Size(Theme.S(680), Theme.S(470));
            BackColor = Theme.Bg; ForeColor = Theme.Fg; Font = Theme.UI(9.5f, false);

            var title = new Label();
            title.Text = Lang.T("addon.title");
            title.ForeColor = Theme.Fg; title.BackColor = Theme.Bg; title.Font = Theme.UI(14f, true);
            title.SetBounds(Theme.S(22), Theme.S(18), Theme.S(600), Theme.S(30));
            title.MouseDown += DragMove;

            var close = new Label();
            close.Text = "✕"; close.ForeColor = Theme.Dim; close.BackColor = Theme.Bg;
            close.SetBounds(Theme.S(642), Theme.S(12), Theme.S(26), Theme.S(26));
            close.TextAlign = ContentAlignment.MiddleCenter;
            close.Cursor = Cursors.Hand;
            close.Click += delegate { Close(); };

            var note = new Label();
            note.Text = Lang.T("addon.desc");
            note.ForeColor = Theme.Dim; note.BackColor = Theme.Bg; note.Font = Theme.UI(8.5f, false);
            note.SetBounds(Theme.S(22), Theme.S(50), Theme.S(636), Theme.S(56));

            pathBox.SetBounds(Theme.S(22), Theme.S(112), Theme.S(500), Theme.S(30));
            pathBox.BackColor = Theme.Card; pathBox.ForeColor = Theme.Fg;
            pathBox.BorderStyle = BorderStyle.FixedSingle;
            pathBox.AllowDrop = true;
            pathBox.DragEnter += delegate(object s, DragEventArgs e)
            {
                e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop)
                    ? DragDropEffects.Copy : DragDropEffects.None;
            };
            pathBox.DragDrop += delegate(object s, DragEventArgs e)
            {
                var files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (files != null && files.Length > 0) { pathBox.Text = files[0]; Inspect(); }
            };

            var browse = new PillButton(Lang.T("scan.browse"));
            browse.SetBounds(Theme.S(534), Theme.S(110), Theme.S(124), Theme.S(34));
            browse.Click += delegate { Browse(); };

            status.SetBounds(Theme.S(22), Theme.S(150), Theme.S(636), Theme.S(24));
            status.ForeColor = Theme.Dim; status.BackColor = Theme.Bg; status.Font = Theme.UI(8.6f, false);
            status.Text = Lang.T("addon.hint.pick");

            var wrap = new RoundPanel();
            wrap.SetBounds(Theme.S(22), Theme.S(180), Theme.S(636), Theme.S(210));
            wrap.BackColor = Theme.Bg; wrap.Fill = Theme.Card; wrap.Border = Theme.Stroke;
            wrap.Radius = Theme.S(12); wrap.Padding = new Padding(Theme.S(8));
            list.Dock = DockStyle.Fill;
            Theme.StyleList(list);
            wrap.Controls.Add(list);

            btnDelete = new PillButton(Lang.T("addon.delete"), BtnKind.Danger);
            btnDelete.SetBounds(Theme.S(458), Theme.S(404), Theme.S(200), Theme.S(38));
            btnDelete.Enabled = false;
            btnDelete.Click += delegate { Execute(); };

            var cancel = new PillButton(Lang.T("notes.close"));
            cancel.SetBounds(Theme.S(242), Theme.S(404), Theme.S(200), Theme.S(38));
            cancel.Click += delegate { Close(); };

            Controls.AddRange(new Control[] { title, close, note, pathBox, browse, status, wrap, btnDelete, cancel });
            MouseDown += DragMove;
            KeyPreview = true;
            KeyDown += delegate(object s, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) Close(); };
            Shown += delegate { AutoFill(); };
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

        private void AutoFill()
        {
            if (hints == null) return;
            foreach (string candidate in hints)
            {
                string root;
                string error;
                if (!LolAddonCleaner.TryResolveRoot(candidate, out root, out error)) continue;
                pathBox.Text = root;
                Inspect();
                return;
            }
        }

        private void Browse()
        {
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = Lang.T("addon.pick.desc");
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                pathBox.Text = dialog.SelectedPath;
                Inspect();
            }
        }

        private void Inspect()
        {
            if (Interlocked.Exchange(ref busy, 1) == 1) return;
            string input = pathBox.Text;
            btnDelete.Enabled = false;
            list.Items.Clear();
            status.Text = Lang.T("addon.hint.busy");
            ThreadPool.QueueUserWorkItem(delegate
            {
                string root;
                string error;
                LolAddonCleaner.Inspection inspection = null;
                bool ok = LolAddonCleaner.TryResolveRoot(input, out root, out error);
                if (ok) inspection = LolAddonCleaner.Inspect(root);
                Interlocked.Exchange(ref busy, 0);
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        if (IsDisposed) return;
                        if (!ok) { resolvedRoot = null; status.Text = error; return; }
                        resolvedRoot = root;
                        pathBox.Text = root;
                        Render(inspection);
                    });
                }
                catch { }
            });
        }

        private void Render(LolAddonCleaner.Inspection inspection)
        {
            list.Items.Clear();
            if (inspection == null) { status.Text = Lang.T("addon.hint.fail"); return; }
            if (!string.IsNullOrEmpty(inspection.Error)) { status.Text = inspection.Error; return; }
            if (!inspection.IsValidRoot) { status.Text = Lang.T("addon.hint.notlol"); return; }

            foreach (LolAddonCleaner.CandidateInfo candidate in inspection.Candidates)
                list.Items.Add(candidate.RelativePath
                    + (candidate.Exists
                        ? "    " + FormatBytes(candidate.Bytes) + " · " + Lang.F("addon.files", candidate.FileCount)
                        : "    " + Lang.T("addon.absent")));

            if (inspection.IsBlocked)
            {
                status.Text = Lang.F("addon.hint.blocked",
                    string.Join("、", inspection.BlockingProcesses.ToArray()));
                btnDelete.Enabled = false;
                return;
            }
            if (inspection.CandidateCount == 0)
            {
                status.Text = Lang.T("addon.hint.clean");
                btnDelete.Enabled = false;
                return;
            }
            status.Text = Lang.F("addon.hint.ready",
                inspection.CandidateCount, FormatBytes(inspection.CandidateBytes));
            btnDelete.Enabled = inspection.CanDelete;
        }

        private void Execute()
        {
            if (string.IsNullOrEmpty(resolvedRoot)) return;
            if (!PaviseDialog.Confirm(this, App.DisplayName, Lang.F("addon.confirm", resolvedRoot), DlgKind.Warn)) return;
            if (Interlocked.Exchange(ref busy, 1) == 1) return;
            btnDelete.Enabled = false;
            status.Text = Lang.T("addon.hint.deleting");
            string root = resolvedRoot;
            ThreadPool.QueueUserWorkItem(delegate
            {
                LolAddonCleaner.OperationResult result = null;
                try { result = LolAddonCleaner.Delete(root); }
                catch { }
                Interlocked.Exchange(ref busy, 0);
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        if (IsDisposed) return;
                        status.Text = result == null ? Lang.T("addon.hint.fail")
                            : (result.Message ?? Lang.F("addon.hint.done",
                                result.DeletedCount, FormatBytes(result.Bytes)));
                        Inspect();
                    });
                }
                catch { }
            });
        }

        internal static string FormatBytes(long bytes)
        {
            if (bytes >= 1073741824L) return (bytes / 1073741824.0).ToString("0.0") + " GB";
            if (bytes >= 1048576L) return (bytes / 1048576.0).ToString("0") + " MB";
            if (bytes <= 0) return "0 MB";
            return (bytes / 1024.0).ToString("0") + " KB";
        }
    }
}
