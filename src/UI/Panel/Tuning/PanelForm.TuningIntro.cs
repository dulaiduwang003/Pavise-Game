using System;
using System.Windows.Forms;

namespace PaviseApp
{
    internal partial class PanelForm
    {
        private bool tuningIntroHandled, tuningIntroQueued;

        private bool ReadyForTuningIntro()
        {
            return !tuningIntroHandled && pagePolicy != null && curPage == pagePolicy
                && !IsDisposed && !Disposing && !runtimeShutdownBusy && wipeBusy == 0
                && !SelfUpdateGate.IsBusy && !UpdateDialog.IsOpen && IsHandleCreated && Visible && Enabled
                && WindowState != FormWindowState.Minimized && !shotMode
                && !introPending && !introActive && !outroActive && !moveSizeLoop;
        }

        private void QueueTuningIntro()
        {
            if (tuningIntroQueued || !ReadyForTuningIntro()) return;
            tuningIntroQueued = true;
            try
            {
                BeginInvoke(new MethodInvoker(delegate
                {
                    tuningIntroQueued = false;
                    if (!ReadyForTuningIntro() || AnyDialogOpen()) return;
                    tuningIntroHandled = true;
                    if (Settings.Load("TuningIntroSeenV1", false)) return;
                    try
                    {
                        using (var dialog = PaviseDialog.CreateTuningIntro(this))
                        {
                            if (dialog.ShowDialog(this) != DialogResult.OK) return;
                        }
                        // 保存失败只留到下次启动处理 避免反复打断本次操作
                        bool confirmed;
                        if (!Settings.SaveDurable("TuningIntroSeenV1", true)
                            || !Settings.TryLoadBool("TuningIntroSeenV1", false, out confirmed) || !confirmed)
                            Logger.Warn(Logger.Text("调优面板使用提示已确认，但确认状态未能保存", "Tuning panel tip acknowledged, but the acknowledgement could not be saved"));
                    }
                    catch (Exception error) { Logger.Warn(Logger.Format("调优面板使用提示展示失败： {0}", "Tuning panel tip failed to show: {0}", error.GetType().Name)); }
                }));
            }
            catch (InvalidOperationException)
            {
                tuningIntroQueued = false;
                tuningIntroHandled = true;
            }
        }
    }
}
