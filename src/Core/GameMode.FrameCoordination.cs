using System;
using System.Threading;

namespace PaviseApp
{
    internal partial class GameMode
    {
        public bool FrameCoordinationEnabled { get { return AutomationPolicySnapshot.FrameCoordinationOn; } }
        private void SyncFrameCoordination(BoostPass pass)
        {
            int pid=pass.RendererPid; long birth=pass.RendererCreation; int epoch=Volatile.Read(ref rendererHandoffEpoch);
            Func<bool> admitted=delegate {
                GameDetection current=Volatile.Read(ref activeDetection);
                return FrameCoordinationEnabled && enabled && active && !stopping && !panicReq && !ProfileStoreSaveFailed
                    && EffBoost && !pass.WriteDenied && epoch==Volatile.Read(ref rendererHandoffEpoch)
                    && current!=null && current.RendererCandidateSelected && !current.RendererSafetyOnly && !current.RequiresGpuConfirm
                    && current.RendererPid==pid && current.RendererCreation==birth && RendererForegroundPid()==pid;
            };
            if (pid>0 && birth>0 && admitted()) FrameCoordination.Ensure(pid,birth,pass.RendererName,admitted);
            else FrameCoordination.ReleaseExcept(0,0);
        }
    }
}
