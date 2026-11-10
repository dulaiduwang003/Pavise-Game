using System;
using System.Threading;

namespace PaviseApp
{
    internal partial class PanelForm
    {
        internal Action<UpdateDialog, string, SelfUpdatePackage> InstallSelfUpdate;

        internal string SelfUpdateAdmissionReason()
        {
            if (IsDisposed || Disposing || runtimeShutdownBusy || Volatile.Read(ref wipeBusy) != 0)
                return PerformancePolicy.Text("软件正在退出或维护，请完成后重新更新。", "The app is shutting down or under maintenance. Retry when it finishes.");
            if (gameMode != null && gameMode.IsActive)
                return PerformancePolicy.Text("请先退出游戏，再更新 Pavise，避免中断本局守护。", "Leave the game before updating Pavise to avoid interrupting the current guard session.");
            if (gameMode != null && gameMode.ProfileStoreSaveFailed)
                return PerformancePolicy.Text("游戏库保存未完成，请先处理保存问题；不会通过更新重置用户数据。", "The game library could not be saved. Resolve that issue first; updating will not reset user data.");
            if (Volatile.Read(ref auditBusy) != 0 || ShaderCleanupBusy || familyChangeBusy || rebarChangeBusy || fullscreenOptimizationsChangeBusy
                || PrivacyControl.Busy || PerformanceTuning.EnvironmentBundleBusy()
                || (configurationPresetRuntime != null && configurationPresetRuntime.Busy))
                return PerformancePolicy.Text("检测、配置或维护操作仍在进行，请完成后重新更新。", "An inspection, configuration or maintenance operation is still running. Retry when it finishes.");
            return null;
        }

        private void OpenSoftwareUpdate()
        {
            if (SelfUpdateGate.IsBusy) { UpdateDialog.ActivateCurrent(); return; }
            if (UpdateDialog.IsOpen) { UpdateDialog.ActivateCurrent(); return; }
            string reason = SelfUpdateAdmissionReason();
            if (reason != null) { PaviseDialog.Warn(this, "PAVISE", reason); return; }
            CancelAutoHide();
            CancelOutro();
            UpdateDialog.ShowFor(this, latestUpdate, InstallSelfUpdate);
        }
    }
}
