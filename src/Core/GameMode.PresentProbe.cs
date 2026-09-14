// @author bdth 2074055628@qq.com
// File purpose Present probe, long frame capture and DPC alignment
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal partial class GameMode
    {
        // Present capture starts and stops per match, new instance each match, PresentProbe.frames never self-clears so reuse would accumulate across matches
        //   needs admin, if not admin or Start fails (already logged internally) keep no reference, skip gracefully without crashing
        private void StartPresentProbe()
        {
            try
            {
                PresentProbe old = presentProbe;
                presentProbe = null;
                if (old != null) try { old.Stop(); } catch { }
            }
            catch { }
            try
            {
                var p = new PresentProbe();
                presentProbe = p.Start() ? p : null;
            }
            catch { presentProbe = null; }
        }

        // Stop this match's present session, take only the renderer process frames, compute long frame intervals (previous frame qpc to this frame qpc) for intersecting with the DPC timeline
        //   present/DPC alignment exists only to strengthen the device interrupt verdict, produces no match report summary, no frame count, p99 or 1% low
        //   must be a complete drain, no truncation/lost events, and the renderer pid itself must have at least 30 valid intervals, otherwise returns null
        //   a handful of presents, or presents from other processes, are not enough for a meaningful time-alignment sample
        //   intervals are QPC-ascending and contiguous, same QPC ruler as the DPC session, both RawTimestamp, so they align directly
        private IrqFrameEvidence irqFrameEvidence;

        private void CollectLongFrames(int rendererPid, TimeSpan sessionDuration,
            out List<long[]> longFrameIntervals)
        {
            longFrameIntervals = null;
            irqFrameEvidence = null;
            PresentProbe p = presentProbe;
            presentProbe = null;
            if (p == null) return;

            List<PresentFrame> frames;
            long freq;
            try
            {
                p.Stop();
                if (!PresentDpcAlignment.PresentCaptureComplete(p.DrainCompleted, p.ConsumerExitedEarly,
                    p.Truncated, p.EventsLost, p.BuffersLost)) return;
                frames = p.Frames;
                freq = p.QpcFrequency;
            }
            catch { return; }
            // The alignment sample must cover at least half the match and no less than 2 seconds, a handful of frames from match start is not recorded as a clue
            long begin, end;
            if (!irqProbe.FrameWindow(out begin, out end) || freq <= 0) return;
            frames = frames.FindAll(delegate(PresentFrame f) { return f.Qpc >= begin && f.Qpc <= end; });
            double minCoverageSeconds = Math.Max(2.0, (end - begin) / (double)freq * 0.5);
            longFrameIntervals = PresentDpcAlignment.BuildLongFrameIntervals(
                frames, freq, rendererPid, minCoverageSeconds, out irqFrameEvidence);
        }

        internal static int UpdateSessionRendererPid(string sessionProfileId, int currentPid,
            string detectedProfileId, int detectedPid)
        {
            return !string.IsNullOrEmpty(sessionProfileId) && detectedPid > 0
                && string.Equals(sessionProfileId, detectedProfileId, StringComparison.OrdinalIgnoreCase)
                ? detectedPid : currentPid;
        }

        internal static bool SameReportedProfile(string sessionProfileId, string detectedProfileId)
        {
            return !string.IsNullOrEmpty(sessionProfileId)
                && !string.IsNullOrEmpty(detectedProfileId)
                && string.Equals(sessionProfileId, detectedProfileId, StringComparison.OrdinalIgnoreCase);
        }

    }
}
