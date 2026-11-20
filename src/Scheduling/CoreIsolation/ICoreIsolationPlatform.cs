using System;

namespace PaviseApp
{
    internal interface ICoreIsolationPlatform
    {
        string BootIdentity();

        IsolationCpuState ReadSystem(IntPtr process);

        bool SetSystemAllowed(ulong mask);

        IntPtr Open(int pid, long creation);

        bool Exited(IntPtr handle);

        bool Gone(int pid, long creation);

        bool ReadAllowed(IntPtr handle, out ulong mask);

        bool SetAllowed(IntPtr handle, ulong mask);

        void Close(IntPtr handle);
    }
}
