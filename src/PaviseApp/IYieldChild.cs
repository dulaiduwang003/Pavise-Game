using System;

namespace PaviseApp
{
    internal interface IYieldChild : IDisposable
    {
        bool Owned { get; }

        int Read(out uint[] ids);

        bool Write(uint[] expected, uint[] desired);
    }
}
