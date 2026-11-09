namespace PaviseApp
{
    internal sealed class RendererProbeTicket
    {
        public readonly long Generation;

        public readonly long StartedMs;

        public readonly GameDetection Detection;

        public volatile bool Canceled;

        internal RendererProbeTicket(long generation, long startedMs, GameDetection detection)
        {
            Generation = generation;
            StartedMs = startedMs;
            Detection = RendererHandoffTracker.Copy(detection);
        }
    }
}
