// 作者：bdth 2074055628@qq.com
using System;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private readonly AdaptiveGuard adaptiveGuard = new AdaptiveGuard();
        private readonly CpuSaturation adaptivePressure = new CpuSaturation();
        private volatile bool adaptiveEscalated;
        private volatile bool adaptiveEscalateOn;

        public bool AdaptiveEscalateOn
        {
            get { return adaptiveEscalateOn; }
            set
            {
                adaptiveEscalateOn = value;
                Settings.Save(PolicyCatalog.KeyAdaptiveEscalate, value);
                RequestPolicyApply();
            }
        }

        private bool EffAdaptiveEscalate
        {
            get
            {
                PolicySnapshot s = sessionPolicy;
                return s != null ? s.AdaptiveEscalate : adaptiveEscalateOn;
            }
        }

        private void StepAdaptiveGuard()
        {
            bool want = EffPreset == PerformancePreset.Standard && EffAdaptiveEscalate
                && !stopping && !panicReq;
            long now = DateTime.UtcNow.Ticks;
            if (!want)
            {
                if (adaptiveEscalated)
                {
                    adaptiveEscalated = false;
                    Logger.Log(Lang.T("log.adaptive.3"));
                }
                return;
            }
            int change = adaptiveGuard.Step(
                adaptivePressure.Update(adaptivePressure.Sample(), now), now);
            if (change > 0)
            {
                adaptiveEscalated = true;
                Logger.Log(Lang.T("log.adaptive.1")
                    + adaptiveGuard.Episodes + "/" + AdaptiveGuard.MaxEpisodes);
            }
            else if (change < 0)
            {
                adaptiveEscalated = false;
                Logger.Log(Lang.T("log.adaptive.2"));
            }
        }

        private void ResetAdaptiveGuard()
        {
            adaptiveGuard.Reset();
            adaptivePressure.Reset();
            adaptiveEscalated = false;
        }
    }
}
