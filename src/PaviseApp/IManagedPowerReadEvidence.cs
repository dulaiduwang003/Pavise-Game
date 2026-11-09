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

    // 通过读取证据区分设置不受支持与读取失败，不改变旧后端。
    internal interface IManagedPowerReadEvidence
    {
        ManagedPowerReadEvidence ReadEvidence(Guid scheme, Guid sub, Guid setting, bool ac);
    }
}
