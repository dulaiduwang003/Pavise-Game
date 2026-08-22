// @author bdth 2074055628@qq.com
// 文件用途 DirectX UserGpuPreferences 那种 "键=值;键=值;" 串的读写工具 纯文本无状态
using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal static class PrefFieldText
    {
        public static string MergeField(string current, string field, string value)
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

        public static string RemoveField(string current, string field)
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

        public static string ReadField(string current, string field)
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

        public static string RestoreField(string current, string original, string field)
        {
            string want = ReadField(original, field);
            return want == null ? RemoveField(current, field) : MergeField(current, field, want);
        }
    }
}
