// @author bdth 2074055628@qq.com
// File purpose Per-game disable of Windows fullscreen optimizations, writes a token into the HKCU compat Layers key, reversible, read back to verify after write
using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace PaviseApp
{
    // Per-game persistent preference; reads and writes the HKCU compat layer string directly, bypasses ReversibleReg and stays out of the match snapshot
    //   The compat layer is read at exe launch, so it takes effect on the game's next launch; a temporary match-time write does nothing this match, hence no mount and no restore
    internal static class FsoTweak
    {
        private const string LayersKey =
            @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";
        // Compat layer token for disabling fullscreen optimizations; the leading ~ marks a user-level compat layer
        private const string Token = "DISABLEDXMAXIMIZEDWINDOWEDMODE";
        private const string Marker = "~";
        // The ledger only records exes Pavise itself wrote; fullscreen optimizations the user disabled in system properties are neither tracked nor restored here
        //   Separator is the unit separator, which can't appear in a path, to avoid clashing with semicolons and spaces in paths
        private const string ListKey = "FsoExeList";
        private const char ListSep = '\u001F';
        private static readonly object sync = new object();

#if PAVISE_SELFTEST
        internal static Func<string, string> ReadLayerForTest;
        internal static Action<string, string> WriteLayerForTest;
        internal static Func<string, bool> SaveTrackedForTest;
#endif

        internal static string[] TrackedExes()
        {
            lock (sync)
            {
                string stored;
                if (!Settings.TryLoadStr(ListKey, out stored))
                    throw new InvalidOperationException("Cannot read the FSO recovery ledger");
                return stored.Split(new[] { ListSep }, StringSplitOptions.RemoveEmptyEntries);
            }
        }

        public static bool HasResidue()
        {
            try { return TrackedExes().Length > 0; }
            catch { return true; } // Unreadable ownership is not an empty ledger
        }

        // Wipe all settings undoes each ledger entry; the undo goes through the same RemoveToken path as the UI and doesn't touch other tokens
        public static bool RestoreAll()
        {
            lock (sync)
            {
                try
                {
                    bool all = true;
                    foreach (string exe in TrackedExes())
                        if (!SetForExe(exe, false)) all = false;
                    return all && TrackedExes().Length == 0;
                }
                catch { return false; }
            }
        }

        private static bool Track(string exePath, bool disabled)
        {
            var kept = new List<string>();
            foreach (string exe in TrackedExes())
                if (!string.Equals(exe, exePath, StringComparison.OrdinalIgnoreCase)) kept.Add(exe);
            if (disabled) kept.Add(exePath);
            string next = string.Join(ListSep.ToString(), kept.ToArray());
            bool saved;
#if PAVISE_SELFTEST
            if (SaveTrackedForTest != null) saved = SaveTrackedForTest(next);
            else
#endif
                saved = Settings.SaveStr(ListKey, next);
            string confirmed;
            return saved && Settings.TryLoadStr(ListKey, out confirmed) && confirmed == next;
        }

        private static bool IsTracked(string exePath)
        {
            foreach (string exe in TrackedExes())
                if (string.Equals(exe, exePath, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // Read failure and token-absent must be told apart, especially a rejected read right after removal
        // Can't be taken as restore confirmed, nor clear its ledger entry
        private static string ReadLayer(string exePath)
        {
#if PAVISE_SELFTEST
            if (ReadLayerForTest != null) return ReadLayerForTest(exePath);
            if (WriteLayerForTest != null) throw new InvalidOperationException("Missing in-memory FSO reader");
#endif
            using (var k = Registry.CurrentUser.OpenSubKey(LayersKey))
            {
                object value = k == null ? null : k.GetValue(exePath);
                if (value != null && !(value is string))
                    throw new InvalidOperationException("Unexpected FSO compatibility-layer value type");
                return value as string;
            }
        }

        private static void WriteLayer(string exePath, string value)
        {
#if PAVISE_SELFTEST
            if (WriteLayerForTest != null) { WriteLayerForTest(exePath, value); return; }
            if (ReadLayerForTest != null) throw new InvalidOperationException("Missing in-memory FSO writer");
#endif
            using (var k = Registry.CurrentUser.CreateSubKey(LayersKey))
            {
                if (k == null) throw new InvalidOperationException("Cannot open FSO compatibility layers");
                if (value.Length == 0) k.DeleteValue(exePath, false);
                else k.SetValue(exePath, value, RegistryValueKind.String);
            }
        }

        private static void RollbackToken(string exePath)
        {
            try
            {
                // Merge against the latest layer string rather than replaying a stale value
                // on top of compat settings someone else has written
                string current = ReadLayer(exePath);
                if (HasToken(current)) WriteLayer(exePath, RemoveToken(current));
                if (!HasToken(ReadLayer(exePath))) Track(exePath, false);
            }
            catch { } // A failed rollback deliberately retains the recovery ledger
        }

        public static bool IsDisabledForExe(string exePath)
        {
            if (string.IsNullOrEmpty(exePath)) return false;
            lock (sync)
            {
                try { return HasToken(ReadLayer(exePath)); }
                catch (Exception ex)
                {
                    Logger.Log(Lang.T("log.fso.3") + " " + ex.Message);
                    return false;
                }
            }
        }

        public static bool SetForExe(string exePath, bool disableFso)
        {
            if (string.IsNullOrEmpty(exePath)) return false;
            lock (sync)
            {
                bool rollback = false;
                try
                {
                    string current = ReadLayer(exePath);
                    bool tracked = IsTracked(exePath);
                    if (HasToken(current) == disableFso)
                    {
                        // A preference the user already had doesn't count as a Pavise-owned change
                        // A tracked entry restored earlier still needs cleanup
                        return disableFso || !tracked || Track(exePath, false);
                    }
                    // Before touching Windows, confirm the persistent restore record has landed
                    // Don't change first and hope the ownership save succeeds later
                    if (disableFso && !tracked && !Track(exePath, true)) return false;
                    rollback = disableFso;
                    WriteLayer(exePath, disableFso ? AddToken(current) : RemoveToken(current));
                    if (HasToken(ReadLayer(exePath)) != disableFso)
                        throw new InvalidOperationException("FSO compatibility-layer verification failed");
                    if (disableFso && !IsTracked(exePath))
                        throw new InvalidOperationException("FSO recovery ledger changed during apply");
                    if (!disableFso && tracked && !Track(exePath, false)) return false;
                    Logger.Log(Lang.T(disableFso ? "log.fso.1" : "log.fso.2") + " " + exePath);
                    return true;
                }
                catch (Exception ex)
                {
                    if (rollback) RollbackToken(exePath);
                    Logger.Log(Lang.T("log.fso.3") + " " + ex.Message);
                    return false;
                }
            }
        }

        private static string[] Split(string s)
        {
            if (string.IsNullOrEmpty(s)) return new string[0];
            return s.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        }

        private static bool HasToken(string s)
        {
            foreach (string t in Split(s))
                if (string.Equals(t, Token, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // Collect tokens other than the marker and the target token, dropping duplicate ~ along the way, preserving other compat layer settings
        //   The existing value may be malformed (~ not first or repeated); drop every ~ here and let the caller put one back in front
        private static List<string> OtherTokens(string cur)
        {
            var others = new List<string>();
            foreach (string t in Split(cur))
            {
                if (t == Marker) continue;
                if (string.Equals(t, Token, StringComparison.OrdinalIgnoreCase)) continue;
                others.Add(t);
            }
            return others;
        }

        // Append the token keeping the others; output always has exactly one ~ in front and the target token at the end
        private static string AddToken(string cur)
        {
            var parts = new List<string> { Marker };
            parts.AddRange(OtherTokens(cur));
            parts.Add(Token);
            return string.Join(" ", parts.ToArray());
        }

        // Remove only our own token, leave the others; if other tokens remain put back the single ~, if only the marker is left return empty so the whole value gets deleted
        private static string RemoveToken(string cur)
        {
            var others = OtherTokens(cur);
            if (others.Count == 0) return "";
            var parts = new List<string> { Marker };
            parts.AddRange(others);
            return string.Join(" ", parts.ToArray());
        }
    }
}
