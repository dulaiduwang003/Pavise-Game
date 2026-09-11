// @author bdth 2074055628@qq.com
// 文件用途 构建核心调度独立页面 一张选核图加游戏独占开关
using System;

using System.Drawing;
using System.Windows.Forms;
using Microsoft.Win32;

namespace PaviseApp
{
    internal partial class PanelForm
    {
        private CoreSchedulingPanel coreSchedulingPanel;
        private DBPanel coreScrollPanel;

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
            string line = Lang.F("core.topo.line", cores.Length,
                CpuTopology.CountSetBits(CpuTopology.AllMask), smt);
            // 拓扑读数自相矛盾时保存的方案随时会失效 摆在标题行上 别只写进日志
            return CpuTopology.TopologySourcesAgree && !CpuTopology.AllMaskReconciled
                ? line : line + Lang.T("core.topo.split");
        }

        private void BuildCoreSchedulingPage()
        {
            int y = PageHeader(pageCoreScheduling, Lang.T("nav.corescheduling"),
                Lang.T("schedule.page.sub"), 2);
            var banner = new ModuleBanner();
            banner.SetBounds(Theme.S(ContentX), Theme.S(y), Theme.S(ContentW), Theme.S(72));
            banner.Code = "CORE SCHEDULING // 10";
            banner.TitleText = CpuDisplayName();
            banner.Detail = TopologyLine();
            banner.Glyph = "chip";
            pageCoreScheduling.Controls.Add(banner);
            y += 84;

            // 分配与独占合成一页 选核只选一次 独占是它的附加项
            coreScrollPanel = new DBPanel();
            coreScrollPanel.SetBounds(Theme.S(20), Theme.S(y), Theme.S(PageW - 40), Theme.S(PageH - y - 8));
            coreScrollPanel.BackColor = Theme.Bg; coreScrollPanel.AutoScroll = true;
            Native.Dark(coreScrollPanel);
            pageCoreScheduling.Controls.Add(coreScrollPanel);

            coreSchedulingPanel = new CoreSchedulingPanel(ScrollContentW - 16, null,
                delegate(CoreSchedulingPlan plan, string expected, string profileToken, bool follow)
                { return gameMode.SaveCoreScheduling(plan, expected, null, false, null); },
                delegate { return gameMode.IsActive; });
            coreSchedulingPanel.HardAffinityState = delegate { return gameMode.HardAffinityOn; };
            coreSchedulingPanel.HardAffinityChanged = delegate(bool on) { gameMode.HardAffinityOn = on; };
            coreSchedulingPanel.AffinityGuardState = delegate { return gameMode.AffinityGuardOn; };
            coreSchedulingPanel.IsolationBlocked = delegate { return gameMode.ActivePreset == PerformancePreset.Handheld; };
            coreSchedulingPanel.AffinityGuardChanged = delegate(bool on) { gameMode.AffinityGuardOn = on; };
            coreSchedulingPanel.RefreshView();
            coreSchedulingPanel.Location = new Point(Theme.S(14), Theme.S(4));
            coreScrollPanel.Controls.Add(coreSchedulingPanel);
        }

        private void SyncCorePage()
        {
            if (coreSchedulingPanel != null && !coreSchedulingPanel.IsDisposed)
                coreSchedulingPanel.RefreshView();
        }
    }
}
