// @author bdth 2074055628@qq.com
// 文件用途 构建手动游戏选核与可展开的核心隔离设置页
using System;

using System.Drawing;
using System.Windows.Forms;
using Microsoft.Win32;

namespace PaviseApp
{
    internal partial class PanelForm
    {
        private CoreSchedulingPanel coreSchedulingPanel;

#if PAVISE_SELFTEST
        internal static string CpuNameOverride;
#endif

        private static string CpuDisplayName()
        {
#if PAVISE_SELFTEST
            if (!string.IsNullOrEmpty(CpuNameOverride)) return CpuNameOverride;
#endif
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(
                    @"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
                {
                    string name = key == null ? null : key.GetValue("ProcessorNameString") as string;
                    if (!string.IsNullOrEmpty(name))
                        return System.Text.RegularExpressions.Regex.Replace(name, @"\s+", " ").Trim();
                }
            }
            catch { }
            return Lang.T("core.topo.unknown");
        }

        private static string TopologyLine()
        {
            ulong[] cores = CpuTopology.PhysicalCoreMasks();
            int smt = 0;
            foreach (ulong c in cores)
                if (CpuTopology.CountSetBits(c & CpuTopology.AllMask) > 1) smt++;
            return Lang.F("core.topo.line", cores.Length,
                CpuTopology.CountSetBits(CpuTopology.AllMask), smt);
        }

        private void BuildCorePage(Control panel)
        {
            RoundPanel topo = MakeConsolePanel(panel, 6, 2, ScrollContentW, 62, true);
            CardLabel(topo, CpuDisplayName(), 16, 10, ScrollContentW - 32, 20, 9.2f, true, Theme.Fg);
            CardLabel(topo, TopologyLine(), 16, 32, ScrollContentW - 32, 18, 7.9f, false, Theme.Dim);
            coreSchedulingPanel = new CoreSchedulingPanel(ScrollContentW - 16, null,
                delegate(CoreSchedulingPlan plan, string expected, string profileToken, bool follow)
                { return gameMode.SaveCoreScheduling(plan, expected, null, false, null); },
                delegate { return gameMode.IsActive; });
            coreSchedulingPanel.Location = new Point(Theme.S(14), Theme.S(76));
            panel.Controls.Add(coreSchedulingPanel);
            policySync.Add(SyncCorePage);
        }

        private void SyncCorePage()
        {
            if (coreSchedulingPanel != null && !coreSchedulingPanel.IsDisposed)
                coreSchedulingPanel.RefreshView();
        }
    }
}
