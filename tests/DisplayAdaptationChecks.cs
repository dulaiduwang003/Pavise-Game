#if PAVISE_SELFTEST && PAVISE_DISPLAY_ADAPTATION_CHECKS
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PaviseApp
{
    // Synthetic monitor geometry, actual product Forms. No display-setting API,
    // product entry point, calibration start or game-mode activation is invoked.
    internal static class DisplayAdaptationChecks
    {
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        private static int checks, fixtures, transitions;
        private static Rectangle currentArea;
        private static int currentDpi;
        private static string context;
        private static readonly List<string> popupFailures = new List<string>();
        private sealed class Case
        {
            internal string Name; internal Rectangle Work; internal int Dpi;
            internal Case(string name, int x, int y, int w, int h, int dpi)
            { Name = name; Work = new Rectangle(x, y, w, h); Dpi = dpi; }
        }
        private static readonly Case[] Cases = {
            new Case("4k100",0,0,3840,2120,96), new Case("4k150",0,0,3840,2100,144), new Case("4k200",0,0,3840,2080,192),
            new Case("qhd100",0,0,2560,1400,96), new Case("qhd125",0,0,2560,1390,120), new Case("qhd150",0,0,2560,1380,144),
            new Case("fhd100",0,0,1920,1040,96), new Case("fhd125",0,0,1920,1030,120), new Case("fhd150",0,0,1920,1020,144),
            new Case("handheld800100",0,0,1280,760,96), new Case("handheld800125",0,0,1280,750,120),
            new Case("handheld800150",0,0,1280,740,144), new Case("handheld720100",0,0,1280,680,96),
            new Case("handheld720125",0,0,1280,670,120), new Case("negativeMonitor",-2560,40,2560,1360,144),
            new Case("leftTaskbar",64,0,1856,1040,96), new Case("thickTaskbar",0,0,1920,952,96),
            new Case("portrait200",0,0,720,1200,192)
        };
        private static void Check(bool ok, string detail)
        { checks++; if (!ok) throw new InvalidOperationException("FAIL: " + context + ": " + detail); }
        private static T Field<T>(PanelForm form, string name)
        { return (T)typeof(PanelForm).GetField(name, Hidden).GetValue(form); }
        private static object Call(object owner, string name, params object[] args)
        {
            Type type = owner is PanelForm ? typeof(PanelForm) : owner.GetType();
            return type.GetMethod(name, Hidden | BindingFlags.Public).Invoke(owner, args);
        }
        private static int Property(object owner, string name)
        { return (int)owner.GetType().GetProperty(name, Hidden).GetValue(owner, null); }
        private static void Pump() { Application.DoEvents(); }
        private static void GuardProviders(PanelForm form)
        {
            // RebuildUi creates new provider controls. Reapply observation fakes
            // before pumping queued handle events; do not change any geometry.
            foreach (var privacy in PerformanceWorkspaceLayout.Descendants(Field<Control>(form, "pagePolicy")).OfType<PrivacyOptionsPanel>()) {
                privacy.InspectionVisibleForTest = delegate { return false; };
                privacy.SetForTest = delegate { throw new InvalidOperationException("Display fixture cannot change privacy settings"); };
                privacy.RefreshForTest = delegate { throw new InvalidOperationException("Display fixture cannot inspect native privacy settings"); };
                privacy.StateForTest = delegate(PrivacyAction action, string target) { return PrivacyControl.State(action.Key, target); };
                privacy.SyncCached();
            }
        }
        private static void Fit(PanelForm form) { Call(form, "OnFitTick", null, EventArgs.Empty); GuardProviders(form); Pump(); }
        private static Rectangle Relative(Control owner, Control child)
        { return new Rectangle(owner.PointToClient(child.PointToScreen(Point.Empty)), child.Size); }
        private static void Contained(Control parent, Control child, string name)
        {
            Rectangle r = Relative(parent, child);
            Check(r.Width > 0 && r.Height > 0 && parent.ClientRectangle.Contains(r), name + " outside viewport: " + r + " in " + parent.ClientRectangle);
        }
        private static void ModelChecks()
        {
            foreach (Case item in Cases) {
                context = "model/" + item.Name;
                Rectangle bounds = WorkspaceLayout.InitialBounds(item.Work, item.Dpi);
                Check(item.Work.Contains(bounds), "startup rectangle exceeds work area");
                Check(bounds.Width < item.Work.Width && bounds.Height < item.Work.Height, "startup consumes the entire work area");
                Check(WorkspaceLayout.PreferredScale(item.Work, item.Dpi) >= item.Dpi / 96f, "scale shrinks below OS text scale");
                Rectangle extreme = new Rectangle(item.Work.Left - 10000, item.Work.Top - 5000, 9000, 7000);
                Check(item.Work.Contains(WorkspaceLayout.Clamp(extreme, item.Work, 12)), "clamp fails for oversize/negative coordinates");
            }
            Rectangle large = WorkspaceLayout.InitialBounds(Cases[0].Work, 96);
            Rectangle standard = WorkspaceLayout.InitialBounds(Cases[6].Work, 96);
            Check(large.Width > standard.Width * 1.5 && large.Height > standard.Height * 1.5,
                "4K at 100% still uses the small 1080p default");
        }
        private static void Chrome(PanelForm form)
        {
            Check(currentArea.Contains(form.Bounds), "native normal-window bounds exceed target work area: " + form.Bounds);
            Check(form.WindowState == FormWindowState.Normal, "startup/resize forced maximization");
            var root = Field<Control>(form, "root");
            Check(root.Bounds == form.ClientRectangle, "root does not use actual client viewport");
            var nav = Field<NavRail>(form, "nav"); Contained(root, nav, "floating navigation");
            Check(nav.Width > nav.Height * 2 && nav.Top > root.ClientSize.Height / 2,
                "navigation lost horizontal bottom Dock layout");
            Control top = root.Controls["WorkspaceTopBar"]; Check(top != null, "top bar missing"); Contained(root, top, "top bar");
            var targets = new[] { top.Controls["WindowClose"], top.Controls["WindowMaximize"], top.Controls["WindowMinimize"],
                Field<Control>(form, "modeButton"), Field<Control>(form, "themeSwitch") };
            foreach (Control target in targets) { Check(target != null, "chrome action missing"); Contained(top, target, "chrome " + target.Name); }
            for (int a = 0; a < targets.Length; a++) for (int b = a + 1; b < targets.Length; b++)
                Check(!targets[a].Bounds.IntersectsWith(targets[b].Bounds), "chrome actions overlap: " + targets[a].Name + "/" + targets[b].Name);
            Check(Dpi.Scale >= currentDpi / 96f, "product text shrank below the requested DPI");
            using (Graphics graphics = form.CreateGraphics()) {
                // Font.FromLogFont converts pixels to points using this desktop's
                // actual graphics DPI, independently of our synthetic monitor DPI.
                float body = Theme.UI(9f, false).SizeInPoints * graphics.DpiY / 72f;
                float status = Theme.UI(8.5f, false).SizeInPoints * graphics.DpiY / 72f;
                Check(body >= 11.9f, "body text fell below twelve physical pixels: " + body);
                Check(status >= 10.9f, "status text fell below eleven physical pixels: " + status);
            }
            int count = 0, invoked = -1;
            Action<int> savedInvoke = nav.ItemInvoked;
            nav.ItemInvoked = delegate(int item) { invoked = item; };
            try {
                Rectangle previous = Rectangle.Empty;
                for (int slot = 0; ; slot++) {
                    int item = nav.ItemAtSlot(slot); if (item < 0) break; count++;
                    // Exercise the real horizontal keyboard route without
                    // navigating pages or changing any product preference.
                    Call(nav, "OnKeyDown", new KeyEventArgs(Keys.Home));
                    for (int i = 0; i < slot; i++) Call(nav, "OnKeyDown", new KeyEventArgs(Keys.Right));
                    invoked = -1; Call(nav, "OnKeyDown", new KeyEventArgs(Keys.Enter));
                    Check(invoked == item, "right-arrow navigation invoked the wrong item: " + item);
                    if (slot > 0) {
                        Call(nav, "OnKeyDown", new KeyEventArgs(Keys.Left));
                        invoked = -1; Call(nav, "OnKeyDown", new KeyEventArgs(Keys.Space));
                        Check(invoked == nav.ItemAtSlot(slot - 1), "left-arrow navigation invoked the wrong item");
                    }
                    Rectangle bounds = (Rectangle)Call(nav, "ItemBounds", slot);
                    Check(bounds.Width >= Theme.S(44) && bounds.Height >= Theme.S(44),
                        "navigation target smaller than minimum hit size: " + bounds);
                    Check(nav.ClientRectangle.Contains(bounds), "navigation item unreachable: " + item + " at " + bounds);
                    Check(slot == 0 || previous.Right <= bounds.Left, "navigation hit targets overlap");
                    previous = bounds;
                    Point middle = new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
                    Check((int)Call(nav, "HitTestPoint", middle) == item, "painted navigation item does not match hit test");
                    AccessibleObject accessible = nav.AccessibilityObject.GetChild(slot);
                    Check(accessible != null && !String.IsNullOrEmpty(accessible.Name), "navigation item lost accessible name");
                    Check(nav.RectangleToClient(accessible.Bounds) == bounds, "navigation accessible bounds disagree with its hit target");
                }
            }
            finally { nav.ItemInvoked = savedInvoke; }
            Check(count == 7, "main navigation display entries lost or duplicated");
            var tuning = Field<NavRail>(form, "tuningNav");
            if (tuning.Visible) {
                Contained(root, tuning, "active tuning navigation");
                for (int slot = 0; slot < 4; slot++) {
                    int item = tuning.ItemAtSlot(slot); Check(item >= 0, "tuning navigation entry lost");
                    Call(tuning, "OnKeyDown", new KeyEventArgs(Keys.Home));
                    for (int i = 0; i < slot; i++) Call(tuning, "OnKeyDown", new KeyEventArgs(Keys.Right));
                    Rectangle bounds = (Rectangle)Call(tuning, "ItemBounds", slot);
                    Point middle = new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
                    Check(tuning.ClientRectangle.Contains(bounds) && (int)Call(tuning, "HitTestPoint", middle) == item,
                        "active tuning navigation cannot be reached: " + item);
                }
            }
        }
        private static void Show(PanelForm form, PageId page)
        { form.ShowPageForShot(page); Pump(); Call(form, "StopPageReveal"); }
        private static void ScrollBottom(ScrollableControl panel, Control target, string label)
        {
            panel.ScrollControlIntoView(target); Pump();
            panel.AutoScrollPosition = new Point(Math.Max(0, -panel.AutoScrollPosition.X), Int32.MaxValue); Pump();
            Rectangle r = Relative(panel, target);
            Check(r.Bottom > 0 && r.Bottom <= panel.ClientSize.Height, label + " bottom not reachable: " + r + " in " + panel.ClientRectangle);
            if (target.Height <= panel.ClientSize.Height) Check(r.Top >= 0, label + " fitting final item clipped at top");
        }
        private static void Overview(PanelForm form)
        {
            Show(form, PageId.Overview);
            DBPanel page = Field<DBPanel>(form, "pageOverview"); Contained(Field<Control>(form, "root"), page, "overview viewport");
            Check(!page.AutoScroll && !page.HorizontalScroll.Visible && !page.VerticalScroll.Visible,
                "overview open canvas has a stale scrollbar");
            string[] expected = { "overviewAnimatedCore", "overviewStatusDot", "overviewGameStatus",
                "overviewStatusDivider", "overviewGuardLabel", "overviewGuardToggle" };
            Check(page.Controls.Count == expected.Length, "overview regained extra actions or decorative cards");
            Control root = Field<Control>(form, "root"); NavRail nav = Field<NavRail>(form, "nav");
            Check(nav.Visible && Relative(root, page).Bottom <= nav.Top, "overview viewport overlaps floating navigation");
            foreach (string name in expected) {
                Control control = page.Controls[name];
                Check(control != null && control.Visible, "overview control missing: " + name);
                Contained(page, control, name);
                Check(!Relative(root, control).IntersectsWith(nav.Bounds), "floating navigation covers " + name);
            }
            var core = Field<PaviseCore>(form, "paviseCore");
            Check(core == page.Controls["overviewAnimatedCore"] && core.Immersive,
                "overview lost its original animated control or immersive presentation");
            var status = Field<Label>(form, "lblStatus");
            var toggle = Field<Toggle>(form, "swGame");
            Check(status == page.Controls["overviewGameStatus"] && !String.IsNullOrWhiteSpace(status.Text),
                "overview live game status is missing");
            Check(toggle == page.Controls["overviewGuardToggle"] && !String.IsNullOrWhiteSpace(toggle.AccessibleName),
                "overview guard toggle is missing its accessible name");
            Control[] controls = page.Controls.Cast<Control>().ToArray();
            for (int a = 0; a < controls.Length; a++) for (int b = a + 1; b < controls.Length; b++)
                Check(!controls[a].Bounds.IntersectsWith(controls[b].Bounds),
                    "overview controls overlap: " + controls[a].Name + "/" + controls[b].Name);
        }
        private static void Performance(PanelForm form)
        {
            Show(form, PageId.Policy);
            var categories = Field<PerformanceCategoryBar>(form, "performanceCategories");
            var panels = Field<DBPanel[]>(form, "performanceTabPanels");
            Check(panels.Length == 4, "performance categories lost");
            for (int i = 0; i < panels.Length; i++) {
                categories.Select(i, true); Pump(); var grid = (PerformanceFeatureGrid)panels[i];
                var outerViewport = grid.Parent as ScrollableControl;
                if (outerViewport != null && outerViewport.AutoScroll) { outerViewport.ScrollControlIntoView(grid); Pump(); }
                Contained(grid.Parent, grid, "performance category " + i); // before helper reflow
                Check(!grid.HorizontalScroll.Visible, "performance grid overflows horizontally before validation");
                PerformanceWorkspaceLayout.Context = context + "/category=" + i;
                PerformanceWorkspaceLayout.Validate(grid); PerformanceWorkspaceLayout.BottomReachable(grid);
            }
            DBPanel policy = Field<DBPanel>(form, "pagePolicy");
            Check(policy.Controls.Find("environmentStandbyCleaner", true).Length == 0
                && policy.Controls.Find("StandbyCleanerOptions", true).Length == 0, "old performance cleaner entry remains");
        }
        private static void SettingsPage(PanelForm form)
        {
            Show(form, PageId.Settings); Field<TechTabs>(form, "settingsTabs").Index = 0; Pump();
            var panels = Field<DBPanel[]>(form, "settingsTabPanels");
            Control toggle = Field<Control>(form, "swPolicyStandbyCleaner");
            Check(panels[0].Contains(toggle), "cleaner is not on the application-default settings tab");
            Check(form.Controls.Find("environmentStandbyCleaner", true).Length == 1, "cleaner toggle duplicated or missing");
            Check(form.Controls.Find("StandbyCleanerOptions", true).Length == 1, "cleaner options duplicated or missing");
            Contained(Field<Control>(form, "root"), Field<Control>(form, "pageSettings"), "settings viewport");
            Control last = panels[0].Controls.Cast<Control>().OrderBy(x => x.Bottom).Last();
            ScrollBottom(panels[0], last, "application settings");
            panels[0].ScrollControlIntoView(toggle.Parent.Parent); Pump();
            Rectangle r = Relative(panels[0], toggle); Check(r.Bottom > 0 && r.Top < panels[0].ClientSize.Height, "settings cleaner cannot be scrolled into view");
            foreach (Control action in new[] { toggle, form.Controls.Find("StandbyCleanerOptions", true)[0] }) {
                panels[0].ScrollControlIntoView(action); Pump();
                Contained(panels[0], action, "settings cleaner action " + action.Name);
            }
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect { internal int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        private struct NativeMinMax { internal Point Reserved, MaxSize, MaxPosition, MinTrack, MaxTrack; }
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wp, IntPtr lp);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
        private static void NativeConstraints(PanelForm form)
        {
            IntPtr p = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(NativeMinMax)));
            try {
                Marshal.StructureToPtr(new NativeMinMax(), p, false); SendMessage(form.Handle, 0x24, IntPtr.Zero, p);
                var mm = (NativeMinMax)Marshal.PtrToStructure(p, typeof(NativeMinMax));
                Point workSize = new Point(currentArea.Width, currentArea.Height);
                Check(mm.MaxSize == workSize && mm.MaxTrack == workSize, "native maximize/track sizes ignore work area");
                Check(mm.MinTrack.X > 0 && mm.MinTrack.X <= currentArea.Width && mm.MinTrack.Y > 0 && mm.MinTrack.Y <= currentArea.Height,
                    "native minimum tracking size cannot fit work area");
            } finally { Marshal.FreeHGlobal(p); }
        }
        private static void FrameworkGate(PanelForm form)
        {
            Type helper = typeof(Form).Assembly.GetType("System.Windows.Forms.DpiHelper", true);
            PropertyInfo property = helper.GetProperty("EnableDpiChangedMessageHandling", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            Check(property != null && (bool)property.GetValue(null, null), "actual WinForms DPI-message gate is disabled despite runtime config/manifest");
            Console.WriteLine("FRAMEWORK DeviceDpi=" + form.DeviceDpi + "; Forms=" + typeof(Form).Assembly.GetName().Version
                + "; config=" + AppDomain.CurrentDomain.SetupInformation.ConfigurationFile + "; DPI message gate=true");
        }
        private static void StateTransitions(PanelForm form, GameMode mode)
        {
            Show(form, PageId.Settings); Field<TechTabs>(form, "settingsTabs").Index = 2; Pump();
            Show(form, PageId.Policy); Field<PerformanceCategoryBar>(form, "performanceCategories").Select(3, true); Pump(); TraceSettings(form, "seed");
            var before = PolicyCatalog.All.ToDictionary(x => x.Key, x => PolicyResolver.RawGlobalValue(x.Key));
            PerformancePreset preset = mode.ActivePreset;
            var oldGrid = (PerformanceFeatureGrid)Field<DBPanel[]>(form, "performanceTabPanels")[3];
            oldGrid.AutoScrollPosition = new Point(0, Int32.MaxValue); Pump();
            Point savedScroll = oldGrid.AutoScrollPosition;
            int oldMaximum = oldGrid.VerticalScroll.Maximum, oldHeight = oldGrid.ClientSize.Height;
            Call(form, "RebuildUi"); GuardProviders(form); Pump(); Call(form, "StopPageReveal"); Pump();
            var rebuiltGrid = (PerformanceFeatureGrid)Field<DBPanel[]>(form, "performanceTabPanels")[3];
            TraceSettings(form, "same-viewport rebuild");
            Check(Math.Abs(rebuiltGrid.AutoScrollPosition.Y - savedScroll.Y) <= Theme.S(2), "same-viewport rebuild lost performance scroll position: before="
                + savedScroll + ", after=" + rebuiltGrid.AutoScrollPosition + ", beforeMax=" + oldMaximum + ", afterMax=" + rebuiltGrid.VerticalScroll.Maximum
                + ", beforeHeight=" + oldHeight + ", afterHeight=" + rebuiltGrid.ClientSize.Height);
            Rectangle original = form.Bounds; float scale = Dpi.Scale;
            form.Size = new Size(Math.Max(Theme.S(640), original.Width - Theme.S(120)), Math.Max(Theme.S(480), original.Height - Theme.S(60)));
            Fit(form); Check(Math.Abs(scale - Dpi.Scale) < .001, "ordinary window resize changed text scale");
            TraceSettings(form, "resize");
            Check(Field<TechTabs>(form, "performanceTabs").Index == 3, "resize lost selected performance category");
            form.Bounds = original; Fit(form); Chrome(form); TraceSettings(form, "restore size");
            currentArea = new Rectangle(-2560, 40, 2560, 1340);
            int messageDpi = form.DeviceDpi == 144 ? 192 : 144; currentDpi = messageDpi;
            Rectangle suggested = new Rectangle(-2480, 80, 1700, 1100);
            int dpiEvents = 0; bool cancelled = true;
            form.DpiChanged += delegate(object sender, DpiChangedEventArgs e) { dpiEvents++; cancelled &= e.Cancel; };
            IntPtr p = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(NativeRect)));
            try {
                Marshal.StructureToPtr(new NativeRect { Left = suggested.Left, Top = suggested.Top, Right = suggested.Right, Bottom = suggested.Bottom }, p, false);
                float beforeFont = form.Font.SizeInPoints;
                SendMessage(form.Handle, 0x2e0, new IntPtr((messageDpi << 16) | messageDpi), p);
                Check(dpiEvents == 1 && cancelled, "framework DPI event was skipped, duplicated, or automatic scaling was not cancelled");
                Check(Math.Abs(form.Font.SizeInPoints - beforeFont) <= .01f, "framework scaled the form font before custom rebuild");
                Fit(form);
                Check(form.DeviceDpi == messageDpi, "WinForms DeviceDpi stayed stale after native WM_DPICHANGED");
                Check(form.Bounds == suggested, "DPI suggested native bounds were rescaled a second time or clamped to host screen");
            } finally { Marshal.FreeHGlobal(p); }
            Chrome(form); NativeConstraints(form); transitions++; TraceSettings(form, "DPI increase");
            currentArea = new Rectangle(64, 0, 1216, 680); currentDpi = 96;
            p = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(NativeRect)));
            try {
                Rectangle whole = currentArea;
                Marshal.StructureToPtr(new NativeRect { Left = whole.Left, Top = whole.Top, Right = whole.Right, Bottom = whole.Bottom }, p, false);
                SendMessage(form.Handle, 0x2e0, new IntPtr((96 << 16) | 96), p); Fit(form);
                Check(dpiEvents == 2 && cancelled, "DPI reduction event was skipped, duplicated or not cancelled");
                Check(form.DeviceDpi == 96, "WinForms DeviceDpi stayed stale after DPI reduction");
                Check(form.Bounds == whole, "DPI whole-work-area suggestion was inset or resized again");
            } finally { Marshal.FreeHGlobal(p); }
            Chrome(form); transitions++; TraceSettings(form, "DPI decrease");
            SendMessage(form.Handle, 0x7e, IntPtr.Zero, IntPtr.Zero); Fit(form); Chrome(form); transitions++;
            currentArea = new Rectangle(64, 0, 1216, 620);
            SendMessage(form.Handle, 0x1a, IntPtr.Zero, IntPtr.Zero); Fit(form); Chrome(form); transitions++;
            Check(Field<TechTabs>(form, "performanceTabs").Index == 3, "display/work-area change lost performance category");
            Check(Field<Control>(form, "curPage") == Field<Control>(form, "pagePolicy"), "display/work-area change lost current page");
            Check(mode.ActivePreset == preset && !mode.Enabled && !form.UiActive, "display adaptation changed mode or activated workers");
            foreach (var item in before) Check(PolicyResolver.RawGlobalValue(item.Key) == item.Value, "display adaptation rewrote preference " + item.Key);
            var hiddenGrid = (PerformanceFeatureGrid)Field<DBPanel[]>(form, "performanceTabPanels")[3];
            hiddenGrid.AutoScrollPosition = new Point(0, Int32.MaxValue); Pump(); Point hiddenScroll = hiddenGrid.AutoScrollPosition;
            Check(hiddenScroll.Y < 0, "hidden performance restoration case never scrolled");
            Show(form, PageId.Settings); Check(Field<TechTabs>(form, "settingsTabs").Index == 2, "display adaptation lost background settings tab");
            for (int i = 0; i < 2; i++) { Call(form, "RebuildUi"); GuardProviders(form); Pump(); Call(form, "StopPageReveal"); }
            Show(form, PageId.Policy);
            Check(Field<TechTabs>(form, "performanceTabs").Index == 3, "two background rebuilds lost hidden performance category");
            var restoredGrid = (PerformanceFeatureGrid)Field<DBPanel[]>(form, "performanceTabPanels")[3];
            Check(Math.Abs(restoredGrid.AutoScrollPosition.Y - hiddenScroll.Y) <= Theme.S(2), "two background rebuilds lost hidden performance scroll");
            Performance(form); Overview(form); SettingsPage(form);
        }
        private static void TraceSettings(PanelForm form, string stage)
        {
            var saved = Field<System.Collections.IDictionary>(form, "pagePositions");
            object snapshot = saved[(int)PageId.Settings]; string tabs = "missing";
            if (snapshot != null) {
                var values = (Dictionary<string, int>)snapshot.GetType().GetField("tabs", Hidden).GetValue(snapshot);
                tabs = String.Join(";", values.Select(x => x.Key + "=" + x.Value));
            }
            Console.WriteLine("SETTINGS " + stage + ": live=" + Field<TechTabs>(form, "settingsTabs").Index + "; saved=" + tabs);
        }
        private static void OverviewResizeCycle(PanelForm form)
        {
            Rectangle original = form.Bounds; float scale = Dpi.Scale;
            foreach (int logicalWidth in new[] { 1200, 760, 1200 }) {
                form.Width = Theme.S(logicalWidth); Fit(form); Overview(form);
                Check(Math.Abs(Dpi.Scale - scale) < .001f, "wide/narrow/wide viewport cycle shrank text");
                Check(!Field<DBPanel>(form, "pageOverview").HorizontalScroll.Visible, "wide/narrow/wide left stale horizontal extent");
            }
            form.Bounds = original; Fit(form);
        }
        private static void StrategyPopupLayout(PanelForm owner, string output)
        {
            int savedLanguage = Lang.Cur;
            try {
                foreach (int language in new[] { 0, 1 }) {
                    Lang.Cur = language;
                    // Current manual-mode popup, with no preference callbacks.
                    // Opening, measuring and scrolling cannot apply a strategy.
                    using (var popup = new StrategyFlyout()) {
                        Control root = Field<Control>(owner, "root");
                        root.Controls.Add(popup);
                        popup.Power.SetItems(new[] { Lang.T("plan.pick.auto"), Lang.T("plan.pick.off") });
                        popup.FitViewport(new Size(Math.Max(1, root.Width - Theme.S(24)), Math.Max(1, root.Height - Theme.S(24))));
                        popup.Location = new Point((root.Width - popup.Width) / 2, Theme.S(12));
                        popup.Visible = true; popup.BringToFront(); Pump();
                        Contained(root, popup, "manual strategy popup");
                        Check(popup.Width <= currentArea.Width && popup.Height <= currentArea.Height, "manual strategy popup exceeds work area");
                        int titleWidth = TextRenderer.MeasureText(popup.AccessibleName, Theme.UI(12.5f, true), Size.Empty,
                            TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;
                        if (titleWidth > popup.Width - Theme.S(112)) popupFailures.Add("lang=" + language
                            + "; title=" + popup.AccessibleName + "; measuredWidth=" + titleWidth + "; available=" + (popup.Width - Theme.S(112)));
                        Console.WriteLine("POPUP lang=" + language + "; bounds=" + popup.Bounds + "; measuredTitleWidth=" + titleWidth);
                        foreach (Control surface in popup.Controls) {
                            popup.ScrollControlIntoView(surface); Pump();
                            Contained(popup, surface, "manual strategy action " + surface.Name);
                            Check(!String.IsNullOrWhiteSpace(surface.AccessibleName), "manual strategy action lost its label");
                        }
                        Control[] surfaces = popup.Controls.Cast<Control>().Where(x => x.Visible).ToArray();
                        for (int a = 0; a < surfaces.Length; a++) for (int b = a + 1; b < surfaces.Length; b++)
                            Check(!surfaces[a].Bounds.IntersectsWith(surfaces[b].Bounds), "manual strategy actions overlap: " + surfaces[a].Name + "/" + surfaces[b].Name);
                        Check(popup.Controls["StrategyScenePicker"] != null && popup.Controls["StrategyPowerPicker"] != null,
                            "manual strategy popup actions missing");
                        PerformanceWorkspaceLayout.Save(popup, Path.Combine(output, "portrait-strategy-popup-" + language + ".png"));
                        popup.Hide();
                    }
                }
            } finally { Lang.Cur = savedLanguage; }
        }
        private sealed class FixtureForm : PanelForm
        {
            internal FixtureForm(Tamer tamer, GameMode mode, Icon icon) : base(tamer, mode, icon, true) { }
            protected override bool ShowWithoutActivation { get { return true; } }
            protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ExStyle |= 0x08000000; return cp; } }
        }
        private static PanelForm CreateFixture(string folder, out GameMode mode)
        {
            Settings.UseTransientStoreForCurrentProcess(); Settings.Save(PolicyCatalog.KeyBoost, false);
            Logger.WritesEnabled = false; Directory.CreateDirectory(folder); Paths.Data = folder;
            PanelForm.SuppressUiWorkersForTest = true;
            CpuTopology.InjectTopologyForTest(255, new ulong[] { 3, 12, 48, 192 }, new ulong[] { 15, 240 }, 0, 0, 0, 0, false, false);
            var core = new SuppressionCore(); mode = new GameMode(folder, core); var tamer = new Tamer(core);
            using (var icon = IconArt.MakeIcon(24)) {
                var form = new FixtureForm(tamer, mode, icon);
                form.ShowInTaskbar = false; GuardProviders(form);
                return form;
            }
        }
        private static void Run(string output, Case item, int language, bool light, bool transitionsForCase)
        {
            context = item.Name + "/lang=" + language + "/light=" + light; Console.WriteLine("DISPLAY " + context);
            PrivacyControl.ResetForTest(); PrivacyControl.AdmissionAllowed = delegate { return false; };
            PrivacyControl.QueueForTest = delegate { throw new InvalidOperationException("Display fixture cannot queue privacy work"); };
            EnvironmentEnhancementEligibility.SampleForTest = delegate { return new EnvironmentEnhancementEligibility.MemorySnapshot {
                Installed = 32 * EnvironmentEnhancementEligibility.GiB, Total = 31 * EnvironmentEnhancementEligibility.GiB, Available = 8 * EnvironmentEnhancementEligibility.GiB }; };
            currentArea = item.Work; currentDpi = item.Dpi;
            PanelForm.WorkspaceAreaForTest = delegate { return currentArea; }; PanelForm.WorkspaceDpiForTest = delegate { return currentDpi; };
            Dpi.NoFit = false; Dpi.Scale = 1; Theme.DropFontCache(); Lang.Cur = language; Theme.SetLight(light);
            GameMode mode;
            using (PanelForm form = CreateFixture(Path.Combine(output, item.Name + "-" + language + "-" + light), out mode)) {
              try {
                Rectangle constructed = form.Bounds;
                Rectangle startup = WorkspaceLayout.InitialBounds(item.Work, item.Dpi);
                // Before its native handle exists, WinForms caps Form bounds at
                // the host desktop's tracking size even for a synthetic monitor.
                // The model stays exact; native-message assertions below exercise
                // full synthetic work-area bounds after the handle is created.
                Size hostTrack = SystemInformation.MaxWindowTrackSize;
                Rectangle hostCappedStartup = new Rectangle(startup.Location,
                    new Size(Math.Min(startup.Width, hostTrack.Width), Math.Min(startup.Height, hostTrack.Height)));
                Check(constructed == startup || constructed == hostCappedStartup, "factory changed startup bounds beyond the host tracking cap: actual="
                    + constructed + "; expected=" + startup + "; hostCapped=" + hostCappedStartup);
                GuardProviders(form); form.Show(); Pump(); Fit(form); FrameworkGate(form); Chrome(form); NativeConstraints(form);
                if (item.Name == "portrait200" && language == 0 && light) StrategyPopupLayout(form, output);
                Check(!form.UiActive && !mode.Enabled, "fixture activated product workers or game mode");
                Overview(form); Performance(form); SettingsPage(form);
                fixtures++;
                if (language == 0 && light && (item.Name == "4k100" || item.Name == "handheld720100" || item.Name == "portrait200")) {
                    Show(form, PageId.Overview); PerformanceWorkspaceLayout.Save(form, Path.Combine(output, item.Name + "-overview.png"));
                    Show(form, PageId.Settings); PerformanceWorkspaceLayout.Save(form, Path.Combine(output, item.Name + "-settings.png"));
                }
                // The OS may supply a whole-work-area viewport while the app
                // remains Normal (e.g. a gaming shell). This simulates geometry;
                // it is not a test of that shell or its physical hardware.
                Rectangle desktopBounds = form.Bounds; float textScale = Dpi.Scale;
                // Model an OS resize directly. Form.Bounds itself applies the
                // host's MaxWindowTrackSize before our synthetic monitor exists.
                Check(SetWindowPos(form.Handle, IntPtr.Zero, currentArea.X, currentArea.Y, currentArea.Width, currentArea.Height, 0x0014),
                    "synthetic OS resize failed: " + Marshal.GetLastWin32Error());
                Fit(form);
                Check(form.Bounds == currentArea && form.WindowState == FormWindowState.Normal,
                    "OS-sized normal viewport snapped back to the startup size: actual=" + form.Bounds + "; requested=" + currentArea);
                Check(Math.Abs(textScale - Dpi.Scale) < .001f, "OS-sized viewport changed the text scale");
                Chrome(form); Overview(form); Performance(form); SettingsPage(form);
                form.Bounds = desktopBounds; Fit(form);
                if (item.Name == "4k100") OverviewResizeCycle(form);
                if (transitionsForCase) StateTransitions(form, mode);
              } catch {
                foreach (var heading in PerformanceWorkspaceLayout.Descendants(Field<Control>(form, "pagePolicy")).OfType<PerformanceSectionHeading>()) {
                    int measuredWidth = TextRenderer.MeasureText(heading.ScopeText, Theme.UI(8f, false), Size.Empty,
                        TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;
                    if (measuredWidth > heading.ScopeBounds.Width)
                        Console.WriteLine("BADGE text=" + heading.ScopeText + "; bounds=" + heading.ScopeBounds + "; singleLineWidth=" + measuredWidth);
                }
                foreach (var card in PerformanceWorkspaceLayout.Cards(Field<Control>(form, "pagePolicy")))
                    if (card.Hosted != null && !card.ClientRectangle.Contains(card.Hosted.Bounds))
                        Console.WriteLine("CLIPPED title=" + card.Title + "; card=" + card.Bounds + "; host=" + card.Hosted.Bounds + "; scale=" + Dpi.Scale);
                PerformanceWorkspaceLayout.Save(form, Path.Combine(output, item.Name + "-failure.png"));
                throw;
              }
            }
        }
        [STAThread] private static int Main(string[] args)
        {
            Console.OutputEncoding = new System.Text.UTF8Encoding(false);
            if (args.Length == 1 && args[0] == "--negative-control") {
                context = "negative";
                try { Check(new Rectangle(0, 0, 1280, 680).Contains(new Rectangle(0, 0, 1600, 900)), "intentional oversized default window"); }
                catch (InvalidOperationException e) { Console.WriteLine(e.Message); return 3; }
                return 0;
            }
            return PerformanceWorkspaceLayout.PrivateDesktop(delegate {
                Settings.UseTransientStoreForCurrentProcess(); PanelForm.SuppressUiWorkersForTest = true;
                Logger.ResetWriteBarrierForTest(); Logger.WritesEnabled = false; Dpi.Init(); Lang.Init(); ModelChecks();
                bool single = args.Length > 1 && args[1] == "--single";
                foreach (Case item in Cases.Where(x => !single || x.Name == "4k100" || x.Name == "handheld720100" || x.Name == "portrait200")
                    .OrderBy(x => single && x.Name == "portrait200" ? -1 : 0))
                    foreach (int language in single && item.Name != "portrait200" ? new[] { 0 } : new[] { 0, 1 })
                        foreach (bool light in single && item.Name != "portrait200" ? new[] { true } : new[] { false, true })
                        Run(args[0], item, language, light, item.Name == "handheld720100" && language == 0 && light);
                Check(popupFailures.Count == 0, "manual strategy popup text clips: " + String.Join(" | ", popupFailures));
                Console.WriteLine("PASS display adaptation assertions=" + (checks + PerformanceWorkspaceLayout.Checks) + " fixtures=" + fixtures + " nativeTransitions=" + transitions
                    + "; synthetic workarea/DPI inputs, actual private-desktop product Forms, transient settings, workers suppressed; no physical monitor claim");
            });
        }
    }
}
#endif
