// @author bdth 2074055628@qq.com
// File purpose Completes the trusted install scope of a single EXE from install records; does not pick a renderer, scan disks or guess game names
using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace PaviseApp
{
    internal sealed class GameInstallRecord
    {
        internal string InstallLocation;
        internal string InstallSource;
        internal string UninstallString;
    }

    internal static class GameInstallScope
    {
        private const int MaxRecords = 8192;
        private const long CacheTicks = 2 * TimeSpan.TicksPerMinute;
        private static readonly object sync = new object();
        private static Snapshot cached;
        private static long cachedAt;

        private sealed class Snapshot
        {
            internal readonly HashSet<string> Roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            internal readonly HashSet<string> Platforms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            internal readonly bool Complete;

            internal Snapshot(IList<GameInstallRecord> records, IList<string> platformRoots, bool complete = true)
            {
                int count = records == null ? 0 : records.Count;
                Complete = complete && count <= MaxRecords;
                // Bulk profile loading reuses this one normalized root set instead of re-reading records and system directories per profile
                for (int i = 0; i < Math.Min(count, MaxRecords); i++)
                {
                    string root = RootFromRecord(records[i]);
                    if (root != null && !UnsafeRoot(root)) Roots.Add(root);
                }
                if (platformRoots != null)
                    foreach (string raw in platformRoots)
                    {
                        string root = Normalize(raw);
                        if (root != null) Platforms.Add(root);
                    }
            }
        }

        // Only for add, edit and profile loading; the snapshot is reused briefly and never enters the game detection hot loop
        internal static string Resolve(string executablePath, string fallbackRoot)
        {
            string executable = Normalize(executablePath);
            if (executable == null || !executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return fallbackRoot;
            try
            {
                Snapshot snapshot = CurrentSnapshot();
                return SelectSnapshotRoot(executable, fallbackRoot, snapshot, Directory.Exists, IsSafeExistingPath);
            }
            catch { return fallbackRoot; }
        }

        // Only narrows the existing inferred scope; without new install evidence it never widens the directory, nor lets a platform or whole-library
        // root re-enter the profile through Resolve's original fallback contract; I/O failure does not guess a new scope
        internal static string RestrictFallback(string executablePath, string fallbackRoot)
        {
            string executable = Normalize(executablePath);
            string root = Normalize(fallbackRoot);
            if (executable == null || !executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                || root == null || !Under(executable, root)
                || UnsafeRoot(root)) return null;
            try
            {
                Snapshot snapshot = CurrentSnapshot();
                if (ContainsPlatform(root, snapshot.Platforms)
                    || ContainsOtherInstall(root, executable, snapshot.Roots)) return null;
            }
            catch { }
            return root;
        }

        // Pure record-selection entry; tests can inject the file system checks; never touches the registry or launches anything
        internal static string SelectRoot(string executablePath, string fallbackRoot,
            IList<GameInstallRecord> records, IList<string> platformRoots,
            Func<string, bool> directoryExists, Func<string, bool> safePath)
        {
            string executable = Normalize(executablePath);
            if (executable == null || !executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                || records == null || records.Count > MaxRecords
                || directoryExists == null || safePath == null) return fallbackRoot;
            try
            {
                return SelectSnapshotRoot(executable, fallbackRoot, new Snapshot(records, platformRoots), directoryExists, safePath);
            }
            catch { return fallbackRoot; }
        }

        private static string SelectSnapshotRoot(string executable, string fallbackRoot,
            Snapshot snapshot, Func<string, bool> directoryExists, Func<string, bool> safePath)
        {
            try
            {
                if (!snapshot.Complete || !safePath(executable)) return fallbackRoot;
                string best = null;
                foreach (string root in snapshot.Roots)
                {
                    if (!Under(executable, root) || (best != null && root.Length <= best.Length)) continue;
                    if (ContainsPlatform(root, snapshot.Platforms)
                        || ContainsOtherInstall(root, executable, snapshot.Roots)) continue;
                    if (!directoryExists(root) || !safePath(root)) continue;
                    best = root;
                }
                return best ?? fallbackRoot;
            }
            catch { return fallbackRoot; }
        }

        private static string RootFromRecord(GameInstallRecord record)
        {
            if (record == null) return null;
            // When an explicit install location exists but is invalid, do not fall back to another inconsistent field
            if (!string.IsNullOrWhiteSpace(record.InstallLocation))
                return Normalize(record.InstallLocation);
            string source = Normalize(record.InstallSource);
            if (source == null) return null;
            string uninstall = UninstallDirectory(record.UninstallString);
            // InstallSource is often just the installer package directory; it must be corroborated by an independent uninstall path
            // Only requires the install directory to still exist; after an upgrade the old uninstall EXE name may already be stale
            return string.Equals(source, uninstall, StringComparison.OrdinalIgnoreCase) ? source : null;
        }

        private static string UninstallDirectory(string command)
        {
            if (string.IsNullOrWhiteSpace(command) || command.Length > 32767) return null;
            string value = command.Trim();
            string executable = null;
            if (value[0] == '"')
            {
                int end = value.IndexOf('"', 1);
                if (end > 1) executable = value.Substring(1, end - 1);
            }
            else
            {
                int start = 0;
                while (start < value.Length)
                {
                    int end = value.IndexOf(".exe", start, StringComparison.OrdinalIgnoreCase);
                    if (end < 0) break;
                    end += 4;
                    if (end == value.Length || char.IsWhiteSpace(value[end]))
                    {
                        executable = value.Substring(0, end);
                        break;
                    }
                    start = end;
                }
            }
            executable = Normalize(executable);
            if (executable == null || !executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
            try { return Normalize(Path.GetDirectoryName(executable)); }
            catch { return null; }
        }

        private static string Normalize(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw) || raw.Length > 32767) return null;
            try
            {
                string value = Environment.ExpandEnvironmentVariables(raw.Trim().Trim('"')).Replace('/', '\\');
                // Relative paths, device paths and network shares are not interpreted as a local install scope
                if (value.Length < 3 || !char.IsLetter(value[0]) || value[1] != ':' || value[2] != '\\') return null;
                for (int i = 0; i < value.Length; i++)
                    if (char.IsControl(value[i]) || value[i] == '"' || value[i] == '*' || value[i] == '?'
                        || value[i] == '|' || value[i] == '<' || value[i] == '>'
                        || (value[i] == ':' && i != 1)) return null;
                return Path.GetFullPath(value).TrimEnd('\\');
            }
            catch { return null; }
        }

        private static bool Under(string path, string root)
        {
            return path != null && root != null
                && path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase);
        }

        private static readonly Environment.SpecialFolder[] BroadFolders =
        {
            Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
            Environment.SpecialFolder.CommonProgramFiles, Environment.SpecialFolder.CommonProgramFilesX86,
            Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolder.ApplicationData,
            Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.UserProfile,
            Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.CommonDesktopDirectory,
            Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.CommonDocuments,
            Environment.SpecialFolder.MyMusic, Environment.SpecialFolder.MyPictures, Environment.SpecialFolder.MyVideos,
            Environment.SpecialFolder.CommonMusic, Environment.SpecialFolder.CommonPictures, Environment.SpecialFolder.CommonVideos,
            Environment.SpecialFolder.System, Environment.SpecialFolder.SystemX86
        };

        private static readonly HashSet<string> KnownBroadRoots = BuildBroadRoots();
        private static readonly string KnownWindowsRoot = KnownFolder(Environment.SpecialFolder.Windows);

        private static string KnownFolder(Environment.SpecialFolder folder)
        {
            try { return Normalize(Environment.GetFolderPath(folder)); }
            catch { return null; }
        }

        private static HashSet<string> BuildBroadRoots()
        {
            var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Environment.SpecialFolder folder in BroadFolders)
            {
                string root = KnownFolder(folder);
                if (root != null) roots.Add(root);
            }
            try
            {
                string common = Normalize(Environment.GetEnvironmentVariable("PUBLIC"));
                if (common != null) roots.Add(common);
            }
            catch { }
            try
            {
                string temporary = Normalize(Path.GetTempPath());
                if (temporary != null) roots.Add(temporary);
            }
            catch { }
            return roots;
        }

        private static bool UnsafeRoot(string root)
        {
            if (root.Length <= 3) return true;
            if (string.Equals(root, KnownWindowsRoot, StringComparison.OrdinalIgnoreCase)
                || Under(root, KnownWindowsRoot) || KnownBroadRoots.Contains(root)) return true;

            string name = Path.GetFileName(root);
            // Only negatively excludes OS and platform common containers; never infers game identity from a directory name
            return string.Equals(name, "Users", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Games", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Game", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Downloads", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "SteamLibrary", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "steamapps", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "common", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "WeGameApps", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "XboxGames", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "WindowsApps", StringComparison.OrdinalIgnoreCase);
        }

        private static bool ContainsPlatform(string root, HashSet<string> platforms)
        {
            foreach (string platform in platforms)
                if (string.Equals(root, platform, StringComparison.OrdinalIgnoreCase) || Under(platform, root))
                    return true;
            return false;
        }

        private static bool ContainsOtherInstall(string root, string executable, HashSet<string> roots)
        {
            // When a scope also holds an independent install outside the selected EXE's branch, that shared scope is not treated as the family
            foreach (string other in roots)
                if (Under(other, root) && !Under(executable, other)) return true;
            return false;
        }

        private static bool IsSafeExistingPath(string path)
        {
            return IsSafePath(path, File.GetAttributes);
        }

        internal static bool IsSafePath(string path, Func<string, FileAttributes> attributes)
        {
            if (attributes == null) return false;
            try
            {
                string current = path;
                for (int i = 0; i < 128 && !string.IsNullOrEmpty(current); i++)
                {
                    if ((attributes(current) & FileAttributes.ReparsePoint) != 0) return false;
                    string parent = Path.GetDirectoryName(current);
                    if (string.IsNullOrEmpty(parent)) return true;
                    current = parent;
                }
            }
            catch { }
            return false;
        }

        private static Snapshot CurrentSnapshot()
        {
            lock (sync)
            {
#if PAVISE_SELFTEST
                if (testSnapshot != null) return testSnapshot;
#endif
                long now = DateTime.UtcNow.Ticks;
                if (cached != null && now >= cachedAt && now - cachedAt < CacheTicks) return cached;
                var records = new List<GameInstallRecord>();
                RegistryView[] views = Environment.Is64BitOperatingSystem
                    ? new[] { RegistryView.Registry64, RegistryView.Registry32 }
                    : new[] { RegistryView.Registry32 };
                foreach (RegistryView view in views)
                {
                    ReadUninstallHive(RegistryHive.LocalMachine, view, records);
                    ReadUninstallHive(RegistryHive.CurrentUser, view, records);
                    if (records.Count > MaxRecords) break;
                }
                var platforms = new List<string>();
                bool complete = records.Count <= MaxRecords;
                try
                {
                    foreach (string platform in GamePlatformCatalog.DetectedPlatforms())
                        platforms.AddRange(GamePlatformCatalog.ResolvedRoots(platform));
                }
                catch { complete = false; }
                cached = new Snapshot(records, platforms, complete);
                cachedAt = now;
                return cached;
            }
        }

        private static void ReadUninstallHive(RegistryHive hive, RegistryView view, List<GameInstallRecord> records)
        {
            if (records.Count > MaxRecords) return;
            try
            {
                using (RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view))
                using (RegistryKey key = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"))
                {
                    if (key == null) return;
                    foreach (string sub in key.GetSubKeyNames())
                    {
                        if (records.Count >= MaxRecords)
                        {
                            // An extra null sentinel marks the snapshot incomplete; SelectRoot then refuses any widening
                            // A truncated snapshot missing other products' records cannot serve as a reliable shared directory boundary
                            records.Add(null);
                            return;
                        }
                        try
                        {
                            using (RegistryKey entry = key.OpenSubKey(sub))
                            {
                                if (entry == null || entry.GetValue("ParentKeyName") != null) continue;
                                object component = entry.GetValue("SystemComponent");
                                if (component is int && (int)component != 0) continue;
                                records.Add(new GameInstallRecord
                                {
                                    InstallLocation = entry.GetValue("InstallLocation") as string,
                                    InstallSource = entry.GetValue("InstallSource") as string,
                                    UninstallString = entry.GetValue("UninstallString") as string
                                });
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

#if PAVISE_SELFTEST
        private static Snapshot testSnapshot;

        // Tests must supply the snapshot explicitly; an empty snapshot never falls back to the host registry or platform config
        internal static IDisposable UseSnapshotForTest(IList<GameInstallRecord> records, IList<string> platformRoots)
        {
            lock (sync)
            {
                var next = new Snapshot(records, platformRoots);
                var scope = new TestSnapshotScope(testSnapshot, next);
                testSnapshot = next;
                return scope;
            }
        }

        private sealed class TestSnapshotScope : IDisposable
        {
            private readonly Snapshot previous;
            private readonly Snapshot installed;
            private bool disposed;

            internal TestSnapshotScope(Snapshot previous, Snapshot installed)
            { this.previous = previous; this.installed = installed; }

            public void Dispose()
            {
                lock (sync)
                {
                    if (disposed) return;
                    if (!ReferenceEquals(testSnapshot, installed))
                        throw new InvalidOperationException("Install-scope test snapshots must be disposed in order.");
                    testSnapshot = previous;
                    disposed = true;
                }
            }
        }
#endif
    }
}
