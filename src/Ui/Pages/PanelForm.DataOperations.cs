using System;
using System.Drawing;
using System.Windows.Forms;

namespace PaviseApp
{
    internal partial class PanelForm
    {
        private DataOperationProgressCard dataOperationPanel;

        private void ShowDataOperationProgress(string message)
        {
            if (root == null || IsDisposed) return;
            if (dataOperationPanel == null)
            {
                dataOperationPanel = new DataOperationProgressCard();
                Controls.Add(dataOperationPanel);
                Resize += delegate { PositionDataOperationProgress(); };
            }
            dataOperationPanel.Status.Text = message;
            PositionDataOperationProgress();
            dataOperationPanel.BringToFront();
        }

        private void PositionDataOperationProgress()
        {
            if (dataOperationPanel == null || dataOperationPanel.IsDisposed) return;
            dataOperationPanel.Location = new Point(Math.Max(0, (ClientSize.Width - dataOperationPanel.Width) / 2),
                Math.Max(0, (ClientSize.Height - dataOperationPanel.Height) / 2));
        }
    }

    internal sealed class DataOperationProgressCard : DBPanel
    {
        internal readonly Label Status;

        internal DataOperationProgressCard()
        {
            BackColor = Theme.Card;
            BorderStyle = BorderStyle.FixedSingle;
            Size = new Size(Theme.S(560), Theme.S(178));
            var title = new Label { Name = "title", Text = Lang.T("data.operation.title"), ForeColor = Theme.Fg,
                BackColor = Color.Transparent, Font = Theme.UI(16, true) };
            title.SetBounds(Theme.S(24), Theme.S(20), Theme.S(510), Theme.S(34));
            Status = new Label { Name = "phase", ForeColor = Theme.Warning, BackColor = Color.Transparent,
                Font = Theme.UI(11, false), AutoEllipsis = true };
            Status.SetBounds(Theme.S(24), Theme.S(64), Theme.S(510), Theme.S(52));
            var note = new Label { Name = "note", Text = Lang.T("data.operation.note"), ForeColor = Theme.Faint,
                BackColor = Color.Transparent, Font = Theme.UI(9, false) };
            note.SetBounds(Theme.S(24), Theme.S(127), Theme.S(510), Theme.S(30));
            Controls.Add(title); Controls.Add(Status); Controls.Add(note);
        }
    }
}
