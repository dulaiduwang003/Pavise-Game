using System;
using System.Threading;

namespace PaviseApp
{
    internal partial class GameMode : ILaptopPerformanceHost
    {
        private LaptopPerformanceCoordinator laptopPerformance;

        // 正常由构造函数创建 这里的兜底只给测试里不走构造函数造出来的 GameMode 用
        internal LaptopPerformanceCoordinator LaptopPerformance
        {
            get { return LazyInitializer.EnsureInitialized(ref laptopPerformance, () => new LaptopPerformanceCoordinator(this)); }
        }

        bool ILaptopPerformanceHost.Stopping { get { return stopping; } }
        bool ILaptopPerformanceHost.GuardEnabled { get { return enabled; } }
        bool ILaptopPerformanceHost.GameActive { get { return active; } }
        bool ILaptopPerformanceHost.ShuttingDown { get { return stopping || panicReq; } }
        bool ILaptopPerformanceHost.PowerSessionEnding { get { return powerSessionEnding; } }
        bool ILaptopPerformanceHost.ProfileStoreReady { get { return !ProfileStoreSaveFailed; } }
        bool ILaptopPerformanceHost.GameGone { get { return gameGoneSinceTicks != 0; } }
        bool ILaptopPerformanceHost.PowerPlanActive { get { return planActive; } }
        int ILaptopPerformanceHost.PowerSessionGeneration { get { return Volatile.Read(ref powerSessionGen); } }
        object ILaptopPerformanceHost.PowerApplyGate { get { return powerApplyGate; } }
        bool ILaptopPerformanceHost.AutomaticWorkAllowed(string key) { return AutomaticWorkAllowed(key); }
        void ILaptopPerformanceHost.RecordAutomaticWorkFailure(string key, string reason) { RecordAutomaticWorkFailure(key, reason); }
        void ILaptopPerformanceHost.RecordAutomation(string key, string state) { RecordAutomation(key, state); }
        void ILaptopPerformanceHost.RequestPolicyApply(string caller) { RequestPolicyApply(caller); }
        void ILaptopPerformanceHost.Kick() { kick.Set(); }
        bool ILaptopPerformanceHost.EnvStep(string key, bool want, bool active, Func<bool> activate, Func<bool> restore)
        { return EnvStep(key, want, active, activate, restore); }
    }
}
