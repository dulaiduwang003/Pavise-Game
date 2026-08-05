// @author bdth 2074055628@qq.com
// 文件用途 编辑用户白名单和排除项

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace PaviseApp
{
    internal sealed class WhitelistDialog : Form
    {
        private readonly GameMode mode;
        private readonly ListBox list = new ListBox();
        private readonly FlatCombo scope = new FlatCombo();
        private readonly object refreshSync = new object();
        private bool refreshWorker;
        private bool refreshPending;
        private int refreshGeneration;
        private volatile bool closed;

        private sealed class ScopeChoice
        {
            public readonly WhitelistRuleKind Kind;
            public readonly string Text;
            public ScopeChoice(WhitelistRuleKind kind, string text) { Kind = kind; Text = text; }
            public override string ToString() { return Text; }
        }

        private sealed class RuleItem
        {
            public readonly WhitelistRuleView View;
            public RuleItem(WhitelistRuleView view) { View = view; }
            public override string ToString()
            {
                string kind = View.Rule.Kind == WhitelistRuleKind.ApplicationFamily ? Lang.T("white.kind.family")
                    : (View.Rule.Kind == WhitelistRuleKind.ExactPath ? Lang.T("white.kind.path") : Lang.T("white.kind.name"));
                return "[" + kind + "]  " + View.Rule.Value + "    ·    "
                    + (View.CurrentMatches < 0
                        ? Lang.T("white.matches.pending")
                        : Lang.F("white.matches", View.CurrentMatches))
                    + (View.Required
                        ? "    ·    " + Lang.T("white.required.badge") : "");
            }
        }

        public WhitelistDialog(GameMode gameMode)
        {
            mode = gameMode;
            Text = Lang.T("nav.white");
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            ClientSize = new Size(Theme.S(760), Theme.S(470));
            BackColor = Theme.Bg; ForeColor = Theme.Fg; Font = Theme.UI(9.5f, false);

            var title = new Label();
            title.Text = Lang.T("nav.white"); title.ForeColor = Theme.Fg; title.BackColor = Theme.Bg; title.Font = Theme.UI(14f, true);
            title.SetBounds(Theme.S(22), Theme.S(18), Theme.S(700), Theme.S(30));
            title.MouseDown += DragMove;

            var lblClose = new Label();
            lblClose.Text = "✕";
            lblClose.ForeColor = Theme.Dim;
            lblClose.BackColor = Theme.Bg;
            lblClose.SetBounds(Theme.S(722), Theme.S(12), Theme.S(26), Theme.S(26));
            lblClose.TextAlign = ContentAlignment.MiddleCenter;
            lblClose.Cursor = Cursors.Hand;
            lblClose.Click += delegate { Close(); };
            var note = new Label();
            note.Text = Lang.T("white.desc"); note.ForeColor = Theme.Dim; note.BackColor = Theme.Bg; note.Font = Theme.UI(8.5f, false);
            note.SetBounds(Theme.S(22), Theme.S(50), Theme.S(716), Theme.S(38));

            var wrap = new RoundPanel();
            wrap.SetBounds(Theme.S(22), Theme.S(98), Theme.S(510), Theme.S(344));
            wrap.BackColor = Theme.Bg; wrap.Fill = Theme.Card; wrap.Border = Theme.Stroke; wrap.Radius = Theme.S(12); wrap.Padding = new Padding(Theme.S(8));
            list.Dock = DockStyle.Fill; list.HorizontalScrollbar = true; Theme.StyleList(list); wrap.Controls.Add(list);

            var scopeLabel = new Label();
            scopeLabel.Text = Lang.T("white.scope"); scopeLabel.ForeColor = Theme.Dim; scopeLabel.BackColor = Theme.Bg;
            scopeLabel.SetBounds(Theme.S(548), Theme.S(98), Theme.S(190), Theme.S(20));
            scope.SetBounds(Theme.S(548), Theme.S(121), Theme.S(190), Theme.S(32));
            scope.Items.Add(new ScopeChoice(
                WhitelistRuleKind.ApplicationFamily,
                Lang.T("white.kind.family.recommended")));
            scope.Items.Add(new ScopeChoice(
                WhitelistRuleKind.ExactPath, Lang.T("white.kind.path")));
            scope.Items.Add(new ScopeChoice(WhitelistRuleKind.LegacyName, Lang.T("white.kind.name.compat")));
            scope.SelectedIndex = 0;

            var running = Button(Lang.T("btn.pick"), 548, 170, BtnKind.Primary); running.Click += delegate { PickRunning(); };
            var browse = Button(Lang.T("btn.browse"), 548, 214, BtnKind.Normal); browse.Click += delegate { Browse(); };
            var remove = Button(Lang.T("btn.remove"), 548, 274, BtnKind.Normal); remove.Click += delegate { Remove(); };
            var reset = Button(Lang.T("btn.reset"), 548, 406, BtnKind.Danger);
            reset.Click += delegate
            {
                if (MessageBox.Show(this, Lang.T("white.reset.confirm"), "Pavise",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
                if (!mode.ResetWhitelist()) ShowMutationError();
                RefreshList();
            };
            Controls.AddRange(new Control[] { title, lblClose, note, wrap, scopeLabel, scope, running, browse, remove, reset });
            FormClosed += delegate { closed = true; };
            Shown += delegate { RefreshList(); };
            MouseDown += DragMove;
            KeyPreview = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); };
            FillList(mode.GetWhitelistRulesFast());
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

        private PillButton Button(string text, int x, int y, BtnKind kind)
        {
            var b = new PillButton(text, kind); b.SetBounds(Theme.S(x), Theme.S(y), Theme.S(190), Theme.S(36)); return b;
        }

        private void PickRunning()
        {
            using (var dlg = new ProcessPickerDialog())
                if (dlg.ShowDialog(this) == DialogResult.OK && dlg.SelectedName != null)
                { AddSelected(dlg.SelectedName, dlg.SelectedPath, false); }
        }

        private void Browse()
        {
            using (var dlg = new OpenFileDialog())
            {
                dlg.Title = Lang.T("ofd.white");
                dlg.Filter = Lang.T("ofd.filter");
                // 同游戏库：商店类目录拒绝读取 exe 时，对话框自校验会弹系统权限错误
                dlg.CheckFileExists = false;
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    string file = dlg.FileName;
                    if (Shortcut.IsLnk(file))
                    {
                        string target;
                        string arguments;
                        if (!Shortcut.TryResolve(file, out target, out arguments))
                        {
                            MessageBox.Show(this, Lang.T("white.shortcut.invalid"), "Pavise",
                                MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            return;
                        }
                        file = target;
                        AddSelected(Path.GetFileNameWithoutExtension(file), file,
                            !string.IsNullOrWhiteSpace(arguments));
                        return;
                    }
                    AddSelected(Path.GetFileNameWithoutExtension(file), file, false);
                }
            }
        }

        private void AddSelected(string name, string path, bool parameterizedShortcut)
        {
            var selected = scope.SelectedItem as ScopeChoice;
            WhitelistRuleKind kind = selected == null ? WhitelistRuleKind.ExactPath : selected.Kind;
            if (kind != WhitelistRuleKind.LegacyName && string.IsNullOrWhiteSpace(path))
            {
                MessageBox.Show(this, Lang.T("white.path.required"), "Pavise",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (kind == WhitelistRuleKind.ApplicationFamily
                && (parameterizedShortcut || WhitelistRule.IsUnsafeFamilyAnchor(path)))
            {
                MessageBox.Show(this, Lang.T("white.family.unsafe"), "Pavise",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            bool added = kind == WhitelistRuleKind.ApplicationFamily ? mode.AddWhitelistFamily(path)
                : (kind == WhitelistRuleKind.ExactPath ? mode.AddWhitelistPath(path) : mode.AddWhitelist(name));
            if (!added)
                ShowMutationError();
            RefreshList();
        }

        private void Remove()
        {
            if (list.SelectedItem == null) return;
            RuleItem item = list.SelectedItem as RuleItem;
            if (item == null) return;
            if (!mode.RemoveWhitelistRule(item.View.Rule.Key)) ShowMutationError();
            RefreshList();
        }

        private void ShowMutationError()
        {
            string message = mode.WhitelistLastError;
            if (string.IsNullOrEmpty(message)) message = Lang.T("white.duplicate");
            MessageBox.Show(this, message, "Pavise",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private void RefreshList()
        {
            FillList(mode.GetWhitelistRulesFast());
            lock (refreshSync)
            {
                refreshGeneration++;
                refreshPending = true;
                if (refreshWorker) return;
                refreshWorker = true;
            }
            ThreadPool.QueueUserWorkItem(delegate
            {
                while (true)
                {
                    int generation;
                    lock (refreshSync)
                    {
                        if (!refreshPending || closed)
                        {
                            refreshWorker = false;
                            return;
                        }
                        refreshPending = false;
                        generation = refreshGeneration;
                    }
                    List<WhitelistRuleView> views = mode.GetWhitelistRules();
                    if (closed) continue;
                    try
                    {
                        BeginInvoke((MethodInvoker)delegate
                        {
                            lock (refreshSync)
                                if (closed || generation != refreshGeneration) return;
                            FillList(views);
                        });
                    }
                    catch { }
                }
            });
        }

        private void FillList(IList<WhitelistRuleView> views)
        {
            if (closed || IsDisposed) return;
            list.BeginUpdate(); list.Items.Clear();
            if (views != null)
                foreach (WhitelistRuleView view in views) list.Items.Add(new RuleItem(view));
            list.EndUpdate();
        }
    }
}
