#if PAVISE_SELFTEST && PAVISE_WORKFLOW_REDESIGN
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PaviseApp
{
    internal static partial class SelfTests
    { public static bool TryHandleRuntimeMode(string[] args) { return false; } }

    internal static class WorkflowRedesignRegressionRunner
    {
        private static int checks;
        private static void Check(bool condition, string reason)
        { if (!condition) throw new InvalidOperationException(reason); checks++; }
        private sealed class Threads : IFrameThreads
        {
            internal readonly Dictionary<int, int> Values = new Dictionary<int, int> { { 101, 0 }, { 102, 0 } };
            internal int DeniedTid, Writes;
            internal Action OnWrite;
            public int Read(FrameThreadReceipt id, out int priority)
            { priority = Values[id.Tid]; return 0; }
            public bool Set(FrameThreadReceipt id, int expected, int desired)
            {
                if (Values[id.Tid] != expected) return false;
                if (desired == 1 && id.Tid == DeniedTid) return false;
                string saved;
                Check(Settings.TryLoadStr(FrameCoordination.JournalKey, out saved) && saved.Length > 0, "Thread mutation requires recovery record");
                Writes++; Values[id.Tid] = desired; if (OnWrite != null) OnWrite(); return true;
            }
        }
        private static FrameCoordinationSample Sample()
        { return new FrameCoordinationSample { Pid = 505, Birth = 999, PresentTid = 101, WorkerTid = 102, Valid = true,
            Threads = new Dictionary<int, long> { { 101, 1001 }, { 102, 1002 } } }; }

        private static void CheckThreadIsolation()
        {
            Settings.UseTransientStoreForCurrentProcess(); ActionFailureStore.ReloadForTest();
            var fake = new Threads { DeniedTid = 102 };
            FrameCoordination.SetPlatformForTest(fake);
            try {
                Check(!FrameCoordination.ApplyIsolated(Sample(), delegate { return true; }, "game-a.exe"), "First write failure rejects the action");
                Check(fake.Values[101] == 0 && fake.Values[102] == 0, "Partial apply immediately rolled back");
                Check(ActionFailureStore.IsIsolated(FrameCoordination.ActionKey, "game-a.exe"), "First failure persists isolation");
                int writes = fake.Writes;
                fake.DeniedTid = 0;
                Check(!FrameCoordination.ApplyIsolated(Sample(), delegate { return true; }, "game-a.exe"), "No automatic retry");
                Check(fake.Writes == writes, "Blocked action makes no writes");
                ActionFailureStore.ReloadForTest();
                Check(!FrameCoordination.ApplyIsolated(Sample(), delegate { return true; }, "game-a.exe"), "Isolation survives cache reload");
                Check(FrameCoordination.ApplyIsolated(Sample(), delegate { return true; }, "game-b.exe"), "Other target remains available");
                Check(FrameCoordination.Restore(), "Other target restores");
                ActionFailureRecord record = ActionFailureStore.Records()[0];
                Check(ActionFailureStore.RequestRetry(record.Key), "User can authorize one retry");
                Check(!ActionFailureStore.RequestRetry(record.Key), "Cannot stack retry permits");
                Check(FrameCoordination.ApplyIsolated(Sample(), delegate { return true; }, "game-a.exe"), "Authorized retry succeeds");
                Check(!ActionFailureStore.IsIsolated(FrameCoordination.ActionKey, "game-a.exe"), "Verified success releases isolation");
                Check(FrameCoordination.Restore(), "Retry preserves restoration");
                Check(fake.Values[101] == 0 && fake.Values[102] == 0, "Original values recovered");

                bool alive = true;
                fake.OnWrite = delegate { alive = false; };
                Check(!FrameCoordination.ApplyIsolated(Sample(), delegate { return alive; }, "cancelled.exe"), "Cancellation ends apply");
                fake.OnWrite = null;
                Check(!ActionFailureStore.IsIsolated(FrameCoordination.ActionKey, "cancelled.exe"), "Normal cancellation is not a fault");
                Check(fake.Values[101] == 0 && fake.Values[102] == 0, "Cancelled partial apply restored");

                fake.OnWrite = delegate { if (fake.Values[101] == 1) fake.Values[102] = 2; };
                Check(!FrameCoordination.ApplyIsolated(Sample(), delegate { return true; }, "external-change.exe"), "External update relinquishes the conflicting apply");
                fake.OnWrite = null;
                Check(fake.Values[101] == 0 && fake.Values[102] == 2, "External thread state preserved");
                Check(!ActionFailureStore.IsIsolated(FrameCoordination.ActionKey, "external-change.exe"), "External change is not misclassified as execution fault");
            } finally { fake.OnWrite = null; fake.DeniedTid = 0; FrameCoordination.Restore(); FrameCoordination.SetPlatformForTest(null); }
            Console.WriteLine("PASS thread first-fault isolation, partial rollback, reload, independent targets, manual retry, cancellation and external changes");
        }

        private sealed class BackgroundProcess
        {
            internal readonly SuppressionCore Core = new SuppressionCore(delegate { return SuppressionCore.RestoreResult.Protected; }, true);
            internal int Identity, Current, Applies, Writes, Restores;
            internal bool ApplyWorks, RestoreWorks = true, ExitDuringApply, CancelDuringApply;
            internal BackgroundProcess()
            {
                Core.EntryIdentityForTest = delegate { return Identity; };
                Core.EntryMatchesForTest = delegate { return Current == 0; };
                Core.EntryRestoreForTest = delegate { Restores++; if (!RestoreWorks) return false; Current = 0; return true; };
                Core.EntryApplyForTest = delegate
                {
                    string[] names;
                    Check(Settings.TryLoadStoredNames(ActionFailureStore.AttemptPrefix, out names), "Background execution intent readable before native mutation");
                    bool saved = false;
                    foreach (string name in names) { string text; saved |= Settings.TryLoadStr(name, out text) && text.Length > 0; }
                    Check(saved, "Background native mutation has a durable intent");
                    Applies++; Writes++; Current = 1;
                    if (ExitDuringApply) Identity = 1;
                    if (CancelDuringApply) throw new OperationCanceledException();
                    return ApplyWorks;
                };
            }
            internal bool Apply(string target) { return Core.ApplyIsolationForTest(606, target, 777); }
        }
        private static void CheckBackgroundIsolation()
        {
            const string target = @"C:\Fixture\Background.exe";
            Settings.UseTransientStoreForCurrentProcess();
            var failed = new BackgroundProcess();
            Check(!failed.Apply(target), "First background native failure rejects apply");
            Check(failed.Applies == 1 && failed.Writes == 1 && failed.Restores == 1 && failed.Current == 0, "First background failure stops new applications and immediately restores");
            Check(ActionFailureStore.IsIsolated(SuppressionCore.FailureAction, target), "Background first failure persists isolation");
            bool[] flags = failed.Core.IsolationFlagsForTest(606);
            Check(flags[0] && flags[1] && flags[2] && !flags[3], "Background isolation retains original journal and restored status");
            failed.ApplyWorks = true;
            Check(!failed.Apply(target) && failed.Applies == 1, "Background action cannot automatically retry");
            ActionFailureStore.ReloadForTest();
            Check(!failed.Apply(target) && failed.Applies == 1, "Background isolation survives process-local cache reset");
            var other = new BackgroundProcess { ApplyWorks = true };
            Check(other.Apply(@"C:\Fixture\Other.exe"), "Background failure leaves unrelated targets available");

            string key = ActionFailureStore.RecordKey(SuppressionCore.FailureAction, target);
            failed.Current = 1; failed.RestoreWorks = false;
            Check(ActionFailureStore.RequestRetry(key), "Explicit background retry accepted");
            Check(!failed.Apply(target) && failed.Applies == 1 && failed.Current == 1, "Background retry cannot apply before restoring original state");
            Check(!ActionFailureStore.IsRetryPending(SuppressionCore.FailureAction, target), "Failed pre-retry restoration revokes permission");
            flags = failed.Core.IsolationFlagsForTest(606);
            Check(flags[0] && !flags[1] && flags[2], "Background failed recovery retains journal and pending state");
            failed.RestoreWorks = true;
            Check(!failed.Apply(target) && failed.Applies == 1, "Recovery availability alone cannot grant an application retry");
            Check(ActionFailureStore.RequestRetry(key), "New explicit background retry accepted after failed recovery");
            Check(failed.Apply(target) && failed.Applies == 2 && !ActionFailureStore.IsIsolated(SuppressionCore.FailureAction, target), "Background verified retry clears isolation after restoring first");

            Settings.UseTransientStoreForCurrentProcess();
            var exited = new BackgroundProcess { ExitDuringApply = true };
            Check(!exited.Apply(target) && exited.Applies == 1 && exited.Restores == 0, "A confirmed exited process is skipped without restoration writes");
            Check(ActionFailureStore.Records().Count == 0 && exited.Core.IsolationFlagsForTest(606)[1], "Ordinary process exit is not a background execution fault");
            var unknown = new BackgroundProcess { Identity = 2 };
            Check(!unknown.Apply(target) && unknown.Applies == 0 && ActionFailureStore.Records().Count == 0, "Unknown background identity is skipped before mutation");
            var cancelled = new BackgroundProcess { CancelDuringApply = true };
            Check(!cancelled.Apply(target) && cancelled.Current == 0 && cancelled.Restores == 1 && ActionFailureStore.Records().Count == 0, "Normally cancelled background partial write restores before intent removal");
            Settings.UseTransientStoreForCurrentProcess();
            var cancelledRetry = new BackgroundProcess();
            Check(!cancelledRetry.Apply(target) && ActionFailureStore.RequestRetry(ActionFailureStore.RecordKey(SuppressionCore.FailureAction, target)), "Cancelled retry fixture starts with one explicit permit");
            cancelledRetry.CancelDuringApply = true;
            Check(!cancelledRetry.Apply(target) && cancelledRetry.Current == 0 && !ActionFailureStore.IsRetryPending(SuppressionCore.FailureAction, target), "Cancelled partial retry cannot reuse its consumed permit");
            Check(!cancelledRetry.Apply(target) && cancelledRetry.Applies == 2, "One cancelled user retry does not admit another application");

            Settings.UseTransientStoreForCurrentProcess();
            var completed = new BackgroundProcess { ApplyWorks = true };
            ActionFailureStore.SaveForTest = delegate(string name, string value) {
                Settings.SaveStrDurable(name, value);
                return value.Length > 0 || !name.StartsWith(ActionFailureStore.AttemptPrefix, StringComparison.Ordinal);
            };
            Check(!completed.Apply(target) && completed.Current == 0 && completed.Restores == 1, "Background completion persistence failure immediately rolls back");
            ActionFailureStore.ReloadForTest();
            Check(ActionFailureStore.IsIsolated(SuppressionCore.FailureAction, target), "Background completion failure remains isolated across reload");
            Console.WriteLine("PASS background durable admission, first failure, rollback, no automatic retry, one user retry, failed pre-retry recovery, ordinary exit, unknown identity and cancelled apply");
        }

        private static PillButton Button(Control parent, string name)
        {
            Control[] found = parent.Controls.Find(name, true);
            if (found.Length != 1) throw new InvalidOperationException("Expected one button: " + name);
            return (PillButton)found[0];
        }
        private static void CheckMaintenanceLists(string output)
        {
            var data = new List<MaintenanceListItem> {
                new MaintenanceListItem { Key = "failed", Title = "已停用的动作", Status = "已停用", Detail = "读取原值失败", Retryable = true }
            };
            int queries = 0, attempts = 0;
            bool failRead = false;
            var queued = new Queue<Action>();
            using (var owner = HiddenOwner())
            using (var panel = new MaintenanceListPanel(delegate { queries++; if (failRead) throw new IOException("Injected unreadable failure records"); return new List<MaintenanceListItem>(data); },
                delegate(string key) { attempts++; return null; }))
            {
                owner.Controls.Add(panel);
                panel.QueueForTest = delegate(Action work) { queued.Enqueue(work); };
                panel.PostForTest = delegate(Action result) { result(); };
                panel.RefreshForTest(); panel.RefreshForTest();
                Check(queued.Count == 1 && panel.BusyForTest, "Duplicate refresh cannot queue another operation");
                queued.Dequeue()();
                Check(queries == 1 && panel.ReadableForTest && panel.ItemCountForTest == 1, "Disabled action query displayed");
                Check(panel.Controls.Find("maintenanceClearHistory", true).Length == 0
                    && panel.Controls.Find("maintenanceTabs", true).Length == 0, "Snapshot history controls removed");
                failRead = true; panel.RefreshForTest(); queued.Dequeue()();
                Check(!panel.ReadableForTest && !Button(panel, "maintenanceRetry").Enabled, "Unreadable records disable retries");
                Button(panel, "maintenanceRetry").PerformClick();
                Check(queued.Count == 0 && attempts == 0, "Stale list cannot retry an action");
                failRead = false; panel.RefreshForTest(); queued.Dequeue()();
                Check(panel.ReadableForTest, "Readable storage can recover on explicit refresh");
                owner.Show();
                foreach (float scale in new[] { 1f, 1.5f, 2f }) {
                    Dpi.Scale = scale;
                    panel.Size = new Size(Theme.S(708), Theme.S(330));
                    using (var bitmap = new Bitmap(panel.Width, panel.Height)) {
                        panel.DrawToBitmap(bitmap, panel.ClientRectangle);
                        bitmap.Save(Path.Combine(output, "maintenance-" + ((int)(scale * 100)).ToString() + ".png"));
                    }
                    Check(Button(panel, "maintenanceRefresh").Right <= panel.ClientSize.Width, "Header action stays within viewport");
                }
                Dpi.Scale = 1;
            }
            int retries = 0;
            using (var panel = new MaintenanceListPanel(delegate {
                return new List<MaintenanceListItem> { new MaintenanceListItem { Key = "fault", Title = "故障动作", Detail = "读回失败", Status = "已停用", Retryable = true } };
            }, delegate(string key) { Check(key == "fault", "Retry targets selected record"); retries++; return null; }))
            {
                panel.QueueForTest = delegate(Action work) { queued.Enqueue(work); }; panel.PostForTest = delegate(Action result) { result(); };
                panel.RefreshForTest(); queued.Dequeue()();
                Button(panel, "maintenanceRetry").PerformClick();
                Check(queued.Count == 1, "Selected retry queues once");
                Check(!Button(panel, "maintenanceRetry").Enabled, "Retry button blocked while busy");
                queued.Dequeue()(); Check(retries == 1, "One explicit retry invokes one operation");
                panel.RefreshForTest(); panel.Dispose(); queued.Dequeue()();
                Check(retries == 1, "Disposed result cannot trigger another retry");
            }
            Console.WriteLine("PASS disabled action list concurrency, unreadable storage, DPI geometry, one retry and disposal");
        }

        private sealed class HiddenTestForm : Form
        { protected override bool ShowWithoutActivation { get { return true; } } }
        private static Form HiddenOwner()
        { return new HiddenTestForm { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Opacity = 0,
            Location = new Point(-32000, -32000), Size = new Size(1600, 900), FormBorderStyle = FormBorderStyle.None }; }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateDesktop(string name, IntPtr device, IntPtr mode, uint flags, uint access, IntPtr security);
        [DllImport("user32.dll")] private static extern bool SetThreadDesktop(IntPtr desktop);
        [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
        [STAThread]
        private static int Main(string[] args)
        {
            IntPtr desktop = IntPtr.Zero;
            try {
                string output = args[0]; Directory.CreateDirectory(output);
                desktop = CreateDesktop("PaviseWorkflow-" + Guid.NewGuid().ToString("N"), IntPtr.Zero, IntPtr.Zero, 0, 0x01FF, IntPtr.Zero);
                if (desktop == IntPtr.Zero || !SetThreadDesktop(desktop))
                    Console.WriteLine("INFO private desktop unavailable; controls render in a transparent offscreen owner without activation");
                UiClock.Frozen = true;
                CheckThreadIsolation(); CheckBackgroundIsolation(); CheckMaintenanceLists(output);
                Console.WriteLine("PASS workflow redesign " + checks + " assertions; no app startup or native system settings");
                return 0;
            } catch (Exception error) { Console.WriteLine(error); return 1; }
            finally { if (desktop != IntPtr.Zero) CloseDesktop(desktop); }
        }
    }
}
#endif
