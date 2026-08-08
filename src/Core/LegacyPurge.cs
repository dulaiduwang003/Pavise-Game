// @author bdth 2074055628@qq.com
// 文件用途 v1.6.6 首次启动时清除旧版本的全部数据 回到全新安装状态
//
// 顺序是这个功能的全部要害：Pavise 改过的系统设置（Win32PrioritySeparation、电源节流、
// 传输优化策略、DWM/HAGS/VBS、网卡与 USB 中断亲和、IFEO、逐游戏 GPU 偏好…）
// 它们的原值快照统统存在 HKCU\Software\Pavise 里。
// 先删键再还原是不可能的——快照没了，那些改动就永远留在用户机器上，卸载都救不回来。
// 所以必须：先还原干净 → 确认全部成功 → 才允许删数据。
// 任何一项还原失败就整体中止、不写完成标记，下次启动重试。

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
            LegacyFreezeRecovery.StateFileName, SuppressionCore.StateFileName
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
            Step("网络优化", NetTweak.Restore, failed);
            Step("时间片校正", QuantumTweak.Restore, failed);
            Step("MPO", MpoTweak.Restore, failed);
            Step("VBS", VbsTweak.Restore, failed);
            Step("游戏模式守护", GameModeGuard.Restore, failed);
            Step("设备电源", DevicePowerTweak.Restore, failed);
            Step("窗口化优化", WindowedOptTweak.Restore, failed);
            Step("NVIDIA 全局项", NvGlobalTweaks.Restore, failed);
            Step("后备提优 IFEO", IfeoBoost.RestoreAll, failed);

            StepIf("HAGS", delegate { return HagsTweak.EnabledByPavise; }, HagsTweak.Disable, failed);
            StepIf("GPU 中断亲和", delegate { return InterruptAffinityTweak.EnabledByPavise; }, InterruptAffinityTweak.Disable, failed);
            StepIf("USB 中断避让", delegate { return UsbInterruptAffinityTweak.EnabledByPavise; }, UsbInterruptAffinityTweak.Disable, failed);

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
            StepVoid("逐游戏全屏优化", delegate { GameExeTweaks.RestoreKind("fso"); }, failed);

            return failed;
        }

        public static void RunOnce(string dataDir)
        {
            if (Settings.Load(DoneKey, false)) return;

            Logger.Log("首次运行 v1.6.6：清除旧版本数据，先还原全部系统改动");

            List<string> failed = RestoreOrHook();
            if (failed.Count > 0)
            {
                Logger.Log("清除已中止：" + failed.Count + " 项未能还原（"
                    + string.Join("、", failed.ToArray()) + "），下次启动重试");
                return;
            }

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

            bool regCleared = DeleteRegistryTree();

            Settings.Save(DoneKey, true);

            Logger.Log("旧版本数据已清除：系统改动已还原，删除 " + files + " 个文件"
                + (regCleared ? "，配置已重置" : "，配置未能完全清空"));
        }
    }
}
