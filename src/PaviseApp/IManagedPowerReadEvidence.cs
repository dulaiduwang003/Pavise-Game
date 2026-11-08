using System;

namespace PaviseApp
{
    internal enum ManagedPowerReadState { Value, ConfirmedUnregistered, Unavailable }

    internal struct ManagedPowerReadEvidence
    {
        internal readonly ManagedPowerReadState State;
        internal readonly uint Value, NativeCode, ResolutionCode;
        internal readonly bool UsedDefault;
        internal ManagedPowerReadEvidence(ManagedPowerReadState state, uint value, uint nativeCode, uint resolutionCode, bool usedDefault)
        { State = state; Value = value; NativeCode = nativeCode; ResolutionCode = resolutionCode; UsedDefault = usedDefault; }
    }

    // Read evidence distinguishes unsupported settings from failed reads without changing legacy backends.
    internal interface IManagedPowerReadEvidence
    {
        ManagedPowerReadEvidence ReadEvidence(Guid scheme, Guid sub, Guid setting, bool ac);
    }
}
