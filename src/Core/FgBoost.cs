// @author bdth 2074055628@qq.com
// 文件用途 调整并恢复前台调度权重

using System;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static class FgBoost
    {
        private static readonly ReversibleReg Sep = new ReversibleReg(
            Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\PriorityControl",
            "Win32PrioritySeparation", RegistryValueKind.DWord, "PrevWin32PriSep");
        private static readonly object lk = new object();
        private static bool active;

        public static bool Activate()
        {
            lock (lk)
            {
                if (active) return true;
                // 0x28 = 短量子/固定：前后台平权，防止游戏满载时语音、音频、
                // 推流线程被饿死。此前写的 0x26 解析后与系统默认 0x2 行为等价，
                // 是个空操作（FPSHeaven / tenforums 均有拆解），2026-08 修正。
                active = Sep.Apply(0x28);
                Logger.Log(active ? "前台调度稳定已启用（Win32PrioritySeparation → 0x28 固定量子）"
                    : "前台调度稳定写入或回读失败，本轮未启用");
                return active;
            }
        }

        public static bool Restore()
        {
            lock (lk)
            {
                if (Sep.HasBackup && Sep.Restore()) Logger.Log("前台调度加权已还原");
                active = false;
                return !Sep.HasBackup;
            }
        }

        public static void HealFromCrash() { if (Sep.HasBackup) Restore(); }
    }
}
