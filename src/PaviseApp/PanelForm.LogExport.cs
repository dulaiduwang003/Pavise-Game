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
            try
            {
                string destination;
                using (var dialog = new SaveFileDialog())
                {
                    dialog.Title = Lang.T("log.export.title");
                    dialog.Filter = Lang.T("log.export.filter");
                    dialog.DefaultExt = "log";
                    dialog.AddExtension = true;
                    dialog.OverwritePrompt = true;
                    dialog.CheckPathExists = true;
                    dialog.RestoreDirectory = true;
                    dialog.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                    dialog.FileName = "Pavise-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".log";
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                    destination = dialog.FileName;
                }
                if (button != null) button.Enabled = false;
                await Task.Run(delegate { Logger.ExportTo(destination); });
                if (!IsDisposed && !Disposing)
                    PaviseDialog.Info(this, Lang.T("log.export.title"), Lang.T("log.export.saved") + "\r\n" + destination);
            }
            catch (Exception error)
            {
                if (!IsDisposed && !Disposing)
                    PaviseDialog.Warn(this, Lang.T("log.export.title"), Lang.T("log.export.failed") + "\r\n" + error.Message);
            }
            finally
            {
                if (button != null && !button.IsDisposed) button.Enabled = true;
            }
        }
    }
}
