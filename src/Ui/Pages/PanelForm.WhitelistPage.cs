// @author bdth 2074055628@qq.com
// 文件用途 构建白名单页 支持拖放添加 运行中选取与逐条作用域调整

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace PaviseApp
{
    internal partial class PanelForm
    {
        private ListBox lstWhite;
        private EmptyStatePanel whitePanel;
        private Label lblWhiteHint;
        private ContextMenuStrip whiteMenu;
        private readonly Dictionary<string, Bitmap> whiteIconCache =
            new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> whiteNameCache =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private int whiteBusy;

        private void BuildWhitelistPage()
        {
            int y = PageHeader(pageWhitelist, Lang.T("nav.white"), Lang.T("white.page.sub"), 2);
            int listH = PageH - y - 16;
            int listW = ContentW - 238;

            whitePanel = new EmptyStatePanel();
            whitePanel.SetBounds(Theme.S(ContentX), Theme.S(y), Theme.S(listW), Theme.S(listH));
            whitePanel.BackColor = Theme.Bg; whitePanel.Fill = Theme.Card;
            whitePanel.Border = Theme.Stroke; whitePanel.Radius = Theme.S(14);
            whitePanel.EmptyTitle = "PAVISE SHIELD";
            whitePanel.EmptyDetail = Lang.T("white.page.empty");
            whitePanel.EmptyGlyph = "white";
            whitePanel.Padding = new Padding(Theme.S(8));

            lstWhite = new ListBox();
            lstWhite.Dock = DockStyle.Fill;
            Theme.StyleList(lstWhite);
            lstWhite.ItemHeight = Math.Min(255, Theme.S(64));
            lstWhite.DrawItem += DrawWhitelistItem;
            lstWhite.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Delete) RemoveSelectedWhitelist();
            };
            lstWhite.MouseDown += delegate(object s, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Right) return;
                int index = lstWhite.IndexFromPoint(e.Location);
                if (index >= 0) lstWhite.SelectedIndex = index;
            };
            whiteMenu = new ContextMenuStrip();
            whiteMenu.Opening += OnWhiteMenuOpening;
            lstWhite.ContextMenuStrip = whiteMenu;
            whitePanel.Controls.Add(lstWhite);

            pageWhitelist.AllowDrop = true;
            whitePanel.AllowDrop = true;
            lstWhite.AllowDrop = true;
            foreach (Control target in new Control[] { pageWhitelist, whitePanel, lstWhite })
            {
                target.DragEnter += OnWhiteDragEnter;
                target.DragDrop += OnWhiteDragDrop;
            }

            int bx = ContentX + listW + 16, bw = ContentW - listW - 16, bh = 40;
            var pick = new PillButton(Lang.T("white.page.pick"), BtnKind.Primary);
            pick.SetBounds(Theme.S(bx), Theme.S(y), Theme.S(bw), Theme.S(bh));
            pick.Click += delegate { PickRunningForWhitelist(); };

            var browse = new PillButton(Lang.T("btn.browse"));
            browse.SetBounds(Theme.S(bx), Theme.S(y + 50), Theme.S(bw), Theme.S(bh));
            browse.Click += delegate { BrowseForWhitelist(); };

            var remove = new PillButton(Lang.T("btn.remove"));
            remove.SetBounds(Theme.S(bx), Theme.S(y + 100), Theme.S(bw), Theme.S(bh));
            remove.Click += delegate { RemoveSelectedWhitelist(); };

            lblWhiteHint = new Label();
            lblWhiteHint.Text = Lang.T("white.page.drop");
            lblWhiteHint.ForeColor = Theme.Dim;
            lblWhiteHint.BackColor = Theme.Bg;
            lblWhiteHint.Font = Theme.UI(8.2f, false);
            lblWhiteHint.SetBounds(Theme.S(bx + 4), Theme.S(y + 158), Theme.S(bw - 8), Theme.S(120));

            var reset = new PillButton(Lang.T("btn.reset"), BtnKind.Danger);
            reset.SetBounds(Theme.S(bx), Theme.S(y + listH - bh), Theme.S(bw), Theme.S(bh));
            reset.Click += delegate
            {
                if (MessageBox.Show(this, Lang.T("white.reset.confirm"), "Pavise",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
                if (!gameMode.ResetWhitelist()) ShowWhitelistError();
                RefreshWhitelist(true);
            };

            pageWhitelist.Controls.AddRange(new Control[] { whitePanel, pick, browse, remove, lblWhiteHint, reset });
            RefreshWhitelist(false);
        }

        private void OnWhiteDragEnter(object sender, DragEventArgs e)
        {
            e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop)
                ? DragDropEffects.Copy : DragDropEffects.None;
        }

        private void OnWhiteDragDrop(object sender, DragEventArgs e)
        {
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files == null || files.Length == 0) return;
            AddWhitelistFiles(files);
        }

        private void AddWhitelistFiles(IEnumerable<string> files)
        {
            int added = 0;
            string firstError = null;
            foreach (string raw in files)
            {
                string path = ResolveWhitelistTarget(raw);
                if (string.IsNullOrEmpty(path)) continue;
                if (gameMode.AddWhitelistAuto(path)) added++;
                else if (firstError == null) firstError = gameMode.WhitelistLastError;
            }
            if (added == 0 && firstError != null)
                MessageBox.Show(this, firstError, "Pavise", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            if (added > 0) RefreshWhitelist(true);
        }

        internal static string ResolveWhitelistTarget(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            string path = raw.Trim().Trim('"');
            try
            {
                if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    string resolved = GameExecutableResolver.ResolveShortcut(path);
                    if (!string.IsNullOrEmpty(resolved)) path = resolved;
                }
            }
            catch { }
            return path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? path : null;
        }

        private void BrowseForWhitelist()
        {
            using (var dialog = new OpenFileDialog())
            {
                dialog.Filter = Lang.T("white.browse.filter");
                dialog.Multiselect = true;
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                AddWhitelistFiles(dialog.FileNames);
            }
        }

        private void PickRunningForWhitelist()
        {
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (WhitelistRuleView view in gameMode.GetWhitelistRulesFast())
                if (view.Rule.Kind != WhitelistRuleKind.LegacyName) known.Add(view.Rule.Value);

            using (var dialog = new RunningPickerDialog(known))
            {
                if (ShowDim(dialog) != DialogResult.OK || dialog.Selected.Count == 0) return;
                AddWhitelistFiles(dialog.Selected);
            }
        }

        private void RemoveSelectedWhitelist()
        {
            var item = lstWhite.SelectedItem as WhitelistItem;
            if (item == null || item.View == null) return;
            if (item.View.Required)
            {
                MessageBox.Show(this, Lang.T("white.required.locked"), "Pavise",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (!gameMode.RemoveWhitelistRule(item.View.Rule.Key)) ShowWhitelistError();
            RefreshWhitelist(true);
        }

        private void ShowWhitelistError()
        {
            string error = gameMode.WhitelistLastError;
            if (string.IsNullOrEmpty(error)) return;
            MessageBox.Show(this, error, "Pavise", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private void OnWhiteMenuOpening(object sender, System.ComponentModel.CancelEventArgs e)
        {
            whiteMenu.Items.Clear();
            var item = lstWhite.SelectedItem as WhitelistItem;
            if (item == null || item.View == null || item.View.Required) { e.Cancel = true; return; }

            WhitelistRuleKind kind = item.View.Rule.Kind;
            string key = item.View.Rule.Key;
            if (kind == WhitelistRuleKind.ApplicationFamily)
                whiteMenu.Items.Add(Lang.T("white.menu.narrow"), null, delegate
                {
                    if (!gameMode.NarrowWhitelistRule(key)) ShowWhitelistError();
                    RefreshWhitelist(true);
                });
            else if (kind == WhitelistRuleKind.ExactPath
                && !WhitelistRule.IsUnsafeFamilyAnchor(item.View.Rule.Value))
                whiteMenu.Items.Add(Lang.T("white.menu.widen"), null, delegate
                {
                    if (!gameMode.WidenWhitelistRule(key)) ShowWhitelistError();
                    RefreshWhitelist(true);
                });

            whiteMenu.Items.Add(Lang.T("btn.remove"), null, delegate { RemoveSelectedWhitelist(); });
            if (whiteMenu.Items.Count == 0) e.Cancel = true;
        }

#if PAVISE_SELFTEST
        internal void SelectPageForTest(int pageId) { nav.Select(pageId); }

        internal Control WhitelistPageForShot()
        {
            pageWhitelist.Left = 0;
            pageWhitelist.Visible = true;
            pageWhitelist.CreateControl();
            RefreshWhitelist(false);
            return pageWhitelist;
        }
#endif

        internal void RefreshWhitelist(bool deep)
        {
            if (lstWhite == null) return;
            List<WhitelistRuleView> views = gameMode.GetWhitelistRulesFast();
            FillWhitelist(views);
            if (!deep || Interlocked.Exchange(ref whiteBusy, 1) == 1) return;
            ThreadPool.QueueUserWorkItem(delegate
            {
                List<WhitelistRuleView> full = null;
                try { full = gameMode.GetWhitelistRules(); }
                catch { }
                Interlocked.Exchange(ref whiteBusy, 0);
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        if (!IsDisposed && full != null) FillWhitelist(full);
                    });
                }
                catch { }
            });
        }

        private void FillWhitelist(List<WhitelistRuleView> views)
        {
            var selected = lstWhite.SelectedItem as WhitelistItem;
            string selectedKey = selected != null && selected.View != null ? selected.View.Rule.Key : null;

            lstWhite.BeginUpdate();
            lstWhite.Items.Clear();
            var user = new List<WhitelistRuleView>();
            var required = new List<WhitelistRuleView>();
            foreach (WhitelistRuleView view in views)
                (view.Required ? required : user).Add(view);
            foreach (WhitelistRuleView view in user) lstWhite.Items.Add(Decorate(new WhitelistItem(view)));
            if (required.Count > 0)
                lstWhite.Items.Add(new WhitelistItem(null) { Title = Lang.F("white.group.builtin", required.Count) });
            foreach (WhitelistRuleView view in required) lstWhite.Items.Add(Decorate(new WhitelistItem(view)));
            lstWhite.EndUpdate();

            whitePanel.ShowEmpty = lstWhite.Items.Count == 0;
            whitePanel.Invalidate();

            if (selectedKey == null) return;
            for (int i = 0; i < lstWhite.Items.Count; i++)
            {
                var candidate = lstWhite.Items[i] as WhitelistItem;
                if (candidate != null && candidate.View != null
                    && candidate.View.Rule.Key == selectedKey) { lstWhite.SelectedIndex = i; return; }
            }
        }

        private void DrawWhitelistItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= lstWhite.Items.Count) return;
            var item = lstWhite.Items[e.Index] as WhitelistItem;
            if (item == null) return;
            if (item.View == null) { DrawWhitelistGroupHeader(e, item.Title); return; }

            Graphics g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            Rectangle r = e.Bounds;
            bool selected = (e.State & DrawItemState.Selected) != 0;
            bool family = item.View.Rule.Kind == WhitelistRuleKind.ApplicationFamily;
            bool live = item.View.CurrentMatches > 0;

            using (var b = new SolidBrush(selected ? Theme.Sel : Theme.Card))
                g.FillRectangle(b, r);
            if (selected)
                using (var p = new Pen(Theme.Accent, Math.Max(1.5f, Theme.S(2))))
                    g.DrawLine(p, r.Left + Theme.S(2), r.Top + Theme.S(6), r.Left + Theme.S(2), r.Bottom - Theme.S(6));

            int iconSize = Theme.S(32);
            var iconRect = new Rectangle(r.Left + Theme.S(14), r.Top + (r.Height - iconSize) / 2, iconSize, iconSize);
            Bitmap icon = WhitelistIcon(item.View.Rule);
            if (icon != null)
            {
                if (item.View.Required || !live)
                {
                    using (var attr = new System.Drawing.Imaging.ImageAttributes())
                    {
                        var matrix = new System.Drawing.Imaging.ColorMatrix();
                        matrix.Matrix33 = 0.45f;
                        attr.SetColorMatrix(matrix);
                        g.DrawImage(icon, iconRect, 0, 0, icon.Width, icon.Height, GraphicsUnit.Pixel, attr);
                    }
                }
                else g.DrawImage(icon, iconRect);
            }

            int textLeft = iconRect.Right + Theme.S(14);
            int badgeW = Theme.S(72);
            int stateW = Theme.S(96);
            int textW = r.Right - textLeft - badgeW - stateW - Theme.S(28);

            TextRenderer.DrawText(g, item.Title, Theme.UI(9.6f, true),
                new Rectangle(textLeft, r.Top + Theme.S(9), textW, Theme.S(22)),
                item.View.Required ? Theme.Dim : Theme.Fg,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, item.Subtitle, Theme.UI(7.9f, false),
                new Rectangle(textLeft, r.Top + Theme.S(31), textW, Theme.S(20)),
                Theme.Faint,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.PathEllipsis);

            var badge = new Rectangle(r.Right - stateW - badgeW - Theme.S(18),
                r.Top + (r.Height - Theme.S(22)) / 2, badgeW, Theme.S(22));
            Color badgeColor = item.View.Required ? Theme.Faint : (family ? Theme.Accent : Theme.Dim);
            using (var path = Theme.Rounded(badge, Theme.S(6)))
            {
                using (var b = new SolidBrush(Col.Alpha(badgeColor, 34))) g.FillPath(b, path);
                using (var p = new Pen(Col.Alpha(badgeColor, 130))) g.DrawPath(p, path);
            }
            TextRenderer.DrawText(g, item.Badge, Theme.UI(7.6f, false), badge, badgeColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

            var state = new Rectangle(r.Right - stateW - Theme.S(10), r.Top, stateW, r.Height);
            int dot = Theme.S(7);
            var dotRect = new Rectangle(state.Left, state.Top + (state.Height - dot) / 2, dot, dot);
            using (var b = new SolidBrush(live ? Theme.Accent : Col.Alpha(Theme.Faint, 150)))
                g.FillEllipse(b, dotRect);
            TextRenderer.DrawText(g, item.StateText, Theme.UI(7.9f, false),
                new Rectangle(dotRect.Right + Theme.S(6), state.Top, state.Width - dot - Theme.S(6), state.Height),
                live ? Theme.Dim : Theme.Faint,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

            using (var p = new Pen(Col.Alpha(Theme.Stroke, 120)))
                g.DrawLine(p, r.Left + Theme.S(14), r.Bottom - 1, r.Right - Theme.S(10), r.Bottom - 1);
        }

        private static void DrawWhitelistGroupHeader(DrawItemEventArgs e, string text)
        {
            Graphics g = e.Graphics;
            Rectangle r = e.Bounds;
            using (var b = new SolidBrush(Theme.Card)) g.FillRectangle(b, r);
            Size size = TextRenderer.MeasureText(g, text, Theme.UI(7.8f, false));
            int textLeft = r.Left + Theme.S(14);
            int mid = r.Top + r.Height / 2;
            TextRenderer.DrawText(g, text, Theme.UI(7.8f, false),
                new Rectangle(textLeft, r.Top, size.Width + Theme.S(8), r.Height), Theme.Faint,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            using (var p = new Pen(Col.Alpha(Theme.Stroke, 150)))
                g.DrawLine(p, textLeft + size.Width + Theme.S(12), mid, r.Right - Theme.S(14), mid);
        }

        private Bitmap WhitelistIcon(WhitelistRule rule)
        {
            if (rule.Kind == WhitelistRuleKind.LegacyName) return null;
            string key = rule.Value;
            Bitmap bitmap;
            if (whiteIconCache.TryGetValue(key, out bitmap)) return bitmap;
            try { using (Icon icon = Icon.ExtractAssociatedIcon(key)) bitmap = icon.ToBitmap(); }
            catch { bitmap = null; }
            whiteIconCache[key] = bitmap;
            return bitmap;
        }

        private string WhitelistTitle(WhitelistRule rule)
        {
            if (rule.Kind == WhitelistRuleKind.LegacyName) return rule.Value;
            string key = rule.Value;
            string cached;
            if (whiteNameCache.TryGetValue(key, out cached)) return cached;
            string title = null;
            try
            {
                FileVersionInfo info = FileVersionInfo.GetVersionInfo(key);
                title = string.IsNullOrEmpty(info.FileDescription) ? info.ProductName : info.FileDescription;
                if (!string.IsNullOrEmpty(title)) title = title.Trim();
            }
            catch { }
            if (string.IsNullOrEmpty(title))
            {
                try { title = Path.GetFileNameWithoutExtension(key); }
                catch { title = key; }
            }
            whiteNameCache[key] = title;
            return title;
        }

        private sealed class WhitelistItem
        {
            public readonly WhitelistRuleView View;
            public string Title;
            public string Subtitle;
            public string Badge;
            public string StateText;

            public WhitelistItem(WhitelistRuleView view) { View = view; }

            public override string ToString()
            {
                return View == null ? (Title ?? "") : View.Rule.Value;
            }
        }

        private WhitelistItem Decorate(WhitelistItem item)
        {
            WhitelistRule rule = item.View.Rule;
            item.Title = WhitelistTitle(rule);
            item.Subtitle = rule.Kind == WhitelistRuleKind.LegacyName
                ? Lang.T("white.badge.name.sub") : rule.Value;
            item.Badge = rule.Kind == WhitelistRuleKind.ApplicationFamily
                ? Lang.T("white.badge.family")
                : rule.Kind == WhitelistRuleKind.ExactPath
                    ? Lang.T("white.badge.only") : Lang.T("white.badge.name");
            item.StateText = item.View.Required
                ? Lang.T("white.required.badge")
                : item.View.CurrentMatches < 0
                    ? Lang.T("white.matches.pending")
                    : item.View.CurrentMatches > 0
                        ? Lang.F("white.state.running", item.View.CurrentMatches)
                        : Lang.T("white.state.idle");
            return item;
        }
    }
}
