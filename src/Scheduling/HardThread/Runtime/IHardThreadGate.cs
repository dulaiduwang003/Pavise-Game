using System;

namespace PaviseApp
{
    internal interface IHardThreadGate
    {
        HardThreadTarget Target { get; }

        bool AllowWorkers { get; }

        IDisposable EnterUse();

        void RequireActive();

        void RequireRecovery();
    }
}
