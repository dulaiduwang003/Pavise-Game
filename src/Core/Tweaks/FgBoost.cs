// @author bdth 2074055628@qq.com
// 文件用途 清理旧版本写入的前台调度权重
//
// v1.6.6 移除了「前台调度稳定」。它把 Win32PrioritySeparation 写成 0x28，
// 即长定长量子并取消前台三倍时间片，方向是削弱前台而非加强游戏。
// 本类只保留还原能力：老用户注册表里还留着 Pavise 写入的值和原值快照。

using Microsoft.Win32;

namespace PaviseApp
{
    internal static class FgBoost
    {
        private static readonly ReversibleReg Sep = new ReversibleReg(
            Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\PriorityControl",
            "Win32PrioritySeparation", RegistryValueKind.DWord, "PrevWin32PriSep");
        private static readonly object lk = new object();

        public static bool Restore()
        {
            lock (lk) return !Sep.HasBackup || Sep.Restore();
        }

        public static bool HasResidue() { return Sep.HasBackup; }

        public static void PurgeLegacy()
        {
            if (!Sep.HasBackup) return;
            if (Restore()) Logger.Log("已还原旧版本的前台调度权重");
            else Logger.Log("前台调度权重还原未完成，下次启动重试");
        }
    }
}
