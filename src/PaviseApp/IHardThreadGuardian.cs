using System;

namespace PaviseApp
{
    internal interface IHardThreadGuardian : IDisposable
    {
        HardThreadGuardianActor Actor { get; }

        bool ResourcesClosed { get; }

        void NoteSettled(HardThreadSettledSource original);

        void RequireForward();

        void OnLost(Action callback);
    }
}
