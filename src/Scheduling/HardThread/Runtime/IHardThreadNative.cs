using System;

namespace PaviseApp
{
    internal interface IHardThreadNative : IDisposable
    {
        HardThreadMachine ReadMachine();

        HardThreadObserved ReadThread(HardThreadIdentity thread);

        bool Set(HardThreadIdentity thread, ushort group, ulong mask, out ushort previousGroup, out ulong previousMask);
    }
}
