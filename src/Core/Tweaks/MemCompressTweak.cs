// 作者：bdth 2074055628@qq.com
using System;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal static class MemCompressTweak
    {
        private const string OnKey = "MemCompressOffByPavise";
        private const string SnapKey = "PrevMMAgent";
        private const ulong MinInstalledKb = 24UL * 1024 * 1024;
        private static readonly object lk = new object();
        private static bool commandsUnavailable;
        internal const string UnavailableOutput = "PAVISE_MMAGENT_UNAVAILABLE";
        // 显式加载系统模块，模块不可用不能误判功能关闭。
        private const string ModuleScript =
            "try { Import-Module -Name ([IO.Path]::Combine($PSHOME, 'Modules', 'MMAgent', 'MMAgent.psd1')) -ErrorAction Stop -WarningAction SilentlyContinue | Out-Null } "
            + "catch { Write-Output '" + UnavailableOutput + "'; exit 0 }\r\n";
        internal const string QueryScript = ModuleScript
            + "$mmQuery = Get-Command -Name 'MMAgent\\Get-MMAgent' -ErrorAction SilentlyContinue;\r\n"
            + "if ($null -eq $mmQuery) { Write-Output '" + UnavailableOutput + "'; exit 0 }\r\n"
            + "$m = & $mmQuery -ErrorAction Stop;\r\n"
            + "if ($m.MemoryCompression -isnot [bool] -or $m.PageCombining -isnot [bool]) { Write-Output '" + UnavailableOutput + "'; exit 0 }\r\n"
            + "Write-Output ($m.MemoryCompression.ToString() + ',' + $m.PageCombining.ToString())";

        [DllImport("kernel32.dll")]
        private static extern bool GetPhysicallyInstalledSystemMemory(out ulong totalMemoryInKilobytes);

        public static bool EnabledByPavise { get { return Settings.Load(OnKey, false); } }
        public static bool OwnsState
        { get { return EnabledByPavise || Settings.LoadStr(SnapKey, "").Length > 0; } }

        public static bool RamEligible()
        {
            ulong installedKb;
            return TryInstalledMemory(out installedKb) && RamEligible(installedKb);
        }

        internal static bool RamEligible(ulong installedKb) { return installedKb >= MinInstalledKb; }

        internal static bool TryInstalledMemory(out ulong installedKb)
        {
            installedKb = 0;
            try { return GetPhysicallyInstalledSystemMemory(out installedKb) && installedKb > 0; }
            catch { return false; }
        }

        internal static bool ParseState(string output, out bool compression, out bool combining)
        {
            compression = combining = false;
            if (string.IsNullOrEmpty(output)) return false;
            string[] parts = output.Trim().Split(',');
            if (parts.Length < 2) return false;
            bool ok1 = bool.TryParse(parts[0].Trim(), out compression);
            bool ok2 = bool.TryParse(parts[1].Trim(), out combining);
            return ok1 && ok2;
        }

        internal static bool ParseSnapshot(string snapshot, out bool compression, out bool combining)
        {
            compression = combining = false;
            if (string.IsNullOrEmpty(snapshot)) return false;
            string[] parts = snapshot.Split(',');
            if (parts.Length != 2
                || (parts[0] != "0" && parts[0] != "1")
                || (parts[1] != "0" && parts[1] != "1")) return false;
            compression = parts[0] == "1";
            combining = parts[1] == "1";
            return true;
        }

        internal static bool SnapshotRestored(string snapshot, bool compression, bool combining)
        {
            bool wantedCompression, wantedCombining;
            return ParseSnapshot(snapshot, out wantedCompression, out wantedCombining)
                && (!wantedCompression || compression)
                && (!wantedCombining || combining);
        }

        internal static string RestoreArguments(string snapshot)
        {
            bool compression, combining;
            if (!ParseSnapshot(snapshot, out compression, out combining)) return "";
            string args = "";
            if (compression) args += " -MemoryCompression";
            if (combining) args += " -PageCombining";
            return args;
        }

        internal static bool QueryState(out bool compression, out bool combining)
        {
            compression = combining = false;
#if PAVISE_SELFTEST
            if (QueryForTest != null) return QueryForTest(out compression, out combining);
#endif
            lock (lk)
            {
                string output;
                if (!ExecuteCapabilityScript(QueryScript, "mmagent-query", out output)) return false;
                return ParseState(output, out compression, out combining);
            }
        }

        private static bool ExecuteCapabilityScript(string script, string label, out string output)
        {
            output = "";
            if (commandsUnavailable) return false;
            bool ok;
#if PAVISE_SELFTEST
            if (ScriptForTest == null) throw new InvalidOperationException("MMAgent scripts require an injected test double.");
            ok = ScriptForTest(script, label, out output);
#else
            ok = PsRunner.Run(script, label, 20000, out output);
#endif
            if (!ok) return false;
            if (String.Equals((output ?? "").Trim(), UnavailableOutput, StringComparison.Ordinal))
            { commandsUnavailable = true; return false; }
            return true;
        }

        private static bool RunCommand(string command, string label, out string output)
        {
#if PAVISE_SELFTEST
            if (RunForTest != null) return RunForTest(command, label, out output);
#endif
            int separator = command.IndexOf(' ');
            string name = separator < 0 ? command : command.Substring(0, separator);
            if (name != "Disable-MMAgent" && name != "Enable-MMAgent") throw new ArgumentException("Unexpected MMAgent command");
            string arguments = separator < 0 ? "" : command.Substring(separator);
            string script = ModuleScript
                + "$mmCommand = Get-Command -Name 'MMAgent\\" + name + "' -ErrorAction SilentlyContinue;\r\n"
                + "if ($null -eq $mmCommand) { Write-Output '" + UnavailableOutput + "'; exit 0 }\r\n"
                + "& $mmCommand" + arguments + " -ErrorAction Stop";
            return ExecuteCapabilityScript(script, label, out output);
        }

        public static bool CurrentlyOff()
        {
            bool compression, combining;
            return QueryState(out compression, out combining) && !compression && !combining;
        }

        public static bool Enable(bool verifyCurrent = false)
        {
            lock (lk)
            {
                if (EnabledByPavise && !verifyCurrent) return true;
                bool beforeCompression, beforeCombining;
                if (!QueryState(out beforeCompression, out beforeCombining))
                { Logger.Warn(Lang.T("log.memcompress.1")); return false; }
                if (!beforeCompression && !beforeCombining) return true;
                string snapshot;
                if (!Settings.TryLoadStr(SnapKey, out snapshot))
                { Logger.Log(Lang.T("log.memcompress.2")); return false; }
                if (snapshot.Length == 0)
                {
                    snapshot = (beforeCompression ? "1" : "0") + ","
                        + (beforeCombining ? "1" : "0");
                    Settings.SaveStr(SnapKey, snapshot);
                    if (Settings.LoadStr(SnapKey, "") != snapshot)
                    { Logger.Log(Lang.T("log.memcompress.2")); return false; }
                }
                else
                {
                    bool originalCompression, originalCombining;
                    if (!ParseSnapshot(snapshot, out originalCompression, out originalCombining))
                    { Logger.Log(Lang.T("log.memcompress.2")); return false; }
                }
                string output;
                // 是否生效以回读状态为准，退出码仅用于诊断。
                RunCommand("Disable-MMAgent -MemoryCompression -PageCombining",
                    "mmagent-off", out output);
                bool compression, combining;
                bool postOk = QueryState(out compression, out combining);
                if (postOk && !compression && !combining)
                {
                    Settings.Save(OnKey, true);
                    Logger.Log(Lang.T("log.memcompress.4"));
                    return true;
                }
                Logger.Log(Lang.T("log.memcompress.3"));

                bool recovered = postOk && SnapshotRestored(snapshot, compression, combining);
                if (!recovered) recovered = RestoreSnapshot(snapshot);
                if (recovered)
                {
                    if (!ClearOwnership()) Logger.Log(Lang.T("log.memcompress.5"));
                }
                else Logger.Log(Lang.T("log.memcompress.5"));
                return false;
            }
        }

        private static bool RestoreSnapshot(string snapshot)
        {
            bool wantedCompression, wantedCombining;
            if (!ParseSnapshot(snapshot, out wantedCompression, out wantedCombining)) return false;
            string args = RestoreArguments(snapshot);
            if (args.Length == 0) return true;
            string output;
            RunCommand("Enable-MMAgent" + args, "mmagent-restore", out output);
            bool compression, combining;
            return QueryState(out compression, out combining)
                && SnapshotRestored(snapshot, compression, combining);
        }

        private static bool ClearOwnership()
        {
            bool flagSaved = Settings.Save(OnKey, false);
            bool snapshotSaved = Settings.SaveStr(SnapKey, "");
            string snapshot;
            return flagSaved && snapshotSaved && !Settings.Load(OnKey, true)
                && Settings.TryLoadStr(SnapKey, out snapshot) && snapshot.Length == 0;
        }

        public static bool Restore()
        {
            lock (lk)
            {
                string snapshot = Settings.LoadStr(SnapKey, "");
                if (snapshot.Length > 0)
                {
                    if (!RestoreSnapshot(snapshot))
                    { Logger.Log(Lang.T("log.memcompress.5")); return false; }
                }
                // 物理状态和所有权记录均确认后才清除收据。
                if (!ClearOwnership())
                { Logger.Log(Lang.T("log.memcompress.5")); return false; }
                Logger.Log(Lang.T("log.memcompress.6"));
                return true;
            }
        }

#if PAVISE_SELFTEST
        internal delegate bool QueryOverride(out bool compression, out bool combining);
        internal delegate bool RunOverride(string command, string label, out string output);
        internal delegate bool ScriptOverride(string script, string label, out string output);
        internal static QueryOverride QueryForTest;
        internal static RunOverride RunForTest;
        internal static ScriptOverride ScriptForTest;

        internal static void ResetForTest()
        {
            lock (lk)
            {
                QueryForTest = null;
                RunForTest = null;
                ScriptForTest = null;
                commandsUnavailable = false;
            }
        }
#endif
    }
}
