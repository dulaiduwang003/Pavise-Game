namespace PaviseApp
{
    internal sealed class NvGamePlan
    {
        public bool MaxPerf;

        public string LowLatMode;

        public bool SmoothMotion;

        public bool ShaderCacheMax;

        public bool Rebar;

        public string DlssMode;

        public bool WindowedVrr;

        public bool Automatic;

        internal int AutomaticKey
        {
            get
            {
                return (MaxPerf ? 1 : 0) | (ShaderCacheMax ? 2 : 0) | ((LowLatMode == "on") ? 4 : 0) | (WindowedVrr ? 8 : 0) | (Rebar ? 16 : 0);
            }
        }

        public bool Empty
        {
            get
            {
                if (!MaxPerf && !Rebar && !WindowedVrr && !SmoothMotion && !ShaderCacheMax && (LowLatMode == null || LowLatMode == "off"))
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

        internal static NvGamePlan ForAutomatic(bool maxPerf, bool shaderCache, bool onAc, string lowLatency = "off", bool windowedVrr = false, bool rebar = false)
        {
            NvGamePlan nvGamePlan = new NvGamePlan();
            nvGamePlan.Automatic = true;
            nvGamePlan.MaxPerf = maxPerf && onAc;
            nvGamePlan.ShaderCacheMax = shaderCache;
            nvGamePlan.LowLatMode = ((lowLatency == "on") ? "on" : "off");
            nvGamePlan.DlssMode = "off";
            nvGamePlan.WindowedVrr = windowedVrr;
            nvGamePlan.Rebar = rebar;
            return nvGamePlan;
        }
    }
}
