// 作者：bdth 2074055628@qq.com
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
        private static readonly ReversibleReg Lazy  = new ReversibleReg(Registry.LocalMachine, Prof,  "NoLazyMode",           RegistryValueKind.DWord,  "Mmcss_NoLazy");
        private static readonly ReversibleReg[] All = { Resp, Pri, Sched, Sfio, Lazy };

        private static readonly object lk = new object();
        private static bool active;
        internal const int Responsiveness = 10;
        internal const string HighCategory = "High";

#if PAVISE_SELFTEST
        internal static ReversibleReg[] ValuesForTest;
#endif
        private static ReversibleReg[] Values
        {
            get
            {
#if PAVISE_SELFTEST
                return ValuesForTest;
#else
                return All;
#endif
            }
        }

        public static bool Activate()
        {
            lock (lk)
            {
                ReversibleReg[] values = Values;
                if (values == null) return false;
                if (active && HasResidue()) return true;
#if !PAVISE_SELFTEST
                if (!Native.IsElevated()) return false;
#endif
                bool ok = values[0].Apply(Responsiveness)
                    & values[2].Apply(HighCategory) & values[4].Apply(1);
                if (!ok)
                {
                    foreach (ReversibleReg value in values) value.Restore();
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
                ReversibleReg[] values = Values;
                if (values == null) { if (!had) active = false; return !had; }
                bool ok = true;
                foreach (ReversibleReg r in values) ok &= r.Restore();
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
