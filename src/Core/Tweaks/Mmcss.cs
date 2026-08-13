// @author bdth 2074055628@qq.com
// 文件用途 清理旧版本写入的 MMCSS 多媒体调度参数 只保留还原能力

using Microsoft.Win32;

namespace PaviseApp
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

        public static bool Restore()
        {
            lock (lk)
            {
                bool ok = true;
                foreach (ReversibleReg r in All) ok &= r.Restore();
                return ok;
            }
        }

        public static bool HasResidue()
        {
            foreach (ReversibleReg r in All) if (r.HasBackup) return true;
            return false;
        }
    }
}
