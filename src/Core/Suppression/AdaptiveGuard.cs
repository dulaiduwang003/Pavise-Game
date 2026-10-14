// 作者：bdth 2074055628@qq.com
using System;

namespace PaviseApp
{
    internal sealed class AdaptiveGuard
    {
        public const long RelaxTicks = TimeSpan.TicksPerSecond * 120;
        public const int MaxEpisodes = 3;

        private bool escalated;
        private long calmSince;
        private int episodes;

        public bool Escalated { get { return escalated; } }
        public int Episodes { get { return episodes; } }

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
