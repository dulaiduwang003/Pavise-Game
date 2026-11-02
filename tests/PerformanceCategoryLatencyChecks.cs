#if PAVISE_SELFTEST && PAVISE_PERFORMANCE_CATEGORY_LATENCY
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
    { public static bool TryHandleRuntimeMode(string[] args) { throw new InvalidOperationException("Latency fixture cannot run the product"); } }

    internal static class PerformanceCategoryLatencyChecks
    {
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        [DllImport("user32.dll")] private static extern bool RedrawWindow(IntPtr window, IntPtr rectangle, IntPtr region, uint flags);
        private sealed class Events
        {
            internal int Layouts, Invalidations, Moves, Resizes, Paints, Visibility, Presentations;
            internal void Reset() { Layouts = Invalidations = Moves = Resizes = Paints = Visibility = Presentations = 0; }
            internal void Observe(Control owner)
            {
                owner.Layout += delegate { Layouts++; }; owner.Invalidated += delegate { Invalidations++; };
                owner.LocationChanged += delegate { Moves++; }; owner.SizeChanged += delegate { Resizes++; };
                owner.Paint += delegate { Paints++; }; owner.VisibleChanged += delegate { Visibility++; };
                var card = owner as PerformanceFeatureCard;
                if (card != null) card.PresentationChanged += delegate { Presentations++; };
                foreach (Control child in owner.Controls) Observe(child);
            }
        }
        private sealed class Sample
        {
            internal string Phase;
            internal int Category;
            internal double Handler, Paint;
            internal long Bytes;
        }
        private static T Field<T>(PanelForm form, string name) { return (T)typeof(PanelForm).GetField(name, Hidden).GetValue(form); }
        private static void Call(PanelForm form, string name) { typeof(PanelForm).GetMethod(name, Hidden).Invoke(form, null); }
        private static long Allocated { get { return AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize; } }
        private static string Number(double number) { return number.ToString("F3", CultureInfo.InvariantCulture); }
        private static void NativePaint(Form form)
        {
            if (!RedrawWindow(form.Handle, IntPtr.Zero, IntPtr.Zero, 0x0001 | 0x0100 | 0x0080)) throw new InvalidOperationException("Native synchronous redraw failed");
            Application.DoEvents();
        }
        private static void Require(bool condition, string text)
        { if (!condition) throw new InvalidOperationException(text); }
        private static void QueueObservation(PanelForm form, bool force)
        { typeof(PanelForm).GetMethod("QueueEnvironmentObservation", Hidden).Invoke(form, new object[] { force }); }
        private static object Snapshot(PanelForm form)
        {
            object cycle = typeof(PanelForm).GetField("environmentObservation", Hidden).GetValue(form);
            return cycle.GetType().GetField("Snapshot", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(cycle);
        }
        private static PanelForm.EnvironmentGraphicsObservation Observation(string marker)
        {
            return new PanelForm.EnvironmentGraphicsObservation {
                HagsKnown = true, HagsSupported = true, HagsOn = true, HagsConfigured = 2,
                Windowed = new PanelForm.EnvironmentPreferenceObservation { Readable = true, Supported = true },
                Vrr = new PanelForm.EnvironmentPreferenceObservation { Readable = true, Supported = true },
                Tuning = new PanelForm.EnvironmentTuningObservation { BundleState = "off", BundleLabel = marker,
                    BundleDetail = "Synthetic full bundle diagnostic " + marker, StorageState = "off", StorageLabel = marker,
                    StorageDetail = "Synthetic full storage diagnostic " + marker }
            };
        }
        private static void PumpUntil(Func<bool> done, int timeout)
        {
            var clock = Stopwatch.StartNew();
            while (!done() && clock.ElapsedMilliseconds < timeout) { Application.DoEvents(); Thread.Sleep(2); }
            Require(done(), "Observation fixture timed out");
        }
        private static void VerifyEnvironmentReads(PanelForm form)
        {
            int uiThread = Thread.CurrentThread.ManagedThreadId, readThread = 0, reads = 0, heartbeat = 0;
            bool checkThreads = Control.CheckForIllegalCrossThreadCalls, wasLight = Theme.LightMode;
            Control.CheckForIllegalCrossThreadCalls = true;
            typeof(PanelForm).GetField("uiActive", Hidden).SetValue(form, true);
            Call(form, "ResetEnvironmentObservation");
            var observed = Observation("slow-native-fake");
            using (var entered = new ManualResetEvent(false))
            using (var release = new ManualResetEvent(false))
            using (var timer = new System.Windows.Forms.Timer { Interval = 10 }) {
                form.EnvironmentReadForTest = delegate {
                    Interlocked.Increment(ref reads); readThread = Thread.CurrentThread.ManagedThreadId;
                    entered.Set(); if (!release.WaitOne(5000)) throw new TimeoutException("Slow fake was not released");
                    return observed;
                };
                timer.Tick += delegate { heartbeat++; }; timer.Start();
                var watch = Stopwatch.StartNew(); QueueObservation(form, false);
                double queueMs = watch.Elapsed.TotalMilliseconds;
                PumpUntil(delegate { return entered.WaitOne(0); }, 1000);
                watch.Restart(); for (int n = 0; n < 5; n++) Call(form, "RefreshAutomationPage");
                double refreshMs = watch.Elapsed.TotalMilliseconds;
                var pumping = Stopwatch.StartNew();
                while (pumping.ElapsedMilliseconds < 120) { Application.DoEvents(); Thread.Sleep(2); }
                Require(readThread != uiThread && reads == 1, "Environment reads ran on UI or duplicated an in-flight query");
                Require(queueMs < 100 && refreshMs < 200 && heartbeat >= 3, "Slow native/settings fake blocked UI refresh or heartbeat");
                Require(Snapshot(form) == null, "Partially read observation became visible");
                Theme.SetLight(!wasLight); release.Set();
                PumpUntil(delegate { return ReferenceEquals(Snapshot(form), observed); }, 2000);
                Require(Field<SettingCard>(form, "cardHags").StatusColor.ToArgb() == Theme.Accent.ToArgb(), "Worker used an obsolete theme color");
                Require(Field<SettingCard>(form, "cardPerformanceEnvironmentBundle").Desc.Contains("slow-native-fake"), "Asynchronous diagnostics were lost");
                Console.WriteLine("OBSERVATION slow_fake_blocked_ms=120 queue_ms=" + Number(queueMs) + " refresh_5_ms=" + Number(refreshMs)
                    + " ui_thread=" + uiThread + " read_thread=" + readThread + " heartbeat=" + heartbeat + " native_reads_coalesced=" + reads);
            }

            // Delay worker completion and delivery independently so old page,
            // explicit invalidation and settings revisions can race deterministically.
            var queued = new Queue<Action>(); var posted = new Queue<Action>();
            form.EnvironmentQueueForTest = delegate(Action work) { queued.Enqueue(work); };
            form.EnvironmentPostForTest = delegate(Action apply) { posted.Enqueue(apply); };
            form.EnvironmentReadForTest = delegate { return Observation("new-revision"); };
            Call(form, "ResetEnvironmentObservation"); QueueObservation(form, false);
            queued.Dequeue()(); Require(posted.Count == 1, "No test delivery was queued");
            Call(form, "ResetEnvironmentObservation"); posted.Dequeue()();
            Require(Snapshot(form) == null && queued.Count == 0, "Old page generation changed the rebuilt controls");

            QueueObservation(form, false); queued.Dequeue()(); Settings.Save("ObservationRevisionForTest", true); posted.Dequeue()();
            Require(Snapshot(form) == null && queued.Count == 1, "Stale settings result was applied instead of reread");
            queued.Dequeue()(); posted.Dequeue()(); Require(Snapshot(form) != null, "Fresh settings observation was discarded");

            Call(form, "ResetEnvironmentObservation"); QueueObservation(form, false); QueueObservation(form, true);
            queued.Dequeue()(); posted.Dequeue()();
            Require(Snapshot(form) == null && queued.Count == 1, "Explicit force invalidation accepted an older observation");
            queued.Dequeue()(); posted.Dequeue()(); Require(Snapshot(form) != null, "Forced observation did not apply");
            Require(((SettingCard)Field<Toggle>(form, "swEnvNvVrr").Parent).Desc.Contains(Lang.T("set.nvvrr.na")), "Unsupported VRR explanation was lost");
            typeof(PanelForm).GetField("environmentBundleOverride", Hidden).SetValue(form, (bool?)true);
            typeof(PanelForm).GetField("environmentStorageOverride", Hidden).SetValue(form, (bool?)true);
            typeof(PanelForm).GetField("environmentQuantumOverride", Hidden).SetValue(form, (int?)8);
            Call(form, "SyncPerformanceTuningEnvironment");
            Require(Field<Toggle>(form, "swPerformanceEnvironmentBundle").Checked && Field<Toggle>(form, "swPerformanceEnvironmentStorage").Checked
                && Field<QuantumProfilePicker>(form, "swPerformanceEnvironmentQuantum").SelectedIndex == 8,
                "Pending manual choices were replaced with the older observation");
            form.EnvironmentReadForTest = null; form.EnvironmentQueueForTest = null; form.EnvironmentPostForTest = null;
            Theme.SetLight(wasLight); Control.CheckForIllegalCrossThreadCalls = checkThreads;
            typeof(PanelForm).GetField("uiActive", Hidden).SetValue(form, false); Call(form, "ResetEnvironmentObservation");
            Console.WriteLine("PASS background environment observations; native_and_strict_receipts_off_ui=true no_native_writes=true stale_page_settings_force_discarded=true palette_applied_on_ui=true");
        }
        private static void Summarize(List<Sample> samples)
        {
            foreach (var batch in samples.GroupBy(x => x.Phase))
            {
                double[] handler = batch.Select(x => x.Handler).OrderBy(x => x).ToArray(), paint = batch.Select(x => x.Paint).OrderBy(x => x).ToArray();
                int p95 = Math.Max(0, (int)Math.Ceiling(handler.Length * .95) - 1);
                Console.WriteLine("LATENCY phase=" + batch.Key + " samples=" + handler.Length + " handler_p50_ms=" + Number(handler[handler.Length / 2])
                    + " handler_p95_ms=" + Number(handler[p95]) + " handler_max_ms=" + Number(handler[handler.Length - 1])
                    + " paint_p50_ms=" + Number(paint[paint.Length / 2]) + " paint_p95_ms=" + Number(paint[p95])
                    + " mean_bytes=" + (long)batch.Average(x => x.Bytes));
            }
        }
        private static void Run(string output)
        {
            Paths.Data = Path.Combine(output, "data"); Directory.CreateDirectory(Paths.Data);
            Settings.UseTransientStoreForCurrentProcess(); Logger.ResetWriteBarrierForTest(); Logger.WritesEnabled = false;
            PanelForm.SuppressUiWorkersForTest = true; Dpi.Init(); Dpi.NoFit = true; Dpi.Scale = 1; Lang.Init(); Lang.Cur = 0;
            AppDomain.MonitoringIsEnabled = true;
            var core = new SuppressionCore(); var mode = new GameMode(Paths.Data, core);
            var samples = new List<Sample>();
            using (var icon = IconArt.MakeIcon(24))
            using (var form = new PanelForm(new Tamer(core), mode, icon, false))
            using (var records = new StreamWriter(Path.Combine(output, "category-latency.tsv")))
            {
                form.ShowInTaskbar = false; form.Location = new Point(-32000, -32000); form.StartPosition = FormStartPosition.Manual;
                foreach (var privacy in PerformanceWorkspaceLayout.Descendants(form).OfType<PrivacyOptionsPanel>()) {
                    privacy.InspectionVisibleForTest = delegate { return false; };
                    privacy.StateForTest = delegate { return new PrivacyResult { State = "restored", Supported = true, Detail = "Synthetic latency observation" }; };
                    privacy.SetForTest = delegate { throw new InvalidOperationException("Latency benchmark cannot modify system preferences"); };
                    privacy.RefreshForTest = delegate { throw new InvalidOperationException("Latency benchmark cannot queue native inspections"); };
                    privacy.SyncCached();
                }
                form.Show(); form.ShowPageForShot(PageId.Policy); Call(form, "StopPageReveal"); Application.DoEvents();
                if (form.UiActive || mode.Enabled) throw new InvalidOperationException("Fixture started real workers");
                var categories = Field<PerformanceCategoryBar>(form, "performanceCategories");
                var panels = Field<DBPanel[]>(form, "performanceTabPanels");
                var events = new Events(); events.Observe(Field<DBPanel>(form, "pagePolicy"));
                UiClock.Suspended = false; NativePaint(form);
                records.WriteLine("phase\tcategory\thandler_ms\tpaint_ms\tallocated_bytes\tlayout\tinvalidated\tmoves\tresizes\tpaints\tvisibility\tpresentation");
                foreach (string phase in new[] { "cold", "warm", "ui-active-readonly" })
                {
                    if (phase == "ui-active-readonly") typeof(PanelForm).GetField("uiActive", Hidden).SetValue(form, true);
                    int count = phase == "cold" ? 4 : phase == "warm" ? 32 : 12;
                    for (int n = 0; n < count; n++)
                    {
                        int category = (n + 1) % 4; events.Reset(); long allocated = Allocated;
                        var clock = Stopwatch.StartNew(); categories.Select(category, true); double handler = clock.Elapsed.TotalMilliseconds;
                        clock.Restart(); NativePaint(form); double paint = clock.Elapsed.TotalMilliseconds;
                        long bytes = Allocated - allocated;
                        samples.Add(new Sample { Phase = phase, Category = category, Handler = handler, Paint = paint, Bytes = bytes });
                        records.WriteLine(phase + "\t" + category + "\t" + Number(handler) + "\t" + Number(paint) + "\t" + bytes
                            + "\t" + events.Layouts + "\t" + events.Invalidations + "\t" + events.Moves + "\t" + events.Resizes
                            + "\t" + events.Paints + "\t" + events.Visibility + "\t" + events.Presentations);
                    }
                }
                typeof(PanelForm).GetField("uiActive", Hidden).SetValue(form, false);
                categories.Select(0, true); NativePaint(form);
                events.Reset(); long before = Allocated; var watch = Stopwatch.StartNew();
                for (int n = 0; n < 100; n++) ((PerformanceFeatureGrid)panels[0]).Reflow();
                Console.WriteLine("MICRO unchanged_reflow_100_ms=" + Number(watch.Elapsed.TotalMilliseconds) + " bytes=" + (Allocated - before)
                    + " layout=" + events.Layouts + " invalidated=" + events.Invalidations + " moves=" + events.Moves + " presentation=" + events.Presentations);
                events.Reset(); before = Allocated; watch.Restart(); var card0 = PerformanceWorkspaceLayout.Cards(panels[0]).First();
                for (int n = 0; n < 1000; n++) card0.ArrangePresentation();
                Console.WriteLine("MICRO unchanged_card_arrange_1000_ms=" + Number(watch.Elapsed.TotalMilliseconds) + " bytes=" + (Allocated - before)
                    + " layout=" + events.Layouts + " invalidated=" + events.Invalidations);
                Summarize(samples);
                VerifyEnvironmentReads(form);
                Console.WriteLine("PASS performance category latency; private_desktop=true workers=false settings=transient animations_preserved=true measured_native_reads=SELFTEST_GUARDED");
            }
        }
        [STAThread] private static int Main(string[] args)
        {
            return PerformanceWorkspaceLayout.PrivateDesktop(delegate { Directory.CreateDirectory(args[0]); Run(args[0]); });
        }
    }
}
#endif
