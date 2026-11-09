using System;

namespace PaviseApp
{
    internal sealed class HardThreadOperationException : InvalidOperationException
    {
        internal readonly HardThreadFailureKind Kind;

        internal readonly bool Recover;

        internal HardThreadOperationException(HardThreadFailureKind kind, string message, bool recover = false)
            : base(message)
        {
            Kind = kind;
            Recover = recover;
        }
    }
}
