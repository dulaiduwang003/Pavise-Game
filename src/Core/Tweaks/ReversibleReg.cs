// @author bdth 2074055628@qq.com
// 文件用途 为注册表改动保存原值并提供可靠恢复

using System;
using Microsoft.Win32;

namespace PaviseApp
{
    internal sealed class ReversibleReg
    {
        public const string Absent = "__pavise_absent__";
        private const char AppliedSep = '\u001F';

        private readonly RegistryKey hive;
        private readonly string subKey;
        private readonly string valName;
        private readonly RegistryValueKind kind;
        private readonly string slot;

        public ReversibleReg(RegistryKey hive, string subKey, string valName, RegistryValueKind kind, string slot)
        {
            this.hive = hive; this.subKey = subKey; this.valName = valName; this.kind = kind; this.slot = slot;
        }

        public bool HasBackup { get { return Settings.LoadStr(slot, "").Length > 0; } }

        public bool Apply(object newVal)
        {
            try
            {
                using (var k = hive.CreateSubKey(subKey))
                {
                    if (k == null) return false;
                    string original = OriginalPart(Settings.LoadStr(slot, ""));
                    if (original.Length == 0)
                    {
                        object cur = k.GetValue(valName);
                        if (cur != null)
                        {
                            RegistryValueKind curKind = RegistryValueKind.Unknown;
                            try { curKind = k.GetValueKind(valName); } catch { }
                            if (curKind != kind)
                            {
                                Logger.Log(valName + " 原值类型异常 " + curKind + " 跳过此项调整");
                                return false;
                            }
                        }
                        original = Encode(cur);
                        Settings.SaveStr(slot, original);
                        if (Settings.LoadStr(slot, "") != original)
                        {
                            Logger.Log("无法持久化 " + valName + " 原值快照 已取消写入");
                            return false;
                        }
                    }
                    k.SetValue(valName, newVal, kind);
                    object actual = k.GetValue(valName);
                    if (actual == null) return false;
                    bool ok;
                    if (kind == RegistryValueKind.DWord)
                        ok = Convert.ToInt64(actual) == Convert.ToInt64(newVal);
                    else if (kind == RegistryValueKind.Binary)
                        ok = BytesEqual((byte[])actual, (byte[])newVal);
                    else
                        ok = string.Equals(actual.ToString(), newVal == null ? "" : newVal.ToString(), StringComparison.Ordinal);
                    if (ok) Settings.SaveStr(slot, original + AppliedSep + Encode(newVal));
                    return ok;
                }
            }
            catch { return false; }
        }

        private string Encode(object value)
        {
            if (value == null) return Absent;
            if (kind == RegistryValueKind.Binary) return "b" + Convert.ToBase64String((byte[])value);
            return "=" + value;
        }

        private static string OriginalPart(string stored)
        {
            int sep = stored.LastIndexOf(AppliedSep);
            return sep < 0 ? stored : stored.Substring(0, sep);
        }

        private static string AppliedPart(string stored)
        {
            int sep = stored.LastIndexOf(AppliedSep);
            return sep < 0 ? "" : stored.Substring(sep + 1);
        }

        private bool TryDecode(string repr, out object value)
        {
            value = null;
            try
            {
                if (repr.Length == 0 || repr == Absent) return false;
                if (kind == RegistryValueKind.Binary)
                {
                    if (repr[0] != 'b') return false;
                    value = Convert.FromBase64String(repr.Substring(1));
                    return true;
                }
                string v = repr[0] == '=' ? repr.Substring(1) : repr;
                if (kind == RegistryValueKind.DWord)
                {
                    long n;
                    if (!long.TryParse(v, out n)) return false;
                    value = unchecked((int)n);
                    return true;
                }
                value = v;
                return true;
            }
            catch { return false; }
        }

        private bool SameByKind(object a, object b)
        {
            try
            {
                if (kind == RegistryValueKind.DWord) return Convert.ToInt64(a) == Convert.ToInt64(b);
                if (kind == RegistryValueKind.Binary) return BytesEqual(a as byte[], b as byte[]);
                return string.Equals(a.ToString(), b == null ? "" : b.ToString(), StringComparison.Ordinal);
            }
            catch { return false; }
        }

        private static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a == null || b == null) return a == b;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        public bool Matches(object expected)
        {
            try
            {
                using (var k = hive.OpenSubKey(subKey))
                {
                    object cur = k == null ? null : k.GetValue(valName);
                    if (cur == null) return false;
                    if (kind == RegistryValueKind.DWord)
                        return Convert.ToInt64(cur) == Convert.ToInt64(expected);
                    if (kind == RegistryValueKind.Binary)
                        return BytesEqual(cur as byte[], expected as byte[]);
                    return string.Equals(cur.ToString(), expected == null ? "" : expected.ToString(), StringComparison.Ordinal);
                }
            }
            catch { return false; }
        }

        public bool Restore()
        {
            string stored = Settings.LoadStr(slot, "");
            if (stored.Length == 0) return true;
            string s = OriginalPart(stored);
            string appliedRepr = AppliedPart(stored);

            bool absent = s == Absent;
            object val = null;
            if (!absent)
            {
                if (kind == RegistryValueKind.Binary)
                {
                    if (s.Length < 1 || s[0] != 'b')
                    {
                        Settings.SaveStr(slot, "");
                        Logger.Log("注册表快照损坏 放弃还原 " + valName + " 二进制快照格式不符");
                        return false;
                    }
                    try { val = Convert.FromBase64String(s.Substring(1)); }
                    catch
                    {
                        Settings.SaveStr(slot, "");
                        Logger.Log("注册表快照损坏 放弃还原 " + valName + " 二进制快照解码失败");
                        return false;
                    }
                }
                else
                {
                    string v = s[0] == '=' ? s.Substring(1) : s;
                    if (kind == RegistryValueKind.DWord)
                    {
                        long n;
                        if (!long.TryParse(v, out n))
                        {
                            Settings.SaveStr(slot, "");
                            Logger.Log("注册表快照损坏 放弃还原 " + valName + " 记录值 \"" + v + "\" ");
                            return false;
                        }
                        val = unchecked((int)n);
                    }
                    else val = v;
                }
            }

            try
            {
                bool restored = false;
                using (var k = hive.OpenSubKey(subKey, true))
                {

                    if (k != null && appliedRepr.Length > 0)
                    {
                        object appliedVal;
                        if (TryDecode(appliedRepr, out appliedVal))
                        {
                            object cur = k.GetValue(valName);
                            if (cur == null || !SameByKind(cur, appliedVal))
                            {
                                Settings.SaveStr(slot, "");
                                Logger.Log(valName + " 当前值已被其它程序改过 尊重现值 跳过还原并清除快照");
                                return Settings.LoadStr(slot, "").Length == 0;
                            }
                        }
                    }
                    if (k == null) restored = true;
                    else if (absent)
                    {
                        if (k.GetValue(valName) != null) k.DeleteValue(valName, false);
                        restored = k.GetValue(valName) == null;
                    }
                    else
                    {
                        k.SetValue(valName, val, kind);
                        object actual = k.GetValue(valName);
                        RegistryValueKind actualKind = RegistryValueKind.Unknown;
                        try { actualKind = k.GetValueKind(valName); } catch { }
                        restored = actual != null && actualKind == kind;
                        if (restored && kind == RegistryValueKind.DWord)
                            restored = Convert.ToInt64(actual) == Convert.ToInt64(val);
                        else if (restored && kind == RegistryValueKind.Binary)
                            restored = BytesEqual((byte[])actual, (byte[])val);
                        else if (restored)
                            restored = string.Equals(actual.ToString(), val == null ? "" : val.ToString(), StringComparison.Ordinal);
                    }
                }
                if (!restored)
                {
                    Logger.Log("还原 " + valName + " 后回读不一致 快照保留待下次重试");
                    return false;
                }
                Settings.SaveStr(slot, "");
                return Settings.LoadStr(slot, "").Length == 0;
            }
            catch
            {
                Logger.Log("还原 " + valName + " 失败 多半是权限不足 快照保留待下次重试");
                return false;
            }
        }
    }
}
