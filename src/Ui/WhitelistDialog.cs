// @author bdth 2074055628@qq.com
// 文件用途 编辑用户白名单和排除项

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace AegisApp
{
    internal sealed class WhitelistDialog : Form
    {
        private readonly GameMode mode;
        private readonly ListBox list = new ListBox();
        private readonly ComboBox scope = new ComboBox();
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
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            ClientSize = new Size(Theme.S(760), Theme.S(470));
            BackColor = Theme.Bg; ForeColor = Theme.Fg; Font = Theme.UI(9.5f, false);

            var title = new Label();
            title.Text = Lang.T("nav.white"); title.ForeColor = Theme.Fg; title.BackColor = Theme.Bg; title.Font = Theme.UI(14f, true);
            title.SetBounds(Theme.S(22), Theme.S(18), Theme.S(700), Theme.S(30));
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
            scope.DropDownStyle = ComboBoxStyle.DropDownList;
            scope.FlatStyle = FlatStyle.Flat;
            scope.BackColor = Theme.Card; scope.ForeColor = Theme.Fg;
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
                if (MessageBox.Show(this, Lang.T("white.reset.confirm"), "Aegis",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
                if (!mode.ResetWhitelist()) ShowMutationError();
                RefreshList();
            };
            Controls.AddRange(new Control[] { title, note, wrap, scopeLabel, scope, running, browse, remove, reset });
            FormClosed += delegate { closed = true; };
            Shown += delegate { RefreshList(); };
            FillList(mode.GetWhitelistRulesFast());
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
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    string file = dlg.FileName;
                    if (Shortcut.IsLnk(file))
                    {
                        string target;
                        string arguments;
                        if (!Shortcut.TryResolve(file, out target, out arguments))
                        {
                            MessageBox.Show(this, Lang.T("white.shortcut.invalid"), "Aegis",
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
                MessageBox.Show(this, Lang.T("white.path.required"), "Aegis",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (kind == WhitelistRuleKind.ApplicationFamily
                && (parameterizedShortcut || WhitelistRule.IsUnsafeFamilyAnchor(path)))
            {
                MessageBox.Show(this, Lang.T("white.family.unsafe"), "Aegis",
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
            MessageBox.Show(this, message, "Aegis",
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
