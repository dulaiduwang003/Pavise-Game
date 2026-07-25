// @author bdth 2074055628@qq.com
// 文件用途 管理按游戏程序保存的图形兼容设置

using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace AegisApp
{
    internal static class GameExeTweaks
    {
        private const string GpuKey = @"SOFTWARE\Microsoft\DirectX\UserGpuPreferences";
        private const string FsoKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";
        private const string BakKey = @"Software\Aegis\ExeTweakBak";
        private const string FsoFlag = "DISABLEDXMAXIMIZEDWINDOWEDMODE";
        private static readonly object lk = new object();

        public static void ApplyForGame(string exePath, bool gpuHighPerf, bool disableFso)
        {
            if (string.IsNullOrEmpty(exePath)) return;
            lock (lk)
            {
                if (gpuHighPerf) SetGpuPref(exePath);
                if (disableFso) SetFso(exePath);
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
                            string target = string.Equals(kind, "gpu", StringComparison.OrdinalIgnoreCase) ? GpuKey : FsoKey;
                            string orig = bak.GetValue(name) as string ?? ReversibleReg.Absent;
                            if (RestoreValue(target, exePath, orig))
                            {
                                n++;
                                try { bak.DeleteValue(name, false); } catch { }
                            }
                        }
                        if (n > 0) Logger.Log("已还原 " + n + " 项逐游戏" + (kind == "gpu" ? " GPU 偏好" : "全屏优化") + "设置");
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

        // UserGpuPreferences 里一个值是多个 "字段=值;" 拼起来的，Windows 会往同一个值里
        // 放别的字段——逐游戏的「窗口化优化」开关就存在 AppStatus 里。所以只能替换
        // 自己那一段：整值覆盖会把用户在 Windows 设置里做过的选择静默抹掉。
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

        // Layers 是空格分隔的兼容性标志列表，同样要按标志还原：
        // 只摘掉自己加的那一个，保留用户后来在"兼容性"选项卡里加的（RUNASADMIN 等）。
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
            // 只剩一个孤零零的 "~" 前缀就等于没有任何标志了
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

        // 还原也必须按字段来：直接写回整条旧快照，会把用户在我们写入之后
        // 才在 Windows 设置里改动的其它字段（比如 AppStatus）一起冲掉。
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

        private static void SetGpuPref(string exePath)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(GpuKey))
                {
                    if (k == null) return;
                    object curObj = k.GetValue(exePath);
                    string cur = curObj as string;
                    if (curObj != null && cur == null) return;
                    if (string.Equals(ReadField(cur, "GpuPreference"), "2", StringComparison.Ordinal)) return;
                    if (!Backup("gpu", exePath, cur)) return;
                    k.SetValue(exePath, MergeField(cur, "GpuPreference", "2"), RegistryValueKind.String);
                    Logger.Log("GPU 偏好 → 高性能：" + exePath + "（下次启动该游戏生效）");
                }
            }
            catch { }
        }

        private static void SetFso(string exePath)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(FsoKey))
                {
                    if (k == null) return;
                    object curObj = k.GetValue(exePath);
                    string cur = curObj as string;
                    if (curObj != null && cur == null) return;
                    if (cur != null && cur.IndexOf(FsoFlag, StringComparison.OrdinalIgnoreCase) >= 0) return;
                    if (!Backup("fso", exePath, cur)) return;
                    string val = string.IsNullOrEmpty(cur) ? "~ " + FsoFlag : cur.TrimEnd() + " " + FsoFlag;
                    k.SetValue(exePath, val, RegistryValueKind.String);
                    Logger.Log("关闭全屏优化：" + exePath + "（下次启动该游戏生效）");
                }
            }
            catch { }
        }

        private static bool Backup(string kind, string exePath, string original)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(BakKey))
                {
                    if (k == null) return false;
                    string name = kind + "|" + exePath;
                    if (k.GetValue(name) != null) return true;
                    k.SetValue(name, original ?? ReversibleReg.Absent, RegistryValueKind.String);
                    return true;
                }
            }
            catch { return false; }
        }
    }
}
