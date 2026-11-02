#if PAVISE_LAB_AUDIO_CHECKS
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

namespace PaviseApp
{
    internal static class PerformancePolicy { internal static string Text(string zh, string en) { return en; } }
    internal static class Logger
    {
        internal static readonly List<string> Lines = new List<string>();
        internal static void Info(string text) { lock (Lines) Lines.Add(text); }
        internal static void Warn(string text) { lock (Lines) Lines.Add(text); }
    }
    internal static class LaboratoryAudioChecks
    {
        private sealed class Fake : ILaboratoryAudioDriver
        {
            internal LaboratoryAudioEndpoint Endpoint = new LaboratoryAudioEndpoint { Id = "wired", ConnectionKnown = true };
            internal LaboratoryAudioPeriod Period = new LaboratoryAudioPeriod {
                Default = 480, Fundamental = 16, Minimum = 128, Maximum = 480, Current = 128, SampleRate = 48000, FormatIdentity = "pcm48" };
            internal LaboratoryAudioPeriod? Actual;
            internal readonly AutoResetEvent Ready = new AutoResetEvent(false);
            internal readonly ManualResetEvent Opened = new ManualResetEvent(false);
            internal long Changed;
            internal bool FailOpen, FailStart, FailRead, FailRender;
            internal int RenderError = unchecked((int)0x80004005);
            internal int Opens, Starts, Reads, Renders, Closes, Disposals, Owner, Delay;
            public WaitHandle RenderReady { get { return Ready; } }
            public long Revision { get { return Changed; } }
            private void CheckThread()
            {
                int id = Thread.CurrentThread.ManagedThreadId;
                if (Owner == 0) Owner = id;
                Check(Owner == id, "driver COM ownership moved between threads");
            }
            public LaboratoryAudioEndpoint DefaultEndpoint() { CheckThread(); return Endpoint; }
            public LaboratoryAudioPeriod Open(string id)
            {
                CheckThread(); Opens++; Opened.Set();
                if (Delay > 0) Thread.Sleep(Delay);
                if (FailOpen) throw new COMException("mock partial activation", unchecked((int)0x88890004));
                return Period;
            }
            public LaboratoryAudioPeriod Start(uint frames)
            {
                CheckThread(); Starts++; Check(frames == Period.Minimum, "wrong requested period");
                if (FailStart) throw new COMException("mock initialization failure");
                return Actual ?? Period;
            }
            public LaboratoryAudioPeriod ReadPeriod()
            { CheckThread(); Reads++; if (FailRead) throw new COMException("mock invalidated"); return Actual ?? Period; }
            public void RenderSilence()
            { CheckThread(); Renders++; if (FailRender) throw new COMException("mock render invalidated", RenderError); }
            public void Close() { CheckThread(); Closes++; }
            public void Dispose() { CheckThread(); Disposals++; Ready.Dispose(); }
        }
        private static int checks;
        private static void Check(bool value, string message)
        { if (!value) throw new InvalidOperationException(message); Interlocked.Increment(ref checks); }
        private static long Seconds(int value) { return value * TimeSpan.TicksPerSecond; }

        private static void SuccessReadbackAndRelease()
        {
            var fake = new Fake(); string state = "";
            using (var session = new LaboratoryAudioSession(fake, delegate(string value) { state = value; }))
            {
                session.Refresh(0, true);
                Check(session.Active && fake.Opens == 1 && fake.Starts == 1, "valid lower readback was not active");
                Check(state.Contains("requested 2.67 ms") && state.Contains("actual 2.67 ms"), "requested/actual periods missing");
                Check(state.Contains("same endpoint and processing mode"), "shared endpoint/mode scope missing");
                session.Render(); Check(fake.Renders == 1, "silent rendering not serviced");
                session.Refresh(Seconds(1), false); Check(fake.Reads == 0, "readback probes ran on every render wakeup");
                session.Refresh(Seconds(4), false); Check(fake.Reads == 1, "active period was not rechecked");
            }
            Check(fake.Closes == 1 && fake.Disposals == 1, "close/dispose failed to release an active stream once");
        }
        private static void StartSuccessIsInsufficient()
        {
            var fake = new Fake(); fake.Period.Current = 480; string state = "";
            using (var session = new LaboratoryAudioSession(fake, delegate(string value) { state = value; }))
            {
                session.Refresh(0, true);
                Check(!session.Active && fake.Starts == 1 && fake.Closes == 1, "Start success without low readback claimed active");
                Check(state.Contains("did not confirm") && state.Contains("actual 10 ms"), "failed readback was not explained");
                session.Refresh(Seconds(10), false);
                Check(fake.Opens == 1, "same endpoint kept retrying an ineffective stream");
            }
            fake = new Fake(); fake.Period.SampleRate = 0;
            using (var session = new LaboratoryAudioSession(fake, delegate { }))
            { session.Refresh(0, true); Check(!session.Active && fake.Starts == 0, "invalid sample rate started a stream"); }
            foreach (int kind in new[] { 0, 1, 2 })
            {
                fake = new Fake(); var actual = fake.Period;
                if (kind == 0) actual.FormatIdentity = "changed-channels";
                if (kind == 1) actual.SampleRate = 44100;
                if (kind == 2) actual.Current = 64;
                fake.Actual = actual;
                using (var session = new LaboratoryAudioSession(fake, delegate { }))
                { session.Refresh(0, true); Check(!session.Active && fake.Closes == 1, "inconsistent actual format/period was accepted"); }
            }
        }
        private static void InvalidPeriodsAndBluetoothAreSkipped()
        {
            uint[] minima = { 0, 480, 512, 127 };
            foreach (uint minimum in minima)
            {
                var fake = new Fake(); fake.Period.Minimum = minimum;
                using (var session = new LaboratoryAudioSession(fake, delegate { }))
                { session.Refresh(0, true); Check(fake.Starts == 0 && !session.Active, "invalid/minimum-no-benefit period started"); }
            }
            foreach (int kind in new[] { 0, 1, 2 })
            {
                var fake = new Fake();
                if (kind == 0) fake.Endpoint.Bluetooth = true;
                if (kind == 1) fake.Endpoint.ConnectionKnown = false;
                if (kind == 2) fake.Endpoint = null;
                using (var session = new LaboratoryAudioSession(fake, delegate { }))
                { session.Refresh(0, true); Check(fake.Opens == 0, "ineligible endpoint attempted stream activation"); }
            }
            Check(LaboratoryAudioSession.IsBluetooth("bthenum") && LaboratoryAudioSession.IsBluetooth("BTHHFENUM")
                && LaboratoryAudioSession.IsBluetooth("BTHLEDEVICE\\test") && !LaboratoryAudioSession.IsBluetooth("HDAUDIO"),
                "Bluetooth transport recognition failed");
        }
        private static void FailureLatchAndDeviceRecovery()
        {
            foreach (int kind in new[] { 0, 1 })
            {
                var fake = new Fake { FailOpen = kind == 0, FailStart = kind == 1 };
                using (var session = new LaboratoryAudioSession(fake, delegate { }))
                {
                    session.Refresh(0, true);
                    Check(fake.Closes == 1 && !session.Active, "partial open/failed start leaked resources");
                    session.Refresh(Seconds(10), false);
                    Check(fake.Opens == 1, "failed endpoint retried without a change");
                    fake.FailOpen = fake.FailStart = false; fake.Changed++;
                    session.Refresh(Seconds(11), false);
                    Check(session.Active && fake.Opens == 2, "device recovery notification did not retry");
                }
            }
            var current = new Fake();
            using (var session = new LaboratoryAudioSession(current, delegate { }))
            {
                session.Refresh(0, true); current.FailRender = true; session.Render();
                Check(!session.Active && current.Closes == 1, "render invalidation did not release");
                session.Refresh(Seconds(4), false); Check(current.Opens == 1, "render failure retried same endpoint");
                current.FailRender = false; current.Endpoint.Id = "new-device";
                session.Refresh(Seconds(8), false); Check(session.Active && current.Opens == 2, "new default endpoint did not recover");
                current.FailRead = true; session.Refresh(Seconds(12), false);
                Check(!session.Active && current.Closes == 2, "period readback failure did not release");
                current.FailRead = false;
                session.Refresh(Seconds(16), true); Check(session.Active, "explicit re-enable did not clear endpoint latch");
            }
        }
        private static void WorkerOwnsResourcesAndDoesNotBlockUpdate()
        {
            var fake = new Fake { Delay = 200 };
            LaboratoryAudio.TestDriverFactory = delegate(AutoResetEvent wake) { return fake; };
            int caller = Thread.CurrentThread.ManagedThreadId;
            var elapsed = Stopwatch.StartNew(); LaboratoryAudio.Update(true); elapsed.Stop();
            Check(elapsed.ElapsedMilliseconds < 150, "UI update waited for audio device activation");
            Check(fake.Opened.WaitOne(2000), "worker failed to reach mock audio activation");
            for (int i = 0; i < 12; i++) LaboratoryAudio.Update(true);
            Check(LaboratoryAudio.Restore(), "restore did not wait for worker resource release");
            Check(fake.Owner != caller && fake.Opens == 1 && fake.Closes == 1 && fake.Disposals == 1,
                "worker did not keep acquire/release on its own thread");
            Check(LaboratoryAudio.Restore(), "repeated restore failed");
            Check(LaboratoryAudio.State == "Off", "disabled worker state is not off");
        }
        private static void TransientInvalidationHasOneBoundedRetry()
        {
            var fake = new Fake { RenderError = unchecked((int)0x88890004) };
            using (var session = new LaboratoryAudioSession(fake, delegate { }))
            {
                session.Refresh(0, true); fake.FailRender = true; session.Render();
                session.Refresh(Seconds(14), false); Check(fake.Opens == 1, "transient failure retried before cooldown");
                session.Refresh(Seconds(16), false); Check(fake.Opens == 2 && session.Active, "transient failure did not receive bounded retry");
                session.Render();
                session.Refresh(Seconds(40), false); Check(fake.Opens == 2 && !session.Active, "repeated transient failures retried indefinitely");
            }
        }
        private static void BootstrapFailureCanBeRetriedByExplicitToggle()
        {
            var fake = new Fake(); int factories = 0;
            LaboratoryAudio.TestDriverFactory = delegate(AutoResetEvent wake) {
                if (Interlocked.Increment(ref factories) == 1) throw new COMException("mock bootstrap failure");
                return fake;
            };
            LaboratoryAudio.Update(true);
            var timeout = Stopwatch.StartNew();
            while (!LaboratoryAudio.State.Contains("unavailable") && timeout.ElapsedMilliseconds < 2000) Thread.Sleep(5);
            Check(LaboratoryAudio.State.Contains("unavailable"), "bootstrap failure was not reported");
            LaboratoryAudio.Update(true); Thread.Sleep(20);
            Check(factories == 1, "game scans kept retrying a failed COM bootstrap");
            LaboratoryAudio.Update(false); LaboratoryAudio.Update(true);
            Check(fake.Opened.WaitOne(2000), "rapid explicit off/on toggle did not clear bootstrap latch");
            Check(LaboratoryAudio.Restore() && fake.Disposals == 1, "retried worker resources were not released");
        }
        private static void NativeAbiIsComplete()
        {
            Type native = typeof(LowLatencyAudioNative);
            Type audio = native.GetNestedType("IAudioClient3", BindingFlags.NonPublic);
            Check(audio.GUID == new Guid("7ED4EE07-8E67-4CD4-8C1A-2B7A5987AD42"), "IAudioClient3 IID drifted");
            Check(audio.GetMethods().Length == 18, "IAudioClient3 inherited vtable incomplete");
            Type render = native.GetNestedType("IAudioRenderClient", BindingFlags.NonPublic);
            Check(render.GetMethods().Length == 2, "audio render ABI incomplete");
            Check(native.GetNestedType("IMMDeviceEnumerator", BindingFlags.NonPublic).GetMethods().Length == 5,
                "notification enumerator ABI incomplete");
        }
        internal static int Main()
        {
            try
            {
                SuccessReadbackAndRelease(); StartSuccessIsInsufficient(); InvalidPeriodsAndBluetoothAreSkipped();
                FailureLatchAndDeviceRecovery(); TransientInvalidationHasOneBoundedRetry();
                WorkerOwnsResourcesAndDoesNotBlockUpdate(); BootstrapFailureCanBeRetriedByExplicitToggle(); NativeAbiIsComplete();
                Console.WriteLine("PASS laboratory audio checks=" + checks + "; COM=mock-only audio_devices=untouched windows_shown=false");
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine("FAIL " + error); return 1; }
            finally { LaboratoryAudio.Restore(); }
        }
    }
}
#endif
