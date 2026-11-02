#if PAVISE_SELFTEST && PAVISE_REPORT_SAMPLING_REMOVAL
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;

namespace PaviseApp
{
    internal static class ReportSamplingRemovalRunner
    {
        private static int assertions, suites, failures;
        private static string output, workspace;
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

        private static int Main(string[] args)
        {
            if (args.Length != 2) return 2;
            using (var watchdog = new Timer(delegate { Environment.Exit(124); }, null, 60000, Timeout.Infinite))
            {
                try
                {
                    output = Path.GetFullPath(args[0]); workspace = Path.GetFullPath(args[1]);
                    Directory.CreateDirectory(output);
                    Settings.UseTransientStoreForCurrentProcess();
                    Logger.ResetWriteBarrierForTest(); Logger.WritesEnabled = false;
                    Logger.LogPath = Path.Combine(output, "isolated.log"); Lang.Init();
                    Run("report-only samplers absent from production", RemovedSamplers);
                    Run("session accounting and brief survive removal", SessionAccounting);
                    Run("short-session threshold and reused identity", SessionBoundaries);
                    Run("session lifecycle records retain genuine restore failure", SessionMetadata);
                    Run("weak renderer observation is bounded per identity/session", RendererObservationBudget);
                    Run("necessary coordination budgets and write failures", delegate { SelfTests.RunFrameCoordinationTests(); });
                    Console.WriteLine("RESULT suites=" + suites + " failed=" + failures + " assertions="
                        + (assertions + SelfTests.RemovalFrameAssertions)
                        + " settings=transient logs=temp application_started=false tuning_workers_started=false system_writes=false");
                    return failures == 0 ? 0 : 1;
                }
                catch (Exception error) { Console.Error.WriteLine(error); return 1; }
                finally { Logger.WritesEnabled = false; Settings.UseTransientStoreForCurrentProcess(); }
            }
        }

        private static void Run(string name, Action action)
        {
            suites++;
            try { action(); Console.WriteLine("PASS " + name); }
            catch (Exception error)
            {
                while (error is TargetInvocationException && error.InnerException != null) error = error.InnerException;
                failures++; Console.Error.WriteLine("FAIL " + name + ": " + error);
            }
        }
        private static void Check(bool good, string message)
        { assertions++; if (!good) throw new InvalidOperationException(message); }
        private static object Get(GameMode mode, string name)
        { return typeof(GameMode).GetField(name, Hidden).GetValue(mode); }
        private static void Set(GameMode mode, string name, object value)
        { typeof(GameMode).GetField(name, Hidden).SetValue(mode, value); }
        private static void Call(GameMode mode, string method, params object[] args)
        { typeof(GameMode).GetMethod(method, Hidden).Invoke(mode, args); }
        private static GameMode Mode(string name)
        {
            Settings.UseTransientStoreForCurrentProcess(); Logger.WritesEnabled = false;
            string directory = Path.Combine(output, name); Directory.CreateDirectory(directory);
            var mode = new GameMode(directory, new SuppressionCore());
            Check(!mode.IsActive && Get(mode, "worker") == null, "Constructor unexpectedly entered the runtime");
            return mode;
        }
        private static string ResetLog(string name)
        {
            Logger.ResetWriteBarrierForTest(); Logger.LogPath = Path.Combine(output, name + ".log");
            if (File.Exists(Logger.LogPath)) File.Delete(Logger.LogPath);
            Logger.WritesEnabled = true; return Logger.LogPath;
        }
        private static void MakeLong(GameMode mode)
        { Set(mode, "repStart", Stopwatch.GetTimestamp() - (long)(70.0 * Stopwatch.Frequency)); }
        private static int Occurrences(string text, string token)
        {
            int count = 0, pos = 0;
            while ((pos = text.IndexOf(token, pos, StringComparison.Ordinal)) >= 0) { count++; pos += token.Length; }
            return count;
        }

        private static void RemovedSamplers()
        {
            Assembly assembly = typeof(GameMode).Assembly;
            Check(assembly.GetType("PaviseApp.CpuLimitProbe", false) == null, "CPU limit reporting type remains");
            Check(assembly.GetType("PaviseApp.VramSpillProbe", false) == null, "VRAM reporting worker remains");
            Check(typeof(GameMode).GetField("cpuLimit", Hidden) == null, "GameMode retains CPU limit probe state");
            foreach (string method in new[] { "SampleIfDue", "Summarize", "Reset", "Percent" })
                Check(typeof(GpuThrottleProbe).GetMethod(method, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static) == null,
                    "GPU periodic report method remains: " + method);
            Check(typeof(GpuThrottleProbe).GetMethod("InstantText", BindingFlags.Public | BindingFlags.Static) != null,
                "Explicit hardware observation lost its one-shot GPU read");
            Check(assembly.GetType("PaviseApp.FrameCoordinationSampler", false) != null,
                "Necessary thread coordination evidence was removed with reporting probes");
            foreach (string file in Directory.GetFiles(Path.Combine(workspace, "src"), "*.cs", SearchOption.AllDirectories))
            {
                string text = File.ReadAllText(file);
                Check(!text.Contains("CpuLimitProbe") && !text.Contains("VramSpillProbe"), "Removed probe still referenced: " + file);
                foreach (string method in new[] { "SampleIfDue", "Summarize", "Reset" })
                    Check(!text.Contains("GpuThrottleProbe." + method), "GPU periodic report still called: " + file);
            }
        }

        private static void SessionAccounting()
        {
            int language = Lang.Cur;
            try
            {
                foreach (int current in new[] { 0, 1, 2 })
                {
                    Lang.Cur = current; var mode = Mode("accounting-" + current);
                    string log = ResetLog("accounting-" + current);
                    string ended = null, briefed = null;
                    mode.SessionEnded += delegate(string msg) { ended = msg; };
                    mode.SessionBriefed += delegate(string msg) { briefed = msg; };
                    mode.ProbeSessionBegin("Sampling-free game");
                    Check((string)Get(mode, "repGame") == "Sampling-free game", "ReportBegin lost its game");
                    Check((long)Get(mode, "repPaviseCpuStart") >= 0, "Pavise boundary CPU account invalid");
                    using (Process own = Process.GetCurrentProcess())
                    {
                        mode.ProbeSessionTrack(own.Id, "Own-process read-only fixture");
                        Check(((Dictionary<int, long>)Get(mode, "repCpu")).ContainsKey(own.Id), "Boundary CPU tracking stopped working");
                        Check(((Dictionary<int, long>)Get(mode, "repCreation"))[own.Id] > 0, "Creation identity lost");
                        mode.ProbeSessionSeal(own.Id);
                        Check(!((Dictionary<int, long>)Get(mode, "repCpu")).ContainsKey(own.Id), "Seal did not retire tracked identity");
                        var sealedCpu = (Dictionary<int, long>)Get(mode, "repSealed");
                        Check(sealedCpu.ContainsKey(own.Id) && sealedCpu[own.Id] >= 0, "Sealed CPU account missing");
                        sealedCpu[own.Id] = 2 * TimeSpan.TicksPerSecond;
                    }
                    MakeLong(mode); mode.ProbeSessionFinish();
                    Check(ended != null && briefed != null, "Completed long session stopped notifying the UI");
                    Check(GameMode.LastSessionBrief == briefed && briefed.Contains("Sampling-free game"), "Last session was not persisted transiently");
                    Check(ended.Contains("Own-process read-only fixture"), "Background CPU top-account detail was lost");
                    string ownCpu = Lang.F("rep.pavise.cpu", "0.00");
                    int suffix = ownCpu.IndexOf("0.00", StringComparison.Ordinal);
                    Check(suffix >= 0 && ended.Contains(ownCpu.Substring(0, suffix)), "Boundary-only Pavise CPU statistic missing");
                    Check(Get(mode, "repGame") == null && (long)Get(mode, "repPaviseCpuStart") == 0, "Finish retained stale report identity");
                    Check(((Dictionary<int, long>)Get(mode, "repSealed")).Count == 0, "Finish retained sealed accounts");
                    string content = File.ReadAllText(log);
                    Check(content.Contains("Sampling-free game"), "End report vanished from raw log");
                    List<LogEventEntry> entries = LogStreamView.ParseEvents(content);
                    Check(entries.Count == 1 && !entries.Exists(delegate(LogEventEntry e)
                        { return e.Module == "CPU" || e.Module == "GPU" || e.Module == "VRAM"; }),
                        "Pure performance-limit report still logged");
                    Check(entries[0].Raw.Contains(Lang.T("log.gamemodesession.1") + ended), "Boundary CPU account differs from its session event");
                    Check(!mode.IsActive && Get(mode, "worker") == null, "Reporting started a runtime worker");
                }
                Check(GameMode.AverageCpuPercent(0, TimeSpan.FromSeconds(5)) == 0, "Zero CPU account invalid");
                Check(GameMode.AverageCpuPercent(TimeSpan.TicksPerSecond, TimeSpan.Zero) == 0, "Zero duration account invalid");
                double expected = 100.0 / Environment.ProcessorCount;
                Check(Math.Abs(GameMode.AverageCpuPercent(TimeSpan.TicksPerSecond, TimeSpan.FromSeconds(1)) - expected) < .0001,
                    "CPU statistic stopped accounting for logical processor count");
            }
            finally { Lang.Cur = language; Logger.WritesEnabled = false; }
        }

        private static void SessionBoundaries()
        {
            var mode = Mode("boundaries"); string log = ResetLog("boundaries");
            Settings.SaveStr(GameMode.LastSessionKey, "retained brief"); int ended = 0, briefed = 0;
            mode.SessionEnded += delegate { ended++; }; mode.SessionBriefed += delegate { briefed++; };
            mode.ProbeSessionBegin("Launcher flash"); mode.ProbeSessionFinish();
            Check(ended == 0 && briefed == 0, "Short launcher flash emitted a session balloon/brief");
            Check(GameMode.LastSessionBrief == "retained brief", "Short session replaced a valid prior summary");
            string once = File.ReadAllText(log); mode.ProbeSessionFinish();
            Check(File.ReadAllText(log) == once, "Repeated Finish duplicated the report");

            mode.ProbeSessionBegin("Reused identity fixture");
            using (Process own = Process.GetCurrentProcess())
            {
                mode.ProbeSessionTrack(own.Id, "stale-process-account");
                ((Dictionary<int, long>)Get(mode, "repCreation"))[own.Id] = -1;
                ((Dictionary<int, long>)Get(mode, "repCpu"))[own.Id] = 0;
            }
            MakeLong(mode); mode.ProbeSessionFinish();
            Check(!File.ReadAllText(log).Contains(Lang.F("rep.top", "stale-process-account", "2.0s")), "Stale identity gained CPU credit");
            string text = File.ReadAllText(log);
            Check(!text.Contains(Lang.F("rep.top", "stale-process-account", "<1s")) && !text.Contains("stale-process-account"),
                "Process identity reuse produced a top-CPU claim");
            Check(ended == 1 && briefed == 1, "Valid long session lost its end notifications");
            Check(GameMode.SameReportedProfile("ABC", "abc"), "Profile identity lost case-insensitive handoff semantics");
            Check(!GameMode.SameReportedProfile("", "abc") && !GameMode.SameReportedProfile("A", "B"), "Empty/different identities merged");
            Logger.WritesEnabled = false;
        }

        private static void SessionMetadata()
        {
            var mode = Mode("metadata"); Settings.Save(AutomaticPolicy.VersionKey, true);
            string log = ResetLog("metadata"); Set(mode, "activeGame", "Game | keep name");
            mode.ProbeSessionBegin("Game | keep name"); Call(mode, "BeginAutomationEvidence");
            string token = mode.AutomationSession;
            Call(mode, "EndAutomationEvidence"); mode.ProbeSessionFinish();
            string intermediate = File.ReadAllText(log);
            Check(Occurrences(intermediate, "SESSION [" + token + "] start | Game | keep name") == 1, "Actual SESSION start metadata lost");
            Check(Occurrences(intermediate, "SESSION [" + token + "] end | Game | keep name") == 1, "Actual SESSION end metadata lost");
            Check(!intermediate.Contains("SESSION [" + token + "] restored |"), "Finish invented restoration before recovery completed");
            Call(mode, "RecordAutomation", "session", "restore-failed");
            Call(mode, "RecordAutomation", "session", "restore-failed");
            Call(mode, "RecordAutomation", "session", "restored");
            string content = File.ReadAllText(log);
            Check(Occurrences(content, "WARN SESSION [" + token + "] restore-failed |") == 1, "Real restoration failure disappeared or duplicated");
            Check(Occurrences(content, "PASS SESSION [" + token + "] restored |") == 1, "Successful retry failed to close original session");
            Check(!mode.IsActive && Get(mode, "worker") == null, "Session metadata unexpectedly activated tuning");
            Logger.WritesEnabled = false;
        }

        private static void DrainObservation(GameMode mode)
        {
            var clock = Stopwatch.StartNew();
            while (((int)Get(mode, "rendererActivityBusy") != 0
                || (int)Get(mode, "rendererObservationTraceBusy") != 0) && clock.ElapsedMilliseconds < 3000)
                Thread.Sleep(1);
            Check((int)Get(mode, "rendererActivityBusy") == 0 && (int)Get(mode, "rendererGpuSamplingBusy") == 0,
                "Observation worker did not release shared GPU sampling gate");
            Check((int)Get(mode, "rendererObservationTraceBusy") == 0, "Optional observation log worker did not drain");
        }
        private static void RendererObservationBudget()
        {
            string directory = Path.Combine(output, "renderer-budget"); Directory.CreateDirectory(directory);
            string executable = Path.Combine(directory, "Inert-renderer.exe");
            File.WriteAllBytes(executable, new byte[] { 1, 2, 3, 4 });
            var profile = GameProfileStore.NewProfile("Renderer fixture", directory, executable);
            Check(new GameProfileStore(directory).Save(new[] { profile }), "Temporary game fixture could not be saved");
            var mode = Mode("renderer-budget");
            Set(mode, "enabled", true);
            GameDetection target = new GameDetection
            {
                Profile = profile.Clone(), RendererPid = 80210, RendererCreation = 1337000000000000L,
                RendererPath = executable, RendererName = "Inert-renderer", RendererCandidateSelected = true,
                RendererUserSelected = true, RendererForeground = true
            };
            target.FamilyPids.Add(target.RendererPid);
            Set(mode, "activeDetection", RendererHandoffTracker.Copy(target));
            int calls = 0; long now = 100000; double utilization = 0;
            mode.RendererTestObservationNow = delegate { return now; };
            mode.RendererTestForeground = delegate { return target.RendererPid; };
            mode.RendererTestIdentity = delegate(GameDetection value) { return RendererHandoffTracker.SameIdentity(value, target); };
            mode.RendererTestGpu = delegate { throw new InvalidOperationException("Unexpected handoff GPU sampling"); };
            mode.RendererTestActivityGpu = delegate(GameDetection value, Func<bool> canceled)
            {
                Interlocked.Increment(ref calls);
                return canceled() ? null : new Dictionary<int, double> { { value.RendererPid, utilization } };
            };
            try
            {
                for (int i = 0; i < 30; i++)
                {
                    Call(mode, "MaybeObserveRendererActivity", target); DrainObservation(mode); now += 300000;
                }
                Check(calls == 5 && (int)Get(mode, "rendererActivityAttempts") == 5,
                    "Weak renderer kept being sampled after its five-attempt budget");
                Check(!mode.HasRendererObservation(profile), "Weak utilization falsely confirmed a renderer badge");
                utilization = 42;
                Call(mode, "MaybeObserveRendererActivity", target); DrainObservation(mode);
                Check(calls == 5 && !mode.HasRendererObservation(profile), "Exhausted target sampled again merely because utilization changed");

                utilization = 0; target.RendererCreation++;
                Set(mode, "activeDetection", RendererHandoffTracker.Copy(target));
                for (int i = 0; i < 30; i++)
                {
                    Call(mode, "MaybeObserveRendererActivity", target); DrainObservation(mode); now += 300000;
                }
                Check(calls == 10 && (int)Get(mode, "rendererActivityAttempts") == 5,
                    "A new renderer identity did not receive exactly one fresh budget");
                Call(mode, "InvalidateRendererHandoff");
                Call(mode, "MaybeObserveRendererActivity", target); DrainObservation(mode);
                Check(calls == 11 && (int)Get(mode, "rendererActivityAttempts") == 1,
                    "A new game session failed to reset the same renderer's budget");
                now += 9999;
                Call(mode, "MaybeObserveRendererActivity", target); DrainObservation(mode);
                Check(calls == 11, "The per-session reset bypassed the shared ten-second interval");

                now += 1; utilization = 42;
                Call(mode, "MaybeObserveRendererActivity", target); DrainObservation(mode);
                Check(calls == 12 && mode.HasRendererObservation(profile), "Valid renderer evidence no longer confirms the exact profile");
                now += 300000;
                for (int i = 0; i < 100; i++) Call(mode, "MaybeObserveRendererActivity", target);
                DrainObservation(mode);
                Check(calls == 12, "A cached renderer caused repeated GPU samples");
                Check(Get(mode, "worker") == null && !mode.IsActive, "Observation test started game runtime scheduling");
            }
            finally { Call(mode, "InvalidateRendererHandoff"); DrainObservation(mode); Set(mode, "enabled", false); }
        }
    }

    internal static partial class SelfTests
    {
        internal static int RemovalFrameAssertions;
        private static void Eq<T>(T expected, T actual)
        {
            RemovalFrameAssertions++;
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new InvalidOperationException("Expected " + expected + ", actual " + actual);
        }
        public static bool TryHandleRuntimeMode(string[] args)
        { throw new InvalidOperationException("Sampling removal checks cannot start the application runtime"); }
    }
}
#endif
