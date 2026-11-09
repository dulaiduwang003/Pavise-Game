namespace PaviseApp
{
    internal interface IAppGpuPreferenceControl
    {
        bool Supported { get; }

        bool IsExecutablePresent(string path);

        bool TryRead(string path, out string value);

        bool TryGetStagedOriginal(string path, out bool staged, out string original);

        bool TryReleaseStage(string path);

        AppGpuPreferenceWriteResult CompareExchange(string path, string expected, string replacement);
    }
}
