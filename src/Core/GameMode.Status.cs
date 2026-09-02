// @author bdth 2074055628@qq.com
// 文件用途 对局状态与预设读取 策略访问器与电源滑块覆盖
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace PaviseApp
{
    internal partial class GameMode
    {
        public bool IsActive { get { lock (sync) return active; } }

        public string ActiveGame { get { lock (sync) return active ? activeGame : null; } }

        public PerformancePreset Preset
        {
            get { lock (sync) return preset; }
            set
            {
                if (!PresetValue.IsValid((int)value)) value = PerformancePreset.Standard;
                lock (sync) preset = value;
                Settings.SaveStr("PerformancePreset", ((int)value).ToString());
                RequestPolicyApply();
            }
        }

        public PerformancePreset ActivePreset
        {
            get
            {
                PolicySnapshot s = sessionPolicy;
                if (s != null) return s.Preset;
                lock (sync) return preset;
            }
        }

        public string SessionPolicySourceName
        {
            get
            {
                PolicySnapshot s = sessionPolicy;
                return s != null && !s.IsGlobal && IsActive ? s.ProfileName : null;
            }
        }

        public string SessionPolicyProfileId
        {
            get
            {
                PolicySnapshot s = sessionPolicy;
                return s != null && IsActive ? s.ProfileId : null;
            }
        }

        private bool EffSuppress
        {
            get
            {
                PolicySnapshot s = sessionPolicy;
                return s != null ? s.EffSuppress : bgSuppressOn;
            }
        }

        private bool overlayRaised;
        private int overlayAttempts;

        // 这个挂点每轮扫描都会跑 对局中 500ms 一次
        //   拨失败了不设上限的话 整局每 500ms 重试一次 每次写注册表加打一条失败日志
        //   跟 EnvFuse 一个道理 试够就不试了 下一局重新给机会
        private const int MaxOverlayAttempts = 3;

        // 电源滑块拨到最佳性能 只有笔记本插电打专注档才动 退场必还原
        private void MaybeActivatePowerOverlay(bool competitive)
        {
            if (overlayRaised || overlayAttempts >= MaxOverlayAttempts) return;
            if (!PowerOverlay.ShouldActivate(Native.HasSystemBattery(),
                    Native.OnAcPower(), competitive)) return;
            if (!PowerOverlay.Supported()) { overlayAttempts = MaxOverlayAttempts; return; }
            overlayAttempts++;
            overlayRaised = RunIrqIsolatedMutation(
                delegate { return PowerOverlay.Activate(); });
        }

        internal void RestorePowerOverlay()
        {
            overlayAttempts = 0;
            if (!overlayRaised) return;
            overlayRaised = false;
            PowerOverlay.Restore();
        }

        // 对局激活那一刻定格的档位 功耗让路只在专注档参与
        private PerformancePreset EffPreset
        {
            get
            {
                PolicySnapshot s = sessionPolicy;
                return s != null ? s.Preset : Preset;
            }
        }

        private bool EffBoost
        {
            get
            {
                PolicySnapshot s = sessionPolicy;
                return s != null ? s.EffBoost : boostOn;
            }
        }

        private bool EffLane
        {
            get
            {
                PolicySnapshot s = sessionPolicy;
                return s != null ? s.EffLane : renderLaneOn;
            }
        }

        // 会话快照只冻结"有逐游戏覆盖"的键 无覆盖的键每次实时回读全局设置
        //   所以对局里关掉全局开关会立刻生效 不会被定格值重新声明
        //   带覆盖的游戏才是定格语义 覆盖值在建快照那一刻拍板 局中改覆盖走清除流程
        private bool EffVramShield
        {
            get
            {
                PolicySnapshot s = sessionPolicy;
                return s != null ? s.VramShield : vramShieldOn;
            }
        }

        private bool EffPowerYield
        {
            get
            {
                PolicySnapshot s = sessionPolicy;
                return s != null ? s.PowerYield : PowerBudgetYieldRunner.EnabledSetting;
            }
        }

        private bool EffCacheWarm
        {
            get
            {
                PolicySnapshot s = sessionPolicy;
                return s != null ? s.CacheWarm : cacheWarmOn;
            }
        }

        private bool EffGpuClockLock
        {
            get
            {
                PolicySnapshot s = sessionPolicy;
                return s != null ? s.GpuClockLock : gpuClockLockOn;
            }
        }

        private bool EffIntelEnduranceOff
        {
            get
            {
                PolicySnapshot s = sessionPolicy;
                return s != null ? s.IntelEnduranceOff : intelEnduranceOn;
            }
        }

        private bool EffLaptopPerf
        {
            get
            {
                PolicySnapshot s = sessionPolicy;
                return s != null ? s.LaptopPerf : laptopPerfOn;
            }
        }
    }
}
