// @author bdth 2074055628@qq.com
// File purpose Startup pre-checks, version comparison, single-instance replacement, system baseline and data reset
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static partial class Program
    {
        private static void SignalShowPanel()
        {
            try
            {
                using (var show = EventWaitHandle.OpenExisting("Global\\Pavise_ShowPanel"))
                {
                    show.Set();
                    return;
                }
            }
            catch { }
            if (IsElevated()) return;
            try
            {
                if (!TaskHelper.TaskExists()) return;
                Settings.Save(PendingPanelKey, true);
                if (TaskHelper.Run("/Run /TN " + TaskHelper.TaskName) != 0)
                    Settings.Save(PendingPanelKey, false);
            }
            catch { }
        }

        private static EventWaitHandle CreateSignalEvent(string name)
        {
            try
            {
                var sec = new EventWaitHandleSecurity();
                sec.AddAccessRule(new EventWaitHandleAccessRule(
                    WindowsIdentity.GetCurrent().User,
                    EventWaitHandleRights.FullControl,
                    AccessControlType.Allow));
                bool createdNew;
                return new EventWaitHandle(false, EventResetMode.AutoReset, name, out createdNew, sec);
            }
            catch { }
            try { return new EventWaitHandle(false, EventResetMode.AutoReset, name); }
            catch { return null; }
        }

        internal static int CompareVersions(string left, string right)
        {
            Version a, b;
            if (!Version.TryParse(NormalizeVersion(left), out a)) a = new Version(0, 0);
            if (!Version.TryParse(NormalizeVersion(right), out b)) b = new Version(0, 0);
            return a.CompareTo(b);
        }

        private static string NormalizeVersion(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "0.0.0.0";
            string text = raw.Trim();
            if (text.StartsWith("v", StringComparison.OrdinalIgnoreCase)) text = text.Substring(1);
            int parts = text.Split('.').Length;
            for (int i = parts; i < 4; i++) text += ".0";
            return text;
        }

        private static bool TryReplaceOlderInstance()
        {
            Process older = null;
            try
            {
                int self = Process.GetCurrentProcess().Id;
                foreach (Process p in Process.GetProcessesByName(
                    Path.GetFileNameWithoutExtension(Application.ExecutablePath)))
                {
                    if (p.Id == self) { p.Dispose(); continue; }
                    string version = null;
                    try { version = p.MainModule.FileVersionInfo.FileVersion; }
                    catch { }
                    if (version != null && CompareVersions(App.Version, version) > 0 && older == null) older = p;
                    else p.Dispose();
                }
                if (older == null) return false;

                try { using (var exit = EventWaitHandle.OpenExisting("Global\\Pavise_Exit")) exit.Set(); }
                catch { return false; }
                if (!older.WaitForExit(20000)) return false;
                return true;
            }
            catch { return false; }
            finally { if (older != null) older.Dispose(); }
        }

        // 19041 is a hard floor, not a recommendation, all three pillars of the scheduling side sit on or after this line
        //   EcoQoS suppression since 1809, per-process timer resolution isolation since 2004
        //   Below it timeBeginPeriod is a global side effect and suppression cannot get the efficiency class
        //   We ourselves cannot get IAudioClient3 or some QoS masks either, installing would only write a pile of changes and buy no scheduling
        internal const int OsBuildBaseline = 19041;
        internal const int OsBuildBest = 26100;

        internal static bool OsBelowBaseline()
        {
            int build = Native.OsBuild();
            return build > 0 && build < OsBuildBaseline;
        }

        // Unreadable version means pass, RtlGetVersion failure returns 0 and must not lock people out
        //   The block point is after crash self-heal, historical changes made on the old system are restored cleanly before exiting
        //   Otherwise the user can neither enter the UI nor has any entry to undo the system changes Pavise left behind
        private static bool BlockIfOsTooOld(string dataDir)
        {
            int build = Native.OsBuild();
            if (!OsBelowBaseline()) return false;
            Logger.Log(Lang.T("log.program.11") + build);
            int restored = 0;
            try
            {
                List<string> failed = LegacyPurge.RestorePersistent(dataDir);
                restored = failed == null ? 1 : (failed.Count == 0 ? 1 : 2);
            }
            catch { restored = 2; }
            string body = Lang.F("os.old.body", build, OsBuildBaseline, OsBuildBest);
            if (restored == 2) body += Lang.T("os.old.restorefail");
            PaviseDialog.Warn(null, App.DisplayName, body);
            return true;
        }

        private static bool IsElevated()
        {
            try
            {
                using (var id = System.Security.Principal.WindowsIdentity.GetCurrent())
                    return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        internal static bool TryResetUserData(string dir, Func<bool> stopRuntime,
            out int files, out string failure)
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
                return LegacyPurge.WipeAll(dir, true, Lang.T("t.panelformsettingspage.3"),
                    out files, out failure);
            }
            catch (Exception ex)
            {
                failure = Lang.T("wipe.cleanupfail") + " (" + ex.GetType().Name + ")";
                return false;
            }
        }

    }
}
