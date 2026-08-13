// @author bdth 2074055628@qq.com
// 文件用途 构建显卡页 逐游戏的驱动项与全局呈现路径开关

using System;
using System.Drawing;
using System.Windows.Forms;

namespace PaviseApp
{
    internal partial class PanelForm
    {
        private Toggle swGpu, swNvMax, swNvLowLat;
        private Toggle swNvRebar, swNvAnsel, swNvBatt;
        private TierPicker frlPicker, dlssPicker;

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

            bool nvOk = NvApi.Available;
            string nvNone = Lang.T("set.nv.none");

            swNvMax = MakeSwitch(gameMode.NvMaxPerf, null);
            swNvMax.CheckedChanged += (s, e) => gameMode.NvMaxPerf = swNvMax.Checked;
            swNvMax.Enabled = nvOk;
            MakeAutoCard(scroll, 6, sy, ScrollContentW, 76, Lang.T("set.nvmax"),
                nvOk ? Lang.T("set.nvmax.n") : nvNone, swNvMax, out cardH);
            sy += cardH + 8;

            swNvLowLat = MakeSwitch(gameMode.NvLowLatency, null);
            swNvLowLat.CheckedChanged += (s, e) => gameMode.NvLowLatency = swNvLowLat.Checked;
            swNvLowLat.Enabled = nvOk;
            MakeAutoCard(scroll, 6, sy, ScrollContentW, 76, Lang.T("set.nvll"),
                nvOk ? Lang.T("set.nvll.n") : nvNone, swNvLowLat, out cardH);
            sy += cardH + 8;

            frlPicker = new TierPicker();
            frlPicker.Size = new Size(Theme.S(270), Theme.S(28));
            frlPicker.Labels = new[] { Lang.T("frl.off"), "60", "120", "240", Lang.T("frl.screen") };
            frlPicker.Index = FrlIndexOf(gameMode.NvFrlMode);
            frlPicker.IndexChanged = delegate(int i) { gameMode.NvFrlMode = FrlModeOf(i); };
            frlPicker.Enabled = nvOk;
            MakeAutoCard(scroll, 6, sy, ScrollContentW, 76, Lang.T("set.nvfrl"),
                nvOk ? Lang.T("set.nvfrl.n") : nvNone, frlPicker, out cardH);
            sy += cardH + 8;

            bool dlssGpu = NvDrsTweaks.DlssGpuCapable();
            bool dlssOk = nvOk && dlssGpu && NvDrsTweaks.DlssDriverSupported();
            dlssPicker = new TierPicker();
            dlssPicker.Size = new Size(Theme.S(270), Theme.S(28));
            dlssPicker.Labels = new[] { Lang.T("frl.off"), Lang.T("dlss.latest"), "J", "K" };
            dlssPicker.Index = DlssIndexOf(gameMode.NvDlssMode);
            dlssPicker.IndexChanged = delegate(int i) { gameMode.NvDlssMode = DlssModeOf(i); };
            dlssPicker.Enabled = dlssOk;
            string dlssDesc = !nvOk ? nvNone
                : dlssOk ? Lang.T("set.nvdlss.n")
                : !dlssGpu ? Lang.T("set.nvdlss.nogpu")
                : Lang.T("set.nvdlss.old");
            MakeAutoCard(scroll, 6, sy, ScrollContentW, 76, Lang.T("set.nvdlss"), dlssDesc, dlssPicker, out cardH);
            sy += cardH + 8;

            string rebarDesc = Lang.T("set.nvrebar.n");
            if (nvOk)
            {
                bool rebarOn;
                ulong rebarWindow;
                string rebarGpu;
                if (RebarProbe.TryDetect(out rebarOn, out rebarWindow, out rebarGpu))
                    rebarDesc += Lang.F(rebarOn ? "set.nvrebar.det.on" : "set.nvrebar.det.off",
                        RebarProbe.WindowText(rebarWindow));
            }
            swNvRebar = MakeSwitch(gameMode.NvRebar, null);
            swNvRebar.CheckedChanged += (s, e) => gameMode.NvRebar = swNvRebar.Checked;
            swNvRebar.Enabled = nvOk;
            MakeAutoCard(scroll, 6, sy, ScrollContentW, 76, Lang.T("set.nvrebar"),
                nvOk ? rebarDesc : nvNone, swNvRebar, out cardH);
            sy += cardH + 8;

            swNvAnsel = MakeSwitch(gameMode.NvAnselOff, null);
            swNvAnsel.CheckedChanged += (s, e) => gameMode.NvAnselOff = swNvAnsel.Checked;
            swNvAnsel.Enabled = nvOk;
            MakeAutoCard(scroll, 6, sy, ScrollContentW, 76, Lang.T("set.nvansel"),
                nvOk ? Lang.T("set.nvansel.n") : nvNone, swNvAnsel, out cardH);
            sy += cardH + 8;

            swNvBatt = MakeSwitch(gameMode.NvBattFull, null);
            swNvBatt.CheckedChanged += (s, e) => gameMode.NvBattFull = swNvBatt.Checked;
            swNvBatt.Enabled = nvOk;
            MakeAutoCard(scroll, 6, sy, ScrollContentW, 76, Lang.T("set.nvbatt"),
                nvOk ? Lang.T("set.nvbatt.n") : nvNone, swNvBatt, out cardH);
            sy += cardH + 8;

            EnableCardCollapse(scroll);
        }

        internal static int DlssIndexOf(string mode)
        {
            return mode == "latest" ? 1 : mode == "j" ? 2 : mode == "k" ? 3 : 0;
        }

        internal static string DlssModeOf(int index)
        {
            return index == 1 ? "latest" : index == 2 ? "j" : index == 3 ? "k" : "off";
        }

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

        private void SyncGraphicsToggles()
        {
            if (swGpu != null) swGpu.SetSilently(gameMode.GpuHighPerf);
            if (swNvMax != null) swNvMax.SetSilently(gameMode.NvMaxPerf);
            if (swNvLowLat != null) swNvLowLat.SetSilently(gameMode.NvLowLatency);
            if (frlPicker != null) frlPicker.Index = FrlIndexOf(gameMode.NvFrlMode);
            if (dlssPicker != null) dlssPicker.Index = DlssIndexOf(gameMode.NvDlssMode);
            if (swNvRebar != null) swNvRebar.SetSilently(gameMode.NvRebar);
            if (swNvAnsel != null) swNvAnsel.SetSilently(gameMode.NvAnselOff);
            if (swNvBatt != null) swNvBatt.SetSilently(gameMode.NvBattFull);
        }
    }
}
