using System;

namespace PaviseApp
{
    internal sealed class ActionFailureRecord
    {
        internal string Key;

        internal string Action;

        internal string Target;

        internal string Environment;

        internal string Reason;

        internal DateTime OccurredUtc;

        internal bool PersistenceFailed;
    }
}
