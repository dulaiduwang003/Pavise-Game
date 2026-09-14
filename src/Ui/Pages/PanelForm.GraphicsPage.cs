// @author bdth 2074055628@qq.com
// File purpose Build the GPU page: per-game driver items and global presentation path switches
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace PaviseApp
{
    internal partial class PanelForm
    {
        private Toggle swNvMax;
        private Toggle swNvVrr;
        private Toggle swNvRebar;
        private Toggle swNvSmooth, swNvShader;
        private Toggle swGpuPower;
        private Toggle swAmdAlag, swAmdAfmf, swAmdRsr;
        private TierPicker dlssPicker, nvllPicker;
        private TechTabs gfxTabs;
        private DBPanel[] gfxTabPanels;
        private readonly List<Action> graphicsSync = new List<Action>();

        private void BuildGraphicsPage()
        {
            graphicsSync.Clear();
            int y = PageHeader(pageGraphics, Lang.T("nav.graphics"), Lang.T("v16.graphics.sub"), 2);

            bool nvOk = NvApi.Available;
            bool gfxOk = nvOk || AdlxTweaks.Available || IntelGraphicsTweaks.HasAvailable;
            var gfxBanner = new ModuleBanner();
            gfxBanner.SetBounds(Theme.S(ContentX), Theme.S(y), Theme.S(ContentW), Theme.S(72));
            gfxBanner.Code = "GRAPHICS DRIVER // 04";
            gfxBanner.TitleText = Lang.T("nav.graphics");
            gfxBanner.Detail = gfxOk ? Lang.T("v16.graphics.sub") : Lang.T("set.gfx.commononly");
            gfxBanner.State = gfxOk ? "DRIVER API READY" : "WINDOWS SETTINGS";
            gfxBanner.StateColor = gfxOk ? Theme.Green : Theme.Dim;
            gfxBanner.Glyph = "gpu";
            pageGraphics.Controls.Add(gfxBanner);
            y += 84;

            gfxTabs = new TechTabs();
            gfxTabs.SetBounds(Theme.S(ContentX), Theme.S(y), Theme.S(ContentW), Theme.S(38));
            gfxTabs.SetTabs(
                new[] { Lang.T("gfx.common"), "NVIDIA", "AMD", "Intel" },
                new[] { Lang.T("gfx.common.sub"), Lang.T("sec.pergame"),
                    Lang.T("sec.amd"), Lang.T("sec.intel") });
            pageGraphics.Controls.Add(gfxTabs);
            y += 48;

            gfxTabPanels = MakeTabPanels(pageGraphics, gfxTabs, 4, y);
            BuildCommonGraphicsPage(gfxTabPanels[0]);
            BuildIntelGraphicsPage(gfxTabPanels[3]);

            Control scroll = gfxTabPanels[1];
            int sy = 2, cardH;

            string nvNone = Lang.T("set.nv.none");

            swNvMax = MakeSwitch(gameMode.NvMaxPerf, null);
            BindGraphicsToggle(swNvMax, delegate { return gameMode.NvMaxPerf; },
                delegate(bool v) { gameMode.NvMaxPerf = v; }, delegate { return NvApi.Available; }, null,
                delegate { return NvApi.Available; });
            MakeAutoCard(scroll, 6, sy, ScrollContentW, 76, Lang.T("set.nvmax"),
                nvOk ? Lang.T("set.nvmax.n") : nvNone, swNvMax, out cardH);
            sy += cardH + 8;

            // Only has something to add when the user already enabled G-SYNC and only for fullscreen; not forced by tier, windowed VRR flickers on some panels
            swNvVrr = MakeSwitch(gameMode.NvVrrWindowedEnabled, null);
            BindGraphicsToggle(swNvVrr, delegate { return gameMode.NvVrrWindowedEnabled; },
                delegate(bool v) { gameMode.NvVrrWindowedEnabled = v; }, NvVrrWindowed.Applicable, null,
                delegate { return NvApi.Available; });
            MakeAutoCard(scroll, 6, sy, ScrollContentW, 76, Lang.T("set.nvvrr"),
                !nvOk ? nvNone : NvVrrWindowed.Applicable() || gameMode.NvVrrWindowedEnabled
                    ? Lang.T("set.nvvrr.n") : Lang.T("set.nvvrr.na"),
                swNvVrr, out cardH);
            sy += cardH + 8;

            nvllPicker = new TierPicker();
            nvllPicker.Size = new Size(Theme.S(270), Theme.S(28));
            nvllPicker.Labels = new[] { Lang.T("frl.off"), Lang.T("nvll.on"), Lang.T("nvll.ultra") };
            nvllPicker.Index = NvllIndexOf(gameMode.NvLowLatMode);
            BindGraphicsPicker(nvllPicker, delegate { return NvllIndexOf(gameMode.NvLowLatMode); },
                delegate(int i) { gameMode.NvLowLatMode = NvllModeOf(i); }, delegate { return NvApi.Available; });
            MakeAutoCard(scroll, 6, sy, ScrollContentW, 76, Lang.T("set.nvll"),
                nvOk ? Lang.T("set.nvll.n") : nvNone, nvllPicker, out cardH);
            sy += cardH + 8;

            bool smoothGpu = NvDrsTweaks.SmoothMotionGpuCapable();
            bool smoothOk = nvOk && NvDrsTweaks.SmoothMotionSupported();
            swNvSmooth = MakeSwitch(gameMode.NvSmoothMotion, null);
            BindGraphicsToggle(swNvSmooth, delegate { return gameMode.NvSmoothMotion; },
                delegate(bool v) { gameMode.NvSmoothMotion = v; }, NvDrsTweaks.SmoothMotionSupported);
            string smoothDesc = !nvOk ? nvNone
                : smoothOk ? Lang.T("set.nvsmooth.n")
                : !smoothGpu ? Lang.T("set.nvsmooth.nogpu")
                : Lang.F("set.nvsmooth.old", NvDrsTweaks.FormatDriver(NvDrsTweaks.SmoothMotionMinDriver()));
            MakeAutoCard(scroll, 6, sy, ScrollContentW, 76, Lang.T("set.nvsmooth"), smoothDesc, swNvSmooth, out cardH);
            sy += cardH + 8;

            swNvShader = MakeSwitch(gameMode.NvShaderCacheMax, null);
            BindGraphicsToggle(swNvShader, delegate { return gameMode.NvShaderCacheMax; },
                delegate(bool v) { gameMode.NvShaderCacheMax = v; }, delegate { return NvApi.Available; }, null,
                delegate { return NvApi.Available; });
            MakeAutoCard(scroll, 6, sy, ScrollContentW, 76, Lang.T("set.nvshader"),
                nvOk ? Lang.T("set.nvshader.n") : nvNone, swNvShader, out cardH);
            sy += cardH + 8;

            bool dlssGpu = NvDrsTweaks.DlssGpuCapable();
            bool dlssOk = nvOk && dlssGpu && NvDrsTweaks.DlssDriverSupported();
            dlssPicker = new TierPicker();
            dlssPicker.Size = new Size(Theme.S(270), Theme.S(28));
            dlssPicker.Labels = new[] { Lang.T("frl.off"), Lang.T("dlss.latest"), "J", "K" };
            dlssPicker.Index = DlssIndexOf(gameMode.NvDlssMode);
            BindGraphicsPicker(dlssPicker, delegate { return DlssIndexOf(gameMode.NvDlssMode); },
                delegate(int i) { gameMode.NvDlssMode = DlssModeOf(i); }, NvDrsTweaks.DlssOverrideSupported);
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
            BindGraphicsToggle(swNvRebar, delegate { return gameMode.NvRebar; },
                delegate(bool v) { gameMode.NvRebar = v; }, delegate { return NvApi.Available && rebarUsable; });
            MakeAutoCard(scroll, 6, sy, ScrollContentW, 76, Lang.T("set.nvrebar"),
                nvOk ? rebarDesc : nvNone, swNvRebar, out cardH);
            sy += cardH + 8;

            int nvTabBottom = sy;
            scroll = gfxTabPanels[2]; sy = 2;

            bool amdOk = AdlxTweaks.Available;
            string amdNone = Lang.T("set.amd.none");
            string amdNoSup = Lang.T("set.amd.nosup");

            bool alagOk = amdOk && AdlxTweaks.AntiLagSupported();
            swAmdAlag = MakeSwitch(gameMode.AmdAntiLag, null);
            BindGraphicsToggle(swAmdAlag, delegate { return gameMode.AmdAntiLag; },
                delegate(bool v) { gameMode.AmdAntiLag = v; },
                delegate { return AdlxTweaks.Available && AdlxTweaks.AntiLagSupported(); }, null,
                delegate { return AdlxTweaks.Available && AdlxTweaks.AntiLagSupported(); });
            MakeAutoCard(scroll, 6, sy, ScrollContentW, 76, Lang.T("set.amdalag"),
                !amdOk ? amdNone : alagOk ? Lang.T("set.amdalag.n") : amdNoSup, swAmdAlag, out cardH);
            sy += cardH + 8;

            bool afmfOk = amdOk && AdlxTweaks.AfmfSupported();
            swAmdAfmf = MakeSwitch(gameMode.AmdAfmf, null);
            BindGraphicsToggle(swAmdAfmf, delegate { return gameMode.AmdAfmf; },
                delegate(bool v) { gameMode.AmdAfmf = v; },
                delegate { return AdlxTweaks.Available && AdlxTweaks.AfmfSupported(); });
            MakeAutoCard(scroll, 6, sy, ScrollContentW, 76, Lang.T("set.amdafmf"),
                !amdOk ? amdNone : afmfOk ? Lang.T("set.amdafmf.n") : amdNoSup, swAmdAfmf, out cardH);
            sy += cardH + 8;

            bool rsrOk = amdOk && AdlxTweaks.RsrSupported();
            swAmdRsr = MakeSwitch(gameMode.RsrUpscale, null);
            BindGraphicsToggle(swAmdRsr, delegate { return gameMode.RsrUpscale; },
                delegate(bool v) { gameMode.RsrUpscale = v; },
                delegate { return AdlxTweaks.Available && AdlxTweaks.RsrSupported(); },
                delegate
                {
                    return PaviseDialog.Confirm(this, Lang.T("set.rsr"), Lang.T("rsr.warn"), DlgKind.Warn);
                });
            MakeAutoCard(scroll, 6, sy, ScrollContentW, 76, Lang.T("set.rsr"),
                !amdOk ? amdNone : rsrOk ? Lang.T("set.rsr.n") : amdNoSup, swAmdRsr, out cardH);
            sy += cardH + 8;

            // Power limit is supported by both vendors; hangs at the bottom of whichever vendor tab has a driver
            Control powerScroll = nvOk ? gfxTabPanels[1] : gfxTabPanels[2];
            int powerY = nvOk ? nvTabBottom : sy;
            bool powerOk = GpuPowerMax.Supported();
            swGpuPower = MakeSwitch(gameMode.GpuPowerLift, null);
            BindGraphicsToggle(swGpuPower, delegate { return gameMode.GpuPowerLift; },
                delegate(bool v) { gameMode.GpuPowerLift = v; },
                delegate { powerOk = GpuPowerMax.Supported(); return powerOk; }, null,
                delegate { return powerOk; });
            MakeAutoCard(powerScroll, 6, powerY, ScrollContentW, 76, Lang.T("set.gpupower"),
                powerOk ? Lang.T("set.gpupower.n") : Lang.T("set.gpupower.nosup"), swGpuPower, out cardH);

            EnableCardCollapse(gfxTabPanels[1]);
            EnableCardCollapse(gfxTabPanels[2]);
        }

        private void BindGraphicsToggle(Toggle toggle, Func<bool> read, Action<bool> write,
            Func<bool> supported, Func<bool> confirm = null)
        {
            BindGraphicsToggle(toggle, read, write, supported, confirm, supported);
        }

        // The isolated regression finds this method by reflection on its six-parameter signature; do not change the parameter count
        private void BindGraphicsToggle(Toggle toggle, Func<bool> read, Action<bool> write,
            Func<bool> supported, Func<bool> confirm, Func<bool> presentationSupported)
        {
            Action sync = delegate
            {
                // Two lock kinds share one label set site-wide: Not supported here and Not applicable here; everything else unlabeled
                //   The card is created after the switch, so take it from Parent and sync once more the moment it is attached to the parent container
                SettingCard card = toggle.Parent as SettingCard;
                bool on = read();
                bool usable = presentationSupported();
                toggle.SetSilently(on);
                toggle.Enabled = on || usable;
                if (card != null) card.SetLock(!on && !usable ? Lang.T("lock.na") : "", false);
            };
            // The isolated regression builds the common GPU page alone on an uninitialized form; field initializers never ran, so the sync table may be null
            if (graphicsSync != null) graphicsSync.Add(sync);
            toggle.ParentChanged += delegate { sync(); };
            toggle.CheckedChanged += delegate
            {
                bool on = toggle.Checked;
                // A lost capability must still leave a way to turn off; re-check after the modal warning too
                // A clickable control does not mean authorization was granted
                bool allowed = !on;
                if (on && supported())
                    allowed = confirm == null || read() || (confirm() && supported());
                if (allowed) write(on);
                sync();
            };
            sync();
        }

        // There used to be an overload that pinned the index for the Extreme tier; with the Extreme tier retired it had no callers, removed along with the lock branch
        //   Re-sync and lock clearing on parent attach now match across both pickers, same shape as BindGraphicsToggle
        private void BindGraphicsPicker(TierPicker picker, Func<int> read, Action<int> write,
            Func<bool> supported)
        {
            Action sync = delegate
            {
                SettingCard card = picker.Parent as SettingCard;
                int index = read();
                picker.Index = index;
                picker.Enabled = index != 0 || supported();
                if (card != null) card.SetLock("", false);
            };
            if (graphicsSync != null) graphicsSync.Add(sync);
            picker.ParentChanged += delegate { sync(); };
            picker.IndexChanged = delegate(int index)
            {
                // All vendor pickers use index 0 for off; a picker that was previously on
                // keeps refusing the other tiers once driver support is lost
                if (index == 0 || (index > 0 && index < picker.Labels.Length && supported()))
                    write(index);
                sync();
            };
            sync();
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
            SyncCommonGraphicsToggles();
            SyncIntelGraphicsToggles();
            if (graphicsSync != null) foreach (Action sync in graphicsSync) sync();
        }
    }
}
