namespace PaviseApp
{
    internal static class OvernightPerformancePolicy
    {
        internal static bool Enabled
        {
            get
            {
#if PAVISE_STABILITY_COMPARISON
                return false;
#else
                return true;
#endif
            }
        }
    }
}
