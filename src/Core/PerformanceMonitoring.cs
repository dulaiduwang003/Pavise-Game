using System;
using System.Diagnostics;
using System.Threading;

namespace PaviseApp
{
    internal interface IMonitoringBridge
    {
        bool GameActive { get; }
        bool TryReadTarget(out RtssFrameSample sample);
        void Publish(MonitoringSnapshot snapshot, MonitoringLayout layout);
        void Clear();
        void Close();
    }
    internal interface IMonitoringPreparationBridge
    {
        bool GamePreparing { get; }
        void Prepare();
    }
    internal interface IMonitoringStatusBridge { string Status { get; } }
    internal interface IMonitoringTimer : IDisposable { void Schedule(int milliseconds); }
    internal interface IMonitoringSensors : IDisposable
    {
        bool Read(RtssFrameSample target, MonitoringMetrics metrics, out MonitoringSensorReading reading);
        void ResetTarget();
    }
    internal sealed class MonitoringSensorReading
    {
        internal double? CpuUsage, GpuUsage, GpuTemperature, GpuClock, GpuPower, RamMiB, VramMiB,
            GameRamMiB, RamUsage, VramUsage,
            GpuMemoryClock, GpuFanRpm, GpuFanPercent, CpuPower, CpuTemperature;
        internal string Status = "";
        internal string GpuAdapterName = "";
        internal bool GpuSourceMatched;
        internal MonitoringMetrics RetainedHardwareMetrics;
    }

    internal static class PerformanceMonitoring
    {
        private sealed class RtssBridge : IMonitoringBridge, IMonitoringPreparationBridge, IMonitoringStatusBridge
        {
            public bool GameActive { get { return RtssOverlayClient.GameActive; } }
            public bool GamePreparing { get { return RtssOverlayClient.GamePreparing; } }
            public string Status { get { return RtssOverlayClient.Status; } }
            public void Prepare() { RtssOverlayClient.Prepare(); }
            public bool TryReadTarget(out RtssFrameSample sample) { return RtssOverlayClient.TryReadTarget(out sample); }
            public void Publish(MonitoringSnapshot snapshot, MonitoringLayout layout) { RtssOverlayClient.Publish(snapshot, layout); }
            public void Clear() { RtssOverlayClient.Clear(); }
            public void Close() { RtssOverlayClient.Close(); }
        }
        private sealed class SamplingTimer : IMonitoringTimer
        {
            private readonly Timer timer;
            internal SamplingTimer(Action callback) { timer = new Timer(delegate { callback(); }, null, Timeout.Infinite, Timeout.Infinite); }
            public void Schedule(int milliseconds) { timer.Change(milliseconds, Timeout.Infinite); }
            public void Dispose() { timer.Dispose(); }
        }
        private static readonly object gate = new object(), workerGate = new object();
        private static readonly MonitoringFrameWindow frames = new MonitoringFrameWindow();
        private static readonly IMonitoringBridge nativeBridge = new RtssBridge();
        private static IMonitoringTimer timer;
        private static object timerToken;
        private static IMonitoringSensors sensors;
        private static bool started;
        private static int generation, targetPid, sensorGeneration = -1;
        private static long targetCreation, nextSensorSample, nextSensorInitialization;
        internal const int FramePollingMs = 250;
        internal const int IdlePollingMs = 5000;
        internal const int StartupPollingMs = 200, StartupProbeWindowMs = 8000;
        internal const int SensorInitializationRetryMs = 30000;
        private static bool rtssWanted, wakePending;
        private static long startupProbeUntil;
        private static MonitoringSnapshot current = Empty(OffText());
        internal static MonitoringSnapshot Current { get { return Volatile.Read(ref current); } }
        internal static string Status
        {
            get
            {
                if (!MonitoringPreferences.Enabled) return OffText();
                if (!Volatile.Read(ref started)) return WaitingStartupText();
                if (!MonitoringPreferences.OverlayVisible)
                    return AutomaticPolicy.Text("监测已开启 · 悬浮栏已隐藏", "Monitoring enabled · overlay hidden");
                string detail = BridgeStatus();
                return detail.Length > 0 ? detail : Current.Status;
            }
        }
#if PAVISE_SELFTEST
        internal static IMonitoringBridge BridgeForTest;
        internal static Func<IMonitoringSensors> SensorsFactoryForTest;
        internal static Func<Action, IMonitoringTimer> TimerFactoryForTest;
        internal static Func<long> ClockForTest;
        internal static bool HasTimerForTest { get { lock (gate) return timer != null; } }
#endif
        private static IMonitoringBridge Bridge
        {
            get
            {
#if PAVISE_SELFTEST
                if (BridgeForTest == null) throw new InvalidOperationException("Monitoring tests require an isolated bridge.");
                return BridgeForTest;
#else
                return nativeBridge;
#endif
            }
        }
        private static string OffText() { return AutomaticPolicy.Text("性能监控未开启", "Monitoring is off"); }
        private static string WaitingStartupText()
        { return AutomaticPolicy.Text("监测已开启 · 等待后台服务启动", "Monitoring enabled · waiting for the background service"); }
        private static string BridgeStatus()
        {
            IMonitoringStatusBridge bridge = Bridge as IMonitoringStatusBridge;
            return bridge == null ? "" : bridge.Status ?? "";
        }
        private static MonitoringSnapshot Empty(string status) { return new MonitoringSnapshot(0, "", DateTime.UtcNow, status); }
        internal static void Start()
        {
            lock (gate)
            {
                if (started) return;
                started = true; MonitoringPreferences.Changed += ConfigurationChanged;
                MonitoringPreferences.AppearanceChanged += AppearanceChanged;
                MonitoringPreferences.VisibilityChanged += VisibilityChanged;
            }
            RefreshConfiguration();
        }
        private static void ConfigurationChanged(object sender, EventArgs args) { RefreshConfiguration(); }
        internal static void WakeForGameStart()
        {
            lock (gate)
            {
                if (!started || timer == null || !MonitoringPreferences.Enabled) return;
                wakePending = true;
                timer.Schedule(0);
            }
        }
        private static void AppearanceChanged(object sender, EventArgs args)
        {
            lock (gate)
            {
                if (!started || !MonitoringPreferences.Enabled) return;
                MonitoringSnapshot snapshot = Current;
                if (snapshot.Pid <= 4 || (DateTime.UtcNow - snapshot.TimeUtc).TotalMilliseconds > 2500) return;
                try { PublishBridgeLocked(snapshot); }
                catch { ClearBridgeLocked(false); }
            }
        }
        private static void VisibilityChanged(object sender, EventArgs args)
        {
            lock (gate)
            {
                if (!started || timer == null || !MonitoringPreferences.Enabled) return;
                if (!MonitoringPreferences.OverlayVisible) { ClearBridgeLocked(false); return; }
                MonitoringSnapshot snapshot = Current;
                if (snapshot.Pid > 4 && (DateTime.UtcNow - snapshot.TimeUtc).TotalMilliseconds <= 2500)
                    try { PublishBridgeLocked(snapshot); } catch { ClearBridgeLocked(false); }
                else timer.Schedule(0);
            }
        }
        private static void PublishBridgeLocked(MonitoringSnapshot snapshot)
        {
            if (MonitoringPreferences.OverlayVisible) Bridge.Publish(snapshot, MonitoringPreferences.Layout);
            else ClearBridgeLocked(false);
        }
        internal static void RefreshConfiguration()
        {
            lock (gate)
            {
                generation++;
                if (!started || !MonitoringPreferences.Enabled)
                {
                    StopTimerLocked();
                    Volatile.Write(ref current, Empty(MonitoringPreferences.Enabled ? WaitingStartupText() : OffText()));
                    ClearBridgeLocked(true);
                }
                else
                {
                    if (timer == null)
                    {
                        Volatile.Write(ref current, Empty(AutomaticPolicy.Text("监测已开启 · 正在准备", "Monitoring enabled · preparing")));
                        object token = new object(); timerToken = token;
#if PAVISE_SELFTEST
                        if (TimerFactoryForTest == null) throw new InvalidOperationException("Monitoring tests require an isolated timer.");
                        timer = TimerFactoryForTest(delegate { Tick(token); });
#else
                        timer = new SamplingTimer(delegate { Tick(token); });
#endif
                    }
                    timer.Schedule(0);
                }
            }
            ReleaseIdleSensors();
        }
        internal static void Stop()
        {
            lock (gate)
            {
                started = false; generation++;
                MonitoringPreferences.Changed -= ConfigurationChanged;
                MonitoringPreferences.AppearanceChanged -= AppearanceChanged;
                MonitoringPreferences.VisibilityChanged -= VisibilityChanged;
                StopTimerLocked();
                Volatile.Write(ref current, Empty(OffText()));
                ClearBridgeLocked(true);
            }
            ReleaseIdleSensors();
        }
        private static void StopTimerLocked()
        {
            rtssWanted = false; startupProbeUntil = 0; wakePending = false;
            timerToken = null;
            if (timer != null) { timer.Dispose(); timer = null; }
        }
        private static void ClearBridgeLocked(bool close)
        {
            try { Bridge.Clear(); } catch { }
            if (close) try { Bridge.Close(); } catch { }
        }
        private static void ReleaseIdleSensors()
        {
            if (!Monitor.TryEnter(workerGate)) return;
            try { lock (gate) { if (timer == null) DisposeSensors(); } }
            finally { Monitor.Exit(workerGate); }
        }
        private static void DisposeSensors()
        {
            if (sensors != null) { try { sensors.Dispose(); } catch { } sensors = null; }
            nextSensorInitialization = 0;
            ResetFrames();
        }
        private static void ResetFrames()
        { targetPid = 0; targetCreation = 0; nextSensorSample = 0; frames.Reset(); }
        private static long ClockMilliseconds()
        {
#if PAVISE_SELFTEST
            if (ClockForTest != null) return ClockForTest();
#endif
            return (long)(Stopwatch.GetTimestamp() * (1000.0 / Stopwatch.Frequency));
        }
        private static void Tick(object token)
        {
            if (!Monitor.TryEnter(workerGate)) return;
            long sampleStarted = ClockMilliseconds();
            try
            {
                int observedGeneration; MonitoringMetrics metrics;
                RtssFrameSample target; bool hasTarget, gameActive;
                lock (gate)
                {
                    if (!started || timer == null || token != timerToken || !MonitoringPreferences.Enabled) return;
                    observedGeneration = generation; metrics = MonitoringPreferences.Metrics;
                    wakePending = false;
                    // 连接和准入共用边界，避免停止后被排队回调重新启动。
                    gameActive = Bridge.GameActive;
                    IMonitoringPreparationBridge preparation = Bridge as IMonitoringPreparationBridge;
                    bool preparing = preparation != null && preparation.GamePreparing;
                    bool wanted = gameActive || preparing;
                    if (wanted && !rtssWanted) startupProbeUntil = sampleStarted + StartupProbeWindowMs;
                    rtssWanted = wanted;
                    if (wanted && preparation != null) preparation.Prepare();
                    if (gameActive) hasTarget = Bridge.TryReadTarget(out target);
                    else { target = null; hasTarget = false; ClearBridgeLocked(!preparing); }
                    if (!wanted || hasTarget) startupProbeUntil = 0;
                }
                if (sensorGeneration != observedGeneration)
                { DisposeSensors(); sensorGeneration = observedGeneration; }
                MonitoringSnapshot snapshot;
                if (!hasTarget || target == null || target.Pid <= 4 || target.Creation <= 0)
                {
                    ResetFrames();
                    if (!gameActive) DisposeSensors();
                    else if (sensors != null) sensors.ResetTarget();
                    string waiting = gameActive
                        ? AutomaticPolicy.Text("等待 Pavise 守护的游戏渲染进程与 RTSS", "Waiting for a Pavise-guarded game renderer and RTSS")
                        : AutomaticPolicy.Text("等待游戏对局，RTSS 按需启动", "Waiting for a game; RTSS starts on demand");
                    string bridgeStatus = BridgeStatus();
                    if (bridgeStatus.Length > 0) waiting = bridgeStatus;
                    snapshot = Empty(waiting);
                }
                else
                {
                    if (targetPid != target.Pid || targetCreation != target.Creation)
                    { ResetFrames(); targetPid = target.Pid; targetCreation = target.Creation; }
                    // 帧历史按 4 Hz 读取，硬件和悬浮栏仍为 1 Hz。
                    if ((metrics & MonitoringMetrics.FrameStatistics) != 0 && target.FrameTimes != null)
                        frames.Add(target.FrameTimes, target.FrameSequence);
                    else frames.Reset();
                    if (nextSensorSample > sampleStarted) return;
                    nextSensorSample = sampleStarted + MonitoringPreferences.IntervalMs;
                    // 先显示帧率，再初始化耗时硬件接口。
                    if ((metrics & MonitoringNativeSensors.HardwareMetrics) != 0
                        && (metrics & (MonitoringMetrics.Fps | MonitoringMetrics.FrameTime)) != 0
                        && (Current.Pid != target.Pid || Current.Creation != target.Creation))
                    {
                        double? gpuTime = Value(metrics, MonitoringMetrics.FrameTime, target.GpuFrameTimeMs, 0.001, 10000);
                        var initial = new MonitoringSnapshot(target.Pid, target.Name, DateTime.UtcNow,
                            AutomaticPolicy.Text("监控中 · 硬件数据准备中", "Monitoring · preparing hardware data"),
                            Value(metrics, MonitoringMetrics.Fps, target.Fps, 0, 100000),
                            gpuTime ?? Value(metrics, MonitoringMetrics.FrameTime, target.FrameTimeMs, 0.001, 10000),
                            creation: target.Creation, frameTimeFromGpu: gpuTime.HasValue,
                            viewportWidth: target.ViewportWidth, viewportHeight: target.ViewportHeight);
                        lock (gate)
                        {
                            if (generation != observedGeneration || !started || timer == null || !MonitoringPreferences.Enabled) return;
                            Volatile.Write(ref current, initial);
                            PublishBridgeLocked(initial);
                        }
                    }
                    bool needsSensors = NeedsNativeSensors(metrics, target.GameRamMiB);
                    MonitoringSensorReading reading;
                    bool sensorTargetValid;
                    try
                    {
                        if (needsSensors && sensors == null && sampleStarted >= nextSensorInitialization)
                        {
                            // 失败前先设退避，避免每轮重复初始化硬件接口。
                            nextSensorInitialization = sampleStarted + SensorInitializationRetryMs;
#if PAVISE_SELFTEST
                            if (SensorsFactoryForTest == null) throw new InvalidOperationException("Monitoring tests require isolated sensors.");
                            sensors = SensorsFactoryForTest();
#else
                            sensors = new MonitoringNativeSensors();
#endif
                            if (sensors != null) nextSensorInitialization = 0;
                        }
                        if (needsSensors && sensors == null)
                        { reading = HardwareUnavailable(); sensorTargetValid = true; }
                        else if (needsSensors) sensorTargetValid = sensors.Read(target, metrics, out reading);
                        else { reading = new MonitoringSensorReading(); sensorTargetValid = true; }
                    }
                    catch
                    {
                        // 硬件初始化失败不影响有效帧率显示。
                        reading = HardwareUnavailable();
                        sensorTargetValid = true;
                    }
                    if (!sensorTargetValid)
                    {
                        ResetFrames(); if (sensors != null) sensors.ResetTarget();
                        snapshot = Empty(AutomaticPolicy.Text("目标已退出或不在前台", "Target exited or is no longer foreground"));
                    }
                    else
                    {
                        MonitoringMetrics rolling = target.BenchmarkActive
                            ? metrics & MonitoringMetrics.FrameTimeDeviation : metrics & MonitoringMetrics.FrameStatistics;
                        MonitoringFrameStatistics statistics = frames.Compute(rolling);
                        // 手动录制与滚动统计不能混用。
                        double? average = MonitoringFrameWindow.Valid(target.BenchmarkActive ? target.AverageFps : statistics.AverageFps, 0.1, 100000);
                        double? low1 = MonitoringFrameWindow.Valid(target.BenchmarkActive ? target.Low1 : statistics.Low1, 0.1, 100000);
                        double? low01 = MonitoringFrameWindow.Valid(target.BenchmarkActive ? target.Low01 : statistics.Low01, 0.1, 100000);
                        bool warming = ((metrics & MonitoringMetrics.Low1) != 0 && !low1.HasValue)
                            || ((metrics & MonitoringMetrics.Low01) != 0 && !low01.HasValue)
                            || ((metrics & MonitoringMetrics.AverageFps) != 0 && !average.HasValue)
                            || ((metrics & MonitoringMetrics.FrameTimeDeviation) != 0 && !statistics.FrameTimeDeviation.HasValue);
                        string status = reading.Status.Length == 0 ? AutomaticPolicy.Text("监控中", "Monitoring") : reading.Status;
                        if ((metrics & (MonitoringMetrics.AverageFps | MonitoringMetrics.Low1 | MonitoringMetrics.Low01)) != 0)
                            status += target.BenchmarkActive
                                ? AutomaticPolicy.Text(" · RTSS 基准统计", " · RTSS benchmark statistics")
                                : AutomaticPolicy.Text(" · 最近 6,000 帧统计", " · Latest 6,000 frames");
                        if (warming) status += AutomaticPolicy.Text(" · 帧率统计积累中", " · Collecting frame statistics");
                        double? gpuFrameTime = Value(metrics, MonitoringMetrics.FrameTime, target.GpuFrameTimeMs, 0.001, 10000);
                        double? frameTime = gpuFrameTime ?? Value(metrics, MonitoringMetrics.FrameTime, target.FrameTimeMs, 0.001, 10000);
                        snapshot = new MonitoringSnapshot(target.Pid, target.Name, DateTime.UtcNow,
                            status,
                            Value(metrics, MonitoringMetrics.Fps, target.Fps, 0, 100000),
                            frameTime,
                            Value(metrics, MonitoringMetrics.Low1, low1, 0.1, 100000),
                            Value(metrics, MonitoringMetrics.CpuUsage, reading.CpuUsage, 0, 100),
                            Value(metrics, MonitoringMetrics.GpuUsage, reading.GpuUsage, 0, 100),
                            Value(metrics, MonitoringMetrics.GpuTemperature, reading.GpuTemperature, -50, 200),
                            Value(metrics, MonitoringMetrics.GpuClock, reading.GpuClock, 0, 20000),
                            Value(metrics, MonitoringMetrics.GpuPower, reading.GpuPower, 0, 5000),
                            Value(metrics, MonitoringMetrics.Ram, reading.RamMiB, 0, double.MaxValue),
                            Value(metrics, MonitoringMetrics.Vram, reading.VramMiB, 0, double.MaxValue),
                            Value(metrics, MonitoringMetrics.AverageFps, average, 0.1, 100000),
                            Value(metrics, MonitoringMetrics.Low01, low01, 0.1, 100000),
                            Value(metrics, MonitoringMetrics.FrameTimeDeviation, statistics.FrameTimeDeviation, 0, 10000),
                            Value(metrics, MonitoringMetrics.GameRam, MonitoringFrameWindow.Valid(target.GameRamMiB, 0, double.MaxValue) ?? reading.GameRamMiB, 0, double.MaxValue),
                            Value(metrics, MonitoringMetrics.RamUsage, reading.RamUsage, 0, 100),
                            Value(metrics, MonitoringMetrics.VramUsage, reading.VramUsage, 0, 100),
                            Value(metrics, MonitoringMetrics.GpuMemoryClock, reading.GpuMemoryClock, 0, 100000),
                            Value(metrics, MonitoringMetrics.GpuFan, reading.GpuFanRpm, 0, 100000),
                            Value(metrics, MonitoringMetrics.GpuFan, reading.GpuFanPercent, 0, 500),
                            Value(metrics, MonitoringMetrics.CpuPower, reading.CpuPower, 0, 5000),
                            Value(metrics, MonitoringMetrics.CpuTemperature, reading.CpuTemperature, 1, 150),
                            creation: target.Creation, frameTimeFromGpu: gpuFrameTime.HasValue,
                            retainedHardwareMetrics: reading.RetainedHardwareMetrics & metrics & MonitoringNativeSensors.HardwareMetrics,
                            gpuAdapterName: reading.GpuAdapterName,
                            viewportWidth: target.ViewportWidth, viewportHeight: target.ViewportHeight);
                    }
                }
                lock (gate)
                {
                    if (generation != observedGeneration || !started || timer == null || !MonitoringPreferences.Enabled) return;
                    Volatile.Write(ref current, snapshot);
                    if (snapshot.Pid > 4) PublishBridgeLocked(snapshot); else ClearBridgeLocked(false);
                }
            }
            catch
            {
                ResetFrames();
                if (sensors != null) try { sensors.ResetTarget(); } catch { }
                lock (gate)
                {
                    if (started && timer != null && token == timerToken)
                    {
                        Volatile.Write(ref current, Empty(AutomaticPolicy.Text("监控数据暂不可用", "Monitoring data is temporarily unavailable")));
                        ClearBridgeLocked(false);
                    }
                }
            }
            finally
            {
                lock (gate)
                {
                    if (timer != null && (!started || !MonitoringPreferences.Enabled))
                    {
                        StopTimerLocked();
                        Volatile.Write(ref current, Empty(OffText()));
                    }
                    if (timer == null) { ClearBridgeLocked(true); DisposeSensors(); }
                    else if (started)
                    {
                        int period = targetPid > 4 && (MonitoringPreferences.Metrics & MonitoringMetrics.FrameStatistics) != 0
                            ? FramePollingMs : MonitoringPreferences.IntervalMs;
                        // 游戏事件立即唤醒，待机定时仅作兜底。
                        if (!rtssWanted) period = IdlePollingMs;
                        long now = ClockMilliseconds(), elapsed = Math.Max(0, now - sampleStarted);
                        if (rtssWanted && now < startupProbeUntil) period = StartupPollingMs;
                        // 扣除采集耗时但不补跑积压回调。
                        int due = elapsed < period ? period - (int)elapsed : period;
                        long untilSensors = nextSensorSample - now;
                        if (untilSensors > 0 && untilSensors < due) due = (int)untilSensors;
                        if (wakePending) due = 1;
                        timer.Schedule(Math.Max(1, due));
                    }
                }
                Monitor.Exit(workerGate);
            }
        }
        internal static bool NeedsNativeSensors(MonitoringMetrics metrics, double? gameRam)
        {
            return (metrics & (MonitoringNativeSensors.HardwareMetrics | MonitoringMetrics.CpuUsage | MonitoringMetrics.SystemMemory)) != 0
                || (metrics & MonitoringMetrics.GameRam) != 0 && !MonitoringFrameWindow.Valid(gameRam, 0, double.MaxValue).HasValue;
        }
        private static MonitoringSensorReading HardwareUnavailable()
        { return new MonitoringSensorReading { Status = AutomaticPolicy.Text(
            "监控中 · 硬件数据暂不可用", "Monitoring · hardware data temporarily unavailable") }; }
        private static double? Value(MonitoringMetrics selected, MonitoringMetrics metric, double? value, double min, double max)
        { return (selected & metric) != 0 ? MonitoringFrameWindow.Valid(value, min, max) : null; }
    }
}
