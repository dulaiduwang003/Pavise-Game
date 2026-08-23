// @author bdth 2074055628@qq.com
// 文件用途 专注模式切换电源滑块到最佳性能 原值取自注册表 退出还原
using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static class PowerOverlay
    {
        private const string SchemeKey = @"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes";
        private const string AcValue = "ActiveOverlayAcPowerScheme";
        private const string SnapKey = "PowerOverlaySnap";

        private static readonly Guid Max = new Guid("ded574b5-45a0-4f42-8737-46345c09c238");

        [DllImport("powrprof.dll")]
        private static extern uint PowerSetActiveOverlayScheme(Guid overlaySchemeGuid);

        private static readonly object lk = new object();
        private static int support;

        public static bool Supported()
        {
            lock (lk)
            {
                if (support != 0) return support > 0;
                bool ok = false;
                try
                {
                    Guid current;
                    ok = TryReadActive(out current);
                }
                catch { }
                support = ok ? 1 : -1;
                if (!ok) Logger.Log(Lang.T("log.poweroverlay.1"));
                return ok;
            }
        }

        internal static bool TryReadActive(out Guid value)
        {
            value = Guid.Empty;
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(SchemeKey))
                {
                    if (key == null) return false;
                    string raw = key.GetValue(AcValue) as string;
                    if (string.IsNullOrEmpty(raw)) return false;
                    value = new Guid(raw);
                    return true;
                }
            }
            catch { return false; }
        }

        // 只有笔记本值得动这个滑块 台式机没有 DTT / DPTF 拨过去基本空转
        //   它通过 GUID_POWER_SAVING_STATUS 通知 Intel DTT 放宽 CPU 功耗限制
        //   并减少外壳温度触发的降频 这是笔记本上少数几个 OS 侧真能影响 PL 的口子
        //   1.8.0.2 曾经全局启用后下架 这次只在笔记本 插电 专注档三条同时成立时用
        //   电池上不动 那跟专注档电池分支放开省电项的方向正好相反
        internal static bool ShouldActivate(bool laptop, bool onAc, bool competitive)
        {
            return laptop && onAc && competitive;
        }

        public static bool Activate()
        {
            if (!Supported()) return false;
            lock (lk)
            {
                Guid before;
                if (!TryReadActive(out before)) return false;
                if (before == Max) return true;
                if (Settings.LoadStr(SnapKey, "").Length == 0
                    && !Settings.SaveStr(SnapKey, before.ToString()))
                {
                    Logger.Log(Lang.T("log.poweroverlay.2"));
                    return false;
                }
                uint status;
                try { status = PowerSetActiveOverlayScheme(Max); }
                catch (EntryPointNotFoundException) { support = -1; return false; }
                catch { return false; }
                Guid after;
                if (status != 0 || !TryReadActive(out after) || after != Max)
                {
                    Logger.Log(Lang.T("log.poweroverlay.3") + status + " ");
                    return false;
                }
                Logger.Log(Lang.T("log.poweroverlay.4"));
                return true;
            }
        }

        public static bool Restore()
        {
            lock (lk)
            {
                string saved = Settings.LoadStr(SnapKey, "");
                if (saved.Length == 0) return true;
                Guid original;
                try { original = new Guid(saved); }
                catch { Settings.SaveStr(SnapKey, ""); return true; }
                uint status;
                try { status = PowerSetActiveOverlayScheme(original); }
                catch { return false; }
                Guid after;
                if (status != 0 || !TryReadActive(out after) || after != original)
                {
                    Logger.Log(Lang.T("log.poweroverlay.5"));
                    return false;
                }
                Settings.SaveStr(SnapKey, "");
                Logger.Log(Lang.T("log.poweroverlay.6"));
                return true;
            }
        }

        public static void HealFromCrash()
        {
            if (Settings.LoadStr(SnapKey, "").Length == 0) return;
            if (Restore()) Logger.Log(Lang.T("log.poweroverlay.7"));
        }
    }
}
