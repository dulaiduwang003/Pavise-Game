// @author bdth 2074055628@qq.com
// 文件用途 关掉指针精度增强 让鼠标位移不再被系统加速曲线改写 可逆
// 依据 这一项不是延迟问题是一致性问题 同样的物理位移会因为移动快慢产生不同的屏幕位移 肌肉记忆建不起来
// 走 Raw Input 的游戏本来就绕过它 这一项影响的是桌面和没走 Raw Input 的游戏
// 写的是 HKCU 不需要管理员 不需要重启 写完调 SystemParametersInfo 让当前会话立刻生效

using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static class PointerPrecisionTweak
    {
        private const string FlagKey = "PointerPrecByPavise";

        private static readonly ReversibleReg SpeedReg = new ReversibleReg(
            Registry.CurrentUser, InputChainProbe.MousePath,
            "MouseSpeed", RegistryValueKind.String, "PrevMouseSpeed");
        private static readonly ReversibleReg Th1Reg = new ReversibleReg(
            Registry.CurrentUser, InputChainProbe.MousePath,
            "MouseThreshold1", RegistryValueKind.String, "PrevMouseTh1");
        private static readonly ReversibleReg Th2Reg = new ReversibleReg(
            Registry.CurrentUser, InputChainProbe.MousePath,
            "MouseThreshold2", RegistryValueKind.String, "PrevMouseTh2");

        private static readonly object lk = new object();

        public static bool EnabledByPavise { get { return Settings.Load(FlagKey, false); } }

        public static bool HasResidue()
        {
            return EnabledByPavise || SpeedReg.HasBackup || Th1Reg.HasBackup || Th2Reg.HasBackup;
        }

        public static bool NeedsFix()
        {
            return InputChainProbe.PointerPrecisionOn();
        }

        public static string Describe()
        {
            if (EnabledByPavise) return "指针精度增强已关 鼠标位移不再被系统加速曲线改写 拨回开关即还原系统原值";
            if (!NeedsFix()) return "指针精度增强本来就没开 鼠标位移是线性的 不用处理";
            return "指针精度增强正开着 同样的物理位移会因为你甩得快慢产生不同的屏幕位移 肌肉记忆建不起来 走 Raw Input 的游戏不受影响";
        }

        public static bool Enable()
        {
            lock (lk)
            {
                bool ok = SpeedReg.Apply("0");
                ok &= Th1Reg.Apply("0");
                ok &= Th2Reg.Apply("0");
                PushToSession();

                if (ok)
                {
                    Settings.Save(FlagKey, true);
                    Logger.Log("指针精度增强 已关闭 鼠标位移不再被加速曲线改写");
                }
                else Logger.Log("指针精度增强 部分项写入失败 已写入的仍可还原");
                return ok;
            }
        }

        public static bool Restore()
        {
            lock (lk)
            {
                bool all = SpeedReg.Restore();
                all &= Th1Reg.Restore();
                all &= Th2Reg.Restore();
                PushToSession();
                if (all)
                {
                    Settings.Save(FlagKey, false);
                    Logger.Log("指针精度增强 已还原系统原值");
                }
                else Logger.Log("指针精度增强 部分项还原失败 快照保留待下次重试");
                return all;
            }
        }

        private static void PushToSession()
        {
            try
            {
                var values = new int[3];
                values[0] = ReadInt("MouseThreshold1", 6);
                values[1] = ReadInt("MouseThreshold2", 10);
                values[2] = ReadInt("MouseSpeed", 1);
                SystemParametersInfoArr(SPI_SETMOUSE, 0, values, SPIF_SENDCHANGE);
            }
            catch { }
        }

        internal static int ReadInt(string name, int fallback)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(InputChainProbe.MousePath))
                {
                    if (key == null) return fallback;
                    int parsed;
                    if (!AccessibilityFlags.TryParse(key.GetValue(name) as string, out parsed)) return fallback;
                    return parsed;
                }
            }
            catch { return fallback; }
        }

        private const uint SPI_SETMOUSE = 0x0004;
        private const uint SPIF_SENDCHANGE = 0x02;

        [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
        private static extern bool SystemParametersInfoArr(uint action, uint param, int[] data, uint winIni);
    }
}
