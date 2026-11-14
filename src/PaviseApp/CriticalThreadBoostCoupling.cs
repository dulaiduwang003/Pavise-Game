namespace PaviseApp
{
    internal enum CriticalThreadBoostCoupling
    {
        Unknown,
        BoostOff,
        BoostDisabled,
        CandidateUnsupported,
        CandidateSelected,
        FrameSelected,
        BoostInactive,
        SameThread,
        OtherThread,
        FrameAssociated,
        FrameOther,
        Unified
    }
}
