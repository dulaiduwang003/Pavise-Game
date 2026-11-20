namespace PaviseApp
{
    internal sealed class AdaptiveGuard
    {
        public const double EnterUtilization = 0.7;

        public const double ExitUtilization = 0.6;

        public const long RelaxTicks = 1200000000L;

        public const int MaxEpisodes = 3;

        private bool escalated;

        private long calmSince;

        private int episodes;

        public bool Escalated
        {
            get
            {
                return escalated;
            }
        }

        public int Episodes
        {
            get
            {
                return episodes;
            }
        }

        public int Step(bool saturated, long now)
        {
            if (!escalated)
            {
                if (!saturated || episodes >= 3)
                {
                    return 0;
                }
                escalated = true;
                episodes++;
                calmSince = 0L;
                return 1;
            }
            if (saturated)
            {
                calmSince = 0L;
                return 0;
            }
            if (calmSince == 0)
            {
                calmSince = now;
                return 0;
            }
            if (now - calmSince < 1200000000)
            {
                return 0;
            }
            escalated = false;
            calmSince = 0L;
            return -1;
        }

        public void Reset()
        {
            escalated = false;
            calmSince = 0L;
            episodes = 0;
        }
    }
}
