namespace PaviseApp
{
    internal sealed class ManualMemorySnapshot
    {
        internal ulong TotalBytes;

        internal ulong AvailableBytes;

        internal ulong CommittedBytes;

        internal ulong CommitLimitBytes;

        internal ulong? StandbyBytes;

        internal bool IsValid
        {
            get
            {
                if (TotalBytes != 0)
                {
                    return AvailableBytes <= TotalBytes;
                }
                return false;
            }
        }
    }
}
