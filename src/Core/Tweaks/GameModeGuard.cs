// @author bdth 2074055628@qq.com
// File purpose Ensure Windows Game Mode hasn't been turned off; plenty of old tuning guides say to disable it, but testing shows it only helps
using System;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static class GameModeGuard
    {
        private const string BarKey = @"Software\Microsoft\GameBar";
        private const string Val = "AutoGameModeEnabled";

        private static readonly ReversibleReg Auto = new ReversibleReg(
            Registry.CurrentUser, BarKey, Val, RegistryValueKind.DWord, "PrevAutoGameMode");

        public static bool EnabledByPavise { get { return Settings.Load("GameModeGuardByPavise", false); } }

        public static bool CurrentlyOn()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(BarKey))
                {
                    if (k == null) return true;
                    object v = k.GetValue(Val);
                    return !(v is int) || (int)v != 0;
                }
            }
            catch { return true; }
        }

        public static bool Enable()
        {
            try
            {
                if (CurrentlyOn())
                {
                    Settings.Save("GameModeGuardByPavise", true);
                    if (!Settings.Load("GameModeGuardByPavise", false))
                    {
                        Logger.Log(Lang.T("log.gamemodeguard.1"));
                        return false;
                    }
                    return true;
                }
                if (!Auto.Apply(1))
                {
                    Logger.Log(Lang.T("log.gamemodeguard.1"));
                    return false;
                }
                Settings.Save("GameModeGuardByPavise", true);
                if (!Settings.Load("GameModeGuardByPavise", false))
                {
                    Auto.Restore();
                    Logger.Log(Lang.T("log.gamemodeguard.2"));
                    return false;
                }
                Logger.Log(Lang.T("log.gamemodeguard.3"));
                return true;
            }
            catch { return false; }
        }

        public static bool Restore()
        {
            try
            {
                bool ok = !Auto.HasBackup || Auto.Restore();
                if (ok)
                {
                    Settings.Save("GameModeGuardByPavise", false);
                    if (Settings.Load("GameModeGuardByPavise", true)) return false;
                    Logger.Log(Lang.T("log.gamemodeguard.4"));
                }
                return ok;
            }
            catch { return false; }
        }
    }
}
