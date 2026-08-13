// @author bdth 2074055628@qq.com
// 文件用途 关掉筛选键 粘滞键 切换键的生效位与热键位 可逆
// 依据 筛选键按微软自己的定义就是让键盘忽略短促或重复的击键 开着等于在输入路径上主动插入延迟
// 热键位是另一半问题 长按或连按 Shift 会在全屏游戏里弹窗打断 这两处是全部键鼠优化里唯一能救回两位数毫秒的软件项
// 写的是 HKCU 不需要管理员 不需要重启 写完调 SystemParametersInfo 让当前会话立刻生效

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
            if (!st.Read) return "读不到辅助功能设置 这一项本次不做判断";
            if (EnabledByPavise) return "生效位与热键位已清 拨回开关即还原系统原值";
            if (!st.AnyNeedsFix) return "筛选键 粘滞键 切换键都没开 热键也没激活 不用处理";

            var parts = new System.Collections.Generic.List<string>();
            if (AccessibilityFlags.IsOn(st.FilterKeys))
                parts.Add("筛选键正开着 每次击键要等 " + st.DelayBeforeAcceptanceMs + " 毫秒才被接受");
            else if (AccessibilityFlags.HotkeyOn(st.FilterKeys))
                parts.Add("筛选键热键激活 长按右 Shift 八秒会弹窗打断游戏");
            if (AccessibilityFlags.IsOn(st.StickyKeys)) parts.Add("粘滞键正开着");
            else if (AccessibilityFlags.HotkeyOn(st.StickyKeys))
                parts.Add("粘滞键热键激活 连按五次 Shift 会弹窗打断游戏");
            if (AccessibilityFlags.IsOn(st.ToggleKeys)) parts.Add("切换键正开着");
            else if (AccessibilityFlags.HotkeyOn(st.ToggleKeys))
                parts.Add("切换键热键激活 长按 NumLock 五秒会弹窗打断游戏");
            return string.Join("  ", parts.ToArray());
        }

        public static bool Enable()
        {
            lock (lk)
            {
                AccessibilityState st = InputChainProbe.ReadAccessibility();
                if (!st.Read)
                {
                    Logger.Log("辅助功能拦截 读不到当前设置 本次不改动");
                    return false;
                }

                bool ok = true;
                ok &= WriteOne(FilterReg, st.FilterKeys, "筛选键");
                ok &= WriteOne(StickyReg, st.StickyKeys, "粘滞键");
                ok &= WriteOne(ToggleReg, st.ToggleKeys, "切换键");
                PushToSession();

                if (ok)
                {
                    Settings.Save(FlagKey, true);
                    Logger.Log("辅助功能拦截 筛选键 粘滞键 切换键的生效位与热键位已清");
                }
                else Logger.Log("辅助功能拦截 部分项写入失败 已写入的仍可还原");
                return ok;
            }
        }

        private static bool WriteOne(ReversibleReg reg, int current, string label)
        {
            int target = AccessibilityFlags.Sanitize(current);
            if (target == current && !reg.HasBackup) return true;
            bool ok = reg.Apply(target.ToString());
            if (!ok) Logger.Log("辅助功能拦截 " + label + " 写入或回读失败");
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
                    Logger.Log("辅助功能拦截 已还原系统原值");
                }
                else Logger.Log("辅助功能拦截 部分项还原失败 快照保留待下次重试");
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
