// @author bdth 2074055628@qq.com
// 文件用途：在隔离配置中运行真实 Aegis 核心，供 PerfLab 做可重复 A/B 测量

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace AegisApp
{
    internal static class PerfEngineProgram
    {
        private const string ReportSchema = "aegis-perflab-engine-v4";
        private const string WorkerRosterSchema =
            "aegis-perflab-worker-roster-v1";
        private const int DiscoverIntervalMs = 1000;
        private const int MeasurementPollMs = 100;
        private const int RequiredPolicyCoveragePercent = 90;
        private const int RequiredMeasurementDensityPercent = 80;

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        private sealed class WorkerIdentity
        {
            public int Pid;
            public long Creation;
            public IntPtr Handle;
        }

        private sealed class WorkerRoster : IDisposable
        {
            public readonly List<WorkerIdentity> Workers =
                new List<WorkerIdentity>();

            public int Count { get { return Workers.Count; } }

            public void Dispose()
            {
                foreach (WorkerIdentity worker in Workers)
                if (worker.Handle != IntPtr.Zero)
                {
                    Native.CloseHandle(worker.Handle);
                    worker.Handle = IntPtr.Zero;
                }
                Workers.Clear();
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessIoCounters(
            IntPtr process, out IoCounters counters);

        private static int Main(string[] args)
        {
            if (args.Length != 13)
            {
                Console.Error.WriteLine(
                    "usage: Aegis.PerfEngine.exe <renderer.exe> <background.exe> <max-seconds>"
                    + " <report> <armed-event> <ready-event> <start-event>"
                    + " <start-ack-event> <done-event>"
                    + " <worker-roster> <roster-ready-event>"
                    + " <overhead|policy> <run-nonce>");
                return 2;
            }

            string rendererPath;
            string backgroundPath;
            try
            {
                rendererPath = Path.GetFullPath(args[0]);
                backgroundPath = Path.GetFullPath(args[1]);
            }
            catch
            {
                return 2;
            }

            int seconds;
            bool policyLane = string.Equals(
                args[11], "policy", StringComparison.OrdinalIgnoreCase);
            if (!policyLane && !string.Equals(
                args[11], "overhead", StringComparison.OrdinalIgnoreCase))
                return 2;
            if (string.IsNullOrWhiteSpace(args[12])
                || args[12].Length > 128)
                return 2;
            if (!File.Exists(rendererPath) || !File.Exists(backgroundPath)
                || !int.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds)
                || seconds < 3 || seconds > 600)
                return 2;

            string reportPath = Path.GetFullPath(args[3]);
            string runDir = Path.Combine(
                Path.GetDirectoryName(reportPath),
                "engine-" + Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture));
            Directory.CreateDirectory(runDir);

            Settings.UseTransientStoreForCurrentProcess();
            Logger.LogPath = Path.Combine(runDir, "Aegis.perf.log");

            var engineCore = new SuppressionCore(Path.Combine(
                runDir, "engine." + SuppressionCore.StateFileName));
            var tamer = new Tamer(engineCore);
            var gameMode = new GameMode(runDir, engineCore);
            LolOptimizationService lol = null;
            ProcNotify notify = null;
            int measurementSuppressedMin = int.MaxValue;
            int measurementSuppressedMax = 0;
            int measurementSuppressedLast = 0;
            int measurementSuppressedPositiveSamples = 0;
            int measurementSuppressedSamples = 0;
            int measurementMinCoveredWorkers = int.MaxValue;
            int measurementLastCoveredWorkers = 0;
            int measurementFullCoverageSamples = 0;
            int measurementCoverageSamples = 0;
            long lastSuppressionSampleTicks = 0;
            long startedTicks = Stopwatch.GetTimestamp();
            Process self = Process.GetCurrentProcess();
            long cpuStarted = self.TotalProcessorTime.Ticks;
            IoCounters ioStarted;
            GetProcessIoCounters(self.Handle, out ioStarted);
            double workingSetTotal = 0;
            double privateTotal = 0;
            int resourceSamples = 0;
            int maxHandles = 0;
            int maxThreads = 0;
            bool readyWritten = false;
            bool eligibilityLogged = false;
            bool measurementStarted = false;
            bool success = false;
            long measurementCpuStarted = 0;
            long measurementStartedTicks = 0;
            IoCounters measurementIoStarted = new IoCounters();
            long measurementCpuEnded = 0;
            long measurementEndedTicks = 0;
            IoCounters measurementIoEnded = new IoCounters();
            long measurementApplyOperationsStarted = 0;
            long measurementApplyOperationsEnded = 0;
            long measurementGameModeScansStarted = 0;
            long measurementGameModeScansEnded = 0;
            double measurementElapsedMs = 0;
            int measurementExpectedSamples = 0;
            double measurementSampleDensityPercent = 0;
            bool measurementEnded = false;
            EventWaitHandle armedEvent = null;
            EventWaitHandle readyEvent = null;
            EventWaitHandle startEvent = null;
            EventWaitHandle startAckEvent = null;
            EventWaitHandle doneEvent = null;
            EventWaitHandle rosterReadyEvent = null;
            WorkerRoster workerRoster = null;

            try
            {
                armedEvent = EventWaitHandle.OpenExisting(args[4]);
                readyEvent = EventWaitHandle.OpenExisting(args[5]);
                startEvent = EventWaitHandle.OpenExisting(args[6]);
                startAckEvent = EventWaitHandle.OpenExisting(args[7]);
                doneEvent = EventWaitHandle.OpenExisting(args[8]);
                rosterReadyEvent = EventWaitHandle.OpenExisting(args[10]);
                ConfigureGameMode(
                    gameMode, rendererPath, backgroundPath, policyLane);
                tamer.Paused = true;
                tamer.Start();
                gameMode.Start();

                lol = new LolOptimizationService();
                lol.Start();

                notify = new ProcNotify();
                notify.CaptureStartIdentity = delegate(string name, int session)
                {
                    return SameProcessName(name, backgroundPath)
                        || SameProcessName(name, rendererPath);
                };
                notify.BatchChanged += delegate(ProcessChangeBatch batch)
                {
                    gameMode.NotifyProcessChanges(batch);
                    tamer.NotifyProcessChanges(batch);
                    lol.NotifyProcessChanges(batch);
                    if (batch == null) return;
                };
                notify.Start();
                gameMode.ProcessEventsAvailable = notify.IsActive;
                tamer.ProcessEventsAvailable = notify.IsActive;
                armedEvent.Set();
                if (!rosterReadyEvent.WaitOne(30000))
                    throw new TimeoutException(
                        "Controller did not publish the long-lived worker roster.");
                workerRoster = LoadWorkerRoster(
                    args[9], args[12], backgroundPath,
                    Process.GetCurrentProcess().SessionId);

                DateTime until = DateTime.UtcNow.AddSeconds(seconds);
                Stopwatch cadence = Stopwatch.StartNew();
                long nextDiscoverMs = 0;
                long nextResourceSampleMs = 0;
                while (DateTime.UtcNow < until)
                {
                    if (!measurementStarted && startEvent.WaitOne(0))
                    {
                        measurementStarted = true;
                        measurementSuppressedMin = int.MaxValue;
                        measurementSuppressedMax = 0;
                        measurementSuppressedLast = 0;
                        measurementSuppressedPositiveSamples = 0;
                        measurementSuppressedSamples = 0;
                        measurementMinCoveredWorkers = int.MaxValue;
                        measurementLastCoveredWorkers = 0;
                        measurementFullCoverageSamples = 0;
                        measurementCoverageSamples = 0;
                        measurementStartedTicks = Stopwatch.GetTimestamp();
                        measurementCpuStarted = self.TotalProcessorTime.Ticks;
                        GetProcessIoCounters(
                            self.Handle, out measurementIoStarted);
                        measurementApplyOperationsStarted =
                            engineCore.ApplyOperations;
                        measurementGameModeScansStarted =
                            gameMode.ProcessScanCount;
                        nextDiscoverMs = cadence.ElapsedMilliseconds;
                        nextResourceSampleMs = nextDiscoverMs;

                        startAckEvent.Set();
                    }

                    if (measurementStarted && doneEvent.WaitOne(0))
                    {
                        measurementEndedTicks = Stopwatch.GetTimestamp();
                        measurementCpuEnded = self.TotalProcessorTime.Ticks;
                        GetProcessIoCounters(
                            self.Handle, out measurementIoEnded);
                        measurementApplyOperationsEnded =
                            engineCore.ApplyOperations;
                        measurementGameModeScansEnded =
                            gameMode.ProcessScanCount;
                        bool countEndSample =
                            lastSuppressionSampleTicks == 0
                            || ElapsedMilliseconds(
                                lastSuppressionSampleTicks,
                                measurementEndedTicks)
                                >= DiscoverIntervalMs / 2.0;
                        CaptureSuppressionObservation(
                            engineCore,
                            countEndSample,
                            ref measurementSuppressedMin,
                            ref measurementSuppressedMax,
                            ref measurementSuppressedLast,
                            ref measurementSuppressedPositiveSamples,
                            ref measurementSuppressedSamples);
                        CaptureWorkerCoverageObservation(
                            CountCoveredWorkers(
                                engineCore, workerRoster),
                            workerRoster.Count,
                            countEndSample,
                            ref measurementMinCoveredWorkers,
                            ref measurementLastCoveredWorkers,
                            ref measurementFullCoverageSamples,
                            ref measurementCoverageSamples);
                        measurementElapsedMs = ElapsedMilliseconds(
                            measurementStartedTicks, measurementEndedTicks);
                        measurementExpectedSamples =
                            ExpectedMeasurementSamples(measurementElapsedMs);
                        measurementSampleDensityPercent =
                            MeasurementDensityPercent(
                                measurementSuppressedSamples,
                                measurementExpectedSamples);
                        measurementEnded = true;
                        break;
                    }

                    long nowMs = cadence.ElapsedMilliseconds;
                    if (nowMs >= nextDiscoverMs)
                    {
                        if (!eligibilityLogged)
                            eligibilityLogged = LogSyntheticEligibility(
                                rendererPath, backgroundPath,
                                self.Id, policyLane);
                        engineCore.RetryPending();
                        int suppressed = engineCore.CountThrottled(
                            SuppressReason.Background);
                        int coveredWorkers = CountCoveredWorkers(
                            engineCore, workerRoster);
                        if (measurementStarted)
                        {
                            measurementSuppressedSamples++;
                            measurementSuppressedLast = suppressed;
                            if (suppressed < measurementSuppressedMin)
                                measurementSuppressedMin = suppressed;
                            if (suppressed > measurementSuppressedMax)
                                measurementSuppressedMax = suppressed;
                            if (suppressed > 0)
                                measurementSuppressedPositiveSamples++;
                            lastSuppressionSampleTicks =
                                Stopwatch.GetTimestamp();
                            CaptureWorkerCoverageObservation(
                                coveredWorkers,
                                workerRoster.Count,
                                true,
                                ref measurementMinCoveredWorkers,
                                ref measurementLastCoveredWorkers,
                                ref measurementFullCoverageSamples,
                                ref measurementCoverageSamples);
                        }
                        bool policyReady = policyLane
                            ? coveredWorkers == workerRoster.Count
                                && gameMode.BoostStateVerified
                            : gameMode.IsActive;
                        if (!readyWritten && policyReady)
                        {
                            readyEvent.Set();
                            readyWritten = true;
                        }
                        nextDiscoverMs = cadence.ElapsedMilliseconds
                            + DiscoverIntervalMs;
                    }

                    nowMs = cadence.ElapsedMilliseconds;
                    if (measurementStarted
                        && nowMs >= nextResourceSampleMs)
                    {
                        self.Refresh();
                        workingSetTotal += self.WorkingSet64 / 1048576.0;
                        privateTotal += self.PrivateMemorySize64 / 1048576.0;
                        resourceSamples++;
                        if (self.HandleCount > maxHandles) maxHandles = self.HandleCount;
                        int threads = self.Threads.Count;
                        if (threads > maxThreads) maxThreads = threads;
                        nextResourceSampleMs = cadence.ElapsedMilliseconds
                            + DiscoverIntervalMs;
                    }

                    if (measurementStarted)
                        doneEvent.WaitOne(MeasurementPollMs);
                    else
                        startEvent.WaitOne(MeasurementPollMs);
                }
                success = readyWritten && measurementStarted
                    && measurementEnded
                    && MeasurementDensityValid(
                        measurementSuppressedSamples,
                        measurementExpectedSamples)
                    && measurementCoverageSamples
                        == measurementSuppressedSamples
                    && (!policyLane || WorkerCoverageValid(
                        workerRoster.Count,
                        measurementLastCoveredWorkers,
                        measurementFullCoverageSamples,
                        measurementCoverageSamples));
            }
            catch (Exception ex)
            {
                try { File.AppendAllText(Logger.LogPath, ex + Environment.NewLine); }
                catch { }
                success = false;
            }
            finally
            {

                if (!measurementEnded)
                {
                    measurementEndedTicks = Stopwatch.GetTimestamp();
                    measurementCpuEnded = self.TotalProcessorTime.Ticks;
                    GetProcessIoCounters(
                        self.Handle, out measurementIoEnded);
                    measurementApplyOperationsEnded =
                        engineCore.ApplyOperations;
                    measurementGameModeScansEnded =
                        gameMode.ProcessScanCount;
                    measurementElapsedMs = ElapsedMilliseconds(
                        measurementStarted ? measurementStartedTicks : startedTicks,
                        measurementEndedTicks);
                    measurementExpectedSamples =
                        ExpectedMeasurementSamples(measurementElapsedMs);
                    measurementSampleDensityPercent =
                        MeasurementDensityPercent(
                            measurementSuppressedSamples,
                            measurementExpectedSamples);
                }
                long elapsedTicks = measurementEndedTicks
                    - (measurementStarted ? measurementStartedTicks : startedTicks);
                long cpuTicks = measurementCpuEnded
                    - (measurementStarted ? measurementCpuStarted : cpuStarted);
                double elapsedSeconds = elapsedTicks <= 0 ? 0
                    : elapsedTicks / (double)Stopwatch.Frequency;
                double cpu = elapsedSeconds <= 0 || cpuTicks < 0 ? 0
                    : cpuTicks / (double)TimeSpan.TicksPerSecond
                        / elapsedSeconds / Math.Max(
                            1, Environment.ProcessorCount) * 100.0;
                ulong readBytes = SafeDelta(
                    measurementIoEnded.ReadTransferCount,
                    measurementStarted
                        ? measurementIoStarted.ReadTransferCount
                        : ioStarted.ReadTransferCount);
                ulong writeBytes = SafeDelta(
                    measurementIoEnded.WriteTransferCount,
                    measurementStarted
                        ? measurementIoStarted.WriteTransferCount
                        : ioStarted.WriteTransferCount);

                if (notify != null)
                    try { notify.Stop(); } catch { }
                if (lol != null)
                    try { lol.Dispose(); } catch { }
                try { tamer.Stop(); } catch { }
                try { gameMode.Stop(); } catch { }
                try { engineCore.ReleaseReason(SuppressReason.Background); } catch { }

                try
                {
                    WriteReport(reportPath, args[11], args[12], success, cpu,
                        measurementElapsedMs,
                        measurementExpectedSamples,
                        measurementSampleDensityPercent,
                        measurementSuppressedSamples == 0
                            ? 0 : measurementSuppressedMin,
                        measurementSuppressedMax,
                        measurementSuppressedLast,
                        measurementSuppressedPositiveSamples,
                        measurementSuppressedSamples,
                        workerRoster == null ? 0 : workerRoster.Count,
                        measurementCoverageSamples == 0
                            ? 0 : measurementMinCoveredWorkers,
                        measurementLastCoveredWorkers,
                        measurementFullCoverageSamples,
                        measurementCoverageSamples,
                        Math.Max(0, measurementApplyOperationsEnded
                            - measurementApplyOperationsStarted),
                        Math.Max(0, measurementGameModeScansEnded
                            - measurementGameModeScansStarted),
                        resourceSamples == 0 ? 0 : workingSetTotal / resourceSamples,
                        resourceSamples == 0 ? 0 : privateTotal / resourceSamples,
                        maxHandles, maxThreads,
                        readBytes, writeBytes);
                }
                catch (Exception ex)
                {
                    success = false;
                    try
                    {
                        File.AppendAllText(
                            Logger.LogPath,
                            "report write failed: " + ex
                            + Environment.NewLine);
                    }
                    catch { }
                }
                if (armedEvent != null) armedEvent.Dispose();
                if (readyEvent != null) readyEvent.Dispose();
                if (startEvent != null) startEvent.Dispose();
                if (startAckEvent != null) startAckEvent.Dispose();
                if (doneEvent != null) doneEvent.Dispose();
                if (rosterReadyEvent != null) rosterReadyEvent.Dispose();
                if (workerRoster != null) workerRoster.Dispose();
            }
            self.Dispose();
            return success ? 0 : 1;
        }

        private static void ConfigureGameMode(
            GameMode gameMode, string rendererPath,
            string backgroundPath, bool policyLane)
        {
            gameMode.Preset = PerformancePreset.Custom;
            gameMode.SuppressBackground = policyLane;
            gameMode.BoostGame = policyLane;
            gameMode.StrictCoreIsolation = false;

            gameMode.AggressiveSuppression = policyLane;
            gameMode.NetOptimize = false;
            gameMode.IdleStateDisable = false;
            gameMode.VisualFxDowngrade = false;
            gameMode.MmcssPriority = false;
            gameMode.PauseDownloads = false;
            gameMode.FgSchedBoost = false;
            gameMode.PauseSvcIndex = false;
            gameMode.NotifQuiet = false;
            gameMode.TrimWorkingSet = false;
            gameMode.GpuHighPerf = false;
            gameMode.DisableFso = false;
            gameMode.KillGameDvr = false;
            gameMode.HzGuard = false;
            gameMode.PowerPlanSwitch = false;
            gameMode.RestrictBackgroundSuppressionToPaths(
                new[] { backgroundPath });
            gameMode.AddGameExecutable("AEGIS PERF RENDERER", rendererPath);
            gameMode.Enabled = true;
        }

        private static bool SameProcessName(string name, string executable)
        {
            return !string.IsNullOrEmpty(name) && string.Equals(
                name, Path.GetFileNameWithoutExtension(executable),
                StringComparison.OrdinalIgnoreCase);
        }

        private static WorkerRoster LoadWorkerRoster(
            string path, string expectedNonce,
            string expectedWorkerPath, int expectedSession)
        {
            string fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath))
                throw new InvalidDataException(
                    "Long-lived worker roster is missing.");

            var pairs = new Dictionary<string, string>(
                StringComparer.Ordinal);
            foreach (string line in File.ReadAllLines(fullPath, Encoding.UTF8))
            {
                int split = line.IndexOf('=');
                string key = split <= 0
                    ? null : line.Substring(0, split);
                if (split <= 0 || pairs.ContainsKey(key))
                    throw new InvalidDataException(
                        "Long-lived worker roster contains a malformed or duplicate key.");
                pairs.Add(key, line.Substring(split + 1));
            }

            string value;
            int expectedWorkers;
            if (!pairs.TryGetValue("roster_schema", out value)
                || !string.Equals(
                    value, WorkerRosterSchema,
                    StringComparison.Ordinal)
                || !pairs.TryGetValue("run_nonce", out value)
                || !string.Equals(
                    value, expectedNonce,
                    StringComparison.Ordinal)
                || !pairs.TryGetValue("expected_workers", out value)
                || !int.TryParse(
                    value, NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out expectedWorkers)
                || expectedWorkers <= 0 || expectedWorkers > 24
                || pairs.Count != 4 + expectedWorkers)
                throw new InvalidDataException(
                    "Long-lived worker roster schema, nonce, or count is invalid.");

            string encodedPath;
            string rosterWorkerPath;
            try
            {
                if (!pairs.TryGetValue(
                        "worker_path_b64", out encodedPath))
                    throw new InvalidDataException(
                        "Long-lived worker roster image is missing.");
                rosterWorkerPath = Path.GetFullPath(
                    Encoding.UTF8.GetString(
                        Convert.FromBase64String(encodedPath)));
            }
            catch (InvalidDataException) { throw; }
            catch (Exception ex)
            {
                throw new InvalidDataException(
                    "Long-lived worker roster image is invalid.", ex);
            }
            if (!string.Equals(
                    rosterWorkerPath,
                    Path.GetFullPath(expectedWorkerPath),
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    "Long-lived worker roster image does not match this trial.");

            var roster = new WorkerRoster();
            var seen = new HashSet<int>();
            try
            {
                for (int i = 0; i < expectedWorkers; i++)
                {
                    string identity;
                    string key = "worker_" + i.ToString(
                        "D2", CultureInfo.InvariantCulture);
                    if (!pairs.TryGetValue(key, out identity))
                        throw new InvalidDataException(
                            "Long-lived worker roster member is missing: " + key);
                    string[] parts = identity.Split('|');
                    int pid;
                    long creation;
                    if (parts.Length != 2
                        || !int.TryParse(
                            parts[0], NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out pid)
                        || !long.TryParse(
                            parts[1], NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out creation)
                        || pid <= 0 || creation <= 0
                        || !seen.Add(pid))
                        throw new InvalidDataException(
                            "Long-lived worker roster member identity is invalid.");

                    IntPtr handle = Native.OpenProcess(
                        Native.PROCESS_QUERY_LIMITED_INFORMATION
                            | Native.SYNCHRONIZE,
                        false, pid);
                    if (handle == IntPtr.Zero)
                        throw new InvalidDataException(
                            "Long-lived worker roster member cannot be opened.");
                    var worker = new WorkerIdentity
                    {
                        Pid = pid,
                        Creation = creation,
                        Handle = handle
                    };
                    roster.Workers.Add(worker);

                    long actualCreation;
                    long cpu;
                    ulong io;
                    int session;
                    string image = Native.ImagePath(handle);
                    if (!Native.QueryProcessSample(
                            handle, out actualCreation, out cpu, out io)
                        || actualCreation != creation
                        || !Native.TryGetLiveProcessSessionId(
                            handle, pid, out session)
                        || session != expectedSession
                        || string.IsNullOrEmpty(image)
                        || !string.Equals(
                            Path.GetFullPath(image),
                            rosterWorkerPath,
                            StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException(
                            "Long-lived worker roster member failed live identity validation.");
                }
                return roster;
            }
            catch
            {
                roster.Dispose();
                throw;
            }
        }

        private static int CountCoveredWorkers(
            SuppressionCore core, WorkerRoster roster)
        {
            if (core == null || roster == null) return 0;
            int covered = 0;
            foreach (WorkerIdentity worker in roster.Workers)
                if (WorkerIsCovered(core, worker)) covered++;
            return covered;
        }

        private static bool WorkerIsCovered(
            SuppressionCore core, WorkerIdentity worker)
        {
            if (!WorkerIdentityIsLive(worker)) return false;
            if (!core.HasReason(
                    worker.Pid, SuppressReason.Background)
                || !core.IsThrottled(worker.Pid)
                || !core.HasReason(
                    worker.Pid, SuppressReason.Background))
                return false;
            return WorkerIdentityIsLive(worker);
        }

        private static bool WorkerIdentityIsLive(
            WorkerIdentity worker)
        {
            if (worker == null || worker.Handle == IntPtr.Zero)
                return false;
            long creation;
            long cpu;
            ulong io;
            int ignoredSession;
            return Native.QueryProcessSample(
                    worker.Handle, out creation, out cpu, out io)
                && creation == worker.Creation
                && Native.TryGetLiveProcessSessionId(
                    worker.Handle, worker.Pid, out ignoredSession);
        }

        private static bool LogSyntheticEligibility(
            string rendererPath, string backgroundPath, int selfPid,
            bool aggressive)
        {
            Process[] all;
            try { all = Process.GetProcesses(); }
            catch { return false; }
            try
            {
                int session;
                try
                {
                    using (Process current = Process.GetCurrentProcess())
                        session = current.SessionId;
                }
                catch { return false; }
                var parents = new Dictionary<int, int>();
                var names = new Dictionary<int, string>();
                var paths = new Dictionary<int, string>();
                int rendererPid = 0;
                var backgroundPids = new List<int>();
                foreach (Process process in all)
                {
                    try
                    {
                        if (process.SessionId != session) continue;
                        int pid = process.Id;
                        string name = process.ProcessName;
                        names[pid] = name;
                        IntPtr handle = Native.OpenProcess(
                            Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                        if (handle != IntPtr.Zero)
                        {
                            try
                            {
                                parents[pid] = Native.ParentProcessId(handle);
                                paths[pid] = Native.ImagePath(handle);
                            }
                            finally { Native.CloseHandle(handle); }
                        }
                        if (SameProcessName(name, rendererPath)) rendererPid = pid;
                        if (SameProcessName(name, backgroundPath)) backgroundPids.Add(pid);
                    }
                    catch { }
                }
                if (rendererPid <= 0 || backgroundPids.Count == 0) return false;

                int foreground = GameSessionDetector.ForegroundPid();
                HashSet<int> roots = GameSessionDetector.VisibleWindowPids(true);
                if (foreground > 0) roots.Add(foreground);
                HashSet<int> userFacing = GameMode.ExpandUserFacingFamily(
                    parents, names, roots);
                string gameRoot = GameScan.InferGameRoot(rendererPath);
                string windows = Environment.GetFolderPath(
                    Environment.SpecialFolder.Windows);
                foreach (int pid in backgroundPids)
                {
                    string path;
                    string name;
                    paths.TryGetValue(pid, out path);
                    names.TryGetValue(pid, out name);
                    bool basic = GameMode.BasicBackgroundEligible(
                        pid, selfPid, name, path, session, session, foreground,
                        userFacing.Contains(pid), windows, false, gameRoot, aggressive);
                    int parent;
                    parents.TryGetValue(pid, out parent);
                    Logger.Log("PerfLab资格诊断：pid=" + pid
                        + " parent=" + parent
                        + " path=" + (path ?? "<null>")
                        + " foreground=" + (pid == foreground)
                        + " userFacing=" + userFacing.Contains(pid)
                        + " underGameRoot=" + GameMode.UnderRoot(path, gameRoot)
                        + " antiCheat=" + GameSessionDetector.IsAntiCheatLikeName(name)
                        + " aggressive=" + aggressive
                        + " basicEligible=" + basic);
                }
                return true;
            }
            finally
            {
                foreach (Process process in all) process.Dispose();
            }
        }

        private static void WriteReport(
            string path, string lane, string runNonce,
            bool success, double cpu,
            double measurementElapsedMs,
            int measurementExpectedSamples,
            double measurementSampleDensityPercent,
            int suppressedMin, int suppressedMax, int suppressedLast,
            int suppressedPositiveSamples, int suppressedSamples,
            int expectedWorkers, int minCoveredWorkers,
            int lastCoveredWorkers, int fullCoverageSamples,
            int coverageSamples,
            long applyOperations,
            long gameModeScans, double workingSetMb,
            double privateMb, int maxHandles, int maxThreads,
            ulong readBytes, ulong writeBytes)
        {
            string[] lines =
            {
                "report_schema=" + ReportSchema,
                "lane=" + lane,
                "run_nonce=" + runNonce,
                "success=" + (success ? "1" : "0"),
                "cpu_percent=" + cpu.ToString("F4", CultureInfo.InvariantCulture),
                "measurement_elapsed_ms="
                    + measurementElapsedMs.ToString(
                        "F4", CultureInfo.InvariantCulture),
                "measurement_expected_samples="
                    + measurementExpectedSamples.ToString(
                        CultureInfo.InvariantCulture),
                "measurement_sample_density_percent="
                    + measurementSampleDensityPercent.ToString(
                        "F4", CultureInfo.InvariantCulture),
                "measurement_suppressed_min=" + suppressedMin.ToString(CultureInfo.InvariantCulture),
                "measurement_suppressed_max=" + suppressedMax.ToString(CultureInfo.InvariantCulture),
                "measurement_suppressed_last=" + suppressedLast.ToString(CultureInfo.InvariantCulture),
                "measurement_suppressed_positive_samples=" + suppressedPositiveSamples.ToString(CultureInfo.InvariantCulture),
                "measurement_suppressed_samples=" + suppressedSamples.ToString(CultureInfo.InvariantCulture),
                "measurement_expected_workers=" + expectedWorkers.ToString(CultureInfo.InvariantCulture),
                "measurement_min_covered_workers=" + minCoveredWorkers.ToString(CultureInfo.InvariantCulture),
                "measurement_last_covered_workers=" + lastCoveredWorkers.ToString(CultureInfo.InvariantCulture),
                "measurement_full_coverage_samples=" + fullCoverageSamples.ToString(CultureInfo.InvariantCulture),
                "measurement_coverage_samples=" + coverageSamples.ToString(CultureInfo.InvariantCulture),
                "apply_operations=" + applyOperations.ToString(CultureInfo.InvariantCulture),
                "game_mode_scans=" + gameModeScans.ToString(CultureInfo.InvariantCulture),
                "working_set_mb=" + workingSetMb.ToString("F4", CultureInfo.InvariantCulture),
                "private_mb=" + privateMb.ToString("F4", CultureInfo.InvariantCulture),
                "max_handles=" + maxHandles.ToString(CultureInfo.InvariantCulture),
                "max_threads=" + maxThreads.ToString(CultureInfo.InvariantCulture),
                "read_kb=" + (readBytes / 1024.0).ToString("F4", CultureInfo.InvariantCulture),
                "write_kb=" + (writeBytes / 1024.0).ToString("F4", CultureInfo.InvariantCulture)
            };
            string temporary = path + "."
                + Process.GetCurrentProcess().Id.ToString(
                    CultureInfo.InvariantCulture) + ".tmp";
            File.WriteAllLines(temporary, lines);
            File.Move(temporary, path);
        }

        private static ulong SafeDelta(ulong current, ulong started)
        {
            return current >= started ? current - started : 0;
        }

        private static double ElapsedMilliseconds(long started, long ended)
        {
            long ticks = ended - started;
            return ticks <= 0 ? 0
                : ticks * 1000.0 / Stopwatch.Frequency;
        }

        private static int ExpectedMeasurementSamples(double elapsedMs)
        {
            if (double.IsNaN(elapsedMs) || double.IsInfinity(elapsedMs)
                || elapsedMs <= 0)
                return 0;

            double expected =
                Math.Floor(elapsedMs / DiscoverIntervalMs) + 1;
            return expected >= int.MaxValue
                ? int.MaxValue : (int)expected;
        }

        private static double MeasurementDensityPercent(
            int actualSamples, int expectedSamples)
        {
            return actualSamples <= 0 || expectedSamples <= 0
                ? 0 : actualSamples * 100.0 / expectedSamples;
        }

        private static bool MeasurementDensityValid(
            int actualSamples, int expectedSamples)
        {
            return actualSamples > 0 && expectedSamples > 0
                && actualSamples <= expectedSamples + 1
                && (long)actualSamples * 100
                    >= (long)expectedSamples
                        * RequiredMeasurementDensityPercent;
        }

        private static void CaptureSuppressionObservation(
            SuppressionCore core,
            bool countSample,
            ref int minimum, ref int maximum, ref int last,
            ref int positiveSamples, ref int samples)
        {
            int suppressed = core.CountThrottled(
                SuppressReason.Background);
            last = suppressed;
            if (suppressed < minimum) minimum = suppressed;
            if (suppressed > maximum) maximum = suppressed;
            if (!countSample) return;
            samples++;
            if (suppressed > 0) positiveSamples++;
        }

        private static void CaptureWorkerCoverageObservation(
            int coveredWorkers, int expectedWorkers, bool countSample,
            ref int minimum, ref int last,
            ref int fullCoverageSamples, ref int coverageSamples)
        {
            last = coveredWorkers;
            if (coveredWorkers < minimum) minimum = coveredWorkers;
            if (!countSample) return;
            coverageSamples++;
            if (coveredWorkers == expectedWorkers)
                fullCoverageSamples++;
        }

        private static bool WorkerCoverageValid(
            int expectedWorkers, int lastCoveredWorkers,
            int fullCoverageSamples, int totalSamples)
        {
            if (expectedWorkers <= 0 || totalSamples <= 0
                || lastCoveredWorkers != expectedWorkers)
                return false;
            return (long)fullCoverageSamples * 100
                    >= (long)totalSamples * RequiredPolicyCoveragePercent;
        }
    }
}
