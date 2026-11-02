using System.Threading;

namespace PaviseApp
{
    // One in-process owner prevents exits, resets and overlapping update attempts while files are staged.
    internal static class SelfUpdateGate
    {
        private static int busy;

        internal static bool IsBusy { get { return Volatile.Read(ref busy) != 0; } }

        internal static bool TryBegin()
        {
            return Interlocked.CompareExchange(ref busy, 1, 0) == 0;
        }

        internal static void End()
        {
            Interlocked.Exchange(ref busy, 0);
        }
    }
}
