using System;

namespace PaviseApp
{
    internal sealed class HardThreadOptionalEntry
    {
        private readonly object gate = new object();
        private bool missing;

        internal bool TryInvoke(Func<bool> read, out bool unsupported)
        {
            lock (gate)
            {
                unsupported = missing;
                if (missing) return true;
                try { return read(); }
                catch (EntryPointNotFoundException)
                {
                    missing = unsupported = true;
                    return true;
                }
            }
        }
    }
}
