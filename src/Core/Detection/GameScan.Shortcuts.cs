using System;
using System.Collections.Generic;
using System.IO;

namespace PaviseApp
{
    internal static partial class GameScan
    {
        private const int ShortcutDirectoryLimit = 128;
        private const int ShortcutFileLimit = 1024;
        private const int ShortcutRootLimit = 128;
        private const int ShortcutDepthLimit = 4;

        private sealed class ShortcutDirectory
        {
            internal string Path;
            internal int Depth;
        }

        private static void FromShortcuts(string root, List<ScanHit> hits,
            HashSet<string> roots, Func<bool> canceled)
        {
            var locations = new List<string>();
            foreach (Environment.SpecialFolder folder in new[]
            {
                Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.CommonDesktopDirectory,
                Environment.SpecialFolder.StartMenu, Environment.SpecialFolder.CommonStartMenu
            })
            {
                try
                {
                    string location = Environment.GetFolderPath(folder);
                    if (!string.IsNullOrEmpty(location)) locations.Add(location);
                }
                catch { }
            }
            ScanShortcutDirectories(locations, root, hits, roots, canceled);
        }

        internal static void ScanShortcutDirectories(IEnumerable<string> locations, string root,
            List<ScanHit> hits, HashSet<string> roots, Func<bool> canceled)
        {
            if (locations == null || Stop(canceled)) return;
            var queue = new Queue<ShortcutDirectory>();
            var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var consideredRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string location in locations)
            {
                if (Stop(canceled) || directories.Count >= ShortcutDirectoryLimit) break;
                string dir = CleanDir(location);
                if (dir != null && directories.Add(dir))
                    queue.Enqueue(new ShortcutDirectory { Path = dir });
            }
            int files = 0;
            while (queue.Count > 0 && !Stop(canceled))
            {
                ShortcutDirectory current = queue.Dequeue();
                try
                {
                    if ((File.GetAttributes(current.Path) & FileAttributes.ReparsePoint) != 0) continue;
                }
                catch { continue; }
                try
                {
                    foreach (string shortcut in Directory.EnumerateFiles(current.Path, "*.lnk"))
                    {
                        if (Stop(canceled) || files++ >= ShortcutFileLimit) return;
                        try
                        {
                            if ((File.GetAttributes(shortcut) & FileAttributes.ReparsePoint) != 0) continue;
                            string target = GameExecutableResolver.ResolveShortcut(shortcut);
                            if (string.IsNullOrWhiteSpace(target)) continue;
                            target = Environment.ExpandEnvironmentVariables(target.Trim().Trim('"')).Replace('/', '\\');
                            // 自动扫描只读本地可执行文件。
                            if (!Path.IsPathRooted(target) || target.StartsWith("\\\\", StringComparison.Ordinal)
                                || !string.Equals(Path.GetExtension(target), ".exe", StringComparison.OrdinalIgnoreCase)) continue;
                            target = Path.GetFullPath(target);
                            if (!IsLocalShortcutTarget(target)) continue;
                            string executable, error;
                            if (!GameExecutableResolver.TryResolve(target, out executable, out error)) continue;
                            string dir = CleanDir(InferGameRoot(executable));
                            if (dir == null || !UnderRoot(dir, root) || IsSystemOrTooBroad(dir)
                                || IsSystemWideDirName(Path.GetFileName(dir))) continue;
                            bool known = false;
                            foreach (string existing in roots)
                                if (UnderRoot(dir, existing)) { known = true; break; }
                            if (known || consideredRoots.Contains(dir)) continue;
                            if (consideredRoots.Count >= ShortcutRootLimit) return;
                            consideredRoots.Add(dir);
                            // 快捷方式本身不构成游戏证据。
                            if (!LooksLikeGameDir(dir, 3) || Stop(canceled)) continue;
                            int first = hits.Count;
                            AddManifestHit(root, hits, roots, Path.GetFileName(dir), dir, null);
                            if (hits.Count == first + 1 && !hits[first].NeedsChoice
                                && string.Equals(hits[first].Exe, executable, StringComparison.OrdinalIgnoreCase))
                                hits[first].Name = Path.GetFileNameWithoutExtension(shortcut);
                        }
                        catch { }
                    }
                }
                catch { }
                if (current.Depth >= ShortcutDepthLimit) continue;
                try
                {
                    foreach (string child in Directory.EnumerateDirectories(current.Path))
                    {
                        if (Stop(canceled) || directories.Count >= ShortcutDirectoryLimit) break;
                        string name = Path.GetFileName(child);
                        if (name.Length == 0 || name[0] == '.' || SkipDirs.Contains(name)) continue;
                        if (directories.Add(child))
                            queue.Enqueue(new ShortcutDirectory { Path = child, Depth = current.Depth + 1 });
                    }
                }
                catch { }
            }
        }

        private static bool IsLocalShortcutTarget(string target)
        {
            try
            {
                string drive = Path.GetPathRoot(target);
                if (new DriveInfo(drive).DriveType == DriveType.Network) return false;
                string relative = Path.GetDirectoryName(target).Substring(drive.Length);
                string current = drive;
                // 逐级排除目录链接，避免先读取远程目标。
                foreach (string part in relative.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    current = Path.Combine(current, part);
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
                }
                return (File.GetAttributes(target) & FileAttributes.ReparsePoint) == 0;
            }
            catch { return false; }
        }
    }
}
