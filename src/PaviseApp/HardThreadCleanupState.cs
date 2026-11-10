using System;

namespace PaviseApp
{
    internal sealed class HardThreadCleanupState
    {
        private readonly bool[] removed = new bool[6];

        private readonly bool[] deferred = new bool[6];

        internal bool Removed(HardThreadCleanupPart part)
        {
            return removed[(int)part];
        }

        internal bool Deferred(HardThreadCleanupPart part)
        {
            return deferred[(int)part];
        }

        internal bool Step(IHardThreadCleanupFiles original)
        {
            if (original == null || !original.OriginalSourceReady)
            {
                return false;
            }
            foreach (HardThreadCleanupPart cleanupPart in Enum.GetValues(typeof(HardThreadCleanupPart)))
            {
                int valueSnapshot = (int)cleanupPart;
                if (removed[valueSnapshot] || deferred[valueSnapshot])
                {
                    continue;
                }
                if (cleanupPart == HardThreadCleanupPart.Directory && Array.IndexOf(deferred, true) >= 0)
                {
                    deferred[valueSnapshot] = true;
                    continue;
                }
                if (original.DeleteExact(cleanupPart))
                {
                    removed[valueSnapshot] = true;
                    continue;
                }
                if (cleanupPart < HardThreadCleanupPart.Configuration || !original.HeldElsewhere(cleanupPart))
                {
                    return false;
                }
                deferred[valueSnapshot] = true;
            }
            return original.ClosureCertain;
        }
    }
}
