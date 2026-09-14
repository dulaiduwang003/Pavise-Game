// @author bdth 2074055628@qq.com
// File purpose Per-game DPI awareness declaration, writes a token into the HKCU compat Layers key, reversible, read back to verify after write
using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace PaviseApp
{
    // When system scaling isn't 100%, a DPI-unaware game's borderless window gets bitmap-stretched by DWM
    //   Buffer size mismatches the screen so it never reaches Independent Flip, only composition: one extra copy, one extra frame of latency
    //   HIGHDPIAWARE makes the system treat it as aware; the window is sized in physical pixels and the buffer matches the screen
    //   Games with their own DPI handling get a shrunken UI, so this is per-game manual only; same key and same write style as FsoTweak
    internal static class DpiTweak
    {
        private const string LayersKey =
            @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";
        private const string Token = "HIGHDPIAWARE";
        private const string Marker = "~";
        private const string ListKey = "DpiExeList";
        private const char ListSep = '';
        private static readonly object sync = new object();

        internal static string[] TrackedExes()
        {
            lock (sync)
            {
                string stored;
                if (!Settings.TryLoadStr(ListKey, out stored))
                    throw new InvalidOperationException("Cannot read the DPI recovery ledger");
                return stored.Split(new[] { ListSep }, StringSplitOptions.RemoveEmptyEntries);
            }
        }

        public static bool HasResidue()
        {
            try { return TrackedExes().Length > 0; }
            catch { return true; }
        }

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

        // At 100% scaling this token changes nothing; the UI uses this to decide whether prompting is pointless
        public static bool ScalingActive()
        {
            try
            {
                using (var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero))
                    return g.DpiX > 96.5f;
            }
            catch { return false; }
        }

        private static bool Track(string exePath, bool aware)
        {
            var kept = new List<string>();
            foreach (string exe in TrackedExes())
                if (!string.Equals(exe, exePath, StringComparison.OrdinalIgnoreCase)) kept.Add(exe);
            if (aware) kept.Add(exePath);
            string next = string.Join(ListSep.ToString(), kept.ToArray());
            string confirmed;
            return Settings.SaveStr(ListKey, next)
                && Settings.TryLoadStr(ListKey, out confirmed) && confirmed == next;
        }

        private static bool IsTracked(string exePath)
        {
            foreach (string exe in TrackedExes())
                if (string.Equals(exe, exePath, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static string ReadLayer(string exePath)
        {
            using (var k = Registry.CurrentUser.OpenSubKey(LayersKey))
            {
                object value = k == null ? null : k.GetValue(exePath);
                if (value != null && !(value is string))
                    throw new InvalidOperationException("Unexpected DPI compatibility-layer value type");
                return value as string;
            }
        }

        private static void WriteLayer(string exePath, string value)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(LayersKey))
            {
                if (k == null) throw new InvalidOperationException("Cannot open DPI compatibility layers");
                if (value.Length == 0) k.DeleteValue(exePath, false);
                else k.SetValue(exePath, value, RegistryValueKind.String);
            }
        }

        private static void RollbackToken(string exePath)
        {
            try
            {
                string current = ReadLayer(exePath);
                if (HasToken(current)) WriteLayer(exePath, RemoveToken(current));
                if (!HasToken(ReadLayer(exePath))) Track(exePath, false);
            }
            catch { }
        }

        public static bool IsAwareForExe(string exePath)
        {
            if (string.IsNullOrEmpty(exePath)) return false;
            lock (sync)
            {
                try { return HasToken(ReadLayer(exePath)); }
                catch (Exception ex)
                {
                    Logger.Log(Lang.T("log.dpi.3") + " " + ex.Message);
                    return false;
                }
            }
        }

        public static bool SetForExe(string exePath, bool aware)
        {
            if (string.IsNullOrEmpty(exePath)) return false;
            lock (sync)
            {
                bool rollback = false;
                try
                {
                    string current = ReadLayer(exePath);
                    bool tracked = IsTracked(exePath);
                    if (HasToken(current) == aware)
                        return aware || !tracked || Track(exePath, false);
                    if (aware && !tracked && !Track(exePath, true)) return false;
                    rollback = aware;
                    WriteLayer(exePath, aware ? AddToken(current) : RemoveToken(current));
                    if (HasToken(ReadLayer(exePath)) != aware)
                        throw new InvalidOperationException("DPI compatibility-layer verification failed");
                    if (aware && !IsTracked(exePath))
                        throw new InvalidOperationException("DPI recovery ledger changed during apply");
                    if (!aware && tracked && !Track(exePath, false)) return false;
                    Logger.Log(Lang.T(aware ? "log.dpi.1" : "log.dpi.2") + " " + exePath);
                    return true;
                }
                catch (Exception ex)
                {
                    if (rollback) RollbackToken(exePath);
                    Logger.Log(Lang.T("log.dpi.3") + " " + ex.Message);
                    return false;
                }
            }
        }

        private static string[] Split(string s)
        {
            if (string.IsNullOrEmpty(s)) return new string[0];
            return s.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        }

        internal static bool HasToken(string s)
        {
            foreach (string t in Split(s))
                if (string.Equals(t, Token, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

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

        internal static string AddToken(string cur)
        {
            var parts = new List<string> { Marker };
            parts.AddRange(OtherTokens(cur));
            parts.Add(Token);
            return string.Join(" ", parts.ToArray());
        }

        internal static string RemoveToken(string cur)
        {
            var others = OtherTokens(cur);
            if (others.Count == 0) return "";
            var parts = new List<string> { Marker };
            parts.AddRange(others);
            return string.Join(" ", parts.ToArray());
        }
    }
}
