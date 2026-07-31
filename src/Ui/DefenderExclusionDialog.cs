// @author bdth 2074055628@qq.com
// 文件用途 逐目录勾选 Defender 扫描排除并展示其安全代价

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace AegisApp
{
    internal sealed class DefenderExclusionDialog : Form
    {
        private const int DlgW = 660, DlgH = 540;

        private sealed class Row
        {
            public string Name;
            public string Root;
            public Toggle Switch;
            public Label State;
            public bool Excluded;
        }

        private readonly GameMode gameMode;
        private readonly List<Row> rows = new List<Row>();
        private List<string> systemList;

        public DefenderExclusionDialog(GameMode mode)
        {
            gameMode = mode;
            Text = Lang.T("def.title");
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            ClientSize = new Size(Theme.S(DlgW), Theme.S(DlgH));
            BackColor = Theme.Bg; ForeColor = Theme.Fg; Font = Theme.UI(9.5f, false);

            var title = new Label();
            title.Text = Lang.T("def.title");
            title.ForeColor = Theme.Fg; title.BackColor = Theme.Bg; title.Font = Theme.UI(14f, true);
            title.UseCompatibleTextRendering = false;
            title.SetBounds(Theme.S(22), Theme.S(18), Theme.S(DlgW - 100), Theme.S(30));
            title.MouseDown += DragMove;

            var lblClose = new Label();
            lblClose.Text = "✕";
            lblClose.ForeColor = Theme.Dim; lblClose.BackColor = Theme.Bg;
            lblClose.TextAlign = ContentAlignment.MiddleCenter;
            lblClose.Cursor = Cursors.Hand;
            lblClose.SetBounds(Theme.S(DlgW - 46), Theme.S(16), Theme.S(26), Theme.S(26));
            lblClose.Click += delegate { Close(); };

            var warn = new RoundPanel();
            warn.SetBounds(Theme.S(22), Theme.S(56), Theme.S(DlgW - 44), Theme.S(94));
            warn.Fill = Theme.Card; warn.Border = Theme.Danger; warn.Radius = Theme.S(12);

            var warnTitle = new Label();
            warnTitle.Text = Lang.T("def.warn.title");
            warnTitle.ForeColor = Theme.Danger; warnTitle.BackColor = Theme.Card; warnTitle.Font = Theme.UI(9.5f, true);
            warnTitle.UseCompatibleTextRendering = false;
            warnTitle.SetBounds(Theme.S(18), Theme.S(12), Theme.S(DlgW - 80), Theme.S(20));

            var warnBody = new Label();
            warnBody.Text = Lang.T("def.warn.body");
            warnBody.ForeColor = Theme.Dim; warnBody.BackColor = Theme.Card; warnBody.Font = Theme.UI(8.5f, false);
            warnBody.UseCompatibleTextRendering = false;
            warnBody.SetBounds(Theme.S(18), Theme.S(34), Theme.S(DlgW - 80), Theme.S(52));
            warn.Controls.Add(warnTitle); warn.Controls.Add(warnBody);

            var scroll = new DBPanel();
            scroll.SetBounds(Theme.S(22), Theme.S(160), Theme.S(DlgW - 44), Theme.S(DlgH - 226));
            scroll.BackColor = Theme.Bg;
            scroll.AutoScroll = true;
            Native.Dark(scroll);

            BuildRows(scroll);

            var btnClearAll = new PillButton(Lang.T("def.clearall"), BtnKind.Danger);
            btnClearAll.Bg = Theme.Bg;
            btnClearAll.SetBounds(Theme.S(22), Theme.S(DlgH - 52), Theme.S(200), Theme.S(36));
            btnClearAll.Click += delegate { ClearAll(); };

            var btnClose = new PillButton(Lang.T("notes.close"), BtnKind.Primary);
            btnClose.Bg = Theme.Bg;
            btnClose.SetBounds(Theme.S(DlgW - 162), Theme.S(DlgH - 52), Theme.S(140), Theme.S(36));
            btnClose.Click += delegate { Close(); };

            Controls.AddRange(new Control[] { title, lblClose, warn, scroll, btnClearAll, btnClose });
            MouseDown += DragMove;
        }

        private void BuildRows(Control parent)
        {
            DefenderState state = DefenderExclusion.QueryState();
            if (state != DefenderState.Active)
            {
                var note = new Label();
                note.Text = Lang.T(state == DefenderState.Disabled ? "def.off" : "def.unavailable");
                note.ForeColor = state == DefenderState.Disabled ? Theme.Dim : Theme.Danger;
                note.BackColor = Theme.Bg; note.Font = Theme.UI(9f, false);
                note.UseCompatibleTextRendering = false;
                note.SetBounds(Theme.S(4), Theme.S(8), Theme.S(DlgW - 80), Theme.S(72));
                parent.Controls.Add(note);
                return;
            }

            systemList = DefenderExclusion.QuerySystem();
            List<GameProfile> profiles = gameMode.GetProfiles();

            if (systemList == null)
            {
                var fail = new Label();
                fail.Text = Lang.T("def.unavailable");
                fail.ForeColor = Theme.Danger; fail.BackColor = Theme.Bg; fail.Font = Theme.UI(9f, false);
                fail.UseCompatibleTextRendering = false;
                fail.SetBounds(Theme.S(4), Theme.S(8), Theme.S(DlgW - 80), Theme.S(48));
                parent.Controls.Add(fail);
                return;
            }

            List<string> owned = DefenderExclusion.OwnedByAegis();

            if (profiles.Count == 0 && owned.Count == 0)
            {
                var empty = new Label();
                empty.Text = Lang.T("def.nogames");
                empty.ForeColor = Theme.Dim; empty.BackColor = Theme.Bg; empty.Font = Theme.UI(9f, false);
                empty.UseCompatibleTextRendering = false;
                empty.SetBounds(Theme.S(4), Theme.S(8), Theme.S(DlgW - 80), Theme.S(48));
                parent.Controls.Add(empty);
                return;
            }

            int y = 2;
            var seen = new List<string>();
            foreach (GameProfile g in profiles)
            {
                string root = DefenderExclusion.Normalize(g.Root);
                if (root.Length == 0 || DefenderExclusion.Contains(seen, root)) continue;
                seen.Add(root);
                AddRow(parent, ref y, g.Name, root);
            }
            foreach (string path in owned)
            {
                string root = DefenderExclusion.Normalize(path);
                if (root.Length == 0 || DefenderExclusion.Contains(seen, root)) continue;
                seen.Add(root);
                AddRow(parent, ref y, LeafName(root), root);
            }
        }

        private static string LeafName(string root)
        {
            string trimmed = root.TrimEnd('\\');
            int cut = trimmed.LastIndexOf('\\');
            string leaf = cut >= 0 && cut + 1 < trimmed.Length ? trimmed.Substring(cut + 1) : trimmed;
            return leaf.Length == 0 ? root : leaf;
        }

        private void AddRow(Control parent, ref int y, string name, string root)
        {
            bool excluded = DefenderExclusion.IsExcludedInSystem(systemList, root);
            var row = new Row { Name = name, Root = root, Excluded = excluded };

            var sw = new Toggle();
            sw.Size = new Size(Theme.S(46), Theme.S(24));
            sw.Bg = Theme.Card;
            sw.SetSilently(excluded);
            row.Switch = sw;
            sw.CheckedChanged += delegate { OnToggle(row); };

            SettingCard card = MakeRowCard(parent, y, name, root, sw);
            row.State = new Label();
            row.State.BackColor = Theme.Card;
            row.State.Font = Theme.UI(8f, true);
            row.State.UseCompatibleTextRendering = false;
            row.State.TextAlign = ContentAlignment.MiddleRight;
            row.State.SetBounds(Theme.S(DlgW - 44 - 190), Theme.S(20), Theme.S(96), Theme.S(20));
            card.Controls.Add(row.State);
            UpdateRowState(row);

            rows.Add(row);
            y += 74;
        }

        private SettingCard MakeRowCard(Control parent, int y, string title, string desc, Control host)
        {
            var c = new SettingCard();
            c.SetBounds(Theme.S(2), Theme.S(y), Theme.S(DlgW - 68), Theme.S(66));
            c.Title = title;
            c.Desc = desc;
            c.Host(host);
            parent.Controls.Add(c);
            return c;
        }

        private void UpdateRowState(Row row)
        {
            row.State.Text = row.Excluded ? Lang.T("def.state.on") : Lang.T("def.state.off");
            row.State.ForeColor = row.Excluded ? Theme.Danger : Theme.Faint;
        }

        private void OnToggle(Row row)
        {
            bool want = row.Switch.Checked;
            if (want == row.Excluded) return;

            if (!want && !DefenderExclusion.IsOwned(row.Root))
            {
                MessageBox.Show(this, Lang.T("def.notours"), App.DisplayName,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                row.Switch.SetSilently(row.Excluded);
                UpdateRowState(row);
                return;
            }

            if (want)
            {
                string ask = Lang.F("def.confirm", row.Name, row.Root);
                if (MessageBox.Show(this, ask, App.DisplayName,
                        MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
                {
                    row.Switch.SetSilently(false);
                    return;
                }
            }

            Cursor = Cursors.WaitCursor;
            bool ok;
            try { ok = want ? DefenderExclusion.Add(row.Root) : DefenderExclusion.Remove(row.Root); }
            finally { Cursor = Cursors.Default; }

            if (ok) row.Excluded = want;
            else MessageBox.Show(this, Lang.T("def.failed"), App.DisplayName,
                    MessageBoxButtons.OK, MessageBoxIcon.Error);

            row.Switch.SetSilently(row.Excluded);
            UpdateRowState(row);
        }

        private void ClearAll()
        {
            if (DefenderExclusion.OwnedByAegis().Count == 0)
            {
                MessageBox.Show(this, Lang.T("def.clearall.none"), App.DisplayName,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            Cursor = Cursors.WaitCursor;
            int n;
            try { n = DefenderExclusion.RemoveAllOwned(); }
            finally { Cursor = Cursors.Default; }

            List<string> fresh = DefenderExclusion.QuerySystem();
            if (fresh != null)
            {
                systemList = fresh;
                foreach (Row row in rows)
                {
                    row.Excluded = DefenderExclusion.IsExcludedInSystem(systemList, row.Root);
                    row.Switch.SetSilently(row.Excluded);
                    UpdateRowState(row);
                }
            }
            MessageBox.Show(this, Lang.F("def.clearall.done", n), App.DisplayName,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            if (fresh == null)
                MessageBox.Show(this, Lang.T("def.unavailable"), App.DisplayName,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.RoundCorners(Handle);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { Close(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void DragMove(object s, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                Native.ReleaseCapture();
                Native.SendMessage(Handle, Native.WM_NCLBUTTONDOWN, (IntPtr)Native.HT_CAPTION, IntPtr.Zero);
            }
        }
    }
}
