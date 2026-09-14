// Game library and auto-ignore table update as a pair; once the receipt is on disk, an interrupted compensation can resume
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace PaviseApp
{
    internal sealed class LibraryIgnoreTransaction
    {
        internal const string FileName = "Pavise.library-ignore.txn";
        internal const string IgnoreFileName = "Pavise.autoignore.txt";
        private const string Header = "PAVISE_LIBRARY_IGNORE_TXN_V1";
        private const int MaxReceiptBytes = 8 * 1024 * 1024;
        private readonly string profilePath, ignorePath, receiptPath;
        private readonly GameProfileStore store;
        internal bool RecoveryPending { get; private set; }

#if PAVISE_SELFTEST
        internal Action<string> BeforeWriteForTest;
#endif

        internal LibraryIgnoreTransaction(string directory, GameProfileStore profileStore)
        {
            store = profileStore;
            profilePath = Path.Combine(directory, GameProfileStore.FileName);
            ignorePath = Path.Combine(directory, IgnoreFileName);
            receiptPath = Path.Combine(directory, FileName);
            RecoveryPending = File.Exists(receiptPath);
        }

        // Caller is responsible for serializing this with all game library changes; nothing in-use is published here
        internal bool Commit(IList<GameProfile> next, byte[] nextIgnore, Func<bool> canCommit)
        {
            if (nextIgnore == null) return false;
            if (!TryRecover()) return false;
            byte[] nextProfiles;
            try { nextProfiles = GameProfileStore.SnapshotBytes(next); }
            catch (Exception ex) { store.MarkSaveFailed(ex); return false; }
            try
            {
                byte[] previousProfiles = ReadOptional(profilePath), previousIgnore = ReadOptional(ignorePath);
                if (Same(previousIgnore, nextIgnore)) return store.Save(next, canCommit);
                string beforeHash = Hash(previousProfiles), afterHash = Hash(nextProfiles);
                // The paired operation needs an independent primary commit marker
                if (beforeHash == afterHash) return false;
                string body = string.Join("\n", new[] { Header, beforeHash, afterHash,
                    Encode(previousIgnore), Encode(nextIgnore) });
                byte[] receipt = Encoding.UTF8.GetBytes(body + "\n" + Hash(Encoding.UTF8.GetBytes(body)));
                if (receipt.Length > MaxReceiptBytes) throw new FormatException("Oversized library receipt");
                WriteAtomic(receiptPath, receipt, "prepare");
                RecoveryPending = true;
                WriteAtomic(ignorePath, nextIgnore, "apply-ignore");
                bool committed = store.Save(next, canCommit);
                // Before the primary commit, restore the old ignore table; after it, keep the new one
                // A cleanup failure leaves the receipt in place, blocking later writes
                // but a pair that already completed still counts as success, so the new in-memory state must be published
                TryRecover();
                return committed;
            }
            catch (Exception ex)
            {
                Logger.LogFailure("Library/auto-ignore transaction could not commit", ex);
                if (RecoveryPending) TryRecover();
                return false;
            }
        }

        internal bool TryRecover()
        {
            if (!RecoveryPending) return true;
            try
            {
                var info = new FileInfo(receiptPath);
                if (info.Length > MaxReceiptBytes) throw new FormatException("Oversized library receipt");
                string[] fields = new UTF8Encoding(false, true).GetString(File.ReadAllBytes(receiptPath)).Split('\n');
                if (fields.Length != 6 || fields[0] != Header
                    || Hash(Encoding.UTF8.GetBytes(string.Join("\n", fields, 0, 5))) != fields[5])
                    throw new FormatException("Invalid library receipt");
                byte[] before = Decode(fields[3]), after = Decode(fields[4]);
                if (after == null) throw new FormatException("Missing target ignore snapshot");
                string currentHash = Hash(ReadOptional(profilePath));
                byte[] desired;
                if (currentHash == fields[2]) desired = after;
                else if (currentHash == fields[1]) desired = before;
                else throw new IOException("Library changed outside the pending transaction; preserving receipt");
                byte[] current = ReadOptional(ignorePath);
                // Something modified by a third party: neither transaction snapshot may overwrite it
                if (!Same(current, before) && !Same(current, after))
                    throw new IOException("Auto-ignore changed outside the pending transaction; preserving receipt");
                if (!Same(current, desired))
                {
                    BeforeWrite("restore-ignore");
                    if (desired == null) File.Delete(ignorePath);
                    else WriteAtomic(ignorePath, desired, "restore-ignore-write");
                }
                BeforeWrite("clear-receipt");
                File.Delete(receiptPath);
                RecoveryPending = false;
                return true;
            }
            catch (Exception ex)
            {
                Logger.LogFailure("Library/auto-ignore recovery pending; further library writes paused", ex);
                return false;
            }
        }

        private void BeforeWrite(string phase)
        {
#if PAVISE_SELFTEST
            if (BeforeWriteForTest != null) BeforeWriteForTest(phase);
#endif
        }

        private void WriteAtomic(string path, byte[] bytes, string phase)
        {
            BeforeWrite(phase);
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                if (!File.Exists(path)) { File.Move(temporary, path); return; }
                for (int attempt = 0; ; attempt++)
                {
                    try { File.Replace(temporary, path, null); return; }
                    catch (IOException ex)
                    {
                        if (attempt >= 2 || !GameProfileStore.IsRetryableReplaceError(ex)
                            || !File.Exists(path) || !File.Exists(temporary)) throw;
                        Thread.Sleep(25 * (attempt + 1));
                    }
                }
            }
            finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
        }

        private static byte[] ReadOptional(string path)
        {
            try { return File.ReadAllBytes(path); }
            catch (FileNotFoundException) { return null; }
        }

        private static bool Same(byte[] a, byte[] b)
        {
            if (a == null || b == null) return a == b;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        private static string Encode(byte[] bytes) { return bytes == null ? "-" : Convert.ToBase64String(bytes); }
        private static byte[] Decode(string text) { return text == "-" ? null : Convert.FromBase64String(text); }
        private static string Hash(byte[] bytes)
        {
            if (bytes == null) return "-";
            using (SHA256 sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "");
        }
    }
}
