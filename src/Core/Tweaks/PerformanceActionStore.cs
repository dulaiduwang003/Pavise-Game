using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace PaviseApp
{
    // 修改前落盘原值，恢复时尊重外部后续更改。
    internal static class PerformanceActionStore
    {
        internal const string ListKey = "PerformanceActionReceiptsV1";
        private static readonly object gate = new object();
        [ThreadStatic] private static string lastChangeFailure;
        internal static string LastChangeFailure { get { return lastChangeFailure; } }
        private static bool ChangeFailed(string reason) { lastChangeFailure = reason; return false; }
        internal sealed class Receipt
        {
            internal string Id, Target, Original, Expected;
            internal bool Committed;
        }
        internal static string Slot(string id, string target)
        {
            using (var hash = SHA256.Create())
                return "PerformanceReceipt_" + BitConverter.ToString(hash.ComputeHash(
                    Encoding.UTF8.GetBytes(id + "\n" + target.ToUpperInvariant()))).Replace("-", "");
        }
        private static string Encode(string value) { return Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? "")); }
        private static string Decode(string value) { return Encoding.UTF8.GetString(Convert.FromBase64String(value)); }
        internal static string Pack(Receipt r)
        { return (r.Committed ? "1" : "0") + "|" + Encode(r.Id) + "|" + Encode(r.Target) + "|" + Encode(r.Original) + "|" + Encode(r.Expected); }
        internal static Receipt Unpack(string text)
        {
            string[] p = (text ?? "").Split('|');
            if (p.Length != 5 || (p[0] != "0" && p[0] != "1")) throw new FormatException("Invalid performance receipt");
            return new Receipt { Committed = p[0] == "1", Id = Decode(p[1]), Target = Decode(p[2]), Original = Decode(p[3]), Expected = Decode(p[4]) };
        }
        private static string Read(string key)
        {
            string text;
            if (!Settings.TryLoadStr(key, out text)) throw new InvalidOperationException("Unreadable performance receipt");
            return text ?? "";
        }
        private static bool Save(string key, string value)
        { return Settings.SaveStrDurable(key, value) && Read(key) == value; }
        internal static List<Receipt> All()
        {
            lock (gate)
            {
                var result = new List<Receipt>();
                var keys = new List<string>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string key in Read(ListKey).Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                    if (seen.Add(key)) keys.Add(key);
                string[] stored;
                if (!Settings.TryLoadStoredNames("PerformanceReceipt_", out stored))
                    throw new InvalidOperationException("Unreadable performance receipt names");
                // 索引只是加速查询，不能证明恢复收据不存在。
                foreach (string key in stored) if (seen.Add(key)) keys.Add(key);
                foreach (string key in keys)
                {
                    const string prefix = "PerformanceReceipt_";
                    bool ownedName = key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && key.Length == prefix.Length + 64;
                    for (int i = prefix.Length; ownedName && i < key.Length; i++)
                        ownedName = key[i] >= '0' && key[i] <= '9' || key[i] >= 'a' && key[i] <= 'f' || key[i] >= 'A' && key[i] <= 'F';
                    if (!ownedName) throw new FormatException("Invalid performance receipt key: " + key);
                    string text = Read(key);
                    if (text.Length == 0) continue;
                    Receipt receipt = Unpack(text);
                    if (string.IsNullOrEmpty(receipt.Id) || string.IsNullOrEmpty(receipt.Target)
                        || !string.Equals(key, Slot(receipt.Id, receipt.Target), StringComparison.OrdinalIgnoreCase))
                        throw new FormatException("Invalid performance receipt ownership: " + key);
                    result.Add(receipt);
                }
                return result;
            }
        }
        private static bool Track(string key)
        {
            var keys = new List<string>(Read(ListKey).Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries));
            if (!keys.Contains(key)) keys.Add(key);
            return Save(ListKey, string.Join(";", keys.ToArray()));
        }
        private static bool Clear(string key, string expected = null)
        {
            if (expected != null) { string latest = Read(key); if (latest.Length == 0) return true; if (latest != expected) return false; }
            // 先删收据再删索引，失败时仍可找到恢复记录。
            if (!Save(key, "")) return false;
            var keys = new List<string>(Read(ListKey).Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries));
            keys.Remove(key);
            return Save(ListKey, string.Join(";", keys.ToArray()));
        }
        internal static bool Change(string id, string target, string wanted, Func<string> read,
            Func<string, bool> write, Func<string, bool> satisfied)
        { return Change(id, target, wanted, read, write, satisfied, null); }
        internal static bool Change(string id, string target, string wanted, Func<string> read,
            Func<string, bool> write, Func<string, bool> satisfied, Func<Receipt, string, Receipt> reconcile)
        {
            lock (gate)
            {
                lastChangeFailure = null;
                string key = Slot(id, target), stored = Read(key), current = read();
                Receipt receipt = stored.Length == 0 ? null : Unpack(stored);
                if (receipt != null && (receipt.Id != id || !string.Equals(receipt.Target, target, StringComparison.OrdinalIgnoreCase)))
                    return ChangeFailed("Recovery record ownership does not match the target");
                if (satisfied(current))
                {
                    if (receipt == null || receipt.Committed) return true;
                    return Save(key, Pack(new Receipt { Id = id, Target = target, Original = receipt.Original,
                        Expected = current, Committed = true })) || ChangeFailed("Could not commit the recovery record");
                }
                bool prepared = false;
                if (receipt != null && reconcile != null)
                {
                    for (int attempt = 0; attempt < 3; attempt++)
                    {
                        Receipt next = reconcile(receipt, current);
                        if (next == null || next.Id != id || !string.Equals(next.Target, target, StringComparison.OrdinalIgnoreCase))
                            return ChangeFailed("Recovery snapshot cannot be reconciled");
                        string observed = read();
                        if (observed != current) { current = observed; continue; }
                        if (!Track(key) || !Save(key, Pack(next)))
                            return ChangeFailed("Could not save the reconciled recovery record");
                        receipt = next; prepared = true; break;
                    }
                    if (!prepared) return ChangeFailed("Configuration kept changing during reconciliation");
                }
                if (!prepared && receipt != null && id == PerformanceActionCatalog.Mitigations && wanted == "off32"
                    && receipt.Original.Split(';').Length == 5)
                    return ChangeFailed("Legacy recovery record must be reconciled before enabling SimExec");
                if (!prepared && receipt != null && receipt.Committed && current != receipt.Expected)
                {
                    if (id != PerformanceActionCatalog.Mitigations)
                        return ChangeFailed(Clear(key, stored) ? "Configuration changed externally; previous ownership released"
                            : "Configuration changed externally; recovery record could not be cleared");
                    return ChangeFailed("Configuration changed externally; recovery record retained");
                }
                if (receipt == null) receipt = new Receipt { Id = id, Target = target, Original = current, Expected = wanted };
                if (!prepared && (!Track(key) || !Save(key, Pack(receipt))))
                    return ChangeFailed("Could not save the recovery record");
                if (!satisfied(current))
                {
                    // Re-applying another desired value must not leave the old committed
                    // expectation authoritative if a write partially succeeds or we crash.
                    if (!prepared && (receipt.Committed || receipt.Expected != wanted))
                    {
                        receipt = new Receipt { Id = id, Target = target, Original = receipt.Original,
                            Expected = wanted, Committed = false };
                        if (!Save(key, Pack(receipt))) return ChangeFailed("Could not prepare the recovery record");
                    }
                    if (!write(wanted)) return ChangeFailed("Configuration write did not complete");
                }
                current = read();
                if (!satisfied(current)) return ChangeFailed("Configuration did not match the requested state after writing");
                receipt.Expected = current; receipt.Committed = true;
                return Save(key, Pack(receipt)) || ChangeFailed("Could not commit the recovery record");
            }
        }
        internal static bool Restore(Receipt r, Func<string> read, Func<string, bool> write, Func<string, bool> preparedMatch)
        {
            lock (gate)
            {
                string stored = Read(Slot(r.Id, r.Target));
                if (stored.Length == 0) return true;
                if (stored != Pack(r)) return false; // A stale caller cannot clear a newer recovery record.
                string current = read();
                bool ours = r.Committed ? current == r.Expected : preparedMatch(current);
                if (ours && current != r.Original)
                {
                    if (!write(r.Original) || read() != r.Original) return false;
                }
                return Clear(Slot(r.Id, r.Target), stored);
            }
        }
        internal static bool RestoreComponents(Receipt r, Func<string> read, Func<string, bool> write,
            Func<string, string> merge)
        {
            lock (gate)
            {
                string stored = Read(Slot(r.Id, r.Target));
                if (stored.Length == 0) return true;
                if (stored != Pack(r)) return false;
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    string current = read(), desired = merge(current);
                    if (desired == null) return false;
                    if (read() != current) continue;
                    if (desired != current)
                    {
                        if (!write(desired) || read() != desired) return false;
                    }
                    return Clear(Slot(r.Id, r.Target), stored);
                }
                return false;
            }
        }
    }
}
