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
                int warnings;
                DiagnosticSummary.CountIssues(Logger.Tail(220),out warnings,out overviewErrors);
            }
            string last = GameMode.LastSessionBrief;
            lblOverviewRuntime.Text = gameMode.IsActive ? gameMode.BoostStatusText
                : string.IsNullOrEmpty(last) ? gameMode.StatusText : Lang.F("workflow.game.last",last);
            string text;
            var color = Theme.Dim;
            overviewAction = "";
            if (!elevated) { text = Lang.T("workflow.attention.admin"); overviewAction = "admin"; color = Theme.Warning; }
            else if (gameMode.ProfileStoreSaveFailed)
            { text = Lang.T("workflow.attention.save"); overviewAction = "library"; color = Theme.Danger; }
            else if (!gameMode.Enabled)
            { text = Lang.T("workflow.attention.off"); }
            else if (overviewErrors > 0)
            {
                text = Lang.F("workflow.attention.logs",overviewErrors);
                overviewAction = "logs"; color = Theme.Danger;
            }
            else text = Lang.T("workflow.attention.none");
            lblOverviewAttention.Text = text;
            lblOverviewAttention.ForeColor = color;
            bool needsAction = overviewAction.Length > 0 && overviewAction != "logs";
            btnOverviewAction.Text = needsAction ? Lang.T("workflow.attention.action." + overviewAction) : "";
            btnOverviewAction.Kind = BtnKind.Normal;
            btnOverviewAction.Visible = needsAction;
            if (btnOverviewLogs != null && !btnOverviewLogs.IsDisposed && btnOverviewAutomaticActions != null)
            {
                int gap = Theme.S(OverviewButtonGap), width = Theme.S(OverviewButtonWidth);
                int right = btnOverviewLogs.Parent.ClientSize.Width - Theme.S(OverviewPadding);
                int left = right - width * 2 - gap;
                btnOverviewAutomaticActions.SetBounds(left, Theme.S(332), width, Theme.S(34));
                btnOverviewLogs.SetBounds(right - width, Theme.S(332), width, Theme.S(34));
            }
        }
    }
}
