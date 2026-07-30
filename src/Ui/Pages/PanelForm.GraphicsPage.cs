// @author bdth 2074055628@qq.com
// 文件用途 构建显卡页 逐游戏的驱动项与全局呈现路径开关

using System;
using System.Drawing;
using System.Windows.Forms;

namespace AegisApp
{
    internal partial class PanelForm
    {
        private Toggle swGpu, swFso, swNvMax, swWindowedOpt;
        private TierPicker frlPicker;

        private void BuildGraphicsPage()
        {
            int y = PageHeader(pageGraphics, Lang.T("nav.graphics"), Lang.T("v16.graphics.sub"), 2);

            var scroll = new DBPanel();
            scroll.SetBounds(Theme.S(20), Theme.S(y), Theme.S(PageW - 40), Theme.S(PageH - y - 8));
            scroll.BackColor = Theme.Bg;
            scroll.AutoScroll = true;
            Native.Dark(scroll);
            pageGraphics.Controls.Add(scroll);

            int sy = 2, cardH;
            Section(scroll, Lang.T("sec.pergame"), 6, sy); sy += 24;

            swGpu = MakeSwitch(gameMode.GpuHighPerf, null);
            swGpu.CheckedChanged += (s, e) => gameMode.GpuHighPerf = swGpu.Checked;
            MakeAutoCard(scroll, 6, sy, ScrollContentW, 56, Lang.T("set.gpu"), Lang.T("set.gpu.n"), swGpu, out cardH);
            sy += cardH + 8;

            swFso = MakeSwitch(gameMode.DisableFso, null);
            swFso.CheckedChanged += (s, e) => gameMode.DisableFso = swFso.Checked;
            MakeAutoCard(scroll, 6, sy, ScrollContentW, 56, Lang.T("set.fso"), Lang.T("set.fso.n"), swFso, out cardH);
            sy += cardH + 8;

            // 没有 NVIDIA 驱动时这两项整体置灰，描述换成缺少驱动的说明
            bool nvOk = NvApi.Available;
            string nvNone = Lang.T("set.nv.none");

            swNvMax = MakeSwitch(gameMode.NvMaxPerf, null);
            swNvMax.CheckedChanged += (s, e) => gameMode.NvMaxPerf = swNvMax.Checked;
            swNvMax.Enabled = nvOk;
            MakeAutoCard(scroll, 6, sy, ScrollContentW, 76, Lang.T("set.nvmax"),
                nvOk ? Lang.T("set.nvmax.n") : nvNone, swNvMax, out cardH);
            sy += cardH + 8;

            frlPicker = new TierPicker();
            // 每档 54 宽，档位数变了要同步改，否则最后那个「屏-3」会被挤窄
            frlPicker.Size = new Size(Theme.S(270), Theme.S(28));
            frlPicker.Labels = new[] { Lang.T("frl.off"), "60", "120", "240", Lang.T("frl.screen") };
            frlPicker.Index = FrlIndexOf(gameMode.NvFrlMode);
            frlPicker.IndexChanged = delegate(int i) { gameMode.NvFrlMode = FrlModeOf(i); };
            frlPicker.Enabled = nvOk;
            MakeAutoCard(scroll, 6, sy, ScrollContentW, 76, Lang.T("set.nvfrl"),
                nvOk ? Lang.T("set.nvfrl.n") : nvNone, frlPicker, out cardH);
            sy += cardH + 8;

            sy += 10;
            Section(scroll, Lang.T("sec.graphics.present"), 6, sy); sy += 24;

            swWindowedOpt = MakeSwitch(WindowedOptTweak.EnabledByAegis || WindowedOptTweak.CurrentlyOn(), OnWindowedOptToggle);
            MakeAutoCard(scroll, 6, sy, ScrollContentW, 76, Lang.T("set.winopt"), Lang.T("set.winopt.n"), swWindowedOpt, out cardH);
            sy += cardH + 8;
        }

        // 存的是档位名而不是下标，所以插入新档不会让已保存的设置错位
        internal static int FrlIndexOf(string mode)
        {
            return mode == "60" ? 1 : mode == "120" ? 2 : mode == "240" ? 3
                : mode == "screen" ? 4 : 0;
        }

        internal static string FrlModeOf(int index)
        {
            return index == 1 ? "60" : index == 2 ? "120" : index == 3 ? "240"
                : index == 4 ? "screen" : "off";
        }

        private void OnWindowedOptToggle(object s, EventArgs e)
        {
            bool ok = swWindowedOpt.Checked ? WindowedOptTweak.Enable() : WindowedOptTweak.Restore();
            if (!ok) MessageBox.Show(this, Lang.T("winopt.failed"), "Aegis", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            swWindowedOpt.SetSilently(WindowedOptTweak.EnabledByAegis || WindowedOptTweak.CurrentlyOn());
        }

        private void SyncGraphicsToggles()
        {
            if (swGpu != null) swGpu.SetSilently(gameMode.GpuHighPerf);
            if (swFso != null) swFso.SetSilently(gameMode.DisableFso);
            if (swNvMax != null) swNvMax.SetSilently(gameMode.NvMaxPerf);
            if (frlPicker != null) frlPicker.Index = FrlIndexOf(gameMode.NvFrlMode);
            if (swWindowedOpt != null)
                swWindowedOpt.SetSilently(WindowedOptTweak.EnabledByAegis || WindowedOptTweak.CurrentlyOn());
        }
    }
}
