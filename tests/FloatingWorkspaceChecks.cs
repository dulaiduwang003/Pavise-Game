#if PAVISE_SELFTEST && PAVISE_FLOATING_WORKSPACE_CHECKS
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PaviseApp
{
    // Actual product controls on an unselected private desktop. This executable
    // never enters Program.Main, starts a game worker or writes persistent settings.
    internal static class FloatingWorkspaceChecks
    {
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        private static int checks, fixtures, pagesChecked, animatedFrames;
        private static string context;
        private static Rectangle work;
        private static int dpi;
        private sealed class Case
        {
            internal string Name; internal Rectangle Area; internal int Dpi;
            internal Case(string name, int width, int height, int value)
            { Name = name; Area = new Rectangle(0, 0, width, height); Dpi = value; }
        }
        private static readonly Case[] Cases = {
            new Case("design96", 1920, 1040, 96), new Case("fhd96", 1920, 1040, 96), new Case("fhd120", 1920, 1040, 120),
            new Case("fhd144", 1920, 1040, 144), new Case("qhd192", 2560, 1380, 192),
            new Case("handheld96", 1280, 680, 96), new Case("narrow96", 800, 620, 96),
            new Case("portrait192", 720, 1200, 192), new Case("short96", 1000, 700, 96)
        };
        private static void Check(bool ok, string message)
        { checks++; if (!ok) throw new InvalidOperationException("FAIL: " + context + ": " + message); }
        private static T Field<T>(object owner, string name)
        {
            for (Type type = owner.GetType(); type != null; type = type.BaseType) {
                FieldInfo field = type.GetField(name, Hidden | BindingFlags.Public | BindingFlags.DeclaredOnly);
                if (field != null) return (T)field.GetValue(owner);
            }
            throw new MissingFieldException(owner.GetType().Name, name);
        }
        private static object Call(object owner, string name, Type[] types, params object[] args)
        {
            for (Type type = owner.GetType(); type != null; type = type.BaseType) {
                MethodInfo method = type.GetMethod(name, Hidden | BindingFlags.Public | BindingFlags.DeclaredOnly, null, types, null);
                if (method != null) try { return method.Invoke(owner, args); }
                    catch (TargetInvocationException error) { throw error.InnerException; }
            }
            throw new MissingMethodException(owner.GetType().Name, name);
        }
        private static void Pump() { Application.DoEvents(); }
        private static Rectangle Relative(Control parent, Control child)
        { return new Rectangle(parent.PointToClient(child.PointToScreen(Point.Empty)), child.Size); }
        private static void Contained(Control parent, Control child, string label)
        {
            Rectangle r = Relative(parent, child);
            Check(r.Width > 0 && r.Height > 0 && parent.ClientRectangle.Contains(r), label + " outside viewport: " + r + " in " + parent.ClientRectangle);
        }
        private static void Save(Control control, string path)
        {
            using (var bitmap = new Bitmap(control.Width, control.Height, PixelFormat.Format32bppPArgb)) {
                control.DrawToBitmap(bitmap, control.ClientRectangle); bitmap.Save(path, ImageFormat.Png);
            }
        }
        private static void SaveEnabledPreview(PanelForm form, string path)
        {
            PaviseCore core = Field<PaviseCore>(form, "paviseCore");
            Toggle toggle = Field<Toggle>(form, "swGame"); StatusDot dot = Field<StatusDot>(form, "statusDot");
            var brand = Field<Control>(form, "root").Controls["WorkspaceTopBar"].Controls["WorkspaceBrand"] as WorkspaceBrand;
            bool wasChecked = toggle.Checked; Color wasColor = dot.Color;
            PerformancePreset mode = Field<PerformancePreset>(core, "mode");
            bool enabled = Field<bool>(core, "guardEnabled"), active = Field<bool>(core, "gameActive");
            PerformancePreset brandMode = brand == null ? mode : Field<PerformancePreset>(brand, "mode");
            bool brandEnabled = brand != null && Field<bool>(brand, "enabled");
            try {
                // Visual fixture only. SetSilently does not invoke the game-mode
                // event handler, and GameMode.Enabled stays false throughout.
                Call(Field<NavRail>(form, "nav"), "OnMouseLeave", new[] { typeof(EventArgs) }, EventArgs.Empty);
                toggle.SetSilently(true); dot.Color = Theme.Accent;
                if (brand != null) brand.SetState(PerformancePreset.Competitive, true);
                core.SetState(PerformancePreset.Competitive, true, false); Save(form, path);
            } finally {
                toggle.SetSilently(wasChecked); dot.Color = wasColor; core.SetState(mode, enabled, active);
                if (brand != null) brand.SetState(brandMode, brandEnabled);
            }
        }
        private static void GuardProviders(PanelForm form)
        {
            foreach (PrivacyOptionsPanel privacy in PerformanceWorkspaceLayout.Descendants(form).OfType<PrivacyOptionsPanel>()) {
                privacy.InspectionVisibleForTest = delegate { return false; };
                privacy.SetForTest = delegate { throw new InvalidOperationException("Layout checks cannot change system settings"); };
                privacy.RefreshForTest = delegate { throw new InvalidOperationException("Layout checks cannot inspect system settings"); };
                privacy.StateForTest = delegate(PrivacyAction action, string target) {
                    return new PrivacyResult { State = "restored", Supported = true, Detail = "Synthetic layout state" };
                };
            }
        }
        private static void Fit(PanelForm form)
        {
            Call(form, "OnFitTick", new[] { typeof(object), typeof(EventArgs) }, null, EventArgs.Empty);
            GuardProviders(form); Pump();
        }
        private static void Model()
        {
            foreach (Case item in Cases) {
                context = "model/" + item.Name;
                Rectangle bounds = WorkspaceLayout.InitialBounds(item.Area, item.Dpi);
                Check(item.Area.Contains(bounds), "startup exceeds monitor work area");
                Check(WorkspaceLayout.PreferredScale(item.Area, item.Dpi) >= item.Dpi / 96f, "text scale below OS DPI");
                var layout = new WorkspaceLayout(bounds.Size, WorkspaceLayout.PreferredScale(item.Area, item.Dpi));
                Check(layout.RailWidth == 0, "old left rail still reserves width");
                Check(layout.HeaderHeight >= 44 && layout.Height > layout.HeaderHeight, "invalid chrome geometry");
            }
        }
        private static void Chrome(PanelForm form)
        {
            Control root = Field<Control>(form, "root"); NavRail nav = Field<NavRail>(form, "nav");
            Check(root.Bounds == form.ClientRectangle, "root differs from actual viewport");
            Check(nav.Visible && nav.Width > nav.Height * 2, "main navigation is not a horizontal Dock");
            Contained(root, nav, "floating Dock");
            Check(nav.Top > root.Height / 2 && nav.Left > 0 && nav.Right < root.Width && nav.Bottom < root.Height, "Dock lost bottom float margins");
            Check(Math.Abs(nav.Left - (root.Width - nav.Width) / 2) <= 1, "Dock is not centered");
            Check(!Field<NavRail>(form, "tuningNav").Visible, "old left tuning rail became visible");
            int[] expected = { 0, 1, 10, 2, 3, 6, 8, 9 };
            Rectangle dockFrame = (Rectangle)typeof(NavRail).GetProperty("DockBounds", Hidden).GetValue(nav, null);
            Check(nav.Height == Theme.S(72) && dockFrame.Height == Theme.S(56), "floating navigation did not adopt the compact 56px frame");
            Rectangle previous = Rectangle.Empty;
            for (int slot = 0; slot < expected.Length; slot++) {
                Check(nav.ItemAtSlot(slot) == expected[slot], "Dock page order changed at slot " + slot);
                Rectangle bounds = (Rectangle)Call(nav, "ItemBounds", new[] { typeof(int) }, slot);
                Check(nav.ClientRectangle.Contains(bounds), "Dock hit target outside control: " + bounds);
                Check(bounds.Width >= Theme.S(32) && bounds.Height >= Theme.S(44) - 1, "Dock target too small: " + bounds);
                Check(slot == 0 || previous.Right <= bounds.Left, "Dock hit targets overlap"); previous = bounds;
                Point middle = new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
                Check((int)Call(nav, "HitTestPoint", new[] { typeof(Point) }, middle) == expected[slot], "paint and click geometry disagree");
                AccessibleObject accessible = nav.AccessibilityObject.GetChild(slot);
                Check(accessible != null && !String.IsNullOrEmpty(accessible.Name), "Dock item lacks accessible name");
                Check(nav.RectangleToClient(accessible.Bounds) == bounds, "accessible Dock target disagrees with paint geometry");
            }
            Check(nav.ItemAtSlot(expected.Length) == -1, "Dock has extra or duplicate destinations");
            Control top = root.Controls["WorkspaceTopBar"]; Check(top != null, "top bar missing"); Contained(root, top, "header");
            Control[] caption = { top.Controls["WindowMinimize"], top.Controls["WindowMaximize"], top.Controls["WindowClose"],
                Field<Control>(form, "themeSwitch"), Field<Control>(form, "modeButton") };
            foreach (Control target in caption) { Check(target != null, "caption action missing"); Contained(top, target, "caption"); }
            for (int a = 0; a < caption.Length; a++) for (int b = a + 1; b < caption.Length; b++)
                Check(!caption[a].Bounds.IntersectsWith(caption[b].Bounds), "caption actions overlap");
            var mode = Field<ModeButton>(form, "modeButton");
            var theme = Field<ThemeSwitch>(form, "themeSwitch");
            Check(!String.IsNullOrWhiteSpace(mode.SummaryText) && !mode.SummaryText.Contains("手动")
                && !mode.SummaryText.Contains("手動") && mode.SummaryText.IndexOf("Manual", StringComparison.OrdinalIgnoreCase) < 0,
                "mode header still includes the redundant manual prefix: " + mode.SummaryText);
            Check(!theme.Compact && theme.Width == Theme.S(94), "original two-cell theme switch was not restored");
            Check(theme.Height == Theme.S(44) && mode.Height == Theme.S(44), "header actions lost their 44px height");
            Check(theme.Right <= mode.Left, "theme switch is not to the left of the mode action");
            Control brand = top.Controls["WorkspaceBrand"];
            Check(brand != null && brand.Visible, "PAVISE brand missing from header");
            Contained(top, brand, "PAVISE brand");
            foreach (Control target in caption)
                Check(!brand.Bounds.IntersectsWith(target.Bounds), "PAVISE brand overlaps header action " + target.Name);
        }
        private static void Show(PanelForm form, PageId page)
        { form.ShowPageForShot(page); Pump(); Call(form, "StopPageReveal", Type.EmptyTypes); }
        private static void ScrollReachable(ScrollableControl viewport, Control target, string label)
        {
            viewport.AutoScrollPosition = Point.Empty; Pump();
            Rectangle before = Relative(viewport, target);
            viewport.ScrollControlIntoView(target); Pump();
            Rectangle after = Relative(viewport, target);
            Check(after.Bottom > 0 && after.Top < viewport.ClientSize.Height, label + " cannot be scrolled into view: " + after);
            if (target.Height <= viewport.ClientSize.Height)
                Check(after.Top >= 0 && after.Bottom <= viewport.ClientSize.Height, label + " remains vertically clipped after scrolling: " + after);
            if (target.Width <= viewport.ClientSize.Width)
                Check(after.Left >= 0 && after.Right <= viewport.ClientSize.Width, label + " remains horizontally clipped after scrolling: " + after);
            if (before.Top >= viewport.ClientSize.Height)
                Check(viewport.AutoScrollPosition.Y < 0 && after.Top < before.Top, label + " did not exercise actual parent-page scrolling");
        }
        private static void ShortPageContent(PanelForm form, PageId page)
        {
            var active = (ScrollableControl)Field<Control>(form, "curPage");
            if (page == PageId.AntiCheat) {
                var list = Field<Control>(form, "acList");
                Check(list.Height >= Theme.S(96), "short-window anti-cheat list collapsed");
                ScrollReachable(active, list, "anti-cheat list");
            } else if (page == PageId.Whitelist) {
                var list = Field<Control>(form, "lstWhite");
                Check(list.Height >= Theme.S(100), "short-window whitelist collapsed");
                ScrollReachable(active, list, "whitelist");
                ScrollReachable(active, Field<Control>(form, "btnRemoveWhitelist"), "whitelist remove action");
            } else if (page == PageId.Log) {
                var stream = Field<Control>(form, "logStream");
                Check(stream.Height >= Theme.S(120), "short-window log stream collapsed");
                ScrollReachable(active, stream, "log stream");
                Control refresh = active.Controls.Find("refreshSessionLog", true).Single();
                ScrollReachable(active, refresh, "log refresh action");
            } else if (page == PageId.Audit) {
                var scan = Field<Control>(form, "auditScan"); var start = Field<Control>(form, "btnAuditStart");
                Check(scan.ClientRectangle.Contains(start.Bounds), "audit start lies outside scan surface");
                ScrollReachable(active, start, "audit start action");
            } else if (page == PageId.CoreScheduling) {
                var content = Field<ScrollableControl>(form, "coreScrollPanel");
                Check(content.Height >= Theme.S(120), "short-window core editor collapsed");
                ScrollReachable(active, content, "core editor viewport");
                var last = content.Controls.Cast<Control>().Where(x => x.Visible).OrderBy(x => x.Bottom).Last();
                ScrollReachable(content, last, "core editor final section");
            }
        }
        private static void NavigationInteraction(PanelForm form)
        {
            NavRail nav = Field<NavRail>(form, "nav");
            foreach (Keys key in new[] { Keys.Home, Keys.Right, Keys.Enter })
                Call(nav, "OnKeyDown", new[] { typeof(KeyEventArgs) }, new KeyEventArgs(key));
            Check(nav.Selected == (int)PageId.Library, "Home/Right/Enter does not open the second Dock destination");
            foreach (Keys key in new[] { Keys.End, Keys.Space })
                Call(nav, "OnKeyDown", new[] { typeof(KeyEventArgs) }, new KeyEventArgs(key));
            Check(nav.Selected == (int)PageId.About, "End/Space does not open the final Dock destination");
            for (int slot = 0; slot < 8; slot++) {
                Rectangle bounds = (Rectangle)Call(nav, "ItemBounds", new[] { typeof(int) }, slot);
                Call(nav, "OnMouseClick", new[] { typeof(MouseEventArgs) },
                    new MouseEventArgs(MouseButtons.Left, 1, bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2, 0));
                Call(form, "StopPageReveal", Type.EmptyTypes);
                Check(nav.Selected == nav.ItemAtSlot(slot), "Dock pointer route selected the wrong page at " + slot);
            }
            nav.AccessibilityObject.GetChild(0).DoDefaultAction(); Call(form, "StopPageReveal", Type.EmptyTypes);
            Check(nav.Selected == (int)PageId.Overview, "accessible Dock activation failed");
        }
        private static void Overview(PanelForm form)
        {
            Show(form, PageId.Overview);
            Control page = Field<Control>(form, "pageOverview");
            PaviseCore core = Field<PaviseCore>(form, "paviseCore");
            Check(core != null && core.Visible, "original animated core missing");
            Contained(page, core, "original animated core");
            Check(!PerformanceWorkspaceLayout.Descendants(page).OfType<DashboardTile>().Any(), "decorative function cards remain");
            foreach (string name in new[] { "overviewAutomaticActions", "overviewLogs", "overviewNextAction" })
                Check(page.Controls.Find(name, true).Length == 0, "removed overview action remains: " + name);
            Control toggle = Field<Control>(form, "swGame");
            Check(toggle != null && toggle.Visible, "guard toggle missing"); Contained(page, toggle, "guard toggle");
            Label status = Field<Label>(form, "lblStatus");
            Check(status != null && status.Visible && !String.IsNullOrWhiteSpace(status.Text), "game state text missing");
            Contained(page, status, "game state");
            Check(Relative(page, core).Bottom <= Relative(page, status).Top, "animation overlaps game status");
            Check(!Relative(page, status).IntersectsWith(Relative(page, toggle)), "game label overlaps guard toggle");
        }
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr key, IntPtr data);
        private static void NativeKey(Control target, Keys key)
        {
            Check(target.Focus(), "control cannot receive native focus: " + target.Name);
            Check(PostMessage(target.Handle, 0x100, new IntPtr((int)key), new IntPtr(1)), "cannot post key: " + target.Name);
            Check(PostMessage(target.Handle, 0x101, new IntPtr((int)key), new IntPtr(unchecked((int)0xc0000001))), "cannot post key-up");
            Pump();
        }
        private static List<WhitelistRuleView> WhitelistFixture()
        {
            var rows = new List<WhitelistRuleView>();
            string[] names = { "keep-chat", "keep-music", "steam", "wegame", "system-audio", "system-input" };
            for (int i = 0; i < names.Length; i++) {
                WhitelistRule rule; Check(WhitelistRule.TryCreate(WhitelistRuleKind.LegacyName, names[i], out rule), "cannot create whitelist fixture");
                rows.Add(new WhitelistRuleView(rule, i % 2, i >= 4, i == 2 ? "steam" : i == 3 ? "wegame" : null));
            }
            return rows;
        }
        private static void Whitelist(PanelForm form)
        {
            Show(form, PageId.Whitelist);
            var page = Field<ScrollableControl>(form, "pageWhitelist");
            Check(Field<NavRail>(form, "nav").Selected == (int)PageId.Whitelist, "whitelist is still selected as a library detail");
            Check(!Field<Control>(form, "coreBackBar").Visible, "whitelist still has a return-to-library bar");
            Check(!PerformanceWorkspaceLayout.Descendants(page).OfType<ModuleBanner>().Any(), "whitelist retained a large introduction banner");
            var rules = WhitelistFixture(); form.WhitelistQueryForTest = delegate { return rules; }; form.RefreshWhitelist(false); Pump();
            var list = Field<ListBox>(form, "lstWhite"); var search = Field<TextBox>(form, "whiteSearch");
            var filter = Field<TierPicker>(form, "whiteFilters"); var remove = Field<PillButton>(form, "btnRemoveWhitelist");
            Check(list.ItemHeight == Theme.S(56), "whitelist rows did not adopt compact density");
            Check(list.Items.Count == 10, "whitelist dropped rules or duplicated group headers");
            foreach (object item in list.Items) Check(!Field<bool>(item, "IsGroup"), "whitelist repeats protection group headers");
            if (Dpi.U(page.ClientSize.Width) >= 920 && Dpi.U(page.ClientSize.Height) >= 560) {
                Check(!page.VerticalScroll.Visible, "normal whitelist has a redundant outer scrollbar");
                Check(list.Height / list.ItemHeight >= 6, "normal whitelist cannot show six compact entries");
            }
            NativeKey(filter, Keys.Home); NativeKey(filter, Keys.Right);
            Check(list.Items.Count == 2, "manual whitelist filter returned other source categories");
            list.SelectedIndex = 0; Check(remove.Enabled && (bool)Call(form, "CanRemoveSelectedWhitelist", Type.EmptyTypes), "manual whitelist rule cannot be removed");
            NativeKey(filter, Keys.Right);
            Check(list.Items.Count == 2, "platform whitelist filter lost an existing platform");
            foreach (object item in list.Items) Check(Field<WhitelistRuleView>(item, "View").PlatformId != null, "platform filter includes another source");
            NativeKey(filter, Keys.End);
            Check(list.Items.Count == 6, "built-in whitelist filter lost automatic protection or required rules");
            int requiredIndex = -1;
            for (int index = 0; index < list.Items.Count; index++) {
                object item = list.Items[index]; WhitelistRuleView view = Field<WhitelistRuleView>(item, "View");
                Check(Field<bool>(item, "Automatic") || view != null && view.Required, "built-in filter includes a removable rule");
                if (view != null && view.Required) { requiredIndex = index; Check(Field<string>(item, "StateText").Length == 0, "built-in protection label is duplicated as row state"); }
            }
            Check(requiredIndex >= 0, "required-rule fixture missing"); list.SelectedIndex = requiredIndex;
            Check(!remove.Enabled && !(bool)Call(form, "CanRemoveSelectedWhitelist", Type.EmptyTypes), "required protection became removable");
            search.Text = "__no_whitelist_matches__"; Pump();
            Check(list.Items.Count == 0 && !remove.Enabled && Field<EmptyStatePanel>(form, "whitePanel").ShowEmpty, "empty whitelist search retained actionable stale selection");
            search.Text = ""; NativeKey(filter, Keys.Home); search.Text = "keep-chat"; Pump();
            Check(list.Items.Count == 1 && list.Items[0].ToString() == "keep-chat", "whitelist search selected wrong application");
            search.Text = ""; Pump();
            foreach (string field in new[] { "whitePick", "whiteBrowse", "whiteReset", "btnRestorePlatformProtection", "btnRemoveWhitelist" })
                ScrollReachable(page, Field<Control>(form, field), "whitelist action " + field);
            page.AutoScrollPosition = Point.Empty; Pump();
        }
        private static void ModernAntiCheat(PanelForm form, string output, bool screenshots)
        {
            var page = Field<ScrollableControl>(form, "pageAntiCheat"); var list = Field<DBPanel>(form, "acList");
            var cards = Field<List<SettingCard>>(form, "acCards"); var toggles = Field<List<Toggle>>(form, "acToggles");
            Check(cards.Count == 3 && toggles.Count == 3, "anti-cheat default view does not contain exactly three actionable groups");
            Check(!page.AutoScroll && list.AutoScroll, "anti-cheat uses nested scrolling");
            Check(!PerformanceWorkspaceLayout.Descendants(page).OfType<ModuleBanner>().Any(), "anti-cheat retained a large introduction banner");
            var other = Field<List<Control>>(form, "acReadOnlyCards"); var expansion = Field<PillButton>(form, "acOtherToggle");
            Check(other.Count > 0 && other.All(x => !x.Visible), "read-only anti-cheat groups do not start collapsed");
            foreach (Control card in cards) { Check(card.Parent == list, "actionable anti-cheat card is outside single scroll surface"); Contained(card, card.Controls["AntiCheatDetails"], "anti-cheat details action"); }
            expansion.PerformClick(); Pump(); Check(other.All(x => x.Visible), "read-only anti-cheat groups did not expand");
            ScrollReachable(list, other.Last(), "final read-only anti-cheat group");
            if (screenshots) Save(form, Path.Combine(output, context + "-AntiCheat-expanded.png"));
            expansion.PerformClick(); list.AutoScrollPosition = Point.Empty; Pump();
            Check(other.All(x => !x.Visible), "read-only anti-cheat groups did not collapse");
        }
        private static void About(PanelForm form)
        {
            var page = Field<ScrollableControl>(form, "pageAbout");
            if (Dpi.U(page.ClientSize.Width) >= 900 && Dpi.U(page.ClientSize.Height) >= 560) {
                Check(!page.AutoScroll && !page.VerticalScroll.Visible, "normal About page still needs scrolling");
                foreach (string field in new[] { "aboutHeroPanel", "aboutIdentityPanel", "aboutReleasePanel" }) Contained(page, Field<Control>(form, field), "About section");
            }
            foreach (PillButton button in PerformanceWorkspaceLayout.Descendants(page).OfType<PillButton>())
                Check(button.Height >= Theme.S(36) && button.Parent.ClientRectangle.Contains(button.Bounds), "About action is clipped or too short");
        }
        private static void Pages(PanelForm form, string output, bool screenshots)
        {
            PageId[] order = { PageId.Overview, PageId.Library, PageId.Policy, PageId.AntiCheat,
                PageId.Audit, PageId.Settings, PageId.About, PageId.Log, PageId.Whitelist,
                PageId.CoreScheduling, PageId.Graphics, PageId.Environment, PageId.Interrupt };
            if (context.StartsWith("short96", StringComparison.Ordinal))
                order = order.Where(x => x != PageId.Policy).Concat(new[] { PageId.Policy }).ToArray();
            foreach (PageId page in order) {
                Show(form, page); Control active = Field<Control>(form, "curPage");
                Control root = Field<Control>(form, "root"); NavRail nav = Field<NavRail>(form, "nav");
                Contained(root, active, "page " + page); Check(active.Left == 0 && active.Width == root.Width, "page retains old rail gutter: " + page);
                Check(active.Bottom <= nav.Top, "page overlaps Dock: " + page); Check(nav.Visible, "Dock disappeared on " + page);
                Check(Field<int>(form, "pageBaseLeft") == 0, "page reveal still targets old sidebar offset");
                Check(!active.Controls.OfType<Label>().Any(x => x.Top < Theme.S(54) && x.Font.Size / Dpi.Scale >= 16f), "page retained a duplicate large header: " + page);
                if (page == PageId.Whitelist) Whitelist(form);
                if (page == PageId.AntiCheat) ModernAntiCheat(form, output, screenshots);
                if (page == PageId.About) About(form);
                if (page == PageId.Policy) {
                    var categories = Field<PerformanceCategoryBar>(form, "performanceCategories");
                    var grids = Field<DBPanel[]>(form, "performanceTabPanels");
                    Check(grids.Length == 4, "performance categories missing");
                    for (int index = 0; index < grids.Length; index++) {
                        categories.Select(index, true); Pump(); var grid = (PerformanceFeatureGrid)grids[index];
                        PerformanceWorkspaceLayout.Context = context + "/performance" + index;
                        ScrollReachable((ScrollableControl)grid.Parent, grid, "performance category viewport");
                        try { PerformanceWorkspaceLayout.Validate(grid); PerformanceWorkspaceLayout.BottomReachable(grid); }
                        catch {
                            Control last = PerformanceWorkspaceLayout.LastFeature(grid);
                            Console.WriteLine("SCROLL DIAGNOSTIC " + PerformanceWorkspaceLayout.Context + ": grid=" + grid.Bounds
                                + "; parent=" + grid.Parent.ClientRectangle + "; viewport=" + grid.ClientSize
                                + "; display=" + grid.DisplayRectangle + "; minimum=" + grid.AutoScrollMinSize
                                + "; position=" + grid.AutoScrollPosition + "; maximum=" + grid.VerticalScroll.Maximum
                                + "; largeChange=" + grid.VerticalScroll.LargeChange + "; final=" + PerformanceWorkspaceLayout.InGrid(grid, last)
                                + "; finalParent=" + last.Parent.GetType().Name + "; finalName=" + last.Name);
                            throw;
                        }
                        grid.AutoScrollPosition = Point.Empty; Pump();
                    }
                    categories.Select(0, true); Pump();
                }
                if (context.StartsWith("short96", StringComparison.Ordinal)) ShortPageContent(form, page);
                if (screenshots) {
                    string path = Path.Combine(output, context + "-" + page + ".png");
                    if (page == PageId.Overview) SaveEnabledPreview(form, path); else Save(form, path);
                }
                pagesChecked++;
            }
        }
        private static long FrameHash(PaviseCore core, Bitmap bitmap, double at)
        {
            using (Graphics g = Graphics.FromImage(bitmap)) Call(core, "RenderFrame", new[] { typeof(Graphics), typeof(double) }, g, at);
            long hash = 17;
            unchecked { for (int y = 0; y < bitmap.Height; y += 4) for (int x = 0; x < bitmap.Width; x += 4) hash = hash * 31 + bitmap.GetPixel(x, y).ToArgb(); }
            animatedFrames++; return hash;
        }
        private static void Animation(PanelForm form, string output)
        {
            Show(form, PageId.Overview); PaviseCore core = Field<PaviseCore>(form, "paviseCore");
            foreach (PerformancePreset preset in new[] { PerformancePreset.Standard, PerformancePreset.Competitive, PerformancePreset.Handheld, PerformancePreset.Extreme })
            using (var bitmap = new Bitmap(core.Width, core.Height, PixelFormat.Format32bppPArgb)) {
                Theme.SetMode(preset, false); core.SetState(preset, true, false);
                long a = FrameHash(core, bitmap, 1.25);
                Check(FrameHash(core, bitmap, 1.25) == a, "cached core frame is unstable: " + preset);
                Check(FrameHash(core, bitmap, 1.25 + 1.0 / 60) != a, "core adjacent frames stalled: " + preset);
                Check(FrameHash(core, bitmap, 2.25) != a, "original animation stopped: " + preset);
                bitmap.Save(Path.Combine(output, "animation-" + preset + ".png"));
                core.SetState(preset, false, false); Check(FrameHash(core, bitmap, 1.25) != a, "disabled core unchanged: " + preset);
                core.SetState(preset, true, true); FrameHash(core, bitmap, 1.25);
                Check(Field<bool>(core, "gameActive"), "core lost active state: " + preset);
            }
            for (int mask = 0; mask < 128; mask++) {
                bool expected = (mask & 63) == 63 && (mask & 64) == 0;
                Check(PaviseCore.ShouldAnimate((mask & 1) != 0, (mask & 2) != 0, (mask & 4) != 0,
                    (mask & 8) != 0, (mask & 16) != 0 ? FormWindowState.Normal : FormWindowState.Minimized,
                    (mask & 64) != 0, (mask & 32) != 0) == expected, "original animation activity gate changed");
            }
        }
        private sealed class FixtureForm : PanelForm
        {
            internal FixtureForm(Tamer tamer, GameMode mode, Icon icon) : base(tamer, mode, icon, true) { }
            protected override bool ShowWithoutActivation { get { return true; } }
        }
        private static void Run(Case item, string output, int language, bool light, bool screenshots, bool animation)
        {
            context = item.Name + "-lang" + language + (light ? "-light" : "-dark"); Console.WriteLine("FLOATING " + context);
            Settings.UseTransientStoreForCurrentProcess(); Settings.SaveStr("PerformancePreset", ((int)PerformancePreset.Competitive).ToString()); Logger.WritesEnabled = false;
            PrivacyControl.ResetForTest(); PrivacyControl.QueueForTest = delegate { throw new InvalidOperationException("Layout checks cannot queue privacy work"); };
            work = item.Area; dpi = item.Dpi; PanelForm.WorkspaceAreaForTest = delegate { return work; }; PanelForm.WorkspaceDpiForTest = delegate { return dpi; };
            Dpi.NoFit = false; Dpi.Scale = 1; Theme.DropFontCache(); Lang.Cur = language; Theme.SetLight(light);
            CpuTopology.InjectTopologyForTest(255, new ulong[] { 3, 12, 48, 192 }, new ulong[] { 15, 240 }, 0, 0, 0, 0, false, false);
            string folder = Path.Combine(output, context); Directory.CreateDirectory(folder); Paths.Data = folder;
            var suppression = new SuppressionCore(); var mode = new GameMode(folder, suppression); var tamer = new Tamer(suppression);
            using (Icon icon = IconArt.MakeIcon(24))
            using (var form = new FixtureForm(tamer, mode, icon)) {
                try {
                    form.ShowInTaskbar = false; GuardProviders(form); form.Show(); Pump(); Fit(form);
                    if (item.Name == "design96" || item.Name == "short96") {
                        Size viewport = item.Name == "short96" ? new Size(640, 480) : new Size(1220, 760);
                        Dpi.Scale = 1; Theme.DropFontCache(); form.ClientSize = viewport; Fit(form);
                        Check(form.ClientSize == viewport, "design viewport changed");
                    }
                    Check(!form.UiActive && !mode.Enabled && Field<object>(tamer, "worker") == null, "fixture started product workers");
                    Check(work.Contains(form.Bounds), "window exceeds work area"); Check(Dpi.Scale >= dpi / 96f, "DPI text scale shrank");
                    Chrome(form); NavigationInteraction(form); Overview(form); SaveEnabledPreview(form, Path.Combine(output, context + "-overview.png"));
                    Pages(form, output, screenshots); if (animation) Animation(form, output); fixtures++;
                } catch { Save(form, Path.Combine(output, context + "-failure.png")); throw; }
            }
        }
        [STAThread] private static int Main(string[] args)
        {
            Console.OutputEncoding = new System.Text.UTF8Encoding(false);
            if (args.Length == 1 && args[0] == "--negative-control") {
                context = "negative";
                try { Check(new Rectangle(0, 0, 100, 100).Contains(new Rectangle(90, 90, 20, 20)), "intentional Dock overflow"); }
                catch (InvalidOperationException error) { Console.WriteLine(error.Message); return 3; }
                return 0;
            }
            if (args.Length < 1 || Path.GetFullPath(args[0]).TrimEnd('\\') != AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\')) return 2;
            return PerformanceWorkspaceLayout.PrivateDesktop(delegate {
                Settings.UseTransientStoreForCurrentProcess(); PanelForm.SuppressUiWorkersForTest = true; Logger.WritesEnabled = false; Dpi.Init(); Lang.Init(); Model();
                bool single = args.Length > 1 && args[1] == "--single";
                bool shortOnly = args.Length > 1 && args[1] == "--short";
                foreach (Case item in Cases.Where(x => !single && !shortOnly || single && x.Name == "design96" || shortOnly && x.Name == "short96"))
                    Run(item, args[0], 0, false, item.Name == "design96" || item.Name == "short96" || item.Name == "fhd120", item.Name == "design96");
                if (!single && !shortOnly) Run(Cases[0], args[0], 1, true, true, false);
                Console.WriteLine("PASS floating workspace assertions=" + (checks + PerformanceWorkspaceLayout.Checks) + " fixtures=" + fixtures + " pages=" + pagesChecked + " animationFrames=" + animatedFrames
                    + "; private desktop, transient settings, original core renderer, runtime workers suppressed");
            });
        }
    }
}
#endif
