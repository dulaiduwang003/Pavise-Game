namespace PaviseApp
{
    internal interface IPrivacyRecoveryBackend
    {
        bool Satisfied(string action, string target, string current, string wanted);

        bool Restore(PerformanceActionStore.Receipt receipt);
    }
}
