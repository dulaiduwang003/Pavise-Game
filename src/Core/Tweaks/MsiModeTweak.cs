// @author bdth 2074055628@qq.com
// 文件用途 清理旧版本写入的消息信号中断开关
// v1.7 移除 MSI 模式 本类只保留还原能力

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
