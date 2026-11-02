using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace PaviseApp
{
    internal partial class PanelForm
    {
        private Label overviewActivity;
        private GuardActivitySnapshot overviewActivitySnapshot;
        private int overviewActivityPending;
        private int overviewActivityEpoch;
        private bool overviewActivityShown;
        private long overviewActivityNextRead;

        private void BuildOverviewGuardActivity()
        {
            overviewActivity = new Label {
                Name = "overviewGuardActivity", AutoSize = false, AutoEllipsis = true,
                Font = Theme.UI(9f, false), BackColor = Theme.Bg,
                ForeColor = Theme.Dim, TextAlign = ContentAlignment.MiddleCenter,
                TabStop = false, Visible = false
            };
            pageOverview.Controls.Add(overviewActivity);
        }

        private void PresentOverviewGuardActivity(GuardActivitySnapshot snapshot)
        {
            overviewActivitySnapshot = snapshot;
            if (overviewActivity == null || overviewActivity.IsDisposed) return;
            overviewActivity.Text = snapshot == null
                ? PerformancePolicy.Text("正在读取本局执行结果…", "Reading this session's actions…") : snapshot.Summary;
            overviewActivity.AccessibleName = overviewActivity.Text;
            overviewActivity.AccessibleDescription = overviewActivity.Text;
        }

        private void RefreshOverviewGuardActivity()
        {
            if (overviewActivity == null || overviewActivity.IsDisposed) return;
            bool shown = gameMode.Enabled && gameMode.IsActive;
            if (shown != overviewActivityShown)
            {
                overviewActivityShown = shown;
                overviewActivityEpoch++;
                overviewActivity.Visible = shown;
                overviewActivityNextRead = 0;
                PresentOverviewGuardActivity(null);
                overviewViewport = Size.Empty;
                LayoutOverviewPage();
            }
            if (!shown) { overviewActivitySnapshot = null; return; }
            long session = gameMode.GuardActivitySession;
            long now = DateTime.UtcNow.Ticks;
            if (overviewActivitySnapshot != null && (overviewActivitySnapshot.Session != session
                || now - overviewActivitySnapshot.CapturedTicks > TimeSpan.FromSeconds(10).Ticks))
                PresentOverviewGuardActivity(null);
            if (!Visible || curPage != pageOverview || now < overviewActivityNextRead
                || Interlocked.CompareExchange(ref overviewActivityPending, 1, 0) != 0) return;
            overviewActivityNextRead = now + TimeSpan.FromSeconds(2).Ticks;
            int epoch = overviewActivityEpoch;
            try
            {
                QueueUiStateWork(delegate {
                    try
                    {
                        GuardActivitySnapshot snapshot = gameMode.CaptureGuardActivity();
                        if (IsDisposed || Disposing || !IsHandleCreated) return;
                        PostUiStateResult(delegate {
                            if (!IsDisposed && epoch == overviewActivityEpoch && gameMode.Enabled && gameMode.IsActive
                                && gameMode.GuardActivitySession == session && snapshot != null && snapshot.Session == session)
                                PresentOverviewGuardActivity(snapshot);
                        });
                    }
                    catch (Exception) { }
                    finally { Interlocked.Exchange(ref overviewActivityPending, 0); }
                });
            }
            catch (InvalidOperationException) { Interlocked.Exchange(ref overviewActivityPending, 0); }
        }

    }
}
