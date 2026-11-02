#if PAVISE_SELFTEST && PAVISE_LIBRARY_CARD_MENU
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PaviseApp
{
    internal static class LibraryCardMenuChecks
    {
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        private static int assertions, nativeMenus, actions, oldGlyphFailures;
        private static string scenario;
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr handle);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr handle);
        [DllImport("user32.dll")] private static extern IntPtr GetCapture();

        private static void Check(bool value, string reason)
        { if (!value) throw new InvalidOperationException(scenario + ": " + reason); assertions++; }
        private static void Pump() { for (int i = 0; i < 3; i++) Application.DoEvents(); }
        private static ContextMenuStrip Owned(GameCardMenuButton button)
        { return button.OwnedMenu; }
        private static void Invoke(Control control, string method, object argument)
        { control.GetType().GetMethod(method, Hidden).Invoke(control, new[] { argument }); }
        private static void CreateHiddenTree(Control control)
        {
            typeof(Control).GetMethod("CreateControl", Hidden, null, new[] { typeof(bool) }, null)
                .Invoke(control, new object[] { true });
            Check(!IsWindowVisible(control.Handle), "A test control became visible");
            Check(GetForegroundWindow() != control.Handle, "A test control became foreground");
            foreach (Control child in control.Controls) CreateHiddenTree(child);
        }

        private static IntPtr ArmHiddenMenu(ContextMenuStrip menu)
        {
            Check(menu != null && !menu.IsDisposed, "No live menu was created");
            menu.Location = new Point(-32000, -32000);
            IntPtr handle = menu.Handle; menu.PerformLayout();
            Check(IsWindow(handle) && !IsWindowVisible(handle), "Native menu was not hidden");
            Check(GetForegroundWindow() != handle, "A native menu became foreground");
            SetManagedVisible(menu);
            Check(menu.Visible && !IsWindowVisible(handle), "Arming lifecycle displayed a popup");
            nativeMenus++; return handle;
        }
        private static void SetManagedVisible(Control control)
        {
            MethodInfo setter = typeof(Control).GetMethod("SetState", Hidden);
            Type flagType = setter.GetParameters()[0].ParameterType;
            object flag = flagType.IsEnum ? Enum.Parse(flagType, "Visible")
                : typeof(Control).GetField("STATE_VISIBLE", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public).GetRawConstantValue();
            setter.Invoke(control, new[] { flag, (object)true });
        }
        private static void ClickItem(ContextMenuStrip menu, int index)
        {
            Rectangle bounds = menu.Items[index].Bounds;
            Check(bounds.Width > 0 && bounds.Height > 0, "Menu item has no hit-test area");
            var mouse = new MouseEventArgs(MouseButtons.Left, 1, bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2, 0);
            typeof(ToolStrip).GetMethod("OnMouseDown", Hidden).Invoke(menu, new object[] { mouse });
            typeof(ToolStrip).GetMethod("OnMouseUp", Hidden).Invoke(menu, new object[] { mouse });
        }
        private static void CheckClosed(GameCardMenuButton button, ContextMenuStrip menu, IntPtr handle)
        {
            Check(Owned(button) == null && !button.MenuVisible, "The button retained a closing menu");
            Check(!menu.IsDisposed && menu.IsHandleCreated && !menu.Visible && !IsWindowVisible(handle),
                "Menu was destroyed inside WinForms' click/close stack");
            Check(GetForegroundWindow() != handle, "A closing menu became foreground");
            Pump();
            Check(menu.IsDisposed && !IsWindow(handle), "Deferred disposal leaked the menu HWND");
        }
        private static ContextMenuStrip Menu(Action<int> action)
        {
            var menu = new ContextMenuStrip { AutoSize = false, Size = new Size(Theme.S(230), Theme.S(120)),
                Padding = new Padding(Theme.S(6)), ShowImageMargin = false, DropShadowEnabled = false,
                Renderer = new TechMenuRenderer(), Font = Theme.UI(9f, false) };
            string[] labels = { Lang.T("lib.rename"), Lang.T("v15.library.force"), Lang.T("btn.remove") };
            for (int i = 0; i < labels.Length; i++)
            {
                int index = i;
                var item = new ToolStripMenuItem(labels[i]) { AutoSize = false,
                    Size = new Size(menu.Width - menu.Padding.Horizontal, Theme.S(36)), Margin = Padding.Empty };
                item.Click += delegate { action(index); }; menu.Items.Add(item);
            }
            menu.PerformLayout(); return menu;
        }
        private static GameLibraryItem Item(string id)
        { return new GameLibraryItem(new GameProfile { Id = id, Name = "Aurora 星际远征 " + id, ExecutablePath = @"Z:\Pavise-Fixture\" + id + ".exe" }, false, false); }

        private static void CheckCardActions()
        {
            using (var form = new Form { AutoScaleMode = AutoScaleMode.None, ShowInTaskbar = false,
                StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000), ClientSize = new Size(Theme.S(740), Theme.S(480)) })
            using (var list = new GameLibraryList { Bounds = new Rectangle(0, 0, Theme.S(700), Theme.S(430)) })
            {
                form.Controls.Add(list);
                GameLibraryItem first = Item("first"), second = Item("second");
                list.SetItems(new List<GameLibraryItem> { first, second }); CreateHiddenTree(form);
                list.SelectedIndex = 0;
                var row = (GameLibraryRow)list.Controls[1];
                var button = row.MoreButton as GameCardMenuButton;
                Check(button != null && button.TabStop && button.AccessibleName.Contains(second.Profile.Name), "Card did not use its accessible menu button");
                int opened = 0, invoked = 0;
                list.MoreRequested += delegate(object sender, GameLibraryEventArgs args) {
                    Check(args.Item == second && args.Anchor == button, "Menu targeted the selected card instead of its own card"); opened++;
                    button.ShowMenu(Menu(delegate(int choice) {
                        invoked++; actions++;
                        if (choice == 0) second.Profile.Name = "Renamed fixture";
                        else if (choice == 1) second.Profile.ForceTrigger = !second.Profile.ForceTrigger;
                        else list.SetItems(new List<GameLibraryItem> { first });
                    }));
                };
                for (int cycle = 0; cycle < 9; cycle++)
                {
                    if (cycle % 3 == 0) Invoke(button, "OnKeyDown", new KeyEventArgs(Keys.Enter));
                    else if (cycle % 3 == 1) Invoke(button, "OnKeyDown", new KeyEventArgs(Keys.Space));
                    else button.PerformClick();
                    Check(opened == cycle + 1 && list.SelectedItem == first, "Opening menu changed selection or opened twice");
                    ContextMenuStrip menu = Owned(button); IntPtr handle = ArmHiddenMenu(menu);
                    int choice = cycle == 8 ? 2 : cycle % 2;
                    ClickItem(menu, choice);
                    Check(invoked == cycle + 1, "Menu callback was missing or ran twice");
                    CheckClosed(button, menu, handle);
                    if (choice == 0) Check(second.Profile.Name == "Renamed fixture", "Rename action did not target the card");
                }
                Check(button.IsDisposed && list.Items.Count == 1 && list.SelectedItem == first,
                    "Removing the menu's own card broke selection or owner disposal");
            }
        }

        private static void CheckToggling()
        {
            using (var form = new Form { AutoScaleMode = AutoScaleMode.None, ShowInTaskbar = false,
                StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000) })
            using (var parent = new Panel { Bounds = new Rectangle(0, 0, 220, 120) })
            using (var button = new GameCardMenuButton { Bounds = new Rectangle(20, 20, Theme.S(28), Theme.S(22)) })
            {
                form.Controls.Add(parent); parent.Controls.Add(button); CreateHiddenTree(form); SetManagedVisible(form);
                int opened = 0;
                button.Click += delegate { opened++; button.ShowMenu(Menu(delegate { actions++; })); };
                for (int cycle = 0; cycle < 12; cycle++)
                {
                    scenario = "toggle " + cycle + " dpi=" + Dpi.Scale;
                    int before = opened; button.PerformClick();
                    ContextMenuStrip menu = Owned(button); IntPtr handle = ArmHiddenMenu(menu);
                    if (cycle % 3 == 0) button.PerformClick();
                    else if (cycle % 3 == 1)
                    {
                        Invoke(button, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, 8, 8, 0));
                        menu.Close(ToolStripDropDownCloseReason.AppClicked); button.PerformClick();
                        Invoke(button, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1, 8, 8, 0));
                    }
                    else
                    {
                        Message message = Message.Create(button.Handle, 0x100, (IntPtr)Keys.F4, IntPtr.Zero);
                        object result = typeof(GameCardMenuButton).GetMethod("ProcessCmdKey", Hidden)
                            .Invoke(button, new object[] { message, Keys.F4 });
                        Check((bool)result, "F4 was not consumed");
                    }
                    Check(opened == before + 1, "Second owner click reopened the popup: cycle=" + cycle + " before=" + before + " opened=" + opened);
                    CheckClosed(button, menu, handle);
                }
                Action[] close = {
                    delegate { Owned(button).Close(ToolStripDropDownCloseReason.Keyboard); },
                    delegate { Owned(button).Close(ToolStripDropDownCloseReason.AppClicked); },
                    null,
                    delegate { button.Enabled = false; },
                    delegate { button.Left++; },
                    delegate { button.Width++; },
                    delegate { parent.Left++; },
                    delegate { parent.Width++; },
                    delegate { parent.Visible = false; },
                    delegate { Invoke(parent, "OnScroll", new ScrollEventArgs(ScrollEventType.ThumbTrack, 30)); },
                    delegate { parent.Controls.Remove(button); },
                    delegate { button.Dispose(); }
                };
                for (int index = 0; index < close.Length; index++)
                {
                    scenario = "context " + index + " dpi=" + Dpi.Scale;
                    button.PerformClick(); ContextMenuStrip menu = Owned(button); IntPtr handle = ArmHiddenMenu(menu);
                    if (index == 2)
                    {
                        Message message = Message.Create(button.Handle, 0x100, (IntPtr)Keys.Escape, IntPtr.Zero);
                        object result = typeof(GameCardMenuButton).GetMethod("ProcessCmdKey", Hidden)
                            .Invoke(button, new object[] { message, Keys.Escape });
                        Check((bool)result, "Escape was not consumed");
                    }
                    else close[index]();
                    CheckClosed(button, menu, handle);
                    if (!button.IsDisposed) { button.Enabled = true; parent.Visible = true; if (button.Parent == null) parent.Controls.Add(button); }
                }
            }
        }

        private static Rectangle Ink(Bitmap bitmap, Color color, out int groups)
        {
            int left = bitmap.Width, right = -1, top = bitmap.Height, bottom = -1;
            groups = 0; bool previous = false;
            for (int x = Theme.S(4); x < bitmap.Width - Theme.S(4); x++)
            {
                bool column = false;
                for (int y = Theme.S(4); y < bitmap.Height - Theme.S(4); y++)
                {
                    Color value = bitmap.GetPixel(x, y);
                    if (Math.Abs(value.R - color.R) > 6 || Math.Abs(value.G - color.G) > 6 || Math.Abs(value.B - color.B) > 6) continue;
                    left = Math.Min(left, x); right = Math.Max(right, x); top = Math.Min(top, y); bottom = Math.Max(bottom, y); column = true;
                }
                if (column && !previous) groups++; previous = column;
            }
            return right < left ? Rectangle.Empty : Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
        }
        private static bool Centered(Rectangle ink, Size size)
        { return !ink.IsEmpty && Math.Abs(ink.Left + ink.Width / 2f - size.Width / 2f) <= 1f && Math.Abs(ink.Top + ink.Height / 2f - size.Height / 2f) <= 1f; }
        private static void CheckGlyph(string output)
        {
            Size size = new Size(Theme.S(28), Theme.S(22));
            using (var button = new GameCardMenuButton { Size = size })
            using (var old = new PillButton("⋯") { Size = size, Font = Theme.UI(13f, true) })
            using (var fixedImage = new Bitmap(size.Width, size.Height))
            using (var oldImage = new Bitmap(size.Width, size.Height))
            {
                button.DrawToBitmap(fixedImage, button.ClientRectangle); old.DrawToBitmap(oldImage, old.ClientRectangle);
                int groups, oldGroups; Rectangle ink = Ink(fixedImage, Theme.Fg, out groups), oldInk = Ink(oldImage, Theme.Fg, out oldGroups);
                Check(groups == 3 && Centered(ink, size), "Three dots were not centered at scale " + Dpi.Scale + " light=" + Theme.LightMode + " ink=" + ink);
                if (oldGroups != 3 || !Centered(oldInk, size)) oldGlyphFailures++;
                string name = "dots-" + (int)(Dpi.Scale * 100) + "-" + (Theme.LightMode ? "light" : "dark");
                fixedImage.Save(Path.Combine(output, name + "-fixed.png")); oldImage.Save(Path.Combine(output, name + "-before.png"));
                Console.WriteLine(name + " before=" + oldInk + " fixed=" + ink);
            }
        }

        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                if (args.Length != 1) return 2;
                Console.OutputEncoding = new System.Text.UTF8Encoding(false);
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
                Settings.UseTransientStoreForCurrentProcess(); Logger.ResetWriteBarrierForTest(); Logger.WritesEnabled = false;
                Dpi.Init(); Lang.Init(); Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                string output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
                foreach (float scale in new[] { 1f, 1.25f, 1.5f, 2f, 3f })
                foreach (bool light in new[] { false, true })
                {
                    Dpi.Scale = scale; Theme.DropFontCache(); Theme.SetLight(light);
                    CheckGlyph(output); CheckToggling();
                    foreach (int language in new[] { 0, 1 }) { Lang.Cur = language; CheckCardActions(); }
                }
                Check(oldGlyphFailures > 0, "Old glyph problem was not reproduced");
                Check(GetCapture() == IntPtr.Zero, "The fixture retained native mouse capture: " + GetCapture());
                Console.WriteLine("PASS library card menu assertions=" + assertions + " hidden_native_menus=" + nativeMenus + " callbacks=" + actions
                    + " old_glyph_failures=" + oldGlyphFailures + " dpi=100,125,150,200,300 application_started=false windows_shown=false fixture_foreground=false cursor_input_sent=false settings=transient");
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
    }
    internal static partial class SelfTests
    {
        public static bool TryHandleRuntimeMode(string[] args)
        { throw new InvalidOperationException("Card menu checks cannot start the application runtime"); }
    }
}
#endif
