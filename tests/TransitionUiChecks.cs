#if PAVISE_NAV_BENCH && PAVISE_SELFTEST
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PaviseApp
{
    internal static class TransitionUiChecks
    {
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        private static int checks;

        private static void Check(bool value, string message)
        { if (!value) throw new Exception("Interaction: " + message); checks++; }

        private static T Field<T>(object host, string name)
        {
            for (Type type = host.GetType(); type != null; type = type.BaseType)
            {
                FieldInfo field = type.GetField(name, Hidden);
                if (field != null) return (T)field.GetValue(host);
            }
            throw new MissingFieldException(host.GetType().Name, name);
        }

        private static object Call(object host, string name, params object[] args)
        {
            for (Type type = host.GetType(); type != null; type = type.BaseType)
            {
                MethodInfo method = type.GetMethod(name, Hidden);
                if (method == null) continue;
                try { return method.Invoke(host, args); }
                catch (TargetInvocationException error) { throw new Exception(name, error.InnerException); }
            }
            throw new MissingMethodException(host.GetType().Name, name);
        }

        private static EventHandler FrameHandlers()
        {
            return (EventHandler)typeof(UiClock).GetField("Frame",
                BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        }

        private static int ListenerCount()
        {
            EventHandler handler = FrameHandlers();
            return handler == null ? 0 : handler.GetInvocationList().Length;
        }

        private static void Frame()
        {
            EventHandler handler = FrameHandlers();
            if (handler != null) handler(null, EventArgs.Empty);
        }

        private static void FinishFrames()
        { for (int i = 0; i < 45; i++) Frame(); UiClock.Running = false; }

        private static Bitmap Capture(Control control)
        {
            var bitmap = new Bitmap(control.Width, control.Height);
            control.DrawToBitmap(bitmap, control.ClientRectangle);
            return bitmap;
        }

        private static bool SamePicture(Bitmap a, Bitmap b)
        {
            if (a.Size != b.Size) return false;
            Rectangle rect = new Rectangle(Point.Empty, a.Size);
            BitmapData left = a.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                BitmapData right = b.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    byte[] l = new byte[a.Width * 4], r = new byte[b.Width * 4];
                    for (int y = 0; y < a.Height; y++)
                    {
                        System.Runtime.InteropServices.Marshal.Copy(new IntPtr(left.Scan0.ToInt64() + y * left.Stride), l, 0, l.Length);
                        System.Runtime.InteropServices.Marshal.Copy(new IntPtr(right.Scan0.ToInt64() + y * right.Stride), r, 0, r.Length);
                        for (int x = 0; x < l.Length; x++) if (l[x] != r[x]) return false;
                    }
                    return true;
                }
                finally { b.UnlockBits(right); }
            }
            finally { a.UnlockBits(left); }
        }

        public static void Run(string output)
        {
            Dpi.Scale = 1f; Theme.DropFontCache(); Theme.SetLight(false);
            UiClock.Suspended = false; UiClock.Frozen = false;
            int listeners = ListenerCount();
            try
            {
                using (var form = new OffscreenTestForm { ClientSize = new Size(860, 650) })
                {
                    form.Show();
                    // Verify the probe first, otherwise a broken probe silently accepts every snapshot
                    using (var probe = new Panel { Bounds = new Rectangle(0, 0, 20, 20) })
                    {
                        form.Controls.Add(probe);
                        using (var prints = new PrintWatch(probe))
                        using (var bitmap = Capture(probe))
                            Check(prints.Count > 0, "snapshot probe did not observe DrawToBitmap");
                    }
                    CheckCollapse(form);
                    CheckControls(form, output);
                    CheckTabLifecycle(form);
                    CheckNavLifecycle(form);
                    CheckPageRevealLifecycle();
                    CheckDialog();
                    FinishFrames();
                    form.Hide();
                }
                Check(ListenerCount() == listeners, "disposed interaction fixtures retained frame listeners");
                Console.WriteLine("PASS interaction assertions=" + checks
                    + " navigation=immediate sidebar_indicator=animated tab_indicator=animated page_reveal=native snapshots=none window_fade=retained");
            }
            finally { UiClock.Running = false; UiClock.Suspended = true; UiClock.Frozen = false; }
        }

        private static void CheckCollapse(Form form)
        {
            using (var panel = new Panel { Bounds = new Rectangle(0, 450, 500, 170) })
            using (var card = new SettingCard { Bounds = new Rectangle(0, 0, 440, 110),
                Title = "Collapse", Desc = "The content uses its final layout immediately", Collapsible = true,
                CollapsedHeight = 55, ExpandedHeight = 110 })
            {
                panel.Controls.Add(card); form.Controls.Add(panel);
                using (var prints = new PrintWatch(panel))
                {
                    int events = 0;
                    card.ExpandedChanged = delegate { events++; };
                    Point location = card.Location;
                    card.Expanded = false;
                    Check(!card.Expanded && card.Height == 55 && events == 1,
                        "collapse delayed its final height or callback");
                    card.Expanded = false;
                    for (int i = 0; i < 8; i++) Frame();
                    Check(card.Location == location && card.Height == 55 && events == 1,
                        "collapse moved, kept changing height, or repeated its callback");
                    card.Expanded = true;
                    Check(card.Expanded && card.Height == 110 && events == 2,
                        "expand delayed its final height or callback");
                    Check(panel.Controls.Count == 1 && prints.Count == 0,
                        "card expansion captured the parent or added an overlay");
                }
            }
        }

        private static void CheckImmediatePicture(Control control, Action select)
        {
            FinishFrames();
            Rectangle bounds = control.Bounds;
            int children = control.Controls.Count;
            using (var before = Capture(control))
            {
                select();
                using (var first = Capture(control))
                {
                    Check(!SamePicture(before, first), control.GetType().Name + " selection had no immediate visual response");
                    FinishFrames();
                    using (var last = Capture(control))
                        Check(SamePicture(first, last), control.GetType().Name + " selection waited for animation frames");
                }
            }
            Check(control.Bounds == bounds && control.Controls.Count == children,
                control.GetType().Name + " selection moved the control or added an overlay");
        }

        private static void MouseUp(Control control, Rectangle item)
        {
            Call(control, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1,
                item.Left + item.Width / 2, item.Top + item.Height / 2, 0));
        }

        private static void CheckTabSettled(TechTabs tabs, string message)
        {
            Motion marker = Field<Motion>(tabs, "selection");
            Check(marker.Value == tabs.Index && marker.Target == tabs.Index, message);
        }

        private static void FinishTabFrames(TechTabs tabs)
        {
            // Allow the slowest clock step in the supported range, do not tie the test to one specific easing speed
            for (int i = 0; i < 100; i++)
            {
                Motion marker = Field<Motion>(tabs, "selection");
                if (marker.Value == marker.Target) break;
                Frame();
            }
            UiClock.Running = false;
            CheckTabSettled(tabs, "tab indicator failed to settle at its selected tab");
        }

        private static void CheckTabAnimation(TechTabs tabs)
        {
            FinishFrames();
            Rectangle bounds = tabs.Bounds;
            int children = tabs.Controls.Count;
            Check(tabs.IsHandleCreated && tabs.Visible, "tab animation fixture is not visible");
            CheckTabSettled(tabs, "tab indicator was not initialized at its selected tab");
            using (var prints = new PrintWatch(tabs))
            using (var before = PaintSurface(tabs))
            {
                tabs.Index = 2;
                Motion start = Field<Motion>(tabs, "selection");
                Check(tabs.Index == 2 && start.Value == 0 && start.Target == 2 && UiClock.Running,
                    "tab selection did not activate immediately and wake its indicator animation");
                using (var first = PaintSurface(tabs))
                {
                    Check(!SamePicture(before, first), "selected tab text waited for the indicator animation");
                    Frame();
                    Motion middle = Field<Motion>(tabs, "selection");
                    Check(middle.Value > 0 && middle.Value < 2 && middle.Target == 2,
                        "tab indicator skipped its intermediate position");
                    using (var midPicture = PaintSurface(tabs))
                    {
                        Check(!SamePicture(first, midPicture), "tab indicator frames did not change the painted result");
                        FinishTabFrames(tabs);
                        using (var last = PaintSurface(tabs))
                        {
                            Check(!SamePicture(midPicture, last), "tab indicator never reached its final painted position");
                            FinishFrames();
                            using (var settled = PaintSurface(tabs))
                                Check(SamePicture(last, settled), "settled tab indicator kept changing its picture");
                        }
                    }
                }
                Check(prints.Count == 0, "tab indicator animation captured a control snapshot");
            }
            Check(tabs.Bounds == bounds && tabs.Controls.Count == children,
                "tab indicator animation moved the control or added an overlay");
        }

        private static int NavTargetY(NavRail nav, int item)
        {
            int slot = (int)Call(nav, "SlotOfItem", item);
            return (int)Call(nav, "SlotY", slot);
        }

        private static void CheckNavSettled(NavRail nav, string message)
        {
            Motion marker = Field<Motion>(nav, "indicator");
            int target = NavTargetY(nav, nav.Selected);
            Check(marker.Value == target && marker.Target == target, message);
        }

        private static void FinishNavFrames(NavRail nav)
        {
            for (int i = 0; i < 100; i++)
            {
                Motion marker = Field<Motion>(nav, "indicator");
                if (marker.Value == marker.Target) break;
                Frame();
            }
            UiClock.Running = false;
            CheckNavSettled(nav, "sidebar indicator failed to settle at its selected item");
        }

        private static void CheckNavAnimation(NavRail nav, int target, Action select, Action checkContent)
        {
            FinishFrames();
            Rectangle bounds = nav.Bounds;
            int children = nav.Controls.Count;
            Check(nav.IsHandleCreated && nav.Visible, "sidebar animation fixture is not visible");
            CheckNavSettled(nav, "sidebar indicator was not initialized at its selected item");
            int fromY = NavTargetY(nav, nav.Selected), toY = NavTargetY(nav, target);
            Check(fromY != toY, "sidebar animation fixture needs different displayed slots");
            using (var prints = new PrintWatch(nav))
            using (var before = PaintSurface(nav))
            {
                select();
                Motion start = Field<Motion>(nav, "indicator");
                Check(nav.Selected == target && start.Value == fromY && start.Target == toY && UiClock.Running,
                    "sidebar selection did not activate immediately and wake its indicator animation");
                if (checkContent != null) checkContent();
                using (var first = PaintSurface(nav))
                {
                    Check(!SamePicture(before, first), "selected sidebar text waited for the indicator animation");
                    Frame();
                    Motion middle = Field<Motion>(nav, "indicator");
                    Check(middle.Value > Math.Min(fromY, toY) && middle.Value < Math.Max(fromY, toY)
                        && middle.Target == toY, "sidebar indicator skipped its intermediate position");
                    if (checkContent != null) checkContent();
                    using (var midPicture = PaintSurface(nav))
                    {
                        Check(!SamePicture(first, midPicture), "sidebar animation frames did not change its painted marker");
                        FinishNavFrames(nav);
                        if (checkContent != null) checkContent();
                        using (var last = PaintSurface(nav))
                        {
                            Check(!SamePicture(midPicture, last), "sidebar indicator never reached its final painted position");
                            FinishFrames();
                            using (var settled = PaintSurface(nav))
                                Check(SamePicture(last, settled), "settled sidebar indicator kept changing its picture");
                        }
                    }
                }
                Check(prints.Count == 0, "sidebar indicator animation captured a control snapshot");
            }
            Check(nav.Bounds == bounds && nav.Controls.Count == children,
                "sidebar indicator animation moved the control or added an overlay");
        }

        private static void CheckTabLifecycle(Form form)
        {
            bool suspended = UiClock.Suspended, frozen = UiClock.Frozen;
            try
            {
                using (var host = new Panel { Bounds = new Rectangle(0, 0, 700, 75) })
                using (var tabs = new TechTabs { Bounds = new Rectangle(0, 0, 550, 40) })
                {
                    tabs.SetTabs(new[] { "A", "B", "C" }, null);
                    int events = 0;
                    tabs.IndexChanged = delegate(int index)
                    { events++; Check(tabs.Index == index, "tab lifecycle callback preceded its selected state"); };
                    tabs.Index = 2;
                    Check(!tabs.IsHandleCreated && tabs.Index == 2 && events == 1,
                        "detached tab selection created a handle or delayed activation");
                    CheckTabSettled(tabs, "tab without a handle retained an unfinished indicator");
                    host.Controls.Add(tabs); form.Controls.Add(host);
                    CheckTabSettled(tabs, "creating the tab handle started an obsolete transition");
                    int listeners = ListenerCount();

                    tabs.Index = 0; Frame();
                    float reversal = Field<Motion>(tabs, "selection").Value;
                    Check(reversal > 0 && reversal < 2, "reverse-switch fixture did not enter an animation");
                    tabs.Index = 2;
                    Motion resumed = Field<Motion>(tabs, "selection");
                    Check(resumed.Value == reversal && resumed.Target == 2,
                        "rapid tab reversal restarted from an old tab instead of the current indicator");
                    Frame();
                    resumed = Field<Motion>(tabs, "selection");
                    Check(resumed.Value > reversal && resumed.Value < 2,
                        "reversed tab indicator did not head toward the latest selection");
                    FinishTabFrames(tabs);

                    tabs.Index = 0; Frame(); UiClock.Frozen = true;
                    CheckTabSettled(tabs, "freezing left the tab indicator between tabs");
                    tabs.Index = 1;
                    CheckTabSettled(tabs, "frozen tab selection waited for animation frames");
                    Check(UiClock.Frozen && !UiClock.Running, "tab selection resumed the frozen clock");
                    UiClock.Frozen = false;

                    tabs.Index = 2; Frame(); UiClock.Suspended = true;
                    using (var picture = PaintSurface(tabs))
                        CheckTabSettled(tabs, "suspended tab paint left a stale indicator position");
                    tabs.Index = 0;
                    CheckTabSettled(tabs, "suspended tab selection waited for animation frames");
                    Check(UiClock.Suspended && !UiClock.Running, "tab selection resumed the suspended clock");
                    UiClock.Suspended = false;

                    tabs.Index = 2; Frame(); tabs.Hide();
                    CheckTabSettled(tabs, "hiding a tab left its indicator mid-transition");
                    tabs.Index = 1;
                    CheckTabSettled(tabs, "hidden tab selection retained an obsolete transition");
                    tabs.Show();
                    CheckTabSettled(tabs, "showing a tab replayed a hidden transition");
                    tabs.Index = 2; Frame(); host.Hide();
                    Check(!tabs.Visible, "parent-hide fixture left its tab visible");
                    // When the framework hides the parent it does not necessarily notify invisible children synchronously
                    Frame();
                    CheckTabSettled(tabs, "hidden tab indicator did not settle on its next frame");
                    tabs.Index = 0;
                    CheckTabSettled(tabs, "tab selection under a hidden parent retained an obsolete transition");
                    host.Show();
                    using (var picture = PaintSurface(tabs))
                        CheckTabSettled(tabs, "showing the parent painted a hidden tab transition");

                    tabs.Index = 2; Frame();
                    Check(Field<Motion>(tabs, "selection").Value < 2,
                        "parent-reopen fixture did not enter an indicator transition");
                    // No frames and no selection change while hidden, merely reopening must align the first frame
                    host.Hide(); host.Show();
                    using (var picture = PaintSurface(tabs))
                        CheckTabSettled(tabs, "rapid parent reopen replayed its interrupted tab transition");

                    tabs.Index = 0; tabs.SnapToSelection();
                    tabs.Index = 2; Frame(); tabs.Width += 30;
                    CheckTabSettled(tabs, "resizing a tab retained an obsolete indicator position");
                    tabs.Index = 0; Frame(); Call(tabs, "RecreateHandle");
                    CheckTabSettled(tabs, "recreating a handle replayed an obsolete tab transition");
                    Check(ListenerCount() == listeners, "recreating a tab handle duplicated its clock listener");

                    tabs.Index = 2; Frame(); tabs.SnapToSelection();
                    CheckTabSettled(tabs, "explicit tab synchronization did not settle its indicator");
                    PageViewPosition saved = PageViewPosition.Capture(host, Dpi.Scale);
                    tabs.Index = 0; tabs.SnapToSelection();
                    saved.Restore(host, Dpi.Scale);
                    Check(tabs.Index == 2, "restoring a page lost its selected tab");
                    CheckTabSettled(tabs, "restoring a page animated from an obsolete tab position");

                    tabs.Index = 1; Frame(); tabs.SetTabs(new[] { "A", "B" }, null);
                    Check(tabs.Index == 1, "replacing tab labels lost a valid selection");
                    CheckTabSettled(tabs, "replacing tab labels left an obsolete indicator transition");
                    tabs.SetTabs(new[] { "A" }, null);
                    Check(tabs.Index == 0, "shrinking tab labels left an out-of-range selection");
                    CheckTabSettled(tabs, "shrinking tab labels left the indicator outside the strip");
                    tabs.SetTabs(new string[0], null);
                    using (var picture = PaintSurface(tabs))
                        CheckTabSettled(tabs, "clearing tab labels left an obsolete indicator position");
                }
            }
            finally { UiClock.Suspended = suspended; UiClock.Frozen = frozen; }
        }

        private static void CheckNavLifecycle(Form form)
        {
            bool suspended = UiClock.Suspended, frozen = UiClock.Frozen;
            try
            {
                using (var host = new Panel { Bounds = new Rectangle(0, 0, 260, 650) })
                using (var nav = new NavRail(new[] { "A", "B", "C", "D", "E" },
                    new[] { "settings", "gpu", "chip", "settings", "gear" },
                    new[] { 2, 0, 3, 1, 4 }, new[] { 2 }, new[] { "Other" }, 2)
                    { Bounds = new Rectangle(0, 0, 230, 620), ShowBranding = false })
                {
                    int events = 0;
                    nav.SelectionChanged = delegate(int index)
                    { events++; Check(nav.Selected == index, "sidebar lifecycle callback preceded its selected state"); };
                    nav.Select(4);
                    Check(!nav.IsHandleCreated && nav.Selected == 4 && events == 1,
                        "detached sidebar selection created a handle or delayed activation");
                    CheckNavSettled(nav, "sidebar without a handle retained an unfinished indicator");
                    host.Controls.Add(nav); form.Controls.Add(host);
                    CheckNavSettled(nav, "creating the sidebar handle started an obsolete transition");
                    int listeners = ListenerCount();

                    CheckNavAnimation(nav, 3, delegate { nav.Select(3); }, null);
                    Check(Field<Motion>(nav, "indicator").Target == Dpi.S(137) + 2 * (Dpi.S(47) + Dpi.S(13)) + Dpi.S(42),
                        "reordered sidebar indicator ignored its displayed slot or group separator");
                    nav.Select(4); Frame();
                    Motion moving = Field<Motion>(nav, "indicator");
                    Check(moving.Value > NavTargetY(nav, 3) && moving.Value < moving.Target,
                        "sidebar reversal fixture did not enter an animation");
                    int beforeEvents = events;
                    nav.SelectSilently(4);
                    Motion duplicate = Field<Motion>(nav, "indicator");
                    Check(duplicate.Value == moving.Value && duplicate.Target == moving.Target && events == beforeEvents,
                        "duplicate silent sidebar synchronization snapped its indicator or fired a callback");
                    nav.Select(-1); nav.Select(99); nav.SelectSilently(-1); nav.SelectSilently(99);
                    Check(nav.Selected == 4 && events == beforeEvents
                        && Field<Motion>(nav, "indicator").Value == moving.Value,
                        "invalid sidebar selection changed the state, callback, or animation");
                    nav.Select(3);
                    Motion reversed = Field<Motion>(nav, "indicator");
                    Check(reversed.Value == moving.Value && reversed.Target == NavTargetY(nav, 3),
                        "rapid sidebar reversal restarted from an old item instead of the current marker");
                    Frame();
                    reversed = Field<Motion>(nav, "indicator");
                    Check(reversed.Value < moving.Value && reversed.Value > reversed.Target,
                        "reversed sidebar indicator did not head toward the latest selection");
                    beforeEvents = events;
                    nav.Select(3);
                    Check(events == beforeEvents + 1 && Field<Motion>(nav, "indicator").Value == reversed.Value,
                        "repeated public sidebar selection changed its callback contract or reset the animation");
                    FinishNavFrames(nav);

                    nav.Select(4); Frame();
                    int bottomY = NavTargetY(nav, 4);
                    nav.Height += 20;
                    CheckNavSettled(nav, "resizing a sidebar left its bottom indicator at obsolete coordinates");
                    Check(Field<Motion>(nav, "indicator").Target == bottomY + 20
                        && bottomY == nav.Height - 20 - Dpi.S(24) - Dpi.S(47),
                        "resized sidebar indicator lost its bottom anchor");
                    nav.Select(1);
                    Check(Field<Motion>(nav, "indicator").Target
                        == nav.Height - Dpi.S(24) - Dpi.S(47) - (Dpi.S(47) + Dpi.S(13)),
                        "sidebar indicator targeted the wrong item in its bottom-anchored group");
                    FinishNavFrames(nav);

                    nav.Select(0); Frame(); UiClock.Frozen = true;
                    CheckNavSettled(nav, "freezing left the sidebar indicator between items");
                    nav.Select(2);
                    CheckNavSettled(nav, "frozen sidebar selection waited for animation frames");
                    Check(UiClock.Frozen && !UiClock.Running, "sidebar selection resumed the frozen clock");
                    UiClock.Frozen = false;

                    nav.Select(3); Frame(); UiClock.Suspended = true;
                    using (var picture = PaintSurface(nav))
                        CheckNavSettled(nav, "suspended sidebar paint left a stale indicator position");
                    nav.Select(0);
                    CheckNavSettled(nav, "suspended sidebar selection waited for animation frames");
                    Check(UiClock.Suspended && !UiClock.Running, "sidebar selection resumed the suspended clock");
                    UiClock.Suspended = false;

                    nav.Select(4); Frame(); nav.Hide();
                    CheckNavSettled(nav, "hiding a sidebar left its indicator mid-transition");
                    nav.Select(3);
                    CheckNavSettled(nav, "hidden sidebar selection retained an obsolete transition");
                    nav.Show();
                    CheckNavSettled(nav, "showing a sidebar replayed a hidden transition");
                    nav.Select(2); Frame(); host.Hide();
                    Check(!nav.Visible, "parent-hide fixture left its sidebar visible");
                    Frame();
                    CheckNavSettled(nav, "hidden sidebar indicator did not settle on its next frame");
                    nav.Select(0);
                    CheckNavSettled(nav, "sidebar selection under a hidden parent retained an obsolete transition");
                    host.Show();
                    using (var picture = PaintSurface(nav))
                        CheckNavSettled(nav, "showing the parent painted a hidden sidebar transition");
                    nav.Select(4); Frame(); host.Hide(); host.Show();
                    using (var picture = PaintSurface(nav))
                        CheckNavSettled(nav, "rapid parent reopen replayed its interrupted sidebar transition");

                    nav.Select(0); Frame(); Call(nav, "RecreateHandle");
                    CheckNavSettled(nav, "recreating a handle replayed an obsolete sidebar transition");
                    Check(ListenerCount() == listeners, "recreating a sidebar handle duplicated its clock listener");
                    nav.Select(3); Frame(); nav.SnapToSelection();
                    CheckNavSettled(nav, "explicit sidebar synchronization did not settle its indicator");
                }
            }
            finally { UiClock.Suspended = suspended; UiClock.Frozen = frozen; }
        }

        private static void CheckControls(Form form, string output)
        {
            using (var tabs = new TechTabs { Bounds = new Rectangle(0, 0, 550, 40) })
            using (var tiers = new TierPicker { Bounds = new Rectangle(0, 50, 550, 40) })
            using (var modes = new ModeStrip { Bounds = new Rectangle(0, 100, 550, 40) })
            using (var toggle = new Toggle { Bounds = new Rectangle(0, 150, 100, 30) })
            using (var theme = new ThemeSwitch(false) { Bounds = new Rectangle(130, 150, 100, 44) })
            using (var nav = new NavRail(new[] { "A", "B", "C", "D" },
                new[] { "settings", "gpu", "chip", "settings" }) {
                Bounds = new Rectangle(600, 0, 230, 430), ShowBranding = false })
            {
                tabs.SetTabs(new[] { "A", "B", "C" }, null);
                foreach (Control c in new Control[] { tabs, tiers, modes, toggle, theme, nav })
                    form.Controls.Add(c);

                int tabEvents = 0, navEvents = 0, tierEvents = 0, modeEvents = 0, toggleEvents = 0, themeEvents = 0;
                tabs.IndexChanged = delegate(int index)
                { tabEvents++; Check(tabs.Index == index, "tab callback preceded its selected state"); };
                nav.SelectionChanged = delegate(int index)
                { navEvents++; Check(nav.Selected == index, "navigation callback preceded its selected state"); };
                tiers.IndexChanged = delegate(int index)
                { tierEvents++; Check(tiers.Index == index, "tier callback preceded its selected state"); };
                modes.IndexChanged = delegate(int index)
                { modeEvents++; Check(modes.Index == index, "mode callback preceded its selected state"); };
                toggle.CheckedChanged += delegate
                { toggleEvents++; Check(toggle.Checked, "toggle callback preceded its selected state"); };
                theme.Toggled = delegate(bool light)
                { themeEvents++; Check(!light, "theme click did not toggle the current state"); };

                CheckTabAnimation(tabs);
                tabs.Index = 2; tabs.Index = -1; tabs.Index = 99;
                Check(tabs.Index == 2 && tabEvents == 1, "duplicate or invalid tab selection fired a callback");
                CheckNavAnimation(nav, 3, delegate { nav.Select(3); }, null);
                Check(navEvents == 1, "navigation selection callback was lost");
                CheckImmediatePicture(tiers, delegate { tiers.Index = 0; });
                Check(tierEvents == 0, "programmatic tier synchronization fired a user callback");
                MouseUp(tiers, (Rectangle)Call(tiers, "SegmentRect", 2));
                Check(tiers.Index == 2 && tierEvents == 1, "tier click did not activate synchronously");
                CheckImmediatePicture(modes, delegate { modes.Index = 2; });
                Check(modeEvents == 0, "programmatic mode synchronization fired a user callback");
                MouseUp(modes, (Rectangle)Call(modes, "SegmentRect", 0));
                Check(modes.Index == 0 && modeEvents == 1, "mode click did not activate synchronously");

                Rectangle toggleBounds = toggle.Bounds;
                toggle.Checked = true;
                using (var first = Capture(toggle))
                {
                    int diameter = Dpi.S(22) - Dpi.S(3) * 2;
                    int x = toggle.Width - 1 - Dpi.S(3) - diameter / 2;
                    Color knob = first.GetPixel(x, toggle.Height / 2);
                    Check(knob.R > 245 && knob.G > 245 && knob.B > 245,
                        "toggle's active knob waited for the track color animation");
                }
                toggle.Checked = true;
                Check(toggleEvents == 1 && toggle.Bounds == toggleBounds, "toggle repeated its callback or moved");
                CheckImmediatePicture(theme, delegate { theme.SetSilently(true); });
                Check(themeEvents == 0, "silent theme synchronization fired a user callback");
                Call(theme, "OnClick", EventArgs.Empty);
                Check(themeEvents == 1, "theme click lost its callback");
                FinishFrames();
                using (var picture = Capture(form))
                    picture.Save(Path.Combine(output, "interaction-controls.png"), ImageFormat.Png);
            }
        }

        private static void PumpUntil(Func<bool> ready, string failure)
        {
            var watch = Stopwatch.StartNew();
            while (!ready() && watch.ElapsedMilliseconds < 1000)
            {
                Application.DoEvents();
                System.Threading.Thread.Sleep(1);
            }
            Check(ready(), failure);
        }

        private static object TrackedDialogFade(Form dialog)
        {
            // Grabbing the real entry object is only to assert whether the timer was released, the public entry is checked below
            Type entry = typeof(Fx).GetNestedType("FormEntry", BindingFlags.NonPublic);
            return Activator.CreateInstance(entry, new object[] { dialog });
        }

        private static void CheckDialog()
        {
            int listeners = ListenerCount();
            UiClock.Suspended = true;
            using (var dialog = new OffscreenTestForm())
            {
                dialog.Show();
                Rectangle bounds = dialog.Bounds;
                Fx.EnterForm(dialog);
                Check(dialog.Opacity == 0 && UiClock.Suspended && ListenerCount() == listeners,
                    "dialog entry altered the main-window clock or its listeners");
                PumpUntil(delegate { return dialog.Opacity > 0; }, "dialog fade did not produce a visible frame");
                Check(dialog.Bounds == bounds && dialog.Opacity < 1, "dialog failed to fade in place");

                // Opening the main window while the dialog is fading out must not suspend its clock afterwards
                UiClock.Suspended = false;
                PumpUntil(delegate { return dialog.Opacity == 1; }, "dialog fade did not finish");
                Check(!dialog.AllowTransparency && !UiClock.Suspended && ListenerCount() == listeners,
                    "completed dialog fade changed the resumed main-window clock or retained layered style");

                object hiddenEntry = TrackedDialogFade(dialog);
                var hiddenTimer = Field<System.Windows.Forms.Timer>(hiddenEntry, "timer");
                bool hiddenReleased = false;
                hiddenTimer.Disposed += delegate { hiddenReleased = true; };
                dialog.Hide();
                Check(hiddenReleased && Field<bool>(hiddenEntry, "done") && dialog.Opacity == 1
                    && !dialog.AllowTransparency && !UiClock.Suspended,
                    "hidden dialog retained its fade timer or altered the main-window clock");
                dialog.Show();

                UiClock.Frozen = true; Fx.EnterForm(dialog);
                Check(dialog.Opacity == 1 && !dialog.AllowTransparency && ListenerCount() == listeners,
                    "frozen dialog became transparent without a running animation");
                UiClock.Frozen = false;
                object frozenEntry = TrackedDialogFade(dialog);
                bool frozenReleased = false;
                Field<System.Windows.Forms.Timer>(frozenEntry, "timer").Disposed += delegate { frozenReleased = true; };
                UiClock.Frozen = true;
                PumpUntil(delegate { return Field<bool>(frozenEntry, "done"); }, "active dialog fade did not settle on freeze");
                Check(frozenReleased && dialog.Opacity == 1 && !dialog.AllowTransparency && !UiClock.Suspended,
                    "freezing retained a dialog timer or changed the main-window clock");
                UiClock.Frozen = false;

                object disposedEntry = TrackedDialogFade(dialog);
                bool disposedReleased = false;
                Field<System.Windows.Forms.Timer>(disposedEntry, "timer").Disposed += delegate { disposedReleased = true; };
                dialog.Dispose();
                Check(disposedReleased && Field<bool>(disposedEntry, "done") && !UiClock.Suspended
                    && ListenerCount() == listeners, "disposed dialog retained its timer or changed the main-window clock");
            }
        }

        private static int FrameListenersFor(object target)
        {
            EventHandler frame = FrameHandlers();
            int count = 0;
            if (frame != null)
                foreach (Delegate handler in frame.GetInvocationList())
                    if (ReferenceEquals(handler.Target, target)) count++;
            return count;
        }

        private static byte NativeRevealAlpha(PageReveal reveal)
        {
            uint color, flags;
            byte alpha;
            Check(GetLayeredWindowAttributes(reveal.Handle, out color, out alpha, out flags) && flags == 2,
                "page reveal never initialized native alpha, or enabled color-key transparency");
            Check(alpha == reveal.Alpha, "page reveal's managed alpha disagrees with its native window");
            return alpha;
        }

        private static void CheckRevealStopped(PageReveal reveal, string reason)
        {
            Check(!reveal.Visible && !reveal.Fading && reveal.Surface == null && FrameListenersFor(reveal) == 0,
                reason + " left a visible page veil, target reference, or frame listener");
        }

        private static void BeginReveal(PageReveal reveal, WorkspacePanel page)
        {
            Check(reveal.Prepare(page), "visible offscreen page could not prepare its background reveal");
            page.Visible = true;
            reveal.Reveal();
            Check(reveal.Visible && reveal.Fading && ReferenceEquals(reveal.Surface, page)
                && NativeRevealAlpha(reveal) == 255 && FrameListenersFor(reveal) == 1,
                "page reveal did not start from a painted opaque background with one frame listener");
        }

        private static void CheckRevealBackground(PageReveal reveal, WorkspacePanel page, string reason)
        {
            Rectangle full = page.Parent.RectangleToScreen(page.Bounds);
            var source = new Rectangle(reveal.Left - full.Left, reveal.Top - full.Top,
                reveal.Width, reveal.Height);
            // Paint the complete source standalone first, then crop, redoing the mask's translation here
            // would only cover up bugs in the translated GDI text, clip and background coordinates
            using (Bitmap complete = PaintSurface(page))
            using (Bitmap expected = complete.Clone(source, PixelFormat.Format32bppArgb))
            using (Bitmap actual = PaintSurface(reveal))
                Check(SamePicture(expected, actual), reason + " did not match the actual page background");
        }

        private static void CheckPageRevealLifecycle()
        {
            int listeners = ListenerCount();
            bool suspended = UiClock.Suspended, frozen = UiClock.Frozen;
            UiClock.Suspended = false; UiClock.Frozen = false;
            try
            {
                using (var form = new OffscreenTestForm { ClientSize = new Size(400, 260) })
                using (var page = new WorkspacePanel { Bounds = new Rectangle(-18, -11, 430, 300), Visible = false })
                using (var next = new WorkspacePanel { Bounds = new Rectangle(10, 10, 370, 240), Visible = false })
                using (var label = new Label { Text = "Live content", Bounds = new Rectangle(32, 35, 170, 28) })
                // A system-themed TextBox border asks the parent for WM_PRINTCLIENT to paint its own background
                // Use the app's own borderless search box style so the snapshot probe is unambiguous
                using (var text = new TextBox { Text = "Native child", Bounds = new Rectangle(32, 75, 170, 28),
                    BorderStyle = BorderStyle.None, BackColor = Theme.Inset, ForeColor = Theme.Fg })
                using (var reveal = new PageReveal(form))
                {
                    page.Controls.AddRange(new Control[] { label, text });
                    form.Controls.AddRange(new Control[] { page, next });
                    form.Show();
                    int pageStyle = GetWindowLong(page.Handle, -20), textStyle = GetWindowLong(text.Handle, -20);
                    int backgroundPaints = 0;
                    reveal.Paint += delegate { backgroundPaints++; };
                    IntPtr active = GetActiveWindow();
                    Rectangle pageBounds = page.Bounds;
                    using (var prints = new PrintTree(page))
                    {
                        Check(reveal.Prepare(page) && !page.Visible, "preparing a reveal activated its page too early");
                        Check(backgroundPaints > 0 && NativeRevealAlpha(reveal) == 255 && !reveal.Fading,
                            "opaque page veil skipped its first native paint or started timing before page readiness");
                        int style = GetWindowLong(reveal.Handle, -20);
                        Check((style & 0x080800A0) == 0x080800A0 && (style & 8) == 0
                            && (GetWindowLong(reveal.Handle, -16) & 0x40000000) == 0,
                            "page veil is not a nonactivating, input-transparent, non-topmost layered top-level window");
                        Check(!reveal.ShowInTaskbar && ReferenceEquals(reveal.Owner, form)
                            && GetActiveWindow() == active && !reveal.ContainsFocus,
                            "page veil changed activation, taskbar presence, or window ownership");
                        Rectangle expected = Rectangle.Intersect(page.Parent.RectangleToScreen(page.Bounds),
                            form.RectangleToScreen(form.ClientRectangle));
                        Check(reveal.Bounds == expected && expected.Size != page.Size,
                            "page veil escaped its owner's client bounds or missed the clipping fixture");
                        CheckRevealBackground(reveal, page, "clipped reveal");
                        page.Visible = true;
                        int paintsBeforeReveal = prints.Paints;
                        reveal.Reveal();
                        // A GDI child fully off-screen has an empty visible region and skips the managed Paint event entirely
                        // so the window is not shown here, only observe whether native painting gets dispatched
                        Check(reveal.Fading && FrameListenersFor(reveal) == 1
                            && prints.Paints > paintsBeforeReveal && prints.AllPainted,
                            "page reveal started without requesting a native paint for the entire child tree");
                        PumpUntil(delegate { return reveal.Alpha < 255 || !reveal.Fading; },
                            "page reveal did not progress its native alpha");
                        Check(reveal.Fading && NativeRevealAlpha(reveal) > 0 && reveal.Alpha < 255,
                            "page reveal skipped the intermediate native-alpha frame");
                        PumpUntil(delegate { return !reveal.Fading; }, "page reveal did not complete");
                        CheckRevealStopped(reveal, "completed reveal");
                        Check(page.Visible && page.Bounds == pageBounds && page.Controls.Count == 2
                            && GetWindowLong(page.Handle, -20) == pageStyle
                            && GetWindowLong(text.Handle, -20) == textStyle && form.Opacity == 1,
                            "page reveal changed the live page geometry, child styles, or owner opacity");
                        Check(prints.Count == 0, "page preparation or native-alpha frames captured control snapshots");
                    }

                    IntPtr reusable = reveal.Handle;
                    form.ClientSize = new Size(400, 289);
                    Check(reveal.Prepare(page), "visible-footer clipping fixture could not prepare its reveal");
                    CheckRevealBackground(reveal, page, "clipped reveal with visible footer");
                    reveal.Cancel();
                    form.ClientSize = new Size(400, 260);

                    Check(reveal.Prepare(page), "paint-cancellation fixture could not prepare its reveal");
                    using (var paint = new PrintWatch(page))
                    {
                        paint.PaintReceived = delegate { reveal.Cancel(); };
                        reveal.Reveal();
                        Check(paint.Paints > 0, "paint-cancellation fixture never received a native paint");
                    }
                    CheckRevealStopped(reveal, "cancellation during synchronous page painting");

                    BeginReveal(reveal, page);
                    BeginReveal(reveal, next); page.Visible = false;
                    BeginReveal(reveal, page); next.Visible = false;
                    Check(reveal.Handle == reusable && ReferenceEquals(reveal.Surface, page)
                        && FrameListenersFor(reveal) == 1,
                        "rapid page changes created replacement windows or retained an obsolete target/listener");
                    reveal.Cancel();

                    Action[] cancel = {
                        delegate { page.Hide(); }, delegate { page.Left++; }, delegate { page.Height--; },
                        delegate { form.Left++; }, delegate { form.Height--; },
                        delegate { Call(form, "OnDeactivate", EventArgs.Empty); }, delegate { form.Hide(); }
                    };
                    string[] reasons = { "page hide", "page move", "page resize", "owner move",
                        "owner resize", "owner deactivation", "owner hide" };
                    for (int i = 0; i < cancel.Length; i++)
                    {
                        form.Show(); page.Show();
                        BeginReveal(reveal, page);
                        cancel[i]();
                        CheckRevealStopped(reveal, reasons[i]);
                    }
                    Check(!reveal.Prepare(page), "hidden owner allowed a floating reveal window");
                    form.Show(); page.Show();

                    BeginReveal(reveal, page);
                    UiClock.Frozen = true;
                    CheckRevealStopped(reveal, "clock freeze");
                    Check(!reveal.Prepare(page), "frozen clock allowed a stranded reveal window");
                    UiClock.Frozen = false;
                    BeginReveal(reveal, page);
                    UiClock.Suspended = true; Frame();
                    CheckRevealStopped(reveal, "clock suspension");
                    Check(!reveal.Prepare(page) && UiClock.Suspended && !UiClock.Running,
                        "suspended reveal resumed the global clock or left a floating window");
                    UiClock.Suspended = false;

                    BeginReveal(reveal, page);
                    page.Dispose();
                    CheckRevealStopped(reveal, "target disposal");
                    BeginReveal(reveal, next);
                    reveal.Dispose();
                    CheckRevealStopped(reveal, "veil disposal");
                    form.Left++; // Disposed veils must not remain subscribed to their old owner
                }
                Check(ListenerCount() == listeners, "page reveal lifecycle retained global frame listeners");
            }
            finally { UiClock.Running = false; UiClock.Suspended = suspended; UiClock.Frozen = frozen; }
        }

        private static void CheckPageRevealWindow(PanelForm form)
        {
            Call(form, "StopPageReveal");
            form.SelectPageForTest((int)PageId.Overview);
            Call(form, "StopPageReveal");
            var pages = Field<DBPanel[]>(form, "pages");
            PageReveal reusable = null;
            using (var cover = new MemoryBackdrop())
            {
                foreach (int image in new[] { -1, 0 })
                {
                    cover.Use(image);
                    PageId id = image < 0 ? PageId.Whitelist : PageId.Overview;
                    var page = (WorkspacePanel)pages[(int)id];
                    Rectangle bounds = page.Bounds;
                    using (var prints = new PrintTree(page))
                    {
                        form.SelectPageForTest((int)id);
                        PageReveal reveal = Field<PageReveal>(form, "pageReveal");
                        Check(reveal != null && reveal.Fading && reveal.Visible
                            && ReferenceEquals(reveal.Surface, page) && ReferenceEquals(Field<DBPanel>(form, "curPage"), page)
                            && page.Visible && page.Bounds == bounds,
                            "real navigation did not activate its page and reveal its background independently");
                        Check(NativeRevealAlpha(reveal) == 255 && (GetWindowLong(page.Handle, -20) & 0x80000) == 0
                            && form.Opacity == 1 && !form.AllowTransparency,
                            "real page reveal failed native initialization or layered the page/main window");
                        if (reusable == null) reusable = reveal;
                        else Check(ReferenceEquals(reusable, reveal), "real page changes did not reuse the background veil");
                        CheckRevealBackground(reveal, page, image < 0 ? "plain real-page reveal" : "covered real-page reveal");
                        Check(!Backdrop.AppliesTo(reveal) && !(bool)Call(form, "AnyDialogOpen"),
                            "background veil inherited popup cover scope or was mistaken for a real dialog");
                        if (image < 0)
                        {
                            PumpUntil(delegate { return reveal.Alpha < 255 || !reveal.Fading; },
                                "real page reveal did not produce an intermediate frame");
                            byte alpha = NativeRevealAlpha(reveal);
                            Check(reveal.Fading && alpha > 0 && alpha < 255, "real page reveal skipped its fade frames");
                            form.SelectPageForTest((int)id);
                            Check(ReferenceEquals(Field<PageReveal>(form, "pageReveal"), reveal) && reveal.Fading
                                && reveal.Alpha == alpha && FrameListenersFor(reveal) == 1,
                                "clicking the active page restarted its reveal or duplicated a frame listener");
                        }
                        Check(prints.Count == 0, "real page reveal captured the page or one of its children");
                    }
                }
                form.SelectPageForTest((int)PageId.Whitelist);
                form.SelectPageForTest((int)PageId.Overview);
                Check(ReferenceEquals(reusable, Field<PageReveal>(form, "pageReveal"))
                    && ReferenceEquals(reusable.Surface, pages[(int)PageId.Overview])
                    && FrameListenersFor(reusable) == 1,
                    "rapid real-page navigation retained an old surface or multiple reveal listeners");
                try
                {
                    EnableWindow(form.Handle, false);
                    Check(!IsWindowEnabled(form.Handle), "native-disable fixture did not disable its owner");
                    CheckRevealStopped(reusable, "native owner disable");
                }
                finally { EnableWindow(form.Handle, true); }
            }

            foreach (string flyout in new[] { "SetModeFlyout", "SetSearchFlyout" })
            {
                form.SelectPageForTest((int)PageId.Whitelist);
                PageReveal reveal = Field<PageReveal>(form, "pageReveal");
                Check(reveal.Fading, "flyout fixture did not begin a page reveal");
                Call(form, flyout, true);
                CheckRevealStopped(reveal, "opening " + flyout);
                Call(form, flyout, false);
                form.SelectPageForTest((int)PageId.Overview);
            }
            PageReveal beforeExit = Field<PageReveal>(form, "pageReveal");
            Call(form, "BeginOutro");
            CheckRevealStopped(beforeExit, "window exit");
            Call(form, "CancelOutro");
            form.SelectPageForTest((int)PageId.Whitelist);
            PageReveal beforeRebuild = Field<PageReveal>(form, "pageReveal");
            Check(beforeRebuild.Fading, "rebuild fixture did not begin a page reveal");
            Call(form, "RebuildUi");
            Check(beforeRebuild.IsDisposed && Field<PageReveal>(form, "pageReveal") == null
                && FrameListenersFor(beforeRebuild) == 0,
                "UI rebuild retained the old page veil or its global listener");
            UiClock.Suspended = false; UiClock.Frozen = false;
        }

        private static void CheckSidebarNavigation(PanelForm form, string name, PageId initial, PageId target)
        {
            form.SelectPageForTest((int)initial);
            var nav = Field<NavRail>(form, name);
            nav.SnapToSelection();
            var pages = Field<DBPanel[]>(form, "pages");
            DBPanel outgoing = pages[(int)initial], incoming = pages[(int)target];
            Rectangle window = form.Bounds;
            int children = form.Controls.Count;
            var bounds = new Rectangle[pages.Length];
            for (int i = 0; i < pages.Length; i++) bounds[i] = pages[i].Bounds;
            using (var windowPrints = new PrintWatch(form))
            using (var outgoingPrints = new PrintWatch(outgoing))
            using (var incomingPrints = new PrintWatch(incoming))
            {
                CheckNavAnimation(nav, (int)target, delegate { nav.InvokeItem((int)target); }, delegate
                {
                    Check(ReferenceEquals(Field<DBPanel>(form, "curPage"), incoming) && incoming.Visible
                        && nav.Selected == (int)target && Field<NavRail>(form, "nav").Selected == (int)target,
                        name + " waited for its indicator before activating the requested page");
                    for (int i = 0; i < pages.Length; i++)
                        Check(pages[i].Visible == (i == (int)target) && pages[i].Bounds == bounds[i],
                            name + " moved a page or deferred its content visibility during indicator frames");
                    Check(form.Bounds == window && form.Controls.Count == children,
                        name + " moved the window or added a navigation snapshot layer");
                });
                Check(windowPrints.Count == 0 && outgoingPrints.Count == 0 && incomingPrints.Count == 0,
                    name + " captured a window or page snapshot while changing its indicator");
            }
        }

        public static void CheckWindow(PanelForm form)
        {
            bool suspended = UiClock.Suspended, frozen = UiClock.Frozen;
            UiClock.Suspended = false; UiClock.Frozen = false;
            try
            {
                Rectangle window = form.Bounds;
                int children = form.Controls.Count;
                CheckSidebarNavigation(form, "nav", PageId.Overview, PageId.Whitelist);
                CheckSidebarNavigation(form, "tuningNav", PageId.Policy, PageId.Environment);
                using (var prints = new PrintWatch(form))
                {
                    foreach (PageId id in new[] { PageId.Graphics, PageId.Policy, PageId.CoreScheduling, PageId.Environment,
                        PageId.Graphics, PageId.Policy, PageId.CoreScheduling, PageId.Environment })
                    {
                        form.SelectPageForTest((int)id);
                        var page = Field<DBPanel>(form, "curPage");
                        Check(ReferenceEquals(page, Field<DBPanel[]>(form, "pages")[(int)id]) && page.Visible
                            && Field<NavRail>(form, "nav").Selected == (int)id,
                            "navigation delayed activation or displayed the previous page");
                        Check(page.Left == Field<int>(form, "pageBaseLeft") && form.Controls.Count == children,
                            "navigation moved content or added an overlay");
                    }
                    Check(prints.Count == 0, "navigation synchronously captured a window bitmap");
                }

                CheckTabs(form, PageId.Graphics, "gfxTabs", "gfxTabPanels");
                CheckTabs(form, PageId.Policy, "policyTabs", "policyTabPanels");
                CheckTabs(form, PageId.Environment, "envTabs", "envTabPanels");

                // Visible is inherited from the parent, clearing a hidden page must still update the local flag
                Field<PillButton>(form, "btnAuditQuick").Visible = true;
                Field<PillButton>(form, "btnAuditPrecise").Visible = true;
                Field<PillButton>(form, "btnAuditFixAll").Visible = true;
                Call(form, "ShowAuditIdle");
                form.SelectPageForTest((int)PageId.Audit);
                Check(!Field<PillButton>(form, "btnAuditQuick").Visible
                    && !Field<PillButton>(form, "btnAuditPrecise").Visible
                    && !Field<PillButton>(form, "btnAuditFixAll").Visible
                    && Field<Control>(form, "auditScan").Visible
                    && !Field<Control>(form, "auditScroll").Visible,
                    "idle audit page restored stale toolbar or results visibility");

                CheckFlyouts(form);
                CheckBackdrop(form);
                CheckPageRevealWindow(form);
                Call(form, "BeginIntro"); Call(form, "StartIntro"); Frame();
                Check(form.Bounds == window && form.Opacity > 0 && form.Opacity < 1,
                    "main window entry moved or failed to fade");
                // DeltaScale may be at its supported minimum after native message pumping.
                // Advance until settled, allowing enough frames for the slowest clock step.
                for (int i = 0; i < 120 && Field<bool>(form, "introActive"); i++) Frame();
                UiClock.Running = false;
                Check(!Field<bool>(form, "introActive") && form.Opacity == 1 && !form.AllowTransparency,
                    "main window intro left an unfinished layered window");
                Call(form, "BeginOutro"); Call(form, "OnOutroTick", null, EventArgs.Empty);
                Check(form.Bounds == window && form.Opacity > 0 && form.Opacity < 1,
                    "main window exit moved or failed to fade");
                Call(form, "CancelOutro");
                Check(form.Visible && form.Bounds == window && form.Opacity == 1 && !form.AllowTransparency
                    && !Field<System.Windows.Forms.Timer>(form, "outroTimer").Enabled,
                    "interrupted exit did not restore the window and stop its timer");

                Call(form, "BeginIntro"); Call(form, "StartIntro"); Frame(); Call(form, "BeginOutro");
                Check(!Field<bool>(form, "introActive") && Field<bool>(form, "outroActive"),
                    "closing during entry left competing window animations");
                System.Threading.Thread.Sleep(240);
                Call(form, "OnOutroTick", null, EventArgs.Empty);
                Check(!form.Visible && !Field<bool>(form, "outroActive") && form.Opacity == 1
                    && !form.AllowTransparency && !Field<System.Windows.Forms.Timer>(form, "outroTimer").Enabled,
                    "completed exit failed to hide or clean up its timer and layered style");
                form.Show();
                Check(form.Bounds == window, "reopening after a fade changed the window position");
                FinishFrames();
            }
            finally { UiClock.Suspended = suspended; UiClock.Frozen = frozen; }
        }

        private static void CheckTabs(PanelForm form, PageId pageId, string tabsName, string panelsName)
        {
            form.SelectPageForTest((int)pageId);
            var page = Field<DBPanel>(form, "curPage");
            var tabs = Field<TechTabs>(form, tabsName);
            var panels = Field<DBPanel[]>(form, panelsName);
            int children = page.Controls.Count;
            tabs.Index = 0;
            tabs.SnapToSelection();
            panels[0].AutoScrollPosition = new Point(0, Theme.S(150));
            Point savedScroll = panels[0].AutoScrollPosition;
            var bounds = new Rectangle[panels.Length];
            for (int i = 0; i < panels.Length; i++) bounds[i] = panels[i].Bounds;
            double totalMs = 0, maxMs = 0;
            int switches = 0;
            using (var prints = new PrintWatch(page))
            using (var tabPrints = new PrintWatch(tabs))
            {
                int target = panels.Length - 1;
                MouseUp(tabs, (Rectangle)Call(tabs, "TabRect", target));
                Motion start = Field<Motion>(tabs, "selection");
                Check(tabs.Index == target && start.Value == 0 && start.Target == target,
                    tabsName + " did not separate immediate activation from its indicator transition");
                for (int i = 0; i < panels.Length; i++)
                    Check(panels[i].Visible == (i == target) && panels[i].Bounds == bounds[i],
                        tabsName + " waited for the indicator before activating its content");
                Frame();
                Motion middle = Field<Motion>(tabs, "selection");
                Check(middle.Value > 0 && middle.Value < target,
                    tabsName + " did not animate its selected indicator on a real page");
                FinishTabFrames(tabs);
                for (int i = 0; i < panels.Length; i++)
                    Check(panels[i].Visible == (i == target) && panels[i].Bounds == bounds[i],
                        tabsName + " changed content geometry or activation during indicator frames");
                MouseUp(tabs, (Rectangle)Call(tabs, "TabRect", 0));
                FinishTabFrames(tabs);

                // No animation frames and no message pumping between the two clicks, every intermediate state must be computed on the spot
                for (int pass = 0; pass < 3; pass++)
                for (int index = panels.Length - 1; index >= 0; index--)
                {
                    var watch = Stopwatch.StartNew();
                    MouseUp(tabs, (Rectangle)Call(tabs, "TabRect", index));
                    double elapsed = watch.Elapsed.TotalMilliseconds;
                    totalMs += elapsed; maxMs = Math.Max(maxMs, elapsed); switches++;
                    Check(tabs.Index == index, tabsName + " did not activate before returning from the click");
                    for (int i = 0; i < panels.Length; i++)
                        Check(panels[i].Visible == (i == index) && panels[i].Bounds == bounds[i],
                            tabsName + " deferred visibility or moved a tab panel");
                    Check(page.Controls.Count == children, tabsName + " added a transition overlay");
                }
                Check(prints.Count == 0 && tabPrints.Count == 0,
                    tabsName + " captured content or indicator snapshots during a transition");
                Check(panels[0].AutoScrollPosition == savedScroll, tabsName + " rapid switching lost scroll position");
            }
            Console.WriteLine("MEASURE tab=" + tabsName + " switches=" + switches
                + " total_ms=" + totalMs.ToString("F2", CultureInfo.InvariantCulture)
                + " max_ms=" + maxMs.ToString("F2", CultureInfo.InvariantCulture));
        }

        private static void CheckFlyouts(PanelForm form)
        {
            var search = Field<Control>(form, "searchFlyout");
            var modes = Field<Control>(form, "modeFlyout");
            Rectangle bounds = search.Bounds;
            int children = search.Parent.Controls.Count;
            using (var prints = new PrintWatch(form))
            using (var parentPrints = new PrintWatch(search.Parent))
            {
                Call(form, "SetModeFlyout", true);
                Check(modes.Visible, "mode flyout delayed opening");
                Call(form, "SetSearchFlyout", true);
                Check(search.Visible && !modes.Visible, "search did not open and close the other flyout immediately");
                for (int i = 0; i < 3; i++)
                {
                    Call(form, "SetSearchFlyout", false);
                    Check(!search.Visible, "search flyout delayed closing");
                    Call(form, "SetSearchFlyout", true);
                    Check(search.Visible && search.Bounds == bounds, "search flyout moved or delayed reopening");
                }
                Call(form, "SetSearchFlyout", false);
                Check(search.Parent.Controls.Count == children && prints.Count == 0 && parentPrints.Count == 0,
                    "flyout interactions captured a bitmap or added an overlay");
            }
        }

        private static T BackdropField<T>(string name)
        {
            return (T)typeof(Backdrop).GetField(name, BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        }

        // Runs the real managed paint path without showing a real modal window or moving focus
        private static Bitmap PaintSurface(Control control)
        {
            var bitmap = new Bitmap(control.Width, control.Height);
            using (Graphics graphics = Graphics.FromImage(bitmap))
            using (var paint = new PaintEventArgs(graphics, control.ClientRectangle))
            {
                graphics.Clear(Color.Magenta);
                Call(control, "OnPaintBackground", paint);
                Call(control, "OnPaint", paint);
            }
            return bitmap;
        }

        private static Bitmap PaintList(TechListBox list)
        {
            // Runs the native buffered row composition and the WM_ERASEBKGND finishing implementation end to end
            var bitmap = new Bitmap(list.ClientSize.Width, list.ClientSize.Height);
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Magenta);
                IntPtr hdc = graphics.GetHdc();
                try { Call(list, "FillTail", hdc); }
                finally { graphics.ReleaseHdc(hdc); }
                Call(list, "OnDrawItem", new DrawItemEventArgs(graphics, list.Font,
                    new Rectangle(0, 0, list.ClientSize.Width, list.ItemHeight), 0,
                    DrawItemState.None, list.ForeColor, list.BackColor));
            }
            return bitmap;
        }

        private static PaviseDialog MakeBackdropDialog(Control extra)
        {
            var dialog = (PaviseDialog)Activator.CreateInstance(typeof(PaviseDialog),
                BindingFlags.Instance | BindingFlags.NonPublic, null,
                new object[] { "Backdrop isolation", "Popup controls keep their own opaque surfaces.",
                    DlgKind.Info, "OK", "Cancel", extra, 468 }, CultureInfo.InvariantCulture);
            dialog.StartPosition = FormStartPosition.Manual;
            dialog.Location = new Point(-20000, -20000);
            return dialog;
        }

        private static void ApplyBackdropLabels(Control control)
        {
            typeof(PanelForm).GetMethod("ApplyBackdropLabels", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { control });
        }

        private static void CheckBackdrop(PanelForm form)
        {
            bool frozen = UiClock.Frozen;
            UiClock.Frozen = true;
            try
            {
                using (var cover = new MemoryBackdrop())
                using (var mainCard = new RoundPanel { Bounds = new Rectangle(20, 20, 430, 260) })
                using (var excluded = new RoundPanel { Bounds = new Rectangle(8, 85, 380, 150), UseBackdrop = false })
                using (var mainLabel = new Label { Text = "Main cover", BackColor = Theme.Card,
                    Bounds = new Rectangle(8, 8, 160, 25) })
                using (var excludedLabel = new Label { Text = "Popup label", BackColor = Theme.Card,
                    Bounds = new Rectangle(8, 8, 160, 25) })
                using (var movable = new DBPanel { Bounds = new Rectangle(180, 8, 90, 60), BackColor = Theme.Card })
                using (var extra = new RoundPanel { Size = new Size(Theme.S(390), Theme.S(155)) })
                using (var list = new TechListBox { Bounds = new Rectangle(12, 12, 210, 100),
                    DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 30, IntegralHeight = false,
                    BorderStyle = BorderStyle.None, BackColor = Theme.Card, ForeColor = Theme.Fg })
                using (var dialog = MakeBackdropDialog(extra))
                using (var powerRow = new PowerChoiceRow(null, "Offline fixture", null, false))
                {
                    form.Controls.Add(mainCard);
                    mainCard.Controls.AddRange(new Control[] { mainLabel, movable, excluded });
                    excluded.Controls.Add(excludedLabel);
                    extra.Controls.Add(list);
                    dialog.Owner = form; // Ownership is not ancestry and must not inherit the cover from its owner
                    list.Items.Add("One row with an empty tail");
                    int rowsDrawn = 0;
                    list.DrawItem += delegate(object sender, DrawItemEventArgs e)
                    {
                        rowsDrawn++;
                        using (var ink = new SolidBrush(Theme.Accent))
                            e.Graphics.FillRectangle(ink, 6, 6, 8, 8);
                    };

                    var modes = Field<ModePickerPanel>(form, "modeFlyout");
                    var power = Field<PowerFlyout>(form, "powerFlyout");
                    var search = Field<SearchFlyout>(form, "searchFlyout");
                    powerRow.SetBounds(Theme.S(14), Theme.S(66), Theme.S(368), Theme.S(44));
                    power.Controls.Add(powerRow); // Do not enumerate or change real power plans
                    var modeChoice = Field<ModeChoice[]>(modes, "choices")[0];
                    var modeLabel = Field<Label>(modes, "source");
                    var powerLabel = Field<Label>(power, "title");
                    var ok = (PillButton)dialog.Controls[1];
                    var searchList = Field<ListBox>(search, "list");

                    cover.Use(0);
                    ApplyBackdropLabels(mainCard);
                    ApplyBackdropLabels(modes);
                    ApplyBackdropLabels(power);
                    ApplyBackdropLabels(search);
                    ApplyBackdropLabels(extra);
                    Check(mainLabel.BackColor == Color.Transparent && excludedLabel.BackColor == Theme.Card
                        && modeLabel.BackColor == Theme.Card && powerLabel.BackColor == Theme.Card,
                        "cover label refresh crossed an excluded popup subtree");
                    Check(Backdrop.AppliesTo(form) && Backdrop.AppliesTo(mainCard) && Backdrop.AppliesTo(movable)
                        && !Backdrop.AppliesTo(dialog) && !Backdrop.AppliesTo(list) && !Backdrop.AppliesTo(searchList),
                        "cover scope confused the main form, its popup children, or an owned dialog");
                    Check(!dialog.Visible && dialog.Opacity == 1 && !dialog.AllowTransparency,
                        "drawing the modal fixture showed it or changed its whole-window opacity");

                    Control[] surfaces = { modes, modeChoice, power, powerRow, search, dialog, extra, ok, excluded };
                    var plain = new Bitmap[surfaces.Length];
                    Bitmap mainFirst = null;
                    cover.Use(-1);
                    try
                    {
                        using (Bitmap plainMain = PaintSurface(mainCard))
                        using (Bitmap plainList = PaintList(list))
                        {
                            Check((plainList.GetPixel(200, 15).ToArgb() & 0xFFFFFF) == (Theme.Card.ToArgb() & 0xFFFFFF)
                                && (plainList.GetPixel(200, 90).ToArgb() & 0xFFFFFF) == (Theme.Card.ToArgb() & 0xFFFFFF),
                                "list fixture did not actually paint its row and empty tail");
                            for (int i = 0; i < surfaces.Length; i++) plain[i] = PaintSurface(surfaces[i]);
                            for (int image = 0; image < 2; image++)
                            {
                                cover.Use(image);
                                using (Bitmap main = PaintSurface(mainCard))
                                {
                                    Check(!SamePicture(plainMain, main), "main content lost its custom cover");
                                    if (image == 0) mainFirst = (Bitmap)main.Clone();
                                    else Check(!SamePicture(mainFirst, main), "switching the main cover kept stale image content");
                                }
                                Bitmap mainCache = BackdropField<Bitmap>("fitted");
                                Check(mainCache != null && BackdropField<Size>("fittedFor") == form.ClientSize,
                                    "main cover fixture failed to establish a fitted cache");
                                for (int i = 0; i < surfaces.Length; i++)
                                {
                                    Control surface = surfaces[i];
                                    using (Bitmap picture = PaintSurface(surface))
                                        Check(SamePicture(plain[i], picture),
                                            surface.GetType().Name + " picked up cover pixels or lost its opaque background");
                                    Check(!Backdrop.AppliesTo(surface)
                                        && Backdrop.CardFill(surface, Theme.Card) == Theme.Card
                                        && Backdrop.NavFill(surface, Theme.Bg) == Theme.Bg,
                                        surface.GetType().Name + " inherited a translucent cover fill");
                                }
                                using (Bitmap picture = PaintList(list))
                                    Check(SamePicture(plainList, picture), "owned dialog list row or empty tail inherited the cover");
                                using (var direct = new Bitmap(32, 24))
                                using (Graphics graphics = Graphics.FromImage(direct))
                                {
                                    graphics.Clear(Color.Magenta);
                                    Backdrop.Paint(graphics, extra, new Rectangle(0, 0, 32, 24));
                                    Backdrop.PaintOnCard(graphics, ok, new Rectangle(0, 0, 32, 24));
                                    Check(direct.GetPixel(12, 12).ToArgb() == Color.Magenta.ToArgb(),
                                        "cover painting APIs painted into an excluded dialog without a caller guard");
                                }
                                Check(ReferenceEquals(mainCache, BackdropField<Bitmap>("fitted"))
                                    && BackdropField<Size>("fittedFor") == form.ClientSize,
                                    "popup painting replaced the main-window cover cache with a dialog-sized image");
                            }
                            Check(rowsDrawn == 3, "cover checks bypassed the list's actual owner-draw callback");
                            CheckBackdropReparent(mainCard, excluded, extra, movable);
                            search.Controls.Add(list);
                            using (Bitmap picture = PaintList(list))
                                Check(SamePicture(plainList, picture), "same-window search popup list inherited the cover");
                            cover.Use(-1);
                            using (Bitmap cleared = PaintSurface(mainCard))
                                Check(SamePicture(plainMain, cleared), "disabling the cover left painted image fragments");
                        }
                    }
                    finally
                    {
                        if (mainFirst != null) mainFirst.Dispose();
                        foreach (Bitmap picture in plain) if (picture != null) picture.Dispose();
                    }
                    Console.WriteLine("PASS backdrop light=" + Theme.LightMode
                        + " images=memory popup_surfaces=opaque main_cover=retained dialog_shown=false");
                }
            }
            finally { UiClock.Frozen = frozen; }
        }

        private static void CheckBackdropReparent(RoundPanel main, RoundPanel excluded, RoundPanel dialog, DBPanel child)
        {
            Rectangle bounds = child.Bounds;
            using (Bitmap covered = PaintSurface(child))
            {
                excluded.Controls.Add(child);
                Check(!Backdrop.AppliesTo(child), "reparenting into a popup kept cached main-window cover scope");
                using (Bitmap local = PaintSurface(child))
                {
                    Check(!SamePicture(covered, local), "excluded child kept cover pixels after reparenting");
                    dialog.Controls.Add(child);
                    using (Bitmap owned = PaintSurface(child))
                        Check(!Backdrop.AppliesTo(child) && SamePicture(local, owned),
                            "reparenting into an owned dialog changed its opaque content");
                }
                main.Controls.Add(child);
                child.Bounds = bounds;
                using (Bitmap restored = PaintSurface(child))
                    Check(Backdrop.AppliesTo(child) && SamePicture(covered, restored),
                        "moving a control back to the main page failed to restore its cover scope");
                excluded.UseBackdrop = true;
                Check(Backdrop.AppliesTo(excluded.Controls[0]), "re-enabling a panel failed to restore descendant cover scope");
                excluded.UseBackdrop = false;
                Check(!Backdrop.AppliesTo(excluded.Controls[0]), "disabling a panel left its descendants cover-enabled");
            }
        }

        // Rendering regression never calls Init, Choose, Clear or the Dim setter that persists to disk
        private sealed class MemoryBackdrop : IDisposable
        {
            private readonly string[] names = { "source", "fitted", "fittedFor", "fittedDim", "fittedLight", "dim" };
            private readonly object[] saved;
            private readonly Bitmap[] images = { Pattern(Color.Crimson, Color.Gold), Pattern(Color.DodgerBlue, Color.Lime) };

            public MemoryBackdrop()
            {
                saved = new object[names.Length];
                for (int i = 0; i < names.Length; i++) saved[i] = Field(names[i]).GetValue(null);
                Field("fitted").SetValue(null, null); // Detach never dispose the cache owned by the caller
                Field("dim").SetValue(null, 0);
                Use(-1);
            }

            private static FieldInfo Field(string name)
            { return typeof(Backdrop).GetField(name, BindingFlags.Static | BindingFlags.NonPublic); }

            public void Use(int image)
            {
                Bitmap fitted = BackdropField<Bitmap>("fitted");
                if (fitted != null) fitted.Dispose();
                Field("fitted").SetValue(null, null);
                Field("fittedFor").SetValue(null, Size.Empty);
                Field("fittedDim").SetValue(null, -1);
                Field("source").SetValue(null, image < 0 ? null : images[image]);
            }

            private static Bitmap Pattern(Color first, Color second)
            {
                var bitmap = new Bitmap(48, 48);
                using (Graphics graphics = Graphics.FromImage(bitmap))
                using (var brush = new SolidBrush(second))
                {
                    graphics.Clear(first);
                    for (int y = 0; y < 48; y += 8)
                        for (int x = 0; x < 48; x += 8)
                            if ((x / 8 + y / 8) % 2 == 0) graphics.FillRectangle(brush, x, y, 8, 8);
                }
                return bitmap;
            }

            public void Dispose()
            {
                Use(-1);
                for (int i = 0; i < names.Length; i++) Field(names[i]).SetValue(null, saved[i]);
                foreach (Bitmap image in images) image.Dispose();
            }
        }

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr window, int index);
        [DllImport("user32.dll")]
        private static extern bool GetLayeredWindowAttributes(IntPtr window, out uint color, out byte alpha, out uint flags);
        [DllImport("user32.dll")]
        private static extern IntPtr GetActiveWindow();
        [DllImport("user32.dll")]
        private static extern bool EnableWindow(IntPtr window, bool enabled);
        [DllImport("user32.dll")]
        private static extern bool IsWindowEnabled(IntPtr window);

        private sealed class PrintTree : IDisposable
        {
            private readonly System.Collections.Generic.List<PrintWatch> windows
                = new System.Collections.Generic.List<PrintWatch>();

            public PrintTree(Control root) { Add(root); }
            private void Add(Control control)
            {
                windows.Add(new PrintWatch(control));
                foreach (Control child in control.Controls) Add(child);
            }
            public int Count
            {
                get { int count = 0; foreach (PrintWatch window in windows) count += window.Count; return count; }
            }
            public int Paints
            {
                get { int count = 0; foreach (PrintWatch window in windows) count += window.Paints; return count; }
            }
            public bool AllPainted
            {
                get { foreach (PrintWatch window in windows) if (window.Paints == 0) return false; return true; }
            }
            public void Dispose() { foreach (PrintWatch window in windows) window.Dispose(); windows.Clear(); }
        }

        // DrawToBitmap sends WM_PRINT and WM_PRINTCLIENT, observe that work, do not add timing-dependent constraints
        private sealed class PrintWatch : NativeWindow, IDisposable
        {
            public int Count;
            public int Paints;
            public Action PaintReceived;
            public PrintWatch(Control control) { AssignHandle(control.Handle); }
            protected override void WndProc(ref Message message)
            {
                if (message.Msg == 0x0317 || message.Msg == 0x0318) Count++;
                if (message.Msg == 0x000F)
                {
                    Paints++;
                    if (PaintReceived != null) PaintReceived();
                }
                base.WndProc(ref message);
            }
            public void Dispose() { if (Handle != IntPtr.Zero) ReleaseHandle(); }
        }
    }
}
#endif
