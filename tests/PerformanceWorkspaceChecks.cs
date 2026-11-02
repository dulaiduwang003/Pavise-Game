#if PAVISE_PERFORMANCE_WORKSPACE_CHECKS || PAVISE_PERFORMANCE_PAGE_CHECKS
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Threading;
using System.Windows.Forms;

namespace PaviseApp
{
    // Shared real-Forms assertions. Validation never arranges a card itself.
    internal static class PerformanceWorkspaceLayout
    {
        internal static int Checks;
        internal static int NativeScrollCases, QuietReflowPasses;
        private static bool movementObserverCalibrated;
        internal static string Context = "";
        internal static void Check(bool ok, string detail)
        { Checks++; if (!ok) throw new InvalidOperationException("FAIL: " + Context + ": " + detail); }
        internal static void Pump() { Application.DoEvents(); }
        internal static IEnumerable<PerformanceFeatureCard> Cards(Control owner)
        {
            foreach (Control child in owner.Controls) {
                var card = child as PerformanceFeatureCard;
                if (card != null) yield return card;
                else foreach (var nested in Cards(child)) yield return nested;
            }
        }
        internal static IEnumerable<Control> Descendants(Control owner)
        {
            foreach (Control child in owner.Controls) { yield return child; foreach (var nested in Descendants(child)) yield return nested; }
        }
        private static void TextFits(PerformanceFeatureCard card, string text, Rectangle bounds, Font font, string label)
        {
            if (string.IsNullOrEmpty(text)) return;
            Check(bounds.Width > 0 && bounds.Height > 0 && card.ClientRectangle.Contains(bounds), label + " outside card: " + card.Title);
            int fullHeight = TextRenderer.MeasureText(text, font, new Size(Math.Max(1, bounds.Width), int.MaxValue),
                TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Height;
            Check(bounds.Height >= fullHeight, label + " truncates full text: " + card.Title);
        }
        internal static void Validate(PerformanceFeatureGrid grid)
        {
            Check(grid.Visible && grid.Parent != null, "active grid is hidden or detached");
            if (!grid.AutoSizeContent) Check(grid.Parent.ClientRectangle.Contains(grid.Bounds), "bottom viewport exceeds its page parent");
            else Check(grid.Height == grid.ContentHeight, "nested content height was clipped to the viewport");
            Check(!grid.HorizontalScroll.Visible && grid.AutoScrollPosition.X == 0, "horizontal overflow appeared: width=" + grid.Width + ", client=" + grid.ClientSize
                + ", display=" + grid.DisplayRectangle + ", scroll=" + grid.AutoScrollPosition + ", min=" + grid.AutoScrollMinSize
                + ", vertical=" + grid.VerticalScroll.Visible + ", horizontal=" + grid.HorizontalScroll.Visible
                + ", direct=" + string.Join("; ", grid.Controls.Cast<Control>().Select(x => x.GetType().Name + " " + x.Bounds)));
            var surfaces = new List<Control>();
            foreach (Control child in grid.Controls) {
                Check(child.Visible, "section/card is hidden: " + child.Name);
                Check(child.Left >= 0 && child.Right <= grid.ClientSize.Width, "surface exceeds horizontal viewport: " + child.Name);
                surfaces.Add(child);
                var heading = child as PerformanceSectionHeading;
                if (heading != null) {
                    Check(!heading.TabStop && heading.AccessibleRole != AccessibleRole.PushButton, "heading is an interactive collapse control");
                    Check((heading.AccessibilityObject.State & (AccessibleStates.Collapsed | AccessibleStates.Expanded)) == 0, "heading advertises collapse state");
                    Check(heading.Height >= heading.MeasureHeight(heading.Width), "static heading title clips");
                    Check(heading.ClientRectangle.Contains(heading.TitleBounds) && heading.ClientRectangle.Contains(heading.ScopeBounds), "restart heading/badge clips");
                    Check(!heading.TitleBounds.IntersectsWith(heading.ScopeBounds), "restart badge overlaps title");
                    int badgeWidth = TextRenderer.MeasureText(heading.ScopeText, Theme.UI(8f, false), Size.Empty,
                        TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;
                    Check(heading.ScopeBounds.Width >= badgeWidth, "restart badge text is truncated");
                }
            }
            for (int i = 0; i < surfaces.Count; i++) for (int j = i + 1; j < surfaces.Count; j++)
                Check(!surfaces[i].Bounds.IntersectsWith(surfaces[j].Bounds), "sections/cards overlap");
            int count = 0;
            foreach (var card in Cards(grid)) {
                count++;
                Check(card.Visible && !card.Collapsible, "feature is hidden or collapsible: " + card.Title);
                Check(card.Left >= 0 && card.Right <= card.Parent.ClientSize.Width, "nested feature exceeds available width: " + card.Title);
                TextFits(card, card.Title, card.TitleBounds, Theme.UI(10.5f, true), "title");
                TextFits(card, card.StatusText, card.StatusBounds, Theme.UI(9f, false), "status");
                TextFits(card, card.LockText, card.LockBounds, Theme.UI(8.5f, false), "lock");
                TextFits(card, card.Desc, card.DetailBounds, Theme.UI(9f, false), "always-visible explanation");
                Check(card.Controls.Find("PerformanceFeatureDetails", true).Length == 0, "details button remains");
                Check(card.AccessibleName == card.Title && card.AccessibleDescription.Contains(card.Desc), "full explanation/accessibility missing");
                if (card.Hosted != null) {
                    Check(card.Hosted.Visible && card.ClientRectangle.Contains(card.Hosted.Bounds), "primary action clips: " + card.Title);
                    Check(!card.Hosted.Bounds.IntersectsWith(card.TitleBounds) && !card.Hosted.Bounds.IntersectsWith(card.StatusBounds)
                        && !card.Hosted.Bounds.IntersectsWith(card.DetailBounds) && !card.Hosted.Bounds.IntersectsWith(card.LockBounds), "primary action overlaps reading content: " + card.Title);
                }
            }
            Check(count > 0, "fixture contains no real feature cards");
            int span = surfaces.Count == 0 ? 0 : surfaces.Max(x => x.Width);
            foreach (var row in grid.Controls.OfType<PerformanceFeatureCard>().GroupBy(x => x.Top)) {
                var cards = row.ToArray();
                if (grid.Columns == 2 && cards.Length == 2) Check(cards[0].Height == cards[1].Height, "left/right row heights are not aligned");
                if (cards.Length == 1) Check(cards[0].Width == span, "single/orphan card leaves a half-column blank");
            }
            foreach (var nested in Descendants(grid).OfType<PerformanceFeatureGrid>()) {
                Check(nested.AutoSizeContent && !nested.AutoScroll && !nested.VerticalScroll.Visible, "nested group creates a second scroll viewport");
                // Text and row geometry inside privacy groups are independent of
                // the root group's full-width wrapper geometry.
                Validate(nested);
            }
        }
        internal static Dictionary<Control, Rectangle> LogicalBounds(PerformanceFeatureGrid grid)
        {
            var result = new Dictionary<Control, Rectangle>(); Point origin = grid.AutoScrollPosition;
            foreach (Control child in grid.Controls) result.Add(child, new Rectangle(child.Left - origin.X, child.Top - origin.Y, child.Width, child.Height));
            return result;
        }
        private sealed class MovementTrace : IDisposable
        {
            private readonly PerformanceFeatureGrid grid;
            private readonly bool beganScrolled;
            private readonly ScrollEventHandler scrollHandler;
            private readonly List<KeyValuePair<Control, EventHandler>> handlers = new List<KeyValuePair<Control, EventHandler>>();
            internal readonly Dictionary<Control, int> Moves = new Dictionary<Control, int>();
            internal readonly List<string> Events = new List<string>();
            internal int ScrollEvents, ZeroVisits;
            internal MovementTrace(PerformanceFeatureGrid owner)
            {
                grid = owner; beganScrolled = owner.AutoScrollPosition.Y < 0;
                scrollHandler = delegate(object sender, ScrollEventArgs args) { ScrollEvents++; Observe("scroll " + args.Type); };
                grid.Scroll += scrollHandler;
                foreach (Control child in grid.Controls) {
                    Control captured = child; Moves.Add(child, 0);
                    EventHandler handler = delegate { Moves[captured]++; Observe(captured.GetType().Name + " " + captured.Bounds); };
                    captured.LocationChanged += handler; handlers.Add(new KeyValuePair<Control, EventHandler>(captured, handler));
                }
            }
            private void Observe(string value)
            {
                if (beganScrolled && grid.AutoScrollPosition.Y == 0) ZeroVisits++;
                if (Events.Count < 12) Events.Add(value + " origin=" + grid.AutoScrollPosition);
            }
            internal string Detail { get { return "moves=" + Moves.Values.Sum() + ", scrollEvents=" + ScrollEvents + ", zeroVisits=" + ZeroVisits + ", events=" + string.Join("; ", Events); } }
            public void Dispose()
            { grid.Scroll -= scrollHandler; foreach (var entry in handlers) entry.Key.LocationChanged -= entry.Value; }
        }
        internal static void StableReflow(PerformanceFeatureGrid grid, int times)
        {
            var expected = LogicalBounds(grid); int height = grid.AutoScrollMinSize.Height;
            for (int pass = 0; pass < times; pass++) {
                Point beforeScroll = grid.AutoScrollPosition;
                using (var trace = new MovementTrace(grid)) {
                    grid.Reflow(); Pump();
                    Check(trace.Moves.Values.Sum() == 0 && trace.ScrollEvents == 0 && trace.ZeroVisits == 0,
                        "unchanged reflow moves the whole page or resets/restores scroll: " + trace.Detail);
                }
                QuietReflowPasses++;
                Check(grid.AutoScrollPosition == beforeScroll, "unchanged reflow changed the actual scroll position");
                var actual = LogicalBounds(grid);
                Check(grid.AutoScrollMinSize.Height == height && expected.Count == actual.Count, "reflow changes virtual content extent");
                foreach (var item in expected) Check(actual.ContainsKey(item.Key) && actual[item.Key] == item.Value, "scrolled reflow applies origin twice or drifts a surface");
                Validate(grid);
            }
        }
        internal static void QuietOperation(PerformanceFeatureGrid grid, Action action, string name)
        {
            Point before = grid.AutoScrollPosition;
            using (var trace = new MovementTrace(grid)) {
                action(); Pump();
                Check(trace.Moves.Values.Sum() == 0 && trace.ScrollEvents == 0 && trace.ZeroVisits == 0 && grid.AutoScrollPosition == before,
                    name + " triggers a viewport round trip: " + trace.Detail);
            }
        }
        private static void ActualScrollWithoutRoundTrip(PerformanceFeatureGrid grid)
        {
            if (!grid.VerticalScroll.Visible) return;
            int maximum = Math.Max(0, grid.VerticalScroll.Maximum - grid.VerticalScroll.LargeChange + 1);
            if (maximum < 4) return; // Need a nonzero interior start and a distinct destination.
            grid.AutoScrollPosition = new Point(0, Math.Max(1, maximum / 3)); Pump();
            Point start = grid.AutoScrollPosition; var logical = LogicalBounds(grid);
            Check(start.Y < 0, "real scroll case did not begin away from the top");
            using (var trace = new MovementTrace(grid)) {
                Check(PostMessage(grid.Handle, 0x115, new IntPtr(1), IntPtr.Zero), "cannot post actual vertical line-scroll message");
                Pump();
                Check(trace.ScrollEvents > 0 && grid.AutoScrollPosition.Y < start.Y, "actual WM_VSCROLL did not move the viewport");
                Check(trace.ZeroVisits == 0 && trace.Moves.Values.All(x => x <= 1), "one actual scroll causes zero/restore or repeated child movement: " + trace.Detail);
            }
            var after = LogicalBounds(grid);
            foreach (var item in logical) Check(after[item.Key] == item.Value, "actual scroll changed logical card geometry");
            if (!movementObserverCalibrated) {
                // Calibrate against a real zero-and-back sequence whose final
                // position is unchanged; otherwise a silent event observer could
                // let precisely the old regression pass unnoticed.
                Point sameFinal = grid.AutoScrollPosition;
                using (var trace = new MovementTrace(grid)) {
                    grid.AutoScrollPosition = Point.Empty;
                    grid.AutoScrollPosition = new Point(0, -sameFinal.Y); Pump();
                    Check(grid.AutoScrollPosition == sameFinal && trace.Moves.Values.Sum() > 0,
                        "movement observer missed a real zero-and-back sequence with the same final position: " + trace.Detail);
                }
                movementObserverCalibrated = true;
            }
            NativeScrollCases++; StableReflow(grid, 3);
        }
        internal static void NativePageStep(PerformanceFeatureGrid grid)
        {
            Check(grid.VerticalScroll.Visible && grid.VerticalScroll.LargeChange == grid.ClientSize.Height,
                "native scroll page size differs from the resized viewport height");
            int maximum = Math.Max(0, grid.VerticalScroll.Maximum - grid.VerticalScroll.LargeChange + 1);
            grid.AutoScrollPosition = Point.Empty; Pump();
            Check(PostMessage(grid.Handle, 0x115, new IntPtr(3), IntPtr.Zero), "cannot post actual page-down scroll message"); Pump();
            Check(-grid.AutoScrollPosition.Y == Math.Min(maximum, grid.VerticalScroll.LargeChange), "native page-down uses a stale viewport height");
        }
        internal static Rectangle InGrid(PerformanceFeatureGrid grid, Control control)
        { return grid.RectangleToClient(control.RectangleToScreen(new Rectangle(Point.Empty, control.Size))); }
        internal static Control LastFeature(PerformanceFeatureGrid grid)
        {
            Control last = null; int logicalBottom = int.MinValue;
            foreach (var card in Cards(grid)) {
                int bottom = InGrid(grid, card).Bottom - grid.AutoScrollPosition.Y;
                if (bottom > logicalBottom) { logicalBottom = bottom; last = card; }
            }
            Check(last != null, "last feature is missing"); return last;
        }
        internal static void BottomReachable(PerformanceFeatureGrid grid)
        {
            var last = LastFeature(grid);
            grid.AutoScrollPosition = Point.Empty; Pump(); grid.ScrollControlIntoView(last); Pump();
            Rectangle revealed = InGrid(grid, last);
            Check(revealed.Bottom > 0 && revealed.Top < grid.ClientSize.Height, "ScrollControlIntoView does not reach the last feature");
            if (last.Height <= grid.ClientSize.Height)
                Check(revealed.Top >= 0 && revealed.Bottom <= grid.ClientSize.Height, "last fitting feature is not fully visible after ScrollControlIntoView");
            grid.Reveal(last); Pump(); Check(last.Visible, "Reveal hides or detaches the target");
            int maximum = Math.Max(0, grid.VerticalScroll.Maximum - grid.VerticalScroll.LargeChange + 1);
            grid.AutoScrollPosition = new Point(0, maximum); Pump();
            Rectangle bottom = InGrid(grid, last);
            Check(bottom.Bottom > 0 && bottom.Bottom <= grid.ClientSize.Height, "maximum scroll leaves last feature bottom clipped");
            if (grid.AutoScrollMinSize.Height > grid.ClientSize.Height + Theme.S(8))
                Check(grid.AutoScrollPosition.Y < 0, "overflow fixture never actually scrolled");
            StableReflow(grid, 4);
            Check(InGrid(grid, last).Bottom <= grid.ClientSize.Height, "reflow at bottom made the last feature unreachable");
            ActualScrollWithoutRoundTrip(grid);
            grid.AutoScrollPosition = new Point(0, maximum); Pump();
            Check(InGrid(grid, last).Bottom <= grid.ClientSize.Height, "actual scrolling made the final feature unreachable");
        }
        internal static void CheckRemovedInteractions()
        {
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            foreach (string name in new[] { "DetailsOpen", "DetailsButton", "SetDetailsOpen" })
                Check(typeof(PerformanceFeatureCard).GetMember(name, flags).Length == 0, "removed details API remains: " + name);
            foreach (string name in new[] { "CanCollapse", "Expanded", "Toggle" })
                Check(typeof(PerformanceSectionHeading).GetMember(name, flags).Length == 0, "removed heading interaction remains: " + name);
        }
        internal static void Save(Control control, string path)
        {
            using (var bitmap = new Bitmap(control.Width, control.Height)) {
                control.DrawToBitmap(bitmap, control.ClientRectangle); bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
        }
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr key, IntPtr data);
        internal static void CategoryKeyboard(PerformanceCategoryBar bar, Func<int> selected)
        {
            bar.Select(0, true); Pump();
            Keys[] keys = { Keys.Right, Keys.End, Keys.Left, Keys.Home };
            int[] targets = { 1, bar.Controls.Count - 1, bar.Controls.Count - 2, 0 };
            for (int index = 0; index < keys.Length; index++) {
                var button = (PerformanceActionButton)bar.Controls[selected()];
                Check(button.Focus(), "category cannot receive real keyboard focus");
                Message probe = Message.Create(button.Handle, 0x100, new IntPtr((int)keys[index]), new IntPtr(1));
                Check(!button.PreProcessMessage(ref probe), "category arrow/navigation is swallowed as a dialog key before KeyDown");
                Check(PostMessage(button.Handle, 0x100, new IntPtr((int)keys[index]), new IntPtr(1)), "cannot post UI navigation key");
                Check(PostMessage(button.Handle, 0x101, new IntPtr((int)keys[index]), new IntPtr(unchecked((int)0xC0000001))), "cannot post UI key-up");
                Pump();
                Check(selected() == targets[index], "posted navigation did not pass the real Forms preprocessing/dispatch path");
            }
            var inputMethod = typeof(PerformanceActionButton).GetMethod("IsInputKey", BindingFlags.Instance | BindingFlags.NonPublic);
            foreach (Keys modifier in new[] { Keys.Control, Keys.Alt, Keys.Shift })
                Check(!(bool)inputMethod.Invoke(bar.Controls[0], new object[] { modifier | Keys.Right }), "modified arrow was classified as category navigation");
        }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateDesktop(string name, IntPtr device, IntPtr mode, uint flags, uint access, IntPtr security);
        [DllImport("user32.dll")] private static extern bool SetThreadDesktop(IntPtr desktop);
        [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
        internal static int PrivateDesktop(Action action)
        {
            Console.WriteLine("layout stage: create private desktop");
            IntPtr desktop = CreateDesktop("PaviseLayout-" + Guid.NewGuid().ToString("N"), IntPtr.Zero, IntPtr.Zero, 0, 0x01FF, IntPtr.Zero);
            if (desktop == IntPtr.Zero) { Console.Error.WriteLine("FAIL: private layout desktop unavailable"); return 1; }
            int result = 1;
            try {
                var thread = new Thread(delegate() {
                    if (!SetThreadDesktop(desktop)) { Console.Error.WriteLine("FAIL: private layout desktop assignment refused"); return; }
                    try {
                        Console.WriteLine("layout stage: private STA visual initialization");
                        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException); Control.CheckForIllegalCrossThreadCalls = true;
                        Console.WriteLine("layout stage: start fixture"); action(); result = 0;
                        Console.WriteLine("layout stage: fixture complete");
                    } catch (Exception error) { Console.Error.WriteLine(error); }
                    finally {
                        // Forms' hidden timer/parking windows can keep private-
                        // desktop thread teardown waiting. The form has already
                        // been disposed by its using scope; terminate only this
                        // isolated check executable, as the original page suite did.
                        Console.Out.Flush(); Console.Error.Flush(); Environment.Exit(result);
                    }
                });
                thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join(); return result;
            } finally { CloseDesktop(desktop); }
        }
    }
#if PAVISE_PERFORMANCE_WORKSPACE_CHECKS
    internal static partial class SelfTests
    { public static bool TryHandleRuntimeMode(string[] args) { throw new InvalidOperationException("Layout checks cannot start product runtime"); } }
    internal static class PerformanceWorkspaceChecks
    {
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        private static int layouts, inlineResizeCases, heightResizeCases;
        private static SettingCard Make(Control parent, string title, string description, Control action)
        {
            var shell = (PanelForm)FormatterServices.GetUninitializedObject(typeof(PanelForm)); GC.SuppressFinalize(shell);
            return (SettingCard)typeof(PanelForm).GetMethod("MakeCard", Hidden).Invoke(shell,
                new object[] { parent, 6, 2, 900, 120, title, description, action });
        }
        private sealed class FixtureForm : Form
        {
            protected override bool ShowWithoutActivation { get { return true; } }
            protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ExStyle |= 0x08000000; return cp; } }
        }
        private static void ResizeInlineNow(PerformanceFeatureGrid grid, PerformanceFeatureCard card, int logicalWidth)
        {
            int rowHeight = card.Height;
            PerformanceWorkspaceLayout.QuietOperation(grid, delegate {
                card.Hosted.Width = Theme.S(logicalWidth);
                // Inspect synchronously inside the resize operation, before the
                // harness pumps or paints and could mask missing arrangement.
                PerformanceWorkspaceLayout.Check(card.Hosted.Right == card.Width - card.TitleBounds.Left,
                    "inline host resize did not immediately preserve right alignment");
                PerformanceWorkspaceLayout.Check(card.Height == rowHeight && card.ClientRectangle.Contains(card.Hosted.Bounds),
                    "inline host resize changed a tall shared row or clipped the action");
                PerformanceWorkspaceLayout.Check(!card.Hosted.Bounds.IntersectsWith(card.TitleBounds)
                    && !card.Hosted.Bounds.IntersectsWith(card.StatusBounds) && !card.Hosted.Bounds.IntersectsWith(card.DetailBounds),
                    "inline host resize left stale internal reading bounds");
            }, "same-row inline host resize");
            inlineResizeCases++;
        }
        private static void Run(string output, int dpi, int language, bool light)
        {
            PerformanceWorkspaceLayout.Context = "component " + dpi + "/" + language + "/" + light;
            Console.WriteLine(PerformanceWorkspaceLayout.Context);
            Dpi.NoFit = true; Dpi.Scale = dpi / 96f; Theme.DropFontCache(); Theme.SetLight(light); Lang.Cur = language;
            using (var form = new FixtureForm { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual,
                Location = new Point(-32000, -32000), ClientSize = new Size(Theme.S(980), Theme.S(650)) })
            using (var grid = new PerformanceFeatureGrid { Location = new Point(Theme.S(10), Theme.S(60)), Size = new Size(Theme.S(960), Theme.S(560)) })
            using (var legacy = new Panel()) {
                form.Controls.Add(grid); int changes = 0;
                int selectedCategory = 0;
                var categories = new PerformanceCategoryBar(new[] { "Game", "Background", "Graphics", "System" }) {
                    Location = new Point(Theme.S(10), Theme.S(10)), Size = new Size(Theme.S(960), Theme.S(38)) };
                categories.SelectionChanged = delegate(int value) { selectedCategory = value; }; form.Controls.Add(categories);
                var action = new Toggle { Size = new Size(Theme.S(46), Theme.S(24)), AccessibleName = "Fixture primary action" };
                action.CheckedChanged += delegate { changes++; };
                for (int section = 0; section < 3; section++) {
                    grid.Controls.Add(new PerformanceSectionHeading { Text = PerformancePolicy.Text("静态分区 ", "Static section ") + section, RestartScope = (PerformanceRestartScope)section });
                    for (int index = 0; index < 3; index++) {
                        string detail = section == 1 && index == 1 ? new string('长', 260) + "\n" + new string('W', 240)
                            : PerformancePolicy.Text("说明始终完整显示。此项按手动选择执行，退出后恢复原配置。", "The complete explanation stays visible. This follows the manual choice and restores the original configuration on exit.");
                        if (section == 0 && index == 0) detail = PerformancePolicy.Text("完整显示第一行说明。\n完整显示第二行说明。",
                            "The first explanation line stays visible.\nThe second line is also fully visible.");
                        Control hosted = section == 0 && index == 0 ? (Control)action : index == 1
                            ? (Control)new TierPicker { Labels = new[] { "Off", "CPU", "Render" }, Size = new Size(Theme.S(380), Theme.S(30)) }
                            : new PerformanceActionButton(PerformancePolicy.Text("打开", "Open")) { Size = new Size(Theme.S(92), Theme.S(30)) };
                        var card = (PerformanceFeatureCard)Make(grid, PerformancePolicy.Text("功能 ", "Feature ") + section + "/" + index, detail, hosted);
                        if (section == 1 && index == 0) card.Name = "InlinePillFixture";
                        card.SetStatus(PerformancePolicy.Text("未验证收益 · 保留手动偏好", "Benefit unverified; manual preference retained"), Theme.Dim);
                        if (section == 1 && index == 1) { card.SetStatus(new string('R', 180), Theme.Warning); card.SetLock(new string('L', 110), true); }
                    }
                }
                PerformanceWorkspaceLayout.Check(Make(legacy, "Legacy", "Legacy description", null).GetType() == typeof(SettingCard), "performance factory changed other pages");
                Console.WriteLine("component stage: show"); grid.Complete(); form.Show(); PerformanceWorkspaceLayout.Pump();
                Console.WriteLine("component stage: keyboard");
                PerformanceWorkspaceLayout.CategoryKeyboard(categories, delegate { return selectedCategory; });
                var compactReference = (PerformanceFeatureCard)action.Parent;
                int intrinsicHeight = compactReference.PresentationHeight(Theme.S(410));
                Console.WriteLine("compact two-line intrinsic height=" + intrinsicHeight + " dpi=" + dpi);
                PerformanceWorkspaceLayout.Check(intrinsicHeight >= Theme.S(88) && intrinsicHeight <= Theme.S(118),
                    "two-line toggle reference is outside the agreed compact height budget: " + intrinsicHeight);
                foreach (int width in new[] { 960, 560, 960, 560 }) {
                    PerformanceWorkspaceLayout.Context = "component " + dpi + "/" + language + "/" + light + "/viewport=" + width;
                    Console.WriteLine("component stage: viewport " + width);
                    grid.Width = Theme.S(width); grid.Reflow(); PerformanceWorkspaceLayout.Pump();
                    PerformanceWorkspaceLayout.Check(grid.Columns == (width == 960 ? 2 : 1), "fixture failed to cross the column threshold");
                    PerformanceWorkspaceLayout.Validate(grid); PerformanceWorkspaceLayout.BottomReachable(grid); layouts++;
                    PerformanceWorkspaceLayout.Check(changes == 0, "reading, scrolling or reflow changed a setting");
                }
                grid.Width = Theme.S(960); grid.Reflow(); PerformanceWorkspaceLayout.Pump();
                if (language == 0 && light) {
                    // Only height changes: card/row geometry should stay fixed,
                    // but the native scrollbar page size must be refreshed.
                    var sameRows = PerformanceWorkspaceLayout.LogicalBounds(grid); int sameContent = grid.ContentHeight;
                    foreach (int height in new[] { 340, 560 }) {
                        grid.Height = Theme.S(height); PerformanceWorkspaceLayout.Pump();
                        PerformanceWorkspaceLayout.Check(grid.ContentHeight == sameContent, "height-only resize changed the logical content height");
                        var rowsNow = PerformanceWorkspaceLayout.LogicalBounds(grid);
                        foreach (var row in sameRows) PerformanceWorkspaceLayout.Check(rowsNow[row.Key] == row.Value, "height-only resize rearranged cards");
                        PerformanceWorkspaceLayout.NativePageStep(grid); PerformanceWorkspaceLayout.BottomReachable(grid); heightResizeCases++;
                    }
                }
                var toggleCard = (PerformanceFeatureCard)action.Parent;
                var togglePeer = grid.Controls.OfType<PerformanceFeatureCard>().Skip(1).First();
                PerformanceWorkspaceLayout.Check(toggleCard.Top == togglePeer.Top && toggleCard.Height == togglePeer.Height
                    && toggleCard.Height > toggleCard.PresentationHeight(toggleCard.Width), "toggle resize fixture lacks a taller paired neighbor");
                ResizeInlineNow(grid, toggleCard, 66); ResizeInlineNow(grid, toggleCard, 38); ResizeInlineNow(grid, toggleCard, 46);
                var pillCard = grid.Controls.OfType<PerformanceFeatureCard>().Single(x => x.Name == "InlinePillFixture");
                var pillPeer = grid.Controls.OfType<PerformanceFeatureCard>().First(x => x != pillCard && x.Top == pillCard.Top);
                PerformanceWorkspaceLayout.Check(pillCard.Height == pillPeer.Height && pillCard.Height > pillCard.PresentationHeight(pillCard.Width),
                    "pill resize fixture lacks a taller paired neighbor");
                ResizeInlineNow(grid, pillCard, 132); ResizeInlineNow(grid, pillCard, 78); ResizeInlineNow(grid, pillCard, 92);
                var nestedHeading = new PerformanceSectionHeading { Text = "Nested source", RestartScope = PerformanceRestartScope.SystemRestart };
                var nested = new PerformanceFeatureGrid { AutoSizeContent = true, Width = Theme.S(900) };
                var nestedToggle = new Toggle { Size = action.Size };
                var nestedCard = (PerformanceFeatureCard)Make(nested, "Nested feature", "Always-visible nested explanation", nestedToggle);
                nestedCard.SetStatus("Unchanged text", Theme.Dim); nested.Complete();
                grid.Controls.Add(nestedHeading); grid.Controls.Add(nested); PerformanceWorkspaceLayout.Pump();
                var first = action.Parent as PerformanceFeatureCard;
                var firstHeading = grid.Controls.OfType<PerformanceSectionHeading>().First();
                string sameStatus = first.StatusText;
                first.SetStatus(sameStatus, Theme.Warning); PerformanceWorkspaceLayout.Pump();
                PerformanceWorkspaceLayout.Check(firstHeading.AttentionCount == 1, "same-text warning color change did not refresh section attention without resize");
                first.SetStatus(sameStatus, Theme.Dim); PerformanceWorkspaceLayout.Pump();
                PerformanceWorkspaceLayout.Check(firstHeading.AttentionCount == 0, "same-text warning clearance left stale section attention");
                action.Checked = true; PerformanceWorkspaceLayout.Pump();
                PerformanceWorkspaceLayout.Check(firstHeading.SelectedCount == 1 && changes == 1, "toggle state did not refresh section selection without resize");
                action.SetSilently(false); PerformanceWorkspaceLayout.Pump();
                PerformanceWorkspaceLayout.Check(firstHeading.SelectedCount == 0 && changes == 1, "silent toggle update left stale selection or fired its handler");
                int nestedHeight = nested.Height;
                nestedCard.SetStatus("Unchanged text", Theme.Warning); PerformanceWorkspaceLayout.Pump();
                PerformanceWorkspaceLayout.Check(nested.Height == nestedHeight && nestedHeading.AttentionCount == 1, "same-height nested warning did not update the root heading");
                nestedToggle.Checked = true; PerformanceWorkspaceLayout.Pump();
                PerformanceWorkspaceLayout.Check(nested.Height == nestedHeight && nestedHeading.SelectedCount == 1, "same-height nested toggle did not update the root heading");
                nestedToggle.SetSilently(false); PerformanceWorkspaceLayout.Pump();
                PerformanceWorkspaceLayout.Check(nestedHeading.SelectedCount == 0, "same-height nested silent toggle left stale root selection");
                changes = 0; action.SetSilently(true); action.Enabled = false;
                typeof(PerformanceFeatureCard).GetMethod("OnMouseUp", Hidden).Invoke(first, new object[] { new MouseEventArgs(MouseButtons.Left, 1, 20, 20, 0) });
                PerformanceWorkspaceLayout.Check(changes == 0 && action.Checked, "card reading surface changed a disabled setting");
                grid.AutoScrollPosition = Point.Empty; grid.Reflow(); PerformanceWorkspaceLayout.Pump();
                PerformanceWorkspaceLayout.Save(form, Path.Combine(output, "component-" + dpi + "-" + language + "-" + light + ".png")); form.Hide();
            }
        }
        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length == 1 && args[0] == "--negative-control") { Console.WriteLine("FAIL: intentional workspace layout negative control"); return 3; }
            return PerformanceWorkspaceLayout.PrivateDesktop(delegate {
                Settings.UseTransientStoreForCurrentProcess(); Logger.WritesEnabled = false; Directory.CreateDirectory(args[0]); PerformanceWorkspaceLayout.CheckRemovedInteractions();
                bool single = args.Length > 1 && args[1] == "--single";
                foreach (int dpi in single ? new[] { 96 } : new[] { 96, 144, 192 }) foreach (int language in single ? new[] { 0 } : new[] { 0, 1 }) foreach (bool light in single ? new[] { true } : new[] { false, true }) Run(args[0], dpi, language, light);
                Console.WriteLine("PASS component layout assertions=" + PerformanceWorkspaceLayout.Checks + " viewports=" + layouts
                    + " nativeScrollCases=" + PerformanceWorkspaceLayout.NativeScrollCases + " quietReflowPasses=" + PerformanceWorkspaceLayout.QuietReflowPasses
                    + " inlineResizeCases=" + inlineResizeCases
                    + " heightResizeCases=" + heightResizeCases
                    + "; real Forms on private desktop, all explanations visible, no collapse, transient settings, no product runtime");
            });
        }
    }
#endif
}
#endif
