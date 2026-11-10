using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace PaviseApp
{
    internal static class CacheWarmEngine
    {
        internal const int ChunkBytes = 1048576;

        private static readonly HashSet<string> AssetExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".pak", ".ucas", ".utoc", ".vpk", ".wad", ".wad.client", ".bundle", ".assets", ".resource", ".resS",
            ".ba2", ".bsa", ".rpf", ".archive"
        };

        internal static bool Asset(string path)
        {
            if (!AssetExtensions.Contains(Path.GetExtension(path)))
            {
                return path.EndsWith(".wad.client", StringComparison.OrdinalIgnoreCase);
            }
            return true;
        }

        private static bool SkipDirectory(string name)
        {
            string toLowerInvariantResult = name.ToLowerInvariant();
            switch (toLowerInvariantResult)
            {
            default:
                return toLowerInvariantResult == "tensafe";
            case "logs":
            case "crash":
            case "cache":
            case "shadercache":
            case "__installer":
            case "easyanticheat":
            case "battleye":
            case "anticheat":
            case "ace":
                return true;
            }
        }

        internal static List<string> Candidates(string root, Func<bool> allowed)
        {
            List<FileInfo> fileInfos = new List<FileInfo>();
            Queue<KeyValuePair<string, int>> queue = new Queue<KeyValuePair<string, int>>();
            queue.Enqueue(new KeyValuePair<string, int>(root, 0));
            Stopwatch stopwatch = Stopwatch.StartNew();
            int inspectedFiles = 0;
            while (queue.Count > 0 && inspectedFiles < 5000 && stopwatch.ElapsedMilliseconds < 10000 && allowed())
            {
                KeyValuePair<string, int> dequeue = queue.Dequeue();
                if (!CacheWarmPlatform.SafePath(dequeue.Key))
                {
                    continue;
                }
                try
                {
                    foreach (string enumerateFileSystemEntry in Directory.EnumerateFileSystemEntries(dequeue.Key))
                    {
                        if (++inspectedFiles > 5000 || stopwatch.ElapsedMilliseconds >= 10000 || !allowed())
                        {
                            break;
                        }
                        try
                        {
                            FileAttributes attributes = File.GetAttributes(enumerateFileSystemEntry);
                            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Offline)) != 0)
                            {
                                continue;
                            }
                            if ((attributes & FileAttributes.Directory) != 0)
                            {
                                if (dequeue.Value < 8 && queue.Count < 512 && !SkipDirectory(Path.GetFileName(enumerateFileSystemEntry)))
                                {
                                    queue.Enqueue(new KeyValuePair<string, int>(enumerateFileSystemEntry, dequeue.Value + 1));
                                }
                            }
                            else if (Asset(enumerateFileSystemEntry))
                            {
                                FileInfo fileInfo = new FileInfo(enumerateFileSystemEntry);
                                if (fileInfo.Length >= 1048576)
                                {
                                    fileInfos.Add(fileInfo);
                                }
                            }
                        }
                        catch (IOException)
                        {
                        }
                        catch (UnauthorizedAccessException)
                        {
                        }
                    }
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
            fileInfos.Sort(delegate(FileInfo a, FileInfo b)
            {
                int compareToResult = b.LastAccessTimeUtc.CompareTo(a.LastAccessTimeUtc);
                return (compareToResult == 0) ? StringComparer.OrdinalIgnoreCase.Compare(a.FullName, b.FullName) : compareToResult;
            });
            List<string> fullNames = new List<string>();
            foreach (FileInfo currentFileInfo in fileInfos)
            {
                fullNames.Add(currentFileInfo.FullName);
            }
            return fullNames;
        }

        internal static long Warm(string root, long budget, Func<bool> allowed, Func<int, bool> wait, Action<long> progress)
        {
            long warmResult = 0L;
            byte[] rawBytes = new byte[1048576];
            foreach (string candidate in Candidates(root, allowed))
            {
                if (warmResult >= budget || !allowed())
                {
                    break;
                }
                if (!CacheWarmPlatform.SafePath(candidate))
                {
                    continue;
                }
                try
                {
                    using (FileStream fileStream = new FileStream(candidate, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.None))
                    {
                        if (!CacheWarmPlatform.HandleUnder(fileStream, root))
                        {
                            continue;
                        }
                        long lengthLimit = Math.Min(fileStream.Length, 268435456L);
                        long fileBytesRead = 0L;
                        while (warmResult < budget && fileBytesRead < lengthLimit && allowed())
                        {
                            int readResult = fileStream.Read(rawBytes, 0, (int)Math.Min(rawBytes.Length, Math.Min(budget - warmResult, lengthLimit - fileBytesRead)));
                            if (readResult != 0)
                            {
                                warmResult += readResult;
                                fileBytesRead += readResult;
                                progress(warmResult);
                                if (wait(32))
                                {
                                    return warmResult;
                                }
                                continue;
                            }
                            break;
                        }
                    }
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
            return warmResult;
        }
    }
}
