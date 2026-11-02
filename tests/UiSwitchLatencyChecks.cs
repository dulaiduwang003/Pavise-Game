#if PAVISE_SELFTEST && PAVISE_SWITCH_LATENCY_CHECKS
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
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
        public static bool TryHandleRuntimeMode(string[] args) { throw new InvalidOperationException("Latency fixture cannot enter runtime"); }
    }
    internal static class UiSwitchLatencyChecks
    {
        // FirstCompletePaint measures queued UI work plus pending native paint,
        // not desktop-compositor presentation. Both baseline and candidate use
        // the same inactive private desktop, viewport, DPI and worker guards.
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        private static readonly Stopwatch clock = Stopwatch.StartNew();
        private static readonly List<PanelForm> retained = new List<PanelForm>();
        private static string output, current = "initialization";
        private static double previousStage;
        private static int rebuilds;
        private static bool traceStages = true;
        internal static void Stage(string name)
        {
            if (!traceStages) return;
            double now = clock.Elapsed.TotalMilliseconds;
            if (name == "rebuild.enter") rebuilds++;
            File.AppendAllText(Path.Combine(output, "stages.tsv"), current + "\t" + name + "\t" + now.ToString("F3", CultureInfo.InvariantCulture)
                + "\t" + (now - previousStage).ToString("F3", CultureInfo.InvariantCulture) + Environment.NewLine);
            previousStage = now;
        }
        private static object Call(object owner, string name, params object[] args)
        {
            for (Type type = owner.GetType(); type != null; type = type.BaseType) {
                MethodInfo method = type.GetMethods(Hidden | BindingFlags.DeclaredOnly).FirstOrDefault(x => x.Name == name && x.GetParameters().Length == args.Length);
                if (method != null) try { return method.Invoke(owner, args); } catch (TargetInvocationException e) { throw e.InnerException; }
            }
            throw new MissingMethodException(name);
        }
        private static T Field<T>(object owner, string name)
        {
            for (Type type = owner.GetType(); type != null; type = type.BaseType) {
                FieldInfo field = type.GetField(name, Hidden | BindingFlags.DeclaredOnly);
                if (field != null) return (T)field.GetValue(owner);
            }
            throw new MissingFieldException(name);
        }
        private static IEnumerable<Control> Tree(Control root)
        { foreach (Control child in root.Controls) { yield return child; foreach (Control nested in Tree(child)) yield return nested; } }
        private sealed class FixtureForm : PanelForm
        {
            internal FixtureForm(Tamer tamer, GameMode mode, Icon icon) : base(tamer, mode, icon, true) { }
            protected override bool ShowWithoutActivation { get { return true; } }
        }
        private sealed class DraftExtension : GameCardExtension
        {
            internal readonly TextBox Draft;
            internal DraftExtension()
            {
                Draft = new TextBox { Name = "LatencyDraft", Text = "unsaved fixture draft", BackColor = Theme.Inset,
                    ForeColor = Theme.Fg, Bounds = new Rectangle(Theme.S(8), Theme.S(16), Theme.S(180), Theme.S(28)) };
                Controls.Add(Draft);
            }
            public override int PreferredHeight { get { return Theme.S(160); } }
        }
        private static void PreserveInteractionState(PanelForm form)
        {
            current = "preservation";
            form.ShowPageForShot(PageId.Library); Call(form, "StopPageReveal"); Settle(20);
            form.LibraryExtensionForTest = delegate { return new DraftExtension(); };
            var gallery = Field<GameLibraryGallery>(form, "lstGames");
            string executable = Process.GetCurrentProcess().MainModule.FileName;
            var items = new List<GameLibraryItem>();
            for (int i = 0; i < 12; i++) items.Add(new GameLibraryItem(new GameProfile {
                Id = "latency-state-" + i, Name = "StateFixture " + i, Root = Path.GetDirectoryName(executable), ExecutablePath = executable
            }, false, true));
            gallery.SetItems(items); Field<TextBox>(form, "librarySearch").Text = "StateFixture"; Settle(20);
            var tile = gallery.Controls.OfType<GameLibraryTile>().First(x => x.Item.Profile.Id == items[4].Profile.Id);
            Call(tile.ConfigurationButton, "OnClick", EventArgs.Empty); Settle(20);
            Control settings = tile.ConfigurationContent;
            ((PillButton)settings.Controls.Find("libraryExtensionToggle", true).Single()).PerformClick(); Settle(20);
            var extension = settings.Controls.Find("LibraryGameExtension", true).OfType<DraftExtension>().Single();
            extension.Draft.Text = "keep this unsaved text"; extension.Draft.SelectionStart = 7;
            gallery.ScrollControlIntoView(extension); Application.DoEvents(); gallery.SelectedIndex = 10;
            Point scroll = gallery.AutoScrollPosition;
            var controls = new HashSet<Control>(Tree(form));
            foreach (bool light in new[] { true, false }) {
                Call(form, "OnThemeToggled", light); Settle(40);
                if (!controls.SetEquals(Tree(form))) throw new InvalidOperationException("Theme replaced a control or expanded extension");
                if (settings.IsDisposed || extension.IsDisposed || !extension.Visible || tile.ConfigurationContent != settings
                    || gallery.ExpandedItem.Profile.Id != items[4].Profile.Id || gallery.SelectedIndex != 10)
                    throw new InvalidOperationException("Theme lost card expansion or manual selection");
                if (gallery.AutoScrollPosition != scroll || Field<TextBox>(form, "librarySearch").Text != "StateFixture"
                    || extension.Draft.Text != "keep this unsaved text" || extension.Draft.SelectionStart != 7)
                    throw new InvalidOperationException("Theme lost scroll, query, unsaved draft or caret");
                if (extension.Draft.BackColor != Theme.Inset || extension.Draft.ForeColor != Theme.Fg
                    || Field<bool>(Field<ThemeSwitch>(form, "themeSwitch"), "lightOn") != light)
                    throw new InvalidOperationException("Theme failed to recolor native draft or synchronize theme switch");
            }
            gallery.CollapseExpandedConfiguration();
            if (!settings.IsDisposed || !extension.IsDisposed) throw new InvalidOperationException("Expanded state fixture did not dispose on collapse");
            Console.WriteLine("PASS theme state preservation: entire tree identity, expanded extension, manual selection, scroll, query, draft, caret, native colors");
        }
        private static void BackgroundCacheState(PanelForm form)
        {
            form.ShowPageForShot(PageId.Overview); Call(form, "StopPageReveal"); Settle(20);
            var core = Field<PaviseCore>(form, "paviseCore");
            FieldInfo source = typeof(Backdrop).GetField("source", BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo drop = typeof(Backdrop).GetMethod("DropFitted", BindingFlags.Static | BindingFlags.NonPublic);
            object previous = source.GetValue(null);
            using (var red = new Bitmap(40, 40)) using (var blue = new Bitmap(40, 40))
            using (var first = new Bitmap(core.Width, core.Height)) using (var second = new Bitmap(core.Width, core.Height)) {
                using (Graphics g = Graphics.FromImage(red)) g.Clear(Color.Red);
                using (Graphics g = Graphics.FromImage(blue)) g.Clear(Color.Blue);
                try {
                    source.SetValue(null, red); drop.Invoke(null, null); core.DrawToBitmap(first, core.ClientRectangle);
                    object cached = Field<object>(core, "staticLayers");
                    if (!Backdrop.AppliesTo(core)) throw new InvalidOperationException("Actual PanelForm did not enable custom background");
                    source.SetValue(null, blue); drop.Invoke(null, null); core.DrawToBitmap(second, core.ClientRectangle);
                    if (!object.ReferenceEquals(cached, Field<object>(core, "staticLayers")))
                        throw new InvalidOperationException("Changing active background unnecessarily rebuilt vector layers");
                    double difference = 0; int samples = 0;
                    for (int y = 0; y < core.Height; y += 8) for (int x = 0; x < core.Width; x += 8) {
                        Color a = first.GetPixel(x, y), b = second.GetPixel(x, y);
                        difference += (a.R - a.B) - (b.R - b.B); samples++;
                    }
                    if (difference / samples < 5) throw new InvalidOperationException("Custom background source change was not painted immediately");
                    source.SetValue(null, null); drop.Invoke(null, null); core.DrawToBitmap(first, core.ClientRectangle);
                    if (Backdrop.AppliesTo(core) || object.ReferenceEquals(cached, Field<object>(core, "staticLayers"))
                        || Field<bool>(Field<object>(core, "staticLayers"), "Backdrop"))
                        throw new InvalidOperationException("Clearing custom background did not restore cached vector background");
                } finally { source.SetValue(null, previous); drop.Invoke(null, null); }
            }
            Console.WriteLine("PASS actual PanelForm background source update, active cache reuse and vector restore");
        }
        private static void Settle(int milliseconds)
        {
            double until = clock.Elapsed.TotalMilliseconds + milliseconds;
            do { Application.DoEvents(); Thread.Sleep(1); } while (clock.Elapsed.TotalMilliseconds < until);
        }
        private static void Measure(PanelForm form, string name, Action action)
        {
            current = name; previousStage = clock.Elapsed.TotalMilliseconds;
            int before = Tree(form).Count(), startRebuilds = rebuilds, pulses = 0;
            double heartbeat = 0;
            using (var pulse = new System.Threading.Timer(delegate {
                double posted = clock.Elapsed.TotalMilliseconds;
                try { form.BeginInvoke((MethodInvoker)delegate { pulses++; heartbeat = Math.Max(heartbeat, clock.Elapsed.TotalMilliseconds - posted); }); }
                catch (InvalidOperationException) { }
            }, null, 0, 16)) {
                Settle(32); previousStage = clock.Elapsed.TotalMilliseconds;
                double started = clock.Elapsed.TotalMilliseconds;
                Stage("action.enter"); action();
                double handler = clock.Elapsed.TotalMilliseconds - started;
                Stage("handler.return");
                bool completed = false; form.BeginInvoke((MethodInvoker)delegate { completed = true; });
                while (!completed) { Application.DoEvents(); Thread.Sleep(1); if (clock.Elapsed.TotalMilliseconds - started > 15000) throw new TimeoutException(name); }
                form.Update(); Field<Control>(form, "curPage").Update(); Application.DoEvents();
                double frame = clock.Elapsed.TotalMilliseconds - started;
                Settle(80);
                if (Tree(form).Count() != before) throw new InvalidOperationException("Switch changed the control count: " + name);
                if (name.StartsWith("mode.", StringComparison.Ordinal) && rebuilds != startRebuilds)
                    throw new InvalidOperationException("Mode switching rebuilt UI: " + name);
                string row = name + "\t" + handler.ToString("F3", CultureInfo.InvariantCulture) + "\t" + frame.ToString("F3", CultureInfo.InvariantCulture)
                    + "\t" + heartbeat.ToString("F3", CultureInfo.InvariantCulture) + "\t" + before + "\t" + Tree(form).Count() + "\t" + (rebuilds - startRebuilds) + "\t" + pulses;
                File.AppendAllText(Path.Combine(output, "latency.tsv"), row + Environment.NewLine);
                Console.WriteLine(row); Console.Out.Flush();
            }
        }
        private static void PrivacyDisplayCacheState()
        {
            int reads = 0, sets = 0, notifications = 0, workerId = 0, uiId = Thread.CurrentThread.ManagedThreadId;
            Action completed = null; var posted = new Queue<Action>();
            var result = new PrivacyResult { State = "ready", Supported = true };
            using (var owner = new Form { ShowInTaskbar = false })
            using (var panel = new PrivacyOptionsPanel(delegate { return true; }, delegate { notifications++; }, true)) {
                panel.Visible = false; panel.InspectionVisibleForTest = delegate { return true; };
                panel.RefreshForTest = delegate(PrivacyAction action, string target, Action done) { completed = done; return true; };
                panel.SetForTest = delegate(PrivacyAction action, bool enabled, string target, Action done) { sets++; completed = done; return true; };
                panel.PostForTest = delegate(Action work) { lock (posted) posted.Enqueue(work); };
                owner.Controls.Add(panel); IntPtr handle = owner.Handle; handle = panel.Handle;
                panel.SyncCached();
                var toggle = panel.Controls.Find("privacyToggle" + PrivacyActionCatalog.NtfsMemory, true).OfType<Toggle>().Single();
                if (toggle.Enabled || toggle.Checked) throw new InvalidOperationException("Unverified privacy cache was editable");
                Action publish = delegate {
                    panel.StateForTest = delegate { reads++; workerId = Thread.CurrentThread.ManagedThreadId; return result; };
                    Exception failure = null; var worker = new Thread(delegate() { try { completed(); } catch (Exception e) { failure = e; } });
                    worker.Start(); worker.Join(); panel.StateForTest = null;
                    if (failure != null) throw failure;
                    while (posted.Count > 0) posted.Dequeue()();
                };
                panel.RefreshAllForTest(); publish();
                if (!toggle.Enabled || toggle.Checked || reads != 1 || workerId == uiId || panel.BusyForTest)
                    throw new InvalidOperationException("Completed worker result did not publish the editable privacy cache");
                result = new PrivacyResult { State = "unsupported", Supported = false };
                int rawReads = 0;
                Settings.BeforeStrictStringReadForTest = delegate { rawReads++; Thread.Sleep(20); };
                Settings.BeforeStrictBoolReadForTest = delegate { rawReads++; Thread.Sleep(20); };
                try { for (int i = 0; i < 100; i++) panel.SyncCached(); }
                finally { Settings.BeforeStrictStringReadForTest = null; Settings.BeforeStrictBoolReadForTest = null; }
                if (!toggle.Enabled || reads != 1 || rawReads != 0) throw new InvalidOperationException("UI sync bypassed the published privacy cache or read raw settings");
                toggle.PerformClick();
                if (!toggle.Checked || !panel.BusyForTest || sets != 1) throw new InvalidOperationException("Explicit privacy transaction did not retain pending desired state");
                result = new PrivacyResult { State = "configured", Configured = true, Supported = true };
                publish();
                for (int i = 0; i < 100; i++) panel.SyncCached();
                if (!toggle.Checked || !toggle.Enabled || reads != 2 || panel.BusyForTest)
                    throw new InvalidOperationException("Explicit completion did not update cached privacy state");
                panel.SetForTest = delegate { sets++; return false; }; toggle.PerformClick();
                if (!toggle.Checked || panel.BusyForTest || sets != 2 || notifications != 1 || reads != 2)
                    throw new InvalidOperationException("Rejected privacy request did not restore the cached checked state");
                bool oldLight = Theme.LightMode; Theme.SetLight(!oldLight); panel.SyncCached();
                var card = panel.Controls.Find("privacyCard" + PrivacyActionCatalog.NtfsMemory, true).OfType<PerformanceFeatureCard>().Single();
                if (card.StatusColor != Theme.Accent || reads != 2) throw new InvalidOperationException("Cached privacy status lost current palette or performed another read");
                Theme.SetLight(oldLight);
            }
            Console.WriteLine("PASS privacy display cache: worker callbacks publish refresh/set results; 200 UI syncs cause no provider reads; refusal, pending state and palette preserved");
        }
        private static void TickRefreshState(PanelForm form)
        {
            var sync = Field<List<Action>>(form, "automationSync");
            int count = 0; Action counter = delegate { count++; }; sync.Add(counter);
            FieldInfo active = typeof(PanelForm).GetField("uiActive", Hidden);
            MethodInfo tick = typeof(PanelForm).GetMethod("OnUiTick", Hidden);
            object[] tickArgs = { null, EventArgs.Empty };
            File.WriteAllText(Path.Combine(output, "ticks.tsv"), "Page\tTicks\tAutomationCallbacks\tMeanTickMs\tMaxTickMs" + Environment.NewLine);
            try {
                traceStages = false;
                foreach (PageId page in new[] { PageId.Overview, PageId.About, PageId.Policy }) {
                    active.SetValue(form, false); form.ShowPageForShot(page); Call(form, "StopPageReveal"); Settle(20);
                    active.SetValue(form, true);
                    for (int warm = 0; warm < 5; warm++) tick.Invoke(form, tickArgs);
                    count = 0; double total = 0, maximum = 0;
                    for (int i = 0; i < 100; i++) {
                        double started = clock.Elapsed.TotalMilliseconds; tick.Invoke(form, tickArgs);
                        double duration = clock.Elapsed.TotalMilliseconds - started; total += duration; maximum = Math.Max(maximum, duration);
                        if (count != (page == PageId.Policy ? i + 1 : 0)) throw new InvalidOperationException("Tick refreshed automation outside Policy or more than once: " + page);
                    }
                    active.SetValue(form, false);
                    string row = page + "\t100\t" + count + "\t" + (total / 100).ToString("F3", CultureInfo.InvariantCulture)
                        + "\t" + maximum.ToString("F3", CultureInfo.InvariantCulture);
                    File.AppendAllText(Path.Combine(output, "ticks.tsv"), row + Environment.NewLine); Console.WriteLine("TICK " + row);
                }
            } finally { active.SetValue(form, false); sync.Remove(counter); traceStages = true; }
            Console.WriteLine("PASS native active UI ticks: Overview/About zero automation callbacks; Policy exactly one per tick; runtime workers suppressed");
        }
        private static void Run(bool privacyOnly)
        {
            Settings.UseTransientStoreForCurrentProcess(); Logger.WritesEnabled = false; PanelForm.SuppressUiWorkersForTest = true;
            Paths.Data = Path.Combine(output, "isolated-data"); Directory.CreateDirectory(Paths.Data);
            PrivacyControl.ResetForTest(); PrivacyControl.QueueForTest = delegate { throw new InvalidOperationException("Latency fixture cannot queue privacy work"); };
            Dpi.Init(); Lang.Init(); Lang.Cur = 0; Theme.SetLight(false); Settings.SaveStr("PerformancePreset", "1");
            if (privacyOnly) { PrivacyDisplayCacheState(); Console.WriteLine("PASS UI switch latency; privacy callback cache only, private desktop, mocked native work"); return; }
            CpuTopology.InjectTopologyForTest(255, new ulong[] {3,12,48,192}, new ulong[] {15,240},0,0,0,0,false,false);
            PanelForm.WorkspaceAreaForTest = delegate { return new Rectangle(0, 0, 1920, 1040); };
            PanelForm.WorkspaceDpiForTest = delegate { return 120; };
            var core = new SuppressionCore(); var mode = new GameMode(Paths.Data, core); var tamer = new Tamer(core);
            var form = new FixtureForm(tamer, mode, IconArt.MakeIcon(24)); retained.Add(form);
            form.ShowInTaskbar = false; form.Show(); Settle(80); form.ShowPageForShot(PageId.Overview); Call(form, "StopPageReveal"); Settle(40);
            if (mode.Enabled || form.UiActive || Field<object>(tamer, "worker") != null) throw new InvalidOperationException("Runtime workers started");
            File.WriteAllText(Path.Combine(output, "latency.tsv"), "Operation\tHandlerMs\tFirstCompletePaintMs\tHeartbeatMaxMs\tControlsBefore\tControlsAfter\tRebuilds\tHeartbeats" + Environment.NewLine);
            for (int i = 0; i < 6; i++) {
                PerformancePreset preset = i % 2 == 0 ? PerformancePreset.Standard : PerformancePreset.Competitive;
                if (!mode.TrySetPreset(preset)) throw new InvalidOperationException("Transient mode setting rejected");
                Measure(form, "mode." + preset + "." + i, delegate { Call(form, "UpdateModePresentation", true); });
            }
            for (int i = 0; i < 4; i++) {
                bool light = i % 2 == 0;
                Measure(form, "theme." + (light ? "light" : "dark") + "." + i, delegate { Call(form, "OnThemeToggled", light); });
            }
            form.ShowPageForShot(PageId.Policy); Call(form, "StopPageReveal"); Settle(40);
            for (int i = 0; i < 2; i++) {
                bool light = i == 0; Measure(form, "theme.policy." + (light ? "light" : "dark"), delegate { Call(form, "OnThemeToggled", light); });
                if (Theme.LightMode != light) throw new InvalidOperationException("Theme state did not switch");
                using (var bitmap = new Bitmap(form.Width, form.Height)) {
                    form.DrawToBitmap(bitmap, form.ClientRectangle); bitmap.Save(Path.Combine(output, "policy-theme-" + (light ? "light" : "dark") + ".png"));
                }
            }
            PreserveInteractionState(form);
            BackgroundCacheState(form);
            TickRefreshState(form);
            PrivacyDisplayCacheState();
            form.Hide(); Console.WriteLine("PASS UI switch latency; isolated data, private desktop, no runtime workers");
        }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateDesktop(string name, IntPtr device, IntPtr mode, uint flags, uint access, IntPtr security);
        [DllImport("user32.dll")] private static extern bool SetThreadDesktop(IntPtr desktop);
        [STAThread] private static int Main(string[] args)
        {
            if (args.Length < 1 || args.Length > 2) return 2; output = Path.GetFullPath(args[0]);
            IntPtr desktop = CreateDesktop("PaviseLatency-" + Guid.NewGuid().ToString("N"), IntPtr.Zero, IntPtr.Zero, 0, 0x01ff, IntPtr.Zero);
            if (desktop == IntPtr.Zero) return 3;
            var thread = new Thread(delegate() {
                int exit = 1;
                try { if (!SetThreadDesktop(desktop)) throw new InvalidOperationException("Cannot select private desktop");
                    Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false); Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
                    Run(args.Contains("--privacy-cache-only")); exit = 0;
                } catch (Exception error) { Console.Error.WriteLine(error); }
                finally { Console.Out.Flush(); Console.Error.Flush(); Environment.Exit(exit); }
            });
            thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join(); return 1;
        }
    }
}
#endif
