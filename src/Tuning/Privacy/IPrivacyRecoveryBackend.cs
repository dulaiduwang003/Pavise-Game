namespace PaviseApp
{
    internal interface IPrivacyRecoveryBackend
    {
        bool Restore(PerformanceActionStore.Receipt receipt);
    }
}
