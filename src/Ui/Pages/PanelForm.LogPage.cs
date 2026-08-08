// @author bdth 2074055628@qq.com
// 文件用途 构建日志页 展示运行日志并提供打开与清空

using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace PaviseApp
{
    internal partial class PanelForm
    {
        private TextBox tbLog;
        private RoundPanel logWrap;

        private void BuildLogPage()
        {
            int y = PageHeader(pageLog, Lang.T("nav.log"), Lang.T("v17.log.sub"), 2);

            logWrap = new RoundPanel();
            logWrap.SetBounds(Theme.S(ContentX), Theme.S(y), Theme.S(ContentW), Theme.S(PageH - y - 58));
            logWrap.BackColor = Theme.Bg; logWrap.Fill = Theme.Inset; logWrap.Border = Theme.Stroke;
            logWrap.Radius = Theme.S(14); logWrap.Padding = new Padding(Theme.S(14));
            tbLog = new TextBox(); tbLog.Multiline = true; tbLog.ReadOnly = true; tbLog.ScrollBars = ScrollBars.Both; tbLog.WordWrap = false;
            tbLog.BackColor = Theme.Inset; tbLog.ForeColor = Theme.Fg; tbLog.BorderStyle = BorderStyle.None; tbLog.Font = Theme.Mono(8.75f); tbLog.Dock = DockStyle.Fill;
            Native.Dark(tbLog); logWrap.Controls.Add(tbLog);

            var openLog = new PillButton(Lang.T("btn.openlog"));
            openLog.SetBounds(Theme.S(ContentX), Theme.S(PageH - 48), Theme.S(190), Theme.S(36));
            openLog.Click += delegate { OpenTextFile(Logger.LogPath); };
            var clearLog = new PillButton(Lang.T("rep.clear.log"), BtnKind.Danger);
            clearLog.SetBounds(Theme.S(ContentX + 202), Theme.S(PageH - 48), Theme.S(150), Theme.S(36));
            clearLog.Click += delegate
            {
                if (!PaviseDialog.Confirm(this, App.DisplayName, Lang.T("rep.clear.ask"), DlgKind.Warn)) return;
                Logger.Clear();
                Logger.Log("运行日志已手动清除");
                RefreshLog();
            };

            pageLog.Controls.AddRange(new Control[] { logWrap, openLog, clearLog });
            RefreshLog();
        }

        private void OpenTextFile(string path)
        {
            try { if (!File.Exists(path)) File.WriteAllText(path, "", System.Text.Encoding.UTF8); using (Process.Start(System.IO.Path.Combine(Environment.SystemDirectory, "notepad.exe"), path)) { } }
            catch { }
        }

        private void RefreshLog()
        {
            if (tbLog == null) return;
            string text = Logger.Tail(220);
            if (text.Length == 0) text = Lang.T("rep.log.none");
            if (tbLog.Text == text) return;
            tbLog.Text = text;
            try { tbLog.SelectionStart = tbLog.TextLength; tbLog.ScrollToCaret(); } catch { }
        }
    }
}
