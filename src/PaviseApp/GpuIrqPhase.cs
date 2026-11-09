namespace PaviseApp
{
    internal enum GpuIrqPhase
    {
        None,
        Pending,
        Applied,
        Clear,
        NoRegion,
        RolledBack,
        Skipped
    }
}
