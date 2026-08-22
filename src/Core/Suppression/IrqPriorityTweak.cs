// @author bdth 2074055628@qq.com
// 文件用途 设备中断优先级 DevicePriority 提到 High 每设备快照 单独或整批还原
using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static class IrqPriorityTweak
    {
        private const string EnumRoot = @"SYSTEM\CurrentControlSet\Enum\";
        private const string PolicySub = @"\Device Parameters\Interrupt Management\Affinity Policy";
        private const string ValueName = "DevicePriority";
        private const int High = 3;
        private const string JournalKey = "IrqPrio";
        private static readonly object lk = new object();

        public static bool HasResidue { get { return Settings.LoadStr(JournalKey, "").Length > 0; } }

        public static bool AppliedTo(string deviceId)
        {
            if (string.IsNullOrEmpty(deviceId)) return false;
            foreach (KeyValuePair<string, string> kv in ReadJournal())
                if (string.Equals(kv.Key, deviceId, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public static bool Apply(string deviceId)
        {
            if (string.IsNullOrEmpty(deviceId) || !Native.IsElevated()) return false;
            lock (lk)
            {
                try
                {
                    using (RegistryKey k = Registry.LocalMachine.CreateSubKey(
                        EnumRoot + deviceId + PolicySub))
                    {
                        if (k == null) return false;
                        object cur = k.GetValue(ValueName);
                        int curVal = cur is int ? (int)cur : -1;
                        if (curVal == High) return true;
                        List<KeyValuePair<string, string>> journal = ReadJournal();
                        bool known = false;
                        foreach (KeyValuePair<string, string> kv in journal)
                            if (string.Equals(kv.Key, deviceId, StringComparison.OrdinalIgnoreCase))
                            { known = true; break; }
                        if (!known)
                        {
                            journal.Add(new KeyValuePair<string, string>(
                                deviceId, curVal < 0 ? "-" : curVal.ToString()));
                            string line = EncodeJournal(journal);
                            if (!Settings.SaveStr(JournalKey, line)
                                || Settings.LoadStr(JournalKey, "") != line) return false;
                        }
                        k.SetValue(ValueName, High, RegistryValueKind.DWord);
                        Logger.Log(Lang.T("log.irqprio.1") + deviceId + Lang.T("log.irqprio.2"));
                        return true;
                    }
                }
                catch { return false; }
            }
        }

        public static bool RestoreAll()
        {
            lock (lk)
            {
                List<KeyValuePair<string, string>> journal = ReadJournal();
                if (journal.Count == 0) { Settings.SaveStr(JournalKey, ""); return true; }
                var remain = new List<KeyValuePair<string, string>>();
                foreach (KeyValuePair<string, string> kv in journal)
                    if (!RestoreOne(kv.Key, kv.Value)) remain.Add(kv);
                Settings.SaveStr(JournalKey, EncodeJournal(remain));
                if (remain.Count == 0) { Logger.Log(Lang.T("log.irqprio.3")); return true; }
                Logger.Log(Lang.T("log.irqprio.4") + remain.Count);
                return false;
            }
        }

        private static bool RestoreOne(string deviceId, string original)
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(
                    EnumRoot + deviceId + PolicySub, true))
                {
                    if (k == null) return true;
                    int back;
                    if (original == "-" || !int.TryParse(original, out back))
                        k.DeleteValue(ValueName, false);
                    else
                        k.SetValue(ValueName, back, RegistryValueKind.DWord);
                    return true;
                }
            }
            catch { return false; }
        }

        internal static string EncodeJournal(List<KeyValuePair<string, string>> entries)
        {
            var parts = new List<string>();
            foreach (KeyValuePair<string, string> kv in entries)
                parts.Add(Convert.ToBase64String(
                    System.Text.Encoding.UTF8.GetBytes(kv.Key)) + "|" + kv.Value);
            return string.Join(";", parts.ToArray());
        }

        internal static List<KeyValuePair<string, string>> DecodeJournal(string raw)
        {
            var entries = new List<KeyValuePair<string, string>>();
            if (string.IsNullOrEmpty(raw)) return entries;
            foreach (string part in raw.Split(';'))
            {
                string[] halves = part.Split('|');
                if (halves.Length != 2) continue;
                try
                {
                    string id = System.Text.Encoding.UTF8.GetString(
                        Convert.FromBase64String(halves[0]));
                    if (id.Length > 0)
                        entries.Add(new KeyValuePair<string, string>(id, halves[1]));
                }
                catch { }
            }
            return entries;
        }

        private static List<KeyValuePair<string, string>> ReadJournal()
        {
            return DecodeJournal(Settings.LoadStr(JournalKey, ""));
        }
    }
}
