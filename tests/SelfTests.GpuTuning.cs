// @author bdth 2074055628@qq.com
// 文件用途 显卡深度调优的纯逻辑自测 计划映射 模式往返 降级路径

using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        private static bool HasPair(List<KeyValuePair<string, uint>> desired, string key, uint value)
        {
            foreach (var item in desired)
                if (item.Key == key) return item.Value == value;
            return false;
        }

        private static bool HasKey(List<KeyValuePair<string, uint>> desired, string key)
        {
            foreach (var item in desired)
                if (item.Key == key) return true;
            return false;
        }

        private static void TestNvBuildDesired()
        {
            var plan = new NvGamePlan
            {
                MaxPerf = true, FrlFps = 141, LowLatency = true,
                AnselOff = true, Rebar = true, BattFull = true, DlssMode = "off"
            };
            var desired = NvDrsTweaks.BuildDesired(plan);
            Eq(true, HasPair(desired, NvDrsTweaks.KeyPState, NvApi.PStatePreferMax));
            Eq(true, HasPair(desired, NvDrsTweaks.KeyFrl, 141u));
            Eq(true, HasPair(desired, NvDrsTweaks.KeyPreRender, 1u));
            Eq(true, HasPair(desired, NvDrsTweaks.KeyAnsel, 0u));
            Eq(true, HasPair(desired, NvDrsTweaks.KeyRebarFeat, 1u));
            Eq(true, HasPair(desired, NvDrsTweaks.KeyRebarOpt, 1u));
            Eq(true, HasPair(desired, NvDrsTweaks.KeyRebarSize, NvApi.RebarSizeDefault));
            Eq(true, HasPair(desired, NvDrsTweaks.KeyBattFps, NvApi.BatteryFpsUncapped));
            Eq(false, HasKey(desired, NvDrsTweaks.KeyDlssOvr));

            var offPlan = NvDrsTweaks.BuildDesired(new NvGamePlan());
            Eq(0, offPlan.Count);

            var dlssK = NvDrsTweaks.BuildDesired(new NvGamePlan { DlssMode = "k" });
            var dlssLatest = NvDrsTweaks.BuildDesired(new NvGamePlan { DlssMode = "latest" });
            if (NvDrsTweaks.DlssOverrideSupported())
            {
                Eq(true, HasPair(dlssK, NvDrsTweaks.KeyDlssOvr, 1u));
                Eq(true, HasPair(dlssK, NvDrsTweaks.KeyDlssPreset, NvApi.DlssPresetK));
                Eq(true, HasPair(dlssLatest, NvDrsTweaks.KeyDlssPreset, NvApi.DlssPresetLatest));
            }
            else
            {
                Eq(0, dlssK.Count);
                Eq(0, dlssLatest.Count);
            }
        }

        private static void TestGpuModeRoundTrips()
        {
            for (int i = 0; i <= 4; i++)
                Eq(i, PanelForm.FrlIndexOf(PanelForm.FrlModeOf(i)));
            for (int i = 0; i <= 3; i++)
                Eq(i, PanelForm.DlssIndexOf(PanelForm.DlssModeOf(i)));
            Eq(240, GameMode.ResolveFrlFps("240"));
            Eq(60, GameMode.ResolveFrlFps("60"));
            Eq(0, GameMode.ResolveFrlFps("off"));
            Eq("566.14", NvDrsTweaks.FormatDriver(56614));
            Eq("未知版本", NvDrsTweaks.FormatDriver(0));

            string dir = Path.Combine(
                Path.GetTempPath(), "PaviseGpuMode_" + Process.GetCurrentProcess().Id);
            Directory.CreateDirectory(dir);
            var mode = new GameMode(dir, new SuppressionCore());
            mode.NvFrlMode = "240";
            Eq("240", mode.NvFrlMode);
            mode.NvFrlMode = "999";
            Eq("off", mode.NvFrlMode);
            mode.NvDlssMode = "k";
            Eq("k", mode.NvDlssMode);
            mode.NvDlssMode = "x";
            Eq("off", mode.NvDlssMode);
            mode.AmdChillMode = "240";
            Eq("240", mode.AmdChillMode);
        }

        private static void TestNvPlanEmpty()
        {
            Eq(true, new NvGamePlan().Empty);
            Eq(true, new NvGamePlan { DlssMode = "off" }.Empty);
            Eq(false, new NvGamePlan { AnselOff = true }.Empty);
            Eq(false, new NvGamePlan { Rebar = true }.Empty);
            Eq(false, new NvGamePlan { DlssMode = "j" }.Empty);
            Eq(false, new NvGamePlan { BattFull = true }.Empty);
            Eq(null, NvDrsTweaks.ApplyForGame(@"C:\x\game.exe", new NvGamePlan()));
        }

        private static void TestGpuThrottleSummary()
        {
            GpuThrottleProbe.Reset();
            Eq(null, GpuThrottleProbe.Summarize());
            Eq("33%", GpuThrottleProbe.Percent(1, 3));
            Eq("100%", GpuThrottleProbe.Percent(5, 5));
            Eq("0%", GpuThrottleProbe.Percent(0, 0));
        }

        private static void TestRebarProbe()
        {
            Eq(true, RebarProbe.IsDiscretePciGpu(@"PCI\VEN_10DE&DEV_1F10&SUBSYS_00000000\4&AAAA"));
            Eq(true, RebarProbe.IsDiscretePciGpu(@"PCI\VEN_1002&DEV_73BF\4&BBBB"));
            Eq(false, RebarProbe.IsDiscretePciGpu(@"PCI\VEN_8086&DEV_3E9B\3&CCCC"));
            Eq(false, RebarProbe.IsDiscretePciGpu(@"ROOT\DISPLAY\0000"));
            Eq(false, RebarProbe.IsDiscretePciGpu(null));
            Eq(false, RebarProbe.EnabledFromWindow(256UL * 1024 * 1024));
            Eq(true, RebarProbe.EnabledFromWindow(1024UL * 1024 * 1024));
            Eq("256 MB", RebarProbe.WindowText(256UL * 1024 * 1024));
            Eq("8 GB", RebarProbe.WindowText(8UL * 1024 * 1024 * 1024));

            bool enabled;
            ulong window;
            string gpu;
            if (!RebarProbe.TryDetect(out enabled, out window, out gpu))
                throw new TestSkippedException("无独显或资源表读取失败");
            if (window < 16UL * 1024 * 1024)
                throw new Exception("检测到的显存窗口小得不合理: " + window);
        }

        private static void TestAdlxDegrade()
        {
            if (AdlxTweaks.Available)
                throw new TestSkippedException("本机存在 AMD ADLX 接口，降级路径不适用");
            Eq(false, AdlxTweaks.ActivateAntiLag());
            Eq(false, AdlxTweaks.ActivateEnhancedSync());
            Eq(false, AdlxTweaks.ActivateChill(120));
            Eq(false, AdlxTweaks.ActivateRis());
            Eq(true, AdlxTweaks.RestoreAntiLag());
            Eq(true, AdlxTweaks.RestoreEnhancedSync());
            Eq(true, AdlxTweaks.RestoreChill());
            Eq(true, AdlxTweaks.RestoreRis());
            Eq(0, AdlxTweaks.ProbeWriteback().Count);
            int done;
            Eq(false, AdlxTweaks.ResetShaderCacheAll(out done));
            Eq(0, done);
        }
    }
}
