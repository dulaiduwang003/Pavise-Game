using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Principal;
using System.Threading;

namespace PaviseApp
{
    internal static partial class Program
    {

        private static bool TryHandleCriticalThreadGuardian(string[] args)
        {
            bool guardianRequested = false;
            if (args != null)
            {
                foreach (string a in args)
                {
                    if (string.Equals(a, "--hard-thread-guardian", StringComparison.OrdinalIgnoreCase))
                    {
                        guardianRequested = true;
                    }
                }
            }
            if (!guardianRequested)
            {
                return false;
            }
            Environment.ExitCode = 13;
            try
            {
                int exitCode;
                if (CriticalThreadGuardian.TryRun(args, Paths.ResolveStartupDataArgument(args), out exitCode))
                {
                    Environment.ExitCode = exitCode;
                }
            }
            catch (ArgumentException)
            {
            }
            catch (Exception)
            {
                Environment.ExitCode = 14;
            }
            return true;
        }

        internal static bool IsAutostart(string[] args)
        {
            if (args != null)
            {
                foreach (string a in args)
                {
                    if (string.Equals(a, "--autostart", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        internal static bool ShouldSignalExistingPanel(bool autoStarted)
        {
            return !autoStarted;
        }

        internal static bool ShouldShowStartupPanel(bool autoStarted, bool requested, long requestTicks, long nowTicks)
        {
            if (!autoStarted)
            {
                return true;
            }
            if (requested && requestTicks > 0 && nowTicks >= requestTicks)
            {
                return nowTicks - requestTicks <= 300000000;
            }
            return false;
        }

        internal static bool ShouldShowPanelForMissingTray(bool autoStarted, bool showingPanel)
        {
            if (autoStarted)
            {
                return showingPanel;
            }
            return true;
        }

        private static void SetPendingPanelRequest(bool requested)
        {
            Settings.SaveStr("ShowPanelRequestUtcTicks", requested ? DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture) : "");
            Settings.Save("ShowPanelOnNextStart", requested);
        }

        private static void SignalShowPanel()
        {
            try
            {
                using (EventWaitHandle eventWaitHandle = EventWaitHandle.OpenExisting("Global\\Pavise_ShowPanel"))
                {
                    eventWaitHandle.Set();
                    return;
                }
            }
            catch
            {
            }
            if (IsElevated())
            {
                return;
            }
            try
            {
                if (TaskHelper.TaskExists())
                {
                    SetPendingPanelRequest(true);
                    if (TaskHelper.Run("/Run /TN PaviseAuto") != 0)
                    {
                        SetPendingPanelRequest(false);
                    }
                }
            }
            catch
            {
            }
        }

        internal static bool OsBelowBaseline()
        {
            int osBuildResult = Native.OsBuild();
            if (osBuildResult > 0)
            {
                return osBuildResult < 19041;
            }
            return false;
        }

        private static bool BlockIfOsTooOld(string dataDir)
        {
            int osBuildResult = Native.OsBuild();
            if (!OsBelowBaseline())
            {
                return false;
            }
            Logger.Log(Lang.T("log.program.11", 0) + osBuildResult);
            int startupInspectionState = 0;
            try
            {
                List<string> texts = LegacyPurge.RestorePersistent(dataDir);
                startupInspectionState = ((texts == null) ? 1 : ((texts.Count == 0) ? 1 : 2));
            }
            catch
            {
                startupInspectionState = 2;
            }
            string osOldBodyText = Lang.F("os.old.body", osBuildResult, 19041, 26100);
            if (startupInspectionState == 2)
            {
                osOldBodyText += Lang.T("os.old.restorefail");
            }
            PaviseDialog.Warn(null, "PAVISE AUTO", osOldBodyText);
            return true;
        }

        private static bool IsElevated()
        {
            try
            {
                using (WindowsIdentity ntIdentity = WindowsIdentity.GetCurrent())
                {
                    return new WindowsPrincipal(ntIdentity).IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
            catch
            {
                return false;
            }
        }

        internal static bool TryResetUserData(string dir, Func<bool> stopRuntime, out int files, out string failure, Action<string> progress = null)
        {
            files = 0;
            failure = null;
            if (string.IsNullOrWhiteSpace(dir))
            {
                failure = Lang.T("wipe.pathfail");
                return false;
            }
            try
            {
                if (stopRuntime == null || !stopRuntime())
                {
                    failure = Lang.T("wipe.stopfail");
                    return false;
                }
            }
            catch (Exception ex)
            {
                failure = Lang.T("wipe.stopfail") + " (" + ex.GetType().Name + ")";
                return false;
            }
            try
            {
                return LegacyPurge.WipeAll(dir, true, Lang.T("t.panelformsettingspage.3"), out files, out failure, progress);
            }
            catch (Exception ex2)
            {
                failure = Lang.T("wipe.cleanupfail") + " (" + ex2.GetType().Name + ")";
                return false;
            }
        }

    }
}
