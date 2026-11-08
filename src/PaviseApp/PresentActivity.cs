using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace PaviseApp
{
    // Identify submitters from runtime/kernel Present events, independent of graphics API.
    // Executable names, launchers and windows do not establish presentation ownership.
    internal sealed class PresentActivitySample
    {
        private readonly Dictionary<int, int> frames;
        internal readonly bool Valid;
        internal readonly string Failure;
        internal readonly int WindowMs;

        private PresentActivitySample(bool valid, string failure, Dictionary<int, int> frames, int windowMs)
        {
            Valid = valid; Failure = failure ?? ""; WindowMs = windowMs;
            this.frames = frames ?? new Dictionary<int, int>();
        }

        internal static PresentActivitySample Unavailable(string failure)
        { return new PresentActivitySample(false, string.IsNullOrEmpty(failure) ? "unavailable" : failure, null, 0); }

        internal static PresentActivitySample FromFrames(IDictionary<int, int> frames, int windowMs)
        { return new PresentActivitySample(windowMs > 0, windowMs > 0 ? "" : "empty-window", frames == null ? null : new Dictionary<int, int>(frames), windowMs); }

        internal int FramesOf(int pid)
        { int value; return pid > 0 && frames.TryGetValue(pid, out value) ? value : 0; }

        internal double FpsOf(int pid)
        { return WindowMs > 0 ? FramesOf(pid) * 1000.0 / WindowMs : 0.0; }

        internal int ProcessCount { get { return frames.Count; } }

        internal KeyValuePair<int, int>[] Entries()
        { return new List<KeyValuePair<int, int>>(frames).ToArray(); }
    }

    internal sealed class PresentActivityCapture
    {
        internal enum Kind { None, RuntimeDxgi, RuntimeD3d9, KernelPresent, KernelHistory }

        private sealed class Counts
        {
            internal int Dxgi, D3d9, KernelPresent, KernelHistory, KernelDetailed;
            // One frame can emit a runtime event plus kernel events; count each source once.
            internal int Frames
            { get { return Math.Max(Math.Max(Dxgi, D3d9), Math.Max(KernelPresent, Math.Max(KernelHistory, KernelDetailed))); } }
        }

        private readonly object gate = new object();
        private readonly Dictionary<int, Counts> counts = new Dictionary<int, Counts>();
        private readonly FrameCoordinationTrace trace = new FrameCoordinationTrace();
        private readonly Stopwatch clock = new Stopwatch();
        private Native.EventRecordCallback receive;
        private bool finishing, finished;

        private PresentActivityCapture() { }

        internal static PresentActivityCapture TryStart(out string failure)
        {
            failure = "";
            var capture = new PresentActivityCapture();
            capture.receive = capture.Receive;
            try
            {
                capture.clock.Start();
                if (capture.trace.StartPresentProbe(capture.receive)) return capture;
                failure = "start-error-" + capture.trace.Error;
            }
            catch (Exception error) { failure = "start-" + error.GetType().Name; }
            try { capture.trace.Stop(); } catch { }
            return null;
        }

        // Lost events, failed stop or unsettled receipts invalidate the sample and retain its recovery debt.
        internal PresentActivitySample Finish()
        {
            lock (gate)
            {
                if (finishing) return PresentActivitySample.Unavailable("finished");
                finishing = true;
            }
            clock.Stop();
            int windowMs = (int)Math.Min(int.MaxValue, clock.ElapsedMilliseconds);
            bool stopped = false;
            try { stopped = trace.Stop(); } catch { }
            var frames = new Dictionary<int, int>();
            lock (gate)
            {
                finished = true;
                if (!stopped) return PresentActivitySample.Unavailable("stop-error-" + trace.Error + "-lost-" + trace.Lost);
                foreach (KeyValuePair<int, Counts> pair in counts)
                {
                    int value = pair.Value.Frames;
                    if (value > 0) frames[pair.Key] = value;
                }
            }
            return PresentActivitySample.FromFrames(frames, windowMs);
        }

        internal static Kind Classify(Guid provider, ushort id, byte opcode)
        {
            if (provider == FrameCoordinationSampler.Dxgi)
                return (id == 42 || id == 55) ? Kind.RuntimeDxgi : Kind.None;            // Present_Start, PresentMultiplaneOverlay_Start
            if (provider == FrameCoordinationSampler.D3d9)
                return id == 1 ? Kind.RuntimeD3d9 : Kind.None;                          // Present_Start
            if (provider == FrameCoordinationTrace.DxgKrnl)
            {
                if (id == 184) return Kind.KernelPresent;                              // Present (D3DKMTPresent)
                if ((id == 171 || id == 215) && opcode == 1) return Kind.KernelHistory; // PresentHistory(Detailed)_Start
            }
            return Kind.None;
        }

        internal void Count(int pid, Kind kind, ushort id)
        {
            if (pid <= 4 || kind == Kind.None) return;
            lock (gate)
            {
                if (finished) return;
                Counts value;
                if (!counts.TryGetValue(pid, out value)) counts[pid] = value = new Counts();
                switch (kind)
                {
                    case Kind.RuntimeDxgi: value.Dxgi++; break;
                    case Kind.RuntimeD3d9: value.D3d9++; break;
                    case Kind.KernelPresent: value.KernelPresent++; break;
                    case Kind.KernelHistory: if (id == 215) value.KernelDetailed++; else value.KernelHistory++; break;
                }
            }
        }

        private void Receive(ref Native.EventRecord record)
        {
            try
            {
                Native.EventDescriptor descriptor = record.EventHeader.EventDescriptor;
                Kind kind = Classify(record.EventHeader.ProviderId, descriptor.Id, descriptor.Opcode);
                if (kind != Kind.None) Count((int)record.EventHeader.ProcessId, kind, descriptor.Id);
            }
            catch { }
        }

#if PAVISE_SELFTEST
        private PresentActivityCapture(FrameCoordinationTrace ownedTrace) { trace = ownedTrace; clock.Start(); }
        internal static PresentActivityCapture ForTest() { var capture = new PresentActivityCapture(); capture.clock.Start(); return capture; }
        internal static PresentActivityCapture CleanupForTest()
        { return new PresentActivityCapture(FrameCoordinationTrace.CleanupFixtureForTest()); }
        internal PresentActivitySample SnapshotForTest(int windowMs)
        {
            var frames = new Dictionary<int, int>();
            lock (gate) foreach (KeyValuePair<int, Counts> pair in counts) if (pair.Value.Frames > 0) frames[pair.Key] = pair.Value.Frames;
            return PresentActivitySample.FromFrames(frames, windowMs);
        }
#endif
    }
}
