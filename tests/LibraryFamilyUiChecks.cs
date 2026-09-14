// Isolated UI regression entry: builds controls only, no GameMode, no app startup, no user config read/write
#if PAVISE_UI_TEST
using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace PaviseApp
{
    internal static class LibraryFamilyUiChecks
    {
        private static int checks;
#if PAVISE_LIBRARY_BENCH && PAVISE_SELFTEST
        [STAThread]
        private static int Main(string[] args)
        {
            Console.OutputEncoding = new System.Text.UTF8Encoding(false);
            if (args.Length != 1 || !string.Equals(Path.GetFullPath(args[0]).TrimEnd('\\'),
                AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return 2;
            Settings.UseTransientStoreForCurrentProcess();
            Logger.LogPath = Path.Combine(args[0], "library-ui.log");
            Dpi.Init(); Lang.Init();
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                checks = 0;
                CheckUnifiedAddGame();
                Check(ScrollGetCapture() == IntPtr.Zero, "Isolated checks retained mouse capture");
                Console.WriteLine("PASS add-game assertions=" + checks
                    + " application_started=false windows_shown=false scans_started=false mouse_captured=false settings=transient snapshots=0");
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
#endif

        private static void CheckUnifiedAddGame()
        {
            float originalScale = Dpi.Scale; int originalLang = Lang.Cur; bool originalLight = Theme.LightMode;
            try
            {
                foreach (float scale in new[] { 1f, 1.25f, 1.5f, 2f, 3f })
                foreach (int language in new[] { 0, 1 })
                foreach (bool light in new[] { false, true })
                {
                    Dpi.Scale = scale; Theme.DropFontCache(); Lang.Cur = language; Theme.SetLight(light);
                    CheckAddGameDialog();
                    CheckAddGameRowLayout();
                    CheckScrollGeometry();
                    if (scale == 1f)
                    {
                        CheckScrollNative();
                        CheckScrollInteractions();
                        CheckScrollLifetime();
                        CheckPickerScroll();
                        CheckAddGameMergeOrder(false);
                        CheckAddGameMergeOrder(true);
                        CheckAddGameRunningRefresh();
                        CheckAddGameSelection();
                        CheckAddGameEntryChoices();
                        CheckAddGameSearch();
                        CheckAddGameGpuSelection();
                        CheckAddGameIconLifetime();
                        CheckAddGamePostedClose();
                        CheckRunningPickerResults();
                        CheckRunningPickerPendingClose();
                    }
                }
            }
            finally { Dpi.Scale = originalScale; Lang.Cur = originalLang; Theme.SetLight(originalLight); Theme.DropFontCache(); }
        }

        internal static int Run(string outputDirectory)
        {
            checks = 0;
            Exception failure = null;
            var thread = new Thread(delegate()
            {
                try { RunSta(outputDirectory); }
                catch (Exception ex) { failure = ex; }
            });
            thread.IsBackground = true; thread.SetApartmentState(ApartmentState.STA); thread.Start();
            if (!thread.Join(30000)) throw new Exception("UI checks exceeded 30 seconds");
            if (failure != null) throw new Exception("UI checks failed", failure);
            return checks;
        }
        private static void Check(bool good, string message)
        {
            if (!good) throw new Exception(message); checks++;
        }
        private static void CreateHiddenTree(Control control)
        {
            typeof(Control).GetMethod("CreateControl", BindingFlags.Instance | BindingFlags.NonPublic,
                null, new[] { typeof(bool) }, null).Invoke(control, new object[] { true });
            foreach (Control child in control.Controls) CreateHiddenTree(child);
        }
        private static GameLibraryItem MakeItem(string id, bool observed, bool suppress)
        {
            var profile = new GameProfile();
            profile.Id = id; profile.Name = "星际远征 · Aurora & Beyond " + id;
            profile.ExecutablePath = @"D:\Games\Aurora\Project With A Very Long Folder Name\Binaries\Win64\Aurora-Client-Win64-Shipping.exe";
            if (suppress) profile.Overrides[PolicyCatalog.KeySuppressFamily] = "1";
            if (id == "01")
            {
                profile.ForceTrigger = true;
                profile.Overrides[PolicyCatalog.KeyBoost] = "1";
                profile.Overrides[PolicyCatalog.KeyPauseUpdate] = "1";
            }
            return new GameLibraryItem(profile, id == "01", observed);
        }
        private static void CheckLongResetDialog()
        {
            var lines = new string[40];
            for (int i = 0; i < lines.Length; i++)
                lines[i] = @"D:\Pavise\" + new string('x', 180) + i + ".dat (IOException)";
            string body = Lang.F("store.savefatal.deletefail", @"D:\Pavise", string.Join("\r\n", lines));
            ConstructorInfo create = typeof(PaviseDialog).GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic, null,
                new[] { typeof(string), typeof(string), typeof(DlgKind), typeof(string), typeof(string), typeof(Control), typeof(int) }, null);
            using (var dialog = (Form)create.Invoke(new object[]
                { Lang.T("store.savefatal.title"), body, DlgKind.Danger, null, null, null, 468 }))
            {
                CreateHiddenTree(dialog);
                Control[] matches = dialog.Controls.Find("ScrollableDialogDetails", true);
                Check(matches.Length == 1, "Long reset diagnostics must scroll");
                var details = matches[0] as TextBox;
                Check(details != null && details.Multiline && details.ReadOnly
                    && details.ScrollBars == ScrollBars.Vertical && details.Text == body,
                    "Reset diagnostics must remain complete and copyable");
                Check(dialog.ClientSize.Height < Screen.FromPoint(Cursor.Position).WorkingArea.Height,
                    "Long reset diagnostics put the confirmation button off-screen");
                foreach (Control control in dialog.Controls)
                    Check(dialog.ClientRectangle.Contains(control.Bounds), "Reset dialog control is clipped");
            }
        }

        private static void CheckAddGameDialog()
        {
            // Do not create a handle or show this dialog, Load really scans install records and processes
            using (var dialog = new AddGameDialog(new string[0], false))
            {
                Type type = typeof(AddGameDialog);
                BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
                Check(type.GetField("btnDeep", fields) == null && type.GetMethod("DeepScan", fields) == null,
                    "Removed deep scan still has a UI entry");
                Check(!dialog.IsHandleCreated
                    && !(bool)type.GetField("scanning", fields).GetValue(dialog)
                    && !(bool)type.GetField("collectingRunning", fields).GetValue(dialog)
                    && type.GetField("infoTimer", fields).GetValue(dialog) == null
                    && type.GetField("runningTimer", fields).GetValue(dialog) == null,
                    "Isolated add-game UI check unexpectedly started scanning");
                var buttons = new List<Control>();
                Label hint = null;
                foreach (Control control in dialog.Controls)
                {
                    if (control is PillButton && control.Top == Theme.S(506)) buttons.Add(control);
                    if (control is Label && control.Text == Lang.T("scan.hint")) hint = (Label)control;
                }
                Check(type.GetField("btnRunning", fields) == null && type.GetMethod("PickRunning", fields) == null,
                    "Unified add-game dialog still opens a separate running-program picker");
                Check(buttons.Count == 4, "Add-game footer must contain only Browse, Browse Folder, Add, and Cancel");
                bool folderButton = false;
                foreach (Control button in buttons) if (button.Text == Lang.T("scan.folder")) folderButton = true;
                Check(folderButton, "Add-game footer lost the folder browse button");
                foreach (Control button in buttons)
                {
                    Check(dialog.ClientRectangle.Contains(button.Bounds), "Add-game footer button is clipped");
                    int textWidth = TextRenderer.MeasureText(button.Text, button.Font,
                        new Size(int.MaxValue, int.MaxValue), TextFormatFlags.SingleLine).Width;
                    Check(textWidth <= button.Width - Theme.S(16), "Add-game footer button text is clipped");
                }
                for (int i = 0; i < buttons.Count; i++)
                    for (int j = i + 1; j < buttons.Count; j++)
                        Check(!buttons[i].Bounds.IntersectsWith(buttons[j].Bounds), "Add-game footer buttons overlap");
                buttons.Sort(delegate(Control a, Control b) { return a.Left.CompareTo(b.Left); });
                Check(buttons[0].Text == Lang.T("scan.browse") && buttons[1].Text == Lang.T("scan.folder")
                    && buttons[2].Text == Lang.T("btn.add") && buttons[3].Text == Lang.T("btn.cancel"),
                    "Add-game footer action order changed");
                Check(hint != null, "Install-record scan limitation hint is missing");
                int hintHeight = TextRenderer.MeasureText(hint.Text, hint.Font,
                    new Size(hint.Width, int.MaxValue), TextFormatFlags.WordBreak).Height;
                Check(hintHeight <= hint.Height, "Install-record scan limitation hint is clipped: scale="
                    + Dpi.Scale + " language=" + Lang.Cur + " measured=" + hintHeight + " available=" + hint.Height);
                type.GetMethod("UpdateInfoLabel", fields).Invoke(dialog, null);
                Label info = (Label)type.GetField("lblInfo", fields).GetValue(dialog);
                Check(info.Text == Lang.T("scan.none") && info.Text.IndexOf("深度扫描", StringComparison.Ordinal) < 0
                    && info.Text.IndexOf("Deep Scan", StringComparison.OrdinalIgnoreCase) < 0,
                    "Empty scan results still recommend the removed deep scan");
                Check(info.Text.IndexOf("\u201c正在运行\u201d", StringComparison.Ordinal) < 0
                    && info.Text.IndexOf("Select a running program", StringComparison.OrdinalIgnoreCase) < 0,
                    "Empty scan results still direct users to the removed running-program picker");
                Check(!buttons[2].Enabled, "Add-game confirmation must start disabled without a selection");
                var list = (TechListBox)type.GetField("lst", fields).GetValue(dialog);
                Check(list.Parent is TechListScrollHost && list.ExternalScrollBar,
                    "Add-game list did not opt in to the themed scroll host");
            }
        }

        private const BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const string FixtureRoot = @"C:\PaviseAddGameFixture";

        // No test may show a window or call the production Load handler
        // Those enumerate processes, install records, icons and GPU counters
        private sealed class NoScanAddGameDialog : AddGameDialog
        {
            internal NoScanAddGameDialog(params string[] existing) : base(existing, false) { }
            protected override void OnLoad(EventArgs e) { }
            protected override void SetVisibleCore(bool value)
            {
                if (value) throw new InvalidOperationException("Add-game checks must never show a window");
                base.SetVisibleCore(false);
            }
        }

        private static T Member<T>(object instance, string name)
        {
            for (Type type = instance.GetType(); type != null; type = type.BaseType)
            {
                FieldInfo field = type.GetField(name, InstanceFields | BindingFlags.DeclaredOnly);
                if (field != null) return (T)field.GetValue(instance);
            }
            throw new MissingFieldException(instance.GetType().Name, name);
        }

        private static void SetMember(object instance, string name, object value)
        {
            for (Type type = instance.GetType(); type != null; type = type.BaseType)
            {
                FieldInfo field = type.GetField(name, InstanceFields | BindingFlags.DeclaredOnly);
                if (field == null) continue;
                field.SetValue(instance, value);
                return;
            }
            throw new MissingFieldException(instance.GetType().Name, name);
        }

        private static object Invoke(object instance, string name, params object[] args)
        {
            for (Type type = instance.GetType(); type != null; type = type.BaseType)
            {
                MethodInfo method = type.GetMethod(name, InstanceFields | BindingFlags.DeclaredOnly);
                if (method == null) continue;
                try { return method.Invoke(instance, args); }
                catch (TargetInvocationException error) { throw new Exception(name, error.InnerException); }
            }
            throw new MissingMethodException(instance.GetType().Name, name);
        }

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        private static extern int ScrollGetWindowLong(IntPtr window, int index);
        [DllImport("user32.dll", EntryPoint = "SendMessageW")]
        private static extern IntPtr ScrollMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", EntryPoint = "GetCapture")]
        private static extern IntPtr ScrollGetCapture();

        private sealed class NoShowScrollForm : Form
        {
            protected override void SetVisibleCore(bool value)
            {
                if (value) throw new InvalidOperationException("Scroll checks must never show a window");
                base.SetVisibleCore(false);
            }
        }

        private sealed class ScrollFixture : IDisposable
        {
            internal readonly NoShowScrollForm Form;
            internal readonly TechListBox List;
            internal readonly TechListScrollHost Host;
            internal ScrollFixture()
            {
                Form = new NoShowScrollForm { AutoScaleMode = AutoScaleMode.None, ShowInTaskbar = false,
                    ClientSize = new Size(Theme.S(400), Theme.S(240)) };
                List = new TechListBox { BorderStyle = BorderStyle.None, IntegralHeight = false,
                    DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = Theme.S(24), BackColor = Theme.Card };
                Host = new TechListScrollHost(List) { Size = new Size(Theme.S(320), Theme.S(145)) };
                Form.Controls.Add(Host);
                CreateHiddenTree(Form);
                Application.DoEvents();
                Check(!Form.Visible && !List.ContainsFocus && ScrollGetCapture() == IntPtr.Zero,
                    "Hidden scroll fixture interfered with desktop input");
            }
            public void Dispose() { Form.Dispose(); }
        }

        private static T ScrollProperty<T>(Control rail, string name)
        { return (T)rail.GetType().GetProperty(name, InstanceFields).GetValue(rail, null); }

        private static void ScrollRows(TechListBox list, int count)
        {
            list.BeginUpdate();
            try
            {
                list.Items.Clear();
                for (int i = 0; i < count; i++) list.Items.Add("Row " + i.ToString("D3"));
            }
            finally { list.EndUpdate(); }
            Application.DoEvents();
        }

        private static void CheckNoNativeScroll(TechListBox list, string operation)
        {
            Check(list.IsHandleCreated && (ScrollGetWindowLong(list.Handle, -16) & 0x00200000) == 0,
                "Native white scrollbar returned after " + operation);
        }

        private static void CheckScrollRange(TechListScrollHost host, TechListBox list, string operation)
        {
            Application.DoEvents();
            int page = Math.Max(1, list.ClientSize.Height / Math.Max(1, list.ItemHeight));
            Check(host.VisibleRows == page && host.MaximumTop == Math.Max(0, list.Items.Count - page),
                "Scroll range did not follow the final native viewport after " + operation);
            Check(host.ScrollNeeded == (list.Items.Count > page), "Scroll visibility mismatch after " + operation);
            Control rail = Member<Control>(host, "rail");
            int railWidth = host.ScrollNeeded ? Math.Min(host.ClientSize.Width, Theme.S(14)) : 0;
            Check(list.Width == Math.Max(0, host.ClientSize.Width - railWidth) && rail.Width == railWidth
                && rail.Right == host.ClientSize.Width && list.Height == host.ClientSize.Height,
                "Scroll gutter clips the list or wastes empty-list width after " + operation);
            Rectangle track = ScrollProperty<Rectangle>(rail, "TrackRectangle");
            Rectangle thumb = ScrollProperty<Rectangle>(rail, "ThumbRectangle");
            Check(track.Width >= 0 && track.Height >= 0 && thumb.Width >= 0 && thumb.Height >= 0,
                "Scroll geometry became negative after " + operation);
            if (host.ScrollNeeded && track.Width > 0 && track.Height > 0)
                Check(track.Contains(thumb) && thumb.Height >= Math.Min(Theme.S(32), track.Height),
                    "Scroll thumb escaped its track or lost its minimum target after " + operation);
            else Check(thumb.IsEmpty, "A non-scrollable list still paints a scroll thumb after " + operation);
            CheckNoNativeScroll(list, operation);
        }

        private static void CheckRailPaint(Control rail, Color expected, string state)
        {
            Rectangle thumb = ScrollProperty<Rectangle>(rail, "ThumbRectangle");
            using (var pixels = new Bitmap(rail.Width, rail.Height))
            using (Graphics graphics = Graphics.FromImage(pixels))
            {
                graphics.Clear(rail.BackColor);
                Invoke(rail, "OnPaint", new PaintEventArgs(graphics, rail.ClientRectangle));
                Check(pixels.GetPixel(thumb.Left + thumb.Width / 2, thumb.Top + thumb.Height / 2).ToArgb()
                    == expected.ToArgb(), "Scroll thumb does not use the current theme in " + state);
                Check(pixels.GetPixel(0, thumb.Top + thumb.Height / 2).ToArgb() == rail.BackColor.ToArgb(),
                    "Scroll painting filled the whole hit lane in " + state);
            }
        }

        private static void CheckScrollGeometry()
        {
            using (var fixture = new ScrollFixture())
            {
                TechListBox list = fixture.List; TechListScrollHost host = fixture.Host;
                Control rail = Member<Control>(host, "rail");
                Check(!rail.TabStop && !(bool)Invoke(rail, "GetStyle", ControlStyles.Selectable)
                    && rail.AccessibleRole == AccessibleRole.ScrollBar
                    && rail.AccessibleName == Lang.T("ui.scroll.vertical"),
                    "Scroll rail became focusable or lost localized accessibility metadata");
                int page = host.VisibleRows;
                foreach (int count in new[] { 0, page - 1, page, page + 1, 75, 0 })
                {
                    ScrollRows(list, count);
                    CheckScrollRange(host, list, "count=" + count);
                }
                ScrollRows(list, 75);
                Rectangle track = ScrollProperty<Rectangle>(rail, "TrackRectangle");
                Rectangle thumb = ScrollProperty<Rectangle>(rail, "ThumbRectangle");
                Check(thumb.Top == track.Top && thumb.Width == Theme.S(6),
                    "Idle scroll thumb has the wrong start position or width");
                CheckRailPaint(rail, Theme.StrokeHi, "idle");
                Invoke(rail, "OnMouseEnter", EventArgs.Empty);
                Check(ScrollProperty<Rectangle>(rail, "ThumbRectangle").Width == Theme.S(8),
                    "Hover scroll thumb did not expand inside the fixed hit lane");
                CheckRailPaint(rail, Col.Lerp(Theme.StrokeHi, Theme.Accent, 0.7f), "hover");
                Invoke(rail, "BeginDrag", thumb.Top + thumb.Height / 2);
                CheckRailPaint(rail, Theme.Accent, "drag");
                Invoke(rail, "OnMouseCaptureChanged", EventArgs.Empty);
                Invoke(rail, "OnMouseLeave", EventArgs.Empty);
                host.ScrollTo(int.MaxValue);
                Check(list.TopIndex == host.MaximumTop
                    && ScrollProperty<Rectangle>(rail, "ThumbRectangle").Bottom == track.Bottom,
                    "Last row and scroll thumb cannot reach the bottom together");
                host.ScrollTo(int.MinValue);
                Check(list.TopIndex == 0, "ScrollTo did not clamp negative input");
                foreach (Size size in new[] { Size.Empty, new Size(Theme.S(2), Theme.S(6)),
                    new Size(Theme.S(320), Theme.S(145)) })
                {
                    host.Size = size;
                    CheckScrollRange(host, list, "resize=" + size);
                }
                Check(!fixture.Form.Visible && !list.ContainsFocus && ScrollGetCapture() == IntPtr.Zero,
                    "Geometry or synthetic paint checks acquired desktop input");
            }
        }

        private static void ScrollWheel(TechListBox list, int delta)
        {
            ScrollMessage(list.Handle, 0x020A,
                (IntPtr)unchecked((int)((uint)(ushort)delta << 16)), IntPtr.Zero);
            Application.DoEvents();
        }

        private static void ScrollKey(TechListBox list, Keys key)
        {
            ScrollMessage(list.Handle, 0x0100, (IntPtr)(int)key, (IntPtr)1);
            ScrollMessage(list.Handle, 0x0101, (IntPtr)(int)key, (IntPtr)unchecked((int)0xC0000001));
            Application.DoEvents();
        }

        private static void CheckScrollNative()
        {
            using (var fixture = new ScrollFixture())
            {
                TechListBox list = fixture.List; TechListScrollHost host = fixture.Host;
                ScrollRows(list, 80);
                int viewportEvents = 0;
                list.ViewportChanged += delegate { viewportEvents++; };
                list.BeginUpdate();
                list.Items.Add("Added A"); list.Items.Add("Added B"); list.Items.Add("Added C");
                CheckNoNativeScroll(list, "batched add");
                list.Items.RemoveAt(1);
                CheckNoNativeScroll(list, "batched remove");
                Application.DoEvents();
                Check(viewportEvents == 0, "Incomplete BeginUpdate batch published an intermediate viewport");
                list.EndUpdate(); Application.DoEvents();
                Check(viewportEvents == 1, "EndUpdate did not coalesce the final viewport notification");
                CheckScrollRange(host, list, "EndUpdate");
                list.Items.Insert(0, "Inserted"); list.Items.RemoveAt(list.Items.Count - 1);
                CheckScrollRange(host, list, "insert/delete");

                host.ScrollTo(0); list.SelectedIndex = 0; Application.DoEvents();
                ScrollWheel(list, -120);
                int wheelStep = list.TopIndex;
                Check(SystemInformation.MouseWheelScrollLines == 0 ? wheelStep == 0 : wheelStep > 0,
                    "Hiding the native scrollbar disabled wheel scrolling");
                Check(list.SelectedIndex == 0, "Mouse wheel changed the pending keyboard selection");
                host.ScrollTo(0);
                ScrollWheel(list, -60);
                Check(list.TopIndex == 0, "A half wheel notch was not accumulated");
                ScrollWheel(list, -60);
                Check(list.TopIndex == wheelStep, "Two half notches differ from one full wheel notch");
                if (wheelStep > 0)
                {
                    for (int i = 0; i <= list.Items.Count && list.TopIndex < host.MaximumTop; i++) ScrollWheel(list, -120);
                    Check(list.TopIndex == host.MaximumTop && list.SelectedIndex == 0,
                        "Repeated wheel input cannot reach the last row without changing selection");
                    var wheel = new HandledMouseEventArgs(MouseButtons.None, 0, 0, 0, 120);
                    Invoke(Member<Control>(host, "rail"), "OnMouseWheel", wheel);
                    Application.DoEvents();
                    Check(wheel.Handled && list.TopIndex == Math.Max(0, host.MaximumTop - wheelStep),
                        "Wheel over the rail was ignored or delivered more than once");
                }

                host.ScrollTo(0); list.SelectedIndex = 0;
                ScrollKey(list, Keys.Down); Check(list.SelectedIndex == 1, "Down arrow stopped moving native selection");
                ScrollKey(list, Keys.Up); Check(list.SelectedIndex == 0, "Up arrow stopped moving native selection");
                ScrollKey(list, Keys.PageDown); int firstPage = list.SelectedIndex;
                ScrollKey(list, Keys.PageDown);
                Check(firstPage > 0 && list.SelectedIndex > firstPage && list.TopIndex > 0,
                    "PageDown stopped moving through the native list");
                int secondPage = list.SelectedIndex;
                ScrollKey(list, Keys.PageUp);
                Check(list.SelectedIndex < secondPage, "PageUp stopped moving through the native list");
                ScrollKey(list, Keys.End);
                Check(list.SelectedIndex == list.Items.Count - 1 && list.TopIndex == host.MaximumTop,
                    "End key cannot reach the last item with the external rail");
                ScrollKey(list, Keys.Home);
                Check(list.SelectedIndex == 0 && list.TopIndex == 0, "Home key cannot return to the first item");
                for (int i = 1; i < list.Items.Count; i++) ScrollKey(list, Keys.Down);
                Check(list.SelectedIndex == list.Items.Count - 1 && list.TopIndex == host.MaximumTop,
                    "Arrow navigation cannot reach the last item");

                list.Items.Add("Zebra final"); Application.DoEvents();
                ScrollKey(list, Keys.Home); viewportEvents = 0;
                ScrollMessage(list.Handle, 0x0102, (IntPtr)'Z', (IntPtr)1);
                Application.DoEvents();
                Check(list.SelectedIndex == list.Items.Count - 1 && list.TopIndex > 0 && viewportEvents > 0,
                    "Native character navigation failed to notify the themed rail");
                Rectangle thumb = ScrollProperty<Rectangle>(Member<Control>(host, "rail"), "ThumbRectangle");
                Rectangle track = ScrollProperty<Rectangle>(Member<Control>(host, "rail"), "TrackRectangle");
                Check(thumb.Bottom == track.Bottom, "Character navigation left the thumb at an old position");
                list.ItemHeight = Theme.S(31); CheckScrollRange(host, list, "item-height change");
                host.Height = Theme.S(190); CheckScrollRange(host, list, "viewport resize");
                ScrollRows(list, 0); CheckScrollRange(host, list, "clear");
                Check(!fixture.Form.Visible && !list.ContainsFocus && ScrollGetCapture() == IntPtr.Zero,
                    "Native scroll input acquired desktop focus or capture");
            }
        }

        private static void ExerciseRail(TechListScrollHost host, TechListBox list)
        {
            Control rail = Member<Control>(host, "rail");
            int selected = list.SelectedIndex, selectionEvents = 0, rowInputs = 0;
            EventHandler selectedChanged = delegate { selectionEvents++; };
            EventHandler clicked = delegate { rowInputs++; };
            MouseEventHandler pressed = delegate { rowInputs++; };
            list.SelectedIndexChanged += selectedChanged; list.Click += clicked;
            list.DoubleClick += clicked; list.MouseDown += pressed;
            try
            {
                host.ScrollTo(0);
                Rectangle track = ScrollProperty<Rectangle>(rail, "TrackRectangle");
                Rectangle thumb = ScrollProperty<Rectangle>(rail, "ThumbRectangle");
                Check(thumb.Bottom < track.Bottom, "Rail interaction fixture must have room below its thumb");
                // Click outside the thumb only, clicking the thumb in production code grabs global mouse capture
                // The pure BeginDrag and DragTo helpers can safely exercise that mapping
                Invoke(rail, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1,
                    rail.Width / 2, track.Bottom - 1, 0));
                Check(list.TopIndex == host.VisibleRows, "Clicking the lower track did not advance one page");
                Check(ScrollProperty<Rectangle>(rail, "ThumbRectangle").Top > track.Top,
                    "Track paging did not update thumb geometry on the first frame");
                Invoke(rail, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1,
                    rail.Width / 2, track.Top, 0));
                Check(list.TopIndex == 0, "Clicking the upper track did not return one page");
                int offset = thumb.Height / 2;
                Invoke(rail, "BeginDrag", thumb.Top + offset);
                Check(Member<bool>(rail, "dragging") && !rail.Capture && ScrollGetCapture() == IntPtr.Zero,
                    "Synthetic drag unexpectedly acquired mouse capture");
                int travel = track.Height - thumb.Height;
                Invoke(rail, "DragTo", track.Top + travel / 2 + offset);
                Check(Math.Abs(list.TopIndex - host.MaximumTop / 2) <= 1,
                    "Thumb drag did not map proportionally to the native row range");
                Invoke(rail, "DragTo", track.Bottom + offset);
                Check(list.TopIndex == host.MaximumTop
                    && ScrollProperty<Rectangle>(rail, "ThumbRectangle").Bottom == track.Bottom,
                    "Dragging to the bottom did not expose the final row immediately");
                Invoke(rail, "DragTo", track.Top - 100);
                Check(list.TopIndex == 0, "Reversing the drag did not clamp at the first row");
                Invoke(rail, "OnMouseCaptureChanged", EventArgs.Empty);
                Invoke(rail, "DragTo", track.Bottom + offset);
                Check(!Member<bool>(rail, "dragging") && list.TopIndex == 0,
                    "Losing capture allowed a stale drag to continue");
                Invoke(rail, "BeginDrag", thumb.Top + offset);
                Invoke(rail, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1, 0, 0, 0));
                Check(!Member<bool>(rail, "dragging"), "Mouse-up did not end the drag");
                Check(list.SelectedIndex == selected && selectionEvents == 0 && rowInputs == 0,
                    "Scroll rail forwarded a click, double-click, or selection change to a row");
                Check(ScrollGetCapture() == IntPtr.Zero && !list.ContainsFocus,
                    "Rail interactions acquired desktop focus or capture");
            }
            finally
            {
                list.SelectedIndexChanged -= selectedChanged; list.Click -= clicked;
                list.DoubleClick -= clicked; list.MouseDown -= pressed;
            }
        }

        private static void CheckScrollInteractions()
        {
            using (var fixture = new ScrollFixture())
            {
                TechListBox list = fixture.List; TechListScrollHost host = fixture.Host;
                ScrollRows(list, 120); list.SelectedIndex = 2;
                ExerciseRail(host, list);
                Control rail = Member<Control>(host, "rail");
                Invoke(rail, "BeginDrag", ScrollProperty<Rectangle>(rail, "ThumbRectangle").Top + 1);
                list.Items.RemoveAt(list.Items.Count - 1); Application.DoEvents();
                Check(!Member<bool>(rail, "dragging"), "A changed item range retained stale drag coordinates");
                Invoke(rail, "BeginDrag", ScrollProperty<Rectangle>(rail, "ThumbRectangle").Top + 1);
                host.Height += Theme.S(24); Application.DoEvents();
                Check(!Member<bool>(rail, "dragging"), "Resizing the viewport did not cancel the old drag range");
                Invoke(rail, "BeginDrag", ScrollProperty<Rectangle>(rail, "ThumbRectangle").Top + 1);
                rail.Enabled = false;
                Check(!Member<bool>(rail, "dragging"), "Disabling the rail retained a drag");
                rail.Enabled = true;
                Invoke(rail, "BeginDrag", ScrollProperty<Rectangle>(rail, "ThumbRectangle").Top + 1);
                Invoke(rail, "OnVisibleChanged", EventArgs.Empty);
                Check(!Member<bool>(rail, "dragging"), "Hidden rail retained a drag");
                CheckScrollRange(host, list, "range changes during drag");
            }
        }

        private static bool HasViewportTarget(TechListBox list, object target)
        {
            EventHandler callback = Member<EventHandler>(list, "ViewportChanged");
            if (callback == null) return false;
            foreach (Delegate entry in callback.GetInvocationList())
                if (ReferenceEquals(entry.Target, target)) return true;
            return false;
        }

        private static void PrimeScrollBuffer(TechListBox list)
        {
            using (var pixels = new Bitmap(list.Width, list.Height))
            using (Graphics graphics = Graphics.FromImage(pixels))
                Invoke(list, "OnDrawItem", new DrawItemEventArgs(graphics, list.Font,
                    new Rectangle(0, 0, list.Width, list.ItemHeight), 0, DrawItemState.None));
            Check(Member<IntPtr>(list, "memDc") != IntPtr.Zero && Member<IntPtr>(list, "memBmp") != IntPtr.Zero,
                "Scroll buffer lifetime fixture failed to create its drawing resources");
        }

        private static void CheckScrollBufferReleased(TechListBox list, string operation)
        {
            Check(Member<IntPtr>(list, "memDc") == IntPtr.Zero && Member<IntPtr>(list, "memBmp") == IntPtr.Zero
                && Member<object>(list, "surface") == null, "Scroll buffer leaked after " + operation);
        }

        private static void CheckScrollLifetime()
        {
            using (var fixture = new ScrollFixture())
            {
                TechListBox list = fixture.List; TechListScrollHost host = fixture.Host;
                ScrollRows(list, 80); list.SelectedIndex = 10; Application.DoEvents();
                PrimeScrollBuffer(list);
                int callbacks = 0, generation = Member<int>(list, "viewportGeneration");
                list.ViewportChanged += delegate { callbacks++; };
                list.Items.Add("Queued before destroy");
                Check(Member<bool>(list, "viewportQueued"), "Handle-lifetime fixture did not queue a viewport callback");
                Invoke(list, "DestroyHandle");
                Check(Member<int>(list, "viewportGeneration") > generation && !Member<bool>(list, "viewportQueued"),
                    "Destroyed handle retained a live viewport generation");
                CheckScrollBufferReleased(list, "handle destruction");
                Application.DoEvents();
                Check(callbacks == 0, "A callback from the destroyed handle reached the host");
                CreateHiddenTree(list); Application.DoEvents();
                Check(list.Items.Count == 81 && list.SelectedIndex == 10 && callbacks > 0,
                    "Handle recreation lost list data, selection, or the fresh viewport notification");
                CheckScrollRange(host, list, "handle recreation");
                list.Items.Add("Queued before recreate");
                Invoke(list, "RecreateHandle"); Application.DoEvents();
                CheckScrollRange(host, list, "recreate with pending callback");
                Check(list.Items.Count == 82 && list.SelectedIndex == 10,
                    "Recreating with a pending callback lost native list state");
                PrimeScrollBuffer(list);
                callbacks = 0; list.Items.Add("Queued before dispose");
                host.Dispose(); Application.DoEvents();
                Check(list.IsDisposed && Member<Control>(host, "rail").IsDisposed && callbacks == 0,
                    "Disposing the host left a control or queued viewport callback alive");
                Check(!HasViewportTarget(list, host), "Disposed scroll host remained subscribed to viewport changes");
                CheckScrollBufferReleased(list, "host disposal");
            }
            using (var form = new NoShowScrollForm { AutoScaleMode = AutoScaleMode.None, ShowInTaskbar = false })
            using (var list = new TechListBox { Size = new Size(320, 145), ItemHeight = 24,
                IntegralHeight = false, BorderStyle = BorderStyle.None, DrawMode = DrawMode.OwnerDrawFixed })
            {
                form.Controls.Add(list); ScrollRows(list, 40); CreateHiddenTree(form); Application.DoEvents();
                int callbacks = 0;
                list.ViewportChanged += delegate { callbacks++; };
                list.Items.Add("Default native list"); Application.DoEvents();
                Check(!list.ExternalScrollBar && (ScrollGetWindowLong(list.Handle, -16) & 0x00200000) != 0
                    && callbacks == 0, "Unrelated TechListBox instances lost their native scrollbar or gained viewport callbacks");
                list.SelectedIndex = 9;
                list.TopIndex = 0; ScrollWheel(list, -120);
                Check((SystemInformation.MouseWheelScrollLines == 0 ? list.TopIndex == 0 : list.TopIndex > 0)
                    && list.SelectedIndex == 9 && callbacks == 0,
                    "The picker-only wheel handler changed ordinary native lists");
                var host = new TechListScrollHost(list) { Size = new Size(320, 145) };
                form.Controls.Add(host); CreateHiddenTree(host); Application.DoEvents();
                Check(list.SelectedIndex == 9 && list.Items.Count == 41 && HasViewportTarget(list, host),
                    "Attaching a scroll host lost native selection/data or failed to subscribe");
                CheckNoNativeScroll(list, "attaching an already-created list");
                host.Controls.Remove(list); form.Controls.Add(list); Application.DoEvents();
                Check(!list.ExternalScrollBar && !HasViewportTarget(list, host)
                    && (ScrollGetWindowLong(list.Handle, -16) & 0x00200000) != 0,
                    "Detaching a list did not restore native scrolling and remove the host callback");
                callbacks = 0; list.Items.Add("After detach"); Application.DoEvents();
                Check(callbacks == 0, "Detached native list still posted external viewport notifications");
                host.Dispose();
                Check(!list.IsDisposed && list.Items.Count == 42,
                    "Disposing a detached host disposed or changed the independent list");
            }
        }

        private static void CheckPickerScroll()
        {
            using (var dialog = new NoScanAddGameDialog())
            {
                var candidates = new object[40];
                for (int i = 0; i < candidates.Length; i++)
                {
                    string path = FixtureRoot + "\\Scroll" + i.ToString("D3") + @"\Game.exe";
                    candidates[i] = Candidate("Title " + i.ToString("D3"), path, FixtureRoot);
                    Invoke(dialog, "CacheIcon", path, null); // A cached miss prevents real icon work after handle creation
                }
                Merge(dialog, false, candidates);
                string chosen = FixtureRoot + @"\Scroll000\Game.exe";
                TogglePath(dialog, chosen);
                object row = FindRow(dialog, chosen);
                CreateHiddenTree(dialog); Application.DoEvents();
                var list = (TechListBox)Member<ListBox>(dialog, "lst");
                var host = list.Parent as TechListScrollHost;
                Check(host != null && host.ScrollNeeded, "Populated add-game picker is missing its scroll rail");
                ExerciseRail(host, list);
                Check(Member<bool>(row, "Checked") && Member<PillButton>(dialog, "btnAdd").Enabled
                    && dialog.Selected.Count == 0 && !Member<bool>(dialog, "closed"),
                    "Scrolling toggled or accepted the pending add-game selection");
                TextBox filter = Member<TextBox>(dialog, "tbFilter");
                foreach (string text in new[] { "Title 039", "No fixture match", "" })
                {
                    filter.Text = text;
                    CheckScrollRange(host, list, "add-game filter=" + text);
                }
                Merge(dialog, true, candidates);
                CheckScrollRange(host, list, "running-source refresh");
                Check(ReferenceEquals(row, FindRow(dialog, chosen)) && Member<bool>(row, "Checked")
                    && Member<HashSet<string>>(dialog, "queuedIcons").Count == 0,
                    "Refreshing a scrollable picker replaced selection or started icon work");
                CheckNoScans(dialog, true);
            }
            using (var dialog = new RunningPickerDialog(null))
            {
                var entries = new RunningPickerDialog.Entry[40];
                for (int i = 0; i < entries.Length; i++) entries[i] = PickerEntry("Scroll" + i.ToString("D3"), null);
                CreateHiddenTree(dialog);
                Check((bool)Invoke(dialog, "QueueScanResult", PickerResult(0, entries)), "Scroll fixture result was not queued");
                Application.DoEvents();
                var list = (TechListBox)Member<ListBox>(dialog, "list");
                var host = list.Parent as TechListScrollHost;
                Check(host != null && host.ScrollNeeded, "Populated whitelist picker is missing its scroll rail");
                list.SelectedIndex = 0;
                Invoke(dialog, "OnListMouseDown", list, new MouseEventArgs(MouseButtons.Left, 1, Theme.S(10), Theme.S(10), 0));
                ExerciseRail(host, list);
                Check(entries[0].Checked && Member<PillButton>(dialog, "confirm").Enabled && dialog.Selected.Count == 0,
                    "Scrolling toggled or confirmed a whitelist entry");
                TextBox filter = Member<TextBox>(dialog, "search");
                foreach (string text in new[] { "Scroll039", "No fixture match", "" })
                {
                    filter.Text = text;
                    CheckScrollRange(host, list, "whitelist filter=" + text);
                }
                Check(entries[0].Checked && !dialog.Visible && Member<int>(dialog, "scanGeneration") == 0,
                    "Whitelist scrolling lost selection, showed a window, or started discovery");
            }
            Check(ScrollGetCapture() == IntPtr.Zero, "Picker scroll fixtures captured the user's mouse");
        }

        private static object Candidate(string name, string path, string root, long memory = 0, int count = 0)
        {
            Type type = typeof(AddGameDialog).GetNestedType("Candidate", BindingFlags.NonPublic);
            object candidate = Activator.CreateInstance(type, true);
            type.GetField("Name").SetValue(candidate, name);
            type.GetField("Path").SetValue(candidate, path);
            type.GetField("Root").SetValue(candidate, root);
            type.GetField("Memory").SetValue(candidate, memory);
            type.GetField("Count").SetValue(candidate, count);
            return candidate;
        }

        private static void Merge(AddGameDialog dialog, bool running, params object[] candidates)
        {
            Type type = typeof(AddGameDialog).GetNestedType("Candidate", BindingFlags.NonPublic);
            var hits = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(type));
            foreach (object candidate in candidates) hits.Add(candidate);
            Type kind = typeof(AddGameDialog).GetNestedType("RowKind", BindingFlags.NonPublic);
            Invoke(dialog, "Merge", hits, Enum.Parse(kind, running ? "Running" : "Installed"));
        }

        private static object FindRow(AddGameDialog dialog, string path)
        {
            foreach (object row in Member<IList>(dialog, "rows"))
                if (string.Equals(Member<string>(row, "Path"), path, StringComparison.OrdinalIgnoreCase)) return row;
            return null;
        }

        private static int SelectRow(AddGameDialog dialog, string path)
        {
            IList shown = Member<IList>(dialog, "shown");
            for (int i = 0; i < shown.Count; i++)
                if (string.Equals(Member<string>(shown[i], "Path"), path, StringComparison.OrdinalIgnoreCase))
                {
                    Member<ListBox>(dialog, "lst").SelectedIndex = i;
                    return i;
                }
            throw new Exception("Missing visible fixture row: " + path);
        }

        private static void TogglePath(AddGameDialog dialog, string path)
        { Invoke(dialog, "ToggleAt", SelectRow(dialog, path)); }

        private static void CheckNoScans(AddGameDialog dialog, bool hasHandle = false)
        {
            Check(!dialog.Visible && dialog.IsHandleCreated == hasHandle, "Fixture visibility/handle state changed unexpectedly");
            Check(!Member<bool>(dialog, "scanning") && !Member<bool>(dialog, "collectingRunning")
                && !Member<bool>(dialog, "probingGpu") && !Member<bool>(dialog, "refreshBusy"),
                "Synthetic add-game checks started a real scanner");
            Check(Member<object>(dialog, "infoTimer") == null && Member<object>(dialog, "runningTimer") == null,
                "Synthetic add-game checks started a production timer");
        }

        private static void CheckAddGameMergeOrder(bool runningFirst)
        {
            string path = FixtureRoot + @"\Title\Bin\Game.exe";
            string installRoot = FixtureRoot + @"\Title";
            object installed = Candidate("Store title", path.ToUpperInvariant(), installRoot);
            object running = Candidate("Executable title", path, installRoot + @"\Bin", 12345678L, 2);
            using (var dialog = new NoScanAddGameDialog())
            {
                Merge(dialog, runningFirst, runningFirst ? running : installed);
                object original = FindRow(dialog, path);
                Check(original != null, "First discovery did not create its row");
                TogglePath(dialog, path);
                Merge(dialog, !runningFirst, runningFirst ? installed : running);
                object merged = FindRow(dialog, path);
                Check(Member<IList>(dialog, "rows").Count == 1 && ReferenceEquals(original, merged),
                    "Two discovery sources must update one case-insensitive row in place");
                Check(Member<bool>(merged, "Installed") && Member<bool>(merged, "Running")
                    && Member<bool>(merged, "Checked"), "Merging the second source lost status or user selection");
                Check(Member<string>(merged, "Name") == "Store title" && Member<string>(merged, "Root") == installRoot,
                    "Installed name/root must win regardless of scan completion order");
                Check(Member<long>(merged, "Memory") == 12345678L && Member<int>(merged, "Count") == 2,
                    "Merged installed row lost live process metadata");
                Merge(dialog, true, Candidate("New executable title", path, installRoot + @"\Bin", 23456789L, 3));
                Check(ReferenceEquals(merged, FindRow(dialog, path)) && Member<bool>(merged, "Checked")
                    && Member<string>(merged, "Name") == "Store title" && Member<string>(merged, "Root") == installRoot,
                    "Periodic refresh replaced the row, selection, or installed metadata");
                Check(Member<long>(merged, "Memory") == 23456789L && Member<int>(merged, "Count") == 3,
                    "Periodic refresh did not update memory/process count");
                Check(Member<ListBox>(dialog, "lst").SelectedIndex == 0,
                    "Periodic refresh lost keyboard selection");
                Invoke(dialog, "Accept");
                Check(dialog.Selected.Count == 1 && dialog.Selected[0].Root == installRoot,
                    "Unified selection must retain trusted installed metadata for library insertion");
                CheckNoScans(dialog);
            }
        }

        private static void CheckAddGameRunningRefresh()
        {
            string installed = FixtureRoot + @"\Installed\Game.exe";
            string checkedPath = FixtureRoot + @"\Checked\Game.exe";
            string uncheckedPath = FixtureRoot + @"\Unchecked\Game.exe";
            using (var dialog = new NoScanAddGameDialog())
            {
                Merge(dialog, false, Candidate("Installed", installed, FixtureRoot + @"\Installed"));
                Merge(dialog, true, Candidate("Installed live", installed, FixtureRoot, 100, 1),
                    Candidate("Checked", checkedPath, FixtureRoot, 200, 2),
                    Candidate("Unchecked", uncheckedPath, FixtureRoot, 300, 3));
                TogglePath(dialog, checkedPath);
                object retained = FindRow(dialog, checkedPath);
                Type kind = typeof(AddGameDialog).GetNestedType("RowKind", BindingFlags.NonPublic);
                Invoke(dialog, "Merge", null, Enum.Parse(kind, "Running"));
                Check(Member<IList>(dialog, "rows").Count == 3 && Member<bool>(retained, "Running"),
                    "A failed running snapshot must not be interpreted as all processes exiting");
                Merge(dialog, true);
                Check(Member<IList>(dialog, "rows").Count == 2 && FindRow(dialog, uncheckedPath) == null,
                    "Exited unchecked running-only rows must be removed");
                Check(Member<bool>(FindRow(dialog, installed), "Installed")
                    && !Member<bool>(FindRow(dialog, installed), "Running"),
                    "An exited installed game must remain installed without a stale running badge");
                Check(ReferenceEquals(retained, FindRow(dialog, checkedPath)) && Member<bool>(retained, "Checked")
                    && !Member<bool>(retained, "Running"), "Exiting a selected process must preserve the user's pending choice");
                Invoke(dialog, "ApplyGpuTags", new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
                    { { checkedPath, 90 } });
                Check(!Member<bool>(retained, "RendererLike"), "Late GPU data tagged a process that is no longer running");
                Merge(dialog, true, Candidate("Returned", checkedPath, FixtureRoot, 400, 4));
                Check(ReferenceEquals(retained, FindRow(dialog, checkedPath)) && Member<bool>(retained, "Running")
                    && Member<bool>(retained, "Checked"), "Returning process lost its retained row/selection");
                Invoke(dialog, "Accept");
                Check(dialog.Selected.Count == 1 && dialog.Selected[0].Exe == checkedPath,
                    "Refresh changed the pending add set");
                CheckNoScans(dialog);
            }
        }

        private static void CheckAddGameEntryChoices()
        {
            string root = FixtureRoot + @"\MultipleEntries";
            string first = root + @"\Alpha.exe", second = root + @"\Bin\Beta.exe";
            object a = Candidate("Store title", first, root), b = Candidate("Store title", second, root);
            SetMember(a, "NeedsChoice", true); SetMember(b, "NeedsChoice", true);
            using (var dialog = new NoScanAddGameDialog())
            {
                Merge(dialog, false, a, b);
                Check(Member<IList>(dialog, "rows").Count == 2, "Ambiguous installed entries disappeared");
                var list = Member<ListBox>(dialog, "lst");
                Check(list.Items[0].ToString() == "Store title · Alpha.exe"
                    && list.Items[1].ToString() == "Store title · Beta.exe", "Entry choices need distinct display labels");
                Check(!Member<bool>(FindRow(dialog, first), "Checked") && !Member<bool>(FindRow(dialog, second), "Checked"),
                    "Ambiguous static entries must not be checked automatically");
                Invoke(dialog, "ToggleAll");
                Check(!Member<bool>(FindRow(dialog, first), "Checked") && !Member<bool>(FindRow(dialog, second), "Checked"),
                    "Select-all must not select ambiguous entries");
                string unique = FixtureRoot + @"\Unique\Game.exe";
                Merge(dialog, false, Candidate("Unique title", unique, FixtureRoot + @"\Unique"));
                Invoke(dialog, "ToggleAll");
                Check(Member<bool>(FindRow(dialog, unique), "Checked")
                    && !Member<bool>(FindRow(dialog, first), "Checked") && !Member<bool>(FindRow(dialog, second), "Checked"),
                    "Select-all must select ordinary entries while leaving entry choices to the user");
                Invoke(dialog, "ToggleAll");
                Member<TextBox>(dialog, "tbFilter").Text = "Beta.exe";
                Check(list.Items.Count == 1, "Entry executable must remain searchable");
                TogglePath(dialog, second);
                Merge(dialog, true, Candidate("Running title", second, root, 100, 1));
                Check(Member<bool>(FindRow(dialog, second), "NeedsChoice"), "Running refresh lost installed entry metadata");
                Invoke(dialog, "Accept");
                Check(dialog.Selected.Count == 1 && dialog.Selected[0].Exe == second
                    && dialog.Selected[0].Name == "Store title" && dialog.Selected[0].Root == root,
                    "Choosing an entry must preserve the game title and install root");
                CheckNoScans(dialog);
            }
        }

        private static void CheckAddGameSelection()
        {
            string a = FixtureRoot + @"\A\Game.exe", b = FixtureRoot + @"\B\Game.exe";
            string known = FixtureRoot + @"\Known\Game.exe";
            using (var dialog = new NoScanAddGameDialog(known.ToUpperInvariant()))
            {
                Merge(dialog, false, Candidate("Same name", a, FixtureRoot + @"\A"),
                    Candidate("Same name", b, FixtureRoot + @"\B"), Candidate("Known", known, FixtureRoot + @"\Known"));
                Check(Member<IList>(dialog, "rows").Count == 3, "Distinct executable paths were deduplicated by name");
                TogglePath(dialog, known);
                Check(Member<bool>(FindRow(dialog, known), "Already") && !Member<bool>(FindRow(dialog, known), "Checked"),
                    "Already-listed executable was selectable");
                TogglePath(dialog, a);
                TextBox filter = Member<TextBox>(dialog, "tbFilter");
                filter.Text = @"\B\";
                Check(Member<IList>(dialog, "shown").Count == 1, "Path search did not filter the unified list");
                Invoke(dialog, "ToggleAll");
                Check(Member<bool>(FindRow(dialog, a), "Checked") && Member<bool>(FindRow(dialog, b), "Checked"),
                    "Filtered select-all must preserve choices outside the filter");
                Invoke(dialog, "ToggleAll");
                Check(Member<bool>(FindRow(dialog, a), "Checked") && !Member<bool>(FindRow(dialog, b), "Checked"),
                    "Filtered deselect-all changed hidden rows");
                filter.Text = "";
                SelectRow(dialog, b);
                var space = new KeyEventArgs(Keys.Space);
                Invoke(dialog, "OnKeyDown", space);
                Check(space.SuppressKeyPress && Member<bool>(FindRow(dialog, b), "Checked"),
                    "Space must toggle the selected unified row once");
                for (int i = 0; i < 8; i++) TogglePath(dialog, b);
                Check(Member<bool>(FindRow(dialog, b), "Checked"), "Rapid row toggles corrupted the checked state");
                Invoke(dialog, "Accept");
                Invoke(dialog, "Accept");
                Check(dialog.Selected.Count == 2 && dialog.Selected[0].Exe != dialog.Selected[1].Exe,
                    "Repeated confirmation added duplicates or included an already-listed row");
                CheckNoScans(dialog);
            }
            using (var dialog = new NoScanAddGameDialog(known))
            {
                Merge(dialog, true, Candidate("Known", known, FixtureRoot, 10, 1),
                    Candidate("Single", a, FixtureRoot, 20, 1));
                SelectRow(dialog, known);
                Invoke(Member<ListBox>(dialog, "lst"), "OnDoubleClick", EventArgs.Empty);
                Check(dialog.Selected.Count == 0, "Double-click selected an already-listed row");
                SelectRow(dialog, a);
                Invoke(Member<ListBox>(dialog, "lst"), "OnDoubleClick", EventArgs.Empty);
                Check(dialog.Selected.Count == 1 && dialog.Selected[0].Exe == a,
                    "Double-click did not confirm the intended executable");
                CheckNoScans(dialog);
            }
        }

        private static void CheckAddGameSearch()
        {
            string first = FixtureRoot + @"\First\Binaries\Win64\bg3.exe";
            string second = FixtureRoot + @"\Second\Game.exe";
            using (var dialog = new NoScanAddGameDialog())
            {
                Merge(dialog, false, Candidate("Baldur’s Gate 3", first, FixtureRoot + @"\First"),
                    Candidate("黑神话：悟空", second, FixtureRoot + @"\Second"));
                var filter = Member<TextBox>(dialog, "tbFilter");
                var list = Member<ListBox>(dialog, "lst");
                filter.Text = "ＢＡＬＤＵＲＳ　ＧＡＴＥ";
                Check(list.Items.Count == 1 && Member<string>(Member<IList>(dialog, "shown")[0], "Path") == first,
                    "Full-width and punctuation-tolerant search lost the matching game");
                TogglePath(dialog, first);
                filter.Text = "gate win64 bg3";
                Check(list.Items.Count == 1, "Search terms must match across title and executable path");
                Merge(dialog, true, Candidate("Running title", first, FixtureRoot + @"\First", 100, 1));
                Check(list.Items.Count == 1 && Member<bool>(FindRow(dialog, first), "Checked"),
                    "Running refresh lost filtered results or selection");
                filter.Text = "黑 神话悟空";
                Check(list.Items.Count == 1 && Member<string>(Member<IList>(dialog, "shown")[0], "Path") == second,
                    "Chinese fragments with punctuation differences must match");
                filter.Text = "gate win32";
                Check(list.Items.Count == 0, "Missing keyword should exclude the game");
                filter.Text = "---";
                Check(list.Items.Count == 0, "Punctuation-only search must not match every game");
                filter.Text = " \t\u3000";
                Check(list.Items.Count == 2 && Member<bool>(FindRow(dialog, first), "Checked"),
                    "Clearing the filter must preserve hidden selections");
                Invoke(dialog, "Accept");
                Check(dialog.Selected.Count == 1 && dialog.Selected[0].Exe == first,
                    "Filtering changed the selected add result");
                CheckNoScans(dialog);
            }
        }

        private static void CheckAddGameGpuSelection()
        {
            string a = FixtureRoot + @"\A\Game.exe", b = FixtureRoot + @"\B\Game.exe";
            using (var dialog = new NoScanAddGameDialog())
            {
                Merge(dialog, true, Candidate("A", a, FixtureRoot, 100, 1), Candidate("B", b, FixtureRoot, 200, 1));
                TogglePath(dialog, a); TogglePath(dialog, a);
                Invoke(dialog, "ApplyGpuTags", new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
                    { { a, 20 }, { b, 60 } });
                Check(Member<bool>(FindRow(dialog, b), "RendererLike"), "GPU evidence no longer marks the live recommendation");
                Check(!Member<bool>(FindRow(dialog, a), "Checked") && !Member<bool>(FindRow(dialog, b), "Checked")
                    && !Member<PillButton>(dialog, "btnAdd").Enabled,
                    "Late GPU recommendation overrode an explicit deselection");
                CheckNoScans(dialog);
            }
        }

        private static void CheckAddGameRowLayout()
        {
            var bounds = new Rectangle(Theme.S(7), Theme.S(3), Theme.S(566), Theme.S(52));
            foreach (string status in new[] { Lang.T("scan.running.tag"), Lang.T("scan.already"),
                Lang.F("scan.renderer.tag", 99), "" })
            {
                object layout = typeof(AddGameDialog).GetMethod("LayoutRow", BindingFlags.Static | BindingFlags.NonPublic)
                    .Invoke(null, new object[] { bounds, status, "1.2 GB  " + Lang.F("white.pick.procs", 12) });
                var parts = new List<Rectangle>();
                foreach (string name in new[] { "Check", "Icon", "Name", "Path", "Status", "Details" })
                {
                    Rectangle part = Member<Rectangle>(layout, name);
                    Check(part.Width >= 0 && part.Height >= 0, "Negative unified-row geometry: " + name);
                    if (part.Width == 0 || part.Height == 0) continue;
                    Check(bounds.Contains(part), "Unified-row geometry is clipped: " + name);
                    foreach (Rectangle prior in parts)
                        Check(!prior.IntersectsWith(part), "Unified-row text, status, icon, or checkbox overlap");
                    parts.Add(part);
                }
                Check(Member<Rectangle>(layout, "Icon").Width == Theme.S(26),
                    "Unified list lost the running-picker icon size");
            }
        }

        private static Bitmap FixtureIcon()
        {
            var icon = new Bitmap(26, 26);
            using (Graphics g = Graphics.FromImage(icon)) g.Clear(Color.FromArgb(187, 61, 229));
            return icon;
        }

        private static void CheckDisposed(Bitmap image, string message)
        {
            bool disposed = false;
            try { image.GetPixel(0, 0); }
            catch (ArgumentException) { disposed = true; }
            catch (ObjectDisposedException) { disposed = true; }
            Check(disposed, message);
        }

        private static void CheckAddGameIconLifetime()
        {
            string path = FixtureRoot + @"\Icon\Game.exe";
            Bitmap icon = FixtureIcon();
            using (var dialog = new NoScanAddGameDialog())
            {
                Merge(dialog, true, Candidate("Icon", path, FixtureRoot, 12345, 2));
                Invoke(dialog, "CacheIcon", path, icon);
                ListBox list = Member<ListBox>(dialog, "lst");
                var bounds = new Rectangle(0, 0, Theme.S(566), Theme.S(52));
                object layout = typeof(AddGameDialog).GetMethod("LayoutRow", BindingFlags.Static | BindingFlags.NonPublic)
                    .Invoke(null, new object[] { bounds, "", "" });
                Rectangle iconBounds = Member<Rectangle>(layout, "Icon");
                using (var pixels = new Bitmap(bounds.Width, bounds.Height))
                using (Graphics g = Graphics.FromImage(pixels))
                {
                    Invoke(dialog, "DrawRow", list,
                        new DrawItemEventArgs(g, list.Font, bounds, 0, DrawItemState.None));
                    Check(pixels.GetPixel(iconBounds.Left + iconBounds.Width / 2, iconBounds.Top + iconBounds.Height / 2)
                        .ToArgb() == Color.FromArgb(187, 61, 229).ToArgb(),
                        "Unified running row did not paint its actual process icon");
                }
                Merge(dialog, true, Candidate("Icon", path, FixtureRoot, 23456, 3));
                Check(ReferenceEquals(Member<Dictionary<string, Bitmap>>(dialog, "iconCache")[path], icon),
                    "Repeated refresh discarded a reusable cached icon");
                CheckNoScans(dialog);
                Invoke(dialog, "OnFormClosed", new FormClosedEventArgs(CloseReason.UserClosing));
                Check(Member<Dictionary<string, Bitmap>>(dialog, "iconCache").Count == 0,
                    "Dialog close retained its icon cache");
                CheckDisposed(icon, "Dialog close did not dispose owned icons");
                Bitmap late = FixtureIcon();
                Invoke(dialog, "CacheIcon", path, late);
                CheckDisposed(late, "An icon arriving after close must be disposed even without a UI callback");
                Check(Member<Dictionary<string, Bitmap>>(dialog, "iconCache").Count == 0,
                    "Late icon completion resurrected a closed cache");
            }
            Bitmap disposedIcon = FixtureIcon();
            var disposedDialog = new NoScanAddGameDialog();
            Bitmap replacedIcon = FixtureIcon();
            Invoke(disposedDialog, "CacheIcon", path, replacedIcon);
            Invoke(disposedDialog, "CacheIcon", path, disposedIcon);
            CheckDisposed(replacedIcon, "Replacing a cached icon leaked the previous bitmap");
            disposedDialog.Dispose();
            CheckDisposed(disposedIcon, "Direct Dispose without FormClosed leaked owned icons");
            Check(Member<Dictionary<string, Bitmap>>(disposedDialog, "iconCache").Count == 0,
                "Direct Dispose retained the icon cache");
            Bitmap afterDispose = FixtureIcon();
            Invoke(disposedDialog, "CacheIcon", path, afterDispose);
            CheckDisposed(afterDispose, "An icon arriving after Dispose leaked its bitmap");
        }

        private static void CheckAddGamePostedClose()
        {
            foreach (bool dispose in new[] { false, true })
            using (var dialog = new NoScanAddGameDialog())
            {
                CreateHiddenTree(dialog);
                CheckNoScans(dialog, true);
                int applied = 0;
                Invoke(dialog, "Post", (Action)delegate { applied++; });
                if (dispose) dialog.Dispose();
                else Invoke(dialog, "OnFormClosed", new FormClosedEventArgs(CloseReason.UserClosing));
                Application.DoEvents();
                Check(applied == 0, "A queued scan result changed the dialog after close/disposal");
                Invoke(dialog, "Post", (Action)delegate { applied++; });
                Application.DoEvents();
                Check(applied == 0 && Member<bool>(dialog, "closed"), "Closed dialog accepted a late scan result");
            }
        }

        private static object PickerResult(int generation, params RunningPickerDialog.Entry[] entries)
        {
            Type type = typeof(RunningPickerDialog).GetNestedType("ScanResult", BindingFlags.NonPublic);
            object result = Activator.CreateInstance(type, true);
            type.GetField("Generation").SetValue(result, generation);
            type.GetField("Entries").SetValue(result, entries == null ? null : new List<RunningPickerDialog.Entry>(entries));
            return result;
        }

        private static RunningPickerDialog.Entry PickerEntry(string suffix, Bitmap icon)
        {
            return new RunningPickerDialog.Entry
            {
                Path = FixtureRoot + "\\" + suffix + @"\Game.exe", Title = "Running " + suffix,
                Memory = 1073741824L, Count = 2, Icon = icon
            };
        }

        private static void CheckRunningPickerResults()
        {
            string known = FixtureRoot + @"\Known\Game.exe";
            var exclusions = new HashSet<string> { known.ToUpperInvariant() };
            using (var dialog = new RunningPickerDialog(exclusions))
            {
                exclusions.Clear();
                Check(Member<HashSet<string>>(dialog, "known").Contains(known),
                    "Whitelist picker must copy exclusions and compare paths without case sensitivity");
                CreateHiddenTree(dialog);
                Check(!dialog.Visible && Member<int>(dialog, "scanGeneration") == 0,
                    "Constructing the hidden whitelist picker unexpectedly started discovery");
                var first = PickerEntry("First", FixtureIcon());
                var second = PickerEntry("Second", FixtureIcon());
                object result = PickerResult(0, first, second);
                Check((bool)Invoke(dialog, "QueueScanResult", result), "Owned synthetic picker result was not queued");
                Application.DoEvents();
                Check(Member<object>(dialog, "pendingScan") == null && Member<object>(result, "Entries") == null,
                    "Applying a picker result did not transfer icon ownership out of the pending result");
                Check(Member<IList>(dialog, "all").Count == 2 && Member<IList>(dialog, "shown").Count == 2,
                    "Shared-source picker result did not populate the list");
                Check(!Member<PillButton>(dialog, "confirm").Enabled,
                    "A successful scan enabled whitelist confirmation before any user selection");
                Check(Member<Label>(dialog, "status").Text == Lang.F("white.pick.count", 2),
                    "Whitelist picker lost its own count text after discovery was shared");

                TextBox search = Member<TextBox>(dialog, "search");
                search.Text = "First";
                Check(Member<IList>(dialog, "shown").Count == 1, "Whitelist picker search lost the shared entry");
                ListBox list = Member<ListBox>(dialog, "list");
                Invoke(dialog, "OnListMouseDown", list,
                    new MouseEventArgs(MouseButtons.Left, 1, Theme.S(10), Theme.S(10), 0));
                Check(first.Checked && !second.Checked && Member<PillButton>(dialog, "confirm").Enabled,
                    "Whitelist picker click did not retain an independent checked state");
                using (var pixels = new Bitmap(Theme.S(620), Theme.S(52)))
                using (Graphics graphics = Graphics.FromImage(pixels))
                {
                    Invoke(dialog, "DrawEntry", list, new DrawItemEventArgs(graphics, list.Font,
                        new Rectangle(0, 0, pixels.Width, pixels.Height), 0, DrawItemState.None));
                    Check(pixels.GetPixel(Theme.S(55), Theme.S(26)).ToArgb() == Color.FromArgb(187, 61, 229).ToArgb(),
                        "Whitelist picker stopped painting process icons after source unification");
                }
                search.Text = "";
                Check(first.Checked && Member<IList>(dialog, "shown").Count == 2,
                    "Clearing the whitelist filter lost the pending selection");

                object failed = PickerResult(0, (RunningPickerDialog.Entry[])null);
                Check((bool)Invoke(dialog, "QueueScanResult", failed), "Failure fixture was not queued");
                Application.DoEvents();
                Check(Member<IList>(dialog, "all").Count == 2 && first.Icon != null
                    && Member<Label>(dialog, "status").Text == Lang.T("white.pick.failed"),
                    "Failed shared discovery must preserve prior rows and report failure, not a successful empty result");
                Bitmap previousFirst = first.Icon, previousSecond = second.Icon;
                object empty = PickerResult(0);
                Check((bool)Invoke(dialog, "QueueScanResult", empty), "Empty fixture was not queued");
                Application.DoEvents();
                Check(Member<IList>(dialog, "all").Count == 0 && Member<IList>(dialog, "shown").Count == 0
                    && Member<Label>(dialog, "status").Text == Lang.T("white.pick.none"),
                    "A successful empty snapshot must clear the old list");
                CheckDisposed(previousFirst, "Replacing picker rows leaked the first icon");
                CheckDisposed(previousSecond, "Replacing picker rows leaked the second icon");
                Check(!dialog.Visible && Member<int>(dialog, "scanGeneration") == 0,
                    "Synthetic result handling started real discovery or showed a window");
            }
            using (var dialog = new RunningPickerDialog(null))
            {
                var selected = PickerEntry("Chosen", FixtureIcon());
                CreateHiddenTree(dialog);
                Check((bool)Invoke(dialog, "QueueScanResult", PickerResult(0, selected)),
                    "Confirmation fixture was not queued");
                Application.DoEvents();
                Invoke(dialog, "OnListMouseDown", Member<ListBox>(dialog, "list"),
                    new MouseEventArgs(MouseButtons.Left, 1, Theme.S(10), Theme.S(10), 0));
                Bitmap icon = selected.Icon;
                Invoke(Member<PillButton>(dialog, "confirm"), "OnClick", EventArgs.Empty);
                Check(dialog.Selected.Count == 1 && dialog.Selected[0] == selected.Path,
                    "Whitelist confirmation lost the selected executable after source unification");
                dialog.Dispose();
                CheckDisposed(icon, "Confirmed whitelist picker leaked an adopted icon");
            }
        }

        private static void CheckRunningPickerPendingClose()
        {
            foreach (bool dispose in new[] { false, true })
            using (var dialog = new RunningPickerDialog(null))
            {
                CreateHiddenTree(dialog);
                var pending = PickerEntry("Pending", FixtureIcon());
                Bitmap icon = pending.Icon;
                object result = PickerResult(0, pending);
                Check((bool)Invoke(dialog, "QueueScanResult", result), "Pending cancellation fixture was not queued");
                if (dispose) dialog.Dispose();
                else Invoke(dialog, "OnFormClosed", new FormClosedEventArgs(CloseReason.UserClosing));
                CheckDisposed(icon, "Closing before BeginInvoke dispatch leaked a pending picker icon");
                Application.DoEvents();
                Check(Member<object>(dialog, "pendingScan") == null && Member<IList>(dialog, "all").Count == 0
                    && (bool)Invoke(dialog, "ScanCanceled", 0), "Late picker callback repopulated a closed dialog");
            }
            using (var dialog = new RunningPickerDialog(null))
            {
                CreateHiddenTree(dialog);
                var stale = PickerEntry("Stale", FixtureIcon());
                Bitmap icon = stale.Icon;
                object result = PickerResult(0, stale);
                Check((bool)Invoke(dialog, "QueueScanResult", result), "Stale-generation fixture was not queued");
                SetMember(dialog, "scanGeneration", 1);
                Application.DoEvents();
                CheckDisposed(icon, "Superseded picker generation leaked its icon");
                Check(Member<IList>(dialog, "all").Count == 0 && (bool)Invoke(dialog, "ScanCanceled", 0)
                    && !(bool)Invoke(dialog, "ScanCanceled", 1), "Picker generation cancellation is inconsistent");
            }
            using (var dialog = new RunningPickerDialog(null))
            {
                var rejected = PickerEntry("NoHandle", FixtureIcon());
                Bitmap icon = rejected.Icon;
                Check(!(bool)Invoke(dialog, "QueueScanResult", PickerResult(0, rejected))
                    && Member<object>(dialog, "pendingScan") == null && !dialog.IsHandleCreated,
                    "Failed dispatcher must return result ownership to its worker without creating a window");
                typeof(RunningPickerDialog).GetMethod("DisposeEntries", BindingFlags.Static | BindingFlags.NonPublic)
                    .Invoke(null, new object[] { new[] { rejected } });
                CheckDisposed(icon, "Worker cleanup of a rejected picker result leaked its icon");
            }
        }

        private static void RunSta(string output)
        {
            Directory.CreateDirectory(output);
            float originalScale = Dpi.Scale; int originalLang = Lang.Cur; bool originalLight = Theme.LightMode;
            try
            {
                foreach (float scale in new[] { 1f, 1.25f, 1.5f, 2f, 3f })
                foreach (int language in new[] { 0, 1 })
                foreach (bool light in new[] { false, true })
                {
                    Dpi.Scale = scale; Theme.DropFontCache(); Lang.Cur = language; Theme.SetLight(light);
                    CheckLongResetDialog();
                    CheckAddGameDialog();
                    var emptyConfig = new GameProfile { Name = "Profile" };
                    Check(PanelForm.CfgClearConfirmation(emptyConfig) == null, "Empty profile should not prompt to clear");
                    var familyConfig = MakeItem("02", false, true).Profile;
                    Check(PanelForm.CfgOverrideSummary(familyConfig) == Lang.T("cfg.count.none.family"),
                        "Family-only config must not advertise a nonexistent override target");
                    Check(PanelForm.CfgClearConfirmation(familyConfig) == Lang.F("cfg.clear.family.only", familyConfig.Name),
                        "Family-only profile must remain clearable with explicit protection wording");
                    var mixedConfig = MakeItem("01", true, true).Profile;
                    Check(PanelForm.CfgOverrideSummary(mixedConfig) == Lang.F("cfg.count", 2), "Config summary counted family switch");
                    Check(PanelForm.CfgClearConfirmation(mixedConfig) == Lang.F("cfg.clear.family.confirm", mixedConfig.Name, 2),
                        "Mixed clear must state ordinary count and closing family suppression");
                    mixedConfig.Overrides.Remove(PolicyCatalog.KeySuppressFamily);
                    Check(PanelForm.CfgClearConfirmation(mixedConfig) == Lang.F("cfg.clear.confirm", mixedConfig.Name, 2),
                        "Ordinary-only clear text changed meaning");
                    foreach (int logicalWidth in new[] { 340, 480, 560, 680, 920 })
                    {
                        int width = Theme.S(logicalWidth), height = GameLibraryRowLayout.HeightForWidth(width);
                        GameLibraryRowLayout layout = GameLibraryRowLayout.ForSize(new Size(width, height));
                        var bounds = new Rectangle(0, 0, width, height);
                        Check(bounds.Contains(layout.Policy), "Policy outside row at " + scale + "/" + logicalWidth);
                        Check(bounds.Contains(layout.Name) && bounds.Contains(layout.Path) && bounds.Contains(layout.Metadata), "Text outside row");
                        Check(!layout.Name.IntersectsWith(layout.Policy) && !layout.Path.IntersectsWith(layout.Policy)
                            && !layout.Metadata.IntersectsWith(layout.Policy), "Policy overlaps text");
                        Check(!layout.Icon.IntersectsWith(layout.Name) && !layout.Name.IntersectsWith(layout.Path)
                            && !layout.Path.IntersectsWith(layout.Metadata), "Row text/icon overlaps");
                    }
                    using (var form = new Form { AutoScaleMode = AutoScaleMode.None, BackColor = Theme.Bg,
                        ClientSize = new Size(Theme.S(728), Theme.S(466)) })
                    using (var list = new GameLibraryList())
                    {
                        list.SetBounds(Theme.S(14), Theme.S(14), Theme.S(700), Theme.S(438));
                        form.Controls.Add(list);
                        var first = MakeItem("01", true, false);
                        var second = MakeItem("02", false, false);
                        var third = MakeItem("03", true, true);
                        list.SetItems(new List<GameLibraryItem> { first, second, third });
                        CreateHiddenTree(form); list.PerformLayout();
                        list.SelectedIndex = 1;
                        Check(list.SelectedItem == second, "Selection model mismatch");
                        Check(list.Controls.Count == 3, "Wrong row count");
                        var row = (GameLibraryRow)list.Controls[1];
                        Check(row.FamilySwitch.AccessibleDescription.Contains("Steam")
                            && row.FamilySwitch.AccessibleDescription.Contains("CS:GO"),
                            "Family switch help must include the launcher/game example");
                        Check(row.FamilySwitch is CheckBox && row.FamilySwitch.TabStop
                            && !row.FamilySwitch.AutoCheck, "Switch must remain a native keyboard-accessible checkbox");
                        Check(row.FamilySwitch.AccessibilityObject.Role == AccessibleRole.CheckButton, "Wrong accessibility role");
                        int requests = 0;
                        list.FamilyToggleRequested += delegate(object sender, GameLibraryEventArgs e)
                        {
                            Check(e.Item == second, "Toggle routed to wrong game"); requests++;
                        };
                        typeof(FamilySuppressionSwitch).GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic)
                            .Invoke(row.FamilySwitch, new object[] { new KeyEventArgs(Keys.Enter) });
                        Check(requests == 1 && !row.FamilySwitch.Checked, "Enter must request, not optimistically save");
                        typeof(FamilySuppressionSwitch).GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic)
                            .Invoke(row.FamilySwitch, new object[] { new KeyEventArgs(Keys.Space) });
                        typeof(CheckBox).GetMethod("OnKeyUp", BindingFlags.Instance | BindingFlags.NonPublic)
                            .Invoke(row.FamilySwitch, new object[] { new KeyEventArgs(Keys.Space) });
                        Check(requests == 2 && !row.FamilySwitch.Checked, "Space must request without optimistic state");
                        row.FamilySwitch.AccessibilityObject.DoDefaultAction();
                        Check(requests == 3 && !row.FamilySwitch.Checked, "Accessible default action must request without optimistic state");
                        var refreshed = new List<GameLibraryItem> { MakeItem("01", true, false), MakeItem("02", true, true), MakeItem("03", false, false) };
                        list.SetItems(refreshed);
                        Check(ReferenceEquals(row, list.Controls[1]), "Metadata refresh rebuilt native row");
                        Check(row.FamilySwitch.Checked && list.SelectedIndex == 1, "Saved state/selection not reflected");
                        Check(GameLibraryRow.OrdinaryOverrideCount(row.Item.Profile) == 0, "Family toggle leaked into ordinary override badge");
                        Check(GameLibraryRow.OrdinaryOverrideCount(refreshed[0].Profile) == 2, "Ordinary override count lost");
                        Check((row.FamilySwitch.AccessibilityObject.State & AccessibleStates.Checked) != 0, "Checked accessibility state missing");
                        int deleteRequests = 0, activations = 0;
                        list.KeyDown += delegate(object sender, KeyEventArgs e)
                        {
                            if (e.KeyCode == Keys.Delete) deleteRequests++;
                        };
                        list.ItemActivated += delegate { activations++; };
                        typeof(FamilySuppressionSwitch).GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic)
                            .Invoke(row.FamilySwitch, new object[] { new KeyEventArgs(Keys.Delete) });
                        Check(deleteRequests == 1 && list.SelectedIndex == 1, "Delete no longer reaches the selected library entry");
                        typeof(GameLibraryRow).GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic)
                            .Invoke(row, new object[] { new KeyEventArgs(Keys.Enter) });
                        Check(activations == 1, "Enter no longer opens per-game configuration");
                        typeof(GameLibraryRow).GetMethod("OnDoubleClick", BindingFlags.Instance | BindingFlags.NonPublic)
                            .Invoke(row, new object[] { EventArgs.Empty });
                        Check(activations == 2 && list.SelectedIndex == 1, "Double-click no longer opens the correct entry");
                        var down = new KeyEventArgs(Keys.Down);
                        typeof(GameLibraryRow).GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(row, new object[] { down });
                        Check(down.Handled && list.SelectedIndex == 2, "Arrow navigation broken");
                        if (scale == 1f)
                            using (var bitmap = new Bitmap(form.Width, form.Height))
                            {
                                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                                bitmap.Save(Path.Combine(output, "library-" + (light ? "light" : "dark") + "-" + language + ".png"));
                            }
                        var many = new List<GameLibraryItem>();
                        for (int i = 0; i < 20; i++) many.Add(MakeItem(i.ToString("00"), i % 2 == 0, i % 3 == 0));
                        list.SetItems(many); CreateHiddenTree(list); list.PerformLayout();
                        Check(list.VerticalScroll.Visible, "Long library must scroll");
                        foreach (Control card in list.Controls)
                            Check(card.Right <= list.ClientSize.Width, "Scroll bar clips policy control");
                        list.AutoScrollPosition = new Point(0, list.AutoScrollMinSize.Height);
                        Check(list.Controls[0].Top < 0 && list.Controls[list.Controls.Count - 1].Bottom <= list.ClientSize.Height,
                            "Last row unreachable through vertical scroll");
                        list.AutoScrollPosition = Point.Empty;
                        Check(list.Controls[0].Top == 0, "Scroll return corrupted row location");
                        list.Width = Theme.S(480); list.PerformLayout();
                        Check(list.Controls[0].Height == Theme.S(184), "Narrow library did not stack policy below metadata");
                        if (scale == 1f)
                            using (var bitmap = new Bitmap(list.Width, list.Height))
                            {
                                list.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                                bitmap.Save(Path.Combine(output, "library-narrow-" + (light ? "light" : "dark") + "-" + language + ".png"));
                            }
                    }
                    using (var warning = new FamilySuppressionDialog("Aurora & Beyond", @"D:\Games\Launcher\Entry.exe"))
                    {
                        string warningBody = Lang.T("lib.family.warning.body");
                        Check(warningBody.Contains("League of Legends.exe")
                            && warningBody.Contains(language == 0 ? "每次开启" : "every time")
                            && warning.Text.Contains(language == 0 ? "不建议开启" : "not recommended"),
                            "Every-enable warning must discourage suppression and explain the LoL exception");
                        Check(!warningBody.Contains(language == 0 ? "暂未确认" : "has not confirmed"),
                            "Warning must not claim an observed renderer is unconfirmed");
                        Check(warningBody.Contains("Steam.exe") && warningBody.Contains("CS:GO"),
                            "Family warning must explain choosing the game EXE, not the launcher");
                        Label warningMessage = null;
                        foreach (Control control in warning.Controls)
                            if (control is Label && control.Text == warningBody) warningMessage = (Label)control;
                        Check(warningMessage != null && warningMessage.Bottom < warning.EnableAnyway.Top,
                            "Family warning example overlaps its action buttons");
                        int requiredBodyHeight = TextRenderer.MeasureText(warningBody, warningMessage.Font,
                            new Size(warningMessage.Width, int.MaxValue), TextFormatFlags.WordBreak).Height;
                        Check(requiredBodyHeight <= warningMessage.Height,
                            "Family warning example is clipped");
                        Check(warning.AcceptButton == warning.CancelButton && warning.AcceptButton.DialogResult == DialogResult.Cancel,
                            "Default dialog action must keep suppression off");
                        Check(warning.EnableAnyway.DialogResult == DialogResult.OK, "Explicit opt-in missing");
                        Check(warning.ExecutableBox.ReadOnly && warning.ExecutableBox.Multiline
                            && warning.ExecutableBox.Text.EndsWith("Entry.exe"), "Full EXE unavailable to copy");
                        Check(warning.ClientRectangle.Contains(warning.EnableAnyway.Bounds), "Dialog buttons clipped");
                        if (scale == 1f)
                            using (var bitmap = new Bitmap(warning.Width, warning.Height))
                            {
                                CreateHiddenTree(warning);
                                warning.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                                bitmap.Save(Path.Combine(output, "warning-" + (light ? "light" : "dark") + "-" + language + ".png"));
                            }
                    }
                }
            }
            finally { Dpi.Scale = originalScale; Lang.Cur = originalLang; Theme.SetLight(originalLight); Theme.DropFontCache(); }
        }
    }
#if PAVISE_LIBRARY_BENCH && PAVISE_SELFTEST
    internal static partial class SelfTests
    {
        public static bool TryHandleRuntimeMode(string[] args)
        { throw new InvalidOperationException("Library UI bench cannot start the application runtime"); }
    }
#endif
}
#endif
