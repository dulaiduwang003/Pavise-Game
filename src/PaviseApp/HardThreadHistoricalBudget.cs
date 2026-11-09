namespace PaviseApp
{
    internal sealed class HardThreadHistoricalBudget
    {
        private int sessions;

        private long imageBytes;

        internal bool BeginSession(long elapsedMilliseconds)
        {
            if (elapsedMilliseconds < 0 || (sessions > 0 && elapsedMilliseconds > 2000) || sessions >= 8)
            {
                return false;
            }
            sessions++;
            return true;
        }

        internal bool ReadImage(long bytes)
        {
            if (bytes <= 0 || bytes > 67108864 || imageBytes > 67108864 - bytes)
            {
                return false;
            }
            imageBytes += bytes;
            return true;
        }
    }
}
