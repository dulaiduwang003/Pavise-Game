// @author bdth 2074055628@qq.com
// 文件用途 调整 MMCSS 多媒体调度参数 游戏模式开启期间常驻 关闭或退出即还原
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
        // MMCSS 空闲检测有 10ms/100ms 双档 lazy 档会让已注册线程的调度周期变钝
        //   NoLazyMode 消掉 lazy 循环 代价是 MMCSS 自身的空闲检测更勤 功耗略升
        private static readonly ReversibleReg Lazy  = new ReversibleReg(Registry.LocalMachine, Prof,  "NoLazyMode",           RegistryValueKind.DWord,  "Mmcss_NoLazy");
        private static readonly ReversibleReg[] All = { Resp, Pri, Sched, Sfio, Lazy };

        internal const int Responsiveness = 10;
        internal const string HighCategory = "High";

        private static readonly object lk = new object();
        private static bool active;

        public static bool Activate()
        {
            lock (lk)
            {
                if (active && HasResidue()) return true;
                if (!Native.IsElevated()) return false;
                bool ok = Resp.Apply(Responsiveness) & Sched.Apply(HighCategory) & Lazy.Apply(1);
                if (!ok)
                {
                    foreach (ReversibleReg r in All) r.Restore();
                    active = false;
                    Logger.Log(Lang.T("log.mmcss.1"));
                    return false;
                }
                active = true;
                Logger.Log(Lang.T("log.mmcss.2"));
                return true;
            }
        }

        public static bool Restore()
        {
            lock (lk)
            {
                bool had = HasResidue();
                bool ok = true;
                foreach (ReversibleReg r in All) ok &= r.Restore();
                active = false;
                if (had) Logger.Log(Lang.T(ok ? "log.mmcss.3" : "log.mmcss.4"));
                return ok;
            }
        }

        public static bool HasResidue()
        {
            foreach (ReversibleReg r in All) if (r.HasBackup) return true;
            return false;
        }

        public static void HealFromCrash()
        {
            if (HasResidue()) Restore();
        }
    }
}
