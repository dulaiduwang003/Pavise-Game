namespace PaviseApp
{
    internal sealed class NvGamePlan
    {
        public bool MaxPerf;

        public bool ShaderCacheMax;

        public bool Rebar;

        public bool LowLatency;

        public string DlssMode;

        public bool Automatic;

        internal int AutomaticKey
        {
            get
            {
                return (MaxPerf ? 1 : 0) | (ShaderCacheMax ? 2 : 0) | (LowLatency ? 4 : 0) | (Rebar ? 16 : 0);
            }
        }

        public bool Empty
        {
            get
            {
                if (!MaxPerf && !Rebar && !ShaderCacheMax && !LowLatency)
                {
                    if (DlssMode != null)
                    {
                        return DlssMode == "off";
                    }
                    return true;
                }
                return false;
            }
        }

        internal static NvGamePlan ForAutomatic(bool maxPerf, bool shaderCache, bool onAc, bool rebar = false, bool lowLatency = false)
        {
            NvGamePlan nvGamePlan = new NvGamePlan();
            nvGamePlan.Automatic = true;
            nvGamePlan.MaxPerf = maxPerf && onAc;
            nvGamePlan.ShaderCacheMax = shaderCache;
            nvGamePlan.DlssMode = "off";
            nvGamePlan.Rebar = rebar;
            nvGamePlan.LowLatency = lowLatency;
            return nvGamePlan;
        }
    }
}
