// @author bdth 2074055628@qq.com
// 文件用途 自适应压制的升降级状态机 余量塌了临时升到专注口径 稳定两分钟降回
using System;

namespace PaviseApp
{
    // 自适应压制 智能档专属 面向"平时安静 掉帧时狠"的折中
    //   智能档的压制范围和电源口径是保守列 余量充足时那是对的 省电安静
    //   CPU 持续饱和说明余量塌了 这时临时借用专注档的两样东西
    //   激进压制范围和电源激进列 塌因若是后台抢食 升档直接治本
    //
    // 为什么传感器用 CPU 饱和而不是帧时间
    //   帧时间探针是批处理契约 逐帧时间线要退局才能读 局中拿不到
    //   CPU 饱和判定自带滞回(90% 进 10 秒 80% 出 5 秒) 是已经在给智能让位用的成熟信号
    //   GPU 瓶颈升压制档也无益 压后台救不了 GPU 所以只认 CPU 侧的塌方
    //
    // 防振荡三件套 进出滞回(传感器自带) 降级要稳定两分钟 每局最多升三次
    //   没有熔断 这两个执行器都是用户本来就能手动开的档位 升错了代价是费点电 不是伤害
    internal sealed class AdaptiveGuard
    {
        public const long RelaxTicks = TimeSpan.TicksPerSecond * 120;
        public const int MaxEpisodes = 3;

        private bool escalated;
        private long calmSince;
        private int episodes;

        public bool Escalated { get { return escalated; } }
        public int Episodes { get { return episodes; } }

        // 返回 +1 本次升档 -1 本次降档 0 无变化 调用方只负责喂饱和判定和打日志
        public int Step(bool saturated, long now)
        {
            if (!escalated)
            {
                if (!saturated || episodes >= MaxEpisodes) return 0;
                escalated = true;
                episodes++;
                calmSince = 0;
                return 1;
            }
            if (saturated) { calmSince = 0; return 0; }
            if (calmSince == 0) { calmSince = now; return 0; }
            if (now - calmSince < RelaxTicks) return 0;
            escalated = false;
            calmSince = 0;
            return -1;
        }

        public void Reset()
        {
            escalated = false;
            calmSince = 0;
            episodes = 0;
        }
    }
}
