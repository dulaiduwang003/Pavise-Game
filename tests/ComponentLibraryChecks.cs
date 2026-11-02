#if PAVISE_SELFTEST && PAVISE_COMPONENT_LIBRARY
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PaviseApp
{
    // 组件库隔离回归：事件次数、键盘访问、禁用/忙碌门禁、主题与 DPI 几何、弹出层边界、对话框门禁。
    internal static class ComponentLibraryChecks
    {
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        private static int checks;
        private static string scenario = "";
        private static string output = "";
        private static float dpi;
        private static bool lightMode;
        private static int language;

        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr handle);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();

        internal static int Run(string outputDir, float scale, bool light, int lang)
        {
            output = outputDir; dpi = scale; lightMode = light; language = lang;
            int before = checks;
            bool suspended = UiClock.Suspended, frozen = UiClock.Frozen;
            UiClock.Suspended = true; UiClock.Frozen = false;
            DsFocus.ForceCuesForTest = false;
            try
            {
                Scenario("tokens"); CheckTokens();
                Scenario("keys"); CheckKeys();
                Scenario("popup-math"); CheckPopupMath();
                Scenario("surfaces"); CheckSurfaces();
                Scenario("badges"); CheckBadges();
                using (var host = new ComponentLibraryHost())
                {
                    CreateHiddenTree(host);
                    // 托管可见标志让子控件的 Visible 为真，但窗口句柄从不显示。
                    SetManagedVisible(host);
                    Check(host.Visible && !IsWindowVisible(host.Handle), "Host form became visible");
                    Scenario("buttons"); CheckButtons(host);
                    Scenario("toggle"); CheckToggle(host);
                    Scenario("card"); CheckCard(host);
                    Scenario("tier"); CheckTierPicker(host);
                    Scenario("tabs"); CheckTechTabs(host);
                    Scenario("dropdown"); using (var select = new TechSelect()) CheckDropdown(host, select);
                    Scenario("card-menu"); CheckCardMenuButton(host);
                    Scenario("quantum-dropdown"); checks += QuantumComponentChecks.Run(host);
                    Scenario("maintenance-dialog"); CheckMaintenanceDialog(host);
                    Scenario("showcase"); CheckShowcase(host);
                    if (lang == 0 && (scale == 1f || scale == 2f)) { Scenario("dialog"); CheckDialog(host); }
                    Check(GetForegroundWindow() != host.Handle && !IsWindowVisible(host.Handle), "Host form became foreground or visible");
                }
            }
            finally
            {
                DsFocus.ForceCuesForTest = false;
                UiClock.Running = false;
                UiClock.Suspended = suspended; UiClock.Frozen = frozen;
            }
            return checks - before;
        }

        private static void Scenario(string name)
        {
            scenario = name + " dpi=" + dpi + " light=" + lightMode + " lang=" + language;
        }

        private static void Check(bool value, string reason)
        {
            if (!value) throw new InvalidOperationException(scenario + ": " + reason);
            checks++;
        }

        private static object Call(object host, string name, params object[] args)
        {
            for (Type type = host.GetType(); type != null; type = type.BaseType)
            {
                MethodInfo method = type.GetMethod(name, Hidden | BindingFlags.Public | BindingFlags.DeclaredOnly);
                if (method == null) continue;
                try { return method.Invoke(host, args); }
                catch (TargetInvocationException error) { throw new InvalidOperationException(scenario + ": " + name, error.InnerException); }
            }
            throw new MissingMethodException(host.GetType().Name, name);
        }

        private static T Field<T>(object host, string name)
        {
            for (Type type = host.GetType(); type != null; type = type.BaseType)
            {
                FieldInfo field = type.GetField(name, Hidden | BindingFlags.DeclaredOnly);
                if (field != null) return (T)field.GetValue(host);
            }
            throw new MissingFieldException(host.GetType().Name, name);
        }

        private static void Pump() { for (int i = 0; i < 3; i++) Application.DoEvents(); }

        private static void PumpUntil(Func<bool> condition, string reason)
        {
            DateTime limit = DateTime.UtcNow.AddSeconds(8);
            while (!condition())
            {
                if (DateTime.UtcNow > limit) throw new InvalidOperationException(scenario + ": timeout " + reason);
                Application.DoEvents();
                System.Threading.Thread.Sleep(5);
            }
        }

        private static EventHandler FrameHandlers()
        {
            return (EventHandler)typeof(UiClock).GetField("Frame", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        }

        private static int Listeners()
        {
            EventHandler handler = FrameHandlers();
            return handler == null ? 0 : handler.GetInvocationList().Length;
        }

        private static void Frame()
        {
            EventHandler handler = FrameHandlers();
            if (handler != null) handler(null, EventArgs.Empty);
        }

        private static void CreateHiddenTree(Control control)
        {
            typeof(Control).GetMethod("CreateControl", Hidden, null, new[] { typeof(bool) }, null)
                .Invoke(control, new object[] { true });
            Check(!IsWindowVisible(control.Handle), "A fixture control became visible: " + control.GetType().Name);
            foreach (Control child in control.Controls) CreateHiddenTree(child);
        }

        private static void SetManagedVisible(Control control)
        {
            MethodInfo setter = typeof(Control).GetMethod("SetState", Hidden);
            Type flagType = setter.GetParameters()[0].ParameterType;
            object flag = flagType.IsEnum ? Enum.Parse(flagType, "Visible")
                : typeof(Control).GetField("STATE_VISIBLE", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public).GetRawConstantValue();
            setter.Invoke(control, new[] { flag, (object)true });
        }

        private static Bitmap Capture(Control control)
        {
            var bitmap = new Bitmap(Math.Max(1, control.Width), Math.Max(1, control.Height));
            control.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
            return bitmap;
        }

        private static bool SamePicture(Bitmap a, Bitmap b)
        {
            if (a.Size != b.Size) return false;
            var rect = new Rectangle(Point.Empty, a.Size);
            BitmapData left = a.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                BitmapData right = b.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    var l = new byte[a.Width * 4]; var r = new byte[b.Width * 4];
                    for (int y = 0; y < a.Height; y++)
                    {
                        Marshal.Copy(new IntPtr(left.Scan0.ToInt64() + y * left.Stride), l, 0, l.Length);
                        Marshal.Copy(new IntPtr(right.Scan0.ToInt64() + y * right.Stride), r, 0, r.Length);
                        for (int i = 0; i < l.Length; i++) if (l[i] != r[i]) return false;
                    }
                    return true;
                }
                finally { b.UnlockBits(right); }
            }
            finally { a.UnlockBits(left); }
        }

        private static bool Near(Color value, Color target, int tolerance)
        {
            return Math.Abs(value.R - target.R) <= tolerance && Math.Abs(value.G - target.G) <= tolerance
                && Math.Abs(value.B - target.B) <= tolerance;
        }

        private static int CountNear(Bitmap bitmap, Color target, int tolerance)
        {
            int count = 0;
            for (int y = 0; y < bitmap.Height; y++)
                for (int x = 0; x < bitmap.Width; x++)
                    if (Near(bitmap.GetPixel(x, y), target, tolerance)) count++;
            return count;
        }

        private static int ChangedBottomPixels(Bitmap first, Bitmap second)
        {
            int changed = 0;
            for (int y = Math.Max(0, first.Height - Theme.S(6)); y < first.Height; y++)
                for (int x = 0; x < first.Width; x++)
                    if (first.GetPixel(x, y) != second.GetPixel(x, y)) changed++;
            return changed;
        }

        // 极小尺寸绘制：包括共享卡片底在内，布局过程收缩到 1x1 也不抛异常。
        private static void PaintsAtTinySizes(Control control)
        {
            Size saved = control.Size;
            var sizes = new List<Size> { new Size(2, 2), new Size(5, 3), new Size(40, 6), new Size(6, 40) };
            sizes.Insert(0, new Size(1, 1));
            foreach (Size size in sizes)
            {
                control.Size = size;
                using (Bitmap picture = Capture(control))
                    Check(picture.Width == Math.Max(1, size.Width), "Tiny paint lost its surface: " + control.GetType().Name);
            }
            control.Size = saved;
        }

        private static void Save(Bitmap bitmap, string name)
        {
            bitmap.Save(Path.Combine(output, name + "-" + (int)(dpi * 100) + "-" + (lightMode ? "light" : "dark") + "-" + language + ".png"), ImageFormat.Png);
        }

        private static void CheckTokens()
        {
            Check(DsSpace.Px(DsSpace.M) == Theme.S(8) && DsSpace.Px(DsSpace.XL) == Theme.S(16), "Spacing scale does not follow DPI");
            Check(DsSpace.Line(1) >= 1f && DsSpace.Line(2) == Math.Max(1f, Theme.S(2)), "Line width token lost its minimum");
            Check(ReferenceEquals(DsType.Font(DsText.Body), Theme.UI(9f, false)), "Body font differs from the theme font cache");
            Check(ReferenceEquals(DsType.Font(DsText.Title, true), Theme.UI(9.75f, true)), "Title font differs from the theme font cache");
            Check(ReferenceEquals(DsType.Mono(DsMono.Code), Theme.Mono(5.5f)), "Code font differs from the theme mono cache");
            Check(ReferenceEquals(DsType.MonoFor(DsMono.Badge, "中文"), Theme.MonoFor("中文", 7f)), "CJK badge font did not fall back to the UI face");
            Check(DsType.LineHeight(DsText.Description) > 0, "Description line height is empty");
            Check(DsPalette.Ink(DsTone.Danger) == Theme.Danger && DsPalette.Ink(DsTone.Success) == Theme.Green
                && DsPalette.Ink(DsTone.Warning) == Theme.Warning && DsPalette.Ink(DsTone.Neutral) == Theme.Fg,
                "Tone palette does not follow the theme");
            PerformancePreset savedMode = Theme.CurrentMode;
            try
            {
                Theme.SetMode(PerformancePreset.Competitive, false);
                Check(DsPalette.Ink(DsTone.Accent) == Theme.ModeColor(PerformancePreset.Competitive), "Accent tone ignored the mode color");
                Theme.SetMode(PerformancePreset.Extreme, false);
                Check(DsPalette.Ink(DsTone.Accent) == Theme.ModeColor(PerformancePreset.Extreme), "Accent tone did not follow a mode switch");
            }
            finally { Theme.SetMode(savedMode, false); }
            Check(DsAlpha.VeilTier == (Theme.LightMode ? DsAlpha.VeilTierLight : DsAlpha.VeilTierDark), "Tier veil alpha ignores the theme");
            Check(SettingCard.StatusLineH == DsMetrics.Card.StatusLine && SettingCard.StatusLineH == 17, "Card status line token drifted");
            Check(DsMetrics.Button.Height == 34 && DsMetrics.Toggle.Width == 46 && DsMetrics.Toggle.Height == 30
                && DsMetrics.Tabs.TabWidth == 122 && DsMetrics.Dialog.Width == 468, "Component metrics drifted from the shipped geometry");
            foreach (bool navigation in new[] { true, false })
            {
                DsSurfaceProfile profile = DsSurfaces.Profile(navigation);
                Check(profile.BusLevels.Length == profile.BusReach.Length && profile.BusLevels.Length > 0, "Surface bus levels and reaches mismatch");
                Check(profile.MeshAlpha > 0 && profile.EdgeAlpha > profile.FaceAlpha, "Surface alphas are not ordered");
            }
            Check(DsPaint.GradientRect(new Rectangle(3, 4, 0, -2)) == new Rectangle(3, 4, 1, 1), "Gradient rectangle guard failed");
        }

        private static void CheckKeys()
        {
            Check(DsKeys.IsActivation(Keys.Enter) && DsKeys.IsActivation(Keys.Space) && !DsKeys.IsActivation(Keys.Shift | Keys.Enter),
                "Activation keys must match exactly");
            Check(DsKeys.IsActivationInput(Keys.Shift | Keys.Enter) && !DsKeys.IsActivationInput(Keys.Tab), "Masked activation input failed");
            var handled = new KeyEventArgs(Keys.Enter); handled.Handled = true;
            Check(DsKeys.IsPlainActivation(new KeyEventArgs(Keys.Space)) && !DsKeys.IsPlainActivation(handled)
                && !DsKeys.IsPlainActivation(new KeyEventArgs(Keys.Control | Keys.Enter)) && !DsKeys.IsPlainActivation(null),
                "Plain activation ignores handled or modified keys");
            Check(DsKeys.NavigateHorizontal(Keys.Left, 0, 3) == 0 && DsKeys.NavigateHorizontal(Keys.Right, 2, 3) == 2
                && DsKeys.NavigateHorizontal(Keys.Right, 0, 3) == 1 && DsKeys.NavigateHorizontal(Keys.Home, 2, 3) == 0
                && DsKeys.NavigateHorizontal(Keys.End, 0, 3) == 2 && DsKeys.NavigateHorizontal(Keys.Up, 1, 3) == -1
                && DsKeys.NavigateHorizontal(Keys.Right, 1, 0) == -1 && DsKeys.NavigateHorizontal(Keys.Left, 9, 3) == 1,
                "Horizontal navigation math failed");
            Check(DsKeys.IsTab(Keys.Tab) && DsKeys.IsTab(Keys.Shift | Keys.Tab) && !DsKeys.IsTab(Keys.Control | Keys.Tab), "Tab detection failed");
            Check(DsKeys.IsDropdownOpen(Keys.F4, false) && DsKeys.IsDropdownOpen(Keys.Alt | Keys.Down, true)
                && DsKeys.IsDropdownOpen(Keys.Down, false) && !DsKeys.IsDropdownOpen(Keys.Down, true)
                && DsKeys.IsDropdownOpen(Keys.Apps, true) && !DsKeys.IsDropdownOpen(Keys.Apps, false)
                && DsKeys.IsDropdownOpen(Keys.Shift | Keys.F10, true) && DsKeys.IsDropdownOpen(Keys.Space, false)
                && !DsKeys.IsDropdownOpen(Keys.Escape, false), "Dropdown open key sets diverged");
            Check(DsFocus.Wants(true, true, true) && !DsFocus.Wants(false, true, true) && !DsFocus.Wants(true, false, true)
                && !DsFocus.Wants(true, true, false), "Focus cue decision table failed");
            DsFocus.ForceCuesForTest = true;
            Check(DsFocus.Wants(false, false, true) && !DsFocus.Wants(false, false, false), "Forced cues must still require interactivity");
            DsFocus.ForceCuesForTest = false;
            Check(DsFocus.Inset(new Rectangle(0, 0, 4, 4), 2).IsEmpty && !DsFocus.Inset(new Rectangle(0, 0, 60, 30), 2).IsEmpty,
                "Focus inset guard failed");
            var state = new DsState(); state.Enabled = false; state.Hover = 1f;
            Check(state.HoverIfEnabled == 0f && !state.Interactive, "Visual state helpers ignore Enabled");
            state.Enabled = true; state.Busy = true;
            Check(state.HoverIfEnabled == 1f && !state.Interactive, "Busy state must not be interactive");
        }

        private static void CheckPopupMath()
        {
            foreach (float scale in new[] { 1f, 1.5f, 2f, 3f })
            {
                var work = new Rectangle(0, 0, (int)(1920 * scale), (int)(1080 * scale));
                var size = new Size((int)(220 * scale), (int)(140 * scale));
                int gap = (int)(4 * scale), edge = (int)(6 * scale);
                Rectangle[] anchors = {
                    new Rectangle(10, 10, 100, 30), new Rectangle(work.Right - 40, 10, 100, 30),
                    new Rectangle(10, work.Bottom - 20, 100, 30), new Rectangle(work.Right - 40, work.Bottom - 20, 100, 30),
                    new Rectangle(-50, -50, 100, 30), new Rectangle(work.Width / 2, work.Height / 2, 100, 30)
                };
                foreach (Rectangle anchor in anchors)
                foreach (bool alignRight in new[] { false, true })
                {
                    Rectangle placed = DsPopup.Place(anchor, size, work, gap, edge, alignRight);
                    Check(work.Contains(placed), "Popup left the work area: anchor=" + anchor + " scale=" + scale + " placed=" + placed);
                    Check(placed.Size == size, "Popup placement changed the size");
                    Check(placed.Left >= work.Left + edge && placed.Right <= work.Right - edge, "Popup ignored the edge margin");
                    bool below = placed.Top == anchor.Bottom + gap;
                    bool above = placed.Bottom == anchor.Top - gap;
                    bool clamped = placed.Top == work.Top || placed.Bottom == work.Bottom;
                    Check(below || above || clamped, "Popup is neither below, above nor clamped: " + placed);
                    if (anchor.Bottom + gap >= work.Top && anchor.Bottom + gap + size.Height <= work.Bottom)
                        Check(below, "Popup flipped although it fits below: " + placed);
                }
                Rectangle rightAligned = DsPopup.Place(anchors[5], size, work, gap, edge, true);
                Check(rightAligned.Right == anchors[5].Right, "Right alignment lost the anchor edge");
                Rectangle leftAligned = DsPopup.Place(anchors[5], size, work, gap, edge, false);
                Check(leftAligned.Left == anchors[5].Left, "Left alignment lost the anchor edge");
                Rectangle zero = DsPopup.Place(anchors[0], Size.Empty, work, gap, edge, false);
                Check(zero.Width == 1 && zero.Height == 1, "Empty popup size was not normalized");
                Size fit = DsPopup.Fit(new Size(work.Width * 2, work.Height * 2), work, (int)(20 * scale), (int)(170 * scale));
                Check(fit.Width == work.Width - (int)(20 * scale) && fit.Height == work.Height - (int)(20 * scale), "Popup fit did not clamp to the work area");
                Check(DsPopup.Fit(new Size(10, 10), work, 0, 170).Width == 10, "Popup fit grew a small popup");
                var narrowWork = new Rectangle(0, 0, 90, 60);
                Size narrow = DsPopup.Fit(new Size(400, 800), narrowWork, 12, 170);
                Check(narrow.Width == 78 && narrow.Height == 48, "Preferred minimum overflowed a narrow work area");
                Check(narrowWork.Contains(DsPopup.Place(narrowWork, new Size(400, 800), narrowWork, 4, 6, false)),
                    "Oversized popup placement escaped the work area");
            }
        }

        private static void CheckSurfaces()
        {
            foreach (bool navigation in new[] { true, false })
            foreach (Size size in new[] { new Size(1, 1), new Size(10, 10), new Size(300, 200), new Size(2000, 1200) })
            {
                using (var bitmap = new Bitmap(size.Width, size.Height))
                using (Graphics g = Graphics.FromImage(bitmap))
                {
                    g.Clear(Theme.Bg);
                    RectangleF clipBefore = g.ClipBounds;
                    RogSurface.Draw(g, new Rectangle(Point.Empty, size), navigation);
                    Check(g.ClipBounds == clipBefore && g.SmoothingMode == System.Drawing.Drawing2D.SmoothingMode.None,
                        "Surface painting leaked graphics state: " + size);
                    RogSurface.Draw(g, Rectangle.Empty, navigation);
                }
            }
            using (var panel = new WorkspacePanel { Size = new Size(Theme.S(320), Theme.S(200)) })
            using (Bitmap picture = Capture(panel))
                Check(picture.Width == panel.Width, "Workspace panel did not paint");
            using (var panel = new WorkspacePanel { Size = new Size(1, 1) })
            using (Bitmap picture = Capture(panel))
                Check(picture.Width == 1, "Workspace panel failed at 1x1");
        }

        private static void CheckBadges()
        {
            Font font = DsType.Mono(DsMono.Badge);
            using (var bitmap = new Bitmap(Theme.S(120), Theme.S(30)))
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.Clear(Theme.Bg);
                int width = DsPaint.BadgeWidth(g, "LOCK", font);
                Check(width > Theme.S(12), "Badge width lost its padding");
                var pill = new Rectangle(Theme.S(2), Theme.S(6), width, Theme.S(DsMetrics.Badge.Height));
                DsPaint.Badge(g, pill, "LOCK", font, true);
                Check(CountNear(bitmap, Theme.Accent, 12) > 0, "Strong badge is not filled with the accent color");
                g.Clear(Theme.Bg);
                DsPaint.Badge(g, pill, "LOCK", font, false);
                Check(CountNear(bitmap, Theme.Inset, 6) > 0, "Normal badge did not use the inset fill");
                DsPaint.Badge(g, Rectangle.Empty, "LOCK", font, false);
                DsPaint.Chevron(g, 2f, 2f, 1f, true, Theme.Fg);
                DsPaint.DropArrow(g, 1, 1, 1, false, Theme.Fg);
                DsPaint.BusyRail(g, new Rectangle(0, 0, 3, 3), 0.5f, Theme.Accent);
                DsPaint.BusyRail(g, new Rectangle(0, 0, Theme.S(120), Theme.S(30)), 2f, Theme.Accent);
            }
            using (var menu = new ContextMenuStrip { AutoSize = false, Renderer = new TechMenuRenderer(), Font = DsType.Font(DsText.Body),
                Padding = new Padding(Theme.S(DsMetrics.Menu.Padding)), ShowImageMargin = false, DropShadowEnabled = false,
                Size = new Size(Theme.S(230), Theme.S(120)) })
            {
                var strong = new ToolStripMenuItem("Current") { AutoSize = false, Margin = Padding.Empty,
                    Size = new Size(menu.Width - menu.Padding.Horizontal, Theme.S(DsMetrics.Menu.ItemHeight)), Tag = new MenuBadge("当前 Current", true) };
                var plain = new ToolStripMenuItem("Other") { AutoSize = false, Margin = Padding.Empty,
                    Size = new Size(menu.Width - menu.Padding.Horizontal, Theme.S(DsMetrics.Menu.ItemHeight)), Tag = new MenuBadge("LOCAL", false) };
                menu.Items.Add(strong); menu.Items.Add(plain); menu.Items.Add(new ToolStripSeparator());
                menu.PerformLayout();
                Check(strong.Height == Theme.S(DsMetrics.Menu.ItemHeight), "Menu item height does not follow the token");
                menu.Location = new Point(-32000, -32000);
                IntPtr handle = menu.Handle;
                Check(!IsWindowVisible(handle), "Renderer fixture menu became visible");
                using (var picture = new Bitmap(menu.Width, menu.Height))
                {
                    menu.DrawToBitmap(picture, new Rectangle(0, 0, menu.Width, menu.Height));
                    Check(CountNear(picture, Theme.Accent, 10) > 0, "Menu renderer did not paint the accent line or badge");
                }
            }
        }

        private static void CheckButtons(Form host)
        {
            int baseline = Listeners();
            using (var button = new PillButton("Run"))
            using (var primary = new PillButton("Go", BtnKind.Primary))
            using (var danger = new PillButton("Stop", BtnKind.Danger))
            using (var settings = new SettingsButton("Settings", "Open settings"))
            using (var link = new RogLinkButton("Website", "LINK // EXTERNAL", "info"))
            {
                button.Width = Theme.S(120); primary.Width = Theme.S(120); danger.Width = Theme.S(120);
                settings.Size = new Size(Theme.S(32), Theme.S(28));
                link.Size = new Size(Theme.S(220), Theme.S(48));
                host.Controls.Add(button); host.Controls.Add(primary); host.Controls.Add(danger);
                host.Controls.Add(settings); host.Controls.Add(link);
                CreateHiddenTree(host);
                Check(Listeners() == baseline + 5, "Button handles did not register exactly one frame listener each");
                Check(button.Height == Theme.S(DsMetrics.Button.Height) && button.TabStop && button.AccessibleRole == AccessibleRole.PushButton,
                    "PillButton defaults changed");
                Check((bool)Call(button, "IsInputKey", Keys.Enter) && (bool)Call(button, "IsInputKey", Keys.Space)
                    && !(bool)Call(button, "IsInputKey", Keys.Shift | Keys.Enter), "PillButton activation keys must be exact");
                Check((bool)Call(link, "IsInputKey", Keys.Shift | Keys.Enter) && (bool)Call(link, "IsInputKey", Keys.Space),
                    "RogLinkButton keeps masked activation keys");

                int clicks = 0, busyChanges = 0;
                button.Click += delegate { clicks++; };
                button.BusyChanged += delegate { busyChanges++; };
                button.PerformClick();
                var enter = new KeyEventArgs(Keys.Enter); Call(button, "OnKeyDown", enter);
                var space = new KeyEventArgs(Keys.Space); Call(button, "OnKeyDown", space);
                Check(clicks == 3 && enter.SuppressKeyPress && space.SuppressKeyPress, "Mouse and keyboard activation must fire once each");
                var modified = new KeyEventArgs(Keys.Control | Keys.Enter); Call(button, "OnKeyDown", modified);
                var handled = new KeyEventArgs(Keys.Enter); handled.Handled = true; Call(button, "OnKeyDown", handled);
                Check(clicks == 3 && !modified.SuppressKeyPress, "Modified or handled keys must not activate");

                button.Enabled = false;
                button.PerformClick(); Call(button, "OnKeyDown", new KeyEventArgs(Keys.Enter));
                Check(clicks == 3 && !button.CanInteract, "Disabled button activated");
                button.Enabled = true;
                button.Busy = true;
                Check(button.Busy && !button.CanInteract && busyChanges == 1, "Busy state did not block interaction");
                button.PerformClick(); Call(button, "OnKeyDown", new KeyEventArgs(Keys.Space));
                Call(button, "OnClick", EventArgs.Empty);
                Call(button, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, 4, 4, 0));
                Check(clicks == 3 && !Field<bool>(button, "pressed"), "Busy button accepted activation or press feedback");
                Check((button.AccessibilityObject.State & AccessibleStates.Busy) != 0, "Busy state is not exposed to accessibility");
                button.Busy = true;
                Check(busyChanges == 1, "Setting Busy twice repeated its event");
                button.Busy = false;
                Check(button.CanInteract && busyChanges == 2 && (button.AccessibilityObject.State & AccessibleStates.Busy) == 0,
                    "Leaving busy did not restore interaction");
                button.PerformClick();
                Check(clicks == 4, "Button lost activation after busy");

                var gate = new BusyGate(button, true);
                Check(gate.TryEnter() && button.Busy && !button.Enabled && gate.Held, "Busy gate did not enter");
                Check(!gate.TryEnter(), "Busy gate allowed re-entry");
                button.PerformClick();
                Check(clicks == 4, "Gated button activated");
                gate.Leave();
                Check(!button.Busy && button.Enabled && !gate.Held, "Busy gate did not restore the button");
                gate.Leave();
                button.Enabled = false;
                Check(!gate.TryEnter() && !gate.Held && !button.Busy, "Busy gate entered a disabled button");
                button.Enabled = true;
                button.Busy = true;
                Check(!gate.TryEnter() && !gate.Held, "Busy gate entered a busy button");
                button.Busy = false;
                var soft = new BusyGate(button, false);
                Check(soft.TryEnter() && button.Busy && button.Enabled, "Soft gate must keep Enabled");
                var competing = new BusyGate(button, false);
                Check(!competing.TryEnter(), "Another gate took a control already owned by an operation");
                competing.Leave();
                Check(button.Busy && soft.Held, "An unheld gate cleared another operation's busy state");
                soft.Leave();
                Check(!button.Busy, "Soft gate did not clear busy");

                int settingsClicks = 0;
                settings.Click += delegate { settingsClicks++; };
                settings.PerformClick();
                var settingsKey = new KeyEventArgs(Keys.Enter);
                typeof(PillButton).GetMethod("OnKeyDown", Hidden).Invoke(settings, new object[] { settingsKey });
                Check(settingsClicks == 2 && settingsKey.SuppressKeyPress && Field<ToolTip>(settings, "tips").GetToolTip(settings) == "Open settings",
                    "SettingsButton lost its button contract or tooltip");
                settings.Busy = true; settings.PerformClick();
                Check(settingsClicks == 2, "Busy settings button activated");
                settings.Busy = false;

                int linkClicks = 0;
                link.Click += delegate { linkClicks++; };
                link.PerformClick();
                var linkKey = new KeyEventArgs(Keys.Space); Call(link, "OnKeyDown", linkKey);
                Check(linkClicks == 2 && linkKey.Handled && linkKey.SuppressKeyPress, "RogLinkButton activation changed");
                link.Enabled = false; link.PerformClick(); Call(link, "OnKeyDown", new KeyEventArgs(Keys.Enter));
                link.Enabled = true; link.Busy = true; link.PerformClick();
                Check(linkClicks == 2, "Disabled or busy link activated");
                link.Busy = false;

                using (Bitmap normal = Capture(button))
                {
                    button.Busy = true;
                    using (Bitmap busy = Capture(button))
                        Check(!SamePicture(normal, busy) && ChangedBottomPixels(normal, busy) > Theme.S(20), "Busy rail is not painted");
                    button.Busy = false;
                    button.Enabled = false;
                    using (Bitmap disabled = Capture(button))
                    {
                        Check(!SamePicture(normal, disabled), "Disabled button looks enabled");
                        DsFocus.ForceCuesForTest = true;
                        using (Bitmap disabledCues = Capture(button))
                            Check(SamePicture(disabled, disabledCues), "Disabled button painted a focus ring");
                        button.Enabled = true;
                        using (Bitmap focused = Capture(button))
                            Check(!SamePicture(normal, focused), "Focus ring is not painted for an interactive button");
                        DsFocus.ForceCuesForTest = false;
                    }
                    using (Bitmap again = Capture(button))
                        Check(SamePicture(normal, again), "Button painting is not deterministic");
                }
                using (Bitmap a = Capture(primary)) using (Bitmap b = Capture(danger)) using (Bitmap c = Capture(button))
                    Check(!SamePicture(a, b) && !SamePicture(a, c) && !SamePicture(b, c), "Button kinds paint identically");
                using (Bitmap linkNormal = Capture(link))
                {
                    link.Enabled = false;
                    using (Bitmap linkDisabled = Capture(link)) Check(!SamePicture(linkNormal, linkDisabled), "Disabled link looks enabled");
                    link.Enabled = true;
                    DsFocus.ForceCuesForTest = true;
                    using (Bitmap linkFocused = Capture(link)) Check(!SamePicture(linkNormal, linkFocused), "Link focus ring missing");
                    DsFocus.ForceCuesForTest = false;
                }
                using (Bitmap picture = Capture(settings))
                    Check(CountNear(picture, Theme.Dim, 20) > 0 || CountNear(picture, Theme.Fg, 20) > 0, "Settings glyph not painted");
                foreach (Control control in new Control[] { button, primary, danger, settings, link }) PaintsAtTinySizes(control);
                if (language == 0) using (Bitmap picture = Capture(primary)) Save(picture, "button-primary");
                host.Controls.Clear();
            }
            Pump();
            Check(Listeners() == baseline, "Disposed buttons retained frame listeners");
        }

        private static void CheckToggle(Form host)
        {
            int baseline = Listeners();
            using (var detached = new Toggle())
            {
                int detachedEvents = 0;
                detached.CheckedChanged += delegate { detachedEvents++; };
                detached.Checked = true; detached.Checked = true; detached.SetSilently(false);
                Check(detachedEvents == 1 && !detached.Checked && !detached.IsHandleCreated, "Detached toggle created a handle or repeated events");
                Check(detached.Size == new Size(Theme.S(46), Theme.S(30)), "Toggle default size changed");
            }
            using (var toggle = new Toggle { Bounds = new Rectangle(10, 10, 100, Theme.S(30)) })
            using (var labelled = new Toggle { Bounds = new Rectangle(10, 60, Theme.S(200), Theme.S(30)), Text = "Label" })
            {
                host.Controls.Add(toggle); host.Controls.Add(labelled);
                CreateHiddenTree(host);
                Check(toggle.TabStop && toggle.AccessibleRole == AccessibleRole.CheckButton, "Toggle is not keyboard accessible");
                Check((bool)Call(toggle, "IsInputKey", Keys.Space) && (bool)Call(toggle, "IsInputKey", Keys.Enter)
                    && !(bool)Call(toggle, "IsInputKey", Keys.Left), "Toggle activation keys");

                var order = new List<string>();
                var seen = new List<bool>();
                toggle.Click += delegate { order.Add("click"); };
                toggle.CheckedChanged += delegate { order.Add("changed"); seen.Add(toggle.Checked); };
                toggle.PerformClick();
                Check(seen.Count == 1 && seen[0] && toggle.Checked && order.Count == 2 && order[0] == "click" && order[1] == "changed",
                    "PerformClick order or count");
                var space = new KeyEventArgs(Keys.Space); Call(toggle, "OnKeyDown", space);
                Check(seen.Count == 2 && !seen[1] && !toggle.Checked && space.Handled && space.SuppressKeyPress, "Space did not toggle once");
                var enter = new KeyEventArgs(Keys.Enter); Call(toggle, "OnKeyDown", enter);
                Check(seen.Count == 3 && seen[2] && toggle.Checked && enter.SuppressKeyPress, "Enter did not toggle once");
                Call(toggle, "OnKeyDown", new KeyEventArgs(Keys.Alt | Keys.Space));
                Check(seen.Count == 3, "Modified key toggled");
                Call(toggle, "OnClick", EventArgs.Empty);
                Check(seen.Count == 4 && !toggle.Checked, "Click handler did not toggle");
                Check((toggle.AccessibilityObject.State & AccessibleStates.Checked) == 0, "Accessible state shows checked while off");
                toggle.AccessibilityObject.DoDefaultAction();
                Check(seen.Count == 5 && toggle.Checked && (toggle.AccessibilityObject.State & AccessibleStates.Checked) != 0
                    && toggle.AccessibilityObject.DefaultAction.Length > 0, "Accessible default action did not toggle");

                toggle.Enabled = false;
                toggle.PerformClick(); Call(toggle, "OnKeyDown", new KeyEventArgs(Keys.Space)); Call(toggle, "OnClick", EventArgs.Empty);
                toggle.AccessibilityObject.DoDefaultAction();
                Check(seen.Count == 5 && toggle.Checked && !toggle.CanInteract, "Disabled toggle accepted user activation");
                toggle.Checked = false;
                Check(seen.Count == 6 && !seen[5] && !toggle.Checked, "Programmatic change on a disabled toggle must still notify");
                toggle.Enabled = true;
                toggle.Busy = true;
                toggle.PerformClick(); Call(toggle, "OnKeyDown", new KeyEventArgs(Keys.Enter)); Call(toggle, "OnClick", EventArgs.Empty);
                Check(seen.Count == 6 && !toggle.Checked && (toggle.AccessibilityObject.State & AccessibleStates.Busy) != 0, "Busy toggle accepted user activation");
                toggle.Checked = true;
                Check(seen.Count == 7 && toggle.Checked, "Programmatic change on a busy toggle must still notify");
                toggle.Busy = false;
                toggle.PerformClick();
                Check(seen.Count == 8 && !toggle.Checked, "Toggle lost activation after busy");

                toggle.Checked = true;
                using (Bitmap on = Capture(toggle))
                {
                    int diameter = Theme.S(22) - Theme.S(3) * 2;
                    int x = toggle.Width - 1 - Theme.S(3) - diameter / 2;
                    Color knob = on.GetPixel(x, toggle.Height / 2);
                    Check(knob.R > 245 && knob.G > 245 && knob.B > 245, "Active knob is not white immediately: " + knob);
                    toggle.SetSilently(false);
                    using (Bitmap off = Capture(toggle))
                    {
                        Color offKnob = off.GetPixel(Theme.S(3) + diameter / 2, toggle.Height / 2);
                        Check(Near(offKnob, DsPalette.KnobOff, 12), "Inactive knob color drifted: " + offKnob);
                        Check(!SamePicture(on, off), "Toggle state has no visual difference");
                        toggle.Enabled = false;
                        using (Bitmap disabled = Capture(toggle))
                        {
                            Check(!SamePicture(off, disabled), "Disabled toggle looks enabled");
                            DsFocus.ForceCuesForTest = true;
                            using (Bitmap disabledCues = Capture(toggle))
                                Check(SamePicture(disabled, disabledCues), "Disabled toggle painted a focus ring");
                        }
                        toggle.Enabled = true;
                        using (Bitmap focused = Capture(toggle)) Check(!SamePicture(off, focused), "Toggle focus ring missing");
                        DsFocus.ForceCuesForTest = false;
                        toggle.Busy = true;
                        using (Bitmap busy = Capture(toggle))
                        {
                            Check(!SamePicture(off, busy), "Busy toggle has no indicator");
                            bool wasSuspended = UiClock.Suspended;
                            UiClock.Suspended = false;
                            try
                            {
                                for (int i = 0; i < 4; i++) Frame();
                                Check(Field<float>(toggle, "busyPhase") > 0f, "Busy phase did not advance on frames");
                                using (Bitmap later = Capture(toggle)) Check(!SamePicture(busy, later), "Busy sweep did not move");
                            }
                            finally { UiClock.Running = false; UiClock.Suspended = wasSuspended; }
                            float phase = Field<float>(toggle, "busyPhase");
                            UiClock.Frozen = true;
                            try { Frame(); Check(Field<float>(toggle, "busyPhase") == phase, "Frozen clock advanced the busy sweep"); }
                            finally { UiClock.Frozen = false; }
                        }
                        toggle.Busy = false;
                    }
                }
                using (Bitmap picture = Capture(labelled)) Check(picture.Width == labelled.Width, "Labelled toggle did not paint");
                PaintsAtTinySizes(toggle); PaintsAtTinySizes(labelled);
                if (language == 0) { labelled.SetSilently(true); using (Bitmap picture = Capture(labelled)) Save(picture, "toggle"); }
                host.Controls.Clear();
            }
            Pump();
            Check(Listeners() == baseline, "Disposed toggles retained frame listeners");
        }

        private static void CheckCard(Form host)
        {
            using (var card = new SettingCard { Bounds = new Rectangle(0, 0, Theme.S(440), Theme.S(84)), Title = "Card", Desc = "Description", Channel = 7 })
            using (var toggle = new Toggle())
            {
                toggle.Size = new Size(Theme.S(46), Theme.S(24));
                card.Host(toggle);
                host.Controls.Add(card);
                CreateHiddenTree(host);
                Check(card.Hosted == toggle && card.Cursor == Cursors.Hand, "Card did not adopt its host");
                Check(toggle.Left == card.Width - Theme.S(DsMetrics.Card.HostRight) - toggle.Width
                    && toggle.Top == (card.Height - toggle.Height) / 2, "Host placement drifted");
                card.HostTop = true;
                Check(toggle.Top == Theme.S(DsMetrics.Card.HostTop), "HostTop placement drifted");
                card.HostTop = false;
                int events = 0;
                toggle.CheckedChanged += delegate { events++; };
                Action press = delegate
                {
                    Call(card, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, 20, 20, 0));
                    Call(card, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1, 20, 20, 0));
                };
                press();
                Check(events == 1 && toggle.Checked, "Card click did not toggle its host once");
                toggle.Enabled = false; press();
                Check(events == 1 && toggle.Checked, "Card click toggled a disabled host");
                toggle.Enabled = true; toggle.Busy = true; press();
                Check(events == 1, "Card click toggled a busy host");
                toggle.Busy = false; press();
                Check(events == 2 && !toggle.Checked, "Card click lost toggling after busy");
                Call(card, "OnMouseDown", new MouseEventArgs(MouseButtons.Right, 1, 20, 20, 0));
                Call(card, "OnMouseUp", new MouseEventArgs(MouseButtons.Right, 1, 20, 20, 0));
                Check(events == 2, "Right click toggled the host");
                card.Height = Theme.S(112);
                card.SetLock("LOCK", true); card.SetStatus("Applied", Theme.Green); card.SetValue("42%", Theme.Accent); card.Meta = "META";
                using (Bitmap full = Capture(card))
                {
                    Check(CountNear(full, Theme.Accent, 12) > 0, "Card lock badge or accent lines missing");
                    card.SetLock("", false);
                    using (Bitmap unlocked = Capture(card)) Check(!SamePicture(full, unlocked), "Lock badge has no visual effect");
                }
                Check(card.DescLinesShown >= 1 && card.DescLinesWanted >= 1, "Description line accounting is empty");
                card.Collapsible = true; card.CollapsedHeight = Theme.S(48); card.ExpandedHeight = Theme.S(84);
                int expands = 0; card.ExpandedChanged = delegate { expands++; };
                press();
                Check(!card.Expanded && card.Height == Theme.S(48) && expands == 1 && events == 2, "Collapsible card click did not collapse without toggling");
                press();
                Check(card.Expanded && card.Height == Theme.S(84) && expands == 2, "Collapsible card did not expand");
                PaintsAtTinySizes(card);
                if (language == 0) using (Bitmap picture = Capture(card)) Save(picture, "card");
                host.Controls.Clear();
            }
        }

        private static void CheckTierPicker(Form host)
        {
            using (var detached = new TierPicker())
            {
                detached.Labels = new[] { "Off", "On", "Ultra" };
                int calls = 0; detached.IndexChanged = delegate { calls++; };
                detached.Index = 1; detached.Index = -1; detached.Index = 9; detached.Index = 1;
                Check(detached.Index == 1 && calls == 0 && !detached.IsHandleCreated, "Programmatic tier index fired a callback or created a handle");
            }
            foreach (int count in new[] { 3, 4 })
            using (var picker = new TierPicker { Bounds = new Rectangle(0, 0, Theme.S(300), Theme.S(32)) })
            {
                picker.Labels = count == 3 ? new[] { "Off", "On", "Ultra" } : new[] { "Off", "Latest", "J", "K" };
                host.Controls.Add(picker);
                CreateHiddenTree(host);
                Check(picker.TabStop && picker.AccessibleRole == AccessibleRole.PageTabList, "Tier picker is not keyboard accessible");
                Rectangle previous = Rectangle.Empty;
                for (int i = 0; i < count; i++)
                {
                    var rect = (Rectangle)Call(picker, "SegmentRect", i);
                    Check(rect.Height == picker.Height && rect.Width > 0, "Segment geometry collapsed at scale " + dpi);
                    if (i == 0) Check(rect.X == 0, "First segment is not flush left");
                    else Check(rect.X == previous.Right + Theme.S(DsMetrics.Tier.Gap), "Segment gap drifted");
                    if (i == count - 1) Check(rect.Right == picker.Width, "Last segment is not flush right");
                    previous = rect;
                }
                var events = new List<int>();
                picker.IndexChanged = delegate(int index) { events.Add(index); Check(picker.Index == index, "Tier callback preceded its state"); };
                Func<int, MouseEventArgs> at = delegate(int i)
                {
                    var rect = (Rectangle)Call(picker, "SegmentRect", i);
                    return new MouseEventArgs(MouseButtons.Left, 1, rect.Left + rect.Width / 2, rect.Top + rect.Height / 2, 0);
                };
                Call(picker, "OnMouseUp", at(0));
                Check(events.Count == 1 && events[0] == 0 && picker.Index == 0, "Mouse selection did not fire once");
                Call(picker, "OnMouseUp", at(0));
                Check(events.Count == 1, "Repeated selection fired again");
                var rightButton = (Rectangle)Call(picker, "SegmentRect", 1);
                Call(picker, "OnMouseUp", new MouseEventArgs(MouseButtons.Right, 1, rightButton.Left + 2, 2, 0));
                Check(events.Count == 1, "Right button selected a tier");
                var right = new KeyEventArgs(Keys.Right); Call(picker, "OnKeyDown", right);
                Check(events.Count == 2 && picker.Index == 1 && right.Handled && right.SuppressKeyPress, "Right arrow did not select");
                Call(picker, "OnKeyDown", new KeyEventArgs(Keys.End));
                Check(events.Count == 3 && picker.Index == count - 1, "End did not select the last tier");
                Call(picker, "OnKeyDown", new KeyEventArgs(Keys.Right));
                Check(events.Count == 3, "Right at the end fired a callback");
                Call(picker, "OnKeyDown", new KeyEventArgs(Keys.Home));
                Check(events.Count == 4 && picker.Index == 0, "Home did not select the first tier");
                Call(picker, "OnKeyDown", new KeyEventArgs(Keys.Left));
                var up = new KeyEventArgs(Keys.Up); Call(picker, "OnKeyDown", up);
                Check(events.Count == 4 && !up.Handled && (bool)Call(picker, "IsInputKey", Keys.Left) && !(bool)Call(picker, "IsInputKey", Keys.Up),
                    "Non-navigation keys were consumed");
                Check(picker.AccessibilityObject.Value == picker.Labels[0], "Accessible value is not the selected label");

                picker.Enabled = false;
                Call(picker, "OnMouseUp", at(1)); Call(picker, "OnKeyDown", new KeyEventArgs(Keys.Right));
                Check(events.Count == 4 && picker.Index == 0 && !picker.CanInteract, "Disabled tier picker accepted activation");
                picker.Index = 2;
                Check(picker.Index == 2 && events.Count == 4, "Programmatic index on a disabled picker was lost");
                picker.Enabled = true; picker.Busy = true;
                Call(picker, "OnMouseUp", at(1)); Call(picker, "OnKeyDown", new KeyEventArgs(Keys.Home));
                Check(events.Count == 4 && picker.Index == 2, "Busy tier picker accepted activation");
                picker.Busy = false;
                Call(picker, "OnMouseUp", at(1));
                Check(events.Count == 5 && picker.Index == 1, "Tier picker lost activation after busy");

                using (Bitmap normal = Capture(picker))
                {
                    picker.Enabled = false;
                    using (Bitmap disabled = Capture(picker)) Check(!SamePicture(normal, disabled), "Disabled picker looks enabled");
                    picker.Enabled = true; picker.Busy = true;
                    using (Bitmap busy = Capture(picker)) Check(!SamePicture(normal, busy), "Busy picker has no indicator");
                    picker.Busy = false;
                    DsFocus.ForceCuesForTest = true;
                    using (Bitmap focused = Capture(picker)) Check(!SamePicture(normal, focused), "Picker focus ring missing");
                    DsFocus.ForceCuesForTest = false;
                }
                PaintsAtTinySizes(picker);
                if (language == 0 && count == 3) using (Bitmap picture = Capture(picker)) Save(picture, "tier");
                host.Controls.Clear();
            }
        }

        private static void CheckTechTabs(Form host)
        {
            int baseline = Listeners();
            using (var tabs = new TechTabs { Bounds = new Rectangle(0, 0, Theme.S(550), Theme.S(40)) })
            {
                tabs.SetTabs(new[] { "A", "B", "C" }, new[] { "first", "", "third" });
                Check(tabs.TabsWidth == 3 * Theme.S(DsMetrics.Tabs.TabWidth) + 2 * Theme.S(DsMetrics.Tabs.Gap) && tabs.Count == 3, "Tab strip width drifted");
                var events = new List<int>();
                tabs.IndexChanged = delegate(int index) { events.Add(index); Check(tabs.Index == index, "Tab callback preceded its state"); };
                tabs.Index = 2; tabs.Index = -1; tabs.Index = 99; tabs.Index = 2;
                Check(events.Count == 1 && tabs.Index == 2 && !tabs.IsHandleCreated, "Detached tab selection misbehaved");
                Motion marker = Field<Motion>(tabs, "selection");
                Check(marker.Value == 2 && marker.Target == 2, "Detached tab indicator did not snap");
                host.Controls.Add(tabs);
                CreateHiddenTree(host);
                Check(Listeners() == baseline + 1 && tabs.TabStop && tabs.AccessibleRole == AccessibleRole.PageTabList, "Tabs registration or accessibility");
                Call(tabs, "RecreateHandle");
                Check(Listeners() == baseline + 1, "Recreating the tab handle duplicated its listener");
                var left = new KeyEventArgs(Keys.Left); Call(tabs, "OnKeyDown", left);
                Check(events.Count == 2 && tabs.Index == 1 && left.Handled && left.SuppressKeyPress, "Left arrow did not move the tab");
                Call(tabs, "OnKeyDown", new KeyEventArgs(Keys.Home));
                Call(tabs, "OnKeyDown", new KeyEventArgs(Keys.End));
                Call(tabs, "OnKeyDown", new KeyEventArgs(Keys.Right));
                Check(events.Count == 4 && tabs.Index == 2, "Home/End navigation or end clamp failed");
                var modified = new KeyEventArgs(Keys.Shift | Keys.Left); Call(tabs, "OnKeyDown", modified);
                Check(events.Count == 4 && !modified.Handled, "Modified arrow moved the tab");
                var second = (Rectangle)Call(tabs, "TabRect", 1);
                Call(tabs, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1, second.Left + second.Width / 2, second.Top + second.Height / 2, 0));
                Check(events.Count == 5 && tabs.Index == 1, "Mouse tab selection failed");
                Call(tabs, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1, tabs.TabsWidth + Theme.S(20), 5, 0));
                Check(events.Count == 5, "Clicking beside the strip selected a tab");
                Check(tabs.AccessibilityObject.Value == "B", "Accessible value is not the selected tab");

                tabs.Enabled = false;
                Call(tabs, "OnKeyDown", new KeyEventArgs(Keys.Right));
                Call(tabs, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1, 5, 5, 0));
                Check(events.Count == 5 && tabs.Index == 1, "Disabled tabs accepted activation");
                tabs.Index = 0;
                Check(events.Count == 6, "Programmatic tab index on disabled tabs must still notify");
                tabs.Enabled = true; tabs.Busy = true;
                Call(tabs, "OnKeyDown", new KeyEventArgs(Keys.Right));
                Check(events.Count == 6 && tabs.Index == 0 && !tabs.CanInteract, "Busy tabs accepted activation");
                tabs.Busy = false;
                Call(tabs, "OnKeyDown", new KeyEventArgs(Keys.Right));
                Check(events.Count == 7 && tabs.Index == 1, "Tabs lost activation after busy");
                tabs.SnapToSelection();
                marker = Field<Motion>(tabs, "selection");
                Check(marker.Value == 1 && marker.Target == 1, "Snap did not settle the indicator");
                tabs.SetHot(new[] { true, false, true });
                using (Bitmap normal = Capture(tabs))
                {
                    tabs.Busy = true;
                    using (Bitmap busy = Capture(tabs)) Check(!SamePicture(normal, busy), "Busy tabs have no indicator");
                    tabs.Busy = false;
                    DsFocus.ForceCuesForTest = true;
                    using (Bitmap focused = Capture(tabs)) Check(!SamePicture(normal, focused), "Tab focus ring missing");
                    DsFocus.ForceCuesForTest = false;
                }
                tabs.Width = tabs.TabsWidth + Theme.S(30);
                using (Bitmap narrow = Capture(tabs)) Check(narrow.Width == tabs.Width, "Narrow tabs did not paint");
                PaintsAtTinySizes(tabs);
                tabs.SetTabs(new[] { "A" }, null);
                Check(tabs.Index == 0 && tabs.Count == 1, "Shrinking labels left an out-of-range selection");
                if (language == 0) using (Bitmap picture = Capture(tabs)) Save(picture, "tabs");
                host.Controls.Clear();
            }
            Pump();
            Check(Listeners() == baseline, "Disposed tabs retained frame listeners");
        }

        // TechSelect 下拉生命周期检查。
        private static void CheckDropdown(Form host, TechSelect select)
        {
            using (var parent = new Panel { Bounds = new Rectangle(10, 10, Theme.S(400), Theme.S(200)), AutoScroll = true })
            {
                select.Location = new Point(Theme.S(20), Theme.S(20));
                host.Controls.Add(parent); parent.Controls.Add(select);
                CreateHiddenTree(host);
                Check(select.Size == new Size(Theme.S(220), Theme.S(32)) && select.TabStop && select.AccessibleRole == AccessibleRole.ComboBox,
                    "Select defaults changed");
                Check(select.Visible && !IsWindowVisible(select.Handle), "Select fixture visibility is wrong");
                select.SetItems(new[] { "Alpha", "Beta", "Gamma" });
                Check(select.SelectedIndex == 0 && select.Text == "Alpha" && select.Count == 3, "SetItems did not clamp the selection");
                select.SetItems(new[] { "Alpha", "Beta", "Gamma" });
                var events = new List<int>();
                select.SelectionChanged += delegate(int index) { events.Add(index); Check(select.SelectedIndex == index, "Select callback preceded its state"); };
                select.SelectIndex(1); select.SelectIndex(1); select.SelectIndex(5); select.SelectIndex(-1);
                Check(events.Count == 1 && events[0] == 1 && select.SelectedText == "Beta", "SelectIndex callback count");
                select.SetSilently(2);
                Check(events.Count == 1 && select.SelectedIndex == 2, "SetSilently fired a callback");
                select.SelectIndex(0);
                Check(events.Count == 2, "Changing selection did not fire");
                select.CurrentBadge = DsLabels.Pick("当前", "Current");

                select.Enabled = false;
                select.SelectIndex(1); select.OpenDropDownForTest();
                Check(events.Count == 2 && select.SelectedIndex == 0 && !select.DropDownCreated && !select.CanInteract, "Disabled select changed or opened");
                select.Enabled = true; select.Busy = true;
                select.SelectIndex(1); select.OpenDropDownForTest();
                Check(events.Count == 2 && !select.DropDownCreated, "Busy select changed or opened");
                select.Busy = false;

                select.OpenDropDownForTest();
                ContextMenuStrip menu = select.DropDown;
                Check(select.DropDownCreated && !select.DropDownVisible && menu != null && menu.Items.Count == 3, "Open did not create a hidden menu");
                Check(menu.Items[0].Height == Theme.S(DsMetrics.Menu.ItemHeight) && menu.Width >= Theme.S(DsMetrics.Menu.MinWidth)
                    && menu.Width >= select.Width, "Menu geometry does not follow the tokens");
                Rectangle work = Screen.FromControl(select).WorkingArea;
                Check(work.Contains(select.LastPlacement) && select.LastPlacement.Size == menu.Size, "Menu placement left the work area: " + select.LastPlacement);
                Check((select.AccessibilityObject.State & AccessibleStates.Collapsed) != 0, "Hidden dropdown reports expanded");
                menu.Items[2].PerformClick();
                Check(events.Count == 3 && events[2] == 2 && !select.DropDownCreated, "Menu item click did not select once and close");
                Pump();
                Check(menu.IsDisposed, "Closed menu was not disposed after the message loop ran");

                select.OpenDropDownForTest();
                Check((bool)Call(select, "ProcessCmdKey", new Message(), Keys.Escape) && !select.DropDownCreated, "Escape did not close the menu");
                Check(!(bool)Call(select, "ProcessCmdKey", new Message(), Keys.Escape),
                    "Escape without a menu must pass on to the parent");
                foreach (Keys key in new[] { Keys.F4, Keys.Alt | Keys.Down, Keys.Down, Keys.Up, Keys.Space, Keys.Enter })
                {
                    Check((bool)Call(select, "ProcessCmdKey", new Message(), key) && select.DropDownCreated, "Open key did not open: " + key);
                    Check(!(bool)Call(select, "ProcessCmdKey", new Message(), Keys.Tab) && !select.DropDownCreated, "Tab did not close the menu");
                }
                Check(events.Count == 3, "Opening and closing fired selection callbacks");

                select.OpenDropDownForTest();
                menu = select.DropDown;
                menu.Location = new Point(-32000, -32000);
                IntPtr handle = menu.Handle;
                Check(!IsWindowVisible(handle), "Dropdown fixture became visible");
                SetManagedVisible(menu);
                Check(select.DropDownVisible && (select.AccessibilityObject.State & AccessibleStates.Expanded) != 0, "Managed visibility was not armed");
                Call(select, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, 4, 4, 0));
                Call(select, "OnClick", EventArgs.Empty);
                Check(!select.DropDownCreated, "Clicking the owner while open did not close only");
                Call(select, "OnClick", EventArgs.Empty);
                Check(select.DropDownCreated && select.DropDown != menu, "Click after close did not reopen a fresh menu");
                Pump();
                Check(menu.IsDisposed, "Replaced menu leaked");

                menu = select.DropDown;
                SetManagedVisible(menu);
                Call(select, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, 4, 4, 0));
                select.CloseDropDown();
                Call(select, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1, -4, -4, 0));
                Call(select, "OnClick", EventArgs.Empty);
                Check(select.DropDownCreated && select.DropDown != menu, "Drag-out release swallowed the next dropdown click");

                Action<string, Action> closes = delegate(string reason, Action change)
                {
                    if (!select.DropDownCreated) select.OpenDropDownForTest();
                    Check(select.DropDownCreated, "Fixture could not open before: " + reason);
                    change();
                    Check(!select.DropDownCreated, "Context change did not close the menu: " + reason);
                };
                closes("parent moved", delegate { parent.Left++; });
                closes("parent resized", delegate { parent.Width++; });
                closes("parent hidden", delegate { parent.Visible = false; });
                parent.Visible = true;
                closes("owner moved", delegate { select.Left++; });
                closes("owner resized", delegate { select.Width++; });
                closes("owner disabled", delegate { select.Enabled = false; });
                select.Enabled = true;
                closes("owner hidden", delegate { select.Visible = false; });
                select.Visible = true;
                closes("parent scrolled", delegate { Call(parent, "OnScroll", new ScrollEventArgs(ScrollEventType.ThumbTrack, 30)); });
                closes("items replaced", delegate { select.SetItems(new[] { "One", "Two" }); });
                Check(select.SelectedIndex == 1 && select.Text == "Two", "Replacing items did not clamp the selection");
                closes("explicit close", delegate { select.CloseDropDown(); });
                Pump();
                Check(events.Count == 3, "Context closes fired selection callbacks");

                using (Bitmap normal = Capture(select))
                {
                    select.Enabled = false;
                    using (Bitmap disabled = Capture(select)) Check(!SamePicture(normal, disabled), "Disabled select looks enabled");
                    select.Enabled = true; select.Busy = true;
                    using (Bitmap busy = Capture(select)) Check(!SamePicture(normal, busy), "Busy select has no indicator");
                    select.Busy = false;
                }
                PaintsAtTinySizes(select);
                if (language == 0) using (Bitmap picture = Capture(select)) Save(picture, "select");

                CheckSelectBusy(select, events);
                Check(HostListens(select), "Dropdown host is not subscribed to busy changes");
                select.OpenDropDownForTest();
                ContextMenuStrip last = select.DropDown;
                select.Dispose();
                Check(!HostListens(select), "Disposed select retained its busy subscription");
                parent.Left++;
                select.Busy = true;
                Pump();
                Check(last.IsDisposed && select.IsDisposed, "Disposing the select leaked its menu");
                host.Controls.Clear();
            }
        }

        // 忙碌门禁：打开中的菜单随 Busy 关闭并延迟释放；忙碌期间鼠标、键盘与选择均被拒绝。
        private static void CheckSelectBusy(TechSelect select, List<int> events)
        {
            int before = events.Count;
            select.OpenDropDownForTest();
            ContextMenuStrip open = select.DropDown;
            Check(open != null && select.DropDownCreated, "Fixture could not open before the busy check");
            select.Busy = true;
            Check(!select.DropDownCreated && !select.DropDownVisible && !select.CanInteract, "Busy did not close the open menu");
            Pump();
            Check(open.IsDisposed, "Busy-closed menu leaked");
            select.OpenDropDownForTest();
            Check(!select.DropDownCreated, "Busy select opened a menu");
            Check((bool)Call(select, "ProcessCmdKey", new Message(), Keys.F4) && !select.DropDownCreated, "Busy select opened from the keyboard");
            Call(select, "OnClick", EventArgs.Empty);
            Check(!select.DropDownCreated, "Busy select opened from a click");
            select.SelectIndex((select.SelectedIndex + 1) % select.Count);
            Check(events.Count == before, "Busy select accepted a selection");
            select.Busy = false;
            select.OpenDropDownForTest();
            Check(select.DropDownCreated && select.CanInteract, "Select did not reopen after busy");
            select.CloseDropDown();
            Check(!select.DropDownCreated && events.Count == before, "Busy cycle fired selection callbacks");
        }

        private static bool HostListens(Control control)
        {
            var handlers = Field<EventHandler>(control, "BusyChanged");
            if (handlers == null) return false;
            foreach (Delegate handler in handlers.GetInvocationList()) if (handler.Target is DropdownHost) return true;
            return false;
        }

        private static IntPtr Arm(ContextMenuStrip menu)
        {
            menu.Location = new Point(-32000, -32000);
            IntPtr handle = menu.Handle;
            menu.PerformLayout();
            Check(handle != IntPtr.Zero && !IsWindowVisible(handle), "Armed menu became visible");
            SetManagedVisible(menu);
            Check(menu.Visible && !IsWindowVisible(handle), "Arming displayed a popup");
            return handle;
        }

        // 卡片菜单按钮：托管菜单、键盘契约、点击握手、上下文关闭、忙碌与禁用门禁、释放。
        private static void CheckCardMenuButton(Form host)
        {
            using (var parent = new Panel { Bounds = new Rectangle(10, 10, Theme.S(400), Theme.S(200)), AutoScroll = true })
            using (var button = new GameCardMenuButton { Bounds = new Rectangle(Theme.S(20), Theme.S(20), Theme.S(28), Theme.S(22)) })
            {
                host.Controls.Add(parent); parent.Controls.Add(button);
                CreateHiddenTree(host);
                int clicks = 0, actions = 0;
                Func<ContextMenuStrip> make = delegate
                {
                    var createdMenu = new ContextMenuStrip { AutoSize = false, Size = new Size(Theme.S(230), Theme.S(120)),
                        Padding = new Padding(Theme.S(6)), ShowImageMargin = false, DropShadowEnabled = false,
                        Renderer = new TechMenuRenderer(), Font = DsType.Font(DsText.Body) };
                    for (int i = 0; i < 3; i++)
                    {
                        var item = new ToolStripMenuItem("Item " + i) { AutoSize = false, Margin = Padding.Empty,
                            Size = new Size(createdMenu.Width - createdMenu.Padding.Horizontal, Theme.S(36)) };
                        item.Click += delegate { actions++; };
                        createdMenu.Items.Add(item);
                    }
                    createdMenu.PerformLayout();
                    return createdMenu;
                };
                button.Click += delegate { clicks++; button.ShowMenu(make()); };
                Check(button.OwnedMenu == null && !button.MenuVisible, "Fresh menu button owns a menu");

                button.PerformClick();
                ContextMenuStrip menu = button.OwnedMenu;
                Check(clicks == 1 && menu != null && !button.MenuVisible && !menu.IsHandleCreated, "Click did not take ownership of a hidden menu");
                Check(Screen.FromControl(button).WorkingArea.Contains(button.OwnedMenuPlacement) && button.OwnedMenuPlacement.Size == menu.Size,
                    "Card menu placement left the work area: " + button.OwnedMenuPlacement);
                button.Busy = true;
                Check(button.OwnedMenu == null && !button.MenuVisible && !button.CanInteract, "Busy did not close the owned menu");
                Pump();
                Check(menu.IsDisposed, "Busy-closed card menu leaked");
                ContextMenuStrip refused = make();
                button.ShowMenu(refused);
                Check(refused.IsDisposed && button.OwnedMenu == null, "Busy button accepted a menu");
                button.PerformClick();
                Check(clicks == 1, "Busy button clicked");
                Check((bool)Call(button, "ProcessCmdKey", new Message(), Keys.F4) && clicks == 1 && button.OwnedMenu == null,
                    "Busy button opened from the keyboard");
                button.Busy = false;
                button.Enabled = false;
                refused = make();
                button.ShowMenu(refused);
                button.PerformClick();
                Check(refused.IsDisposed && button.OwnedMenu == null && clicks == 1, "Disabled button accepted a menu or a click");
                button.Enabled = true;

                Check(!(bool)Call(button, "ProcessCmdKey", new Message(), Keys.Escape), "Escape without a menu was consumed");
                Check((bool)Call(button, "ProcessCmdKey", new Message(), Keys.F4) && clicks == 2 && button.OwnedMenu != null, "F4 did not open the menu");
                Check((bool)Call(button, "ProcessCmdKey", new Message(), Keys.Escape) && button.OwnedMenu == null, "Escape did not close the menu");
                foreach (Keys key in new[] { Keys.Alt | Keys.Down, Keys.Apps, Keys.Shift | Keys.F10 })
                {
                    int before = clicks;
                    Check((bool)Call(button, "ProcessCmdKey", new Message(), key) && clicks == before + 1 && button.OwnedMenu != null, "Open key failed: " + key);
                    Check(!(bool)Call(button, "ProcessCmdKey", new Message(), Keys.Tab) && button.OwnedMenu == null, "Tab did not close the menu");
                }
                Check(!(bool)Call(button, "ProcessCmdKey", new Message(), Keys.Down) && button.OwnedMenu == null, "Arrow keys must not open a menu button");
                Pump();

                button.PerformClick();
                menu = button.OwnedMenu;
                IntPtr handle = Arm(menu);
                Check(button.MenuVisible, "Armed menu is not reported visible");
                int opened = clicks;
                button.PerformClick();
                Check(clicks == opened && button.OwnedMenu == null && !menu.IsDisposed && menu.IsHandleCreated && !menu.Visible,
                    "Click while open did not close only, or disposed synchronously");
                Pump();
                Check(menu.IsDisposed, "Toggled menu leaked");

                button.PerformClick();
                menu = button.OwnedMenu;
                Arm(menu);
                Call(button, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, 4, 4, 0));
                opened = clicks;
                Call(button, "OnClick", EventArgs.Empty);
                Check(clicks == opened && button.OwnedMenu == null, "Owner click while open reopened the menu");
                Call(button, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1, 4, 4, 0));
                Call(button, "OnClick", EventArgs.Empty);
                Check(clicks == opened + 1 && button.OwnedMenu != null && button.OwnedMenu != menu, "Click after the handshake did not reopen a fresh menu");
                Pump();
                Check(menu.IsDisposed, "Handshake-closed menu leaked");

                menu = button.OwnedMenu;
                Arm(menu);
                menu.Items[1].PerformClick();
                Check(actions == 1 && button.OwnedMenu == null && !menu.IsDisposed && menu.IsHandleCreated && !menu.Visible,
                    "Item click did not close the menu after its callback");
                Pump();
                Check(menu.IsDisposed, "Item-clicked menu leaked");

                Action<string, Action> closes = delegate(string reason, Action change)
                {
                    if (button.OwnedMenu == null) button.PerformClick();
                    Check(button.OwnedMenu != null, "Fixture could not open before: " + reason);
                    change();
                    Check(button.OwnedMenu == null, "Context change did not close the card menu: " + reason);
                };
                closes("parent moved", delegate { parent.Left++; });
                closes("parent hidden", delegate { parent.Visible = false; });
                parent.Visible = true;
                closes("owner resized", delegate { button.Width++; });
                closes("owner disabled", delegate { button.Enabled = false; });
                button.Enabled = true;
                closes("parent scrolled", delegate { Call(parent, "OnScroll", new ScrollEventArgs(ScrollEventType.ThumbTrack, 30)); });
                closes("explicit close", delegate { button.CloseMenu(); });
                closes("removed from parent", delegate { parent.Controls.Remove(button); });
                parent.Controls.Add(button);
                Pump();

                Check(HostListens(button), "Card menu host is not subscribed to busy changes");
                button.PerformClick();
                ContextMenuStrip last = button.OwnedMenu;
                button.Dispose();
                Check(!HostListens(button) && button.OwnedMenu == null && !button.MenuVisible, "Disposed button retained its menu or subscription");
                parent.Left++;
                button.Busy = true;
                Pump();
                Check(last.IsDisposed && button.IsDisposed, "Disposing the button leaked its menu");
                host.Controls.Clear();
            }
        }

        private static void CheckShowcase(Form host)
        {
            int baseline = Listeners();
            using (var showcase = new DsShowcase())
            {
                Check(showcase.Entries.Count >= 26, "Showcase lost entries: " + showcase.Entries.Count);
                var ids = new HashSet<string>();
                Control previous = null;
                foreach (DsShowcase.Entry entry in showcase.Entries)
                {
                    Check(ids.Add(entry.Id) && entry.Caption.Length > 0 && entry.Control.Name.StartsWith("showcase_"), "Showcase entry identity: " + entry.Id);
                    Check(showcase.ClientRectangle.Contains(entry.Control.Bounds) && showcase.ClientRectangle.Contains(entry.Label.Bounds),
                        "Showcase entry outside the panel: " + entry.Id);
                    Check(entry.Label.Right <= entry.Control.Left, "Caption overlaps its control: " + entry.Id);
                    if (previous != null) Check(entry.Control.Top >= previous.Bottom, "Showcase rows overlap: " + entry.Id);
                    previous = entry.Control;
                }
                Check(showcase.Find("button.busy") != null && ((IBusyInteractive)showcase.Find("button.busy").Control).Busy
                    && !((IBusyInteractive)showcase.Find("button.busy").Control).CanInteract, "Busy showcase entry is not busy");
                Check(!showcase.Find("toggle.disabled").Control.Enabled && !showcase.Find("select.disabled").Control.Enabled, "Disabled showcase entries are enabled");
                Check(((Toggle)showcase.Find("toggle.on").Control).Checked && ((TechSelect)showcase.Find("select.default").Control).SelectedIndex == 1,
                    "Showcase states drifted");
                host.Controls.Add(showcase);
                CreateHiddenTree(host);
                Check(Listeners() > baseline, "Showcase controls did not register frame listeners");
                using (Bitmap picture = Capture(showcase))
                {
                    Check(CountNear(picture, Theme.Accent, 10) > 0, "Showcase rendered without any accent");
                    Save(picture, "showcase");
                }
                foreach (DsShowcase.Entry entry in showcase.Entries) PaintsAtTinySizes(entry.Control);
                host.Controls.Remove(showcase);
            }
            Pump();
            Check(Listeners() == baseline, "Disposed showcase retained frame listeners");
        }

        private static void CheckMaintenanceDialog(Form host)
        {
            using (var content = new MaintenanceListPanel(delegate { return new List<MaintenanceListItem>(); }, null))
            {
                content.QueueForTest = delegate { };
                ConstructorInfo constructor = typeof(PaviseDialog).GetConstructor(Hidden, null,
                    new[] { typeof(string), typeof(string), typeof(DlgKind), typeof(string), typeof(string), typeof(Control), typeof(int), typeof(Func<bool>), typeof(Action) }, null);
                using (var dialog = (PaviseDialog)constructor.Invoke(new object[] { "Records", "Pending recovery records remain protected.", DlgKind.Info, "OK", null, content, 760, null, null }))
                {
                    dialog.StartPosition = FormStartPosition.Manual; dialog.Location = new Point(-32000, -32000);
                    dialog.Show(host);
                    Rectangle work = Screen.FromPoint(Cursor.Position).WorkingArea;
                    Check(dialog.Width <= work.Width && dialog.Height <= work.Height, "Maintenance dialog exceeds the monitor at current DPI");
                    Check(dialog.ClientRectangle.Contains(content.Bounds), "Maintenance content is clipped by the dialog");
                    Control accept = dialog.Controls.Find("dialogAccept", true)[0];
                    Check(dialog.ClientRectangle.Contains(accept.Bounds) && accept.Top >= content.Bottom, "Maintenance footer overlaps its content");
                }
            }
        }

        private static void CheckDialog(Form host)
        {
            int attempts = 0;
            using (var release = new System.Threading.ManualResetEvent(false))
            using (var dialog = PaviseDialog.CreateRequiredRestart(delegate
            {
                int attempt = System.Threading.Interlocked.Increment(ref attempts);
                if (attempt == 1) { release.WaitOne(3000); return false; }
                return true;
            }))
            {
                dialog.StartPosition = FormStartPosition.Manual; dialog.Location = new Point(-32000, -32000);
                dialog.Show(host);
                Check(dialog.Left <= -30000, "Dialog fixture is on screen");
                var button = (PillButton)dialog.Controls.Find("dialogAccept", true)[0];
                var error = (Label)dialog.Controls.Find("restartError", true)[0];
                Check(dialog.Controls.Find("dialogCancel", true).Length == 0, "Required dialog offers a cancel button");
                Check(button.Width == Theme.S(DsMetrics.Dialog.AcceptWidth) && button.Height == Theme.S(DsMetrics.Dialog.ButtonHeight)
                    && button.Right == dialog.ClientSize.Width - Theme.S(DsMetrics.Dialog.PadX)
                    && dialog.ClientRectangle.Contains(button.Bounds) && dialog.ClientRectangle.Contains(error.Bounds), "Dialog geometry drifted");
                typeof(Form).GetMethod("OnKeyDown", Hidden).Invoke(dialog, new object[] { new KeyEventArgs(Keys.Escape) });
                dialog.Close();
                Check(dialog.Visible && !dialog.IsDisposed && attempts == 0, "Escape or Close bypassed the required action");
                var previousContext = System.Threading.SynchronizationContext.Current;
                System.Threading.SynchronizationContext.SetSynchronizationContext(null);
                try { button.PerformClick(); button.PerformClick(); }
                finally { System.Threading.SynchronizationContext.SetSynchronizationContext(previousContext); }
                PumpUntil(delegate { return System.Threading.Thread.VolatileRead(ref attempts) == 1; }, "first attempt");
                Check(!button.Enabled && button.Busy && !button.CanInteract, "Running action did not gate the button");
                button.PerformClick();
                typeof(Form).GetMethod("OnKeyDown", Hidden).Invoke(dialog, new object[] { new KeyEventArgs(Keys.Enter) });
                release.Set();
                PumpUntil(delegate { return button.Enabled; }, "button re-enabled");
                Check(!button.Busy && attempts == 1 && dialog.Visible && error.Text == Lang.T("vbs.restart.failed"), "Failed action did not restore the gate");
                button.PerformClick();
                PumpUntil(delegate { return dialog.IsDisposed; }, "dialog closed");
                Check(attempts == 2 && dialog.DialogResult == DialogResult.OK, "Successful action did not close with OK");
            }

            ConstructorInfo ctor = typeof(PaviseDialog).GetConstructor(Hidden, null,
                new[] { typeof(string), typeof(string), typeof(DlgKind), typeof(string), typeof(string), typeof(Control), typeof(int), typeof(Func<bool>), typeof(Action) }, null);
            Check(ctor != null, "Dialog constructor signature changed");
            Func<bool, PaviseDialog> create = delegate(bool cancellable)
            {
                var dialog = (PaviseDialog)ctor.Invoke(new object[] { "Title", "Body text", DlgKind.Warn, "OK", cancellable ? "Cancel" : null, null, 468, null, null });
                dialog.StartPosition = FormStartPosition.Manual; dialog.Location = new Point(-32000, -32000);
                dialog.Show(host);
                return dialog;
            };
            foreach (Keys key in new[] { Keys.Escape, Keys.Enter })
            foreach (bool cancellable in new[] { true, false })
            {
                PaviseDialog dialog = create(cancellable);
                Check(dialog.ClientSize.Width == Theme.S(DsMetrics.Dialog.Width), "Dialog width drifted");
                Check((dialog.Controls.Find("dialogCancel", true).Length == 1) == cancellable, "Cancel button presence");
                typeof(Form).GetMethod("OnKeyDown", Hidden).Invoke(dialog, new object[] { new KeyEventArgs(key) });
                PumpUntil(delegate { return dialog.IsDisposed; }, "keyboard close");
                DialogResult expected = key == Keys.Escape && cancellable ? DialogResult.Cancel : DialogResult.OK;
                Check(dialog.DialogResult == expected, "Dialog keyboard result: key=" + key + " cancellable=" + cancellable + " result=" + dialog.DialogResult);
            }
            PaviseDialog byCancel = create(true);
            ((PillButton)byCancel.Controls.Find("dialogCancel", true)[0]).PerformClick();
            PumpUntil(delegate { return byCancel.IsDisposed; }, "cancel click");
            Check(byCancel.DialogResult == DialogResult.Cancel, "Cancel button did not cancel");
            PaviseDialog byAccept = create(true);
            var accept = (PillButton)byAccept.Controls.Find("dialogAccept", true)[0];
            Check(accept.Kind == BtnKind.Primary, "Warn dialog accept kind changed");
            accept.PerformClick();
            PumpUntil(delegate { return byAccept.IsDisposed; }, "accept click");
            Check(byAccept.DialogResult == DialogResult.OK, "Accept button did not confirm");
        }
    }
}
#endif
