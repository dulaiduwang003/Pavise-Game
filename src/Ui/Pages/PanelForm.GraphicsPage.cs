// @author bdth 2074055628@qq.com
// 文件用途 构建显卡页 逐游戏的驱动项与全局呈现路径开关
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
                delegate { return NvApi.Available; },
                delegate { return ExtremeGraphicsForced(PolicyCatalog.KeyNvMaxPerf, NvApi.Available); });
            MakeAutoCard(scroll, 6, sy, ScrollContentW, 76, Lang.T("set.nvmax"),
                nvOk ? Lang.T("set.nvmax.n") : nvNone, swNvMax, out cardH);
            sy += cardH + 8;

            // 只在用户已开 G-SYNC 且只给全屏时有东西可补 不由档位强制 部分面板窗口 VRR 会闪
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
                delegate(int i) { gameMode.NvLowLatMode = NvllModeOf(i); }, delegate { return NvApi.Available; },
                delegate { return ExtremeGraphicsForced(PolicyCatalog.KeyNvLowLat, NvApi.Available) ? 1 : -1; });
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
                delegate { return NvApi.Available; },
                delegate { return ExtremeGraphicsForced(PolicyCatalog.KeyNvShaderCache, NvApi.Available); });
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
                delegate { return AdlxTweaks.Available && AdlxTweaks.AntiLagSupported(); },
                delegate
                {
                    return ExtremeGraphicsForced(PolicyCatalog.KeyAmdAntiLag,
                        AdlxTweaks.Available && AdlxTweaks.AntiLagSupported());
                });
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

            // 功耗墙两家都支持 挂在有驱动那家的页签底部
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

        // 显卡页极限清单项共用 全局模式为极限且解析层仍在强制这个键才显示锁定
        //   熔断进了退出集或本机根本不支持的项 解析层不强制 界面也不能挂预设强制的锁
        private bool ExtremeGraphicsForced(string key, bool supported)
        {
            return supported && gameMode.ActivePreset == PerformancePreset.Extreme
                && ExtremeMode.ForcedPolicyValue(key) != null;
        }

        private void BindGraphicsToggle(Toggle toggle, Func<bool> read, Action<bool> write,
            Func<bool> supported, Func<bool> confirm = null)
        {
            BindGraphicsToggle(toggle, read, write, supported, confirm, supported);
        }

        // 隔离回归按六参签名反射查找这个方法 极限锁定走独立的七参重载 别合并
        private void BindGraphicsToggle(Toggle toggle, Func<bool> read, Action<bool> write,
            Func<bool> supported, Func<bool> confirm, Func<bool> presentationSupported)
        {
            BindGraphicsToggle(toggle, read, write, supported, confirm, presentationSupported, null);
        }

        private void BindGraphicsToggle(Toggle toggle, Func<bool> read, Action<bool> write,
            Func<bool> supported, Func<bool> confirm, Func<bool> presentationSupported,
            Func<bool> extremeForced)
        {
            Action sync = delegate
            {
                // 三种锁定全站同一套标签 档位强制"预设强制开" 本机不支持"本机不适用" 其余无标签
                //   卡片在开关之后才创建 所以从 Parent 取 挂上父容器那一刻再同步一次
                SettingCard card = toggle.Parent as SettingCard;
                // 极限档强制的项显示锁定为开 否则开关显示用户配置值 与实际生效相反
                if (extremeForced != null && extremeForced())
                {
                    toggle.SetSilently(true);
                    toggle.Enabled = false;
                    if (card != null) card.SetLock(Lang.T("v14.preset.forced.on"), true);
                    return;
                }
                bool on = read();
                bool usable = presentationSupported();
                toggle.SetSilently(on);
                toggle.Enabled = on || usable;
                if (card != null) card.SetLock(!on && !usable ? Lang.T("lock.na") : "", false);
            };
            // 隔离回归用未初始化的窗体单独构建公共显卡页 字段初始化器没跑过 同步表可能为空
            if (graphicsSync != null) graphicsSync.Add(sync);
            toggle.ParentChanged += delegate { sync(); };
            toggle.CheckedChanged += delegate
            {
                bool on = toggle.Checked;
                // 能力丢失时仍然要留出关闭的路 模态警告之后也要再查一遍
                // 控件可点不等于已经授权
                bool allowed = !on;
                if (on && supported())
                    allowed = confirm == null || read() || (confirm() && supported());
                if (allowed) write(on);
                sync();
            };
            sync();
        }

        private void BindGraphicsPicker(TierPicker picker, Func<int> read, Action<int> write,
            Func<bool> supported)
        {
            BindGraphicsPicker(picker, read, write, supported, null);
        }

        // extremeForced 返回极限档钉住的索引 负数表示不锁 选择器锁住时卡片挂预设强制的标签
        private void BindGraphicsPicker(TierPicker picker, Func<int> read, Action<int> write,
            Func<bool> supported, Func<int> extremeForced)
        {
            Action sync = delegate
            {
                SettingCard card = picker.Parent as SettingCard;
                int forced = extremeForced != null ? extremeForced() : -1;
                if (forced >= 0)
                {
                    picker.Index = forced;
                    picker.Enabled = false;
                    if (card != null) card.SetLock(Lang.T("v14.preset.forced.on"), true);
                    return;
                }
                int index = read();
                picker.Index = index;
                picker.Enabled = index != 0 || supported();
                if (extremeForced != null && card != null) card.SetLock("", false);
            };
            if (graphicsSync != null) graphicsSync.Add(sync);
            if (extremeForced != null) picker.ParentChanged += delegate { sync(); };
            picker.IndexChanged = delegate(int index)
            {
                // 所有厂商选择器都用索引 0 表示关闭 之前开着的选择器
                // 一旦失去驱动支持 其它档位继续拒绝
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
