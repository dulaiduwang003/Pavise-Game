namespace PaviseApp
{
    internal interface IAppGpuPreferenceLedger
    {
        bool TryRead(out string value);

        bool TryWrite(string value);
    }
}
