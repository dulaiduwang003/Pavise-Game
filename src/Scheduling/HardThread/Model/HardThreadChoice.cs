namespace PaviseApp
{
    internal sealed class HardThreadChoice
    {
        internal readonly HardThreadIdentity Thread;

        internal readonly ulong Mask;

        internal HardThreadChoice(HardThreadIdentity thread, ulong mask)
        {
            Thread = thread;
            Mask = mask;
        }
    }
}
