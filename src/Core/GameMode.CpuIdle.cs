// 文件用途 显式开启的禁止 CPU 空闲 原生写入和还原共用电源工作线程闸
using System;
using System.Threading;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private int cpuIdleGeneration;
        private bool cpuIdleActive;
        private bool cpuIdleWanted;

        private bool EffDisableCpuIdle
        {
            get { return LiveCpuIdlePreference(PolicyCatalog.KeyDisableCpuIdle); }
        }

        private bool LiveCpuIdlePreference(string key)
        {
            lock (sync)
            {
                PolicySnapshot snapshot = sessionPolicy;
                bool global = key == PolicyCatalog.KeyDisableCpuIdle ? disableCpuIdleOn : planSwitch;
                if (snapshot == null || string.IsNullOrEmpty(snapshot.ProfileId)) return global;
                foreach (GameProfile profile in profiles)
                    if (string.Equals(profile.Id, snapshot.ProfileId, StringComparison.OrdinalIgnoreCase))
                    {
                        string value;
                        return profile.Overrides.TryGetValue(key, out value) ? value == "1" : global;
                    }
                return false;
            }
        }

        private Func<bool> CaptureCpuIdleAdmission(bool allowPowerPlan)
        {
            int generation = Volatile.Read(ref cpuIdleGeneration);
            // 拿 PowerPlan 的原生锁之前 先把当前档案读一次
            // 用户每改一次这个令牌就失效 包括关了又开这种
            bool wanted = allowPowerPlan && EffDisableCpuIdle
                && LiveCpuIdlePreference(PolicyCatalog.KeyPowerPlan);
            return delegate
            {
                return wanted && generation == Volatile.Read(ref cpuIdleGeneration)
                    && active && enabled && !stopping && !panicReq && !ProfileStoreSaveFailed;
            };
        }

        private void ApplyCpuIdlePolicy(bool allowPowerPlan)
        {
            Func<bool> mayContinue = CaptureCpuIdleAdmission(allowPowerPlan);
            if (!mayContinue() && !cpuIdleActive && !PowerPlan.CpuIdleHasResidue) return;
            lock (powerApplyGate)
            {
                bool want = mayContinue();
                lock (sync) { if (envFused.Contains("cpuidle")) want = false; }
                // 不支持 电池供电 以及用户自选方案这三种算跳过
                // 不是激活失败 先把之前拥有的值还原掉
                want = want && PowerPlan.CpuIdleEligible;
                lock (sync)
                {
                    if (cpuIdleWanted != want)
                    {
                        cpuIdleWanted = want;
                        envNextAttempt.Remove("cpuidle");
                        envFailures.Remove("cpuidle");
                    }
                }
                bool applied = want ? PowerPlan.CpuIdleActive : cpuIdleActive;
                if (!want && PowerPlan.CpuIdleHasResidue) applied = true;
                bool result = EnvStep("cpuidle", want, applied,
                    delegate { return PowerPlan.TryDisableCpuIdle(mayContinue); }, PowerPlan.RestoreCpuIdle);
                // 取消或回滚成功 以及用户本来就关着这个设置
                // 都不给 Pavise 这次设置改动的所有权
                cpuIdleActive = want ? result && PowerPlan.CpuIdleActive : result;
            }
        }

#if PAVISE_SELFTEST
        internal bool ProbeEffDisableCpuIdle { get { return EffDisableCpuIdle; } }
        internal Func<bool> CaptureCpuIdleAdmissionForTest() { return CaptureCpuIdleAdmission(true); }
        internal bool StepCpuIdleForTest(bool allowPowerPlan)
        {
            ApplyCpuIdlePolicy(allowPowerPlan);
            return cpuIdleActive;
        }
#endif
    }
}
