using System;
using System.Windows.Forms;

namespace PaviseApp
{
    internal partial class PanelForm
    {
        private bool autoIgnoreDialogOpen;
        private void ShowAutoIgnoreDialog()
        {
            if (autoIgnoreDialogOpen || IsDisposed || Disposing) return;
            autoIgnoreDialogOpen = true;
            try
            {
                using (var dialog = new AutoIgnoreDialog(gameMode)) ShowDim(dialog);
            }
            finally { autoIgnoreDialogOpen = false; }
        }
    }
}
