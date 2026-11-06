using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;

namespace PaviseApp
{
    internal sealed class LaboratoryAudioEndpoint
    {
        internal string Id;
        internal bool Bluetooth, ConnectionKnown;
    }

    internal struct LaboratoryAudioPeriod
    {
        internal uint Default, Fundamental, Minimum, Maximum, Current;
        internal int SampleRate;
        internal string FormatIdentity;
    }

    internal interface ILaboratoryAudioDriver : IDisposable
    {
        WaitHandle RenderReady { get; }
        long Revision { get; }
        LaboratoryAudioEndpoint DefaultEndpoint();
        LaboratoryAudioPeriod Open(string endpointId);
        LaboratoryAudioPeriod Start(uint frames);
        LaboratoryAudioPeriod ReadPeriod();
        void RenderSilence();
        void Close();
    }

    internal sealed class LaboratoryAudioSession : IDisposable
    {
        private readonly ILaboratoryAudioDriver driver;
        private readonly Action<string> report;
        private bool open, active;
        private string attempted;
        private long revision = long.MinValue, nextProbe;
        private long lastNow, transientRetryAt;
        private int transientRetries = 1;
        private LaboratoryAudioPeriod requested;

        internal LaboratoryAudioSession(ILaboratoryAudioDriver driver, Action<string> report)
        { this.driver = driver; this.report = report; }
        internal bool Active { get { return active; } }
        internal WaitHandle RenderReady { get { return driver.RenderReady; } }

        internal void Refresh(long now, bool reset)
        {
            lastNow = now;
            if (reset) { Close(); attempted = null; nextProbe = 0; transientRetryAt = 0; transientRetries = 1; }
            if (transientRetryAt != 0 && now >= transientRetryAt)
            { transientRetryAt = 0; attempted = null; nextProbe = 0; }
            long changed = driver.Revision;
            if (now < nextProbe && changed == revision) return;
            nextProbe = now + 3L * TimeSpan.TicksPerSecond;
            try
            {
                LaboratoryAudioEndpoint endpoint = driver.DefaultEndpoint();
                string key = (endpoint == null ? "<none>" : endpoint.Id) + "|" + changed;
                if (key == attempted)
                {
                    if (active) Verify(driver.ReadPeriod());
                    return;
                }
                Close(); attempted = key; revision = changed; transientRetryAt = 0;
                if (endpoint == null)
                { report(Text("未生效：没有默认播放设备", "Inactive: no default playback endpoint")); return; }
                if (endpoint.Bluetooth)
                { report(Text("不适用：蓝牙输出", "Not applicable: Bluetooth output")); return; }
                if (!endpoint.ConnectionKnown)
                { report(Text("未生效：无法确认输出连接类型", "Inactive: output connection could not be identified")); return; }
                // Open can partially acquire native resources; Close is mandatory even when it throws.
                open = true;
                requested = driver.Open(endpoint.Id);
                if (!WorthApplying(requested))
                {
                    Close();
                    report(Text("不适用：设备没有更小的有效共享周期", "Not applicable: no smaller valid shared period"));
                    return;
                }
                Verify(driver.Start(requested.Minimum));
            }
            catch (Exception error)
            {
                Close(); revision = changed;
                report(Text("未生效：", "Inactive: ") + Failure(error));
                ScheduleTransientRetry(error, now);
            }
        }

        private void Verify(LaboratoryAudioPeriod actual)
        {
            if (actual.SampleRate != requested.SampleRate
                || !string.Equals(actual.FormatIdentity, requested.FormatIdentity, StringComparison.Ordinal)
                || actual.Current < requested.Minimum || actual.Current > requested.Maximum
                || actual.Current % requested.Fundamental != 0 || actual.Current >= requested.Default)
            {
                Close();
                report(Text("未生效：周期读回未确认降低；", "Inactive: readback did not confirm a lower period; ")
                    + PeriodText(requested, actual));
                return;
            }
            active = true;
            report(Text("已生效：", "Active: ") + PeriodText(requested, actual)
                + Text("；仅同设备、同信号处理模式的共享音频", "; shared audio on the same endpoint and processing mode only"));
        }

        internal void Render()
        {
            if (!active) return;
            try { driver.RenderSilence(); }
            catch (Exception error)
            {
                Close();
                report(Text("已释放失效音频流：", "Invalid audio stream released: ") + Failure(error));
                ScheduleTransientRetry(error, lastNow);
                // Other errors latch this endpoint/revision until a device change or explicit re-enable.
            }
        }

        private void Close()
        {
            active = false;
            if (!open) return;
            try { driver.Close(); }
            finally { open = false; }
        }
        public void Dispose() { try { Close(); } finally { driver.Dispose(); } }

        private void ScheduleTransientRetry(Exception error, long now)
        {
            int hr = error.HResult;
            if (error is COMException && transientRetries > 0
                && (hr == unchecked((int)0x88890004) || hr == unchecked((int)0x88890026)
                    || hr == unchecked((int)0x88890010)))
            { transientRetries--; transientRetryAt = now + 15L * TimeSpan.TicksPerSecond; }
        }

        internal static bool WorthApplying(LaboratoryAudioPeriod value)
        {
            return value.SampleRate > 0 && value.Fundamental > 0 && value.Minimum > 0
                && value.Minimum < value.Default && value.Minimum <= value.Maximum
                && value.Minimum % value.Fundamental == 0;
        }
        internal static bool IsBluetooth(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            return value.IndexOf("BTH", StringComparison.OrdinalIgnoreCase) >= 0
                || value.IndexOf("BLUETOOTH", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        private static string Text(string zh, string en) { return PerformancePolicy.Text(zh, en); }
        internal static string PeriodText(LaboratoryAudioPeriod requested, LaboratoryAudioPeriod actual)
        {
            return Text("默认 ", "default ") + Milliseconds(requested.Default, requested.SampleRate)
                + Text("，申请 ", ", requested ") + Milliseconds(requested.Minimum, requested.SampleRate)
                + Text("，实际 ", ", actual ") + Milliseconds(actual.Current, actual.SampleRate);
        }
        private static string Milliseconds(uint frames, int rate)
        { return rate > 0 ? (frames * 1000.0 / rate).ToString("0.##", CultureInfo.InvariantCulture) + " ms" : frames + " frames"; }
        private static string Failure(Exception error)
        { return error.GetType().Name + " (0x" + error.HResult.ToString("X8", CultureInfo.InvariantCulture) + ")"; }
    }

    internal static class LaboratoryAudio
    {
        internal const string Key = "LabAudioLowBufferV1";
        private static readonly object gate = new object();
        private static readonly AutoResetEvent command = new AutoResetEvent(false);
        private static Thread worker;
        private static bool enabled;
        private static long generation;
        private static long ownerSerial;
        internal static long OwnerSerial { get { lock (gate) return ownerSerial; } }
        private static string state;
#if PAVISE_LAB_AUDIO_CHECKS
        internal static Func<AutoResetEvent, ILaboratoryAudioDriver> TestDriverFactory;
#endif
        internal static string State
        { get { lock (gate) return state ?? PerformancePolicy.Text("已关闭", "Off"); } }

        internal static void Update(bool value)
        {
            lock (gate)
            {
                if (enabled != value) { enabled = value; generation++; }
                if (value && worker == null) StartWorker();
                if (!value && worker == null) state = PerformancePolicy.Text("已关闭", "Off");
                command.Set();
            }
        }
        private static void StartWorker()
        {
            ownerSerial++;
            state = PerformancePolicy.Text("正在检查默认播放设备…", "Checking default playback endpoint…");
            worker = new Thread(Run) { IsBackground = true, Name = "Pavise laboratory audio" };
            worker.SetApartmentState(ApartmentState.MTA);
            worker.Start();
        }
        internal static bool Restore()
        {
            long expectedOwner;
            lock (gate) expectedOwner = ownerSerial;
            return Restore(expectedOwner);
        }
        internal static bool Restore(long expectedOwner)
        {
            Thread pending;
            lock (gate)
            {
                if (ownerSerial != expectedOwner) return false;
                Update(false); pending = worker;
            }
            bool exited = pending == null || (pending != Thread.CurrentThread && pending.Join(2500));
            lock (gate) return exited && ownerSerial == expectedOwner && worker == null;
        }
        private static void Report(string value)
        {
            bool changed;
            lock (gate) { changed = state != value; state = value; }
            if (changed) Logger.Info("LaboratoryAudio | " + value);
        }
        private static void Run()
        {
            LaboratoryAudioSession session = null;
            long workerGeneration;
            lock (gate) workerGeneration = generation;
            try
            {
                ILaboratoryAudioDriver driver;
#if PAVISE_LAB_AUDIO_CHECKS
                driver = TestDriverFactory(command);
#else
                driver = new LowLatencyAudioNative(command);
#endif
                session = new LaboratoryAudioSession(driver, Report);
                long seen = -1;
                WaitHandle[] waits = { command, session.RenderReady };
                while (true)
                {
                    bool wanted; long current;
                    lock (gate) { wanted = enabled; current = generation; }
                    if (!wanted) break;
                    workerGeneration = current;
                    session.Refresh(DateTime.UtcNow.Ticks, seen != current); seen = current;
                    int ready = WaitHandle.WaitAny(waits, 500);
                    if (ready == 1) session.Render();
                }
            }
            catch (Exception error)
            {
                Report(PerformancePolicy.Text("音频实验不可用：", "Audio experiment unavailable: ") + error.GetType().Name);
                // Do not restart the worker on every game scan after a COM/bootstrap failure.
                while (true)
                {
                    lock (gate) if (!enabled || generation != workerGeneration) break;
                    command.WaitOne();
                }
            }
            finally
            {
                if (session != null) try { session.Dispose(); } catch (Exception error)
                { Logger.Warn("LaboratoryAudio release | " + error.GetType().Name); }
                lock (gate)
                {
                    worker = null;
                    if (enabled) StartWorker();
                    else state = PerformancePolicy.Text("已关闭", "Off");
                }
            }
        }
    }
}
