namespace PaviseApp
{
    internal static class BackgroundSessionPolicy
    {
        internal static bool KeepAwake(bool sessionRunning, GameDetection detection)
        {
            return sessionRunning && detection != null && detection.RendererForeground;
        }

        internal static bool PauseDownloads(PolicySnapshot policy, bool sessionRunning, GameDetection detection)
        {
            return sessionRunning && detection != null && policy != null && policy.SuppressBackground
                && (detection.RendererForeground || PresetValue.IsCompetitive(policy.Preset));
        }
    }
}
