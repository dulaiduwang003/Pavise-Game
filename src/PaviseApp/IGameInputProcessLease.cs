using System;

namespace PaviseApp
{
    internal interface IGameInputProcessLease : IDisposable
    {
        long Creation { get; }

        bool IsAlive { get; }
    }
}
