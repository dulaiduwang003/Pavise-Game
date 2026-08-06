// @author bdth 2074055628@qq.com
// 文件用途 构建全部页面并检查界面上没有漏定义的文案键

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        private static readonly Regex UntranslatedKey =
            new Regex(@"^[a-z][a-z0-9]*(\.[a-z0-9]+)*\.[a-z][a-z0-9]*(\.[a-z0-9]+)*$",
                RegexOptions.CultureInvariant);

        private static void TestNoUntranslatedKeysOnScreen()
        {
            string data = Path.Combine(Path.GetTempPath(),
                "PaviseLangCov_" + System.Diagnostics.Process.GetCurrentProcess().Id);
            Directory.CreateDirectory(data);
            string previousLog = Logger.LogPath;
            try
            {
                Logger.LogPath = Path.Combine(data, "langcov.log");
                Dpi.Init();
                Lang.Init();
                var core = new SuppressionCore();
                var tamer = new Tamer(core);
                var mode = new GameMode(data, core);
                PanelForm form;
                try { form = new PanelForm(tamer, mode, IconArt.MakeIcon(Dpi.S(24)), true); }
                catch (Exception ex) { throw new TestSkippedException("面板无法构建：" + ex.GetType().Name); }
                using (form)
                {
                    form.StartPosition = FormStartPosition.Manual;
                    form.Location = new Point(-20000, -20000);
                    GC.KeepAlive(form.Handle);

                    var offenders = new List<string>();
                    Collect(form, offenders);
                    if (offenders.Count > 0)
                        throw new Exception("界面上出现未定义的文案键："
                            + string.Join("、", offenders.ToArray()));
                }
            }
            finally
            {
                Logger.LogPath = previousLog;
                try { Directory.Delete(data, true); } catch { }
            }
        }

        private static void Collect(Control parent, List<string> offenders)
        {
            foreach (Control child in parent.Controls)
            {
                string text = child.Text;
                if (!string.IsNullOrEmpty(text) && UntranslatedKey.IsMatch(text)
                    && !offenders.Contains(text) && Lang.Row(text) == null)
                    offenders.Add(text);
                foreach (string extra in DescriptiveText(child))
                    if (!string.IsNullOrEmpty(extra) && UntranslatedKey.IsMatch(extra)
                        && !offenders.Contains(extra) && Lang.Row(extra) == null)
                        offenders.Add(extra);
                Collect(child, offenders);
            }
        }

        private static IEnumerable<string> DescriptiveText(Control control)
        {
            var card = control as SettingCard;
            if (card != null) { yield return card.Title; yield return card.Desc; }
            var empty = control as EmptyStatePanel;
            if (empty != null) { yield return empty.EmptyTitle; yield return empty.EmptyDetail; }
        }
    }
}
