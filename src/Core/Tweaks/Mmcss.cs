// @author bdth 2074055628@qq.com
// 文件用途 清理旧版本写入的 MMCSS 多媒体调度参数
//
// v1.6.6 移除了 MMCSS 参数写入：实测把这四个值改成 Pavise 的档位与保持系统默认相比，
// 帧时间 p50 / p99 / p99.9 全部为 1.00 倍，没有任何可测收益。
// 真正有价值的是游戏进程自己调用 AvSetMmThreadCharacteristics 登记任务类别，
// 那是游戏的行为，改注册表参数影响不到它。
//
// 本类只保留还原能力：老用户注册表里还留着 Pavise 写入的值和原值快照，
// 必须能把它们清干净，否则删掉功能等于把改动永久留在别人机器上。

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

        public static void PurgeLegacy()
        {
            if (!HasResidue()) return;
            if (Restore()) Logger.Log("已还原旧版本的 MMCSS 参数");
            else Logger.Log("MMCSS 参数还原未完成，下次启动重试");
        }
    }
}
