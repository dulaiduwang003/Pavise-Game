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
        Unified,
        // 游戏优先：线程交给线程协同提优，统一调度不参与。
        Classic
    }
}
