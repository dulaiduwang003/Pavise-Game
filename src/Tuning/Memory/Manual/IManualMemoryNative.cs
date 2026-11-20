namespace PaviseApp
{
    internal interface IManualMemoryNative
    {
        bool TryQuery(out ManualMemorySnapshot snapshot);

        int Execute(ManualMemoryStep step);
    }
}
