// @author bdth 2074055628@qq.com
// 文件用途 构建显卡页 逐游戏的驱动项与全局呈现路径开关
using System;
using System.Drawing;
using System.Windows.Forms;

namespace PaviseApp
{
    internal partial class PanelForm
    {
        private Toggle swNvMax;
        private Toggle swNvRebar;
        private Toggle swNvSmooth, swNvShader;
        private Toggle swGpuPower;
        private TierPicker dlssPicker, nvllPicker;
        private TechTabs gfxTabs;
        private DBPanel[] gfxTabPanels;

        private void BuildGraphicsPage()
        {
            int y = PageHeader(pageGraphics, Lang.T("nav.graphics"), Lang.T("v16.graphics.sub"), 2);

            bool nvOk = NvApi.Available;
            var gfxBanner = new ModuleBanner();
            gfxBanner.SetBounds(Theme.S(ContentX), Theme.S(y), Theme.S(ContentW), Theme.S(72));
            gfxBanner.Code = "GRAPHICS DRIVER // 04";
            gfxBanner.TitleText = Lang.T("nav.graphics");
            gfxBanner.Detail = nvOk ? Lang.T("v16.graphics.sub") : Lang.T("set.nv.none");
            gfxBanner.State = nvOk ? "DRIVER API READY" : "DRIVER API OFFLINE";
            gfxBanner.StateColor = nvOk ? Theme.Green : Theme.Danger;
            gfxBanner.Glyph = "gpu";
            pageGraphics.Controls.Add(gfxBanner);
            y += 84;

            gfxTabs = new TechTabs();
            gfxTabs.SetBounds(Theme.S(ContentX), Theme.S(y), Theme.S(ContentW), Theme.S(38));
            gfxTabs.SetTabs(
                new[] { "NVIDIA" },
                new[] { Lang.T("sec.pergame"), Lang.T("sec.amd") });
            pageGraphics.Controls.Add(gfxTabs);
            y += 48;

            gfxTabPanels = MakeTabPanels(pageGraphics, gfxTabs, 1, y);

            Control scroll = gfxTabPanels[0];
            int sy = 2, cardH;

            string nvNone = Lang.T("set.nv.none");

            bool hybridGpu = GpuPrefStage.Supported;
            var swGpuPref = MakeSwitch(hybridGpu && gameMode.GpuPrefStageOn, null);
            swGpuPref.CheckedChanged += (s, e) => gameMode.GpuPrefStageOn = swGpuPref.Checked;
            swGpuPref.Enabled = hybridGpu;
            MakeAutoCard(scroll, 6, sy, ScrollContentW, 76, Lang.T("set.gpupref"),
                hybridGpu ? Lang.T("set.gpupref.n") : Lang.T("set.gpupref.single"), swGpuPref, out cardH);
            sy += cardH + 8;

            swNvMax = MakeSwitch(gameMode.NvMaxPerf, null);
            swNvMax.CheckedChanged += (s, e) => gameMode.NvMaxPerf = swNvMax.Checked;
            swNvMax.Enabled = nvOk;
            MakeAutoCard(scroll, 6, sy, ScrollContentW, 76, Lang.T("set.nvmax"),
                nvOk ? Lang.T("set.nvmax.n") : nvNone, swNvMax, out cardH);
            sy += cardH + 8;

            nvllPicker = new TierPicker();
            nvllPicker.Size = new Size(Theme.S(270), Theme.S(28));
            nvllPicker.Labels = new[] { Lang.T("frl.off"), Lang.T("nvll.on"), Lang.T("nvll.ultra") };
            nvllPicker.Index = NvllIndexOf(gameMode.NvLowLatMode);
            nvllPicker.IndexChanged = delegate(int i) { gameMode.NvLowLatMode = NvllModeOf(i); };
            nvllPicker.Enabled = nvOk;
            MakeAutoCard(scroll, 6, sy, ScrollContentW, 76, Lang.T("set.nvll"),
                nvOk ? Lang.T("set.nvll.n") : nvNone, nvllPicker, out cardH);
            sy += cardH + 8;

            bool smoothGpu = NvDrsTweaks.SmoothMotionGpuCapable();
            bool smoothOk = nvOk && NvDrsTweaks.SmoothMotionSupported();
            swNvSmooth = MakeSwitch(gameMode.NvSmoothMotion, null);
            swNvSmooth.CheckedChanged += (s, e) => gameMode.NvSmoothMotion = swNvSmooth.Checked;
            swNvSmooth.Enabled = smoothOk;
            string smoothDesc = !nvOk ? nvNone
                : smoothOk ? Lang.T("set.nvsmooth.n")
                : !smoothGpu ? Lang.T("set.nvsmooth.nogpu")
                : Lang.F("set.nvsmooth.old", NvDrsTweaks.FormatDriver(NvDrsTweaks.SmoothMotionMinDriver()));
            MakeAutoCard(scroll, 6, sy, ScrollContentW, 76, Lang.T("set.nvsmooth"), smoothDesc, swNvSmooth, out cardH);
            sy += cardH + 8;

            swNvShader = MakeSwitch(gameMode.NvShaderCacheMax, null);
            swNvShader.CheckedChanged += (s, e) => gameMode.NvShaderCacheMax = swNvShader.Checked;
            swNvShader.Enabled = nvOk;
            MakeAutoCard(scroll, 6, sy, ScrollContentW, 76, Lang.T("set.nvshader"),
                nvOk ? Lang.T("set.nvshader.n") : nvNone, swNvShader, out cardH);
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
            bool rebarUsable = true;
            if (nvOk)
            {
                bool rebarOn, rebarNvidia;
                ulong rebarWindow;
                string rebarGpu;
                if (RebarProbe.TryDetect(out rebarOn, out rebarWindow, out rebarGpu, out rebarNvidia)
                    && rebarNvidia)
                {
                    rebarDesc += Lang.F(rebarOn ? "set.nvrebar.det.on" : "set.nvrebar.det.off",
                        RebarProbe.WindowText(rebarWindow));
                    rebarUsable = rebarOn;
                }
            }
            swNvRebar = MakeSwitch(gameMode.NvRebar, null);
            swNvRebar.CheckedChanged += (s, e) => gameMode.NvRebar = swNvRebar.Checked;
            swNvRebar.Enabled = nvOk && (rebarUsable || gameMode.NvRebar);
            MakeAutoCard(scroll, 6, sy, ScrollContentW, 76, Lang.T("set.nvrebar"),
                nvOk ? rebarDesc : nvNone, swNvRebar, out cardH);
            sy += cardH + 8;

            int nvTabBottom = sy;

            bool powerOk = GpuPowerMax.Supported();
            swGpuPower = MakeSwitch(gameMode.GpuPowerLift, null);
            swGpuPower.CheckedChanged += delegate { gameMode.GpuPowerLift = swGpuPower.Checked; };
            swGpuPower.Enabled = powerOk;
            MakeAutoCard(scroll, 6, nvTabBottom, ScrollContentW, 76, Lang.T("set.gpupower"),
                powerOk ? Lang.T("set.gpupower.n") : Lang.T("set.gpupower.nosup"), swGpuPower, out cardH);

            EnableCardCollapse(gfxTabPanels[0]);
        }

        internal static int NvllIndexOf(string mode)
        {
            return mode == "on" ? 1 : mode == "ultra" ? 2 : 0;
        }

        internal static string NvllModeOf(int index)
        {
            return index == 1 ? "on" : index == 2 ? "ultra" : "off";
        }

        internal static int DlssIndexOf(string mode)
        {
            return mode == "latest" ? 1 : mode == "j" ? 2 : mode == "k" ? 3 : 0;
        }

        internal static string DlssModeOf(int index)
        {
            return index == 1 ? "latest" : index == 2 ? "j" : index == 3 ? "k" : "off";
        }

        private void SyncGraphicsToggles()
        {
            if (swNvMax != null) swNvMax.SetSilently(gameMode.NvMaxPerf);
            if (nvllPicker != null) nvllPicker.Index = NvllIndexOf(gameMode.NvLowLatMode);
            if (swNvSmooth != null) swNvSmooth.SetSilently(gameMode.NvSmoothMotion);
            if (swNvShader != null) swNvShader.SetSilently(gameMode.NvShaderCacheMax);
            if (dlssPicker != null) dlssPicker.Index = DlssIndexOf(gameMode.NvDlssMode);
            if (swNvRebar != null) swNvRebar.SetSilently(gameMode.NvRebar);
            if (swGpuPower != null) swGpuPower.SetSilently(gameMode.GpuPowerLift);
        }
    }
}
