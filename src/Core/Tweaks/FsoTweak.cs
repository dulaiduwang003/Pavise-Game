// 作者：bdth 2074055628@qq.com
using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace PaviseApp
{
    // 兼容层在游戏启动时读取，临时对局写入不会即时生效。
    internal static class FsoTweak
    {
        private const string LayersKey =
            @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";
        private const string Token = "DISABLEDXMAXIMIZEDWINDOWEDMODE";
        private const string Marker = "~";
        // 仅记录本程序写入的兼容项。
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

        // 读取失败不能当作目标项已删除。
        private static string ReadLayer(string exePath)
        {
#if PAVISE_SELFTEST
            if (ReadLayerForTest != null) return ReadLayerForTest(exePath);
            throw new InvalidOperationException("Missing in-memory FSO reader");
#else
            using (var k = Registry.CurrentUser.OpenSubKey(LayersKey))
            {
                object value = k == null ? null : k.GetValue(exePath);
                if (value != null && !(value is string))
                    throw new InvalidOperationException("Unexpected FSO compatibility-layer value type");
                return value as string;
            }
#endif
        }

        private static void WriteLayer(string exePath, string value)
        {
#if PAVISE_SELFTEST
            if (WriteLayerForTest != null) { WriteLayerForTest(exePath, value); return; }
            throw new InvalidOperationException("Missing in-memory FSO writer");
#else
            using (var k = Registry.CurrentUser.CreateSubKey(LayersKey))
            {
                if (k == null) throw new InvalidOperationException("Cannot open FSO compatibility layers");
                if (value.Length == 0) k.DeleteValue(exePath, false);
                else k.SetValue(exePath, value, RegistryValueKind.String);
            }
#endif
        }

        private static void RollbackToken(string exePath)
        {
            try
            {
                // 合并最新兼容层，保留外部新增选项。
                string current = ReadLayer(exePath);
                if (HasToken(current)) WriteLayer(exePath, RemoveToken(current));
                if (!HasToken(ReadLayer(exePath))) Track(exePath, false);
            }
            catch { }
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
                        return disableFso || !tracked || Track(exePath, false);
                    }
                    // 先落盘所有权记录，再修改系统。
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

        private static string AddToken(string cur)
        {
            var parts = new List<string> { Marker };
            parts.AddRange(OtherTokens(cur));
            parts.Add(Token);
            return string.Join(" ", parts.ToArray());
        }

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
