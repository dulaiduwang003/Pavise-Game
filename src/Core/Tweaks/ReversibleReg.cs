// @author bdth 2074055628@qq.com
// 文件用途 为注册表改动保存原值并提供可靠恢复

using System;
using Microsoft.Win32;

namespace PaviseApp
{
    internal sealed class ReversibleReg
    {
        public const string Absent = "__pavise_absent__";
        private const string LegacyAbsent = "none";

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
                    if (Settings.LoadStr(slot, "").Length == 0)
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
                        string snapshot;
                        if (cur == null) snapshot = Absent;
                        else if (kind == RegistryValueKind.Binary) snapshot = "b" + Convert.ToBase64String((byte[])cur);
                        else snapshot = "=" + cur;
                        Settings.SaveStr(slot, snapshot);
                        if (Settings.LoadStr(slot, "") != snapshot)
                        {
                            Logger.Log("无法持久化 " + valName + " 原值快照 已取消写入");
                            return false;
                        }
                    }
                    k.SetValue(valName, newVal, kind);
                    object actual = k.GetValue(valName);
                    if (actual == null) return false;
                    if (kind == RegistryValueKind.DWord)
                        return Convert.ToInt64(actual) == Convert.ToInt64(newVal);
                    if (kind == RegistryValueKind.Binary)
                        return BytesEqual((byte[])actual, (byte[])newVal);
                    return string.Equals(actual.ToString(), newVal == null ? "" : newVal.ToString(), StringComparison.Ordinal);
                }
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
            string s = Settings.LoadStr(slot, "");
            if (s.Length == 0) return true;

            bool absent = s == Absent || s == LegacyAbsent;
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
