using System;

namespace PaviseApp
{
    internal interface IStandbyMemoryControl
    {
        bool TryQuery(out StandbyMemorySnapshot snapshot);

        bool TryPurge(Func<bool> mayContinue, out int nativeStatus);
    }
}
