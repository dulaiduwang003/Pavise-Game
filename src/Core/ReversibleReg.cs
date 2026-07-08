// @author bdth 2074055628@qq.com
// 文件用途 为注册表改动保存原值并提供可靠恢复

using System;
using Microsoft.Win32;

namespace AegisApp
{
    internal sealed class ReversibleReg
    {
        public const string Absent = "__aegis_absent__";
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
                                Logger.Log(valName + " 原值类型异常（" + curKind + "），跳过此项调整");
                                return false;
                            }
                        }
                        string snapshot = cur == null ? Absent : "=" + cur;
                        Settings.SaveStr(slot, snapshot);
                        if (Settings.LoadStr(slot, "") != snapshot)
                        {
                            Logger.Log("无法持久化 " + valName + " 原值快照，已取消写入");
                            return false;
                        }
                    }
                    k.SetValue(valName, newVal, kind);
                    object actual = k.GetValue(valName);
                    if (actual == null) return false;
                    if (kind == RegistryValueKind.DWord)
                        return Convert.ToInt64(actual) == Convert.ToInt64(newVal);
                    return string.Equals(actual.ToString(), newVal == null ? "" : newVal.ToString(), StringComparison.Ordinal);
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
                string v = s[0] == '=' ? s.Substring(1) : s;
                if (kind == RegistryValueKind.DWord)
                {
                    long n;
                    if (!long.TryParse(v, out n))
                    {
                        Settings.SaveStr(slot, "");
                        Logger.Log("注册表快照损坏，放弃还原 " + valName + "（记录值 \"" + v + "\"）");
                        return false;
                    }
                    val = unchecked((int)n);
                }
                else val = v;
            }

            try
            {
                bool restored = false;
                using (var k = hive.OpenSubKey(subKey, true))
                {
                    if (k == null) restored = absent;
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
                        else if (restored)
                            restored = string.Equals(actual.ToString(), val == null ? "" : val.ToString(), StringComparison.Ordinal);
                    }
                }
                if (!restored)
                {
                    Logger.Log("还原 " + valName + " 后回读不一致，快照保留待下次重试");
                    return false;
                }
                Settings.SaveStr(slot, "");
                return Settings.LoadStr(slot, "").Length == 0;
            }
            catch
            {
                Logger.Log("还原 " + valName + " 失败（多半是权限不足），快照保留待下次重试");
                return false;
            }
        }
    }
}
