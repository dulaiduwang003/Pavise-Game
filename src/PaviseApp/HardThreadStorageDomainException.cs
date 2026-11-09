using System;

namespace PaviseApp
{
    internal sealed class HardThreadStorageDomainException : NotSupportedException
    {
        internal HardThreadStorageDomainException(string message)
            : base(message)
        {
        }

        internal HardThreadStorageDomainException(string message, Exception inner)
            : base(message, inner)
        {
        }
    }
}
