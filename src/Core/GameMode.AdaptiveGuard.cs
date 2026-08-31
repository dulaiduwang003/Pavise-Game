// @author bdth 2074055628@qq.com
// 文件用途 自适应压制的会话编排 采样喂状态机 升降档只翻一个布尔 两个执行点自取
//   智能档内置 没有开关 与智能保帧/智能让位同一待遇 档位本身就是启用条件
using System;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private readonly AdaptiveGuard adaptiveGuard = new AdaptiveGuard();
        private readonly CpuSaturation adaptivePressure = new CpuSaturation();
        private volatile bool adaptiveEscalated;

        // 主循环每轮喂一次 独立的饱和度实例 不与智能让位的采样抢差分
        private void StepAdaptiveGuard()
        {
            bool want = EffPreset == PerformancePreset.Standard
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
