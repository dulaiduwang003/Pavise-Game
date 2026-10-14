// 作者：bdth 2074055628@qq.com
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
        private const string JournalKey = "IrqPrio";
        private static readonly object lk = new object();

        public static bool HasResidue { get { return Settings.LoadStr(JournalKey, "").Length > 0; } }

        public static bool RestoreOnly(string deviceId)
        { return !string.IsNullOrEmpty(deviceId) && RestoreScope(deviceId); }

        public static bool RestoreAll() { return RestoreScope(null); }

        internal static bool TryTouchedDevices(out List<string> deviceIds)
        {
            lock (lk)
            {
                deviceIds = new List<string>();
                string raw;
                List<KeyValuePair<string,string>> entries;
                if (!Settings.TryLoadStr(JournalKey,out raw) || !TryJournal(raw,out entries)) return false;
                foreach (var entry in entries) deviceIds.Add(entry.Key);
                return true;
            }
        }

        private static bool RestoreScope(string deviceId)
        {
            lock (lk)
            {
                string raw, remaining;
                if (!Settings.TryLoadStr(JournalKey,out raw)) return false;
                bool ok = RestoreJournal(raw,deviceId,RestoreOne,out remaining);
                // 部分恢复失败时保留失败项及无关收据。
                return Settings.SaveStr(JournalKey,remaining) && Settings.LoadStr(JournalKey,"") == remaining && ok;
            }
        }

        private static bool TryJournal(string raw, out List<KeyValuePair<string,string>> entries)
        {
            entries = DecodeJournal(raw);
            if (!string.IsNullOrEmpty(raw) && entries.Count != raw.Split(';').Length) return false;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                int value;
                if (!seen.Add(entry.Key) || (entry.Value != "-" && !int.TryParse(entry.Value,out value))) return false;
            }
            return true;
        }

        internal static bool RestoreJournal(string raw, string selected,
            Func<string,string,bool> restore, out string remaining)
        {
            remaining = raw ?? "";
            List<KeyValuePair<string,string>> entries;
            if (!TryJournal(raw,out entries)) return false;
            bool ok = true;
            for (int i = entries.Count - 1; i >= 0; i--)
                if (selected == null || string.Equals(entries[i].Key,selected,StringComparison.OrdinalIgnoreCase))
                {
                    bool restored = false;
                    try { restored = restore(entries[i].Key,entries[i].Value); } catch { }
                    if (restored) entries.RemoveAt(i); else ok = false;
                }
            remaining = EncodeJournal(entries);
            return ok;
        }

        private static bool RestoreOne(string deviceId, string original)
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(
                    EnumRoot + deviceId + PolicySub, true))
                {
                    if (k == null) return false;
                    int back = 0;
                    if (original != "-" && !int.TryParse(original, out back)) return false;
                    if (original == "-")
                        k.DeleteValue(ValueName, false);
                    else
                        k.SetValue(ValueName, back, RegistryValueKind.DWord);
                    return original == "-" ? k.GetValue(ValueName) == null
                        : object.Equals(k.GetValue(ValueName), back);
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
    }
}
