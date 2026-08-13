// @author bdth 2074055628@qq.com
// 文件用途 独占模式切换电源滑块到最佳性能 原值取自注册表 退出还原

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
                if (!ok) Logger.Log("电源滑块 本机读不到 overlay 状态 独占模式跳过该项");
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
                    Logger.Log("电源滑块 快照无法持久化 本轮未切换");
                    return false;
                }
                uint status;
                try { status = PowerSetActiveOverlayScheme(Max); }
                catch (EntryPointNotFoundException) { support = -1; return false; }
                catch { return false; }
                Guid after;
                if (status != 0 || !TryReadActive(out after) || after != Max)
                {
                    Logger.Log("电源滑块 切最佳性能失败 状态 " + status + " ");
                    return false;
                }
                Logger.Log("电源滑块 已切到最佳性能 退出独占模式还原");
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
                    Logger.Log("电源滑块还原失败 快照保留 下次启动继续尝试");
                    return false;
                }
                Settings.SaveStr(SnapKey, "");
                Logger.Log("电源滑块已还原");
                return true;
            }
        }

        public static void HealFromCrash()
        {
            if (Settings.LoadStr(SnapKey, "").Length == 0) return;
            if (Restore()) Logger.Log("检测到上次未还原的电源滑块设置 已恢复");
        }
    }
}
