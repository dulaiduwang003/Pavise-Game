using System.Threading;

namespace PaviseApp
{
    internal partial class GameMode : ILaboratoryHost
    {
        private LaboratoryCoordinator laboratoryCoordinator;

        // 正常由构造函数创建 这里的兜底只给测试里不走构造函数造出来的 GameMode 用
        internal LaboratoryCoordinator Laboratory
        {
            get { return LazyInitializer.EnsureInitialized(ref laboratoryCoordinator, () => new LaboratoryCoordinator(this)); }
        }

        object ILaboratoryHost.SyncGate { get { return sync; } }
        object ILaboratoryHost.PowerApplyGate { get { return powerApplyGate; } }
        bool ILaboratoryHost.AutomaticAdmittedLocked
        {
            get
            {
                return enabled && active && !stopping && !panicReq && !ProfileStoreSaveFailed && !autoIgnoreLoadFailed
                    && (libraryIgnoreTransaction == null || !libraryIgnoreTransaction.RecoveryPending);
            }
        }
        GameDetection ILaboratoryHost.ActiveDetection { get { return activeDetection; } }
        long ILaboratoryHost.GameSessionStarts { get { return Interlocked.Read(ref gameSessionStarts); } }
    }
}
