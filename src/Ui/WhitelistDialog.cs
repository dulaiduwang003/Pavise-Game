// @author bdth 2074055628@qq.com
// 文件用途 编辑用户白名单和排除项

using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace AegisApp
{
    internal sealed class WhitelistDialog : Form
    {
        private readonly GameMode mode;
        private readonly ListBox list = new ListBox();

        public WhitelistDialog(GameMode gameMode)
        {
            mode = gameMode;
            Text = Lang.T("nav.white");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            ClientSize = new Size(Theme.S(560), Theme.S(440));
            BackColor = Theme.Bg; ForeColor = Theme.Fg; Font = Theme.UI(9.5f, false);

            var title = new Label();
            title.Text = Lang.T("nav.white"); title.ForeColor = Theme.Fg; title.BackColor = Theme.Bg; title.Font = Theme.UI(14f, true);
            title.SetBounds(Theme.S(22), Theme.S(18), Theme.S(500), Theme.S(30));
            var note = new Label();
            note.Text = Lang.T("white.desc"); note.ForeColor = Theme.Dim; note.BackColor = Theme.Bg; note.Font = Theme.UI(8.5f, false);
            note.SetBounds(Theme.S(22), Theme.S(50), Theme.S(516), Theme.S(38));

            var wrap = new RoundPanel();
            wrap.SetBounds(Theme.S(22), Theme.S(98), Theme.S(340), Theme.S(314));
            wrap.BackColor = Theme.Bg; wrap.Fill = Theme.Card; wrap.Border = Theme.Stroke; wrap.Radius = Theme.S(12); wrap.Padding = new Padding(Theme.S(8));
            list.Dock = DockStyle.Fill; Theme.StyleList(list); wrap.Controls.Add(list);

            var running = Button(Lang.T("btn.pick"), 376, 98, BtnKind.Primary); running.Click += delegate { PickRunning(); };
            var browse = Button(Lang.T("btn.browse"), 376, 142, BtnKind.Normal); browse.Click += delegate { Browse(); };
            var remove = Button(Lang.T("btn.remove"), 376, 202, BtnKind.Normal); remove.Click += delegate { Remove(); };
            var reset = Button(Lang.T("btn.reset"), 376, 376, BtnKind.Danger); reset.Click += delegate { mode.ResetWhitelist(); RefreshList(); };
            Controls.AddRange(new Control[] { title, note, wrap, running, browse, remove, reset });
            RefreshList();
        }

        private PillButton Button(string text, int x, int y, BtnKind kind)
        {
            var b = new PillButton(text, kind); b.SetBounds(Theme.S(x), Theme.S(y), Theme.S(162), Theme.S(36)); return b;
        }

        private void PickRunning()
        {
            using (var dlg = new ProcessPickerDialog())
                if (dlg.ShowDialog(this) == DialogResult.OK && dlg.SelectedName != null)
                { mode.AddWhitelist(dlg.SelectedName); RefreshList(); }
        }

        private void Browse()
        {
            using (var dlg = new OpenFileDialog())
            {
                dlg.Filter = Lang.T("ofd.filter");
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    string file = dlg.FileName;
                    if (Shortcut.IsLnk(file))
                    {
                        string target = Shortcut.ResolveTarget(file);
                        if (!string.IsNullOrEmpty(target)) file = target;
                    }
                    mode.AddWhitelist(Path.GetFileNameWithoutExtension(file));
                    RefreshList();
                }
            }
        }

        private void Remove()
        {
            if (list.SelectedItem == null) return;
            mode.RemoveWhitelist((string)list.SelectedItem); RefreshList();
        }

        private void RefreshList()
        {
            list.BeginUpdate(); list.Items.Clear();
            foreach (string s in mode.GetWhitelist()) list.Items.Add(s);
            list.EndUpdate();
        }
    }
}
