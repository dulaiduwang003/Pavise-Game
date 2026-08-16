// @author bdth 2074055628@qq.com
// 文件用途 关掉筛选键 粘滞键 切换键的生效位与热键位 可逆

using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static class AccessibilityKeysTweak
    {
        private const string FlagKey = "AccessKeysByPavise";

        private static readonly ReversibleReg FilterReg = new ReversibleReg(
            Registry.CurrentUser, InputChainProbe.FilterKeysPath,
            "Flags", RegistryValueKind.String, "PrevFilterKeys");
        private static readonly ReversibleReg StickyReg = new ReversibleReg(
            Registry.CurrentUser, InputChainProbe.StickyKeysPath,
            "Flags", RegistryValueKind.String, "PrevStickyKeys");
        private static readonly ReversibleReg ToggleReg = new ReversibleReg(
            Registry.CurrentUser, InputChainProbe.ToggleKeysPath,
            "Flags", RegistryValueKind.String, "PrevToggleKeys");

        private static readonly object lk = new object();

        public static bool EnabledByPavise { get { return Settings.Load(FlagKey, false); } }

        public static bool HasResidue()
        {
            return EnabledByPavise || FilterReg.HasBackup || StickyReg.HasBackup || ToggleReg.HasBackup;
        }

        public static bool NeedsFix()
        {
            return InputChainProbe.ReadAccessibility().AnyNeedsFix;
        }

        public static string Describe()
        {
            AccessibilityState st = InputChainProbe.ReadAccessibility();
            if (!st.Read) return Lang.T("t.accessibilitykeystweak.1");
            if (EnabledByPavise) return Lang.T("t.accessibilitykeystweak.2");
            if (!st.AnyNeedsFix) return Lang.T("t.accessibilitykeystweak.3");

            var parts = new System.Collections.Generic.List<string>();
            if (AccessibilityFlags.IsOn(st.FilterKeys))
                parts.Add(Lang.T("t.accessibilitykeystweak.4") + st.DelayBeforeAcceptanceMs + Lang.T("t.accessibilitykeystweak.5"));
            else if (AccessibilityFlags.HotkeyOn(st.FilterKeys))
                parts.Add(Lang.T("t.accessibilitykeystweak.6"));
            if (AccessibilityFlags.IsOn(st.StickyKeys)) parts.Add(Lang.T("t.accessibilitykeystweak.7"));
            else if (AccessibilityFlags.HotkeyOn(st.StickyKeys))
                parts.Add(Lang.T("t.accessibilitykeystweak.8"));
            if (AccessibilityFlags.IsOn(st.ToggleKeys)) parts.Add(Lang.T("t.accessibilitykeystweak.9"));
            else if (AccessibilityFlags.HotkeyOn(st.ToggleKeys))
                parts.Add(Lang.T("t.accessibilitykeystweak.10"));
            return string.Join("  ", parts.ToArray());
        }

        public static bool Enable()
        {
            lock (lk)
            {
                AccessibilityState st = InputChainProbe.ReadAccessibility();
                if (!st.Read)
                {
                    Logger.Log(Lang.T("log.accessibilitykeystweak.11"));
                    return false;
                }

                bool ok = true;
                ok &= WriteOne(FilterReg, st.FilterKeys, Lang.T("t.systemauditverdicts.7"));
                ok &= WriteOne(StickyReg, st.StickyKeys, Lang.T("t.accessibilitykeystweak.12"));
                ok &= WriteOne(ToggleReg, st.ToggleKeys, Lang.T("t.accessibilitykeystweak.13"));
                PushToSession();

                if (ok)
                {
                    Settings.Save(FlagKey, true);
                    Logger.Log(Lang.T("log.accessibilitykeystweak.14"));
                }
                else Logger.Log(Lang.T("log.accessibilitykeystweak.15"));
                return ok;
            }
        }

        private static bool WriteOne(ReversibleReg reg, int current, string label)
        {
            int target = AccessibilityFlags.Sanitize(current);
            if (target == current && !reg.HasBackup) return true;
            bool ok = reg.Apply(target.ToString());
            if (!ok) Logger.Log(Lang.T("log.accessibilitykeystweak.16") + label + Lang.T("log.accessibilitykeystweak.17"));
            return ok;
        }

        public static bool Restore()
        {
            lock (lk)
            {
                bool all = true;
                all &= FilterReg.Restore();
                all &= StickyReg.Restore();
                all &= ToggleReg.Restore();
                PushToSession();
                if (all)
                {
                    Settings.Save(FlagKey, false);
                    Logger.Log(Lang.T("log.accessibilitykeystweak.18"));
                }
                else Logger.Log(Lang.T("log.accessibilitykeystweak.19"));
                return all;
            }
        }

        private static void PushToSession()
        {
            try
            {
                AccessibilityState st = InputChainProbe.ReadAccessibility();
                if (!st.Read) return;

                var fk = new FILTERKEYS();
                fk.cbSize = (uint)Marshal.SizeOf(typeof(FILTERKEYS));
                if (SystemParametersInfoRef(SPI_GETFILTERKEYS, fk.cbSize, ref fk, 0))
                {
                    fk.dwFlags = (uint)st.FilterKeys;
                    SystemParametersInfoRef(SPI_SETFILTERKEYS, fk.cbSize, ref fk, SPIF_SENDCHANGE);
                }

                var sk = new STICKYKEYS();
                sk.cbSize = (uint)Marshal.SizeOf(typeof(STICKYKEYS));
                sk.dwFlags = (uint)st.StickyKeys;
                SystemParametersInfoSticky(SPI_SETSTICKYKEYS, sk.cbSize, ref sk, SPIF_SENDCHANGE);

                var tk = new TOGGLEKEYS();
                tk.cbSize = (uint)Marshal.SizeOf(typeof(TOGGLEKEYS));
                tk.dwFlags = (uint)st.ToggleKeys;
                SystemParametersInfoToggle(SPI_SETTOGGLEKEYS, tk.cbSize, ref tk, SPIF_SENDCHANGE);
            }
            catch { }
        }

        private const uint SPI_GETFILTERKEYS = 0x0032;
        private const uint SPI_SETFILTERKEYS = 0x0033;
        private const uint SPI_SETTOGGLEKEYS = 0x0035;
        private const uint SPI_SETSTICKYKEYS = 0x003B;
        private const uint SPIF_SENDCHANGE = 0x02;

        [StructLayout(LayoutKind.Sequential)]
        private struct FILTERKEYS
        {
            public uint cbSize, dwFlags, iWaitMSec, iDelayMSec, iRepeatMSec, iBounceMSec;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct STICKYKEYS { public uint cbSize, dwFlags; }

        [StructLayout(LayoutKind.Sequential)]
        private struct TOGGLEKEYS { public uint cbSize, dwFlags; }

        [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
        private static extern bool SystemParametersInfoRef(uint action, uint param, ref FILTERKEYS data, uint winIni);
        [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
        private static extern bool SystemParametersInfoSticky(uint action, uint param, ref STICKYKEYS data, uint winIni);
        [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
        private static extern bool SystemParametersInfoToggle(uint action, uint param, ref TOGGLEKEYS data, uint winIni);
    }
}
