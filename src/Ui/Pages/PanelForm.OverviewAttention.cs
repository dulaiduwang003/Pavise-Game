namespace PaviseApp
{
    internal partial class PanelForm
    {
        private void RefreshOverviewAttention()
        {
            if (lblOverviewAttention == null || lblOverviewAttention.IsDisposed || gameMode == null) return;
            string game = gameMode.ActiveGame ?? Lang.T("workflow.game.waiting");
            if (lblStatus.Text != game)
            {
                lblStatus.Text = game;
                FitLabelFont(lblStatus,true,StatusFontMax,StatusFontMin);
            }
            if (overviewLogVersion != Logger.Version)
            {
                overviewLogVersion = Logger.Version;
                DiagnosticSummary.CountIssues(Logger.Tail(220),out overviewWarnings,out overviewErrors);
            }
            string last = GameMode.LastSessionBrief;
            lblOverviewRuntime.Text = gameMode.IsActive ? gameMode.BoostStatusText
                : string.IsNullOrEmpty(last) ? gameMode.StatusText : Lang.F("workflow.game.last",last);
            string text;
            var color = Theme.Dim;
            overviewAction = "report";
            if (!elevated) { text = Lang.T("workflow.attention.admin"); overviewAction = "admin"; color = Theme.Warning; }
            else if (gameMode.ProfileStoreSaveFailed)
            { text = Lang.T("workflow.attention.save"); overviewAction = "library"; color = Theme.Danger; }
            else if (!gameMode.Enabled)
            { text = Lang.T("workflow.attention.off"); overviewAction = "enable"; }
            else if (gameMode.IrqObservationStatusWarning)
            { text = gameMode.IrqObservationStatusText; overviewAction = "logs"; color = Theme.Warning; }
            else if (overviewWarnings + overviewErrors > 0)
            {
                text = Lang.F("workflow.attention.logs",overviewWarnings,overviewErrors);
                overviewAction = "logs"; color = overviewErrors > 0 ? Theme.Danger : Theme.Warning;
            }
            else text = Lang.T("workflow.attention.none");
            lblOverviewAttention.Text = text;
            lblOverviewAttention.ForeColor = color;
            btnOverviewAction.Text = Lang.T("workflow.attention.action." + overviewAction);
            btnOverviewAction.Kind = overviewAction == "report" ? BtnKind.Normal : BtnKind.Primary;
        }
    }
}
