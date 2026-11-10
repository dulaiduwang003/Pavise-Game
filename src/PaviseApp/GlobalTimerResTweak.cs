using Microsoft.Win32;

namespace PaviseApp
{
    internal static class GlobalTimerResTweak
    {

        private static readonly ReversibleReg Global = new ReversibleReg(Registry.LocalMachine, "SYSTEM\\CurrentControlSet\\Control\\Session Manager\\kernel", "GlobalTimerResolutionRequests", RegistryValueKind.DWord, "PrevGlobalTimerRes");

        private static readonly object lk = new object();

        public static bool EnabledByPavise
        {
            get
            {
                return Settings.Load("GlobalTimerResByPavise", false);
            }
        }

        public static bool OwnsState
        {
            get
            {
                if (!EnabledByPavise)
                {
                    return Global.HasBackup;
                }
                return true;
            }
        }

        public static bool CurrentlyOn()
        {
            return Global.Matches(1);
        }

        public static bool Enable()
        {
            lock (lk)
            {
                if (!Native.IsElevated())
                {
                    return false;
                }
                if (!Global.Apply(1))
                {
                    Logger.Log(Lang.T("log.gtimer.1", 0));
                    return false;
                }
                if (!Settings.Save("GlobalTimerResByPavise", true))
                {
                    return false;
                }
                Logger.Log(Lang.T("log.gtimer.2", 0));
                return true;
            }
        }

        public static bool Restore()
        {
            lock (lk)
            {
                if (!Global.Restore())
                {
                    Logger.Log(Lang.T("log.gtimer.3", 0));
                    return false;
                }
                if (!Settings.Save("GlobalTimerResByPavise", false))
                {
                    return false;
                }
                Logger.Log(Lang.T("log.gtimer.4", 0));
                return true;
            }
        }
    }
}
