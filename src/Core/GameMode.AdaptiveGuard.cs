// @author bdth 2074055628@qq.com
// 文件用途 自适应压制的会话编排 采样喂状态机 升降档只翻一个布尔 两个执行点自取
//   智能档专属 有开关默认关 升档后带可见窗口的程序也会被隔离 副屏视频和开着窗口的语音都受影响
//   智能档的承诺是不碰用户正在用的东西 所以这条不能默认开 也不能没有开关
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

        // 主循环每轮喂一次 独立的饱和度实例 不与智能让位的采样抢差分
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
                    // 走到这里不是负载恢复 是档位切走或会话在收尾 日志按实说
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
