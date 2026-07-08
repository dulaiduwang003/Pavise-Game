// @author bdth 2074055628@qq.com
// 文件用途 调整并恢复多媒体调度参数

using System;
using Microsoft.Win32;

namespace AegisApp
{
    internal static class Mmcss
    {
        private const string Prof = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";
        private const string Games = Prof + @"\Tasks\Games";

        private static readonly ReversibleReg Resp  = new ReversibleReg(Registry.LocalMachine, Prof,  "SystemResponsiveness", RegistryValueKind.DWord,  "Mmcss_Resp");
        private static readonly ReversibleReg Pri   = new ReversibleReg(Registry.LocalMachine, Games, "Priority",             RegistryValueKind.DWord,  "Mmcss_Pri");
        private static readonly ReversibleReg Sched = new ReversibleReg(Registry.LocalMachine, Games, "Scheduling Category",  RegistryValueKind.String, "Mmcss_Sched");
        private static readonly ReversibleReg Sfio  = new ReversibleReg(Registry.LocalMachine, Games, "SFIO Priority",        RegistryValueKind.String, "Mmcss_Sfio");
        private static readonly ReversibleReg[] All = { Resp, Pri, Sched, Sfio };

        private static readonly object lk = new object();
        private static bool active;

        public static bool Activate()
        {
            lock (lk)
            {
                if (active) return true;
                active = Resp.Apply(10) & Pri.Apply(6) & Sched.Apply("High") & Sfio.Apply("High");
                if (!active)
                {
                    foreach (ReversibleReg r in All) r.Restore();
                    Logger.Log("MMCSS 调度未能完整写入并回读，已回滚本轮修改");
                }
                else Logger.Log("MMCSS 多媒体调度已按游戏优化");
                return active;
            }
        }

        public static bool Restore()
        {
            lock (lk)
            {
                bool ok = true;
                foreach (ReversibleReg r in All) ok &= r.Restore();
                active = false;
                return ok;
            }
        }

        public static void HealFromCrash()
        {
            foreach (ReversibleReg r in All)
                if (r.HasBackup) { Restore(); return; }
        }
    }
}
