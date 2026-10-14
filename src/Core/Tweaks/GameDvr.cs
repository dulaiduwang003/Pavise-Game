// 作者：bdth 2074055628@qq.com
using System;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static class GameDvr
    {
        private static readonly ReversibleReg Dvr = new ReversibleReg(
            Registry.CurrentUser, @"System\GameConfigStore", "GameDVR_Enabled", RegistryValueKind.DWord, "PrevGameDvr");
        private static readonly ReversibleReg Cap = new ReversibleReg(
            Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", RegistryValueKind.DWord, "PrevGameDvrCap");
        private static readonly object lk = new object();
        private static bool active;

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
                return new[] { Dvr, Cap };
#endif
            }
        }
        internal static bool HasResidue { get { return Dvr.HasBackup || Cap.HasBackup; } }

        public static bool Activate()
        {
            lock (lk)
            {
                ReversibleReg[] values = Values;
                if (values == null) return false;
                if (active) return true;
                bool dvrOk = values[0].Apply(0);
                bool capOk = values[1].Apply(0);
                active = dvrOk && capOk;
                Logger.Log(active ? Lang.T("log.gamedvr.1")
                    : dvrOk ? Lang.T("log.gamedvr.2")
                    : Lang.T("log.gamedvr.3"));
                return active;
            }
        }

        public static bool Restore()
        {
            lock (lk)
            {
                bool had = HasResidue;
                ReversibleReg[] values = Values;
                if (values == null) { if (!had) active = false; return !had; }
                bool dvrOk = !values[0].HasBackup || values[0].Restore();
                bool capOk = !values[1].HasBackup || values[1].Restore();
                if (had && dvrOk && capOk) Logger.Log(Lang.T("log.gamedvr.4"));
                active = false;
                return !HasResidue;
            }
        }

        public static void HealFromCrash() { if (Dvr.HasBackup || Cap.HasBackup) Restore(); }
    }
}
