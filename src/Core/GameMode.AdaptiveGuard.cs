// @author bdth 2074055628@qq.com
// File purpose Session orchestration for adaptive suppression, sampling feeds the state machine, escalate/de-escalate flips one bool that the two execution points read on their own
//   Smart tier only, has a switch, off by default, after escalation programs with visible windows get isolated too, second-screen video and voice apps with open windows are affected
//   The Smart tier promise is not to touch what the user is actively using, so this can't default on and can't lack a switch
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

        // Fed once per main loop pass, own saturation instance so it doesn't compete with smart-yield sampling for deltas
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
                    // Reaching here isn't load recovery, it's a tier switch or session wind-down, log it as such
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
