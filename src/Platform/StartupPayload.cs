using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace PaviseApp
{
    internal static class StartupPayload
    {
        internal const string ExecutableName = "PaviseAuto.exe";
        private const string StagePrefix = ".PaviseAuto.";
        private const string StageSuffix = ".tmp";
#if PAVISE_SELFTEST
        internal static string ManagedRootForTest;
#endif

        internal static string ManagedRoot
        {
            get
            {
#if PAVISE_SELFTEST
                if (string.IsNullOrEmpty(ManagedRootForTest))
                    throw new InvalidOperationException("Startup payload root must be isolated in self-tests");
                return NormalizeRoot(ManagedRootForTest);
#else
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrEmpty(local)) throw new IOException("Local application data is unavailable");
                return Path.Combine(local, "PaviseAuto", "Autostart");
#endif
            }
        }

        internal static string Ensure(string sourceExecutable)
        { return Ensure(sourceExecutable, ManagedRoot); }

        internal static string Ensure(string sourceExecutable, string managedRoot)
        {
            string root = NormalizeRoot(managedRoot);
            string source = Path.GetFullPath(sourceExecutable);
            // 计算哈希与复制期间保持源文件稳定，不覆盖已发布副本。
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                string hash = Hash(input);
                string directory = Path.Combine(root, hash);
                string target = Path.Combine(directory, ExecutableName);
                RejectReparseChain(root); RejectReparse(directory); RejectReparse(target);
                if (File.Exists(target))
                {
                    Verify(target, hash);
                    return target;
                }
                Directory.CreateDirectory(directory);
                RejectReparseChain(root); RejectReparse(directory);
                string stage = Path.Combine(directory, StagePrefix + Guid.NewGuid().ToString("N") + StageSuffix);
                try
                {
                    input.Position = 0;
                    using (var output = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    { input.CopyTo(output); output.Flush(true); }
                    Verify(stage, hash);
                    try { File.Move(stage, target); }
                    catch (IOException)
                    {
                        if (!File.Exists(target)) throw;
                        Verify(target, hash);
                    }
                    Verify(target, hash);
                    return target;
                }
                finally
                {
                    try { if (File.Exists(stage)) File.Delete(stage); } catch { }
                }
            }
        }

        internal static string GetExpectedExecutable(string sourceExecutable)
        { return GetExpectedExecutable(sourceExecutable, ManagedRoot); }

        internal static string GetExpectedExecutable(string sourceExecutable, string managedRoot)
        {
            string root = NormalizeRoot(managedRoot);
            using (var input = new FileStream(Path.GetFullPath(sourceExecutable), FileMode.Open, FileAccess.Read, FileShare.Read))
                return Path.Combine(root, Hash(input), ExecutableName);
        }

        internal static bool IsManagedExecutable(string executable)
        { return IsManagedExecutable(executable, ManagedRoot); }

        internal static bool IsManagedExecutable(string executable, string managedRoot)
        {
            try
            {
                string root = NormalizeRoot(managedRoot), path = Path.GetFullPath(executable);
                string directory = Path.GetDirectoryName(path);
                return string.Equals(Path.GetFileName(path), ExecutableName, StringComparison.OrdinalIgnoreCase)
                    && IsHash(Path.GetFileName(directory))
                    && SamePath(Path.GetDirectoryName(directory), root);
            }
            catch { return false; }
        }

        internal static bool IsVerifiedExecutable(string executable, string managedRoot)
        {
            if (!IsManagedExecutable(executable, managedRoot)) return false;
            try
            {
                RejectReparseChain(NormalizeRoot(managedRoot));
                RejectReparse(Path.GetDirectoryName(executable)); RejectReparse(executable);
                Verify(executable, Path.GetFileName(Path.GetDirectoryName(executable)));
                return true;
            }
            catch { return false; }
        }

        internal static int Cleanup(string managedRoot, string currentExecutable)
        {
            string root;
            try { root = NormalizeRoot(managedRoot); RejectReparseChain(root); }
            catch { return 0; }
            if (!Directory.Exists(root)) return 0;
            int removed = 0;
            string[] directories;
            try { directories = Directory.GetDirectories(root, "*", SearchOption.TopDirectoryOnly); }
            catch { return 0; }
            foreach (string directory in directories)
            {
                try
                {
                    // 只清理已知哈希目录及文件，未知内容保留。
                    if (!IsHash(Path.GetFileName(directory)) || !SamePath(Path.GetDirectoryName(directory), root)) continue;
                    RejectReparse(directory);
                    foreach (string file in Directory.GetFiles(directory, "*", SearchOption.TopDirectoryOnly))
                    {
                        string name = Path.GetFileName(file);
                        if (!string.Equals(name, ExecutableName, StringComparison.OrdinalIgnoreCase) && !IsStageName(name)) continue;
                        if (SamePath(file, currentExecutable)) continue;
                        try { RejectReparse(file); File.Delete(file); removed++; } catch { }
                    }
                    TryRemoveEmptyDirectory(directory);
                }
                catch { }
            }
            TryRemoveEmptyDirectory(root);
            return removed;
        }

        private static void TryRemoveEmptyDirectory(string directory)
        {
            try
            {
                RejectReparse(directory);
                if (Directory.GetFileSystemEntries(directory).Length == 0) Directory.Delete(directory, false);
            }
            catch { }
        }

        private static bool IsStageName(string name)
        {
            if (name.Length != StagePrefix.Length + 32 + StageSuffix.Length
                || !name.StartsWith(StagePrefix, StringComparison.Ordinal)
                || !name.EndsWith(StageSuffix, StringComparison.Ordinal)) return false;
            string token = name.Substring(StagePrefix.Length, name.Length - StagePrefix.Length - StageSuffix.Length);
            if (token.Length != 32) return false;
            foreach (char c in token) if (!IsHex(c)) return false;
            return true;
        }

        private static string NormalizeRoot(string root)
        {
            if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("Startup payload root is required");
            string full = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(full, Path.GetPathRoot(full).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("A drive root cannot hold startup payloads");
            return full;
        }

        private static void RejectReparse(string path)
        {
            if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Startup payload path is redirected");
        }

        private static void RejectReparseChain(string path)
        {
            for (string current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
                RejectReparse(current);
        }

        private static bool SamePath(string first, string second)
        {
            if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second)) return false;
            try { return string.Equals(Path.GetFullPath(first).TrimEnd('\\', '/'), Path.GetFullPath(second).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        private static bool IsHash(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length != 64) return false;
            foreach (char c in name) if (!IsHex(c)) return false;
            return true;
        }

        private static bool IsHex(char c)
        { return c >= '0' && c <= '9' || c >= 'a' && c <= 'f' || c >= 'A' && c <= 'F'; }

        private static string Hash(Stream stream)
        {
            using (var sha = SHA256.Create())
            {
                byte[] bytes = sha.ComputeHash(stream);
                var text = new StringBuilder(bytes.Length * 2);
                foreach (byte value in bytes) text.Append(value.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
                return text.ToString();
            }
        }

        private static void Verify(string path, string expected)
        {
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                if (!string.Equals(Hash(input), expected, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Startup payload hash mismatch");
        }
    }
}
