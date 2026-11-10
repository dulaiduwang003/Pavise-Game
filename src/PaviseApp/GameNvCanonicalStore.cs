using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace PaviseApp
{
    internal sealed class GameNvCanonicalStore
    {
        internal const string Folder = "GameNvProfilesV2";

        internal readonly string Root;

        private readonly string user;

        private readonly string machine;

        private readonly bool nativeActor;

        private GameNvCanonicalStore(string data, string actor, string machineToken, bool native)
        {
            string getFullPathTrimmed = Path.GetFullPath(data).TrimEnd(Path.DirectorySeparatorChar);
            NoReparse.CheckPath(getFullPathTrimmed);
            if (getFullPathTrimmed.StartsWith("\\\\", StringComparison.Ordinal))
            {
                throw new InvalidDataException("Network-root NVIDIA ownership is unsupported");
            }
            Root = Path.Combine(getFullPathTrimmed, "GameNvProfilesV2");
            user = actor;
            machine = machineToken;
            nativeActor = native;
        }

        internal static GameNvCanonicalStore Open(string dataRoot)
        {
            string text;
            using (WindowsIdentity windowsIdentity = WindowsIdentity.GetCurrent())
            {
                if (windowsIdentity.User == null)
                {
                    throw new UnauthorizedAccessException();
                }
                text = windowsIdentity.User.Value;
            }
            string input;
            using (RegistryKey registryKey = Registry.LocalMachine.OpenSubKey("SOFTWARE\\Microsoft\\Cryptography"))
            {
                input = ((registryKey == null) ? null : (registryKey.GetValue("MachineGuid") as string));
            }
            Guid result;
            if (!Guid.TryParse(input, out result) || result == Guid.Empty)
            {
                throw new InvalidDataException("Native recovery machine identity unavailable");
            }
            return new GameNvCanonicalStore(dataRoot, text, GameNvText.Hash(Encoding.UTF8.GetBytes(result.ToString("N") + "\n" + text)), true);
        }

        private byte[] Header()
        {
            Dictionary<string, string> textsByName = new Dictionary<string, string>();
            textsByName.Add("schema", "game-nv-owner-2");
            textsByName.Add("user", user);
            textsByName.Add("machine", machine);
            textsByName.Add("root", Root.ToUpperInvariant());
            return GameNvText.Encode(textsByName);
        }

        internal void RequireNativeOwner()
        {
            if (!nativeActor)
            {
                throw new InvalidOperationException("Synthetic store is not native authority");
            }
            RequireOwner(false);
        }

        internal FileStream PinNativeOwner()
        {
            RequireNativeOwner();
            return new FileStream(Path.Combine(Root, "owner.tsv"), FileMode.Open, FileAccess.Read, FileShare.Read);
        }

        internal GameNvRecoveryIdentity ReadOriginal(string journal)
        {
            RequireOwner(false);
            if (!IsJournal(journal))
            {
                throw new InvalidDataException("Journal outside canonical NVIDIA root");
            }
            byte[] bytes = Read(Path.Combine(journal, "original.tsv"), 1048576);
            Dictionary<string, string> textsByName = GameNvText.Decode(bytes);
            if (textsByName.Count != 14 || textsByName["schema"] != "game-nv-original-1" || (textsByName["own"] != "0" && textsByName["own"] != "1"))
            {
                throw new InvalidDataException("Invalid NVIDIA original record");
            }
            GameNvRecoveryIdentity gameNvRecoveryIdentity = new GameNvRecoveryIdentity(textsByName["plan_text"], textsByName["plan"], textsByName["target"], textsByName["image"]).WithJournal(journal, GameNvText.Hash(bytes), textsByName["profile"], uint.Parse(textsByName["predefined"], CultureInfo.InvariantCulture), textsByName["scope"], textsByName["lease"], textsByName["own"] == "1");
            if (!Path.IsPathRooted(gameNvRecoveryIdentity.Executable) || Path.GetFullPath(gameNvRecoveryIdentity.Executable) != gameNvRecoveryIdentity.Executable)
            {
                throw new InvalidDataException("Original recovery target is not an exact absolute path");
            }
            int processedCount = 0;
            foreach (uint settingId in new uint[2] { 274197361u, 549528094u })
            {
                string settingIdText = settingId.ToString("X8");
                GameNvSetting.Parse(textsByName["original." + settingIdText]);
                string message = textsByName["target." + settingIdText];
                if (message == "1")
                {
                    processedCount++;
                }
                else if (message != "leave")
                {
                    throw new InvalidDataException("Unknown original NVIDIA target value");
                }
            }
            if (processedCount == 0)
            {
                throw new InvalidDataException("Original recovery has no issued key vector");
            }
            RequireReceipt(gameNvRecoveryIdentity);
            return gameNvRecoveryIdentity;
        }

        internal void RequireOwner(bool initialize)
        {
            NoReparse.CheckPath(Root);
            if (!DirectoryPresent(Root, File.GetAttributes))
            {
                if (!initialize)
                {
                    throw new GameNvPreparationPending(GameNvPending.Recovery);
                }
                if (nativeActor)
                {
                    SecurityIdentifier securityIdentifier = new SecurityIdentifier(user);
                    DirectorySecurity directorySecurity = new DirectorySecurity();
                    directorySecurity.SetOwner(securityIdentifier);
                    directorySecurity.SetAccessRuleProtection(true, false);
                    directorySecurity.AddAccessRule(new FileSystemAccessRule(securityIdentifier, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                    Directory.CreateDirectory(Root, directorySecurity);
                }
                else
                {
                    Directory.CreateDirectory(Root);
                }
            }
            if (nativeActor)
            {
                DirectorySecurity accessControl = Directory.GetAccessControl(Root);
                if (accessControl.GetOwner(typeof(SecurityIdentifier)).Value != user || !accessControl.AreAccessRulesProtected)
                {
                    throw new UnauthorizedAccessException("NVIDIA recovery directory ownership changed");
                }
                FileSystemAccessRule[] castItems = accessControl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
                if (castItems.Length == 0 || castItems.Any((FileSystemAccessRule x) => x.IdentityReference.Value != user || x.AccessControlType != 0 || (x.FileSystemRights & FileSystemRights.FullControl) != FileSystemRights.FullControl))
                {
                    throw new UnauthorizedAccessException("NVIDIA recovery directory has an external writer");
                }
            }
            string path = Path.Combine(Root, "owner.tsv");
            if (!File.Exists(path))
            {
                if (!initialize || Directory.GetFileSystemEntries(Root).Length != 0)
                {
                    throw new InvalidDataException("NVIDIA store without original owner proof");
                }
                GameNvText.Create(path, Header());
            }
            NoReparse.CheckPath(path);
            if (!Read(path, 4096).SequenceEqual(Header()))
            {
                throw new InvalidDataException("NVIDIA store belongs to another original owner/machine/root");
            }
        }

        private static byte[] Read(string path, int bound)
        {
            NoReparse.CheckPath(path);
            using (FileStream fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (fileStream.Length < 1 || fileStream.Length > bound)
                {
                    throw new InvalidDataException("NVIDIA ledger record exceeds bound");
                }
                byte[] rawBytes = new byte[(int)fileStream.Length];
                int bytesRead;
                for (int i = 0; i < rawBytes.Length; i += bytesRead)
                {
                    bytesRead = fileStream.Read(rawBytes, i, rawBytes.Length - i);
                    if (bytesRead <= 0)
                    {
                        throw new EndOfStreamException();
                    }
                }
                return rawBytes;
            }
        }

        private bool IsJournal(string journal)
        {
            string fullPath = Path.GetFullPath(journal);
            NoReparse.CheckPath(fullPath);
            Guid result;
            if (Path.GetDirectoryName(Path.GetDirectoryName(fullPath)) == Root && GameNvText.IsHash(Path.GetFileName(Path.GetDirectoryName(fullPath))) && Guid.TryParseExact(Path.GetFileName(fullPath), "N", out result))
            {
                return result != Guid.Empty;
            }
            return false;
        }

        internal void RequireReceipt(GameNvRecoveryIdentity original)
        {
            RequireOwner(false);
            if (original == null || !original.ResourceBound || !IsJournal(original.JournalDirectory) || Path.GetFileName(original.JournalDirectory) != original.LeaseToken)
            {
                throw new InvalidDataException("Receipt escaped canonical NVIDIA owner root");
            }
            byte[] bytes = Read(Path.Combine(original.JournalDirectory, "original.tsv"), 1048576);
            Dictionary<string, string> textsByName = GameNvText.Decode(bytes);
            if (GameNvText.Hash(bytes) != original.OriginalHash || textsByName.Count != 14 || textsByName["schema"] != "game-nv-original-1" || textsByName["lease"] != original.LeaseToken || textsByName["profile"] != original.ProfileName || textsByName["predefined"] != original.ProfilePredefined.ToString(CultureInfo.InvariantCulture) || textsByName["scope"] != original.ProfileScopeHash || textsByName["own"] != (original.OwnResources ? "1" : "0") || textsByName["target"] != original.Executable || textsByName["image"] != original.ImageHash || textsByName["plan"] != original.PlanHash)
            {
                throw new InvalidDataException("Recovery no longer owns its exact original resource");
            }
            new GameNvRecoveryIdentity(textsByName["plan_text"], original.PlanHash, original.Executable, original.ImageHash);
            string hashResult = GameNvText.Hash(Encoding.UTF8.GetBytes(original.ProfileName.ToUpperInvariant() + "\n" + original.ProfilePredefined.ToString(CultureInfo.InvariantCulture)));
            if (Path.GetFileName(Path.GetDirectoryName(original.JournalDirectory)) != hashResult)
            {
                throw new InvalidDataException("Canonical profile identity changed");
            }
        }

        internal string[] PendingJournals()
        {
            if (!DirectoryPresent(Root, File.GetAttributes))
            {
                return new string[0];
            }
            RequireOwner(false);
            foreach (string path in Directory.GetFiles(Root))
            {
                if (Path.GetFileName(path) != "owner.tsv" && Path.GetFileName(path) != "store.lock")
                {
                    throw new InvalidDataException("Unknown root-level NVIDIA recovery record");
                }
            }
            List<string> entryTexts = new List<string>();
            string[] directories = Directory.GetDirectories(Root);
            if (directories.Length > 256)
            {
                throw new InvalidDataException("NVIDIA history exceeds bound");
            }
            foreach (string path2 in directories)
            {
                NoReparse.CheckPath(path2);
                if (!GameNvText.IsHash(Path.GetFileName(path2)))
                {
                    throw new InvalidDataException("Unknown NVIDIA profile directory");
                }
                foreach (string path3 in Directory.GetFiles(path2))
                {
                    if (Path.GetFileName(path3) != "profile.lock")
                    {
                        throw new InvalidDataException("Misplaced NVIDIA profile recovery record");
                    }
                }
                string[] directories2 = Directory.GetDirectories(path2);
                if (directories2.Length > 256)
                {
                    throw new InvalidDataException("NVIDIA lease history exceeds bound");
                }
                foreach (string entryText in directories2)
                {
                    if (!IsJournal(entryText))
                    {
                        throw new InvalidDataException("Unknown NVIDIA lease directory");
                    }
                    if (!File.Exists(Path.Combine(entryText, "original.tsv")))
                    {
                        if (Directory.GetFileSystemEntries(entryText).Length != 0)
                        {
                            throw new InvalidDataException("Partial NVIDIA original record");
                        }
                    }
                    else if (!Terminal(entryText))
                    {
                        entryTexts.Add(entryText);
                    }
                }
            }
            return entryTexts.ToArray();
        }

        private bool Terminal(string journal)
        {
            string path = Path.Combine(journal, "terminal.tsv");
            if (!File.Exists(path))
            {
                return false;
            }
            ReadOriginal(journal);
            byte[] bytes = Read(Path.Combine(journal, "original.tsv"), 1048576);
            Dictionary<string, string> textsByName = GameNvText.Decode(bytes);
            string fileName = Path.GetFileName(journal);
            if (textsByName.Count != 14 || textsByName["schema"] != "game-nv-original-1" || textsByName["lease"] != fileName)
            {
                throw new InvalidDataException("Invalid terminal original");
            }
            Dictionary<string, string> currentTextsByName = new Dictionary<string, string>();
            currentTextsByName.Add("schema", "game-nv-phase-1");
            currentTextsByName.Add("lease", fileName);
            currentTextsByName.Add("original", GameNvText.Hash(bytes));
            currentTextsByName.Add("operation", "terminal");
            byte[] second = GameNvText.Encode(currentTextsByName);
            if (!Read(path, 4096).SequenceEqual(second))
            {
                throw new InvalidDataException("Terminal file is not original-bound proof");
            }
            HashSet<string> textsSet = new HashSet<string>(new string[10] { "original.tsv", "terminal.tsv", "issued-create.tsv", "issued-bind.tsv", "issued-unbind.tsv", "issued-delete.tsv", "issued-set-1057EB71.tsv", "issued-set-20C1221E.tsv", "issued-restore-1057EB71.tsv", "issued-restore-20C1221E.tsv" }, StringComparer.Ordinal);
            foreach (string path2 in Directory.GetFiles(journal))
            {
                string fileName2 = Path.GetFileName(path2);
                if (!textsSet.Contains(fileName2))
                {
                    throw new InvalidDataException("Unknown terminal phase retained for review");
                }
                if (fileName2 != "original.tsv" && fileName2 != "terminal.tsv")
                {
                    Dictionary<string, string> candidateTextsByName = new Dictionary<string, string>();
                    candidateTextsByName.Add("schema", "game-nv-phase-1");
                    candidateTextsByName.Add("lease", fileName);
                    candidateTextsByName.Add("original", GameNvText.Hash(bytes));
                    candidateTextsByName.Add("operation", Path.GetFileNameWithoutExtension(fileName2));
                    byte[] second2 = GameNvText.Encode(candidateTextsByName);
                    if (!Read(path2, 4096).SequenceEqual(second2))
                    {
                        throw new InvalidDataException("Terminal history contains an unbound issued phase");
                    }
                }
            }
            if (Directory.GetDirectories(journal).Length != 0)
            {
                throw new InvalidDataException("Unexpected nested terminal record");
            }
            return true;
        }

        internal void PruneTerminalHistory(int keep = 64)
        {
            if (!DirectoryPresent(Root, File.GetAttributes))
            {
                return;
            }
            RequireOwner(false);
            if (keep < 0 || keep > 128)
            {
                throw new ArgumentOutOfRangeException("keep");
            }
            using (new FileStream(Path.Combine(Root, "store.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                PendingJournals();
                string[] source = (from x in Directory.GetDirectories(Root).SelectMany((string x) => Directory.GetDirectories(x))
                    where File.Exists(Path.Combine(x, "original.tsv")) && Terminal(x)
                    orderby File.GetLastWriteTimeUtc(Path.Combine(x, "terminal.tsv")) descending
                    select x).ToArray();
                foreach (string skip in source.Skip(keep))
                {
                    if (!IsJournal(skip) || !Terminal(skip))
                    {
                        throw new InvalidDataException("Terminal scope changed before retention cleanup");
                    }
                    foreach (string path in Directory.GetFiles(skip))
                    {
                        NoReparse.CheckPath(path);
                        File.Delete(path);
                    }
                    Directory.Delete(skip, false);
                }
                foreach (string path2 in Directory.GetDirectories(Root))
                {
                    foreach (string entryText in Directory.GetDirectories(path2))
                    {
                        if (!IsJournal(entryText))
                        {
                            throw new InvalidDataException("Unknown empty history directory");
                        }
                        if (Directory.GetFileSystemEntries(entryText).Length == 0)
                        {
                            Directory.Delete(entryText, false);
                        }
                    }
                }
                foreach (string path3 in Directory.GetDirectories(Root))
                {
                    NoReparse.CheckPath(path3);
                    if (Directory.GetDirectories(path3).Length != 0)
                    {
                        continue;
                    }
                    string[] files2 = Directory.GetFiles(path3);
                    if (!files2.Any((string x) => Path.GetFileName(x) != "profile.lock"))
                    {
                        foreach (string path4 in files2)
                        {
                            NoReparse.CheckPath(path4);
                            File.Delete(path4);
                        }
                        Directory.Delete(path3, false);
                    }
                }
            }
        }

        private static bool DirectoryPresent(string path, Func<string, FileAttributes> attributes)
        {
            string getFullPathTrimmed = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
            bool result = false;
            if (getFullPathTrimmed.StartsWith("\\\\", StringComparison.Ordinal))
            {
                throw new IOException("Network recovery root is unsupported");
            }
            for (string iterationIndex = getFullPathTrimmed; !string.IsNullOrEmpty(iterationIndex); iterationIndex = Path.GetDirectoryName(iterationIndex))
            {
                FileAttributes fileAttributes;
                try
                {
                    fileAttributes = attributes(iterationIndex);
                }
                catch (FileNotFoundException)
                {
                    continue;
                }
                catch (DirectoryNotFoundException)
                {
                    continue;
                }
                if ((fileAttributes & FileAttributes.Directory) == 0 || (fileAttributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException("Recovery root or ancestor is redirected or not a directory");
                }
                if (iterationIndex == getFullPathTrimmed)
                {
                    result = true;
                }
            }
            return result;
        }

        internal static bool RootConfirmedAbsent(string dataRoot)
        {
            try
            {
                return !DirectoryPresent(Path.Combine(Path.GetFullPath(dataRoot), "GameNvProfilesV2"), File.GetAttributes);
            }
            catch
            {
                return false;
            }
        }

        internal static bool SafeToClear(string dataRoot)
        {
            try
            {
                string gameNvProfilesV2Path = Path.Combine(Path.GetFullPath(dataRoot).TrimEnd(Path.DirectorySeparatorChar), "GameNvProfilesV2");
                if (!DirectoryPresent(gameNvProfilesV2Path, File.GetAttributes))
                {
                    return true;
                }
                GameNvCanonicalStore gameNvCanonicalStore = Open(dataRoot);
                gameNvCanonicalStore.RequireOwner(false);
                using (new FileStream(Path.Combine(gameNvProfilesV2Path, "store.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                {
                    return gameNvCanonicalStore.PendingJournals().Length == 0;
                }
            }
            catch
            {
                return false;
            }
        }
    }
}
