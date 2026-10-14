using System;

namespace PaviseApp
{
    internal partial class PanelForm
    {
        private PageReveal pageReveal;

        private bool PreparePageReveal(DBPanel page)
        {
            if (!IsHandleCreated || !Visible || UiClock.Frozen || UiClock.Suspended
                || introActive || introPending || outroActive || moveSizeLoop) return false;
            if (pageReveal == null || pageReveal.IsDisposed) pageReveal = new PageReveal(this);
            return pageReveal.Prepare(page as WorkspacePanel);
        }

        private void StartPageReveal()
        {
            if (pageReveal != null && !pageReveal.IsDisposed) pageReveal.Reveal();
        }

        private void StopPageReveal()
        {
            if (pageReveal != null && !pageReveal.IsDisposed) pageReveal.Cancel();
        }

        private void DisposePageReveal()
        {
            PageReveal previous = pageReveal;
            pageReveal = null;
            if (previous != null) previous.Dispose();
        }
    }
}
