using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;

namespace PaviseApp
{
    internal static class BackgroundGovernanceRuntimeChecks
    {
        private static int checks;
        private static void Check(bool pass, string message) { checks++; if (!pass) throw new InvalidOperationException(message); }
        private static int RunPriorityWorker()
        {
            IntPtr self = new IntPtr(-1);
            Console.WriteLine("ready");
            string line;
            while ((line = Console.ReadLine()) != null && line != "exit")
            {
                string[] request = line.Split(' ');
                bool ok = request[0] == "priority" ? Native.SetPriorityClass(self, uint.Parse(request[1]))
                    : request[0] == "boost" ? Native.TrySetBoostDisabled(self, request[1] == "1")
                    : Native.RestorePowerThrottling(self, int.Parse(request[1]), int.Parse(request[2]));
                Console.WriteLine(ok ? "ok" : "error " + System.Runtime.InteropServices.Marshal.GetLastWin32Error());
            }
            return 0;
        }

        private sealed class OwnedWorker : IDisposable
        {
            private readonly Process process;
            internal readonly string Name;
            internal readonly long Birth;
            internal int Pid { get { return process.Id; } }
            internal IntPtr Handle { get { return process.Handle; } }

            internal OwnedWorker()
            {
                var info = new ProcessStartInfo(typeof(BackgroundGovernanceRuntimeChecks).Assembly.Location, "--priority-worker");
                info.UseShellExecute = false;
                info.CreateNoWindow = true;
                info.WindowStyle = ProcessWindowStyle.Hidden;
                info.RedirectStandardInput = true;
                info.RedirectStandardOutput = true;
                process = Process.Start(info);
                try
                {
                    Check(process.StandardOutput.ReadLine() == "ready", "Owned process failed to start");
                    Name = Native.ImageName(Handle);
                    long cpu;
                    ulong io;
                    Check(Name != null && Native.QueryProcessSample(Handle, out Birth, out cpu, out io) && Birth > 0, "Owned process identity unreadable");
                }
                catch { Dispose(); throw; }
            }

            internal void Change(string command)
            {
                process.StandardInput.WriteLine(command);
                string result = process.StandardOutput.ReadLine();
                Check(result == "ok", "Owned process could not set " + command + " " + result);
            }

            internal void CheckPower(int wantedControl, int wantedState, string stage)
            {
                int control = -1, state = -1;
                bool matched = false;
                for (int attempt = 0; attempt < 40; attempt++)
                {
                    if (Native.TryQueryPowerThrottling(Handle, out control, out state)
                        && control == wantedControl && state == wantedState)
                    {
                        matched = true;
                        break;
                    }
                    Thread.Sleep(5);
                }
                Check(matched, stage + " wanted " + wantedControl + "/" + wantedState + " got " + control + "/" + state);
            }

            public void Dispose()
            {
                try
                {
                    if (process.HasExited) return;
                    process.StandardInput.WriteLine("exit");
                    if (!process.WaitForExit(2000)) process.Kill();
                }
                finally { process.Dispose(); }
            }
        }

        private static void Acquire(SuppressionCore core, OwnedWorker worker)
        {
            CheckAcquired(core, worker, core.Acquire(worker.Pid, worker.Name, SuppressReason.Background, null,
                SuppressionLevel.Isolated, worker.Birth));
        }

        private static void CheckAcquired(SuppressionCore core, OwnedWorker worker, AcquireResult result)
        {
            Check(result == AcquireResult.NewlyThrottled, "Native ordinary acquire failed: " + core.LastApplyError);
            Check(Native.QueryIoPriority(worker.Handle) == 0 && Native.QueryPagePriority(worker.Handle) == 1, "Native ordinary I/O and memory priorities were not applied");
        }

        private static void Release(SuppressionCore core, OwnedWorker worker)
        {
            Check(core.ReleaseIfCreation(worker.Pid, SuppressReason.Background, worker.Birth) && !core.BackgroundReleasePending,
                "Native ordinary release failed: " + core.LastApplyError);
        }

        private static int RestartedController(string[] args)
        {
            int pid = int.Parse(args[1]);
            long birth = long.Parse(args[2]);
            string journal = args[3];
            IntPtr handle = Native.OpenProcess(0x1000, false, pid);
            if (handle == IntPtr.Zero) throw new InvalidOperationException("Restarted controller could not open its fixture");
            string name;
            try
            {
                Check(Native.QueryProcessIdentityState(handle, birth) == 0, "Restarted controller saw a different target identity");
                name = Native.ImageName(handle);
            }
            finally { Native.CloseHandle(handle); }
            var core = new SuppressionCore(journal);
            Check(core.Acquire(pid, name, SuppressReason.Background, null, SuppressionLevel.Isolated, birth) == AcquireResult.NewlyThrottled,
                "Restarted controller failed to apply ordinary governance: " + core.LastApplyError);
            return 0;
        }

        private static void StartRestartedController(OwnedWorker worker, string journal)
        {
            string arguments = "--restart-controller " + worker.Pid + " " + worker.Birth + " \"" + journal + "\"";
            var info = new ProcessStartInfo(typeof(BackgroundGovernanceRuntimeChecks).Assembly.Location, arguments);
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.WindowStyle = ProcessWindowStyle.Hidden;
            info.RedirectStandardError = true;
            using (Process controller = Process.Start(info))
            {
                if (!controller.WaitForExit(15000))
                {
                    controller.Kill();
                    throw new InvalidOperationException("Restarted controller timed out");
                }
                Check(controller.ExitCode == 0, controller.StandardError.ReadToEnd());
            }
        }

        private static void NativeOrdinaryValues()
        {
            int occurrence = 0;
            foreach (uint raised in new[] { 32u, 128u })
            {
                using (var worker = new OwnedWorker())
                {
                    string journal = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ordinary-" + occurrence++ + ".state");
                    var core = new SuppressionCore(journal);
                    worker.Change("priority " + (raised == 32u ? 128u : 32u));
                    worker.Change("boost 0");
                    if (Native.PowerThrottlingSupported)
                    {
                        worker.Change("qos 0 0");
                        worker.CheckPower(0, 0, "Original QoS");
                    }
                    Acquire(core, worker);
                    Check(Native.GetPriorityClass(worker.Handle) == 64, "First apply did not write Idle");
                    bool timerSupported = Native.PowerThrottlingSupported && Native.EcoQoSWantMask(true) == 5;
                    if (timerSupported) worker.CheckPower(5, 5, "First apply");
                    worker.Change("priority " + raised);
                    worker.Change("boost 1");
                    for (int audit = 0; audit < 4; audit++)
                    {
                        Check(core.Reconcile(worker.Pid, worker.Name, SuppressReason.Background, true), "Native audit lost the suppressed process");
                        Check(Native.GetPriorityClass(worker.Handle) == raised, "Audit rewrote the process's own CPU priority");
                        Check(Native.QueryBoostDisabled(worker.Handle) == 1, "Audit rewrote the process's own boost");
                    }
                    if (timerSupported)
                    {
                        worker.Change("qos 5 1");
                        worker.CheckPower(5, 1, "Software disabled the timer seal");
                        Check(core.Reconcile(worker.Pid, worker.Name, SuppressReason.Background, true), "Timer audit lost the suppressed process");
                        worker.CheckPower(5, 1, "Audit leaves timer bit changed by software");
                        worker.Change("qos 4 0");
                        worker.CheckPower(4, 0, "Software cleared Eco bit");
                        Check(core.Reconcile(worker.Pid, worker.Name, SuppressReason.Background, true), "Eco correction lost the suppressed process");
                        worker.CheckPower(5, 1, "Correction preserves freshly read timer control and state");
                        worker.Change("qos 0 0");
                        worker.CheckPower(0, 0, "Software removed timer control");
                        Check(core.Reconcile(worker.Pid, worker.Name, SuppressReason.Background, true), "Second Eco correction lost the suppressed process");
                        worker.CheckPower(1, 1, "Correction must leave timer control absent");
                        worker.Change("qos 5 1");
                        worker.CheckPower(5, 1, "Software set the timer control before release");
                    }
                    Release(core, worker);
                    Check(Native.GetPriorityClass(worker.Handle) == raised, "Release overwrote the process's own CPU priority");
                    Check(Native.QueryBoostDisabled(worker.Handle) == 1, "Release overwrote the process's own boost");
                    if (timerSupported) worker.CheckPower(4, 0, "Release preserves externally changed timer state");
                }
            }

            using (var worker = new OwnedWorker())
            {
                string journal = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ordinary-retained.state");
                var core = new SuppressionCore(journal);
                worker.Change("priority 128");
                bool timerSupported = Native.PowerThrottlingSupported && Native.EcoQoSWantMask(true) == 5;
                if (timerSupported)
                {
                    worker.Change("qos 4 0");
                    worker.CheckPower(4, 0, "Original controlled timer bit");
                }
                Acquire(core, worker);
                Check(Native.GetPriorityClass(worker.Handle) == 64, "First apply did not leave Idle");
                if (timerSupported) worker.CheckPower(5, 5, "Original controlled timer bit was sealed");
                Release(core, worker);
                Check(Native.GetPriorityClass(worker.Handle) == 128, "Release did not restore owned Idle priority");
                if (timerSupported) worker.CheckPower(4, 0, "Release did not restore owned timer bit");
                Acquire(core, worker);
                Check(Native.GetPriorityClass(worker.Handle) == 64, "Reacquire after an identity-scoped exemption did not rewrite restored CPU priority");
                if (timerSupported) worker.CheckPower(5, 5, "Reacquire after an identity-scoped exemption did not reseal the restored timer");
                Release(core, worker);
                if (timerSupported) worker.CheckPower(4, 0, "Second release did not restore the timer bit");

                Check(!File.Exists(journal) && !File.Exists(journal + ".once"),
                    "A clean release must settle recovery without a persistent first-write list");
                StartRestartedController(worker, journal);
                Check(Native.GetPriorityClass(worker.Handle) == 64, "A new game controller must get an in-memory first write");
                if (timerSupported) worker.CheckPower(5, 5, "A new game controller must seal the timer");
                for (int recover = 0; recover < 20 && File.Exists(journal); recover++)
                {
                    SuppressionCore.HealFromCrash(journal);
                    if (File.Exists(journal)) Thread.Sleep(10);
                }
                Check(!File.Exists(journal), "Interrupted controller recovery did not settle");
                Check(Native.GetPriorityClass(worker.Handle) == 128, "Ownership journal did not restore Idle after controller exit");
                if (timerSupported) worker.CheckPower(4, 0, "Ownership journal did not restore timer after controller exit");
                var afterCrash = new SuppressionCore(journal);
                Acquire(afterCrash, worker);
                Check(Native.GetPriorityClass(worker.Handle) == 64, "The next game after recovery did not get a first CPU attempt");
                if (timerSupported) worker.CheckPower(5, 5, "The next game after recovery did not get a first timer attempt");
                Release(afterCrash, worker);
            }
        }

        private static void NativeOrdinarySessionsAndRelease()
        {
            bool timerSupported = Native.PowerThrottlingSupported && Native.EcoQoSWantMask(true) == 5;
            foreach (bool cpuChanged in new[] { false, true })
            foreach (bool timerChanged in new[] { false, true })
            using (var worker = new OwnedWorker())
            {
                string journal = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "ordinary-round-" + cpuChanged + "-" + timerChanged + ".state");
                foreach (string suffix in new[] { ".once", ".once.tmp", ".once.stale.bak" })
                    File.WriteAllText(journal + suffix, "retired attempt record");
                var core = new SuppressionCore(journal);
                foreach (string suffix in new[] { ".once", ".once.tmp", ".once.stale.bak" })
                    Check(!File.Exists(journal + suffix), "Construction did not clean a retired first-write file");
                worker.Change("priority 128");
                if (timerSupported)
                {
                    worker.Change("qos 0 0");
                    worker.CheckPower(0, 0, "Fixture original timer");
                }

                Acquire(core, worker);
                Check(Native.GetPriorityClass(worker.Handle) == 64, "Game one did not write Idle");
                if (timerSupported) worker.CheckPower(5, 5, "Game one did not seal the timer");
                if (cpuChanged) worker.Change("priority 32");
                if (timerSupported && timerChanged)
                {
                    worker.Change("qos 5 1");
                    worker.CheckPower(5, 1, "Program changed the timer before governance was disabled");
                }
                for (int audit = 0; audit < 3; audit++)
                {
                    Check(core.Reconcile(worker.Pid, worker.Name, SuppressReason.Background, true), "Game one audit lost the process");
                    Check(Native.GetPriorityClass(worker.Handle) == (cpuChanged ? 32u : 64u), "Game one audit changed CPU priority");
                }

                core.SetResourceJobMode(-1, null);
                Check(core.Release(worker.Pid, SuppressReason.Background) && !core.BackgroundReleasePending,
                    "Disabling governance did not settle the background release");
                Check(Native.GetPriorityClass(worker.Handle) == (cpuChanged ? 32u : 128u), "Disabling governance restored the wrong CPU value");
                if (timerSupported) worker.CheckPower(timerChanged ? 4 : 0, 0, "Disabling governance restored the wrong timer value");

                core.SetResourceJobMode(BackgroundGovernancePolicy.Normal, null);
                Acquire(core, worker);
                Check(Native.GetPriorityClass(worker.Handle) == (cpuChanged ? 32u : 64u), "Re-enabling governance rearmed the wrong CPU attempt");
                if (timerSupported) worker.CheckPower(5, timerChanged ? 1 : 5, "Re-enabling governance rearmed the wrong timer attempt");
                Release(core, worker);
                core.ResetOrdinaryBackgroundAttempts();
                Check(Native.GetPriorityClass(worker.Handle) == (cpuChanged ? 32u : 128u), "Game one exit did not settle CPU priority");
                if (timerSupported) worker.CheckPower(timerChanged ? 4 : 0, 0, "Game one exit did not settle timer");

                Acquire(core, worker);
                Check(Native.GetPriorityClass(worker.Handle) == 64, "Game two did not apply Idle to the same process instance");
                if (timerSupported) worker.CheckPower(5, 5, "Game two did not seal the same process instance");
                Release(core, worker);
                Check(Native.GetPriorityClass(worker.Handle) == (cpuChanged ? 32u : 128u), "Game two did not restore its own original CPU priority");
                if (timerSupported) worker.CheckPower(timerChanged ? 4 : 0, 0, "Game two did not restore its own original timer");
            }
        }

        private static bool OrdinaryOwnershipPending(SuppressionCore core, int pid)
        {
            var entries = (IDictionary)typeof(SuppressionCore).GetField("map", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(core);
            object entry = entries[pid];
            return entry != null && (bool)entry.GetType().GetField("OrdinaryOwnershipJournalPending").GetValue(entry);
        }

        private static void JournalPublishRetries()
        {
            for (int failures = 1; failures <= 5; failures++)
            {
                CheckJournalPublish("replace-io-" + failures, true, failures, failures < 5 ? failures + 1 : 5, failures < 5,
                    message => new IOException(message));
                CheckJournalPublish("replace-access-" + failures, true, failures, failures < 5 ? failures + 1 : 5, failures < 5,
                    message => new UnauthorizedAccessException(message));
            }
            CheckJournalPublish("replace-other", true, 1, 1, false, message => new InvalidOperationException(message));
            CheckJournalPublish("move-io", false, 4, 5, true, message => new IOException(message));
        }

        private static void CheckJournalPublish(string label, bool replace, int failures, int expectedAttempts, bool expectedSaved,
            Func<string, Exception> newFault)
        {
            const int caseAttempts = 5;
            for (int attempt = 1; attempt <= caseAttempts; attempt++)
            {
                string journal = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, label + "-" + attempt + ".state");
                IOException conflict = CheckJournalPublishAttempt(journal, label, replace, failures, expectedAttempts, expectedSaved, newFault);
                if (conflict == null) return;
                string message = label + " hit a real publish conflict (0x" + conflict.HResult.ToString("X8")
                    + ") on fixture attempt " + attempt + "/" + caseAttempts;
                if (attempt == caseAttempts) throw new IOException(message, conflict);
                Console.WriteLine("RETRY " + message);
            }
        }

        private static bool IsJournalPublishConflict(IOException error)
        {
            if (error == null || (error.HResult != unchecked((int)0x80070497)
                && error.HResult != unchecked((int)0x80070020))) return false;
            // 保存会吞掉发布异常 通过当前栈确认它来自文件操作
            var stack = new StackTrace();
            bool filePublish = false;
            for (int i = 0; i < stack.FrameCount; i++)
            {
                MethodBase method = stack.GetFrame(i).GetMethod();
                if (method == null) continue;
                if (method.DeclaringType == typeof(File) && (method.Name == "Replace" || method.Name == "InternalReplace"
                    || method.Name == "Move" || method.Name == "InternalMove")) filePublish = true;
                if (method.DeclaringType == typeof(SuppressionCore) && method.Name == "PublishJournalFile")
                    return filePublish;
            }
            return false;
        }

        private static IOException CheckJournalPublishAttempt(string journal, string label, bool replace, int failures,
            int expectedAttempts, bool expectedSaved, Func<string, Exception> newFault)
        {
            const string fault = "injected publish fault";
            string log = journal + ".log";
            string oldLog = Logger.LogPath;
            bool timerSupported = Native.PowerThrottlingSupported && Native.EcoQoSWantMask(true) == 5;
            string receipt = timerSupported ? "|ordinary-v1|3|" : "|ordinary-v1|1|";
            int attempts = 0;
            bool observingRealPublish = false;
            IOException conflict = null;
            int fixtureThread = Thread.CurrentThread.ManagedThreadId;
            EventHandler<FirstChanceExceptionEventArgs> observe = (sender, args) =>
            {
                if (!observingRealPublish || Thread.CurrentThread.ManagedThreadId != fixtureThread) return;
                IOException error = args.Exception as IOException;
                if (IsJournalPublishConflict(error)) conflict = error;
            };
            string firstContents = null;
            DateTime stamped = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            Logger.LogPath = log;
            try
            {
                using (var worker = new OwnedWorker())
                {
                    worker.Change("priority 32");
                    if (timerSupported) worker.Change("qos 0 0");
                    var core = new SuppressionCore(journal);
                    SuppressionCore.BeforeJournalPublishForTest = (temporary, destination, replacing) =>
                    {
                        observingRealPublish = false;
                        if (destination != journal || replace != replacing || conflict != null) return;
                        string contents = File.ReadAllText(temporary);
                        if (replace && !contents.Contains(receipt)) return;
                        attempts++;
                        if (attempts == 1)
                        {
                            firstContents = contents;
                            File.SetLastWriteTimeUtc(temporary, stamped);
                        }
                        else
                        {
                            Check(contents == firstContents && File.GetLastWriteTimeUtc(temporary) == stamped,
                                label + " rewrote the staged temporary file during publish retry");
                        }
                        if (attempts <= failures) throw newFault(fault);
                        observingRealPublish = expectedSaved && attempts == expectedAttempts;
                    };
                    AcquireResult result;
                    AppDomain.CurrentDomain.FirstChanceException += observe;
                    try
                    {
                        result = core.Acquire(worker.Pid, worker.Name, SuppressReason.Background, null,
                            SuppressionLevel.Isolated, worker.Birth);
                    }
                    finally { AppDomain.CurrentDomain.FirstChanceException -= observe; }
                    if (conflict != null) return conflict;
                    CheckAcquired(core, worker, result);
                    Check(attempts == expectedAttempts, label + " publish attempted " + attempts + " times");
                    Check(File.ReadAllText(journal).Contains(receipt) == expectedSaved, label + " persisted the wrong ownership");
                    Check(OrdinaryOwnershipPending(core, worker.Pid) == !expectedSaved, label + " left the wrong in-memory pending state");
                    string logs = File.Exists(log) ? File.ReadAllText(log) : "";
                    Check(logs.Contains(fault) == !expectedSaved, label + " logged the wrong publish outcome");
                    SuppressionCore.BeforeJournalPublishForTest = null;
                    Release(core, worker);
                    Check(Native.GetPriorityClass(worker.Handle) == 32, label + " did not restore ordinary priority");
                    if (timerSupported) worker.CheckPower(0, 0, label + " did not restore ordinary timer");
                }
            }
            finally
            {
                SuppressionCore.BeforeJournalPublishForTest = null;
                Logger.LogPath = oldLog;
            }
            return null;
        }

        private static void OrdinaryOwnershipFailure()
        {
            bool timerSupported = Native.PowerThrottlingSupported && Native.EcoQoSWantMask(true) == 5;
            string receipt = timerSupported ? "|ordinary-v1|3|" : "|ordinary-v1|1|";
            foreach (bool batched in new[] { false, true })
            foreach (string exit in new[] { "release-owned", "release-changed", "retry" })
            using (var worker = new OwnedWorker())
            {
                string journal = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ownership-" + batched + "-" + exit + ".state");
                var core = new SuppressionCore(journal);
                worker.Change("priority 32");
                if (timerSupported) worker.Change("qos 0 0");
                int failures = 0;
                SuppressionCore.BeforeJournalPublishForTest = (temporary, destination, replacing) =>
                {
                    if (destination != journal || !File.ReadAllText(temporary).Contains(receipt)) return;
                    failures++;
                    throw new IOException("persistent ordinary ownership publish fault");
                };
                try
                {
                    if (batched)
                    {
                        core.BeginBackgroundJournalRound();
                        core.BeginBatch();
                        try
                        {
                            Check(core.Acquire(worker.Pid, worker.Name, SuppressReason.Background, null, SuppressionLevel.Isolated, worker.Birth)
                                == AcquireResult.NewlyThrottled, "The ordinary acquisition did not queue");
                        }
                        finally
                        {
                            SuppressionCore.BatchResult applied = core.EndBatch();
                            int checkpoints = core.EndBackgroundJournalRound(true);
                            Check(applied.WasApplied(worker.Pid) && checkpoints == 2, "The failed ownership checkpoint did not preserve the background apply");
                        }
                    }
                    else Acquire(core, worker);
                    Check(failures == 5 && OrdinaryOwnershipPending(core, worker.Pid)
                        && !File.ReadAllText(journal).Contains(receipt), exit + " must keep the failed ownership write pending");
                    Check(Native.GetPriorityClass(worker.Handle) == 64
                        && Native.QueryIoPriority(worker.Handle) == 0 && Native.QueryPagePriority(worker.Handle) == 1,
                        exit + " rolled back settings after an ownership failure");
                    if (timerSupported) worker.CheckPower(5, 5, exit + " must retain the written timer while pending");

                    bool changed = exit != "release-owned";
                    if (changed)
                    {
                        worker.Change("priority 128");
                        if (timerSupported) worker.Change("qos 5 1");
                        ReconcileOwnership(core, worker, batched);
                        Check(failures == 10 && OrdinaryOwnershipPending(core, worker.Pid),
                            exit + " did not retry the pending save on the next scan");
                        Check(Native.GetPriorityClass(worker.Handle) == 128, exit + " reapplied Idle over the program's priority");
                        if (timerSupported) worker.CheckPower(5, 1, exit + " resealed the program's timer");
                    }

                    if (exit == "retry")
                    {
                        SuppressionCore.BeforeJournalPublishForTest = null;
                        ReconcileOwnership(core, worker, batched);
                        Check(!OrdinaryOwnershipPending(core, worker.Pid) && File.ReadAllText(journal).Contains(receipt),
                            "The next scan did not persist the pending ownership");
                        Check(Native.GetPriorityClass(worker.Handle) == 128, "Retrying the ownership save reapplied Idle");
                        if (timerSupported) worker.CheckPower(5, 1, "Retrying the ownership save resealed the program's timer");
                    }

                    if (batched)
                    {
                        core.BeginBackgroundJournalRound();
                        try
                        {
                            core.SetResourceJobMode(-1, null);
                            Check(core.ReleaseReason(SuppressReason.Background) == 1, exit + " session release failed");
                        }
                        finally { core.EndBackgroundJournalRound(false); }
                    }
                    else Release(core, worker);
                    Check(!core.BackgroundReleasePending && Native.GetPriorityClass(worker.Handle) == (changed ? 128u : 32u)
                        && Native.QueryIoPriority(worker.Handle) == 2 && Native.QueryPagePriority(worker.Handle) == 5,
                        exit + " did not restore owned fields and preserve the program's changes on release");
                    if (timerSupported) worker.CheckPower(changed ? 4 : 0, 0, exit + " did not restore owned timer or preserve its changed state");
                }
                finally { SuppressionCore.BeforeJournalPublishForTest = null; }
            }
        }

        private static void ReconcileOwnership(SuppressionCore core, OwnedWorker worker, bool batched)
        {
            if (batched) core.BeginBackgroundJournalRound();
            try
            {
                Check(core.Reconcile(worker.Pid, worker.Name, SuppressReason.Background),
                    "Reconcile lost the process with pending ownership");
            }
            finally { if (batched) core.EndBackgroundJournalRound(true); }
        }

        private static void GlobalMigration()
        {
            string key = BackgroundGovernancePolicy.SettingKey;
            Check(Settings.SaveStrDurable("BackgroundGovernanceResetV1", "1") && Settings.SaveStrDurable(key, "2"),
                "Cannot seed the former global level and prior release marker");
            BackgroundGovernancePolicy.ResetForThisVersionOnce();
            Check(Settings.LoadStr(key, "") == "0", "The one-time upgrade reset must select Normal");
            Check(Settings.SaveStrDurable(key, "2"), "Cannot seed a legacy setting for an upgrade already marked complete");
            BackgroundGovernancePolicy.ResetForThisVersionOnce();
            Check(Settings.LoadStr(key, "") == "2", "The one-time reset must not run again");

            Settings.BeforeStringWriteForTest = name =>
            {
                if (name == key) throw new IOException("Cannot write the test setting");
            };
            try
            {
                Check(PolicyResolver.Global().BackgroundGovernanceMode == BackgroundGovernancePolicy.Normal
                    && Settings.LoadStr(key, "") == "2", "A failed global migration must use Normal and leave the original value");
            }
            finally { Settings.BeforeStringWriteForTest = null; }

            Check(PolicyResolver.Global().BackgroundGovernanceMode == BackgroundGovernancePolicy.Aggressive
                && Settings.LoadStr(key, "") == "1", "Reading the former global level must durably migrate it to Aggressive");
            Check(Settings.SaveStrDurable(key, "7")
                && PolicyResolver.Global().BackgroundGovernanceMode == BackgroundGovernancePolicy.Normal
                && Settings.LoadStr(key, "") == "7", "An unknown global value must retain its existing fallback");
        }

        private static string Encode(string value)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
        }

        private static void GameProfileMigration()
        {
            string directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "governance-profile");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, GameProfileStore.FileName);
            string key = PolicyCatalog.KeyBackgroundGovernance;
            GameProfile game = GameProfileStore.NewProfile("Migrated game", null);
            GameProfile other = GameProfileStore.NewProfile("Unchanged game", null);
            game.Overrides[PolicyCatalog.KeyBoost] = "0";
            other.Overrides[key] = "0";
            string record = "O|" + Encode(game.Id) + "|" + Encode(key) + "|";
            File.WriteAllBytes(path, GameProfileStore.SnapshotBytes(new[] { game, other }));
            File.AppendAllText(path, record + Encode("2") + Environment.NewLine);
            string before = File.ReadAllText(path);

            var blocked = new GameProfileStore(directory);
            blocked.BeforeReplaceForTest = attempt => { throw new UnauthorizedAccessException("Cannot replace the test profile"); };
            List<GameProfile> loaded = blocked.LoadProfiles();
            Check(blocked.SaveFailed && !blocked.LoadFailed && loaded[0].Overrides[key] == "1"
                && File.ReadAllText(path) == before, "A failed migration must use the existing profile save failure path");

            var store = new GameProfileStore(directory);
            loaded = store.LoadProfiles(false);
            Check(loaded[0].Overrides[key] == "1" && File.ReadAllText(path) == before,
                "Read-only profile loading must defer the rewrite");
            loaded = store.LoadProfiles();
            Check(!store.LoadFailed && !store.SaveFailed && loaded.Count == 2
                && PolicyResolver.For(loaded[0]).BackgroundGovernanceMode == BackgroundGovernancePolicy.Aggressive
                && loaded[0].Overrides[PolicyCatalog.KeyBoost] == "0" && loaded[1].Overrides[key] == "0",
                "Reading the former per-game level must preserve other profile settings");
            string persisted = File.ReadAllText(path);
            Check(persisted.Contains(record + Encode("1") + Environment.NewLine)
                && !persisted.Contains(record + Encode("2") + Environment.NewLine)
                && new GameProfileStore(directory).LoadProfiles()[0].Overrides[key] == "1",
                "The migrated per-game value must be saved as Aggressive and survive reload");
        }

        private static int Main(string[] args)
        {
            if (args.Length == 1 && args[0] == "--priority-worker") return RunPriorityWorker();
            try
            {
                Settings.UseTransientStoreForCurrentProcess();
                if (args.Length == 4 && args[0] == "--restart-controller") return RestartedController(args);
                GlobalMigration(); GameProfileMigration();
                Check(PolicyCatalog.Canonical(PolicyCatalog.KeyBackgroundGovernance, "1") == "1" && PolicyCatalog.Canonical(PolicyCatalog.KeyBackgroundGovernance, "2") == "0"
                    && PolicyCatalog.Canonical(PolicyCatalog.KeyBackgroundGovernance, "3") == "0" && PolicyCatalog.Canonical(PolicyCatalog.KeyBackgroundGovernance, "garbage") == "0",
                    "Governance setting must be canonical with safe default");
                Check(!BackgroundGovernancePolicy.Valid(2) && BackgroundGovernancePolicy.Parse("2") == BackgroundGovernancePolicy.Normal && !BackgroundGovernancePolicy.Save(2),
                    "Only Normal and Aggressive may be saved or read as current governance levels");
                int[] logical = { 4, 5, 6, 8, 12, 16, 24, 32 };
                uint[] rates = { 0, 6000, 5000, 3750, 2500, 1875, 1250, 938 };
                for (int i = 0; i < logical.Length; i++)
                {
                    Check(BackgroundGovernancePolicy.ResourceCpuRate(0, logical[i]) == 0,
                        "Normal governance must have no CPU cap on " + logical[i] + " logical processors");
                    Check(BackgroundGovernancePolicy.ResourceCpuRate(1, logical[i]) == rates[i],
                        "Aggressive CPU rate mismatch on " + logical[i] + " logical processors");
                }
                Check(BackgroundGovernancePolicy.ResourceCpuRate(7, 32) == 0 && BackgroundGovernancePolicy.ResourceIoBandwidth(7) == 0,
                    "An unknown level must fall back to Normal with no limits");
                Check(BackgroundGovernancePolicy.ResourceIoBandwidth(0) == 0 && BackgroundGovernancePolicy.ResourceIoBandwidth(1) == 10L * 1024 * 1024,
                    "Only Aggressive must limit disk bandwidth to 10 MiB/s");
                Check(RecoveryRecordKeys.Contains(RetiredBackgroundCpuCap.ReceiptKey) && RecoveryRecordKeys.Contains(RetiredBackgroundCpuCap.LegacyReceiptKey),
                    "Retired CPU cap receipts must still be swept by reset and settings archival");
                NativeOrdinaryValues(); NativeOrdinarySessionsAndRelease();
                JournalPublishRetries(); OrdinaryOwnershipFailure();
                Console.WriteLine("PASS Background governance runtime: " + checks + " checks; native priority and timer on owned processes; isolated settings and profile storage"); return 0;
            }
            catch (Exception e) { Console.Error.WriteLine(e); return 1; }
        }
    }
}
