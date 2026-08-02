// @author bdth 2074055628@qq.com
// 文件用途：生成合成渲染/后台负载并对 Pavise 核心执行可复现 A/B 性能测量

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;

namespace PavisePerfLab
{
    internal static class Program
    {
        private const string OutputOwnerFileName = ".pavise-perflab-owner";
        private const string OutputOwnerSignature = "PAVISE_PERFLAB_OUTPUT_V1";
        private const string EngineReportSchema = "pavise-perflab-engine-v4";
        private const string WorkerRosterSchema =
            "pavise-perflab-worker-roster-v1";
        private const int RequiredPolicyCoveragePercent = 90;
        private const int RequiredMeasurementDensityPercent = 80;
        private const int MeasurementSampleIntervalMs = 1000;
        private static readonly string[] EngineReportKeys =
        {
            "report_schema",
            "lane",
            "run_nonce",
            "success",
            "cpu_percent",
            "measurement_elapsed_ms",
            "measurement_expected_samples",
            "measurement_sample_density_percent",
            "measurement_suppressed_min",
            "measurement_suppressed_max",
            "measurement_suppressed_last",
            "measurement_suppressed_positive_samples",
            "measurement_suppressed_samples",
            "measurement_expected_workers",
            "measurement_min_covered_workers",
            "measurement_last_covered_workers",
            "measurement_full_coverage_samples",
            "measurement_coverage_samples",
            "apply_operations",
            "game_mode_scans",
            "working_set_mb",
            "private_mb",
            "max_handles",
            "max_threads",
            "read_kb",
            "write_kb"
        };

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessTimes(
            IntPtr process, out long creation, out long exit,
            out long kernel, out long user);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageName(
            IntPtr process, int flags, StringBuilder buffer, ref int size);

        // 呈现模式必须整轮统一。DwmFlush 的等待中位数在高刷新率屏幕上
        // 贴着校准阈值（240Hz 期望约 2.1ms，阈值 3.0ms），后台争抢一变
        // 判定就翻面；而 active 臂的负载被压制、baseline 臂没有，于是
        // 被测变量透过争抢反过来决定了量具模式，两种模式本身又差约 11%
        // 帧时间。首个 trial 校准一次，其余全部沿用，杜绝这条混杂路径。
        internal const string PresentationAuto = "auto";
        internal const string PresentationDwm = "dwm_flush";
        internal const string PresentationGdi = "gdi_timer";

        // Process.MainModule 依赖模块列表可枚举，进程刚起来或正在退出时
        // 会返回 FileName 为 null 的模块并在取值时抛 NullReferenceException。
        // 走 QueryFullProcessImageName 只需要句柄本身，不受模块枚举时机影响。
        private static string LiveImagePath(Process process)
        {
            try
            {
                var buffer = new StringBuilder(1024);
                int size = buffer.Capacity;
                if (QueryFullProcessImageName(process.Handle, 0, buffer, ref size)
                    && size > 0)
                    return buffer.ToString(0, size);
            }
            catch { }
            return null;
        }

        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                if (args.Length > 0 && args[0] == "--renderer")
                    return RunRenderer(args);
                if (args.Length > 0 && args[0] == "--background")
                    return RunBackground(args);
                if (args.Length > 0 && args[0] == "--burst")
                    return RunBurst(args);
                if (args.Length > 0 && args[0] == "--self-test")
                    return RunSelfTest();
                return RunController(args);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                try
                {
                    if (args != null && args.Length >= 4
                        && string.Equals(args[0], "--renderer",
                            StringComparison.OrdinalIgnoreCase))
                        File.WriteAllText(
                            Path.GetFullPath(args[3]) + ".error.txt",
                            ex.ToString(), new UTF8Encoding(false));
                    string output = ArgumentValue(args, "--out");
                    if (!string.IsNullOrEmpty(output))
                    {
                        output = Path.GetFullPath(output);
                        if (IsOwnedOutputDirectory(output))
                            File.WriteAllText(
                                Path.Combine(output, "PerfLab.error.txt"),
                                ex.ToString(), new UTF8Encoding(false));
                    }
                }
                catch { }
                return 1;
            }
        }

        private static string ArgumentValue(string[] args, string name)
        {
            if (args == null) return null;
            for (int i = 0; i + 1 < args.Length; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            return null;
        }

        private static int RunController(string[] args)
        {
            Options options = Options.Parse(args);
            if (!IsElevated())
                throw new InvalidOperationException(
                    "PerfLab must run elevated so the real boost/readback path can be verified.");
            PrepareOutputDirectory(options.OutputDirectory);
            string executable = Process.GetCurrentProcess().MainModule.FileName;

            string gameDir = Path.Combine(options.OutputDirectory, "render-target");
            string loadDir = Path.Combine(options.OutputDirectory, "background-load");
            Directory.CreateDirectory(gameDir);
            Directory.CreateDirectory(loadDir);

            string renderer = Path.Combine(gameDir, "Pavise.PerfLauncher.exe");
            string background = Path.Combine(loadDir, "Pavise.PerfBackground.exe");
            File.Copy(executable, renderer, true);
            File.Copy(executable, background, true);

            var rows = new List<TrialResult>();
            // 首个 trial 自行校准呈现模式，其余全部沿用它的结果
            string presentationLock = PresentationAuto;
            for (int round = 1; round <= options.Rounds; round++)
            {
                bool activeFirst = round % 2 == 0;
                for (int order = 0; order < 2; order++)
                {
                    bool active = order == 0 ? activeFirst : !activeFirst;
                    TrialResult result = RunTrial(
                        options, round, order + 1, active, renderer, background,
                        presentationLock);
                    rows.Add(result);
                    if (presentationLock == PresentationAuto
                        && !string.IsNullOrEmpty(result.PresentationMode))
                    {
                        presentationLock = result.PresentationMode;
                        Console.WriteLine(
                            "presentation_lock=" + presentationLock);
                    }
                    Console.WriteLine(result.ToConsoleLine());
                    Thread.Sleep(options.CooldownSeconds * 1000);
                }
            }

            string csv = Path.Combine(options.OutputDirectory, "trials.csv");
            WriteCsv(csv, rows);
            string summary = Path.Combine(options.OutputDirectory, "summary.txt");
            bool passed = WriteSummary(summary, rows, options);
            Console.WriteLine("RAW=" + csv);
            Console.WriteLine("SUMMARY=" + summary);
            return passed ? 0 : 3;
        }

        private static TrialResult RunTrial(
            Options options, int round, int order, bool active,
            string rendererPath, string backgroundPath,
            string presentationLock)
        {
            string tag = "r" + round.ToString("D2", CultureInfo.InvariantCulture)
                + "-" + (active ? "active" : "baseline");
            string frameReport = Path.Combine(options.OutputDirectory, tag + "-frames.txt");
            string engineReport = Path.Combine(options.OutputDirectory, tag + "-engine.txt");
            string workerRoster = Path.Combine(
                options.OutputDirectory, tag + "-workers.txt");
            DeleteIfExists(frameReport);
            DeleteIfExists(engineReport);
            DeleteIfExists(workerRoster);
            string reportNonce = Guid.NewGuid().ToString("N");

            Process engine = null;
            Process renderer = null;
            var workers = new List<Process>();
            var rendererStats = new ProcessSampler();
            string token = "PavisePerf_" + Process.GetCurrentProcess().Id.ToString(
                CultureInfo.InvariantCulture) + "_" + Guid.NewGuid().ToString("N");
            string armedName = "Global\\" + token + "_armed";
            string readyName = "Global\\" + token + "_ready";
            string startName = "Global\\" + token + "_start";
            string startAckName = "Global\\" + token + "_start_ack";
            string doneName = "Global\\" + token + "_done";
            string rosterReadyName = "Global\\" + token + "_roster_ready";
            using (var armedEvent = new EventWaitHandle(
                false, EventResetMode.ManualReset, armedName))
            using (var readyEvent = new EventWaitHandle(
                false, EventResetMode.ManualReset, readyName))
            using (var startEvent = new EventWaitHandle(
                false, EventResetMode.ManualReset, startName))
            using (var startAckEvent = new EventWaitHandle(
                false, EventResetMode.ManualReset, startAckName))
            using (var doneEvent = new EventWaitHandle(
                false, EventResetMode.ManualReset, doneName))
            using (var rosterReadyEvent = new EventWaitHandle(
                false, EventResetMode.ManualReset, rosterReadyName))
            {
                try
                {
                    if (active)
                    {
                        engine = Start(options.EnginePath,
                            Quote(rendererPath) + " " + Quote(backgroundPath) + " "
                            + (options.WarmupSeconds + options.Seconds + 60)
                                .ToString(CultureInfo.InvariantCulture) + " "
                            + Quote(engineReport) + " " + Quote(armedName) + " "
                            + Quote(readyName) + " " + Quote(startName) + " "
                            + Quote(startAckName) + " " + Quote(doneName) + " "
                            + Quote(workerRoster) + " "
                            + Quote(rosterReadyName) + " "
                            + options.Lane + " "
                            + reportNonce, true);
                        if (!armedEvent.WaitOne(15000))
                        {
                            if (engine.HasExited)
                                throw new InvalidOperationException(
                                    "Performance engine exited before arming.");
                            throw new TimeoutException(
                                "Performance engine did not arm its process observers.");
                        }
                    }

                    for (int i = 0; i < options.Workers; i++)
                        workers.Add(Start(backgroundPath,
                            "--background " + (options.Seconds + 2)
                                .ToString(CultureInfo.InvariantCulture)
                            + " " + (10 + i % 3).ToString(CultureInfo.InvariantCulture)
                            + " " + Quote(startAckName) + " "
                            + (options.WarmupSeconds + 20)
                                .ToString(CultureInfo.InvariantCulture), true));
                    if (active)
                    {
                        WriteWorkerRoster(
                            workerRoster, reportNonce,
                            backgroundPath, workers);
                        rosterReadyEvent.Set();
                    }

                    renderer = Start(rendererPath,
                        "--renderer " + options.Seconds.ToString(CultureInfo.InvariantCulture)
                        + " " + (options.WarmupSeconds + 20).ToString(CultureInfo.InvariantCulture)
                        + " " + Quote(frameReport) + " " + Quote(startAckName)
                        + " " + Quote(backgroundPath) + " " + Quote(doneName)
                        + " " + (presentationLock ?? PresentationAuto),
                        false);

                    Stopwatch warmup = Stopwatch.StartNew();
                    if (active && !readyEvent.WaitOne(
                        (options.WarmupSeconds + 15) * 1000))
                    {
                        if (engine.HasExited)
                            throw new InvalidOperationException(
                                "Performance engine exited before readiness.");
                        throw new TimeoutException(
                            "Performance engine did not verify boost and suppression.");
                    }
                    int remainingWarmup = options.WarmupSeconds * 1000
                        - (int)warmup.ElapsedMilliseconds;
                    if (remainingWarmup > 0) Thread.Sleep(remainingWarmup);
                    if (active)
                    {
                        startEvent.Set();
                        if (!startAckEvent.WaitOne(5000))
                        {
                            if (engine.HasExited)
                                throw new InvalidOperationException(
                                    "Performance engine exited before measurement ACK.");
                            throw new TimeoutException(
                                "Performance engine did not ACK the measurement boundary.");
                        }
                    }
                    else
                    {

                        startAckEvent.Set();
                    }

                    while (!renderer.WaitForExit(200))
                    {
                        rendererStats.Sample(renderer);
                    }
                    rendererStats.Sample(renderer);
                    doneEvent.Set();

                    WaitForAllAndDispose(
                        workers, 4000, "background worker");
                    if (engine != null) WaitOrStop(engine, 8000);

                    if (renderer.ExitCode != 0 || !File.Exists(frameReport))
                        throw new InvalidOperationException(
                            "Renderer failed with exit code " + renderer.ExitCode
                            + ". " + ReadOptional(frameReport + ".error.txt"));
                    FrameReport frames = FrameReport.Load(frameReport);
                    Dictionary<string, string> engineValues = LoadPairs(engineReport);
                    string engineError = "";
                    if (active && (engine.ExitCode != 0
                        || !ValidateEngineReport(
                            engineValues, options.Lane,
                            reportNonce, options.Workers,
                            out engineError)))
                        throw new InvalidOperationException(
                            "Performance engine report is invalid: "
                            + (engine.ExitCode != 0
                                ? "exit code " + engine.ExitCode.ToString(
                                    CultureInfo.InvariantCulture)
                                : engineError));
                    return new TrialResult
                    {
                        Round = round,
                        Order = order,
                        Lane = options.Lane,
                        Active = active,
                        Frames = frames.Frames,
                        ChildBurstsStarted = frames.ChildBurstsStarted,
                        PresentationMode = frames.PresentationMode,
                        AverageMs = frames.AverageMs,
                        P95Ms = frames.P95Ms,
                        P99Ms = frames.P99Ms,
                        MaxMs = frames.MaxMs,
                        OverBudget = frames.OverBudget,
                        RendererCpu = rendererStats.AverageCpu,
                        RendererWorkingSetMb = rendererStats.AverageWorkingSetMb,
                        EngineCpu = active ? PairDouble(engineValues, "cpu_percent") : 0,
                        MeasurementElapsedMs = PairDouble(
                            engineValues, "measurement_elapsed_ms"),
                        MeasurementExpectedSamples = PairInt(
                            engineValues, "measurement_expected_samples"),
                        MeasurementSampleDensityPercent = PairDouble(
                            engineValues,
                            "measurement_sample_density_percent"),
                        EngineWorkingSetMb = active ? PairDouble(engineValues, "working_set_mb") : 0,
                        EnginePrivateMb = active ? PairDouble(engineValues, "private_mb") : 0,
                        EngineMaxHandles = active ? PairInt(engineValues, "max_handles") : 0,
                        EngineMaxThreads = active ? PairInt(engineValues, "max_threads") : 0,
                        EngineReadKb = active ? PairDouble(engineValues, "read_kb") : 0,
                        EngineWriteKb = active ? PairDouble(engineValues, "write_kb") : 0,
                        SuppressedMin = PairInt(
                            engineValues, "measurement_suppressed_min"),
                        SuppressedMax = PairInt(
                            engineValues, "measurement_suppressed_max"),
                        SuppressedLast = PairInt(
                            engineValues, "measurement_suppressed_last"),
                        SuppressedPositiveSamples = PairInt(
                            engineValues,
                            "measurement_suppressed_positive_samples"),
                        SuppressedSamples = PairInt(
                            engineValues, "measurement_suppressed_samples"),
                        ExpectedWorkers = PairInt(
                            engineValues, "measurement_expected_workers"),
                        MinCoveredWorkers = PairInt(
                            engineValues,
                            "measurement_min_covered_workers"),
                        LastCoveredWorkers = PairInt(
                            engineValues,
                            "measurement_last_covered_workers"),
                        FullCoverageSamples = PairInt(
                            engineValues,
                            "measurement_full_coverage_samples"),
                        CoverageSamples = PairInt(
                            engineValues,
                            "measurement_coverage_samples"),
                        ApplyOperations = PairLong(engineValues, "apply_operations"),
                        EngineGameModeScans = PairLong(
                            engineValues, "game_mode_scans"),
                        Over833 = frames.Over833,
                        Over1250 = frames.Over1250,
                        Over2500 = frames.Over2500,
                        Over3333 = frames.Over3333,
                        Over5000 = frames.Over5000,
                        Over6667 = frames.Over6667,
                        PresentValid = frames.PresentValid
                    };
                }
                finally
                {
                    doneEvent.Set();
                    if (renderer != null) StopAndDispose(renderer);
                    if (engine != null) StopAndDispose(engine);
                    foreach (Process process in workers) StopAndDispose(process);
                }
            }
        }

        private static int RunRenderer(string[] args)
        {
            if (args.Length != 7 && args.Length != 8) return 2;
            int seconds;
            int warmupLimit;
            if (!int.TryParse(args[1], out seconds)
                || !int.TryParse(args[2], out warmupLimit)
                || seconds < 1 || warmupLimit < 1)
                return 2;
            string forcedMode = args.Length == 8 ? args[7] : PresentationAuto;
            if (forcedMode != PresentationAuto
                && forcedMode != PresentationDwm
                && forcedMode != PresentationGdi)
                return 2;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (var form = new RenderForm(seconds, warmupLimit,
                Path.GetFullPath(args[3]), args[4],
                Path.GetFullPath(args[5]), args[6], forcedMode))
                Application.Run(form);
            return 0;
        }

        private static int RunBackground(string[] args)
        {
            if (args.Length != 5) return 2;
            int seconds;
            int duty;
            if (!int.TryParse(args[1], out seconds) || !int.TryParse(args[2], out duty)
                || seconds < 1 || duty < 1 || duty > 80)
                return 2;
            int warmupLimit;
            if (!int.TryParse(args[4], out warmupLimit) || warmupLimit < 1) return 2;
            using (EventWaitHandle gate = EventWaitHandle.OpenExisting(args[3]))
                if (!gate.WaitOne(warmupLimit * 1000)) return 3;
            byte[] memory = new byte[8 * 1024 * 1024];
            for (int i = 0; i < memory.Length; i += 4096) memory[i] = (byte)i;
            Stopwatch until = Stopwatch.StartNew();
            long checksum = 0;
            while (until.Elapsed.TotalSeconds < seconds)
            {
                Stopwatch slice = Stopwatch.StartNew();
                while (slice.ElapsedMilliseconds < duty)
                    checksum = unchecked(checksum * 33 + Environment.TickCount);
                Thread.Sleep(Math.Max(1, 100 - duty));
            }
            GC.KeepAlive(memory);
            return checksum == long.MinValue ? 4 : 0;
        }

        private static int RunBurst(string[] args)
        {
            int milliseconds;
            if (args.Length != 2 || !int.TryParse(args[1], out milliseconds)
                || milliseconds < 50 || milliseconds > 5000)
                return 2;
            Stopwatch until = Stopwatch.StartNew();
            long value = 17;
            while (until.ElapsedMilliseconds < milliseconds)
                value = unchecked(value * 1103515245 + 12345);
            return value == long.MinValue ? 4 : 0;
        }

        private static Process Start(string executable, string arguments, bool hidden)
        {
            var info = new ProcessStartInfo(executable, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = hidden,
                WindowStyle = hidden ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal
            };
            Process process = Process.Start(info);
            if (process == null) throw new InvalidOperationException("Failed to start " + executable);
            return process;
        }

        private static void WaitOrStop(Process process, int milliseconds)
        {
            if (process == null || process.HasExited) return;
            if (!process.WaitForExit(milliseconds))
            {
                process.Kill();
                process.WaitForExit(3000);
            }
        }

        private static void WaitForSuccess(
            Process process, int milliseconds, string role)
        {
            if (process == null)
                throw new InvalidOperationException(role + " was not created.");
            if (!process.HasExited && !process.WaitForExit(milliseconds))
            {
                process.Kill();
                process.WaitForExit(3000);
                throw new TimeoutException(role + " did not exit in time.");
            }
            if (process.ExitCode != 0)
                throw new InvalidOperationException(
                    role + " failed with exit code " + process.ExitCode + ".");
        }

        private static void ReapExited(List<Process> processes, string role)
        {
            for (int i = processes.Count - 1; i >= 0; i--)
            {
                Process process = processes[i];
                int exitCode;
                try
                {
                    if (!process.HasExited) continue;
                    exitCode = process.ExitCode;
                }
                catch (Exception error)
                {
                    processes.RemoveAt(i);
                    try { process.Dispose(); } catch { }
                    throw new InvalidOperationException(
                        role + " exit status could not be read.", error);
                }

                processes.RemoveAt(i);
                process.Dispose();
                if (exitCode != 0)
                    throw new InvalidOperationException(
                        role + " failed with exit code " + exitCode + ".");
            }
        }

        private static void WaitForAllAndDispose(
            List<Process> processes, int milliseconds, string role)
        {
            while (processes.Count > 0)
            {
                int index = processes.Count - 1;
                Process process = processes[index];
                try
                {
                    WaitForSuccess(process, milliseconds, role);
                }
                finally
                {
                    processes.RemoveAt(index);
                    StopAndDispose(process);
                }
            }
        }

        private static void StopAndDispose(Process process)
        {
            if (process == null) return;
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                    process.WaitForExit(3000);
                }
            }
            catch { }
            process.Dispose();
        }

        private static void WriteWorkerRoster(
            string path, string runNonce, string workerPath,
            IList<Process> workers)
        {
            if (string.IsNullOrWhiteSpace(runNonce)
                || workers == null || workers.Count <= 0)
                throw new InvalidOperationException(
                    "Worker roster identity is incomplete.");

            string expectedPath = Path.GetFullPath(workerPath);
            var identities = new List<string>(workers.Count);
            var seen = new HashSet<int>();
            foreach (Process worker in workers)
            {
                if (worker == null || worker.HasExited
                    || !seen.Add(worker.Id))
                    throw new InvalidOperationException(
                        "A long-lived worker exited or reused a PID before roster publication.");

                string liveImage = LiveImagePath(worker);
                if (liveImage == null)
                    throw new InvalidOperationException(
                        "A long-lived worker image path could not be read.");
                string livePath = Path.GetFullPath(liveImage);
                if (!string.Equals(
                        livePath, expectedPath,
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        "A long-lived worker image does not match the private workload copy.");

                long creation;
                long exit;
                long kernel;
                long user;
                if (!GetProcessTimes(
                        worker.Handle, out creation, out exit,
                        out kernel, out user)
                    || creation <= 0 || worker.HasExited)
                    throw new InvalidOperationException(
                        "A long-lived worker creation identity could not be captured.");
                identities.Add(
                    worker.Id.ToString(CultureInfo.InvariantCulture)
                    + "|" + creation.ToString(CultureInfo.InvariantCulture));
            }

            var lines = new List<string>(4 + identities.Count)
            {
                "roster_schema=" + WorkerRosterSchema,
                "run_nonce=" + runNonce,
                "worker_path_b64=" + Convert.ToBase64String(
                    Encoding.UTF8.GetBytes(expectedPath)),
                "expected_workers=" + identities.Count.ToString(
                    CultureInfo.InvariantCulture)
            };
            for (int i = 0; i < identities.Count; i++)
                lines.Add(
                    "worker_" + i.ToString(
                        "D2", CultureInfo.InvariantCulture)
                    + "=" + identities[i]);

            string temporary = path + "."
                + Process.GetCurrentProcess().Id.ToString(
                    CultureInfo.InvariantCulture) + ".tmp";
            File.WriteAllLines(
                temporary, lines, new UTF8Encoding(false));
            File.Move(temporary, path);
        }

        private static void PrepareOutputDirectory(string path)
        {
            if (File.Exists(path))
                throw new InvalidOperationException(
                    "PerfLab output path names an existing file: " + path);
            if (!Directory.Exists(path)) Directory.CreateDirectory(path);
            if (IsOwnedOutputDirectory(path)) return;

            using (IEnumerator<string> entries =
                Directory.EnumerateFileSystemEntries(path).GetEnumerator())
                if (entries.MoveNext())
                    throw new InvalidOperationException(
                        "PerfLab refuses to use a non-empty output directory without "
                        + OutputOwnerFileName + ": " + path);

            string marker = Path.Combine(path, OutputOwnerFileName);
            using (var stream = new FileStream(
                marker, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(
                stream, new UTF8Encoding(false)))
                writer.WriteLine(OutputOwnerSignature);
        }

        private static bool IsOwnedOutputDirectory(string path)
        {
            try
            {
                string marker = Path.Combine(path, OutputOwnerFileName);
                return Directory.Exists(path)
                    && File.Exists(marker)
                    && string.Equals(
                        File.ReadAllText(marker, Encoding.UTF8).Trim(),
                        OutputOwnerSignature, StringComparison.Ordinal);
            }
            catch { return false; }
        }

        private static void DeleteIfExists(string path)
        {
            if (!File.Exists(path)) return;
            File.Delete(path);
            if (File.Exists(path))
                throw new IOException(
                    "Unable to remove stale report: " + path);
        }

        private static string ReadOptional(string path)
        {
            try { return File.Exists(path) ? File.ReadAllText(path) : ""; }
            catch { return ""; }
        }

        private static Dictionary<string, string> LoadPairs(string path)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(path)) return result;
            foreach (string line in File.ReadAllLines(path))
            {
                int split = line.IndexOf('=');
                if (split > 0) result[line.Substring(0, split)] = line.Substring(split + 1);
            }
            return result;
        }

        private static bool ValidateEngineReport(
            Dictionary<string, string> pairs, string expectedLane,
            string expectedNonce, int expectedWorkers,
            out string error)
        {
            error = "";
            string value;
            if (pairs == null)
            {
                error = "report is missing";
                return false;
            }
            if (pairs.Count != EngineReportKeys.Length)
            {
                error = "report has missing or unexpected keys";
                return false;
            }
            foreach (string key in EngineReportKeys)
            {
                if (!pairs.ContainsKey(key))
                {
                    error = "report key is missing: " + key;
                    return false;
                }
            }
            if (!pairs.TryGetValue("report_schema", out value)
                || !string.Equals(
                    value, EngineReportSchema, StringComparison.Ordinal))
            {
                error = "report_schema is missing or unsupported";
                return false;
            }
            if (!pairs.TryGetValue("lane", out value)
                || !string.Equals(
                    value, expectedLane, StringComparison.OrdinalIgnoreCase))
            {
                error = "lane does not match the requested lane";
                return false;
            }
            if (!pairs.TryGetValue("run_nonce", out value)
                || !string.Equals(
                    value, expectedNonce, StringComparison.Ordinal))
            {
                error = "run_nonce does not match this trial";
                return false;
            }

            int integer;
            int suppressedMin;
            int suppressedMax;
            int suppressedLast;
            int suppressedPositiveSamples;
            int suppressedSamples;
            int measurementExpectedSamples;
            int reportedExpectedWorkers;
            int minCoveredWorkers;
            int lastCoveredWorkers;
            int fullCoverageSamples;
            int coverageSamples;
            long whole;
            double number;
            double measurementElapsedMs;
            double measurementSampleDensityPercent;
            if (!TryPairInt(pairs, "success", out integer) || integer != 1)
            {
                error = "success is missing or false";
                return false;
            }
            if (!TryPairDouble(pairs, "cpu_percent", out number)
                || !ValidNonNegative(number))
            {
                error = "cpu_percent is missing or invalid";
                return false;
            }
            if (!TryPairDouble(
                    pairs, "measurement_elapsed_ms",
                    out measurementElapsedMs)
                || !ValidPositive(measurementElapsedMs)
                || !TryPairInt(
                    pairs, "measurement_expected_samples",
                    out measurementExpectedSamples)
                || measurementExpectedSamples <= 0
                || measurementExpectedSamples
                    != ExpectedMeasurementSamples(measurementElapsedMs)
                || !TryPairDouble(
                    pairs, "measurement_sample_density_percent",
                    out measurementSampleDensityPercent)
                || !ValidPositive(
                    measurementSampleDensityPercent))
            {
                error = "measurement timing or density metrics are missing or invalid";
                return false;
            }
            if (!TryPairInt(
                    pairs, "measurement_suppressed_min",
                    out suppressedMin)
                || suppressedMin < 0
                || !TryPairInt(
                    pairs, "measurement_suppressed_max",
                    out suppressedMax)
                || suppressedMax < 0
                || !TryPairInt(
                    pairs, "measurement_suppressed_last",
                    out suppressedLast)
                || suppressedLast < 0
                || !TryPairInt(
                    pairs, "measurement_suppressed_positive_samples",
                    out suppressedPositiveSamples)
                || suppressedPositiveSamples < 0
                || !TryPairInt(
                    pairs, "measurement_suppressed_samples",
                    out suppressedSamples)
                || suppressedSamples <= 0)
            {
                error = "measurement suppression metrics are missing or invalid";
                return false;
            }
            double calculatedDensity = MeasurementDensityPercent(
                suppressedSamples, measurementExpectedSamples);
            if (!MeasurementDensityValid(
                    suppressedSamples, measurementExpectedSamples)
                || Math.Abs(
                    measurementSampleDensityPercent
                        - calculatedDensity) > 0.01)
            {
                error = "measurement sample density is insufficient or inconsistent";
                return false;
            }
            if (suppressedMin > suppressedMax
                || suppressedLast < suppressedMin
                || suppressedLast > suppressedMax
                || suppressedPositiveSamples > suppressedSamples
                || (suppressedMin > 0
                    && suppressedPositiveSamples != suppressedSamples)
                || (suppressedMax > 0)
                    != (suppressedPositiveSamples > 0))
            {
                error = "measurement suppression metrics are inconsistent";
                return false;
            }
            if (expectedWorkers <= 0
                || !TryPairInt(
                    pairs, "measurement_expected_workers",
                    out reportedExpectedWorkers)
                || reportedExpectedWorkers != expectedWorkers
                || !TryPairInt(
                    pairs, "measurement_min_covered_workers",
                    out minCoveredWorkers)
                || minCoveredWorkers < 0
                || minCoveredWorkers > reportedExpectedWorkers
                || !TryPairInt(
                    pairs, "measurement_last_covered_workers",
                    out lastCoveredWorkers)
                || lastCoveredWorkers < 0
                || lastCoveredWorkers > reportedExpectedWorkers
                || !TryPairInt(
                    pairs, "measurement_full_coverage_samples",
                    out fullCoverageSamples)
                || fullCoverageSamples < 0
                || !TryPairInt(
                    pairs, "measurement_coverage_samples",
                    out coverageSamples)
                || coverageSamples <= 0
                || coverageSamples != suppressedSamples
                || fullCoverageSamples > coverageSamples)
            {
                error = "worker coverage metrics are missing or inconsistent";
                return false;
            }
            bool policyLane = string.Equals(
                expectedLane, "policy",
                StringComparison.OrdinalIgnoreCase);
            if (policyLane && !WorkerCoverageValid(
                    reportedExpectedWorkers,
                    lastCoveredWorkers,
                    fullCoverageSamples,
                    coverageSamples))
            {
                error = "policy did not cover the complete long-lived worker roster";
                return false;
            }
            if (!policyLane
                && (suppressedMin != 0 || suppressedMax != 0
                    || suppressedLast != 0
                    || suppressedPositiveSamples != 0
                    || minCoveredWorkers != 0
                    || lastCoveredWorkers != 0
                    || fullCoverageSamples != 0))
            {
                error = "overhead lane unexpectedly suppressed the workload";
                return false;
            }
            if (!TryPairLong(pairs, "apply_operations", out whole)
                || whole < 0)
            {
                error = "apply_operations is missing or invalid";
                return false;
            }
            if (!TryPairLong(pairs, "game_mode_scans", out whole)
                || whole < 0)
            {
                error = "game_mode_scans is missing or invalid";
                return false;
            }
            if (!TryPairDouble(pairs, "working_set_mb", out number)
                || !ValidPositive(number))
            {
                error = "working_set_mb is missing or invalid";
                return false;
            }
            if (!TryPairDouble(pairs, "private_mb", out number)
                || !ValidPositive(number))
            {
                error = "private_mb is missing or invalid";
                return false;
            }
            if (!TryPairInt(pairs, "max_handles", out integer)
                || integer <= 0)
            {
                error = "max_handles is missing or invalid";
                return false;
            }
            if (!TryPairInt(pairs, "max_threads", out integer)
                || integer <= 0)
            {
                error = "max_threads is missing or invalid";
                return false;
            }
            if (!TryPairDouble(pairs, "read_kb", out number)
                || !ValidNonNegative(number))
            {
                error = "read_kb is missing or invalid";
                return false;
            }
            if (!TryPairDouble(pairs, "write_kb", out number)
                || !ValidNonNegative(number))
            {
                error = "write_kb is missing or invalid";
                return false;
            }
            return true;
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

        private static bool WorkerCoverageValid(TrialResult result)
        {
            return result != null && WorkerCoverageValid(
                result.ExpectedWorkers,
                result.LastCoveredWorkers,
                result.FullCoverageSamples,
                result.CoverageSamples);
        }

        private static int ExpectedMeasurementSamples(double elapsedMs)
        {
            if (!ValidPositive(elapsedMs)) return 0;

            double expected = Math.Floor(
                elapsedMs / MeasurementSampleIntervalMs) + 1;
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

        private static bool ValidNonNegative(double value)
        {
            return !double.IsNaN(value)
                && !double.IsInfinity(value) && value >= 0;
        }

        private static bool TryPairInt(
            Dictionary<string, string> pairs, string key, out int parsed)
        {
            parsed = 0;
            string value;
            return pairs.TryGetValue(key, out value)
                && int.TryParse(
                    value, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out parsed);
        }

        private static bool TryPairLong(
            Dictionary<string, string> pairs, string key, out long parsed)
        {
            parsed = 0;
            string value;
            return pairs.TryGetValue(key, out value)
                && long.TryParse(
                    value, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out parsed);
        }

        private static bool TryPairDouble(
            Dictionary<string, string> pairs, string key, out double parsed)
        {
            parsed = 0;
            string value;
            return pairs.TryGetValue(key, out value)
                && double.TryParse(
                    value, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out parsed);
        }

        private static int PairInt(Dictionary<string, string> pairs, string key)
        {
            int parsed;
            return TryPairInt(pairs, key, out parsed) ? parsed : 0;
        }

        private static long PairLong(Dictionary<string, string> pairs, string key)
        {
            long parsed;
            return TryPairLong(pairs, key, out parsed) ? parsed : 0;
        }

        private static double PairDouble(Dictionary<string, string> pairs, string key)
        {
            double parsed;
            return TryPairDouble(pairs, key, out parsed) ? parsed : 0;
        }

        private static int RunSelfTest()
        {
            var valid = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase)
            {
                { "report_schema", EngineReportSchema },
                { "lane", "overhead" },
                { "run_nonce", "self-test-nonce" },
                { "success", "1" },
                { "cpu_percent", "0.0125" },
                { "measurement_elapsed_ms", "20000.0000" },
                { "measurement_expected_samples", "21" },
                { "measurement_sample_density_percent", "100.0000" },
                { "measurement_suppressed_min", "0" },
                { "measurement_suppressed_max", "0" },
                { "measurement_suppressed_last", "0" },
                { "measurement_suppressed_positive_samples", "0" },
                { "measurement_suppressed_samples", "21" },
                { "measurement_expected_workers", "6" },
                { "measurement_min_covered_workers", "0" },
                { "measurement_last_covered_workers", "0" },
                { "measurement_full_coverage_samples", "0" },
                { "measurement_coverage_samples", "21" },
                { "apply_operations", "0" },
                { "game_mode_scans", "1" },
                { "working_set_mb", "32.5" },
                { "private_mb", "20.0" },
                { "max_handles", "100" },
                { "max_threads", "8" },
                { "read_kb", "0" },
                { "write_kb", "1.5" }
            };
            string error;
            if (!ValidateEngineReport(
                    valid, "overhead", "self-test-nonce", 6, out error))
                throw new InvalidOperationException(
                    "valid engine report rejected: " + error);
            foreach (string key in valid.Keys.ToArray())
            {
                string saved = valid[key];
                valid.Remove(key);
                if (ValidateEngineReport(
                        valid, "overhead", "self-test-nonce", 6, out error))
                    throw new InvalidOperationException(
                        "missing report key was accepted: " + key);
                valid[key] = saved;
            }
            valid["game_mode_scans"] = "not-a-number";
            if (ValidateEngineReport(
                    valid, "overhead", "self-test-nonce", 6, out error))
                throw new InvalidOperationException(
                    "malformed game_mode_scans was accepted");
            valid["game_mode_scans"] = "1";
            valid["cpu_percent"] = "NaN";
            if (ValidateEngineReport(
                    valid, "overhead", "self-test-nonce", 6, out error))
                throw new InvalidOperationException(
                    "non-finite cpu_percent was accepted");
            valid["cpu_percent"] = "0.0125";
            valid["report_schema"] = "pavise-perflab-engine-v3";
            if (ValidateEngineReport(
                    valid, "overhead", "self-test-nonce", 6, out error))
                throw new InvalidOperationException(
                    "obsolete report schema was accepted");
            valid["report_schema"] = EngineReportSchema;
            if (ValidateEngineReport(
                    valid, "overhead", "wrong-nonce", 6, out error))
                throw new InvalidOperationException(
                    "wrong run_nonce was accepted");
            valid["unexpected"] = "1";
            if (ValidateEngineReport(
                    valid, "overhead", "self-test-nonce", 6, out error))
                throw new InvalidOperationException(
                    "unexpected report key was accepted");
            valid.Remove("unexpected");

            valid["measurement_suppressed_samples"] = "1";
            valid["measurement_coverage_samples"] = "1";
            valid["measurement_sample_density_percent"] = "4.7619";
            if (ValidateEngineReport(
                    valid, "overhead", "self-test-nonce", 6, out error))
                throw new InvalidOperationException(
                    "one sample was accepted as 20-second measurement coverage");
            valid["measurement_suppressed_samples"] = "21";
            valid["measurement_coverage_samples"] = "21";
            valid["measurement_sample_density_percent"] = "100.0000";
            valid["measurement_expected_samples"] = "20";
            if (ValidateEngineReport(
                    valid, "overhead", "self-test-nonce", 6, out error))
                throw new InvalidOperationException(
                    "expected sample count inconsistent with elapsed time was accepted");
            valid["measurement_expected_samples"] = "21";
            valid["measurement_sample_density_percent"] = "99.0000";
            if (ValidateEngineReport(
                    valid, "overhead", "self-test-nonce", 6, out error))
                throw new InvalidOperationException(
                    "reported sample density inconsistent with counts was accepted");
            valid["measurement_sample_density_percent"] = "100.0000";

            var policy = new Dictionary<string, string>(
                valid, StringComparer.OrdinalIgnoreCase);
            policy["lane"] = "policy";
            policy["measurement_suppressed_min"] = "1";
            policy["measurement_suppressed_max"] = "6";
            policy["measurement_suppressed_last"] = "6";
            policy["measurement_suppressed_positive_samples"] = "21";
            policy["measurement_min_covered_workers"] = "6";
            policy["measurement_last_covered_workers"] = "6";
            policy["measurement_full_coverage_samples"] = "21";
            if (!ValidateEngineReport(
                    policy, "policy", "self-test-nonce", 6, out error))
                throw new InvalidOperationException(
                    "sustained policy coverage was rejected: " + error);

            policy["measurement_suppressed_min"] = "0";
            policy["measurement_suppressed_last"] = "6";
            policy["measurement_suppressed_positive_samples"] = "19";
            policy["measurement_min_covered_workers"] = "5";
            policy["measurement_full_coverage_samples"] = "19";
            if (!ValidateEngineReport(
                    policy, "policy", "self-test-nonce", 6, out error))
                throw new InvalidOperationException(
                    "90-percent policy coverage was rejected: " + error);
            policy["measurement_full_coverage_samples"] = "18";
            if (ValidateEngineReport(
                    policy, "policy", "self-test-nonce", 6, out error))
                throw new InvalidOperationException(
                    "sub-threshold policy coverage was accepted");

            policy["measurement_suppressed_max"] = "0";
            policy["measurement_suppressed_last"] = "0";
            policy["measurement_suppressed_positive_samples"] = "0";
            policy["measurement_last_covered_workers"] = "0";
            policy["measurement_full_coverage_samples"] = "0";
            if (ValidateEngineReport(
                    policy, "policy", "self-test-nonce", 6, out error))
                throw new InvalidOperationException(
                    "warmup-only suppression followed by zero measurement "
                    + "coverage was accepted");

            policy["measurement_suppressed_min"] = "7";
            policy["measurement_suppressed_max"] = "7";
            policy["measurement_suppressed_last"] = "7";
            policy["measurement_suppressed_positive_samples"] = "21";
            policy["measurement_min_covered_workers"] = "5";
            policy["measurement_last_covered_workers"] = "5";
            policy["measurement_full_coverage_samples"] = "0";
            if (ValidateEngineReport(
                    policy, "policy", "self-test-nonce", 6, out error))
                throw new InvalidOperationException(
                    "five-of-six roster coverage plus a burst was accepted");
            Console.WriteLine(
                "PASS engine report schema and measurement coverage validation");
            return 0;
        }

        private static string Quote(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        private static void WriteCsv(string path, List<TrialResult> rows)
        {
            var lines = new List<string>
            {
                "round,order,lane,mode,frames,avg_ms,p95_ms,p99_ms,max_ms,"
                + "over_8_33,over_12_50,over_16_67,over_25_00,over_33_33,"
                + "over_50_00,over_66_67,"
                + "renderer_cpu,renderer_ws_mb,engine_cpu,"
                + "measurement_elapsed_ms,measurement_expected_samples,"
                + "measurement_sample_density_percent,engine_ws_mb,"
                + "engine_private_mb,engine_handles_max,engine_threads_max,engine_read_kb,"
                + "engine_write_kb,suppressed_min,suppressed_max,"
                + "suppressed_last,suppressed_positive_samples,"
                + "suppressed_samples,expected_workers,"
                + "min_covered_workers,last_covered_workers,"
                + "full_coverage_samples,coverage_samples,apply_operations,"
                + "engine_game_mode_scans,child_bursts_started,"
                + "presentation_mode,present_valid"
            };
            foreach (TrialResult row in rows) lines.Add(row.ToCsv());
            File.WriteAllLines(path, lines, new UTF8Encoding(false));
        }

        private static bool WriteSummary(
            string path, List<TrialResult> rows, Options options)
        {
            int requiredRounds = options.Rounds;
            List<TrialResult> baseline = rows.Where(x => !x.Active).ToList();
            List<TrialResult> active = rows.Where(x => x.Active).ToList();
            double baselineAvg = Median(baseline.Select(x => x.AverageMs));
            double activeAvg = Median(active.Select(x => x.AverageMs));
            double baselineP99 = Median(baseline.Select(x => x.P99Ms));
            double activeP99 = Median(active.Select(x => x.P99Ms));
            double engineCpu = Median(active.Select(x => x.EngineCpu));
            double engineWrite = Median(active.Select(x => x.EngineWriteKb));
            double engineMedianMeasurementElapsedMs = Median(
                active.Select(x => x.MeasurementElapsedMs));
            double minimumMeasurementDensity = active.Count == 0
                ? 0 : active.Min(
                    x => x.MeasurementSampleDensityPercent);
            long engineMaxGameModeScans = active.Count == 0
                ? long.MaxValue : active.Max(x => x.EngineGameModeScans);
            var pairedAvgDelta = new List<double>();
            var pairedP99Delta = new List<double>();
            var pairedLongFrameDelta = new List<double>();
            double longFrameThreshold = SelectLongFrameThreshold(baselineAvg);
            foreach (IGrouping<int, TrialResult> pair in rows.GroupBy(x => x.Round))
            {
                TrialResult b = pair.FirstOrDefault(x => !x.Active);
                TrialResult a = pair.FirstOrDefault(x => x.Active);
                if (a == null || b == null || b.AverageMs <= 0 || b.P99Ms <= 0) continue;
                pairedAvgDelta.Add((a.AverageMs - b.AverageMs) / b.AverageMs * 100.0);
                pairedP99Delta.Add((a.P99Ms - b.P99Ms) / b.P99Ms * 100.0);
                pairedLongFrameDelta.Add(
                    MissRate(LongFrameCount(a, longFrameThreshold), a.Frames)
                    - MissRate(LongFrameCount(b, longFrameThreshold), b.Frames));
            }
            double pairedMedianAvgDelta = Median(pairedAvgDelta);
            double pairedP90AvgDelta = Percentile(pairedAvgDelta, 0.90);
            double pairedMedianP99Delta = Median(pairedP99Delta);
            double pairedMedianLongFrameDelta = Median(pairedLongFrameDelta);
            double pairedMeanLongFrameDelta =
                pairedLongFrameDelta.Count == 0
                    ? double.MaxValue : pairedLongFrameDelta.Average();
            double pairedP90LongFrameDelta =
                Percentile(pairedLongFrameDelta, 0.90);
            int baselineLongFrames = baseline.Sum(
                x => LongFrameCount(x, longFrameThreshold));
            int activeLongFrames = active.Sum(
                x => LongFrameCount(x, longFrameThreshold));
            int baselineFrames = baseline.Sum(x => x.Frames);
            int activeFrames = active.Sum(x => x.Frames);
            double aggregateLongFrameDelta = MissRate(
                activeLongFrames, activeFrames)
                - MissRate(baselineLongFrames, baselineFrames);
            double pairedLongFrameUpper95 =
                PairedMeanUpper95(pairedLongFrameDelta);
            int avgRegressionsOverFive =
                pairedAvgDelta.Count(x => x > 5.0);
            double rendererDelta = pairedMedianAvgDelta;
            bool rendererPointOk = pairedMedianAvgDelta <= 2.00

                && avgRegressionsOverFive <= Math.Max(
                    2, (int)Math.Floor(requiredRounds * 0.20))
                && pairedMedianP99Delta <= 5.00
                && activeP99 <= baselineP99 * 1.05 + 0.25

                && pairedMeanLongFrameDelta <= 10.0;
            bool longFrameConclusive = pairedLongFrameUpper95 <= 10.0;
            bool rendererOk = rendererPointOk && longFrameConclusive;
            bool rendererInconclusive = rendererPointOk
                && !longFrameConclusive;
            long processScanBudget = (long)Math.Ceiling(
                options.Seconds / 20.0) + 1;
            bool engineOk = engineCpu <= 0.25
                && engineMaxGameModeScans <= processScanBudget;
            bool suppressionOk = options.Lane == "overhead"
                || active.All(WorkerCoverageValid);
            double minimumSuppressionCoverage = options.Lane == "overhead"
                || active.Count == 0
                    ? 0
                    : active.Min(x => x.CoverageSamples <= 0
                        ? 0
                        : x.FullCoverageSamples * 100.0
                            / x.CoverageSamples);
            int minimumCoveredWorkers = options.Lane == "overhead"
                || active.Count == 0
                    ? 0 : active.Min(x => x.MinCoveredWorkers);
            int minimumLastCoveredWorkers = options.Lane == "overhead"
                || active.Count == 0
                    ? 0 : active.Min(x => x.LastCoveredWorkers);
            string[] presentationModes = rows
                .Select(x => x.PresentationMode ?? "")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            bool presentOk = rows.All(x => x.PresentValid)
                && presentationModes.Length == 1
                && presentationModes[0].Length > 0;
            string presentationMode = presentationModes.Length == 1
                ? presentationModes[0] : "mixed";
            bool measurementDensityOk = active.Count > 0
                && active.All(x =>
                    ValidPositive(x.MeasurementElapsedMs)
                    && x.MeasurementExpectedSamples
                        == ExpectedMeasurementSamples(
                            x.MeasurementElapsedMs)
                    && MeasurementDensityValid(
                        x.SuppressedSamples,
                        x.MeasurementExpectedSamples)
                    && x.CoverageSamples == x.SuppressedSamples
                    && Math.Abs(
                        x.MeasurementSampleDensityPercent
                            - MeasurementDensityPercent(
                                x.SuppressedSamples,
                                x.MeasurementExpectedSamples)) <= 0.01);
            bool metricsOk = rows.All(x =>
                x.Frames >= options.Seconds * 20
                && x.ChildBurstsStarted >=
                    MinimumChildBursts(options.Seconds)
                && ValidPositive(x.AverageMs)
                && x.AverageMs >= 3.0 && x.AverageMs <= 40.0
                && ValidPositive(x.P95Ms)
                && ValidPositive(x.P99Ms)
                && ValidPositive(x.MaxMs))
                && pairedAvgDelta.Count == requiredRounds
                && pairedP99Delta.Count == requiredRounds
                && pairedLongFrameDelta.Count == requiredRounds
                && measurementDensityOk;
            bool durationComplete = options.Seconds >= 20;
            bool complete = requiredRounds >= 10 && durationComplete
                && baseline.Count == requiredRounds && active.Count == requiredRounds
                && rows.GroupBy(x => x.Round).All(x => x.Count() == 2);
            bool passed = complete && metricsOk && rendererOk
                && engineOk && suppressionOk && presentOk;
            bool overallInconclusive = rendererInconclusive
                && engineOk && suppressionOk;

            string[] lines =
            {
                "Pavise PerfLab",
                "lane=" + options.Lane,
                "rounds=" + baseline.Count.ToString(CultureInfo.InvariantCulture),
                "baseline_median_avg_ms=" + F(baselineAvg),
                "active_median_avg_ms=" + F(activeAvg),
                "renderer_avg_delta_percent=" + F(rendererDelta),
                "paired_p90_avg_delta_percent=" + F(pairedP90AvgDelta),
                "avg_regressions_over5_count="
                    + avgRegressionsOverFive.ToString(CultureInfo.InvariantCulture),
                "baseline_median_p99_ms=" + F(baselineP99),
                "active_median_p99_ms=" + F(activeP99),
                "paired_median_p99_delta_percent=" + F(pairedMedianP99Delta),
                "long_frame_threshold_ms="
                    + F(longFrameThreshold),
                "paired_median_long_frame_delta_per_1000="
                    + F(pairedMedianLongFrameDelta),
                "paired_mean_long_frame_delta_per_1000="
                    + F(pairedMeanLongFrameDelta),
                "paired_p90_long_frame_delta_per_1000="
                    + F(pairedP90LongFrameDelta),
                "aggregate_long_frame_delta_per_1000="
                    + F(aggregateLongFrameDelta),
                "paired_long_frame_upper95_per_1000="
                    + F(pairedLongFrameUpper95),
                "engine_median_cpu_percent=" + F(engineCpu),
                "engine_median_write_kb=" + F(engineWrite),
                "engine_median_measurement_elapsed_ms="
                    + F(engineMedianMeasurementElapsedMs),
                "engine_min_measurement_sample_density_percent="
                    + F(minimumMeasurementDensity),
                "engine_max_game_mode_scans="
                    + engineMaxGameModeScans.ToString(
                        CultureInfo.InvariantCulture),
                "engine_game_mode_scan_budget="
                    + processScanBudget.ToString(
                        CultureInfo.InvariantCulture),
                "policy_min_full_worker_coverage_percent="
                    + F(minimumSuppressionCoverage),
                "policy_min_covered_workers="
                    + minimumCoveredWorkers.ToString(
                        CultureInfo.InvariantCulture),
                "policy_min_last_covered_workers="
                    + minimumLastCoveredWorkers.ToString(
                        CultureInfo.InvariantCulture),
                "renderer_gate=" + (rendererOk ? "PASS"
                    : (rendererInconclusive ? "INCONCLUSIVE" : "FAIL")),
                "engine_gate=" + (engineOk ? "PASS" : "FAIL"),
                "suppression_gate=" + (suppressionOk ? "PASS" : "FAIL"),
                "presentation_mode=" + presentationMode,
                "present_gate=" + (presentOk ? "PASS" : "ENV_INVALID"),
                "measurement_density_gate="
                    + (measurementDensityOk ? "PASS" : "INVALID"),
                "metrics_gate=" + (metricsOk ? "PASS" : "INVALID"),
                "duration_gate=" + (durationComplete
                    ? "PASS" : "INSUFFICIENT"),
                "complete_gate=" + (complete ? "PASS" : "INSUFFICIENT"),
                "result=" + (passed ? "PASS"
                    : (!presentOk ? "ENV_INVALID"
                        : (!complete ? "INSUFFICIENT"
                            : (!metricsOk ? "INVALID"
                                : (overallInconclusive
                                    ? "INCONCLUSIVE" : "FAIL")))))
            };
            File.WriteAllLines(path, lines, new UTF8Encoding(false));
            return passed;
        }

        private static int MinimumChildBursts(int seconds)
        {
            return Math.Max(1, (seconds * 1000 - 1000) / 750);
        }

        private static double Median(IEnumerable<double> source)
        {
            double[] values = source.OrderBy(x => x).ToArray();
            if (values.Length == 0) return 0;
            int middle = values.Length / 2;
            return values.Length % 2 == 0
                ? (values[middle - 1] + values[middle]) / 2.0
                : values[middle];
        }

        private static double Percentile(IEnumerable<double> source, double percentile)
        {
            double[] values = source.OrderBy(x => x).ToArray();
            if (values.Length == 0) return 0;
            int index = (int)Math.Ceiling(values.Length * percentile) - 1;
            if (index < 0) index = 0;
            if (index >= values.Length) index = values.Length - 1;
            return values[index];
        }

        private static string F(double value)
        {
            return value.ToString("F4", CultureInfo.InvariantCulture);
        }

        private static double MissRate(int misses, int frames)
        {
            return frames <= 0 ? double.MaxValue : misses * 1000.0 / frames;
        }

        private static double SelectLongFrameThreshold(
            double baselineAverageMs)
        {
            double target = baselineAverageMs * 1.30;
            double[] thresholds =
            {
                8.33, 12.50, 16.67, 25.00, 33.33, 50.00, 66.67
            };
            foreach (double threshold in thresholds)
                if (threshold >= target
                    && threshold > baselineAverageMs + 0.50)
                    return threshold;
            return thresholds[thresholds.Length - 1];
        }

        private static int LongFrameCount(
            TrialResult row, double threshold)
        {
            if (row == null) return 0;
            if (threshold <= 8.34) return row.Over833;
            if (threshold <= 12.51) return row.Over1250;
            if (threshold <= 16.68) return row.OverBudget;
            if (threshold <= 25.01) return row.Over2500;
            if (threshold <= 33.34) return row.Over3333;
            if (threshold <= 50.01) return row.Over5000;
            return row.Over6667;
        }

        private static double PairedMeanUpper95(
            IList<double> pairedDifferences)
        {
            if (pairedDifferences == null || pairedDifferences.Count < 2)
                return double.MaxValue;
            double mean = pairedDifferences.Average();
            double squared = 0;
            foreach (double value in pairedDifferences)
            {
                double difference = value - mean;
                squared += difference * difference;
            }
            int degreesOfFreedom = pairedDifferences.Count - 1;
            double standardDeviation = Math.Sqrt(
                squared / degreesOfFreedom);
            return mean + StudentTOneSided95(degreesOfFreedom)
                * standardDeviation / Math.Sqrt(pairedDifferences.Count);
        }

        private static double StudentTOneSided95(int degreesOfFreedom)
        {
            if (degreesOfFreedom <= 0) return double.MaxValue;

            const double z = 1.64485362695147;
            double v = degreesOfFreedom;
            double z2 = z * z;
            double z3 = z2 * z;
            double z5 = z3 * z2;
            double z7 = z5 * z2;
            return z
                + (z3 + z) / (4.0 * v)
                + (5.0 * z5 + 16.0 * z3 + 3.0 * z)
                    / (96.0 * v * v)
                + (3.0 * z7 + 19.0 * z5 + 17.0 * z3 - 15.0 * z)
                    / (384.0 * v * v * v);
        }

        private static bool ValidPositive(double value)
        {
            return value > 0 && !double.IsNaN(value)
                && !double.IsInfinity(value);
        }

        private static bool IsElevated()
        {
            try
            {
                using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                    return new WindowsPrincipal(identity).IsInRole(
                        WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }
    }

    internal sealed class Options
    {
        public string EnginePath;
        public string OutputDirectory;
        public int Rounds = 10;
        public int Seconds = 20;
        public int Workers = 6;
        public int WarmupSeconds = 10;
        public int CooldownSeconds = 10;
        public string Lane = "overhead";

        public static Options Parse(string[] args)
        {
            var result = new Options();
            for (int i = 0; i < args.Length; i++)
            {
                string value = i + 1 < args.Length ? args[i + 1] : null;
                if (args[i] == "--engine" && value != null) { result.EnginePath = Path.GetFullPath(value); i++; }
                else if (args[i] == "--out" && value != null) { result.OutputDirectory = Path.GetFullPath(value); i++; }
                else if (args[i] == "--rounds" && value != null) { result.Rounds = int.Parse(value, CultureInfo.InvariantCulture); i++; }
                else if (args[i] == "--seconds" && value != null) { result.Seconds = int.Parse(value, CultureInfo.InvariantCulture); i++; }
                else if (args[i] == "--workers" && value != null) { result.Workers = int.Parse(value, CultureInfo.InvariantCulture); i++; }
                else if (args[i] == "--warmup" && value != null) { result.WarmupSeconds = int.Parse(value, CultureInfo.InvariantCulture); i++; }
                else if (args[i] == "--cooldown" && value != null) { result.CooldownSeconds = int.Parse(value, CultureInfo.InvariantCulture); i++; }
                else if (args[i] == "--lane" && value != null) { result.Lane = value.ToLowerInvariant(); i++; }
                else if (args[i] == "--run") { }
                else throw new ArgumentException("Unknown or incomplete argument: " + args[i]);
            }
            if (string.IsNullOrEmpty(result.EnginePath) || !File.Exists(result.EnginePath))
                throw new ArgumentException("--engine must name an existing Pavise.PerfEngine.exe");
            if (string.IsNullOrEmpty(result.OutputDirectory))
                result.OutputDirectory = Path.Combine(Path.GetTempPath(),
                    "Pavise-PerfLab-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
            if (result.Rounds < 1 || result.Rounds > 50
                || result.Seconds < 3 || result.Seconds > 120
                || result.Workers < 1 || result.Workers > 24
                || result.WarmupSeconds < 3 || result.WarmupSeconds > 60
                || result.CooldownSeconds < 0 || result.CooldownSeconds > 60
                || result.Lane != "overhead" && result.Lane != "policy")
                throw new ArgumentOutOfRangeException("rounds/seconds/workers");
            return result;
        }
    }

    internal sealed class RenderForm : Form
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct NativeMessage
        {
            public IntPtr HWnd;
            public uint Message;
            public UIntPtr WParam;
            public IntPtr LParam;
            public uint Time;
            public Point Point;
        }

        [DllImport("user32.dll")]
        private static extern bool PeekMessage(
            out NativeMessage message, IntPtr window, uint min, uint max, uint remove);

        [DllImport("dwmapi.dll")]
        private static extern int DwmFlush();

        private readonly string reportPath;
        private readonly string childBurstPath;
        private readonly EventWaitHandle gate;
        private readonly EventWaitHandle completionGate;
        private readonly Stopwatch lifetime = Stopwatch.StartNew();
        private readonly double seconds;
        private readonly double warmupLimit;
        private readonly List<double> intervals = new List<double>(16384);
        private readonly Bitmap frame = new Bitmap(960, 540);
        private readonly Graphics frameGraphics;
        private readonly SolidBrush[] palette;
        private readonly Pen arcPen;
        private readonly Font titleFont;
        private readonly SolidBrush titleBrush;
        private Graphics targetGraphics;
        private long previousFrame;
        private long nextFrame;
        private int frameIndex;
        private int childBurstsStarted;
        private long nextChildBurstMs;
        private bool measuring;
        private Stopwatch measurement;
        private readonly List<double> presentationCalibration =
            new List<double>(64);
        private Stopwatch calibrationClock = Stopwatch.StartNew();
        private bool presentationCalibrated;
        private bool presentValid = true;
        private bool useDwm = true;
        private bool completionSignaled;

        public RenderForm(
            int seconds, int warmupLimit, string reportPath,
            string gateName, string childBurstPath,
            string completionGateName, string forcedPresentationMode)
        {
            this.seconds = seconds;
            this.warmupLimit = warmupLimit;
            this.reportPath = reportPath;
            this.childBurstPath = childBurstPath;
            // 被指定模式时跳过校准，整轮各 trial 因此共用同一量具
            if (forcedPresentationMode == Program.PresentationGdi)
            {
                useDwm = false;
                presentValid = false;
                presentationCalibrated = true;
            }
            else if (forcedPresentationMode == Program.PresentationDwm)
            {
                useDwm = true;
                presentValid = true;
                presentationCalibrated = true;
            }
            try
            {
                gate = EventWaitHandle.OpenExisting(gateName);
                completionGate =
                    EventWaitHandle.OpenExisting(completionGateName);
            }
            catch (Exception ex)
            {
                if (gate != null) gate.Dispose();
                if (completionGate != null) completionGate.Dispose();
                throw new InvalidOperationException(
                    "Unable to open measurement events ["
                    + gateName + ", " + completionGateName + "]", ex);
            }
            Text = "Pavise PerfLab Renderer";
            ClientSize = new Size(960, 540);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            TopMost = true;
            frameGraphics = Graphics.FromImage(frame);
            frameGraphics.SmoothingMode =
                System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            palette = new SolidBrush[32];
            for (int i = 0; i < palette.Length; i++)
                palette[i] = new SolidBrush(Color.FromArgb(
                    80 + i * 4, 40 + i * 5, 110 + i * 3, 190 + i * 2));
            arcPen = new Pen(Color.FromArgb(70, 128, 220), 3);
            titleFont = new Font("Segoe UI", 22, FontStyle.Bold);
            titleBrush = new SolidBrush(Color.FromArgb(230, 235, 245));
            Application.Idle += OnIdle;
            FormClosed += delegate
            {
                Application.Idle -= OnIdle;
                SignalMeasurementDone();
            };
            Shown += delegate
            {
                targetGraphics = CreateGraphics();
                previousFrame = Stopwatch.GetTimestamp();
                nextFrame = previousFrame;
            };
        }

        private void OnIdle(object sender, EventArgs e)
        {
            NativeMessage message;
            while (!PeekMessage(out message, IntPtr.Zero, 0, 0, 0))
            {
                if (!measuring && gate.WaitOne(0))
                {

                    if (!presentationCalibrated)
                        FinishPresentationCalibration(true);
                    measuring = true;
                    measurement = Stopwatch.StartNew();
                    intervals.Clear();
                    frameIndex = 0;
                    nextChildBurstMs = 500;
                    previousFrame = Stopwatch.GetTimestamp();
                    nextFrame = previousFrame;
                }
                if (!measuring && lifetime.Elapsed.TotalSeconds >= warmupLimit)
                {
                    Close();
                    return;
                }
                if (measuring && measurement.Elapsed.TotalSeconds >= seconds)
                {

                    SignalMeasurementDone();
                    Finish();
                    Close();
                    return;
                }
                if (measuring
                    && measurement.ElapsedMilliseconds >= nextChildBurstMs)
                {
                    StartChildBurst();
                    nextChildBurstMs += 750;
                }

                long now = Stopwatch.GetTimestamp();
                if (now < nextFrame)
                {
                    double remainingMs = (nextFrame - now) * 1000.0 / Stopwatch.Frequency;
                    if (remainingMs > 1.5) Thread.Sleep(1);
                    else Thread.SpinWait(50);
                    continue;
                }

                if (measuring && previousFrame != 0)
                    intervals.Add((now - previousFrame) * 1000.0 / Stopwatch.Frequency);
                previousFrame = now;
                nextFrame += Stopwatch.Frequency / 120;
                if (now - nextFrame > Stopwatch.Frequency / 2) nextFrame = now;
                DrawFrame();
                frameIndex++;
            }
        }

        private void StartChildBurst()
        {
            try
            {
                using (Process child = Process.Start(new ProcessStartInfo
                {
                    FileName = childBurstPath,
                    Arguments = "--burst 450",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                }))
                {
                    if (child != null) childBurstsStarted++;
                }
            }
            catch { }
        }

        private void DrawFrame()
        {
            Graphics graphics = frameGraphics;
            graphics.Clear(Color.FromArgb(13, 17, 26));
            for (int i = 0; i < 180; i++)
            {
                double angle = frameIndex * 0.021 + i * 0.31;
                int x = 480 + (int)(Math.Sin(angle * 1.7) * (80 + i * 1.7));
                int y = 270 + (int)(Math.Cos(angle * 1.3) * (50 + i * 1.2));
                int size = 4 + i % 13;
                graphics.FillEllipse(palette[i % palette.Length], x, y, size, size);
            }
            graphics.DrawArc(arcPen, 230, 90, 500, 360, frameIndex % 360, 250);
            graphics.DrawString("PAVISE PERF LAB", titleFont, titleBrush, 24, 24);
            if (targetGraphics != null)
                targetGraphics.DrawImageUnscaled(frame, 0, 0);
            if (useDwm)
            {
                long before = Stopwatch.GetTimestamp();
                int result = -1;
                try { result = DwmFlush(); } catch { }
                double waitMs = (Stopwatch.GetTimestamp() - before)
                    * 1000.0 / Stopwatch.Frequency;
                if (!presentationCalibrated && !measuring)
                {
                    presentationCalibration.Add(waitMs);
                    if (result != 0 || waitMs > 250
                        || presentationCalibration.Count >= 30
                        || calibrationClock.ElapsedMilliseconds >= 3000)
                        FinishPresentationCalibration(result == 0);
                }
            }
        }

        private void Finish()
        {
            if (intervals.Count == 0) intervals.Add(0);
            double[] ordered = intervals.OrderBy(x => x).ToArray();
            double average = intervals.Average();
            double p95 = Percentile(ordered, 0.95);
            string presentationMode = useDwm && presentValid
                ? "dwm_flush" : "gdi_timer";
            bool deliveryValid = presentValid
                || presentationMode == "gdi_timer"
                    && intervals.Count >= seconds * 20
                    && average >= 3.0 && average <= 40.0
                    && p95 <= 100.0;
            int over833 = intervals.Count(x => x > 8.33);
            int over1250 = intervals.Count(x => x > 12.50);
            int over = intervals.Count(x => x > 16.67);
            int over2500 = intervals.Count(x => x > 25.00);
            int over3333 = intervals.Count(x => x > 33.33);
            int over5000 = intervals.Count(x => x > 50.00);
            int over6667 = intervals.Count(x => x > 66.67);
            string[] lines =
            {
                "frames=" + intervals.Count.ToString(CultureInfo.InvariantCulture),
                "avg_ms=" + average.ToString("F6", CultureInfo.InvariantCulture),
                "p95_ms=" + p95.ToString("F6", CultureInfo.InvariantCulture),
                "p99_ms=" + Percentile(ordered, 0.99).ToString("F6", CultureInfo.InvariantCulture),
                "max_ms=" + ordered[ordered.Length - 1].ToString("F6", CultureInfo.InvariantCulture),
                "over_8_33=" + over833.ToString(CultureInfo.InvariantCulture),
                "over_12_50=" + over1250.ToString(CultureInfo.InvariantCulture),
                "over_budget=" + over.ToString(CultureInfo.InvariantCulture),
                "over_25_00=" + over2500.ToString(CultureInfo.InvariantCulture),
                "over_33_33=" + over3333.ToString(CultureInfo.InvariantCulture),
                "over_50_00=" + over5000.ToString(CultureInfo.InvariantCulture),
                "over_66_67=" + over6667.ToString(CultureInfo.InvariantCulture),
                "child_bursts_started="
                    + childBurstsStarted.ToString(CultureInfo.InvariantCulture),
                "presentation_mode=" + presentationMode,
                "present_valid=" + (deliveryValid ? "1" : "0")
            };
            File.WriteAllLines(reportPath, lines, new UTF8Encoding(false));
        }

        private void SignalMeasurementDone()
        {
            if (completionSignaled) return;
            completionGate.Set();
            completionSignaled = true;
        }

        private static double Percentile(double[] ordered, double percentile)
        {
            int index = (int)Math.Ceiling(ordered.Length * percentile) - 1;
            if (index < 0) index = 0;
            if (index >= ordered.Length) index = ordered.Length - 1;
            return ordered[index];
        }

        private void FinishPresentationCalibration(bool callSucceeded)
        {
            presentationCalibrated = true;
            double[] ordered = presentationCalibration.OrderBy(x => x).ToArray();
            double median = ordered.Length == 0 ? double.MaxValue
                : Percentile(ordered, 0.50);
            double p95 = ordered.Length == 0 ? double.MaxValue
                : Percentile(ordered, 0.95);
            presentValid = callSucceeded && ordered.Length >= 5
                && median >= 3.0 && median <= 40.0 && p95 <= 100.0;
            if (!presentValid) useDwm = false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (targetGraphics != null) targetGraphics.Dispose();
                frameGraphics.Dispose();
                foreach (SolidBrush brush in palette) brush.Dispose();
                arcPen.Dispose();
                titleFont.Dispose();
                titleBrush.Dispose();
                gate.Dispose();
                completionGate.Dispose();
                frame.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    internal sealed class FrameReport
    {
        public int Frames;
        public double AverageMs;
        public double P95Ms;
        public double P99Ms;
        public double MaxMs;
        public int OverBudget;
        public int Over833;
        public int Over1250;
        public int Over2500;
        public int Over3333;
        public int Over5000;
        public int Over6667;
        public int ChildBurstsStarted;
        public string PresentationMode;
        public bool PresentValid;

        public static FrameReport Load(string path)
        {
            Dictionary<string, string> values = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);
            foreach (string line in File.ReadAllLines(path))
            {
                int split = line.IndexOf('=');
                if (split > 0) values[line.Substring(0, split)] = line.Substring(split + 1);
            }
            return new FrameReport
            {
                Frames = ParseInt(values, "frames"),
                AverageMs = ParseDouble(values, "avg_ms"),
                P95Ms = ParseDouble(values, "p95_ms"),
                P99Ms = ParseDouble(values, "p99_ms"),
                MaxMs = ParseDouble(values, "max_ms"),
                Over833 = ParseInt(values, "over_8_33"),
                Over1250 = ParseInt(values, "over_12_50"),
                OverBudget = ParseInt(values, "over_budget"),
                Over2500 = ParseInt(values, "over_25_00"),
                Over3333 = ParseInt(values, "over_33_33"),
                Over5000 = ParseInt(values, "over_50_00"),
                Over6667 = ParseInt(values, "over_66_67"),
                ChildBurstsStarted = ParseInt(
                    values, "child_bursts_started"),
                PresentationMode = ParseString(
                    values, "presentation_mode"),
                PresentValid = ParseInt(values, "present_valid") == 1
            };
        }

        private static int ParseInt(Dictionary<string, string> values, string key)
        {
            string value;
            int parsed;
            return values.TryGetValue(key, out value)
                && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)
                ? parsed : 0;
        }

        private static double ParseDouble(Dictionary<string, string> values, string key)
        {
            string value;
            double parsed;
            return values.TryGetValue(key, out value)
                && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)
                ? parsed : 0;
        }

        private static string ParseString(
            Dictionary<string, string> values, string key)
        {
            string value;
            return values.TryGetValue(key, out value)
                ? value : "";
        }
    }

    internal sealed class ProcessSampler
    {
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

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessIoCounters(IntPtr process, out IoCounters counters);

        private bool hasPrevious;
        private DateTime previousAt;
        private TimeSpan previousCpu;
        private ulong firstRead;
        private ulong firstWrite;
        private ulong lastRead;
        private ulong lastWrite;
        private double cpuSum;
        private double workingSetSum;
        private double privateSum;
        private int samples;

        public int MaxHandles { get; private set; }
        public int MaxThreads { get; private set; }
        public double AverageCpu { get { return samples == 0 ? 0 : cpuSum / samples; } }
        public double AverageWorkingSetMb { get { return samples == 0 ? 0 : workingSetSum / samples; } }
        public double AveragePrivateMb { get { return samples == 0 ? 0 : privateSum / samples; } }
        public double ReadKb { get { return (lastRead - firstRead) / 1024.0; } }
        public double WriteKb { get { return (lastWrite - firstWrite) / 1024.0; } }

        public void Sample(Process process)
        {
            try
            {
                process.Refresh();
                DateTime now = DateTime.UtcNow;
                TimeSpan cpu = process.TotalProcessorTime;
                IoCounters io;
                if (GetProcessIoCounters(process.Handle, out io))
                {
                    if (!hasPrevious)
                    {
                        firstRead = io.ReadTransferCount;
                        firstWrite = io.WriteTransferCount;
                    }
                    lastRead = io.ReadTransferCount;
                    lastWrite = io.WriteTransferCount;
                }

                if (hasPrevious)
                {
                    double elapsed = (now - previousAt).TotalSeconds;
                    if (elapsed > 0)
                    {
                        double percent = (cpu - previousCpu).TotalSeconds
                            / elapsed / Math.Max(1, Environment.ProcessorCount) * 100.0;
                        if (percent >= 0 && percent < 1000) cpuSum += percent;
                        workingSetSum += process.WorkingSet64 / 1048576.0;
                        privateSum += process.PrivateMemorySize64 / 1048576.0;
                        samples++;
                    }
                }
                previousAt = now;
                previousCpu = cpu;
                hasPrevious = true;
                if (process.HandleCount > MaxHandles) MaxHandles = process.HandleCount;
                int threads = process.Threads.Count;
                if (threads > MaxThreads) MaxThreads = threads;
            }
            catch { }
        }
    }

    internal sealed class TrialResult
    {
        public int Round;
        public int Order;
        public string Lane;
        public bool Active;
        public int Frames;
        public int ChildBurstsStarted;
        public string PresentationMode;
        public double AverageMs;
        public double P95Ms;
        public double P99Ms;
        public double MaxMs;
        public int OverBudget;
        public int Over833;
        public int Over1250;
        public int Over2500;
        public int Over3333;
        public int Over5000;
        public int Over6667;
        public bool PresentValid;
        public double RendererCpu;
        public double RendererWorkingSetMb;
        public double EngineCpu;
        public double MeasurementElapsedMs;
        public int MeasurementExpectedSamples;
        public double MeasurementSampleDensityPercent;
        public double EngineWorkingSetMb;
        public double EnginePrivateMb;
        public int EngineMaxHandles;
        public int EngineMaxThreads;
        public double EngineReadKb;
        public double EngineWriteKb;
        public int SuppressedMin;
        public int SuppressedMax;
        public int SuppressedLast;
        public int SuppressedPositiveSamples;
        public int SuppressedSamples;
        public int ExpectedWorkers;
        public int MinCoveredWorkers;
        public int LastCoveredWorkers;
        public int FullCoverageSamples;
        public int CoverageSamples;
        public long ApplyOperations;
        public long EngineGameModeScans;

        public string ToConsoleLine()
        {
            return "round=" + Round + " mode=" + (Active ? "active" : "baseline")
                + " avg=" + AverageMs.ToString("F3", CultureInfo.InvariantCulture)
                + "ms p99=" + P99Ms.ToString("F3", CultureInfo.InvariantCulture)
                + "ms engine=" + EngineCpu.ToString("F3", CultureInfo.InvariantCulture) + "%";
        }

        public string ToCsv()
        {
            return string.Join(",", new[]
            {
                Round.ToString(CultureInfo.InvariantCulture),
                Order.ToString(CultureInfo.InvariantCulture),
                Lane,
                Active ? "active" : "baseline",
                Frames.ToString(CultureInfo.InvariantCulture),
                F(AverageMs), F(P95Ms), F(P99Ms), F(MaxMs),
                Over833.ToString(CultureInfo.InvariantCulture),
                Over1250.ToString(CultureInfo.InvariantCulture),
                OverBudget.ToString(CultureInfo.InvariantCulture),
                Over2500.ToString(CultureInfo.InvariantCulture),
                Over3333.ToString(CultureInfo.InvariantCulture),
                Over5000.ToString(CultureInfo.InvariantCulture),
                Over6667.ToString(CultureInfo.InvariantCulture),
                F(RendererCpu), F(RendererWorkingSetMb), F(EngineCpu),
                F(MeasurementElapsedMs),
                MeasurementExpectedSamples.ToString(
                    CultureInfo.InvariantCulture),
                F(MeasurementSampleDensityPercent),
                F(EngineWorkingSetMb), F(EnginePrivateMb),
                EngineMaxHandles.ToString(CultureInfo.InvariantCulture),
                EngineMaxThreads.ToString(CultureInfo.InvariantCulture),
                F(EngineReadKb), F(EngineWriteKb),
                SuppressedMin.ToString(CultureInfo.InvariantCulture),
                SuppressedMax.ToString(CultureInfo.InvariantCulture),
                SuppressedLast.ToString(CultureInfo.InvariantCulture),
                SuppressedPositiveSamples.ToString(
                    CultureInfo.InvariantCulture),
                SuppressedSamples.ToString(CultureInfo.InvariantCulture),
                ExpectedWorkers.ToString(CultureInfo.InvariantCulture),
                MinCoveredWorkers.ToString(CultureInfo.InvariantCulture),
                LastCoveredWorkers.ToString(CultureInfo.InvariantCulture),
                FullCoverageSamples.ToString(CultureInfo.InvariantCulture),
                CoverageSamples.ToString(CultureInfo.InvariantCulture),
                ApplyOperations.ToString(CultureInfo.InvariantCulture),
                EngineGameModeScans.ToString(CultureInfo.InvariantCulture),
                ChildBurstsStarted.ToString(CultureInfo.InvariantCulture),
                PresentationMode ?? "",
                PresentValid ? "1" : "0"
            });
        }

        private static string F(double value)
        {
            return value.ToString("F4", CultureInfo.InvariantCulture);
        }
    }
}
