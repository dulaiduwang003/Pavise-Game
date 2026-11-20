namespace PaviseApp
{
    internal sealed class HardThreadOwnership
    {
        internal readonly HardThreadIdentity Thread;

        internal readonly ushort Group;

        internal readonly ulong Mask;

        internal readonly long VerifiedUtcTicks;

        internal HardThreadOwnership(HardThreadIdentity thread, ushort group, ulong mask, long verifiedUtcTicks)
        {
            Thread = thread;
            Group = group;
            Mask = mask;
            VerifiedUtcTicks = verifiedUtcTicks;
        }
    }
}
