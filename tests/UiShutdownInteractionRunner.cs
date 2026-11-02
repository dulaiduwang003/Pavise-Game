#if PAVISE_SELFTEST && PAVISE_UI_SHUTDOWN_RUNNER
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using System.Windows.Forms;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        public static bool TryHandleRuntimeMode(string[] args) { throw new InvalidOperationException("Normal runtime is forbidden in the UI audit"); }
    }
    internal static class UiShutdownInteractionAudit
    {
        private static int assertions;
        private static void Check(bool result, string description) { Interlocked.Increment(ref assertions); if (!result) throw new InvalidOperationException(description); }
        private sealed class Fixture : IDisposable
        {
            internal readonly PanelForm Form = (PanelForm)FormatterServices.GetUninitializedObject(typeof(PanelForm));
            internal readonly Queue<Action> Workers = new Queue<Action>(), Posts = new Queue<Action>();
            private readonly List<Control> controls = new List<Control>();
            internal PillButton ShaderButton;
            internal PillButton ResetButton, UninstallButton;
            internal readonly GameMode Game = (GameMode)FormatterServices.GetUninitializedObject(typeof(GameMode));
            internal int CleanCalls, Warnings, Messages;
            internal Fixture()
            {
                GC.SuppressFinalize(Form);
                FieldInfo propertyStore = typeof(Control).GetField("propertyStore", BindingFlags.Instance | BindingFlags.NonPublic);
                Check(propertyStore != null, "The inert Control property store could not be initialized");
                propertyStore.SetValue(Form, Activator.CreateInstance(propertyStore.FieldType, true));
                FieldInfo window = typeof(Control).GetField("window", BindingFlags.Instance | BindingFlags.NonPublic);
                Check(window != null, "The inert Control native-window owner could not be initialized");
                window.SetValue(Form, Activator.CreateInstance(window.FieldType, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                    null, new object[] { Form }, CultureInfo.InvariantCulture));
                Check(!Form.IsHandleCreated, "Initializing the inert Control state created a native form window");
                typeof(GameMode).GetField("sync", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(Game, new object());
                Set("gameMode", Game);
                Form.UiStateWorkQueueForTest = action => Workers.Enqueue(action);
                Form.UiStatePostForTest = action => Posts.Enqueue(action);
                Form.SettingsConfirmationForTest = delegate { return true; };
                Form.ShaderCleanForTest = delegate { CleanCalls++; return new CacheSweep.Result { FreedBytes = 1024 }; };
                Form.ShaderMeasureForTest = delegate { return 2048; };
                Form.ShaderCleanupMessageForTest = delegate(bool failed, string message) { Messages++; if (failed) Warnings++; };
                ShaderButton = Add(new PillButton("inert"));
                Set("btnShaderGo", ShaderButton);
                Set("cardShader", Add(new SettingCard()));
                ResetButton = Add(new PillButton("reset")); UninstallButton = Add(new PillButton("uninstall"));
                Set("btnWipeAll", ResetButton); Set("btnUninstall", UninstallButton);
                ReplaceAuditControls();
            }
            internal T Add<T>(T control) where T : Control { controls.Add(control); return control; }
            internal void Set(string field, object value) { typeof(PanelForm).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(Form, value); }
            internal object Get(string field) { return typeof(PanelForm).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(Form); }
            internal void SetGameActive(bool active) { typeof(GameMode).GetField("active", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(Game, active); }
            internal void Call(string method, params object[] args)
            {
                try { typeof(PanelForm).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(Form, args); }
                catch (TargetInvocationException error) { throw new InvalidOperationException("UI fixture callback failed: " + method, error.InnerException); }
            }
            internal void ReplaceAuditControls()
            {
                Set("auditScan", Add(new ScanView())); Set("auditScroll", Add(new DBPanel()));
                Set("btnAuditStart", Add(new PillButton("start"))); Set("btnAuditRun", Add(new PillButton("run")));
                Set("btnAuditCancel", Add(new PillButton("cancel"))); Set("btnAuditFixAll", Add(new PillButton("fix")));
                Set("lblAuditStatus", Add(new Label()));
            }
            internal void DrainPosts() { while (Posts.Count != 0) Posts.Dequeue()(); }
            public void Dispose()
            {
                Workers.Clear(); Posts.Clear();
                foreach (Control control in controls) { Check(!control.IsHandleCreated, "Fixture unexpectedly acquired a native window"); control.Dispose(); }
            }
        }
        private static void ShaderConfirmationReentry()
        {
            using (var f = new Fixture())
            {
                bool first = true;
                f.Form.SettingsConfirmationForTest = delegate { if (first) { first = false; f.Call("OnShaderClean", f.ShaderButton); } return true; };
                f.Call("OnShaderClean", f.ShaderButton);
                Check(f.Workers.Count == 1, "Reentrant confirmation queued duplicate cleanup");
                f.Workers.Dequeue()(); f.DrainPosts();
                Check(f.CleanCalls == 1 && f.ShaderButton.Enabled && f.Messages == 1, "Cleanup completion did not settle");
            }
        }
        private static void ShaderFailuresAndStaleResult()
        {
            using (var f = new Fixture())
            {
                f.Form.ShaderCleanForTest = delegate { throw new IOException("inert cleanup failure"); };
                f.Call("OnShaderClean", f.ShaderButton); f.Workers.Dequeue()(); f.DrainPosts();
                Check(f.Warnings == 1 && f.ShaderButton.Enabled, "Cleanup exception left the switch busy");
                f.Form.UiStateWorkQueueForTest = delegate { throw new InvalidOperationException("inert queue rejection"); };
                f.Call("OnShaderClean", f.ShaderButton);
                Check(f.Warnings == 2 && f.ShaderButton.Enabled, "Queue rejection left the switch busy");
                f.Form.UiStateWorkQueueForTest = action => f.Workers.Enqueue(action);
                f.Form.ShaderCleanForTest = delegate { f.CleanCalls++; return new CacheSweep.Result(); };
                f.Call("OnShaderClean", f.ShaderButton); f.Workers.Dequeue()();
                f.Call("OnShaderClean", f.ShaderButton);
                f.Posts.Dequeue()();
                Check(!f.ShaderButton.Enabled && f.Messages == 2, "Old completion enabled a new running operation");
                f.Workers.Dequeue()(); f.DrainPosts();
                Check(f.ShaderButton.Enabled && f.CleanCalls == 2 && f.Messages == 3, "New cleanup completion lost");
            }
        }
        private static void AuditRebuildAndCancel()
        {
            using (var f = new Fixture())
            {
                bool cancellationSeen = false;
                f.Form.AuditCollectForTest = delegate(int window, Func<bool> cancel, Action<float, string> progress)
                { Check(window == 120000, "Explicit audit observation duration changed"); cancellationSeen = cancel(); return null; };
                f.Call("StartAudit");
                Check(f.Workers.Count == 1, "Audit not queued");
                f.Call("StartAudit"); Check(f.Workers.Count == 1, "Busy audit queued duplicate sampler");
                f.ReplaceAuditControls(); f.Call("RestoreAuditPagePresentation");
                Check(!((Control)f.Get("btnAuditStart")).Visible && ((Control)f.Get("btnAuditCancel")).Visible,
                    "Rebuild presented an ineffective start button during a running audit");
                f.Set("auditCancelRequested", true); f.Call("RestoreAuditPagePresentation");
                Check(!((Control)f.Get("btnAuditCancel")).Enabled, "Rebuild re-enabled a pending cancellation");
                f.Workers.Dequeue()(); f.DrainPosts();
                Check(cancellationSeen && (int)f.Get("auditBusy") == 0 && ((Control)f.Get("btnAuditStart")).Visible,
                    "Cancelled audit did not release the UI");
                f.Call("StartAudit"); f.Set("runtimeShutdownBusy", true); f.Workers.Dequeue()(); f.DrainPosts();
                Check((int)f.Get("auditBusy") == 0, "Disposed/shutdown completion retained the busy slot");
            }
        }
        private static void ShaderRebuiltButtonFollowsWork()
        {
            using (var f = new Fixture())
            {
                f.Call("OnShaderClean", f.ShaderButton);
                PillButton old = f.ShaderButton; old.Dispose();
                f.ShaderButton = f.Add(new PillButton("new")); f.Set("btnShaderGo", f.ShaderButton);
                f.Call("SyncShaderCleanupPresentation");
                Check(!f.ShaderButton.Enabled, "Rebuilt cache button looked actionable during cleanup");
                f.Workers.Dequeue()(); f.DrainPosts();
                Check(f.ShaderButton.Enabled && f.CleanCalls == 1, "Rebuilt cache button did not resume after completion");
            }
        }
        private static void AuditLateProgressCannotReplaceNewRun()
        {
            using (var f = new Fixture())
            {
                Action<float, string> oldProgress = null;
                f.Form.AuditCollectForTest = delegate(int window, Func<bool> cancel, Action<float, string> progress)
                { oldProgress = progress; return null; };
                f.Call("StartAudit"); f.Workers.Dequeue()(); f.DrainPosts();
                f.Call("StartAudit");
                string caption = ((ScanView)f.Get("auditScan")).Caption;
                oldProgress(.5f, "old operation"); f.DrainPosts();
                Check(((ScanView)f.Get("auditScan")).Caption == caption, "Old provider replaced a new operation's progress");
                f.Workers.Dequeue()(); f.DrainPosts();
            }
        }
        private static void AuditSchedulingFailures()
        {
            using (var f = new Fixture())
            {
                f.Form.UiStateWorkQueueForTest = delegate { throw new InvalidOperationException("inert queue rejection"); };
                f.Call("StartAudit"); Check((int)f.Get("auditBusy") == 0, "Queue rejection retained the audit slot");
                f.Form.UiStateWorkQueueForTest = action => f.Workers.Enqueue(action);
                f.Form.AuditCollectForTest = delegate { throw new IOException("inert audit failure"); };
                f.Call("StartAudit"); f.Workers.Dequeue()(); f.DrainPosts();
                Check((int)f.Get("auditBusy") == 0, "Audit provider exception retained busy state");
                f.Form.UiStatePostForTest = delegate { throw new InvalidOperationException("inert post rejection"); };
                f.Call("StartAudit"); f.Workers.Dequeue()();
                Check((int)f.Get("auditBusy") == 0, "UI post rejection retained busy state");
            }
        }
        // 修复、撤销、按钮状态过期后只重建检查项，沿用上一轮采样；只有重新体检才再采样。
        private static void AuditFixRefreshesWithoutResampling()
        {
            using (var f = new Fixture())
            {
                int collects = 0, refreshes = 0;
                AuditReport refreshedFrom = null; GpuIrqResult refreshedIrq = null;
                var measured = new GpuIrqResult { Outcome = GpuIrqOutcome.NeedsPlacement };
                f.Form.AuditCollectForTest = delegate(int window, Func<bool> cancel, Action<float, string> progress)
                {
                    collects++;
                    return new AuditReport { MeasureOk = true, MeasureWindowMs = window, CpuBusy = 37.5, GpuIrq = measured };
                };
                f.Form.AuditRefreshForTest = delegate(AuditReport previous, GpuIrqResult irqFix)
                {
                    refreshes++; refreshedFrom = previous; refreshedIrq = irqFix;
                    return new AuditReport { MeasureOk = previous.MeasureOk, MeasureWindowMs = previous.MeasureWindowMs,
                        CpuBusy = previous.CpuBusy, GpuIrq = GpuIrqCheck.AfterFix(previous.GpuIrq, irqFix) };
                };
                typeof(PanelForm).GetField("elevated", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(f.Form, true);
                f.Call("StartAudit"); f.Workers.Dequeue()(); f.DrainPosts();
                var first = (AuditReport)f.Get("auditReport");
                Check(collects == 1 && first != null && (int)f.Get("auditBusy") == 0, "The measured checkup did not complete");

                Type fixType = typeof(PanelForm).GetNestedType("AuditFix", BindingFlags.NonPublic);
                Func<Func<bool>, Func<bool>, Action, Action, object> makeFix = delegate(Func<bool> canFix, Func<bool> canRevert, Action fix, Action revert)
                {
                    object item = Activator.CreateInstance(fixType, true);
                    fixType.GetField("CanFix").SetValue(item, canFix);
                    fixType.GetField("CanRevert").SetValue(item, canRevert);
                    fixType.GetField("Fix").SetValue(item, fix);
                    fixType.GetField("Revert").SetValue(item, revert);
                    return item;
                };
                int fixes = 0, reverts = 0;
                bool fixable = true;
                object item1 = makeFix(delegate { return fixable; }, delegate { return !fixable; },
                    delegate { fixes++; fixable = false; }, delegate { reverts++; fixable = true; });

                f.Call("OnAuditFixClick", item1);
                Check(fixes == 1 && f.Workers.Count == 1 && (int)f.Get("auditBusy") == 1, "A fix did not queue a refresh");
                f.Call("OnAuditFixClick", item1);
                Check(f.Workers.Count == 1 && reverts == 0, "A running refresh admitted another fix");
                f.Workers.Dequeue()(); f.DrainPosts();
                Check(collects == 1, "Fixing one item re-ran the measured checkup");
                Check(refreshes == 1 && ReferenceEquals(refreshedFrom, first) && refreshedIrq == null, "A fix did not refresh from the previous report");
                var second = (AuditReport)f.Get("auditReport");
                Check(!ReferenceEquals(second, first) && second.CpuBusy == 37.5 && second.MeasureWindowMs == 120000
                    && ReferenceEquals(second.GpuIrq, measured) && (int)f.Get("auditBusy") == 0, "The refresh lost the previous measurement");

                f.Call("OnAuditFixClick", item1); f.Workers.Dequeue()(); f.DrainPosts();
                Check(reverts == 1 && collects == 1 && refreshes == 2, "Reverting one item re-ran the measured checkup");

                object stale = makeFix(delegate { return false; }, delegate { return false; }, null, null);
                f.Call("OnAuditFixClick", stale); f.Workers.Dequeue()(); f.DrainPosts();
                Check(collects == 1 && refreshes == 3, "A stale fix button re-ran the measured checkup");

                var cancelledIrq = new GpuIrqResult { Outcome = GpuIrqOutcome.Cancelled };
                f.Set("auditIrqFix", cancelledIrq);
                f.Call("OnAuditFixClick", stale); f.Workers.Dequeue()(); f.DrainPosts();
                Check(ReferenceEquals(refreshedIrq, cancelledIrq) && f.Get("auditIrqFix") == null, "The interrupt fix result was not handed to the refresh once");
                Check(ReferenceEquals(((AuditReport)f.Get("auditReport")).GpuIrq, measured), "A cancelled interrupt fix replaced the measured result");

                var kept = (AuditReport)f.Get("auditReport");
                f.Form.AuditRefreshForTest = delegate { throw new IOException("inert refresh failure"); };
                f.Call("OnAuditFixClick", stale); f.Workers.Dequeue()(); f.DrainPosts();
                Check((int)f.Get("auditBusy") == 0 && ReferenceEquals(f.Get("auditReport"), kept) && collects == 1,
                    "A failed refresh kept the busy slot, dropped the report or re-sampled");

                f.Call("StartAudit"); f.Workers.Dequeue()(); f.DrainPosts();
                Check(collects == 2, "Re-run no longer measures");
            }
        }

        // 中断修复结果换算成体检口径：与重新体检得到的结论一致，不必再采样。
        private static void AuditInterruptFixMatchesCheckup()
        {
            var previous = new GpuIrqResult { Outcome = GpuIrqOutcome.NeedsPlacement };
            var sample = new GpuIrqSample { Name = "fixture" };
            Settings.SaveStr(GpuIrqCheck.StateKey, "");
            try
            {
                Check(ReferenceEquals(GpuIrqCheck.AfterFix(previous, null), previous), "No interrupt fix replaced the measurement");
                Check(ReferenceEquals(GpuIrqCheck.AfterFix(previous, new GpuIrqResult { Outcome = GpuIrqOutcome.Cancelled }), previous), "A cancelled fix replaced the measurement");
                Check(ReferenceEquals(GpuIrqCheck.AfterFix(previous, new GpuIrqResult { Outcome = GpuIrqOutcome.Failed }), previous), "A failed fix replaced the measurement");
                GpuIrqResult verified = GpuIrqCheck.AfterFix(previous, new GpuIrqResult { Outcome = GpuIrqOutcome.Verified, Gpu = sample });
                Check(verified.Outcome == GpuIrqOutcome.Clear && ReferenceEquals(verified.Gpu, sample), "A verified placement is not shown as clear");
                Check(GpuIrqCheck.AfterFix(previous, new GpuIrqResult { Outcome = GpuIrqOutcome.Restored }).Outcome == GpuIrqOutcome.NoRegion,
                    "A restored placement is not shown as no game region");
                GpuIrqResult rolled = GpuIrqCheck.AfterFix(previous, new GpuIrqResult { Outcome = GpuIrqOutcome.RolledBack, Detail = "reason" });
                Check(rolled.Outcome == GpuIrqOutcome.Skipped && rolled.Detail == Lang.T("irqcheck.reason.fused"), "A rolled-back placement is not shown as fused");
                foreach (GpuIrqOutcome same in new[] { GpuIrqOutcome.Clear, GpuIrqOutcome.NoRegion, GpuIrqOutcome.NoTarget, GpuIrqOutcome.Skipped, GpuIrqOutcome.NoEvidence })
                    Check(GpuIrqCheck.AfterFix(previous, new GpuIrqResult { Outcome = same }).Outcome == same, "Outcome changed in translation: " + same);
                Settings.SaveStr(GpuIrqCheck.StateKey, new GpuIrqState { Phase = GpuIrqPhase.Pending, DeviceId = "PCI\\FIXTURE" }.Encode());
                foreach (bool awaiting in new[] { true, false })
                {
                    bool shot = awaiting;
                    GpuIrqCheck.RebootStateForTest = delegate { return shot ? IrqRebootState.AwaitingReboot : IrqRebootState.Rebooted; };
                    GpuIrqOutcome expected = awaiting ? GpuIrqOutcome.AwaitingReboot : GpuIrqOutcome.NeedsVerification;
                    GpuIrqResult scheduled = GpuIrqCheck.AfterFix(previous,
                        new GpuIrqResult { Outcome = GpuIrqOutcome.Scheduled, Gpu = sample, Detail = "detail" });
                    Check(scheduled.Outcome == expected && scheduled.Gpu == null && scheduled.Detail == "" && scheduled.Slow.Count == 0,
                        "A pending placement is not reported from its saved state like the checkup does");
                    Check(GpuIrqCheck.AfterFix(previous, new GpuIrqResult { Outcome = GpuIrqOutcome.NoEvidence }).Outcome == expected,
                        "A pending placement without enough evidence is not still waiting");
                }
            }
            finally { GpuIrqCheck.RebootStateForTest = null; Settings.SaveStr(GpuIrqCheck.StateKey, ""); }
        }

        private static void WorkerFailuresAreCaught()
        {
            int failures = 0, workCalls = 0;
            Program.ShutdownThreadStartForTest = delegate { throw new InvalidOperationException("inert thread start refusal"); };
            try { Check(!Program.StartShutdownWorker(delegate { workCalls++; }, delegate { failures++; }), "Thread refusal reported success"); }
            finally { Program.ShutdownThreadStartForTest = null; }
            Check(failures == 1 && workCalls == 0, "Thread start failure did not report exactly once");
            using (var done = new ManualResetEvent(false))
            {
                Check(Program.StartShutdownWorker(delegate { throw new IOException("inert worker failure"); }, delegate { failures++; done.Set(); }), "Worker did not start");
                Check(done.WaitOne(1500) && failures == 2, "Uncaught worker error or missing failure callback");
            }
        }
        private static void ResetAndUninstallConfirmationRoutes()
        {
            foreach (bool uninstall in new[] { false, true })
                using (var f = new Fixture())
                {
                    int confirmations = 0, resets = 0, uninstalls = 0;
                    string confirmedKey = null;
                    f.Form.ResetApp = delegate { resets++; };
                    f.Form.UninstallApp = delegate { uninstalls++; };
                    f.Form.SettingsConfirmationForTest = delegate(string key)
                    {
                        confirmations++; confirmedKey = key;
                        f.Call("OnWipeAll"); f.Call("OnUninstall");
                        return true;
                    };
                    f.Call(uninstall ? "OnUninstall" : "OnWipeAll");
                    f.Call("OnWipeAll"); f.Call("OnUninstall");
                    Check(confirmations == 1 && confirmedKey == (uninstall ? "uninstall.confirm" : "wipe.confirm"),
                        "A data action asked twice or displayed the opposite operation's confirmation");
                    Check(resets == (uninstall ? 0 : 1) && uninstalls == (uninstall ? 1 : 0),
                        "Confirmed reset/uninstall entered the wrong operation or reentered");
                    Check(!f.ResetButton.Enabled && !f.UninstallButton.Enabled && (int)f.Get("wipeBusy") == 1,
                        "A confirmed data operation left a second destructive button actionable");
                }
            using (var f = new Fixture())
            {
                int confirmations = 0, starts = 0;
                f.Form.ResetApp = delegate { starts++; }; f.Form.UninstallApp = delegate { starts++; };
                f.Form.SettingsConfirmationForTest = delegate { confirmations++; return false; };
                f.Call("OnWipeAll");
                Check(starts == 0 && confirmations == 1 && (int)f.Get("wipeBusy") == 0
                    && f.ResetButton.Enabled && f.UninstallButton.Enabled, "Cancelled confirmation retained the operation slot or changed data");
                f.Call("OnUninstall");
                Check(starts == 0 && confirmations == 2 && (int)f.Get("wipeBusy") == 0,
                    "Cancelling reset blocked a later independent uninstall confirmation");
            }
        }
        private static void ResetAndUninstallGameGuards()
        {
            foreach (bool uninstall in new[] { false, true })
                foreach (bool startsDuringConfirmation in new[] { false, true })
                    using (var f = new Fixture())
                    {
                        int confirmations = 0, starts = 0, warnings = 0;
                        f.Form.ResetApp = delegate { starts++; }; f.Form.UninstallApp = delegate { starts++; };
                        f.Form.SettingsOperationWarningForTest = delegate { warnings++; };
                        f.SetGameActive(!startsDuringConfirmation);
                        f.Form.SettingsConfirmationForTest = delegate { confirmations++; f.SetGameActive(true); return true; };
                        f.Call(uninstall ? "OnUninstall" : "OnWipeAll");
                        Check(starts == 0 && warnings == 1 && confirmations == (startsDuringConfirmation ? 1 : 0),
                            "A running game was not blocked before/after data action confirmation");
                        Check((int)f.Get("wipeBusy") == 0 && f.ResetButton.Enabled && f.UninstallButton.Enabled,
                            "The in-game refusal retained the operation slot or disabled future retry");
                    }
            using (var f = new Fixture())
            {
                int confirmations = 0, starts = 0, warnings = 0;
                f.Form.ResetApp = delegate { starts++; }; f.Form.UninstallApp = delegate { starts++; };
                f.Form.SettingsOperationWarningForTest = delegate { warnings++; };
                f.Form.SettingsConfirmationForTest = delegate { confirmations++; return true; };
                f.Call("OnShaderClean", f.ShaderButton); confirmations = 0;
                f.Call("OnWipeAll"); f.Call("OnUninstall");
                Check(starts == 0 && confirmations == 0 && warnings == 2 && (int)f.Get("wipeBusy") == 0,
                    "Data actions admitted while shader cache cleanup was still writing");
                f.Workers.Dequeue()(); f.DrainPosts();
            }
        }
        private static void RuntimeShutdownRejectsLateUiWrites()
        {
            using (var f = new Fixture())
            {
                Toggle auto = f.Add(new Toggle()); auto.SetSilently(false); f.Set("swAuto", auto);
                SettingCard shader = (SettingCard)f.Get("cardShader"); shader.Value = "keep before reset";
                Label progress = f.Add(new Label()); f.Set("lblSub", progress);
                f.Set("uiActive", true);
                f.Form.StartupTaskQueryForTest = delegate { return true; };
                f.Form.ShaderMeasureForTest = delegate { return 8192; };
                f.Call("RefreshSlowStateAsync"); f.Workers.Dequeue()();
                f.Call("BeginRuntimeShutdown", "actual reset stage");
                Check(progress.Text == "actual reset stage" && (bool)f.Get("runtimeShutdownBusy"),
                    "Shutdown stage was not routed to the existing progress presentation");
                f.DrainPosts();
                Check(!auto.Checked && shader.Value == "keep before reset" && f.Workers.Count == 0,
                    "A late settings read replaced shutdown progress/state or restarted background refresh");
                f.Call("FinishRuntimeShutdown");
            }
        }
        private static void SoftwareResetCompletionRestartsWithoutAnotherPrompt()
        {
            foreach (bool completed in new[] { false, true })
                foreach (bool runtimeStopped in new[] { false, true })
                {
                    int warnings = 0, restarts = 0, finishes = 0;
                    string message = null;
                    var report = new ResetDataReport { Completed = completed, Failure = "fixture reset failed\nprivate detail" };
                    DataOperationCompletion.CompleteReset(report, runtimeStopped,
                        delegate(string text) { warnings++; message = text; }, delegate { restarts++; }, delegate { finishes++; });
                    Check(warnings == (completed ? 0 : 1) && restarts == (runtimeStopped ? 1 : 0) && finishes == 1,
                        "Reset completion falsely reported success, requested another confirmation, or left a stopped runtime open");
                    if (!completed)
                        Check(message.Contains("fixture reset failed") && !message.Contains("private detail"),
                            "Reset failure lost its short reason or displayed a multiline detail list");
                }
            int warned = 0, restarted = 0, finished = 0;
            DataOperationCompletion.CompleteReset(new ResetDataReport { Completed = true, Warnings = 1, Failure = "fixture locked file" }, true,
                delegate(string text) { warned++; Check(text.Contains("fixture locked file"), "Reset warning lost its reason"); },
                delegate { restarted++; }, delegate { finished++; });
            Check(warned == 1 && restarted == 1 && finished == 1, "A partial reset was presented as an unconditional success");
            bool failedCallback = false;
            try
            {
                DataOperationCompletion.CompleteReset(new ResetDataReport { Completed = true }, true, null,
                    delegate { throw new IOException("fixture restart scheduling failure"); }, delegate { finished++; });
            }
            catch (IOException) { failedCallback = true; }
            Check(failedCallback && finished == 2, "Failed restart scheduling skipped the required final shutdown");
        }
        private static void UninstallCompletionKeepsPartialAndFinalizerFailuresVisible()
        {
            foreach (string scenario in new[] { "success", "partial", "restore-failed", "stop-failed", "finalizer-failed" })
            {
                int dialogs = 0, finishes = 0; bool warned = false; string message = null;
                bool stopped = scenario != "stop-failed", finalizerStarted = scenario != "finalizer-failed";
                var report = new UninstallReport
                {
                    RuntimeStopped = stopped, Cleared = scenario != "restore-failed" && stopped,
                    Warnings = scenario == "partial" ? 3 : 0, Failure = "fixture stop failure\nprivate detail"
                };
                DataOperationCompletion.CompleteUninstall(report, finalizerStarted, "fixture finalizer unavailable",
                    delegate(bool warning, string text) { dialogs++; warned = warning; message = text; }, delegate { finishes++; });
                Check(dialogs == 1 && finishes == 1 && warned == (scenario != "success"),
                    "Uninstall completion falsely reported success or duplicated its result: " + scenario);
                if (scenario == "stop-failed")
                    Check(message.Contains("fixture stop failure") && !message.Contains("private detail")
                        && !message.Contains(Lang.T("uninstall.selfdelete")), "Failed stop claimed program deletion or lost its failure");
                if (scenario == "finalizer-failed")
                    Check(message.Contains("fixture finalizer unavailable"), "Failed executable deletion helper was hidden from the result");
                if (scenario == "partial" || scenario == "restore-failed")
                    Check(message.Contains(Lang.F("uninstall.partial", scenario == "partial" ? 3 : 1)),
                        "Incomplete restoration/data cleanup was shown as a complete uninstall");
            }
        }
        private static void DispatcherTimeoutAndClosed()
        {
            int ran = 0; bool result = true;
            var dispatcher = new Program.RuntimeUiDispatcher();
            using (var done = new ManualResetEvent(false))
            {
                Program.StartShutdownWorker(delegate { result = dispatcher.TrySend(delegate { ran++; }, 25); done.Set(); }, delegate { done.Set(); });
                Check(done.WaitOne(1500) && !result, "Stopped message loop did not bound UI dispatch");
                Application.DoEvents(); Check(ran == 0, "Timed-out UI callback ran after expiry");
            }
            dispatcher.Dispose();
            Check(!dispatcher.Post(delegate { ran++; }) && !dispatcher.TrySend(delegate { ran++; }), "Destroyed dispatcher accepted work");
        }
        private static void FakeResetKeepsUiResponsive()
        {
            string root = Path.Combine(Path.GetTempPath(), "PaviseUiResetAudit-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root); string sentinel = Path.Combine(root, "Pavise.games.txt"); File.WriteAllText(sentinel, "fixture");
            int owner = Thread.CurrentThread.ManagedThreadId, pulses = 0, stops = 0, deletes = 0, progress = 0;
            Exception failure = null; bool completed = false;
            var dispatcher = new Program.RuntimeUiDispatcher();
            using (var release = new ManualResetEvent(false))
            using (var workerDone = new ManualResetEvent(false))
            using (var context = new ApplicationContext())
            using (var pulse = new System.Windows.Forms.Timer())
            using (var limit = new System.Windows.Forms.Timer())
            {
                Func<List<string>> oldRestore = LegacyPurge.RestoreHook;
                Func<bool> oldDelete = LegacyPurge.DeleteRegistryHook;
                Action<string> stage = title => dispatcher.Post(delegate { Check(Thread.CurrentThread.ManagedThreadId == owner, "Progress ran on a worker"); progress++; });
                LegacyPurge.RestoreHook = delegate { stage("inert restoration step"); Check(release.WaitOne(1500), "UI did not keep pumping during the mocked restore"); return new List<string> { "inert restore blocked" }; };
                LegacyPurge.DeleteRegistryHook = delegate { deletes++; return true; };
                pulse.Interval = 15; pulse.Tick += delegate { pulses++; if (pulses >= 4) release.Set(); };
                limit.Interval = 2500; limit.Tick += delegate { failure = new TimeoutException("Audit message loop timed out"); release.Set(); context.ExitThread(); };
                pulse.Start(); limit.Start();
                Program.StartShutdownWorker(delegate
                {
                    try
                    {
                        int files; string unrestored;
                        bool cleared = Program.TryResetUserData(root, delegate { stops++; return dispatcher.TrySend(delegate { Check(Thread.CurrentThread.ManagedThreadId == owner, "UI action ran on a worker"); }); }, out files, out unrestored,
                            stage);
                        Check(!cleared && files == 0 && unrestored.Contains("inert restore blocked"), "Mocked reset erased data after restoration failure");
                        Check(File.ReadAllText(sentinel) == "fixture" && deletes == 0 && stops == 1, "Failed reset did not preserve both stores");
                        dispatcher.Post(delegate { completed = true; context.ExitThread(); });
                    }
                    catch (Exception error) { failure = error; dispatcher.Post(context.ExitThread); }
                    finally { workerDone.Set(); }
                }, error => { failure = error; dispatcher.Post(context.ExitThread); workerDone.Set(); });
                Application.Run(context);
                release.Set(); Check(workerDone.WaitOne(1500), "Mocked shutdown worker did not settle");
                LegacyPurge.RestoreHook = oldRestore; LegacyPurge.DeleteRegistryHook = oldDelete;
                if (failure != null) throw failure;
                Check(completed && pulses >= 4 && progress > 0, "Reset blocked the UI or lost its actual stage progress");
            }
            dispatcher.Dispose(); File.Delete(sentinel); Directory.Delete(root, false);
        }
        private static void SoftwareResetKeepsUiResponsiveAndPreservesRestoreData()
        {
            string root = Path.Combine(Path.GetTempPath(), "PaviseUiSoftwareReset-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string library = Path.Combine(root, "Pavise.games.txt"), recovery = Path.Combine(root, "Pavise.restore-fixture.dat");
            File.WriteAllText(library, "fixture library"); File.WriteAllText(recovery, "original values fixture");
            int owner = Thread.CurrentThread.ManagedThreadId, pulses = 0, stops = 0, preferenceClears = 0, progress = 0, restores = 0;
            bool completed = false; Exception failure = null;
            Func<List<string>> oldRestore = LegacyPurge.RestoreHook;
            Func<ResetDataReport> oldClear = AppDataReset.ClearUserPreferencesForTest;
            Action<string> oldBeforeDelete = AppDataReset.BeforeFileDeleteForTest;
            using (var dispatcher = new Program.RuntimeUiDispatcher())
            using (var release = new ManualResetEvent(false))
            using (var workerDone = new ManualResetEvent(false))
            using (var context = new ApplicationContext())
            using (var pulse = new System.Windows.Forms.Timer())
            using (var limit = new System.Windows.Forms.Timer())
            try
            {
                Settings.UseTransientStoreForCurrentProcess(); Logger.WritesEnabled = false;
                LegacyPurge.RestoreHook = delegate { restores++; throw new InvalidOperationException("Software reset must not restore the system"); };
                AppDataReset.ClearUserPreferencesForTest = delegate { preferenceClears++; return new ResetDataReport { Completed = true }; };
                AppDataReset.BeforeFileDeleteForTest = delegate(string path)
                {
                    Check(path == library, "Software reset attempted to erase its unknown restore record");
                    Check(release.WaitOne(1500), "Software reset blocked the UI message pump");
                };
                pulse.Interval = 15; pulse.Tick += delegate { pulses++; if (pulses >= 4) release.Set(); };
                limit.Interval = 2500; limit.Tick += delegate { failure = new TimeoutException("Software reset fixture timed out"); release.Set(); context.ExitThread(); };
                pulse.Start(); limit.Start();
                Program.StartShutdownWorker(delegate
                {
                    try
                    {
                        ResetDataReport report = AppDataReset.Execute(root, delegate
                        {
                            stops++; return dispatcher.TrySend(delegate { Check(Thread.CurrentThread.ManagedThreadId == owner, "Software reset UI stop ran on a worker"); });
                        }, title => dispatcher.Post(delegate
                        {
                            Check(Thread.CurrentThread.ManagedThreadId == owner && !string.IsNullOrEmpty(title), "Software reset stage was not routed to the UI thread"); progress++;
                        }));
                        Check(report.Completed && report.Warnings == 0 && report.Files == 1,
                            "Software reset did not report actual isolated cleanup accurately");
                        Check(!File.Exists(library) && File.ReadAllText(recovery) == "original values fixture"
                            && preferenceClears == 1 && stops == 1 && restores == 0,
                            "Software reset changed recovery state, restored the system, or missed its actual library cleanup");
                        Check(!Settings.SaveStr("Lang", "late callback"), "A late callback rewrote preferences after software reset");
                        dispatcher.Post(delegate { completed = true; context.ExitThread(); });
                    }
                    catch (Exception error) { failure = error; dispatcher.Post(context.ExitThread); }
                    finally { workerDone.Set(); }
                }, error => { failure = error; dispatcher.Post(context.ExitThread); workerDone.Set(); });
                Application.Run(context);
                release.Set(); Check(workerDone.WaitOne(1500), "Software reset worker did not settle before fixture disposal");
                if (failure != null) throw failure;
                Check(completed && pulses >= 4 && progress >= 3, "Software reset froze the UI or skipped real operation stages");
            }
            finally
            {
                release.Set(); LegacyPurge.RestoreHook = oldRestore; AppDataReset.ClearUserPreferencesForTest = oldClear;
                AppDataReset.BeforeFileDeleteForTest = oldBeforeDelete; Settings.UseTransientStoreForCurrentProcess();
                Logger.ResetWriteBarrierForTest(); Logger.WritesEnabled = false;
                if (File.Exists(library)) File.Delete(library); File.Delete(recovery); Directory.Delete(root, false);
            }
        }
        private static void SoftwareResetFailuresDoNotPretendToComplete()
        {
            Func<List<string>> oldRestore = LegacyPurge.RestoreHook;
            Func<ResetDataReport> oldClear = AppDataReset.ClearUserPreferencesForTest;
            Action<string> oldBeforeDelete = AppDataReset.BeforeFileDeleteForTest;
            try
            {
                foreach (string scenario in new[] { "stop", "preferences", "file" })
                {
                    string root = Path.Combine(Path.GetTempPath(), "PaviseUiResetFailure-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(root); string library = Path.Combine(root, "Pavise.games.txt"); File.WriteAllText(library, "fixture library");
                    int clears = 0, restores = 0, deleteAttempts = 0;
                    Settings.UseTransientStoreForCurrentProcess(); Logger.ResetWriteBarrierForTest(); Logger.WritesEnabled = false;
                    LegacyPurge.RestoreHook = delegate { restores++; throw new InvalidOperationException("Software reset must not restore"); };
                    AppDataReset.ClearUserPreferencesForTest = delegate
                    {
                        clears++; return new ResetDataReport { Completed = scenario != "preferences", Failure = "fixture preference refusal" };
                    };
                    AppDataReset.BeforeFileDeleteForTest = delegate { deleteAttempts++; if (scenario == "file") throw new IOException("fixture file locked"); };
                    try
                    {
                        ResetDataReport report = AppDataReset.Execute(root, delegate { return scenario != "stop"; });
                        Check(!report.Completed && report.Warnings > 0 && !string.IsNullOrEmpty(report.Failure) && restores == 0,
                            "Failed software reset returned unconditional success or entered system restoration: " + scenario);
                        Check(clears == (scenario == "stop" ? 0 : 1) && deleteAttempts == (scenario == "stop" ? 0 : 1),
                            "Software reset ignored failed stop or did not attempt its actual configured cleanup: " + scenario);
                        Check(File.Exists(library) == (scenario != "preferences"), "Reset failure erased the wrong fixture file state: " + scenario);
                        if (scenario != "stop") Check(!Settings.SaveStr("Lang", "late callback"), "Failed cleanup allowed a late callback to recreate preferences");
                    }
                    finally { if (File.Exists(library)) File.Delete(library); Directory.Delete(root, false); }
                }
            }
            finally
            {
                LegacyPurge.RestoreHook = oldRestore; AppDataReset.ClearUserPreferencesForTest = oldClear;
                AppDataReset.BeforeFileDeleteForTest = oldBeforeDelete; Settings.UseTransientStoreForCurrentProcess();
                Logger.ResetWriteBarrierForTest(); Logger.WritesEnabled = false;
            }
        }
        private static void DataOperationProgressCardRendersWithoutTextClipping()
        {
            int oldLanguage = Lang.Cur; float oldScale = Dpi.Scale;
            string renders = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "progress-card-renders");
            Directory.CreateDirectory(renders);
            var metrics = new List<string> { "language\tdpi_percent\tlabel\tmeasured_height\tavailable_height" };
            try
            {
                foreach (int language in new[] { 0, 1 })
                    foreach (float scale in new[] { 1f, 1.5f })
                    {
                        Lang.Cur = language; Dpi.Scale = scale; Theme.DropFontCache();
                        using (var card = new DataOperationProgressCard())
                        using (var bitmap = new Bitmap(card.Width, card.Height))
                        {
                            card.Status.Text = Lang.T("wipe.progress.stop");
                            Check(card.Parent == null && card.FindForm() == null,
                                "Offscreen progress rendering attached a normal application window");
                            foreach (Control item in card.Controls)
                            {
                                Label label = item as Label;
                                Check(label != null && !string.IsNullOrWhiteSpace(label.Text), "Progress card omitted a visible title, stage or note");
                                Size size = TextRenderer.MeasureText(label.Text, label.Font,
                                    new Size(label.ClientSize.Width, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
                                metrics.Add((language == 0 ? "zh" : "en") + "\t" + (int)(scale * 100) + "\t" + label.Name
                                    + "\t" + size.Height + "\t" + label.ClientSize.Height);
                                Check(size.Height <= label.ClientSize.Height && label.Bounds.Right < card.Width && label.Bounds.Bottom < card.Height,
                                    "Progress card text was clipped at language=" + language + " dpi=" + scale.ToString(CultureInfo.InvariantCulture) + " label=" + label.Name);
                            }
                            card.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                            string path = Path.Combine(renders, "progress-card." + (language == 0 ? "zh" : "en") + "." + (int)(scale * 100) + ".png");
                            bitmap.Save(path, ImageFormat.Png);
                            Check(new FileInfo(path).Length > 0 && card.FindForm() == null,
                                "Progress card render failed or displayed an application window");
                        }
                    }
                File.WriteAllLines(Path.Combine(renders, "text-height-metrics.tsv"), metrics.ToArray());
                Console.WriteLine("RENDERS " + renders);
            }
            finally { Lang.Cur = oldLanguage; Dpi.Scale = oldScale; Theme.DropFontCache(); }
        }
        [STAThread]
        private static int Main()
        {
            try
            {
                Settings.UseTransientStoreForCurrentProcess(); Logger.WritesEnabled = false; Lang.Init();
                Action[] tests = { ShaderConfirmationReentry, ShaderFailuresAndStaleResult, ShaderRebuiltButtonFollowsWork,
                    AuditRebuildAndCancel, AuditLateProgressCannotReplaceNewRun, AuditSchedulingFailures,
                    AuditFixRefreshesWithoutResampling, AuditInterruptFixMatchesCheckup,
                    WorkerFailuresAreCaught, DispatcherTimeoutAndClosed, FakeResetKeepsUiResponsive,
                    ResetAndUninstallConfirmationRoutes, ResetAndUninstallGameGuards, RuntimeShutdownRejectsLateUiWrites,
                    SoftwareResetCompletionRestartsWithoutAnotherPrompt, UninstallCompletionKeepsPartialAndFinalizerFailuresVisible,
                    SoftwareResetKeepsUiResponsiveAndPreservesRestoreData, SoftwareResetFailuresDoNotPretendToComplete,
                    DataOperationProgressCardRendersWithoutTextClipping };
                foreach (Action test in tests) { test(); Console.WriteLine("PASS " + test.Method.Name); }
                SelfTests.RunUiAsyncStateRegressionTests();
                Console.WriteLine("PASS interaction_cases=" + tests.Length + " assertions=" + assertions + " existing_async_cases=12 real_restore=0 real_cleanup=0 real_audit=0 normal_runtime=0"); return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
    }
}
#endif
