namespace PaviseApp
{
    internal sealed class AutoCoreGovernor
    {
        private readonly int capacity;

        private readonly int logical;

        internal bool Exclusive { get; private set; }

        internal string Blocked { get; private set; }

        internal AutoCoreGovernor(int exclusiveCores, int exclusiveLogical)
        {
            capacity = exclusiveCores;
            logical = exclusiveLogical;
        }

        internal void EngageManual()
        {
            if (Blocked == null && capacity > 0 && logical >= 1)
            {
                Exclusive = true;
            }
        }

        internal AutoCoreStep Pause()
        {
            if (Blocked != null || !Exclusive)
            {
                return AutoCoreStep.None;
            }
            Exclusive = false;
            return AutoCoreStep.Release;
        }

        internal AutoCoreStep Block(string reason)
        {
            if (Blocked == null)
            {
                Blocked = reason;
            }
            if (!Exclusive)
            {
                return AutoCoreStep.None;
            }
            Exclusive = false;
            return AutoCoreStep.Release;
        }
    }
}
