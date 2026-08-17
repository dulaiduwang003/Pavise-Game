// @author bdth 2074055628@qq.com
// 文件用途 按游戏程序图形设置的历史残留还原与字段工具 gpu/igpu/fso 三类写入路径均已退役
// gpu(强制独显)1.8.1.0 下架:按 exe 写注册表且下次启动才生效 属持久改动 不该混在对局链里装作会话功能

using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static class GameExeTweaks
    {
        private const string GpuKey = @"SOFTWARE\Microsoft\DirectX\UserGpuPreferences";
        private const string FsoKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";
        private const string BakKey = @"Software\Pavise\ExeTweakBak";
        private const string FsoFlag = "DISABLEDXMAXIMIZEDWINDOWEDMODE";
        private static readonly object lk = new object();

        public static bool HasKindResidue(string kind)
        {
            lock (lk)
            {
                try
                {
                    using (var bak = Registry.CurrentUser.OpenSubKey(BakKey))
                    {
                        if (bak == null) return false;
                        foreach (string name in bak.GetValueNames())
                        {
                            int bar = name.IndexOf('|');
                            if (bar <= 0) continue;
                            if (string.Equals(name.Substring(0, bar), kind, StringComparison.OrdinalIgnoreCase))
                                return true;
                        }
                    }
                }
                catch { }
                return false;
            }
        }

        public static void RestoreKind(string kind)
        {
            lock (lk)
            {
                try
                {
                    using (var bak = Registry.CurrentUser.OpenSubKey(BakKey, true))
                    {
                        if (bak == null) return;
                        int n = 0;
                        foreach (string name in bak.GetValueNames())
                        {
                            int bar = name.IndexOf('|');
                            if (bar <= 0) { try { bak.DeleteValue(name, false); } catch { } continue; }
                            if (!string.Equals(name.Substring(0, bar), kind, StringComparison.OrdinalIgnoreCase)) continue;
                            string exePath = name.Substring(bar + 1);
                            string target = string.Equals(kind, "fso", StringComparison.OrdinalIgnoreCase) ? FsoKey : GpuKey;
                            string orig = bak.GetValue(name) as string ?? ReversibleReg.Absent;
                            if (RestoreValue(target, exePath, orig))
                            {
                                n++;
                                try { bak.DeleteValue(name, false); } catch { }
                            }
                        }
                        if (n > 0) Logger.Log(Lang.T("log.gameexetweaks.1") + n + Lang.T("t.gamemodeenv.30") + (kind == "gpu" ? Lang.T("t.legacypurge.25") : kind == "igpu" ? Lang.T("t.legacypurge.26") : Lang.T("t.legacypurge.27")) + Lang.T("nav.set"));
                    }
                }
                catch { }
            }
        }

        private static bool RestoreValue(string key, string exePath, string orig)
        {
            bool isGpu = string.Equals(key, GpuKey, StringComparison.OrdinalIgnoreCase);
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(key, true))
                {
                    if (k == null) return true;
                    string cur = k.GetValue(exePath) as string;
                    string next = isGpu
                        ? RestoreField(cur, orig == ReversibleReg.Absent ? "" : orig, "GpuPreference")
                        : RestoreLayer(cur, orig == ReversibleReg.Absent ? "" : orig);
                    if (next.Length == 0)
                    {
                        if (k.GetValue(exePath) != null) k.DeleteValue(exePath, false);
                    }
                    else k.SetValue(exePath, next, RegistryValueKind.String);
                    return true;
                }
            }
            catch { return false; }
        }

        internal static string MergeField(string current, string field, string value)
        {
            var parts = new List<string>();
            bool replaced = false;
            if (!string.IsNullOrEmpty(current))
            {
                foreach (string raw in current.Split(';'))
                {
                    string seg = raw.Trim();
                    if (seg.Length == 0) continue;
                    int eq = seg.IndexOf('=');
                    string key = eq > 0 ? seg.Substring(0, eq).Trim() : seg;
                    if (eq > 0 && string.Equals(key, field, StringComparison.OrdinalIgnoreCase))
                    {
                        if (replaced) continue;
                        parts.Add(field + "=" + value);
                        replaced = true;
                    }
                    else parts.Add(seg);
                }
            }
            if (!replaced) parts.Add(field + "=" + value);
            return string.Join(";", parts.ToArray()) + ";";
        }

        internal static string RestoreLayer(string current, string original)
        {
            bool hadFlag = original != null
                && original.IndexOf(FsoFlag, StringComparison.OrdinalIgnoreCase) >= 0;
            if (hadFlag) return string.IsNullOrEmpty(current) ? original : current;

            var parts = new List<string>();
            foreach (string raw in (current ?? "").Split(' '))
            {
                string seg = raw.Trim();
                if (seg.Length == 0) continue;
                if (string.Equals(seg, FsoFlag, StringComparison.OrdinalIgnoreCase)) continue;
                parts.Add(seg);
            }

            if (parts.Count == 0 || (parts.Count == 1 && parts[0] == "~")) return "";
            return string.Join(" ", parts.ToArray());
        }

        internal static string RemoveField(string current, string field)
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(current))
            {
                foreach (string raw in current.Split(';'))
                {
                    string seg = raw.Trim();
                    if (seg.Length == 0) continue;
                    int eq = seg.IndexOf('=');
                    string key = eq > 0 ? seg.Substring(0, eq).Trim() : seg;
                    if (eq > 0 && string.Equals(key, field, StringComparison.OrdinalIgnoreCase)) continue;
                    parts.Add(seg);
                }
            }
            if (parts.Count == 0) return "";
            return string.Join(";", parts.ToArray()) + ";";
        }

        internal static string RestoreField(string current, string original, string field)
        {
            string want = ReadField(original, field);
            return want == null ? RemoveField(current, field) : MergeField(current, field, want);
        }

        internal static string ReadField(string current, string field)
        {
            if (string.IsNullOrEmpty(current)) return null;
            foreach (string raw in current.Split(';'))
            {
                string seg = raw.Trim();
                int eq = seg.IndexOf('=');
                if (eq <= 0) continue;
                if (string.Equals(seg.Substring(0, eq).Trim(), field, StringComparison.OrdinalIgnoreCase))
                    return seg.Substring(eq + 1).Trim();
            }
            return null;
        }

        // 游戏被显式钉在省电 GPU(核显)时为真 供探测器避免无谓唤醒休眠独显
        public static bool PrefersIntegrated(string exePath)
        {
            if (string.IsNullOrEmpty(exePath)) return false;
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(GpuKey))
                {
                    if (k == null) return false;
                    return string.Equals(
                        ReadField(k.GetValue(exePath) as string, "GpuPreference"), "1",
                        StringComparison.Ordinal);
                }
            }
            catch { return false; }
        }

    }
}
