namespace PaviseApp
{
    internal interface IAppGpuPreferenceControl
    {
        bool Supported { get; }

        bool IsExecutablePresent(string path);

        bool TryRead(string path, out string value);

        AppGpuPreferenceWriteResult CompareExchange(string path, string expected, string replacement);
    }
}
