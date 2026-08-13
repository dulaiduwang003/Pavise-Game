// @author bdth 2074055628@qq.com
// 文件用途 清除本机数据 首次启动清旧版本 升级越过数据基线 以及设置页手动清除三条路径共用
// 铁律 必须先还原全部系统改动并确认成功再删快照 任何一项失败就整体中止

using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static class LegacyPurge
    {
        private const string DoneKey = "PurgeV180Done";
        private const string RegKey = @"Software\Pavise";

#if PAVISE_SELFTEST
        internal static Func<List<string>> RestoreHook;
        internal static bool SkipRegistryDelete;
#endif

        private static List<string> RestoreOrHook()
        {
#if PAVISE_SELFTEST
            if (RestoreHook != null) return RestoreHook();
#endif
            return RestoreEverything();
        }

        private static bool DeleteRegistryTree()
        {
#if PAVISE_SELFTEST
            if (SkipRegistryDelete) return true;
#endif
            try
            {
                using (RegistryKey parent = Registry.CurrentUser.OpenSubKey(@"Software", true))
                    if (parent != null && parent.OpenSubKey("Pavise") != null)
                        parent.DeleteSubKeyTree("Pavise", false);
                return Registry.CurrentUser.OpenSubKey(RegKey) == null;
            }
            catch { return false; }
        }

        private static readonly string[] DataFiles =
        {
            "Pavise.games.txt", "Pavise.whitelist.txt", "Pavise.targets.txt",
            "Pavise.autoignore.txt", GameProfileStore.FileName,
            "Pavise.log", "Pavise.log.old", "crash.log", "Pavise.preview.log",
            "Pavise.freeze.state", SuppressionCore.StateFileName
        };

        private static void Step(string name, Func<bool> restore, List<string> failed)
        {
            try { if (!restore()) failed.Add(name); }
            catch { failed.Add(name); }
        }

        private static void StepIf(string name, Func<bool> enabled, Func<bool> disable, List<string> failed)
        {
            try { if (enabled() && !disable()) failed.Add(name); }
            catch { failed.Add(name); }
        }

        private static void StepVoid(string name, Action restore, List<string> failed)
        {
            try { restore(); }
            catch { failed.Add(name); }
        }

        private static List<string> RestoreEverything()
        {
            var failed = new List<string>();

            Step("电源计划", PowerPlan.Restore, failed);
            Step("Pavise 建的卓越性能计划", PowerPlan.RemoveCreatedPlan, failed);
            Step("Pavise 托管电源方案", PowerPlan.RemoveManagedPlan, failed);
            Step("Windows 更新暂停", UpdatePause.Restore, failed);
            foreach (RetiredFeature f in VersionMigrations.Entries)
                Step(f.Name, f.Restore, failed);
            Step("Game DVR", GameDvr.Restore, failed);
            Step("通知免打扰", Notif.Restore, failed);
            Step("视觉效果", VisualFx.Restore, failed);
            Step("刷新率守护", DisplayGuard.Restore, failed);
            Step("息屏防护", DisplayAwake.Restore, failed);
            Step("无输入降级", PresenceQos.Restore, failed);
            Step("电源滑块", PowerOverlay.Restore, failed);
            Step("后台下载暂停", DoTweak.Restore, failed);
            Step("服务暂停", SvcPause.Restore, failed);
            Step("服务让路", SvcYield.Restore, failed);
            Step("无线扫描抑制", WlanGuard.Restore, failed);
            Step("平台时钟校正", PlatformClockTweak.Restore, failed);
            Step("网络优化", NetTweak.Restore, failed);
            Step("时间片校正", QuantumTweak.Restore, failed);
            Step("MPO", MpoTweak.Restore, failed);
            Step("VBS", VbsTweak.Restore, failed);
            Step("游戏模式守护", GameModeGuard.Restore, failed);
            Step("设备电源", DevicePowerTweak.Restore, failed);
            Step("辅助功能拦截", AccessibilityKeysTweak.Restore, failed);
            Step("键鼠设备省电", HidPowerTweak.Restore, failed);
            Step("键鼠队列校正", InputMythTweak.Restore, failed);
            Step("指针精度增强", PointerPrecisionTweak.Restore, failed);
            Step("NVIDIA 全局项", NvGlobalTweaks.Restore, failed);
            Step("后备提优 IFEO", IfeoBoost.RestoreAll, failed);

            StepIf("HAGS", delegate { return HagsTweak.EnabledByPavise; }, HagsTweak.Disable, failed);
            StepIf("GPU 中断亲和", delegate { return InterruptAffinityTweak.EnabledByPavise; }, InterruptAffinityTweak.Disable, failed);
            StepIf("USB 中断避让", delegate { return UsbInterruptAffinityTweak.HasResidue; }, UsbInterruptAffinityTweak.Disable, failed);

            foreach (string kind in new[]
            {
                NvDrsTweaks.KeyPState, NvDrsTweaks.KeyFrl, NvDrsTweaks.KeyPreRender,
                NvDrsTweaks.KeyLowLatCpl, NvDrsTweaks.KeyAnsel, NvDrsTweaks.KeyRebarFeat,
                NvDrsTweaks.KeyRebarOpt, NvDrsTweaks.KeyRebarSize, NvDrsTweaks.KeyDlssOvr,
                NvDrsTweaks.KeyDlssPreset, NvDrsTweaks.KeyBattFps
            })
            {
                string k = kind;
                StepVoid("NVIDIA Profile:" + k, delegate { NvDrsTweaks.RestoreKind(k); }, failed);
            }
            StepVoid("逐游戏 GPU 偏好", delegate { GameExeTweaks.RestoreKind("gpu"); }, failed);
            StepVoid("后台集显偏好", delegate { GameExeTweaks.RestoreKind("igpu"); }, failed);
            StepVoid("逐游戏全屏优化", delegate { GameExeTweaks.RestoreKind("fso"); }, failed);

            return failed;
        }

        public static void RunOnce(string dataDir)
        {
            if (Settings.Load(DoneKey, false)) return;

            Logger.Log("首次运行 v1.6.6 清除旧版本数据 先还原全部系统改动");

            List<string> failed = RestoreOrHook();
            if (failed.Count > 0)
            {
                Logger.Log("清除已中止 " + failed.Count + " 项未能还原 "
                    + string.Join(" ", failed.ToArray()) + " 下次启动重试");
                return;
            }

            int files = DeleteDataFiles(dataDir);
            bool regCleared = DeleteRegistryTree();

            Settings.Save(DoneKey, true);

            Logger.Log("旧版本数据已清除 系统改动已还原 删除 " + files + " 个文件"
                + (regCleared ? " 配置已重置" : " 配置未能完全清空"));
        }

        private static int DeleteDataFiles(string dataDir)
        {
            int files = 0;
            foreach (string name in DataFiles)
            {
                try
                {
                    string p = Path.Combine(dataDir, name);
                    if (File.Exists(p)) { File.Delete(p); files++; }
                }
                catch { }
            }
            return files;
        }

        public static bool WipeAll(string dataDir, bool includeSettings, string why,
            out int files, out string unrestored)
        {
            files = 0;
            unrestored = null;
            Logger.Log(why + " 先还原 Pavise 改过的全部系统项");

            List<string> failed = RestoreOrHook();
            if (failed.Count > 0)
            {
                unrestored = string.Join(" ", failed.ToArray());
                Logger.Log(why + " 已中止 " + failed.Count + " 项未能还原 " + unrestored
                    + " 没有删除任何文件");
                return false;
            }

            files = DeleteDataFiles(dataDir);
            bool regCleared = includeSettings && DeleteRegistryTree();
            Settings.Save(DoneKey, true);

            Logger.Log(why + " 完成 系统改动已还原 删除 " + files + " 个文件"
                + (includeSettings ? regCleared ? " 开关已重置" : " 开关未能完全清空" : " 开关保留"));
            return true;
        }
    }
}
