#if PAVISE_SELFTEST && PAVISE_LIBRARY_GALLERY_CHECKS
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        public static bool TryHandleRuntimeMode(string[] args)
        { throw new InvalidOperationException("Gallery checks cannot start product runtime"); }
    }

    // Real product controls on a private, unselected desktop. Every fixture has
    // its own data folder and transient settings; no product process is restarted.
    internal static class LibraryGalleryChecks
    {
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static int assertions, fixtures, screenshots;
        private static string context = "startup";
        private static readonly List<IDisposable> privateDesktopResources = new List<IDisposable>();
        private sealed class FixtureForm : PanelForm
        {
            internal FixtureForm(Tamer tamer, GameMode mode, Icon icon) : base(tamer, mode, icon, true) { }
            protected override bool ShowWithoutActivation { get { return true; } }
        }

        private static void Check(bool ok, string detail)
        {
            assertions++;
            if (!ok) throw new InvalidOperationException("FAIL " + context + ": " + detail);
        }

        private static T Field<T>(object owner, string name)
        {
            for (Type type = owner.GetType(); type != null; type = type.BaseType) {
                FieldInfo field = type.GetField(name, Hidden | BindingFlags.DeclaredOnly);
                if (field != null) return (T)field.GetValue(owner);
            }
            throw new MissingFieldException(owner.GetType().Name, name);
        }

        private static object Call(object owner, string name, params object[] args)
        {
            for (Type type = owner.GetType(); type != null; type = type.BaseType) {
                MethodInfo method = type.GetMethods(Hidden | BindingFlags.DeclaredOnly).FirstOrDefault(x =>
                    x.Name == name && x.GetParameters().Length == args.Length);
                if (method != null) try { return method.Invoke(owner, args); }
                    catch (TargetInvocationException error) { throw error.InnerException; }
            }
            throw new MissingMethodException(owner.GetType().Name, name);
        }

        private static void Pump() { Application.DoEvents(); }
        private static void Trace(string directory, string stage)
        { File.AppendAllText(Path.Combine(directory, "fixture-stages.log"), context + " " + stage + Environment.NewLine); }
        private static IEnumerable<Control> Descendants(Control root)
        {
            foreach (Control child in root.Controls) {
                yield return child;
                foreach (Control nested in Descendants(child)) yield return nested;
            }
        }
        private static Rectangle Relative(Control parent, Control child)
        { return new Rectangle(parent.PointToClient(child.PointToScreen(Point.Empty)), child.Size); }
        private static void Save(Control control, string path)
        {
            using (var bitmap = new Bitmap(control.Width, control.Height, PixelFormat.Format32bppPArgb)) {
                control.DrawToBitmap(bitmap, control.ClientRectangle); bitmap.Save(path, ImageFormat.Png); screenshots++;
            }
        }
        private static Bitmap MakeIcon(Color main, Color detail)
        {
            var bitmap = new Bitmap(48, 48, PixelFormat.Format32bppPArgb);
            using (Graphics graphics = Graphics.FromImage(bitmap)) {
                graphics.Clear(main);
                using (var brush = new SolidBrush(detail)) graphics.FillRectangle(brush, 12, 12, 24, 24);
            }
            return bitmap;
        }
        private static List<GameLibraryItem> SyntheticGames(string directory)
        {
            string[] names = { "SCUM", "League of Legends", "Shift At Midnight", "NEBULA STRIKE", "AURORA FRONTIER",
                "ORBIT RUNNER", "深空边界", "LONG GAME TITLE: A VERY LONG EDITION", "NIGHT CIRCUIT", "最后一款游戏" };
            var items = new List<GameLibraryItem>();
            for (int index = 0; index < names.Length; index++) {
                string gameRoot = Path.Combine(directory, index == 1 ? "WeGame\\英雄联盟\\Game" : "Steam\\steamapps\\common\\Game" + index);
                var profile = new GameProfile { Id = "gallery-fixture-" + index, Name = names[index], Root = gameRoot,
                    ExecutablePath = Path.Combine(gameRoot, "game" + index + ".exe") };
                profile.Entries.Add("game" + index);
                if (index == 0) profile.Overrides[PolicyCatalog.KeyGameFullscreenOptimizations] = "1";
                items.Add(new GameLibraryItem(profile, index == 1, index % 3 == 0));
            }
            return items;
        }
        private static GameLibraryTile Tile(GameLibraryGallery gallery, string id)
        { return gallery.Controls.OfType<GameLibraryTile>().Single(x => x.Item.Profile.Id == id); }
        private static void Click(Control control)
        { Call(control, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, Math.Max(1, control.Width / 2), Math.Max(1, control.Height / 2), 0)); Pump(); }
        private static void Key(Control control, Keys key)
        {
            Check(control.Focus(), "card cannot receive real keyboard focus");
            Check(PostMessage(control.Handle, 0x100, new IntPtr((int)key), new IntPtr(1)), "cannot post gallery navigation key");
            Check(PostMessage(control.Handle, 0x101, new IntPtr((int)key), new IntPtr(unchecked((int)0xC0000001))), "cannot post gallery key-up");
            Pump();
        }
        private static int CountPixels(Bitmap bitmap, Rectangle region, Func<Color, bool> predicate)
        {
            int count = 0; region.Intersect(new Rectangle(Point.Empty, bitmap.Size));
            for (int y = region.Top; y < region.Bottom; y++) for (int x = region.Left; x < region.Right; x++)
                if (predicate(bitmap.GetPixel(x, y))) count++;
            return count;
        }
        private static bool Cyan(Color color) { return color.B > 210 && color.G > 180 && color.R < 60; }
        private static bool Magenta(Color color) { return color.R > 210 && color.B > 180 && color.G < 60; }
        private static bool Green(Color color) { return color.G > 170 && color.R < 70 && color.B < 70; }

        private sealed class PrivateForm : Form
        {
            protected override bool ShowWithoutActivation { get { return true; } }
            protected override CreateParams CreateParams
            { get { var parameters = base.CreateParams; parameters.ExStyle |= 0x08000000; return parameters; } }
        }
        private sealed class SyntheticExtension : GameCardExtension
        {
            internal SyntheticExtension()
            {
                BackColor = Theme.Card;
                var text = new Label { Text = "WeGame · Synthetic client state", AutoSize = false,
                    ForeColor = Theme.Dim, BackColor = Theme.Card, Bounds = new Rectangle(Theme.S(6), Theme.S(6), Theme.S(190), Theme.S(28)) };
                var action = new PillButton("Synthetic action") { Bounds = new Rectangle(Theme.S(6), Theme.S(208), Theme.S(190), Theme.S(32)) };
                Controls.AddRange(new Control[] { text, action });
            }
            public override int PreferredHeight { get { return Theme.S(260); } }
        }
        private static void Reach(ScrollableControl viewport, Control child)
        {
            viewport.ScrollControlIntoView(child); Pump(); Rectangle bounds = Relative(viewport, child);
            Check(bounds.IntersectsWith(viewport.ClientRectangle), child.Name + " cannot be scrolled into view");
            Check(bounds.Left >= 0 && bounds.Right <= viewport.ClientSize.Width, child.Name + " horizontally clipped after scrolling");
            if (child.Height <= viewport.ClientSize.Height)
                Check(viewport.ClientRectangle.Contains(bounds), child.Name + " remains clipped after scrolling");
        }
        private static void Gallery(string directory, string output, int dpi, int width, int language, bool light)
        {
            context = "gallery-" + dpi + "-" + width + "-lang" + language + (light ? "-light" : "-dark");
            Console.WriteLine("GALLERY " + context);
            Settings.UseTransientStoreForCurrentProcess(); Paths.Data = directory;
            Dpi.NoFit = true; Dpi.Scale = dpi / 96f; Theme.DropFontCache(); Lang.Cur = language; Theme.SetLight(light);
            Theme.SetMode(PerformancePreset.Competitive, false);
            var items = SyntheticGames(directory);
            using (var cyan = MakeIcon(Color.Cyan, Color.Navy))
            using (var magenta = MakeIcon(Color.Magenta, Color.Maroon))
            using (var cover = new Bitmap(640, 360, PixelFormat.Format32bppPArgb))
            using (var form = new PrivateForm())
            using (var gallery = new GameLibraryGallery()) {
                using (Graphics graphics = Graphics.FromImage(cover)) graphics.Clear(Color.FromArgb(12, 220, 18));
                form.ShowInTaskbar = false; form.StartPosition = FormStartPosition.Manual;
                form.ClientSize = new Size(Theme.S(width), Theme.S(490)); form.BackColor = Theme.Bg;
                gallery.Dock = DockStyle.Fill;
                gallery.IconProvider = delegate(string executable) { return executable.EndsWith("game1.exe", StringComparison.OrdinalIgnoreCase) ? magenta : cyan; };
                GameCoverStore.DropCache(); gallery.CoverProvider = GameCoverStore.Load;
                form.Controls.Add(gallery); gallery.SetItems(items); form.Show(); Pump(); gallery.PerformLayout(); Pump();
                Check(gallery.Items.Count == 10 && gallery.VisibleItemCount == 10, "ten games missing from gallery");
                Check(gallery.ColumnCount >= 1, "gallery has no columns");
                Check(!gallery.HorizontalScroll.Visible, "gallery requires horizontal scrolling");
                GameLibraryTile[] visible = gallery.Controls.OfType<GameLibraryTile>().Where(x => x.Visible).ToArray();
                Check(visible.Length == 10, "visible cards differ from game count");
                foreach (GameLibraryTile tile in visible) {
                    Check(tile.Width > Theme.S(90) && tile.Height > Theme.S(100), "card collapsed");
                    Check(tile.ClientRectangle.Contains(tile.CoverBounds), "cover outside card");
                    Check(tile.ClientRectangle.Contains(tile.OriginalIconBounds), "original executable icon outside card");
                    Check(tile.CoverBounds.Bottom <= tile.OriginalIconBounds.Top, "original executable icon must remain in footer");
                    Check(tile.ClientRectangle.Contains(tile.MoreButton.Bounds) && tile.MoreButton.Bottom <= tile.OriginalIconBounds.Top,
                        "card menu overlaps original footer icon");
                    Check(!String.IsNullOrWhiteSpace(tile.AccessibleName), "card lacks accessible name");
                    Check(tile.Width <= gallery.ClientSize.Width, "card wider than viewport");
                }
                for (int first = 0; first < visible.Length; first++) for (int second = first + 1; second < visible.Length; second++)
                    Check(!visible[first].Bounds.IntersectsWith(visible[second].Bounds), "cards overlap");
                Save(form, Path.Combine(output, context + "-ten-default.png"));

                GameLibraryTile firstTile = Tile(gallery, items[0].Profile.Id);
                using (var frame = new Bitmap(firstTile.Width, firstTile.Height)) {
                    firstTile.DrawToBitmap(frame, firstTile.ClientRectangle);
                    Check(CountPixels(frame, firstTile.OriginalIconBounds, Cyan) > Theme.S(6) * Theme.S(6), "default footer replaced original icon");
                    Check(CountPixels(frame, firstTile.CoverBounds, Cyan) > Theme.S(12) * Theme.S(12), "default cover missing executable icon");
                }
                Save(firstTile, Path.Combine(output, context + "-default-card.png"));
                string originalExecutable = items[0].Profile.ExecutablePath;
                string source = Path.Combine(directory, "user-custom-cover.png"); cover.Save(source, ImageFormat.Png);
                string coverError;
                Check(GameCoverStore.TrySet(items[0].Profile, source, out coverError), "custom cover import failed: " + coverError);
                Check(items[0].Profile.ExecutablePath == originalExecutable, "custom cover import changed executable identity");
                gallery.RefreshRows(); Pump();
                using (var frame = new Bitmap(firstTile.Width, firstTile.Height)) {
                    firstTile.DrawToBitmap(frame, firstTile.ClientRectangle);
                    Check(CountPixels(frame, firstTile.CoverBounds, Green) > firstTile.CoverBounds.Width * firstTile.CoverBounds.Height / 3, "custom cover is not displayed");
                    Check(CountPixels(frame, firstTile.OriginalIconBounds, Cyan) > Theme.S(6) * Theme.S(6), "custom cover replaced original footer icon");
                    Check(CountPixels(frame, firstTile.OriginalIconBounds, Green) == 0, "custom cover leaked into original footer icon");
                }
                Save(firstTile, Path.Combine(output, context + "-custom-card.png"));
                GameCoverStore.DropCache(); gallery.RefreshRows(); Pump();
                using (var frame = new Bitmap(firstTile.Width, firstTile.Height)) {
                    firstTile.DrawToBitmap(frame, firstTile.ClientRectangle);
                    Check(CountPixels(frame, firstTile.CoverBounds, Green) > firstTile.CoverBounds.Width * firstTile.CoverBounds.Height / 3,
                        "custom cover did not survive cache reload");
                    Check(CountPixels(frame, firstTile.OriginalIconBounds, Cyan) > Theme.S(6) * Theme.S(6),
                        "cache reload changed original footer icon");
                }
                Check(GameCoverStore.TryReset(items[0].Profile, out coverError), "restore default failed: " + coverError);
                gallery.RefreshRows(); Pump();
                using (var frame = new Bitmap(firstTile.Width, firstTile.Height)) {
                    firstTile.DrawToBitmap(frame, firstTile.ClientRectangle);
                    Check(CountPixels(frame, firstTile.CoverBounds, Cyan) > Theme.S(12) * Theme.S(12), "reset did not restore default executable cover");
                    Check(CountPixels(frame, firstTile.OriginalIconBounds, Cyan) > Theme.S(6) * Theme.S(6), "reset changed original footer icon");
                }
                GameLibraryTile secondTile = Tile(gallery, items[1].Profile.Id);
                using (var frame = new Bitmap(secondTile.Width, secondTile.Height)) {
                    secondTile.DrawToBitmap(frame, secondTile.ClientRectangle);
                    Check(CountPixels(frame, secondTile.OriginalIconBounds, Magenta) > Theme.S(6) * Theme.S(6), "footer borrowed another game's icon");
                }
                gallery.IconProvider = null; gallery.RefreshRows(); Pump();
                using (var frame = new Bitmap(firstTile.Width, firstTile.Height)) {
                    firstTile.DrawToBitmap(frame, firstTile.ClientRectangle);
                    Check(CountPixels(frame, firstTile.OriginalIconBounds, Cyan) == 0, "missing-icon fallback reused a stale game icon");
                    Rectangle center = Rectangle.Inflate(firstTile.CoverBounds, -firstTile.CoverBounds.Width / 4, -firstTile.CoverBounds.Height / 4);
                    Check(CountPixels(frame, center, delegate(Color color) { return color.R > 110 && color.G > 110 && color.B > 110; }) > Theme.S(8),
                        "missing executable icon has no visible fallback");
                }
                Save(firstTile, Path.Combine(output, context + "-missing-icon-card.png"));
                gallery.IconProvider = delegate(string executable) { return executable.EndsWith("game1.exe", StringComparison.OrdinalIgnoreCase) ? magenta : cyan; };
                gallery.RefreshRows(); Pump();

                gallery.SelectedIndex = 0; Click(secondTile);
                Check(gallery.SelectedItem != null && gallery.SelectedItem.Profile.Id == items[1].Profile.Id, "click selected the wrong source item");
                gallery.FilterText = "最后"; Pump();
                Check(gallery.Items.Count == 10 && gallery.VisibleItemCount == 1, "search destroyed source items or failed to filter");
                GameLibraryTile lastTile = Tile(gallery, items[9].Profile.Id); Click(lastTile);
                Check(gallery.SelectedItem.Profile.Id == items[9].Profile.Id && gallery.SelectedIndex == 9, "search selected visible index instead of source identity");
                gallery.FilterText = "no-such-game-fixture"; Pump();
                Check(gallery.VisibleItemCount == 0, "empty search did not hide cards");
                gallery.FilterText = ""; gallery.Filter = delegate(GameLibraryItem item) { return item.Running; }; Pump();
                Check(gallery.VisibleItemCount == 1 && secondTile.Visible, "running filter chose wrong game");
                gallery.Filter = null; gallery.FilterText = ""; Pump();

                GameLibraryEventArgs requested = null;
                gallery.MoreRequested += delegate(object sender, GameLibraryEventArgs e) { requested = e; };
                gallery.SelectedIndex = 0;
                Call(secondTile.MoreButton, "OnClick", EventArgs.Empty); Pump();
                Check(requested != null && requested.Item.Profile.Id == items[1].Profile.Id && requested.Anchor == secondTile.MoreButton,
                    "More action did not capture clicked card and anchor");
                Check(gallery.SelectedItem.Profile.Id == requested.Item.Profile.Id, "More action did not select its card first");
                AccessibleObject accessibleMore = secondTile.AccessibilityObject.GetChild(0);
                Check(accessibleMore != null && accessibleMore.Role == AccessibleRole.PushButton && !String.IsNullOrEmpty(accessibleMore.Name),
                    "card menu lacks an accessible action");
                Check(accessibleMore.Bounds == secondTile.MoreButton.RectangleToScreen(secondTile.MoreButton.ClientRectangle),
                    "accessible menu bounds differ from painted menu button");
                gallery.SelectedIndex = 0; requested = null; accessibleMore.DoDefaultAction(); Pump();
                Check(requested != null && gallery.SelectedItem.Profile.Id == items[1].Profile.Id && requested.Item.Profile.Id == items[1].Profile.Id,
                    "accessible More action routed a different game");
                requested = null; Key(secondTile, Keys.Apps);
                Check(requested != null && requested.Item.Profile.Id == items[1].Profile.Id, "keyboard context-menu key did not open the selected game's menu");
                requested = null; Key(secondTile.MoreButton, Keys.Enter);
                Check(requested != null && requested.Item.Profile.Id == items[1].Profile.Id, "Enter on More did not open the correct game's menu");
                Key(secondTile.MoreButton, Keys.End);
                Check(gallery.SelectedItem.Profile.Id == items[9].Profile.Id, "End did not reach final game");
                Check(gallery.ClientRectangle.Contains(Relative(gallery, lastTile)), "final game cannot be fully scrolled into view");
                if (lastTile.Bottom > gallery.ClientSize.Height || gallery.AutoScrollMinSize.Height > gallery.ClientSize.Height)
                    Check(gallery.AutoScrollPosition.Y < 0, "keyboard did not scroll long gallery");
                Key(lastTile, Keys.Home);
                Check(gallery.SelectedItem.Profile.Id == items[0].Profile.Id, "Home did not reach first game");
                Key(firstTile, Keys.Right);
                Check(gallery.SelectedItem.Profile.Id == items[1].Profile.Id, "Right did not reach adjacent game");
                gallery.FilterText = "最后"; Pump(); Key(lastTile, Keys.Home);
                Check(gallery.SelectedItem.Profile.Id == items[9].Profile.Id, "keyboard escaped filtered items");
                gallery.FilterText = "";
                int originalWidth = form.ClientSize.Width;
                form.ClientSize = new Size(Theme.S(Math.Max(240, width / 2)), form.ClientSize.Height); Pump(); gallery.PerformLayout(); Pump();
                Key(lastTile, Keys.End);
                Check(!gallery.HorizontalScroll.Visible && gallery.ClientRectangle.Contains(Relative(gallery, lastTile)), "resized gallery clipped its final card");
                Rectangle stableBounds = lastTile.Bounds; Point stableScroll = gallery.AutoScrollPosition;
                for (int iteration = 0; iteration < 4; iteration++) { gallery.PerformLayout(); Pump(); }
                Check(lastTile.Bounds == stableBounds && gallery.AutoScrollPosition == stableScroll, "idle reflow moved the scrolled gallery");
                form.ClientSize = new Size(originalWidth, form.ClientSize.Height); Pump(); gallery.PerformLayout(); Pump();
                Check(!gallery.HorizontalScroll.Visible, "restoring gallery width left a horizontal scrollbar");
                if (width >= 1040 && gallery.ClientSize.Width >= Theme.S(1040)) {
                    Size originalIconSize = firstTile.OriginalIconBounds.Size;
                    gallery.PreferredCardHeight = 146; gallery.CardGap = 6;
                    form.ClientSize = new Size(originalWidth, Theme.S(460)); Pump(); gallery.PerformLayout(); Pump();
                    gallery.AutoScrollPosition = Point.Empty; Pump();
                    Check(!gallery.VerticalScroll.Visible && gallery.ClientRectangle.Contains(Relative(gallery, lastTile)),
                        "compact three-row viewport cannot display all ten games");
                    Check(firstTile.OriginalIconBounds.Size == originalIconSize, "compact mode shrank original executable icon");
                    Save(form, Path.Combine(output, context + "-ten-compact.png"));
                    gallery.PreferredCardHeight = 170; gallery.CardGap = 12;
                    form.ClientSize = new Size(originalWidth, Theme.S(490)); Pump(); gallery.PerformLayout(); Pump();
                }
                InlineConfiguration(gallery, items, form, output);
                gallery.SelectedIndex = 4;
                var refreshed = items.Select(x => new GameLibraryItem(x.Profile.Clone(), x.Running, x.RendererObserved)).ToList();
                gallery.SetItems(refreshed); Pump();
                Check(gallery.SelectedItem.Profile.Id == items[4].Profile.Id, "data refresh lost selection identity");
                refreshed.Reverse(); gallery.SetItems(refreshed); Pump();
                Check(gallery.SelectedItem.Profile.Id == items[4].Profile.Id && gallery.SelectedIndex == 5, "source reorder changed selected game identity");
                gallery.SetItems(new List<GameLibraryItem>()); Pump();
                Check(gallery.Items.Count == 0 && gallery.VisibleItemCount == 0 && gallery.SelectedItem == null, "empty library retained stale selection or cards");
                Save(form, Path.Combine(output, context + "-empty.png"));
                fixtures++; GameCoverStore.DropCache();
            }
        }

        private static void InlineConfiguration(GameLibraryGallery gallery, List<GameLibraryItem> items, Form form, string output)
        {
            int created = 0, disposed = 0, refreshed = 0;
            gallery.ConfigurationFactory = delegate(GameLibraryItem item) {
                created++;
                var panel = new Panel { Name = "SyntheticInlineSettings", Tag = item.Profile.Id, Height = Theme.S(140), AutoScroll = true };
                var label = new Label { Name = "BoundGame", Text = item.Profile.Name, ForeColor = Theme.Fg,
                    Bounds = new Rectangle(Theme.S(8), Theme.S(8), Theme.S(190), Theme.S(24)) };
                var setting = new TextBox { Name = "FixtureSetting", Text = "keep", Bounds = new Rectangle(Theme.S(8), Theme.S(48), Theme.S(150), Theme.S(24)) };
                panel.Controls.AddRange(new Control[] { label, setting });
                panel.Disposed += delegate { disposed++; };
                return panel;
            };
            gallery.ConfigurationRefresher = delegate(Control content, GameLibraryItem item) {
                refreshed++; Check((string)content.Tag == item.Profile.Id, "inline refresher bound a different source identity");
                content.Controls["BoundGame"].Text = item.Profile.Name;
            };
            gallery.FilterText = ""; gallery.Filter = null; gallery.SelectedIndex = 0; Pump();
            Check(created == 0 && gallery.ExpandedItem == null, "configuration factory ran before explicit expansion");
            GameLibraryTile first = Tile(gallery, items[0].Profile.Id);
            Check(first.ConfigurationButton.Visible, "card has no configuration action");
            Call(first.ConfigurationButton, "OnClick", EventArgs.Empty); Pump();
            Control content0 = first.ConfigurationContent;
            Check(created == 1 && content0 != null && content0.Parent == first && first.ConfigurationExpanded
                && gallery.ExpandedItem.Profile.Id == items[0].Profile.Id, "explicit expansion did not attach settings to the clicked card");
            Check(!((ScrollableControl)content0).AutoScroll && first.ClientRectangle.Contains(content0.Bounds), "configuration uses nested scrolling or escapes its card");
            Check(first.OriginalIconBounds.Bottom < content0.Top, "inline settings replaced the original game footer");
            GameLibraryTile later = gallery.Controls.OfType<GameLibraryTile>().Where(x => x.Visible && x.Top > first.Top).OrderBy(x => x.Top).First();
            int previousTop = later.Top;
            content0.Height += Theme.S(90); Pump();
            Check(later.Top >= previousTop + Theme.S(90), "expanding settings did not reflow subsequent grid rows");
            GameLibraryTile[] visible = gallery.Controls.OfType<GameLibraryTile>().Where(x => x.Visible).ToArray();
            for (int a = 0; a < visible.Length; a++) for (int b = a + 1; b < visible.Length; b++)
                Check(!visible[a].Bounds.IntersectsWith(visible[b].Bounds), "expanded cards overlap");
            var edit = (TextBox)content0.Controls["FixtureSetting"];
            edit.SelectionStart = 0; Key(edit, Keys.Right);
            Check(gallery.SelectedIndex == 0 && edit.SelectionStart == 1, "editing arrows navigated to another game");
            Key(edit, Keys.Delete);
            Check(gallery.SelectedIndex == 0 && edit.Text == "kep", "editing Delete escaped the inline setting");
            gallery.RefreshRows(); Pump();
            Check(first.ConfigurationContent == content0 && !content0.IsDisposed && edit.Focused && refreshed > 0,
                "live refresh recreated settings or lost keyboard focus");
            gallery.SetItems(items.Select(x => new GameLibraryItem(x.Profile.Clone(), x.Running, x.RendererObserved)).ToList()); Pump();
            Check(first.ConfigurationContent == content0 && created == 1, "same-identity refresh recreated inline settings");
            Save(form, Path.Combine(output, context + "-inline-settings.png"));
            GameLibraryTile second = Tile(gallery, items[1].Profile.Id);
            Key(second, Keys.F2); Pump();
            Check(content0.IsDisposed && disposed == 1 && created == 2 && gallery.ExpandedItem.Profile.Id == items[1].Profile.Id,
                "switching expanded cards did not dispose previous settings");
            Control content1 = second.ConfigurationContent;
            GameLibraryTile nextRow = Tile(gallery, items[Math.Min(items.Count - 1, Math.Max(2, gallery.ColumnCount))].Profile.Id);
            Reach(gallery, nextRow.ConfigurationButton);
            Rectangle buttonBefore = Relative(gallery, nextRow.ConfigurationButton);
            nextRow.ConfigurationButton.Focus(); Pump();
            int xy = (nextRow.ConfigurationButton.Height / 2 << 16) | (nextRow.ConfigurationButton.Width / 2 & 0xffff);
            PostMessage(nextRow.ConfigurationButton.Handle, 0x201, (IntPtr)1, (IntPtr)xy); Pump();
            Check(!content1.IsDisposed && second.ConfigurationContent == content1
                && Relative(gallery, nextRow.ConfigurationButton) == buttonBefore,
                "cross-row mouse-down moved the target before mouse-up");
            PostMessage(nextRow.ConfigurationButton.Handle, 0x202, IntPtr.Zero, (IntPtr)xy); Pump();
            Check(content1.IsDisposed && nextRow.ConfigurationExpanded && created == 3 && disposed == 2,
                "cross-row mouse-up failed to open the clicked card and dispose previous settings");
            content1 = nextRow.ConfigurationContent;
            gallery.FilterText = "最后"; Pump();
            Check(content1.IsDisposed && gallery.ExpandedItem == null && disposed == 3, "filtering out an expanded card retained its settings");
            gallery.FilterText = ""; Pump();
            GameLibraryTile last = Tile(gallery, items[9].Profile.Id);
            Call(last.ConfigurationButton, "OnClick", EventArgs.Empty); Pump();
            Control removed = last.ConfigurationContent;
            var fewer = new List<GameLibraryItem>(items); fewer.RemoveAt(9); gallery.SetItems(fewer); Pump();
            Check(removed.IsDisposed && gallery.ExpandedItem == null && disposed == 4, "removing expanded game retained its settings");
            gallery.SetItems(new List<GameLibraryItem>(items)); gallery.ConfigurationFactory = null; gallery.ConfigurationRefresher = null; Pump();
            Check(gallery.ExpandedItem == null && created == disposed, "configuration lifetime leaked after collapse and removal");
            gallery.AutoScrollPosition = Point.Empty; Pump();
        }

        private static void ProtectProviders(PanelForm form)
        {
            foreach (PrivacyOptionsPanel privacy in Descendants(form).OfType<PrivacyOptionsPanel>()) {
                privacy.InspectionVisibleForTest = delegate { return false; };
                privacy.SetForTest = delegate { throw new InvalidOperationException("Gallery checks cannot change system settings"); };
                privacy.RefreshForTest = delegate { throw new InvalidOperationException("Gallery checks cannot inspect system settings"); };
                privacy.StateForTest = delegate(PrivacyAction action, string target) {
                    return new PrivacyResult { State = "restored", Supported = true, Detail = "Synthetic gallery state" };
                };
            }
        }
        private static void Workspace(string directory, string output, int width, int height, int dpi)
        {
            context = "workspace-" + width + "x" + height + "-" + dpi;
            Console.WriteLine("GALLERY " + context);
            Trace(output, "prepare");
            Settings.UseTransientStoreForCurrentProcess(); Logger.WritesEnabled = false; Paths.Data = directory;
            Dpi.NoFit = false; Dpi.Scale = dpi / 96f; Theme.DropFontCache(); Lang.Cur = 0; Theme.SetLight(false);
            Settings.SaveStr("PerformancePreset", "1");
            PrivacyControl.ResetForTest(); PrivacyControl.QueueForTest = delegate { throw new InvalidOperationException("Gallery checks cannot queue system work"); };
            CpuTopology.InjectTopologyForTest(255, new ulong[] { 3, 12, 48, 192 }, new ulong[] { 15, 240 }, 0, 0, 0, 0, false, false);
            var items = SyntheticGames(directory);
            Check(new GameProfileStore(directory).Save(items.Select(x => x.Profile).ToList()), "cannot seed isolated profiles");
            PanelForm.SuppressUiWorkersForTest = true;
            PanelForm.WorkspaceAreaForTest = delegate { return new Rectangle(0, 0, Math.Max(width, 1920), Math.Max(height, 880)); };
            PanelForm.WorkspaceDpiForTest = delegate { return dpi; };
            var suppression = new SuppressionCore(); var mode = new GameMode(directory, suppression); var tamer = new Tamer(suppression);
            Trace(output, "construct form");
            Icon icon = IconArt.MakeIcon(24);
            var cyan = MakeIcon(Color.Cyan, Color.Navy); var magenta = MakeIcon(Color.Magenta, Color.Maroon);
            var form = new FixtureForm(tamer, mode, icon);
            privateDesktopResources.Add(icon); privateDesktopResources.Add(cyan); privateDesktopResources.Add(magenta); privateDesktopResources.Add(form);
            // A full private-desktop native form tree can block after WM_DESTROY.
            // Keep these hidden HWNDs until this isolated executable exits. Card
            // content disposal remains exercised for switches, filtering and removal.
            {
                form.LibraryExtensionForTest = delegate(GameProfile profile) { return profile.Id == items[1].Profile.Id ? new SyntheticExtension() : null; };
                // Arrange before creating native handles. WinForms' private-desktop
                // ParkingWindow can block when a full handle-tree rebuild is pumped.
                // The product's layout code still constructs and arranges the controls.
                if (form.ClientSize != new Size(width, height)) {
                    Check(!form.IsHandleCreated, "fixture unexpectedly created handles before arranging");
                    form.ClientSize = new Size(width, height); Call(form, "RebuildUi");
                }
                Trace(output, "show form");
                form.ShowInTaskbar = false; ProtectProviders(form); form.Show(); Pump();
                Trace(output, "show library");
                form.ShowPageForShot(PageId.Library); Call(form, "StopPageReveal"); Pump();
                Trace(output, "assert library");
                var gallery = Field<GameLibraryGallery>(form, "lstGames");
                gallery.IconProvider = delegate(string executable) { return executable.EndsWith("game1.exe", StringComparison.OrdinalIgnoreCase) ? magenta : cyan; };
                gallery.RefreshRows(); Pump();
                Check(!form.UiActive && !mode.Enabled && Field<object>(tamer, "worker") == null, "workspace fixture started runtime workers");
                Check(gallery.Items.Count == 10, "workspace did not load ten isolated games");
                Check(!gallery.HorizontalScroll.Visible, "workspace gallery horizontally scrolls");
                ScrollableControl page = Field<ScrollableControl>(form, "pageLibrary"); Control nav = Field<Control>(form, "nav");
                Check(Relative(form, page).Bottom <= Relative(form, nav).Top, "library overlaps floating navigation");
                Check(!page.AutoScroll && !page.VerticalScroll.Visible, "library retained a second scroll surface");
                Check(page.ClientRectangle.Contains(Relative(page, gallery)), "full-width gallery escaped page");
                Check(!Descendants(page).Any(x => x.Name == "LibraryInspector" || x.Name == "libraryFileLocation" || x.Name == "libraryWhitelist"),
                    "library retained the sidebar, file-location action or whitelist entry");
                Check(gallery.ConfigurationFactory != null && gallery.ExpandedItem == null, "game settings must start collapsed");
                if (width == 1220 && height == 760) {
                    Check(gallery.ColumnCount == 4 && !gallery.VerticalScroll.Visible, "normal library does not show all ten cards without scrolling");
                    foreach (GameLibraryTile tile in gallery.Controls.OfType<GameLibraryTile>().Where(x => x.Visible))
                        Check(gallery.ClientRectangle.Contains(Relative(gallery, tile)), "normal ten-game viewport clips a card action");
                }
                foreach (GameLibraryTile tile in gallery.Controls.OfType<GameLibraryTile>().Where(x => x.Visible)) {
                    Check(tile.ConfigurationButton.Visible && !tile.ConfigurationExpanded, "game card has no collapsed settings action");
                    Check(!tile.AccessibleDescription.Contains("待启动") && tile.AccessibleDescription.IndexOf("Waiting", StringComparison.OrdinalIgnoreCase) < 0,
                        "idle card still shows a redundant waiting state");
                }
                var search = Field<TextBox>(form, "librarySearch");
                Control searchSurface = Field<Control>(form, "librarySearchPanel");
                using (var searchFrame = new Bitmap(searchSurface.Width, searchSurface.Height)) {
                    searchSurface.DrawToBitmap(searchFrame, searchSurface.ClientRectangle);
                    Check(CountPixels(searchFrame, new Rectangle(Theme.S(13), Theme.S(9), Theme.S(22), Theme.S(22)),
                        delegate(Color color) { return color.R > 60 && color.G > 60 && color.B > 60; }) > Theme.S(8),
                        "search magnifier is hidden by surface painting");
                }
                search.Text = "最后"; Pump();
                Check(gallery.VisibleItemCount == 1 && gallery.Items.Count == 10, "page search did not filter the gallery");
                Click(Tile(gallery, items[9].Profile.Id));
                Check(gallery.SelectedIndex == 9 && gallery.ExpandedItem == null, "search selection opened the wrong game's settings");
                search.Text = ""; Pump();
                var platforms = Field<TierPicker>(form, "libraryPlatforms");
                Key(platforms, Keys.End);
                Check(gallery.VisibleItemCount == 1 && Tile(gallery, items[1].Profile.Id).Visible, "page WeGame platform filter selected wrong games");
                Check(gallery.SelectedItem == null && gallery.ExpandedItem == null, "platform filter retained hidden game's editable settings");
                Key(platforms, Keys.Home); Check(gallery.VisibleItemCount == 10, "All platform filter did not restore ten games");
                gallery.AutoScrollPosition = Point.Empty; gallery.SelectedIndex = 1; Pump();
                Trace(output, "save collapsed screenshot");
                Save(form, Path.Combine(output, context + "-ten-games.png"));
                GameLibraryTile selectedTile = Tile(gallery, items[1].Profile.Id);
                Call(selectedTile.ConfigurationButton, "OnClick", EventArgs.Empty); Pump();
                Check(selectedTile.ConfigurationExpanded && gallery.ExpandedItem.Profile.Id == items[1].Profile.Id,
                    "settings action did not expand the clicked game card");
                Control content = selectedTile.ConfigurationContent;
                Check(content != null && content.Parent == selectedTile && content.Name == "LibraryCardConfiguration", "settings are not inside the game card");
                Check(content.Controls.Find("libraryCoreToggle", true).Length == 1 && content.Controls.Find("libraryFamilyToggle", true).Length == 1
                    && content.Controls.Find("libraryRebarToggle", true).Length == 1 && content.Controls.Find("libraryFullscreenToggle", true).Length == 1,
                    "inline game settings lost an existing policy action");
                Check(!Descendants(content).OfType<ScrollableControl>().Any(x => x.AutoScroll), "inline game settings added nested scrolling");
                PillButton expansion = (PillButton)content.Controls.Find("libraryExtensionToggle", true).Single();
                GameCardExtension extension = content.Controls.Find("LibraryGameExtension", true).OfType<GameCardExtension>().Single();
                Check(expansion.Visible && !extension.Visible, "game extension did not start collapsed");
                expansion.PerformClick(); Pump(); Check(extension.Visible, "game extension did not expand inside card");
                Reach(gallery, extension);
                Save(form, Path.Combine(output, context + "-inline-extension.png"));
                gallery.RefreshRows(); Pump();
                Check(selectedTile.ConfigurationContent == content && !content.IsDisposed && extension.Visible,
                    "refresh recreated inline settings or lost extension expansion");
                var escape = new KeyEventArgs(Keys.Escape); Call(form, "OnEscHide", form, escape); Pump();
                Check(content.IsDisposed && extension.IsDisposed && gallery.ExpandedItem == null && form.Visible
                    && selectedTile.ConfigurationButton.Focused && escape.Handled && escape.SuppressKeyPress,
                    "Escape did not collapse inline settings and return focus without hiding window");
                Call(selectedTile.ConfigurationButton, "OnClick", EventArgs.Empty); Pump();
                content = selectedTile.ConfigurationContent;
                extension = content.Controls.Find("LibraryGameExtension", true).OfType<GameCardExtension>().Single();
                gallery.SelectedIndex = 0; Pump();
                Check(!content.IsDisposed && selectedTile.ConfigurationContent == content, "selecting another game moved open settings before configuration click");
                GameLibraryTile firstTile = Tile(gallery, items[0].Profile.Id);
                Call(firstTile.ConfigurationButton, "OnClick", EventArgs.Empty); Pump();
                Check(content.IsDisposed && extension.IsDisposed, "opening another card did not dispose old settings");
                var fullscreen = firstTile.ConfigurationContent.Controls.Find("libraryFullscreenToggle", true).OfType<FamilySuppressionSwitch>().Single();
                Check(fullscreen.Checked == items[0].Profile.DisableFullscreenOptimizations, "inline configuration did not bind to selected game's policy");
                gallery.SelectedIndex = 1; Pump();
                Check(firstTile.ConfigurationContent != null, "selection moved layout before an explicit settings action");
                Check(gallery.CollapseExpandedConfiguration() && firstTile.ConfigurationContent == null, "explicit collapse retained previous settings");
                gallery.SelectedIndex = 1; Pump();
                Trace(output, "captured menu");
                GameLibraryItem captured = gallery.SelectedItem;
                using (var menu = (ContextMenuStrip)Call(form, "BuildLibraryMoreMenu", captured)) {
                    Trace(output, "menu constructed");
                    Check(menu.Items.Cast<ToolStripItem>().Count(x => x.Text.IndexOf("封面", StringComparison.Ordinal) >= 0) >= 2,
                        "game menu does not offer custom cover and restore-default actions");
                    gallery.SelectedIndex = 0; Pump();
                    Trace(output, "menu other selection set");
                    ToolStripItem remove = menu.Items.Cast<ToolStripItem>().Single(x => x.Text == Lang.T("btn.remove"));
                    Trace(output, "remove click"); remove.PerformClick(); Trace(output, "remove returned"); Pump(); Trace(output, "remove pump returned");
                    Check(mode.GetProfiles().Count == 9 && !mode.GetProfiles().Any(x => x.Id == captured.Profile.Id)
                        && mode.GetProfiles().Any(x => x.Id == items[0].Profile.Id), "menu removal acted on current selection instead of captured game");
                }
                Trace(output, "menu disposed");
                captured = gallery.Items.First(x => x.Profile.Id == items[2].Profile.Id);
                using (var staleMenu = (ContextMenuStrip)Call(form, "BuildLibraryMoreMenu", captured)) {
                    Trace(output, "stale menu constructed");
                    mode.RemoveProfile(captured.Profile.Id); Trace(output, "stale target removed");
                    Call(form, "RefreshGames"); Trace(output, "stale refresh returned"); Pump(); Trace(output, "stale pump returned");
                    ToolStripItem remove = staleMenu.Items.Cast<ToolStripItem>().Single(x => x.Text == Lang.T("btn.remove"));
                    remove.PerformClick(); Trace(output, "stale click returned"); Pump(); Trace(output, "stale click pump returned");
                    Check(mode.GetProfiles().Count == 8 && mode.GetProfiles().Any(x => x.Id == items[0].Profile.Id),
                        "stale game menu changed a different game");
                }
                Trace(output, "stale menu disposed");
                fixtures++; Trace(output, "workspace body complete");
                form.Hide(); Pump();
            }
            Trace(output, "workspace hidden; native tree retained until isolated process exit");
        }

        private static void Photo(string output, string source)
        {
            context = "real-three-game-icons";
            byte[] original = File.ReadAllBytes(source);
            string directory = Path.Combine(output, "photo-data"); Directory.CreateDirectory(directory);
            File.WriteAllBytes(Path.Combine(directory, GameProfileStore.FileName), original);
            var store = new GameProfileStore(directory); var profiles = store.LoadProfiles(false).Take(3).ToList();
            Check(profiles.Count == 3 && store.Save(profiles), "photo needs three copied real profiles");
            Paths.Data = directory; Dpi.NoFit = false; Dpi.Scale = 1; Theme.DropFontCache(); Lang.Cur = 0; Theme.SetLight(false);
            Settings.SaveStr("PerformancePreset", "1");
            CpuTopology.InjectTopologyForTest(255, new ulong[] { 3, 12, 48, 192 }, new ulong[] { 15, 240 }, 0, 0, 0, 0, false, false);
            PanelForm.WorkspaceAreaForTest = delegate { return new Rectangle(0, 0, 1920, 1040); };
            PanelForm.WorkspaceDpiForTest = delegate { return 96; };
            var suppression = new SuppressionCore(); var mode = new GameMode(directory, suppression); var tamer = new Tamer(suppression);
            Icon icon = IconArt.MakeIcon(24); var form = new FixtureForm(tamer, mode, icon);
            privateDesktopResources.Add(icon); privateDesktopResources.Add(form);
            form.ClientSize = new Size(1524, 950); Call(form, "RebuildUi");
            form.ShowInTaskbar = false; ProtectProviders(form); form.Show(); Pump();
            form.ShowPageForShot(PageId.Library); Call(form, "StopPageReveal"); Pump();
            var gallery = Field<GameLibraryGallery>(form, "lstGames");
            Check(gallery.Items.Count == 3 && gallery.ExpandedItem == null, "photo lost real profile copies");
            foreach (GameProfile profile in profiles) {
                Check(File.Exists(profile.ExecutablePath), "real executable missing for original icon: " + profile.Name);
                Check(gallery.IconProvider(profile.ExecutablePath) != null, "real executable icon did not extract: " + profile.Name);
            }
            Check(!mode.Enabled && !form.UiActive && Field<object>(tamer, "worker") == null, "photo started product runtime workers");
            Save(form, Path.Combine(output, "Photo-real-game-icons.png"));
            form.Hide(); Pump();
            Check(original.SequenceEqual(File.ReadAllBytes(source)), "photo changed original profile file");
            fixtures++;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateDesktop(string name, IntPtr device, IntPtr mode, uint flags, uint access, IntPtr security);
        [DllImport("user32.dll")] private static extern bool SetThreadDesktop(IntPtr desktop);
        [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr key, IntPtr data);
        private static int OnPrivateDesktop(Action action)
        {
            IntPtr desktop = CreateDesktop("PaviseGallery-" + Guid.NewGuid().ToString("N"), IntPtr.Zero, IntPtr.Zero, 0, 0x01FF, IntPtr.Zero);
            if (desktop == IntPtr.Zero) { Console.Error.WriteLine("FAIL private desktop unavailable"); return 1; }
            int result = 1;
            try {
                var thread = new Thread(delegate() {
                    if (!SetThreadDesktop(desktop)) { Console.Error.WriteLine("FAIL private desktop assignment refused"); return; }
                    try {
                        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException); Control.CheckForIllegalCrossThreadCalls = true;
                        action(); result = 0;
                    } catch (Exception error) { Console.Error.WriteLine(error); }
                    finally { Console.Out.Flush(); Console.Error.Flush(); Environment.Exit(result); }
                });
                thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join(); return result;
            } finally { CloseDesktop(desktop); }
        }
        [STAThread] private static int Main(string[] args)
        {
            Console.OutputEncoding = new System.Text.UTF8Encoding(false);
            if (args.Length == 1 && args[0] == "--negative-control") {
                context = "negative";
                try {
                    using (var wrongIcon = MakeIcon(Color.Magenta, Color.Maroon))
                        Check(CountPixels(wrongIcon, new Rectangle(Point.Empty, wrongIcon.Size), Cyan) > 100,
                            "intentional original-icon mismatch");
                }
                catch (InvalidOperationException error) { Console.WriteLine(error.Message); return 3; }
                return 0;
            }
            if (args.Length < 1 || Path.GetFullPath(args[0]).TrimEnd('\\') != AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\')) return 2;
            return OnPrivateDesktop(delegate {
                Settings.UseTransientStoreForCurrentProcess(); Logger.WritesEnabled = false;
                PanelForm.SuppressUiWorkersForTest = true; Dpi.Init(); Lang.Init();
                int photo = Array.IndexOf(args, "--photo");
                if (photo >= 0) {
                    if (photo + 1 >= args.Length) throw new ArgumentException("Photo profile file missing");
                    Photo(args[0], args[photo + 1]);
                    Console.WriteLine("PASS library gallery assertions=" + assertions + " fixtures=" + fixtures + " screenshots=" + screenshots
                        + "; read-only original profiles copied to isolated data; original executable icons; runtime workers suppressed");
                    return;
                }
                bool single = args.Contains("--single"), galleryOnly = args.Contains("--gallery-only");
                foreach (int dpi in single ? new[] { 96 } : new[] { 96, 120, 144, 192 })
                    foreach (int width in single ? new[] { 1120 } : new[] { 1120, 600, 280 }) {
                        string directory = Path.Combine(args[0], "gallery-data-" + dpi + "-" + width); Directory.CreateDirectory(directory);
                        Gallery(directory, args[0], dpi, width, 0, false);
                    }
                if (!single) {
                    string directory = Path.Combine(args[0], "gallery-data-light"); Directory.CreateDirectory(directory);
                    Gallery(directory, args[0], 96, 1120, 1, true);
                }
                foreach (Size size in galleryOnly ? new Size[0] : single ? new[] { new Size(1220, 760) } : new[] { new Size(1220, 760), new Size(800, 620), new Size(640, 480) }) {
                    string directory = Path.Combine(args[0], "workspace-data-" + size.Width); Directory.CreateDirectory(directory);
                    Workspace(directory, args[0], size.Width, size.Height, 96);
                }
                Console.WriteLine("PASS library gallery assertions=" + assertions + " fixtures=" + fixtures + " screenshots=" + screenshots
                    + "; private desktop, synthetic profiles, original executable icons, custom cover, transient settings, runtime workers suppressed");
            });
        }
    }
}
#endif
