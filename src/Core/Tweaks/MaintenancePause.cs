// @author bdth 2074055628@qq.com
// File purpose Disable Windows automatic maintenance during a match and write it back at match end; defrag, NGEN and scheduled scans no longer launch off a mid-match idle verdict
using Microsoft.Win32;

namespace PaviseApp
{
    // Automatic maintenance starts after an idle verdict; AFK, cutscenes and loading screens can all be judged idle
    //   MaintenanceDisabled only blocks the automatic trigger; manually run maintenance is unaffected; written back from snapshot at match end
    internal static class MaintenancePause
    {
        // Session journal key shared with the restore-complete check; rename both sides together
        internal const string JournalKey = "PrevMaintDisabled";
        private static readonly ReversibleReg Disabled = new ReversibleReg(
            Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\Maintenance",
            "MaintenanceDisabled", RegistryValueKind.DWord, JournalKey);
        private static readonly object lk = new object();
        private static bool active;

        public static bool HasResidue
        {
            get { lock (lk) { try { return Disabled.HasBackup; } catch { return true; } } }
        }

        public static bool Activate()
        {
            lock (lk)
            {
                if (active) return true;
                if (!Native.IsElevated()) return false;
                if (!Disabled.Apply(1))
                {
                    Logger.Log(Lang.T("log.maint.1"));
                    return false;
                }
                active = true;
                Logger.Log(Lang.T("log.maint.2"));
                return true;
            }
        }

        public static bool Restore()
        {
            lock (lk)
            {
                bool had = Disabled.HasBackup;
                bool ok = !had || Disabled.Restore();
                active = false;
                if (had) Logger.Log(Lang.T(ok ? "log.maint.3" : "log.maint.4"));
                return ok;
            }
        }

        public static void HealFromCrash()
        {
            if (HasResidue) Restore();
        }
    }
}
