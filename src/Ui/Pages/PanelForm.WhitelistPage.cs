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
        private ModuleBanner whiteBanner;
        private ContextMenuStrip whiteMenu;
        private readonly Dictionary<string, Bitmap> whiteIconCache =
            new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> whiteNameCache =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private int whiteBusy;

        private void BuildWhitelistPage()
        {
            int y = PageHeader(pageWhitelist, Lang.T("nav.white"), Lang.T("white.page.sub"), 2);
            whiteBanner = new ModuleBanner();
            whiteBanner.SetBounds(Theme.S(ContentX), Theme.S(y), Theme.S(ContentW), Theme.S(72));
            whiteBanner.Code = "EXCLUSION LAYER // 03";
            whiteBanner.TitleText = Lang.T("nav.white");
            whiteBanner.Detail = Lang.T("white.page.drop");
            whiteBanner.State = "RULE MAP READY";
            whiteBanner.StateColor = Theme.Green;
            whiteBanner.Glyph = "white";
            pageWhitelist.Controls.Add(whiteBanner);
            y += 84;
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

            lstWhite = new TechListBox();
            lstWhite.Dock = DockStyle.Fill;
            Theme.StyleList(lstWhite, false);
            lstWhite.ItemHeight = Math.Min(255, Theme.S(64));
            lstWhite.DrawItem += DrawWhitelistItem;
            lstWhite.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Delete) RemoveSelectedWhitelist();
                else if (e.KeyCode == Keys.Enter && ShowSelectedAutomaticExemption())
                {
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                }
            };
            lstWhite.MouseClick += delegate(object s, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Left) return;
                int index = lstWhite.IndexFromPoint(e.Location);
                if (index < 0) return;
                lstWhite.SelectedIndex = index;
                ShowSelectedAutomaticExemption();
            };
            lstWhite.MouseDown += delegate(object s, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Right) return;
                int index = lstWhite.IndexFromPoint(e.Location);
                if (index >= 0) lstWhite.SelectedIndex = index;
            };
            whiteMenu = new ContextMenuStrip();
            whiteMenu.Font = Theme.UI(9.5f, false);
            whiteMenu.BackColor = Theme.Card;
            whiteMenu.ForeColor = Theme.Fg;
            whiteMenu.ShowImageMargin = false;
            whiteMenu.DropShadowEnabled = false;
            whiteMenu.Renderer = new TechMenuRenderer();
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
            lblWhiteHint.SetBounds(Theme.S(bx + 4), Theme.S(y + 158), Theme.S(bw - 8), Theme.S(220));
            AppendDetectedPlatformHint();

            var reset = new PillButton(Lang.T("btn.reset"), BtnKind.Danger);
            reset.SetBounds(Theme.S(bx), Theme.S(y + listH - bh), Theme.S(bw), Theme.S(bh));
            reset.Click += delegate
            {
                if (!PaviseDialog.Confirm(this, App.DisplayName, Lang.T("white.reset.confirm"), DlgKind.Warn)) return;
                RunWhitelistOp(delegate
                {
                    return gameMode.ResetWhitelist() ? null : gameMode.WhitelistLastError;
                });
            };

            pageWhitelist.Controls.AddRange(new Control[] { whitePanel, pick, browse, remove, lblWhiteHint, reset });
            RefreshWhitelist(false);
        }

        private void AppendDetectedPlatformHint()
        {
            try
            {
                List<string> detected = GamePlatformCatalog.DetectedPlatforms();
                if (detected.Count == 0) return;
                for (int i = 0; i < detected.Count; i++) detected[i] = PlatformDisplayName(detected[i]);
                lblWhiteHint.Text += "\r\n\r\n"
                    + Lang.F("white.page.platforms", string.Join(" ", detected.ToArray()));
            }
            catch { }
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

        private int whiteOpBusy;

        private void RunWhitelistOp(Func<string> op)
        {
            if (Interlocked.CompareExchange(ref whiteOpBusy, 1, 0) != 0) return;
            ThreadPool.QueueUserWorkItem(delegate
            {
                string error;
                try { error = op(); }
                catch (Exception ex) { error = ex.Message; }
                Interlocked.Exchange(ref whiteOpBusy, 0);
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        if (IsDisposed) return;
                        if (!string.IsNullOrEmpty(error))
                            PaviseDialog.Warn(this, App.DisplayName, error);
                        RefreshWhitelist(true);
                    });
                }
                catch { }
            });
        }

        private void AddWhitelistFiles(IEnumerable<string> files)
        {
            var list = new List<string>(files);
            RunWhitelistOp(delegate
            {
                int added = 0;
                string firstError = null;
                foreach (string raw in list)
                {
                    string path = ResolveWhitelistTarget(raw);
                    if (string.IsNullOrEmpty(path)) continue;
                    if (gameMode.AddWhitelistAuto(path)) added++;
                    else if (firstError == null) firstError = gameMode.WhitelistLastError;
                }
                return added == 0 ? firstError : null;
            });
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
                PaviseDialog.Info(this, App.DisplayName, Lang.T("white.required.locked"));
                return;
            }
            string key = item.View.Rule.Key;
            RunWhitelistOp(delegate
            {
                return gameMode.RemoveWhitelistRule(key) ? null : gameMode.WhitelistLastError;
            });
        }

        private bool ShowSelectedAutomaticExemption()
        {
            var item = lstWhite.SelectedItem as WhitelistItem;
            if (item == null || !item.Automatic || string.IsNullOrEmpty(item.Details)) return false;

            const int dlgW = 720;
            var details = new TextBox();
            details.Multiline = true;
            details.ReadOnly = true;
            details.WordWrap = true;
            details.ScrollBars = ScrollBars.Vertical;
            details.BackColor = Theme.Card;
            details.ForeColor = Theme.Fg;
            details.BorderStyle = BorderStyle.FixedSingle;
            details.Font = Theme.UI(9.2f, false);
            details.Text = item.Details;
            details.Size = new Size(Theme.S(dlgW - 52), Theme.S(390));
            details.SelectionStart = 0;
            details.SelectionLength = 0;
            Native.Dark(details);

            PaviseDialog.Show(this, item.Title, Lang.T("white.auto.details.body"), details, dlgW);
            return true;
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
                    RunWhitelistOp(delegate
                    {
                        return gameMode.NarrowWhitelistRule(key) ? null : gameMode.WhitelistLastError;
                    });
                });
            else if (kind == WhitelistRuleKind.ExactPath
                && !WhitelistRule.IsUnsafeFamilyAnchor(item.View.Rule.Value))
                whiteMenu.Items.Add(Lang.T("white.menu.widen"), null, delegate
                {
                    RunWhitelistOp(delegate
                    {
                        return gameMode.WidenWhitelistRule(key) ? null : gameMode.WhitelistLastError;
                    });
                });

            whiteMenu.Items.Add(Lang.T("btn.remove"), null, delegate { RemoveSelectedWhitelist(); });
            if (whiteMenu.Items.Count == 0) e.Cancel = true;
        }

#if PAVISE_SELFTEST
        internal void SelectPageForTest(int pageId) { nav.Select(pageId); }
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
            if (whiteBanner != null)
            {
                whiteBanner.State = views.Count.ToString() + " RULES ACTIVE";
                whiteBanner.StateColor = views.Count > 0 ? Theme.Green : Theme.Faint;
            }
            var selected = lstWhite.SelectedItem as WhitelistItem;
            string selectedKey = selected != null && selected.View != null ? selected.View.Rule.Key : null;

            lstWhite.BeginUpdate();
            lstWhite.Items.Clear();
            var user = new List<WhitelistRuleView>();
            var required = new List<WhitelistRuleView>();
            foreach (WhitelistRuleView view in views)
                (view.Required ? required : user).Add(view);
            foreach (WhitelistRuleView view in user) lstWhite.Items.Add(Decorate(new WhitelistItem(view)));

            List<WhitelistItem> automatic = AutomaticExemptionItems();
            if (automatic.Count > 0)
            {
                lstWhite.Items.Add(new WhitelistItem(null)
                {
                    IsGroup = true,
                    Title = Lang.F("white.group.auto", automatic.Count)
                });
                foreach (WhitelistItem item in automatic) lstWhite.Items.Add(item);
            }
            if (required.Count > 0)
                lstWhite.Items.Add(new WhitelistItem(null)
                {
                    IsGroup = true,
                    Title = Lang.F("white.group.builtin", required.Count)
                });
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
            if (item.IsGroup) { DrawWhitelistGroupHeader(e, item.Title); return; }
            if (item.View == null && !item.Automatic) return;

            Graphics g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            Rectangle r = e.Bounds;
            bool selected = (e.State & DrawItemState.Selected) != 0;
            bool hover = !selected && e.Index == Theme.HoverIndex(lstWhite);
            bool automatic = item.Automatic;
            bool required = !automatic && item.View.Required;
            bool family = !automatic && item.View.Rule.Kind == WhitelistRuleKind.ApplicationFamily;
            bool live = automatic || item.View.CurrentMatches > 0;

            using (var b = new SolidBrush(selected ? Theme.Sel : (hover ? Theme.CardHover : Theme.Card)))
                g.FillRectangle(b, r);
            if (selected)
                using (var p = new Pen(Theme.Accent, Math.Max(1.5f, Theme.S(2))))
                    g.DrawLine(p, r.Left + Theme.S(2), r.Top + Theme.S(6), r.Left + Theme.S(2), r.Bottom - Theme.S(6));

            int iconSize = Theme.S(32);
            var iconRect = new Rectangle(r.Left + Theme.S(14), r.Top + (r.Height - iconSize) / 2, iconSize, iconSize);
            if (automatic) DrawWhitelistAutomaticIcon(g, iconRect, item.Glyph);
            else
            {
                Bitmap icon = WhitelistIcon(item.View.Rule);
                if (icon != null)
                {
                    if (required || !live)
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
                else DrawWhitelistDefaultIcon(g, iconRect, required, live);
            }

            int textLeft = iconRect.Right + Theme.S(14);
            int badgeW = Theme.S(72);
            int stateW = Theme.S(96);
            int textW = r.Right - textLeft - badgeW - stateW - Theme.S(28);

            TextRenderer.DrawText(g, item.Title, Theme.UI(9.6f, true),
                new Rectangle(textLeft, r.Top + Theme.S(9), textW, Theme.S(22)),
                required ? Theme.Dim : Theme.Fg,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, item.Subtitle, Theme.UI(7.9f, false),
                new Rectangle(textLeft, r.Top + Theme.S(31), textW, Theme.S(20)),
                Theme.Faint,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                    | (automatic ? TextFormatFlags.EndEllipsis : TextFormatFlags.PathEllipsis));

            var badge = new Rectangle(r.Right - stateW - badgeW - Theme.S(18),
                r.Top + (r.Height - Theme.S(22)) / 2, badgeW, Theme.S(22));
            Color badgeColor = required ? Theme.Faint : (automatic || family ? Theme.Accent : Theme.Dim);
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

        private static void DrawWhitelistAutomaticIcon(Graphics g, Rectangle box, string glyph)
        {
            Rectangle frame = box;
            frame.Width--; frame.Height--;
            using (var path = Theme.TechPath(frame, Theme.S(5)))
            {
                using (var fill = new SolidBrush(Col.Alpha(Theme.Accent, 18))) g.FillPath(fill, path);
                using (var border = new Pen(Col.Alpha(Theme.Accent, 108))) g.DrawPath(border, path);
            }
            int size = Theme.S(19);
            Glyphs.Draw(g, string.IsNullOrEmpty(glyph) ? "shield" : glyph,
                new Rectangle(box.Left + (box.Width - size) / 2, box.Top + (box.Height - size) / 2,
                    size, size), Col.Alpha(Theme.Accent, 210));
        }

        private static void DrawWhitelistDefaultIcon(Graphics g, Rectangle box, bool required, bool live)
        {
            Color tone = required ? Theme.Faint : live ? Theme.Accent : Theme.Dim;
            Rectangle frame = box;
            frame.Width--; frame.Height--;
            using (var path = Theme.TechPath(frame, Theme.S(5)))
            {
                using (var fill = new SolidBrush(Col.Alpha(tone, required ? 12 : 20)))
                    g.FillPath(fill, path);
                using (var border = new Pen(Col.Alpha(tone, required ? 76 : 118)))
                    g.DrawPath(border, path);
            }

            int glyphSize = Theme.S(19);
            Rectangle shield = new Rectangle(
                box.Left + (box.Width - glyphSize) / 2,
                box.Top + (box.Height - glyphSize) / 2,
                glyphSize, glyphSize);
            Glyphs.Draw(g, "shield", shield, Col.Alpha(tone, required ? 145 : 205));

            int node = Math.Max(2, Theme.S(2));
            int cy = box.Top + box.Height / 2;
            int cx = box.Left + box.Width / 2;
            using (var link = new Pen(Col.Alpha(tone, required ? 112 : 172), Math.Max(1f, Theme.S(1))))
                g.DrawLine(link, cx - Theme.S(4), cy, cx + Theme.S(4), cy);
            using (var dot = new SolidBrush(Col.Alpha(tone, required ? 160 : 225)))
            {
                g.FillEllipse(dot, cx - Theme.S(5) - node / 2, cy - node / 2, node, node);
                g.FillEllipse(dot, cx - node / 2, cy - node / 2, node, node);
                g.FillEllipse(dot, cx + Theme.S(5) - node / 2, cy - node / 2, node, node);
            }
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
            public string Glyph;
            public string Details;
            public bool IsGroup;
            public bool Automatic;

            public WhitelistItem(WhitelistRuleView view) { View = view; }

            public override string ToString()
            {
                return View == null ? (Title ?? "") : View.Rule.Value;
            }
        }

        private static string PlatformDisplayName(string id)
        {
            return !string.IsNullOrEmpty(id) && id.StartsWith("t.", StringComparison.Ordinal)
                ? Lang.T(id) : id;
        }

        private static List<WhitelistItem> AutomaticExemptionItems()
        {
            var rows = new List<WhitelistItem>();
            List<string> supported = GamePlatformCatalog.SupportedPlatformsForDisplay();
            List<string> detected = GamePlatformCatalog.DetectedPlatforms();
            for (int i = 0; i < supported.Count; i++) supported[i] = PlatformDisplayName(supported[i]);
            for (int i = 0; i < detected.Count; i++) detected[i] = PlatformDisplayName(detected[i]);
            string platformDetail = detected.Count > 0
                ? Lang.F("white.auto.platform.detected", string.Join(" · ", detected.ToArray()))
                : Lang.T("white.auto.platform.sub");
            string detectedText = detected.Count > 0
                ? string.Join(" · ", detected.ToArray()) : Lang.T("white.auto.details.none");
            string platformList = string.Join(" · ", supported.ToArray());
            rows.Add(AutomaticExemption("white.auto.platform", platformDetail, "gamepad",
                DetailSection("white.auto.details.platforms", platformList)
                + "\r\n\r\n" + DetailSection("white.auto.details.detected", detectedText)
                + "\r\n\r\n" + Lang.T("white.auto.details.platform.note")));

            string[] acceleratorNames = NetAcceleratorCatalog.ProcessNamesForDisplay();
            string[] acceleratorTokens = NetAcceleratorCatalog.TokensForDisplay();
            rows.Add(AutomaticExemption("white.auto.accel",
                Lang.F("white.auto.accel.list", string.Join(" · ", acceleratorNames)), "chart",
                DetailSection("white.auto.details.processes", string.Join(" · ", acceleratorNames))
                + "\r\n\r\n" + DetailSection("white.auto.details.tokens",
                    string.Join(" · ", acceleratorTokens))));

            string[] hardwareNames = HardwareControlCatalog.ProcessNamesForDisplay();
            string[] hardwareTokens = HardwareControlCatalog.TokensForDisplay();
            rows.Add(AutomaticExemption("white.auto.hardware",
                Lang.F("white.auto.hardware.list", string.Join(" · ", hardwareNames)), "chip",
                DetailSection("white.auto.details.processes", string.Join(" · ", hardwareNames))
                + "\r\n\r\n" + DetailSection("white.auto.details.tokens",
                    string.Join(" · ", hardwareTokens))
                + "\r\n\r\n" + Lang.T("white.auto.details.hardware.note")));

            var antiCheatGroups = new List<string>();
            var antiCheatNames = new List<string>();
            foreach (AcGroup group in AntiCheatCatalog.Groups)
            {
                antiCheatNames.Add(group.Name);
                antiCheatGroups.Add(group.Name + "\r\n" + string.Join(" · ", group.Procs));
            }
            rows.Add(AutomaticExemption("white.auto.anticheat",
                Lang.F("white.auto.anticheat.list", string.Join(" · ", antiCheatNames.ToArray())),
                "acshield", Lang.T("white.auto.details.groups") + "\r\n\r\n"
                + string.Join("\r\n\r\n", antiCheatGroups.ToArray())
                + "\r\n\r\n" + DetailSection("white.auto.details.tokens",
                    string.Join(" · ", AntiCheatCatalog.NameTokensForDisplay()))));

            string[] nameKeywords = PeripheralCatalog.NameKeywordsForDisplay();
            string[] descriptionWords = PeripheralCatalog.DescriptionWordsForDisplay();
            string[] vendorTokens = PeripheralCatalog.PresentVendorTokensForDisplay();
            string vendorText = vendorTokens.Length > 0
                ? string.Join(" · ", vendorTokens) : Lang.T("white.auto.details.none");
            rows.Add(AutomaticExemption("white.auto.peripheral",
                Lang.F("white.auto.peripheral.list", string.Join(" · ", nameKeywords)), "settings",
                DetailSection("white.auto.details.namekeywords", string.Join(" · ", nameKeywords))
                + "\r\n\r\n" + DetailSection("white.auto.details.descriptionwords",
                    string.Join(" · ", descriptionWords))
                + "\r\n\r\n" + DetailSection("white.auto.details.vendors", vendorText)));
            return rows;
        }

        private static string DetailSection(string titleKey, string content)
        {
            return Lang.T(titleKey) + "\r\n" + content;
        }

        private static WhitelistItem AutomaticExemption(
            string titleKey, string subtitle, string glyph, string details)
        {
            return new WhitelistItem(null)
            {
                Automatic = true,
                Title = Lang.T(titleKey),
                Subtitle = subtitle,
                Badge = Lang.T("white.auto.badge"),
                StateText = Lang.T("white.auto.state"),
                Glyph = glyph,
                Details = details
            };
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
