// @author bdth 2074055628@qq.com
// 文件用途 清理旧版本写入的消息信号中断开关
//
// v1.7 移除了 MSI 模式：启用的前提是设备真的支持消息信号中断，而注册表里
// 没有 MSISupported 键并不等于支持，代码也无从验证。给不支持的设备写入 1，
// 轻则设备失效，重则重启后无法进入系统，此时还原逻辑已经够不着了。
//
// 本类只保留还原能力：老用户注册表里还留着 Pavise 写入的值和原值快照。

using System;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static class MsiModeTweak
    {
        private const string EnumRoot = @"SYSTEM\CurrentControlSet\Enum";
        private const string MsiLeaf = @"Device Parameters\Interrupt Management\MessageSignaledInterruptProperties";
        private const string ListKey = "MsiList";
        private const string FlagKey = "MsiOnByPavise";

        private static readonly object lk = new object();

        public static bool HasResidue()
        {
            return Settings.Load(FlagKey, false)
                || ParseList(Settings.LoadStr(ListKey, "")).Length > 0;
        }

        public static bool Restore()
        {
            lock (lk)
            {
                bool all = true;
                foreach (string id in ParseList(Settings.LoadStr(ListKey, "")))
                    all &= Reg(id).Restore();
                if (all)
                {
                    Settings.SaveStr(ListKey, "");
                    Settings.Save(FlagKey, false);
                }
                return all;
            }
        }

        private static ReversibleReg Reg(string instanceId)
        {
            return new ReversibleReg(Registry.LocalMachine,
                EnumRoot + @"\" + instanceId + @"\" + MsiLeaf,
                "MSISupported", RegistryValueKind.DWord,
                "Msi_" + instanceId.Replace('\\', '_'));
        }

        internal static string[] ParseList(string raw)
        {
            return (raw ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
        }
    }
}
