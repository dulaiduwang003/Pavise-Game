using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace PaviseApp
{
    internal static class PerformanceActionStore
    {
        internal sealed class Receipt
        {
            internal string Id;

            internal string Target;

            internal string Original;

            internal string Expected;

            internal bool Committed;

            internal string VisualOwner;

            internal bool VisualRestored;
        }

        internal const string ListKey = "PerformanceActionReceiptsV1";

        private static readonly object gate = new object();

        [ThreadStatic]
        private static string lastChangeFailure;

        internal static string LastChangeFailure
        {
            get
            {
                return lastChangeFailure;
            }
        }

        private static bool ChangeFailed(string reason)
        {
            lastChangeFailure = reason;
            return false;
        }

        internal static string Slot(string id, string target)
        {
            using (SHA256 sHA = SHA256.Create())
            {
                return "PerformanceReceipt_" + BitConverter.ToString(sHA.ComputeHash(Encoding.UTF8.GetBytes(id + "\n" + target.ToUpperInvariant()))).Replace("-", "");
            }
        }

        private static string Encode(string value)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? ""));
        }

        private static string Decode(string value)
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(value));
        }

        internal static string Pack(Receipt r)
        {
            if (r.VisualRestored && string.IsNullOrEmpty(r.VisualOwner))
            {
                if (r.Id != "MatchVisualEffectsV1" || !r.Committed || r.Original != r.Expected || (r.Original != "0" && r.Original != "1"))
                {
                    throw new FormatException("Invalid legacy visual terminal receipt");
                }
                return "visual-legacy-terminal1|1|" + Encode(r.Id) + "|" + Encode(r.Target) + "|" + Encode(r.Original) + "|" + Encode(r.Expected);
            }
            if (!string.IsNullOrEmpty(r.VisualOwner) || r.VisualRestored)
            {
                ValidateVisualOwner(r);
                return "visual2|" + (r.Committed ? "1" : "0") + "|" + Encode(r.Id) + "|" + Encode(r.Target) + "|" + Encode(r.Original) + "|" + Encode(r.Expected) + "|" + Encode(r.VisualOwner) + "|" + (r.VisualRestored ? "1" : "0");
            }
            return (r.Committed ? "1" : "0") + "|" + Encode(r.Id) + "|" + Encode(r.Target) + "|" + Encode(r.Original) + "|" + Encode(r.Expected);
        }

        private static void ValidateVisualOwner(Receipt r)
        {
            if (r.Id != "MatchVisualEffectsV1" || (r.Original != "0" && r.Original != "1") || (r.Expected != "0" && r.Expected != "1") || (r.VisualRestored && (!r.Committed || r.Expected != r.Original)))
            {
                throw new FormatException("Invalid owned visual effect receipt");
            }
            string[] parts = (r.VisualOwner ?? "").Split(':');
            long result;
            Guid result2;
            Guid result3;
            if (parts.Length != 4 || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out result) || result <= 0 || result.ToString(CultureInfo.InvariantCulture) != parts[1] || !Guid.TryParseExact(parts[2], "N", out result2) || result2 == Guid.Empty || result2.ToString("N") != parts[2] || !Guid.TryParseExact(parts[3], "N", out result3) || result3 == Guid.Empty || result3.ToString("N") != parts[3] || new SecurityIdentifier(parts[0]).Value != parts[0])
            {
                throw new FormatException("Invalid owned visual effect receipt epoch");
            }
        }

        internal static Receipt Unpack(string text)
        {
            string[] parts = (text ?? "").Split('|');
            if (parts.Length == 6 && parts[0] == "visual-legacy-terminal1" && parts[1] == "1")
            {
                Receipt currentReceipt = new Receipt();
                currentReceipt.Id = Decode(parts[2]);
                currentReceipt.Target = Decode(parts[3]);
                currentReceipt.Original = Decode(parts[4]);
                currentReceipt.Expected = Decode(parts[5]);
                currentReceipt.Committed = true;
                currentReceipt.VisualRestored = true;
                if (Pack(currentReceipt) != text)
                {
                    throw new FormatException("Noncanonical legacy visual terminal receipt");
                }
                return currentReceipt;
            }
            if (parts.Length == 8 && parts[0] == "visual2")
            {
                if ((parts[1] != "0" && parts[1] != "1") || (parts[7] != "0" && parts[7] != "1"))
                {
                    throw new FormatException("Invalid owned visual effect receipt state");
                }
                Receipt resolvedReceipt = new Receipt();
                resolvedReceipt.Committed = parts[1] == "1";
                resolvedReceipt.Id = Decode(parts[2]);
                resolvedReceipt.Target = Decode(parts[3]);
                resolvedReceipt.Original = Decode(parts[4]);
                resolvedReceipt.Expected = Decode(parts[5]);
                resolvedReceipt.VisualOwner = Decode(parts[6]);
                resolvedReceipt.VisualRestored = parts[7] == "1";
                ValidateVisualOwner(resolvedReceipt);
                if (Pack(resolvedReceipt) != text)
                {
                    throw new FormatException("Noncanonical owned visual effect receipt");
                }
                return resolvedReceipt;
            }
            if (parts.Length != 5 || (parts[0] != "0" && parts[0] != "1"))
            {
                throw new FormatException("Invalid performance receipt");
            }
            Receipt selectedReceipt = new Receipt();
            selectedReceipt.Committed = parts[0] == "1";
            selectedReceipt.Id = Decode(parts[1]);
            selectedReceipt.Target = Decode(parts[2]);
            selectedReceipt.Original = Decode(parts[3]);
            selectedReceipt.Expected = Decode(parts[4]);
            return selectedReceipt;
        }

        private static string Read(string key)
        {
            string value;
            if (!Settings.TryLoadStr(key, out value))
            {
                throw new InvalidOperationException("Unreadable performance receipt");
            }
            return value ?? "";
        }

        private static bool Save(string key, string value)
        {
            if (Settings.SaveStrDurable(key, value))
            {
                return Read(key) == value;
            }
            return false;
        }

        internal static List<Receipt> All()
        {
            lock (gate)
            {
                List<Receipt> receipts = new List<Receipt>();
                List<string> texts = new List<string>();
                HashSet<string> textsSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string readPart in Read("PerformanceActionReceiptsV1").Split(new char[1] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (textsSet.Add(readPart))
                    {
                        texts.Add(readPart);
                    }
                }
                string[] names;
                if (!Settings.TryLoadStoredNames("PerformanceReceipt_", out names))
                {
                    throw new InvalidOperationException("Unreadable performance receipt names");
                }
                foreach (string currentNamesSnapshot in names)
                {
                    if (textsSet.Add(currentNamesSnapshot))
                    {
                        texts.Add(currentNamesSnapshot);
                    }
                }
                foreach (string text in texts)
                {
                    bool receiptKey = text.StartsWith("PerformanceReceipt_", StringComparison.OrdinalIgnoreCase) && text.Length == "PerformanceReceipt_".Length + 64;
                    int processedCount = "PerformanceReceipt_".Length;
                    while (receiptKey && processedCount < text.Length)
                    {
                        receiptKey = (text[processedCount] >= '0' && text[processedCount] <= '9') || (text[processedCount] >= 'a' && text[processedCount] <= 'f') || (text[processedCount] >= 'A' && text[processedCount] <= 'F');
                        processedCount++;
                    }
                    if (!receiptKey)
                    {
                        throw new FormatException("Invalid performance receipt key: " + text);
                    }
                    string readResult = Read(text);
                    if (readResult.Length != 0)
                    {
                        Receipt receipt = Unpack(readResult);
                        if (string.IsNullOrEmpty(receipt.Id) || string.IsNullOrEmpty(receipt.Target) || !string.Equals(text, Slot(receipt.Id, receipt.Target), StringComparison.OrdinalIgnoreCase))
                        {
                            throw new FormatException("Invalid performance receipt ownership: " + text);
                        }
                        receipts.Add(receipt);
                    }
                }
                return receipts;
            }
        }

        internal static bool TryAll(out List<Receipt> receipts)
        {
            receipts = null;
            if (!Monitor.TryEnter(gate))
            {
                return false;
            }
            try
            {
                receipts = All();
                return true;
            }
            finally
            {
                Monitor.Exit(gate);
            }
        }

        private static bool Track(string key)
        {
            List<string> splitSnapshot = new List<string>(Read("PerformanceActionReceiptsV1").Split(new char[1] { ';' }, StringSplitOptions.RemoveEmptyEntries));
            if (!splitSnapshot.Contains(key))
            {
                splitSnapshot.Add(key);
            }
            return Save("PerformanceActionReceiptsV1", string.Join(";", splitSnapshot.ToArray()));
        }

        private static bool Clear(string key, string expected = null)
        {
            if (expected != null)
            {
                string readResult = Read(key);
                if (readResult.Length == 0)
                {
                    return true;
                }
                if (readResult != expected)
                {
                    return false;
                }
            }
            if (!Save(key, ""))
            {
                return false;
            }
            List<string> splitSnapshot = new List<string>(Read("PerformanceActionReceiptsV1").Split(new char[1] { ';' }, StringSplitOptions.RemoveEmptyEntries));
            splitSnapshot.Remove(key);
            return Save("PerformanceActionReceiptsV1", string.Join(";", splitSnapshot.ToArray()));
        }

        internal static bool Change(string id, string target, string wanted, Func<string> read, Func<string, bool> write, Func<string, bool> satisfied)
        {
            return Change(id, target, wanted, read, write, satisfied, null);
        }

        internal static bool Change(string id, string target, string wanted, Func<string> read, Func<string, bool> write, Func<string, bool> satisfied, Func<Receipt, string, Receipt> reconcile)
        {
            lock (gate)
            {
                lastChangeFailure = null;
                string key = Slot(id, target);
                string readResult = Read(key);
                Receipt receipt = ((readResult.Length == 0) ? null : Unpack(readResult));
                if (receipt != null && (!string.IsNullOrEmpty(receipt.VisualOwner) || receipt.VisualRestored))
                {
                    return ChangeFailed("Owned visual receipts require the shared SPI capability path");
                }
                string originalValue = read();
                if (receipt != null && (receipt.Id != id || !string.Equals(receipt.Target, target, StringComparison.OrdinalIgnoreCase)))
                {
                    return ChangeFailed("Recovery record ownership does not match the target");
                }
                if (satisfied(originalValue))
                {
                    if (receipt == null || receipt.Committed)
                    {
                        return true;
                    }
                    return Save(key, Pack(new Receipt
                    {
                        Id = id,
                        Target = target,
                        Original = receipt.Original,
                        Expected = originalValue,
                        Committed = true
                    })) || ChangeFailed("Could not commit the recovery record");
                }
                bool reconciled = false;
                if (receipt != null && reconcile != null)
                {
                    for (int i = 0; i < 3; i++)
                    {
                        Receipt currentReconcile = reconcile(receipt, originalValue);
                        if (currentReconcile == null || currentReconcile.Id != id || !string.Equals(currentReconcile.Target, target, StringComparison.OrdinalIgnoreCase))
                        {
                            return ChangeFailed("Recovery snapshot cannot be reconciled");
                        }
                        string readResult2 = read();
                        if (readResult2 != originalValue)
                        {
                            originalValue = readResult2;
                            continue;
                        }
                        if (!Track(key) || !Save(key, Pack(currentReconcile)))
                        {
                            return ChangeFailed("Could not save the reconciled recovery record");
                        }
                        receipt = currentReconcile;
                        reconciled = true;
                        break;
                    }
                    if (!reconciled)
                    {
                        return ChangeFailed("Configuration kept changing during reconciliation");
                    }
                }
                if (!reconciled && receipt != null && id == "AutoGameMitigationsV1" && wanted == "off32" && receipt.Original.Split(';').Length == 5)
                {
                    return ChangeFailed("Legacy recovery record must be reconciled before enabling SimExec");
                }
                if (!reconciled && receipt != null && receipt.Committed && originalValue != receipt.Expected)
                {
                    if (id != "AutoGameMitigationsV1")
                    {
                        return ChangeFailed(Clear(key, readResult) ? "Configuration changed externally; previous ownership released" : "Configuration changed externally; recovery record could not be cleared");
                    }
                    return ChangeFailed("Configuration changed externally; recovery record retained");
                }
                if (receipt == null)
                {
                    Receipt currentReceipt = new Receipt();
                    currentReceipt.Id = id;
                    currentReceipt.Target = target;
                    currentReceipt.Original = originalValue;
                    currentReceipt.Expected = wanted;
                    receipt = currentReceipt;
                }
                if (!reconciled && (!Track(key) || !Save(key, Pack(receipt))))
                {
                    return ChangeFailed("Could not save the recovery record");
                }
                if (!satisfied(originalValue))
                {
                    if (!reconciled && (receipt.Committed || receipt.Expected != wanted))
                    {
                        Receipt candidateReceipt = new Receipt();
                        candidateReceipt.Id = id;
                        candidateReceipt.Target = target;
                        candidateReceipt.Original = receipt.Original;
                        candidateReceipt.Expected = wanted;
                        candidateReceipt.Committed = false;
                        receipt = candidateReceipt;
                        if (!Save(key, Pack(receipt)))
                        {
                            return ChangeFailed("Could not prepare the recovery record");
                        }
                    }
                    if (!write(wanted))
                    {
                        return ChangeFailed("Configuration write did not complete");
                    }
                }
                originalValue = read();
                if (!satisfied(originalValue))
                {
                    return ChangeFailed("Configuration did not match the requested state after writing");
                }
                receipt.Expected = originalValue;
                receipt.Committed = true;
                return Save(key, Pack(receipt)) || ChangeFailed("Could not commit the recovery record");
            }
        }

        internal static bool Restore(Receipt r, Func<string> read, Func<string, bool> write, Func<string, bool> preparedMatch)
        {
            if (!string.IsNullOrEmpty(r.VisualOwner) || r.VisualRestored)
            {
                return false;
            }
            lock (gate)
            {
                string readResult = Read(Slot(r.Id, r.Target));
                if (readResult.Length == 0)
                {
                    return true;
                }
                if (readResult != Pack(r))
                {
                    return false;
                }
                string readResult2 = read();
                if ((r.Committed ? (readResult2 == r.Expected) : preparedMatch(readResult2)) && readResult2 != r.Original && (!write(r.Original) || read() != r.Original))
                {
                    return false;
                }
                return Clear(Slot(r.Id, r.Target), readResult);
            }
        }

        internal static bool RestoreComponents(Receipt r, Func<string> read, Func<string, bool> write, Func<string, string> merge)
        {
            if (!string.IsNullOrEmpty(r.VisualOwner) || r.VisualRestored)
            {
                return false;
            }
            lock (gate)
            {
                string readResult = Read(Slot(r.Id, r.Target));
                if (readResult.Length == 0)
                {
                    return true;
                }
                if (readResult != Pack(r))
                {
                    return false;
                }
                for (int i = 0; i < 3; i++)
                {
                    string readResult2 = read();
                    string mergeResult = merge(readResult2);
                    if (mergeResult == null)
                    {
                        return false;
                    }
                    if (read() == readResult2)
                    {
                        if (mergeResult != readResult2 && (!write(mergeResult) || read() != mergeResult))
                        {
                            return false;
                        }
                        return Clear(Slot(r.Id, r.Target), readResult);
                    }
                }
                return false;
            }
        }

        internal static bool ChangeVisualOwned(SpiOwnership.SpiOwnerAccess access, string target, string wanted, Func<string> read, Func<string, bool> write)
        {
            if (access == null || access.Operation != 0)
            {
                throw new InvalidOperationException("Visual apply needs its exact SPI owner capability");
            }
            access.Assert(access.Stamp, false);
            string manualReceiptOwner = access.Stamp.ManualReceiptOwner;
            if (wanted == "0" || wanted == "1")
            {
                lock (gate)
                {
                    lastChangeFailure = null;
                    string key = Slot("MatchVisualEffectsV1", target);
                    string readResult = Read(key);
                    Receipt receipt = ((readResult.Length == 0) ? null : Unpack(readResult));
                    if (receipt != null && (receipt.Id != "MatchVisualEffectsV1" || receipt.Target != target || receipt.VisualOwner != manualReceiptOwner || receipt.VisualRestored || !receipt.Committed))
                    {
                        return ChangeFailed("Visual recovery owner or pending state differs");
                    }
                    string originalValue = read();
                    if (originalValue != "0" && originalValue != "1")
                    {
                        return ChangeFailed("Visual original readback unavailable");
                    }
                    if (receipt != null && originalValue != receipt.Expected)
                    {
                        return ChangeFailed("Visual value changed; retain exact recovery owner");
                    }
                    if (receipt == null)
                    {
                        Receipt currentReceipt = new Receipt();
                        currentReceipt.Id = "MatchVisualEffectsV1";
                        currentReceipt.Target = target;
                        currentReceipt.Original = originalValue;
                        currentReceipt.Expected = wanted;
                        currentReceipt.VisualOwner = manualReceiptOwner;
                        receipt = currentReceipt;
                    }
                    else
                    {
                        if (originalValue == wanted)
                        {
                            return true;
                        }
                        receipt.Expected = wanted;
                        receipt.Committed = false;
                    }
                    if (!Track(key) || !Save(key, Pack(receipt)))
                    {
                        return ChangeFailed("Could not save owned visual write intent");
                    }
                    access.Assert(access.Stamp, true);
                    if (originalValue != wanted && !write(wanted))
                    {
                        return ChangeFailed("Owned visual write was not confirmed");
                    }
                    if (read() != wanted)
                    {
                        return ChangeFailed("Owned visual readback differs");
                    }
                    receipt.Committed = true;
                    return Save(key, Pack(receipt)) || ChangeFailed("Could not commit owned visual recovery record");
                }
            }
            throw new FormatException("Invalid visual effect value");
        }

        internal static bool RestoreVisualOwned(SpiOwnership.SpiOwnerAccess access, Receipt receipt, Func<string> read, Func<string, bool> write)
        {
            if (access == null || access.Operation != SpiOwnerOperation.Restore || receipt == null)
            {
                throw new InvalidOperationException("Visual restoration needs its original SPI owner capability");
            }
            access.Assert(access.Stamp, false);
            if (receipt.VisualOwner != access.Stamp.ManualReceiptOwner)
            {
                return false;
            }
            lock (gate)
            {
                string key = Slot(receipt.Id, receipt.Target);
                string readResult = Read(key);
                if (readResult.Length == 0 || readResult != Pack(receipt) || receipt.Id != "MatchVisualEffectsV1")
                {
                    return false;
                }
                if (receipt.VisualRestored)
                {
                    return true;
                }
                string readResult2 = read();
                if (readResult2 != receipt.Original)
                {
                    if (readResult2 != receipt.Expected)
                    {
                        return false;
                    }
                    access.Assert(access.Stamp, true);
                    if (!write(receipt.Original) || read() != receipt.Original)
                    {
                        return false;
                    }
                }
                Receipt r = new Receipt();
                r.Id = receipt.Id;
                r.Target = receipt.Target;
                r.Original = receipt.Original;
                r.Expected = receipt.Original;
                r.VisualOwner = receipt.VisualOwner;
                r.Committed = true;
                r.VisualRestored = true;
                return Save(key, Pack(r));
            }
        }

        internal static bool ClearVisualTerminalAfterClose(SpiOwnership.SpiOwnerAccess closedAccess)
        {
            if (closedAccess == null)
            {
                throw new ArgumentNullException("closedAccess");
            }
            closedAccess.AssertMetadata(closedAccess.Stamp, true);
            string manualReceiptOwner = closedAccess.Stamp.ManualReceiptOwner;
            IDictionary<string, string> iDictionary = SpiManualClosureManifest.Read(closedAccess);
            lock (gate)
            {
                List<Receipt> receipts = All();
                foreach (Receipt receipt in receipts)
                {
                    string currentIDictionary;
                    if (receipt.Id == "MatchVisualEffectsV1" && (receipt.VisualOwner != manualReceiptOwner || !receipt.VisualRestored || !iDictionary.TryGetValue(Slot(receipt.Id, receipt.Target), out currentIDictionary) || SpiOwnerRecord.Digest(Encoding.UTF8.GetBytes(Pack(receipt))) != currentIDictionary))
                    {
                        return false;
                    }
                }
                foreach (Receipt currentReceipt in receipts)
                {
                    if (currentReceipt.Id == "MatchVisualEffectsV1" && !Clear(Slot(currentReceipt.Id, currentReceipt.Target), Pack(currentReceipt)))
                    {
                        return false;
                    }
                }
                return true;
            }
        }

        internal static string ExactVisualBytes(Receipt row)
        {
            lock (gate)
            {
                string readResult = Read(Slot(row.Id, row.Target));
                if (row.Id != "MatchVisualEffectsV1" || readResult != Pack(row))
                {
                    throw new InvalidDataException("Visual receipt is not its exact canonical stored bytes");
                }
                return readResult;
            }
        }

        internal static bool RestoreLegacyVisualBeforeInitialization(Receipt row)
        {
            SharedSpiLegacyMigration.RequireReceipt(row);
            lock (gate)
            {
                if (!string.IsNullOrEmpty(row.VisualOwner) || Read(Slot(row.Id, row.Target)) != Pack(row))
                {
                    return false;
                }
                string readResult = PerformanceActionBackend.Read(row.Id, row.Target);
                if (row.VisualRestored)
                {
                    return readResult == row.Original;
                }
                if (readResult != row.Original && (readResult != row.Expected || !PerformanceActionBackend.Write(row.Id, row.Target, row.Original) || PerformanceActionBackend.Read(row.Id, row.Target) != row.Original))
                {
                    return false;
                }
                SharedSpiLegacyMigration.RequireReceipt(row);
                return Save(Slot(row.Id, row.Target), Pack(new Receipt
                {
                    Id = row.Id,
                    Target = row.Target,
                    Original = row.Original,
                    Expected = row.Original,
                    Committed = true,
                    VisualRestored = true
                }));
            }
        }

        internal static bool ClearLegacyVisualTerminal(Receipt row)
        {
            lock (gate)
            {
                if (row == null || row.Id != "MatchVisualEffectsV1" || !string.IsNullOrEmpty(row.VisualOwner) || !row.VisualRestored)
                {
                    return false;
                }
                return Clear(Slot(row.Id, row.Target), Pack(row));
            }
        }
    }
}
