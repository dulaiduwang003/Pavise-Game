// @author bdth 2074055628@qq.com
// File purpose Escalation/de-escalation state machine for adaptive suppression: when headroom collapses, temporarily step up to Esports criteria, step back after two stable minutes
using System;

namespace PaviseApp
{
    // Adaptive suppression, Smart tier only, a compromise aimed at "quiet normally, hard when frames drop"
    //   Smart tier's suppression scope and power criteria are the conservative column; with ample headroom that is right, power-saving and quiet
    //   Sustained CPU saturation means headroom has collapsed, then temporarily borrow two things from the Esports tier
    //   the aggressive suppression scope and the aggressive power column; if background contention caused the collapse, escalating fixes the root cause
    //
    // Why the sensor is CPU saturation rather than frame time
    //   The frame time probe is a batch contract, the per-frame timeline is readable only at match end, unavailable mid-match
    //   CPU saturation detection has built-in hysteresis, 90% in after 10s, 80% out after 5s, a mature signal already used for Smart yield
    //   Escalating the suppression tier does nothing for a GPU bottleneck, suppressing background cannot save the GPU, so only CPU-side collapse counts
    //
    // Three anti-oscillation measures: entry/exit hysteresis built into the sensor, de-escalation needs two stable minutes, at most three escalations per match
    //   No circuit breaker: both actuators are tiers the user could switch on manually anyway, a wrong escalation costs some power, not damage
    internal sealed class AdaptiveGuard
    {
        public const long RelaxTicks = TimeSpan.TicksPerSecond * 120;
        public const int MaxEpisodes = 3;

        private bool escalated;
        private long calmSince;
        private int episodes;

        public bool Escalated { get { return escalated; } }
        public int Episodes { get { return episodes; } }

        // Returns +1 escalated this step, -1 de-escalated, 0 no change; the caller only feeds the saturation verdict and logs
        public int Step(bool saturated, long now)
        {
            if (!escalated)
            {
                if (!saturated || episodes >= MaxEpisodes) return 0;
                escalated = true;
                episodes++;
                calmSince = 0;
                return 1;
            }
            if (saturated) { calmSince = 0; return 0; }
            if (calmSince == 0) { calmSince = now; return 0; }
            if (now - calmSince < RelaxTicks) return 0;
            escalated = false;
            calmSince = 0;
            return -1;
        }

        public void Reset()
        {
            escalated = false;
            calmSince = 0;
            episodes = 0;
        }
    }
}
