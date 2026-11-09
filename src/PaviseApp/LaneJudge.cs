namespace PaviseApp
{
    internal sealed class LaneJudge
    {
        public const int ConfirmWins = 3;

        public const int UnpinStrikes = 2;

        public const int MaxUnpins = 2;

        public const double ReverifyHealthyShare = 0.15;

        private int candidateTid;

        private int streak;

        private int pinnedTid;

        private int strikes;

        private int unpins;

        public int PinnedTid
        {
            get
            {
                return pinnedTid;
            }
        }

        public bool GaveUp
        {
            get
            {
                return unpins >= 2;
            }
        }

        public LaneDecision Observe(int winnerTid, double winnerShare, double pinnedShare)
        {
            if (pinnedTid == 0)
            {
                if (winnerTid <= 0 || winnerShare < 0.35)
                {
                    candidateTid = 0;
                    streak = 0;
                    return LaneDecision.None;
                }
                if (winnerTid == candidateTid)
                {
                    streak++;
                }
                else
                {
                    candidateTid = winnerTid;
                    streak = 1;
                }
                if (streak < 3)
                {
                    return LaneDecision.None;
                }
                pinnedTid = winnerTid;
                candidateTid = 0;
                streak = 0;
                strikes = 0;
                return LaneDecision.Pin;
            }
            if (pinnedShare >= 0.15)
            {
                strikes = 0;
                return LaneDecision.None;
            }
            if (winnerTid == pinnedTid || winnerShare < 0.35)
            {
                return LaneDecision.None;
            }
            if (++strikes < 2)
            {
                return LaneDecision.None;
            }
            pinnedTid = 0;
            strikes = 0;
            unpins++;
            return LaneDecision.Unpin;
        }

        public void ResetCandidate()
        {
            candidateTid = 0;
            streak = 0;
        }

        public void PinFailed()
        {
            pinnedTid = 0;
            ResetCandidate();
        }
    }
}
