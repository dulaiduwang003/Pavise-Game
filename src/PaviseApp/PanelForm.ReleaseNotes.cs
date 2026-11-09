using System;
using System.Windows.Forms;

namespace PaviseApp
{
    internal partial class PanelForm
    {
        // 由 ShowPanel 开启此标记，创建隐藏启动窗口时不能展示更新说明。
        private bool releaseNotesAutoRequested;
        private bool releaseNotesAutoHandled;
        private bool releaseNotesQueued;
        private bool releaseNotesShowing;

        private bool ReadyForReleaseNotes()
        {
            return releaseNotesAutoRequested && !releaseNotesAutoHandled && !releaseNotesShowing
                && !IsDisposed && !Disposing && !runtimeShutdownBusy && !SelfUpdateGate.IsBusy && !UpdateDialog.IsOpen && IsHandleCreated && Visible
                && WindowState != FormWindowState.Minimized && !shotMode
                && !introPending && !introActive && !outroActive && !moveSizeLoop;
        }

        private void QueueUnseenReleaseNotes()
        {
            if (releaseNotesQueued || !ReadyForReleaseNotes()) return;
            releaseNotesQueued = true;
            try
            {
                BeginInvoke(new MethodInvoker(delegate
                {
                    releaseNotesQueued = false;
                    if (!ReadyForReleaseNotes() || AnyDialogOpen()) return;
                    if (ReleaseNotes.Current == null) return;
                    if (!ReleaseNotes.HasUnseen)
                    {
                        releaseNotesAutoHandled = true;
                        return;
                    }
                    try { ShowReleaseNotes(); }
                    catch (Exception error) { Logger.Warn("更新说明展示失败： " + error.GetType().Name); }
                }));
            }
            catch (InvalidOperationException) { releaseNotesQueued = false; }
        }
    }
}
