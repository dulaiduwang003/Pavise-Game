// @author bdth 2074055628@qq.com
// File purpose Disable system memory compression and page combining; large-memory machines only, fully effective after reboot; the receipt re-enables only what was on
using System;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal static class MemCompressTweak
    {
        private const string OnKey = "MemCompressOffByPavise";
        private const string SnapKey = "PrevMMAgent";
        // Nominal 24GB stays above 23.5GB after hardware reservation; a 16GB machine can't reach it
        private const double MinTotalBytes = 23.5 * 1073741824.0;
        private static readonly object lk = new object();

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryStatusEx
        {
            public uint Length, MemoryLoad;
            public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile,
                TotalVirtual, AvailVirtual, AvailExtendedVirtual;
        }

        [DllImport("kernel32.dll")]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

        public static bool EnabledByPavise { get { return Settings.Load(OnKey, false); } }
        public static bool OwnsState
        { get { return EnabledByPavise || Settings.LoadStr(SnapKey, "").Length > 0; } }

        public static bool RamEligible()
        {
            try
            {
                var status = new MemoryStatusEx();
                status.Length = (uint)Marshal.SizeOf(typeof(MemoryStatusEx));
                return GlobalMemoryStatusEx(ref status) && status.TotalPhys >= MinTotalBytes;
            }
            catch { return false; }
        }

        // Get-MMAgent properties are .NET bool ToString, unaffected by system language
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

        // Snapshot format 1,0 means compression was on and combining off at the time; restore re-enables only what was on
        internal static string RestoreArguments(string snapshot)
        {
            bool compression, combining;
            if (!ParseSnapshot(snapshot, out compression, out combining)) return "";
            string args = "";
            if (compression) args += " -MemoryCompression";
            if (combining) args += " -PageCombining";
            return args;
        }

        private static bool QueryState(out bool compression, out bool combining)
        {
            compression = combining = false;
#if PAVISE_SELFTEST
            if (QueryForTest != null) return QueryForTest(out compression, out combining);
            throw new InvalidOperationException("MMAgent state queries require an injected test double.");
#else
            string output;
            if (!PsRunner.Run(
                "$m = Get-MMAgent; Write-Output ($m.MemoryCompression.ToString() + ',' + $m.PageCombining.ToString())",
                "mmagent-query", 20000, out output)) return false;
            return ParseState(output, out compression, out combining);
#endif
        }

        private static bool RunCommand(string command, string label, out string output)
        {
#if PAVISE_SELFTEST
            if (RunForTest != null) return RunForTest(command, label, out output);
            throw new InvalidOperationException("MMAgent writes require an injected test double.");
#else
            return PsRunner.Run(command, label, 20000, out output);
#endif
        }

        public static bool CurrentlyOff()
        {
            bool compression, combining;
            return QueryState(out compression, out combining) && !compression && !combining;
        }

        public static bool Enable()
        {
            lock (lk)
            {
                // Already in the state this program disabled: accept it as is, the receipt has been there all along
                //   One state query spawns one PowerShell; repeated toggling shouldn't pay that cost
                //   This guard used to sit in the Extreme manifest's ApplySingle; moved here when the manifest was withdrawn
                if (EnabledByPavise) return true;
                bool beforeCompression, beforeCombining;
                if (!QueryState(out beforeCompression, out beforeCombining))
                { Logger.Warn(Lang.T("log.memcompress.1")); return false; }
                // The normal entry skips via CurrentlyOff first; this guards once more against a concurrent change
                // Write neither the snapshot nor the ownership flag; a state already disabled externally stays external
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
                // Exit code is for diagnostics only; whether it took effect is decided by the state query
                RunCommand("Disable-MMAgent -MemoryCompression -PageCombining",
                    "mmagent-off", out output);
                bool compression, combining;
                bool postOk = QueryState(out compression, out combining);
                // CIM and PowerShell exit codes aren't proof of state; a nonzero return is fine
                // As long as the post-check reached the target, keep the original snapshot and own the actual change
                if (postOk && !compression && !combining)
                {
                    Settings.Save(OnKey, true);
                    Logger.Log(Lang.T("log.memcompress.4"));
                    return true;
                }
                // PsRunner already logged the nonzero exit details; give the user only a stable feature-level verdict here
                Logger.Log(Lang.T("log.memcompress.3"));

                // State still matches the original snapshot, so our change didn't land; safe to close the record
                // Otherwise roll back immediately; if rollback fails too, keep the snapshot and let the next restore take over and retry
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
            // Older versions may have left a 0,0 receipt; nothing was on, so there's no physical restore to do
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
                    // Nonzero command still goes by the post-check; an idempotent call that already restored must not pin the receipt here forever
                    if (!RestoreSnapshot(snapshot))
                    { Logger.Log(Lang.T("log.memcompress.5")); return false; }
                }
                // Only close the record once the physical state and both ownership records have been verified
                if (!ClearOwnership())
                { Logger.Log(Lang.T("log.memcompress.5")); return false; }
                Logger.Log(Lang.T("log.memcompress.6"));
                return true;
            }
        }

#if PAVISE_SELFTEST
        internal delegate bool QueryOverride(out bool compression, out bool combining);
        internal delegate bool RunOverride(string command, string label, out string output);
        internal static QueryOverride QueryForTest;
        internal static RunOverride RunForTest;

        internal static void ResetForTest()
        {
            lock (lk)
            {
                QueryForTest = null;
                RunForTest = null;
            }
        }
#endif
    }
}
