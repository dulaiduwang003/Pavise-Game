using System;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PaviseApp
{
    internal partial class PanelForm
    {
        private async void ExportLogFile(object sender, EventArgs e)
        {
            Control button = sender as Control;
            bool diagnostics = logDiagnostics;
            string session = gameMode == null ? "" : gameMode.AutomationSession;
            string title = Lang.T(diagnostics ? "log.export.diagnostics.title" : "log.export.actions.title");
            try
            {
                string destination;
                using (var dialog = new SaveFileDialog())
                {
                    dialog.Title = title;
                    dialog.Filter = Lang.T("log.export.filter");
                    dialog.DefaultExt = "log";
                    dialog.AddExtension = true;
                    dialog.OverwritePrompt = true;
                    dialog.CheckPathExists = true;
                    dialog.RestoreDirectory = true;
                    dialog.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                    dialog.FileName = LogExportFileName(diagnostics, DateTime.Now);
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                    destination = dialog.FileName;
                }
                if (button != null) button.Enabled = false;
                await Task.Run(delegate { ExportLogSnapshot(destination, diagnostics, session); });
                if (!IsDisposed && !Disposing)
                    PaviseDialog.Info(this, title, Lang.T(diagnostics ? "log.export.diagnostics.saved" : "log.export.actions.saved") + "\r\n" + destination);
            }
            catch (Exception error)
            {
                if (!IsDisposed && !Disposing)
                    PaviseDialog.Warn(this, title, Lang.T("log.export.failed") + "\r\n" + error.Message);
            }
            finally
            {
                if (button != null && !button.IsDisposed) button.Enabled = true;
            }
        }

        private static string LogExportFileName(bool diagnostics, DateTime time)
        {
            return (diagnostics ? "Pavise-Diagnostics-" : "Pavise-Actions-") + time.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".log";
        }

        private static void ExportLogSnapshot(string destination, bool diagnostics, string session)
        {
            if (diagnostics) Logger.ExportTo(destination);
            else Logger.ExportActionsTo(destination, session);
        }
    }
}
