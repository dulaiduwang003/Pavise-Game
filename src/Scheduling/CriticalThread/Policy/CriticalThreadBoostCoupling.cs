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
        // 游戏线程提优在运行 线程优先级交给线程协同
        Classic
    }
}
