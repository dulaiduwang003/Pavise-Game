using System;

namespace PaviseApp
{
    internal interface IStandbyMemoryNative
    {
        bool TryGetPerformanceInfo(out StandbyPerformanceInformation information);

        int QueryMemoryList(out StandbyMemoryListInformation information, out int returnedBytes);

        bool TryAcquirePurgePrivilege(out IDisposable lease);

        int SetSystemInformation(int informationClass, ref int command, int length);
    }
}
