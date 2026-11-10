using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static class GameScan
    {
        private sealed class ShortcutDirectory
        {
            internal string Path;

            internal int Depth;
        }

        private static readonly HashSet<string> SkipDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "windows", "programdata", "$recycle.bin", "system volume information", "recovery", "perflogs", "onedrivetemp", "node_modules", ".git", "temp",
            "tmp", "cache", "__pycache__", "easyanticheat", "easyanticheat_eos", "battleye", "tenprotect"
        };

        private static readonly HashSet<string> GenericDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "bin", "bin64", "binaries", "win64", "win32", "x64", "x86", "game", "games", "retail",
            "shipping", "engine", "content", "data", "app"
        };

        private static readonly HashSet<string> GenericDirTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "bin", "bin64", "binaries", "win64", "win32", "x64", "x86", "game", "games", "retail",
            "shipping", "engine", "content", "data", "app", "client"
        };

        private static bool IsSystemWideDirName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.EndsWith(":", StringComparison.Ordinal))
            {
                return true;
            }
            if (!name.Equals("program files", StringComparison.OrdinalIgnoreCase) && !name.Equals("program files (x86)", StringComparison.OrdinalIgnoreCase) && !name.Equals("common files", StringComparison.OrdinalIgnoreCase) && !name.Equals("users", StringComparison.OrdinalIgnoreCase) && !name.Equals("desktop", StringComparison.OrdinalIgnoreCase) && !name.Equals("downloads", StringComparison.OrdinalIgnoreCase))
            {
                return name.Equals("documents", StringComparison.OrdinalIgnoreCase);
            }
            return true;
        }

        internal static bool IsGenericDirName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }
            if (GenericDirs.Contains(name))
            {
                return true;
            }
            string[] nameParts = name.Split('_', '-', ' ', '.');
            if (nameParts.Length < 2)
            {
                return false;
            }
            int processedCount = 0;
            foreach (string entryText in nameParts)
            {
                if (entryText.Length != 0)
                {
                    if (!GenericDirTokens.Contains(entryText))
                    {
                        return false;
                    }
                    processedCount++;
                }
            }
            return processedCount >= 2;
        }

        public static List<ScanHit> RunManifests(Func<bool> canceled)
        {
            List<ScanHit> scanHits = new List<ScanHit>();
            HashSet<string> roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CollectManifests(null, scanHits, roots, canceled);
            return scanHits;
        }

        private static void CollectManifests(string root, List<ScanHit> hits, HashSet<string> roots, Func<bool> canceled)
        {
            try
            {
                FromSteam(root, hits, roots);
            }
            catch
            {
            }
            if (Stop(canceled))
            {
                return;
            }
            try
            {
                FromEpic(root, hits, roots);
            }
            catch
            {
            }
            if (Stop(canceled))
            {
                return;
            }
            try
            {
                FromGog(root, hits, roots);
            }
            catch
            {
            }
            if (Stop(canceled))
            {
                return;
            }
            try
            {
                FromUbisoft(root, hits, roots);
            }
            catch
            {
            }
            if (Stop(canceled))
            {
                return;
            }
            try
            {
                FromRiot(root, hits, roots);
            }
            catch
            {
            }
            if (Stop(canceled))
            {
                return;
            }
            try
            {
                FromWeGameApps(root, hits, roots);
            }
            catch
            {
            }
            if (Stop(canceled))
            {
                return;
            }
            try
            {
                FromBattleNet(root, hits, roots);
            }
            catch
            {
            }
            if (Stop(canceled))
            {
                return;
            }
            try
            {
                FromXbox(root, hits, roots);
            }
            catch
            {
            }
            if (Stop(canceled))
            {
                return;
            }
            try
            {
                FromMicrosoftStore(root, hits, roots);
            }
            catch
            {
            }
            if (Stop(canceled))
            {
                return;
            }
            try
            {
                FromInstalled(root, hits, roots, canceled);
            }
            catch
            {
            }
            if (Stop(canceled))
            {
                return;
            }
            try
            {
                FromShortcuts(root, hits, roots, canceled);
            }
            catch
            {
            }
        }

        private static bool Stop(Func<bool> canceled)
        {
            if (canceled != null)
            {
                return canceled();
            }
            return false;
        }

        private static bool UnderRoot(string dir, string root)
        {
            if (string.IsNullOrEmpty(root))
            {
                return dir != null;
            }
            string value = root.TrimEnd('\\') + "\\";
            if (dir != null)
            {
                return (dir.TrimEnd('\\') + "\\").StartsWith(value, StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }

        internal static void AddManifestHit(string root, List<ScanHit> hits, HashSet<string> roots, string name, string dir, string exePath)
        {
            dir = CleanDir(dir);
            if (dir == null || !UnderRoot(dir, root) || !Directory.Exists(dir) || roots.Contains(dir))
            {
                return;
            }
            if (string.IsNullOrEmpty(name))
            {
                name = Path.GetFileName(dir);
            }
            string executablePath = null;
            if (!string.IsNullOrWhiteSpace(exePath))
            {
                try
                {
                    string replaceResult = Environment.ExpandEnvironmentVariables(exePath.Trim().Trim('"')).Replace('/', '\\');
                    if (!Path.IsPathRooted(replaceResult))
                    {
                        replaceResult = Path.Combine(dir, replaceResult);
                    }
                    replaceResult = Path.GetFullPath(replaceResult);
                    if (UnderRoot(replaceResult, dir) && string.Equals(Path.GetExtension(replaceResult), ".exe", StringComparison.OrdinalIgnoreCase) && !IsJunkName(replaceResult))
                    {
                        string error;
                        GameExecutableResolver.TryResolve(replaceResult, out executablePath, out error);
                    }
                }
                catch
                {
                }
            }
            List<ExecutableCandidateFacts> executableCandidateFacts = null;
            if (executablePath == null)
            {
                executableCandidateFacts = ExecutableCandidateProbe.ListCandidates(dir, 24, out executablePath);
            }
            bool hasExecutable = executablePath != null;
            if (executablePath != null)
            {
                AddUniqueManifestEntry(hits, name, dir, executablePath, false);
            }
            else
            {
                foreach (ExecutableCandidateFacts executableCandidateFact in executableCandidateFacts)
                {
                    if (ExecutableCandidateProbe.Rank(executableCandidateFact) > 0)
                    {
                        hasExecutable = true;
                        AddUniqueManifestEntry(hits, name, dir, executableCandidateFact.Path, true);
                    }
                }
            }
            if (hasExecutable)
            {
                roots.Add(dir);
            }
        }

        private static void AddUniqueManifestEntry(List<ScanHit> hits, string name, string dir, string exe, bool needsChoice)
        {
            foreach (ScanHit hit in hits)
            {
                if (string.Equals(hit.Exe, exe, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
            hits.Add(new ScanHit
            {
                Name = name,
                Proc = Path.GetFileNameWithoutExtension(exe),
                Root = dir,
                Exe = exe,
                NeedsChoice = needsChoice
            });
        }

        private static void FromInstalled(string root, List<ScanHit> hits, HashSet<string> roots, Func<bool> canceled)
        {
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            RegistryKey[] registryKeyItems = new RegistryKey[3]
            {
                Registry.LocalMachine,
                Registry.LocalMachine,
                Registry.CurrentUser
            };
            string[] uninstallRegistryPaths = new string[3] { "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall", "SOFTWARE\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Uninstall", "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall" };
            for (int i = 0; i < registryKeyItems.Length; i++)
            {
                if (Stop(canceled))
                {
                    break;
                }
                try
                {
                    ScanUninstallHive(registryKeyItems[i], uninstallRegistryPaths[i], root, hits, roots, seen, canceled);
                }
                catch
                {
                }
            }
        }

        internal static void ScanUninstallHive(RegistryKey hive, string path, string root, List<ScanHit> hits, HashSet<string> roots, HashSet<string> seen, Func<bool> canceled)
        {
            using (RegistryKey registryKey = hive.OpenSubKey(path))
            {
                if (registryKey == null)
                {
                    return;
                }
                foreach (string name in registryKey.GetSubKeyNames())
                {
                    if (Stop(canceled))
                    {
                        break;
                    }
                    try
                    {
                        using (RegistryKey currentRegistryKey = registryKey.OpenSubKey(name))
                        {
                            if (currentRegistryKey != null && (!(currentRegistryKey.GetValue("SystemComponent") is int) || (int)currentRegistryKey.GetValue("SystemComponent") == 0) && currentRegistryKey.GetValue("ParentKeyName") == null)
                            {
                                string[] candidates = InstalledDirectoryCandidates(currentRegistryKey.GetValue("InstallLocation") as string, currentRegistryKey.GetValue("DisplayIcon") as string, currentRegistryKey.GetValue("UninstallString") as string, currentRegistryKey.GetValue("InstallSource") as string);
                                ScanInstalledRecord(currentRegistryKey.GetValue("DisplayName") as string, candidates, root, hits, roots, seen, canceled);
                            }
                        }
                    }
                    catch
                    {
                    }
                }
            }
        }

        internal static string[] InstalledDirectoryCandidates(string installLocation, string displayIcon, string uninstallString, string installSource)
        {
            List<string> texts = new List<string>();
            HashSet<string> unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddInstalledDirectoryCandidate(installLocation, texts, unique);
            AddInstalledCommandDirectories(displayIcon, texts, unique);
            AddInstalledCommandDirectories(uninstallString, texts, unique);
            AddInstalledDirectoryCandidate(installSource, texts, unique);
            return texts.ToArray();
        }

        private static void AddInstalledDirectoryCandidate(string value, List<string> candidates, HashSet<string> unique)
        {
            string cleanDirResult = CleanDir(value);
            if (cleanDirResult != null && unique.Add(cleanDirResult))
            {
                candidates.Add(cleanDirResult);
            }
        }

        private static void AddInstalledCommandDirectories(string command, List<string> candidates, HashSet<string> unique)
        {
            string installedCommandTargetResult = InstalledCommandTarget(command);
            if (installedCommandTargetResult != null)
            {
                AddInstalledDirectoryCandidate(InferGameRoot(installedCommandTargetResult), candidates, unique);
                AddInstalledDirectoryCandidate(Path.GetDirectoryName(installedCommandTargetResult), candidates, unique);
            }
        }

        internal static void ScanInstalledRecord(string name, string[] candidates, string root, List<ScanHit> hits, HashSet<string> roots, HashSet<string> seen, Func<bool> canceled)
        {
            if (candidates == null)
            {
                return;
            }
            foreach (string candidate in candidates)
            {
                if (Stop(canceled))
                {
                    break;
                }
                try
                {
                    string cleanDirResult = CleanDir(candidate);
                    if (cleanDirResult == null || !UnderRoot(cleanDirResult, root) || !Directory.Exists(cleanDirResult) || IsSystemOrTooBroad(cleanDirResult) || IsSystemWideDirName(Path.GetFileName(cleanDirResult)))
                    {
                        continue;
                    }
                    foreach (ScanHit hit in hits)
                    {
                        if (!string.IsNullOrEmpty(hit.Root) && UnderRoot(cleanDirResult, hit.Root))
                        {
                            return;
                        }
                    }
                    if (roots.Contains(cleanDirResult))
                    {
                        foreach (ScanHit hit2 in hits)
                        {
                            if (!string.IsNullOrEmpty(hit2.Exe) && UnderRoot(hit2.Exe, cleanDirResult))
                            {
                                return;
                            }
                        }
                    }
                    else if (seen.Add(cleanDirResult) && LooksLikeGameDir(cleanDirResult, 3))
                    {
                        AddManifestHit(root, hits, roots, name, cleanDirResult, null);
                        if (roots.Contains(cleanDirResult))
                        {
                            break;
                        }
                    }
                }
                catch
                {
                }
            }
        }

        private static string CleanDir(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return null;
            }
            try
            {
                string path = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"')).Replace('/', '\\');
                if (!Path.IsPathRooted(path))
                {
                    return null;
                }
                path = Path.GetFullPath(path).TrimEnd('\\');
                return (path.Length >= 4) ? path : null;
            }
            catch
            {
                return null;
            }
        }

        private static string InstalledCommandTarget(string command)
        {
            if (string.IsNullOrEmpty(command))
            {
                return null;
            }
            try
            {
                string expandEnvironmentVariablesResult = Environment.ExpandEnvironmentVariables(command.Trim());
                if (expandEnvironmentVariablesResult.StartsWith("\""))
                {
                    int matchIndex = expandEnvironmentVariablesResult.IndexOf('"', 1);
                    if (matchIndex <= 1)
                    {
                        return null;
                    }
                    expandEnvironmentVariablesResult = expandEnvironmentVariablesResult.Substring(1, matchIndex - 1);
                }
                else
                {
                    int commandOffset = 0;
                    while (commandOffset < expandEnvironmentVariablesResult.Length)
                    {
                        int matchIndex2 = expandEnvironmentVariablesResult.IndexOf(".exe", commandOffset, StringComparison.OrdinalIgnoreCase);
                        if (matchIndex2 < 0)
                        {
                            break;
                        }
                        int matchIndex2Offset = matchIndex2 + 4;
                        if (matchIndex2Offset == expandEnvironmentVariablesResult.Length || char.IsWhiteSpace(expandEnvironmentVariablesResult[matchIndex2Offset]) || expandEnvironmentVariablesResult[matchIndex2Offset] == ',')
                        {
                            expandEnvironmentVariablesResult = expandEnvironmentVariablesResult.Substring(0, matchIndex2Offset);
                            break;
                        }
                        commandOffset = matchIndex2Offset;
                    }
                    int matchIndex3 = expandEnvironmentVariablesResult.LastIndexOf(',');
                    int result;
                    if (matchIndex3 > 0 && int.TryParse(expandEnvironmentVariablesResult.Substring(matchIndex3 + 1).Trim(), out result))
                    {
                        expandEnvironmentVariablesResult = expandEnvironmentVariablesResult.Substring(0, matchIndex3);
                    }
                }
                return CleanDir(expandEnvironmentVariablesResult);
            }
            catch
            {
                return null;
            }
        }

        private static bool IsSystemOrTooBroad(string dir)
        {
            string folderPath = null;
            string b = null;
            string b2 = null;
            string b3 = null;
            string b4 = null;
            try
            {
                folderPath = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                b = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                b2 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
                b3 = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
                b4 = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }
            catch
            {
            }
            if (!string.IsNullOrEmpty(folderPath) && (Same(dir, folderPath) || UnderRoot(dir, folderPath)))
            {
                return true;
            }
            if (Same(dir, b) || Same(dir, b2) || Same(dir, b3) || Same(dir, b4))
            {
                return true;
            }
            try
            {
                string directoryName = Path.GetDirectoryName(dir.TrimEnd('\\'));
                if (string.IsNullOrEmpty(directoryName))
                {
                    return true;
                }
            }
            catch
            {
                return true;
            }
            return false;
        }

        private static bool Same(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
            {
                return false;
            }
            return string.Equals(a.TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }

        internal static bool LooksLikeGameDir(string dir, int depth)
        {
            string[] files;
            string[] directories;
            try
            {
                if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0)
                {
                    return false;
                }
                files = Directory.GetFiles(dir);
                directories = Directory.GetDirectories(dir);
            }
            catch
            {
                return false;
            }
            if (HasGameSignals(files, directories))
            {
                return true;
            }
            if (depth <= 1)
            {
                return false;
            }
            int inspectedDirectories = 0;
            foreach (string entryText in directories)
            {
                if (++inspectedDirectories > 24)
                {
                    break;
                }
                string fileName = Path.GetFileName(entryText);
                if (fileName.Length != 0 && fileName[0] != '.' && !SkipDirs.Contains(fileName) && LooksLikeGameDir(entryText, depth - 1))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool HasGameSignals(string[] files, string[] subs)
        {
            bool hasChromiumFiles = false;
            bool hasNwRuntime = false;
            bool hasWebContent = false;
            foreach (string path in files)
            {
                string toLowerInvariantResult = Path.GetFileName(path).ToLowerInvariant();
                switch (toLowerInvariantResult)
                {
                case "unityplayer.dll":
                case "gameassembly.dll":
                    return true;
                case "steam_api.dll":
                case "steam_api64.dll":
                case "steam_appid.txt":
                    return true;
                case "eossdk-win64-shipping.dll":
                    return true;
                case "data.win":
                    return true;
                case "data.pck":
                    return true;
                }
                if (toLowerInvariantResult.EndsWith(".rpa"))
                {
                    return true;
                }
                if (toLowerInvariantResult.EndsWith(".vpk"))
                {
                    return true;
                }
                if (toLowerInvariantResult.StartsWith("pakchunk") && toLowerInvariantResult.EndsWith(".pak"))
                {
                    return true;
                }
                if (toLowerInvariantResult == "fna.dll" || toLowerInvariantResult == "monogame.framework.dll")
                {
                    return true;
                }
                if (toLowerInvariantResult.EndsWith("-win64-shipping.exe") || toLowerInvariantResult.EndsWith("-win32-shipping.exe"))
                {
                    return true;
                }
                if (toLowerInvariantResult.EndsWith(".dll") && (toLowerInvariantResult.StartsWith("bink") || toLowerInvariantResult.StartsWith("fmod") || toLowerInvariantResult.StartsWith("crysystem")))
                {
                    return true;
                }
                if (toLowerInvariantResult == "mss32.dll" || toLowerInvariantResult == "mss64.dll")
                {
                    return true;
                }
                if (toLowerInvariantResult.StartsWith("goggame-"))
                {
                    return true;
                }
                switch (toLowerInvariantResult)
                {
                case "steam_emu.ini":
                case "onlinefix.ini":
                case "cream_api.ini":
                    return true;
                }
                if (toLowerInvariantResult.StartsWith("tersafe"))
                {
                    return true;
                }
                if (toLowerInvariantResult == ".build.info")
                {
                    return true;
                }
                if (toLowerInvariantResult == "nw.dll")
                {
                    hasNwRuntime = true;
                }
                switch (toLowerInvariantResult)
                {
                case "icudtl.dat":
                case "chrome_100_percent.pak":
                case "v8_context_snapshot.bin":
                case "app.asar":
                    hasChromiumFiles = true;
                    break;
                }
            }
            foreach (string path2 in subs)
            {
                switch (Path.GetFileName(path2).ToLowerInvariant())
                {
                case "easyanticheat":
                case "easyanticheat_eos":
                case "battleye":
                case "tenprotect":
                    return true;
                case "renpy":
                    return true;
                case "www":
                    hasWebContent = true;
                    break;
                }
            }
            if (hasNwRuntime && hasWebContent)
            {
                return true;
            }
            if (hasChromiumFiles)
            {
                return false;
            }
            foreach (string filePath in files)
            {
                string toLowerInvariantResult2 = Path.GetFileName(filePath).ToLowerInvariant();
                if (!toLowerInvariantResult2.EndsWith(".exe") || IsJunkName(toLowerInvariantResult2))
                {
                    continue;
                }
                try
                {
                    if (new FileInfo(filePath).Length >= 209715200)
                    {
                        return true;
                    }
                }
                catch
                {
                }
            }
            return false;
        }

        internal static string InferGameRoot(string executablePath)
        {
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                return null;
            }
            string fullPathSnapshot;
            try
            {
                string fullPath = Path.GetFullPath(executablePath.Trim().Trim('"'));
                fullPathSnapshot = (Directory.Exists(fullPath) ? fullPath : Path.GetDirectoryName(fullPath));
            }
            catch
            {
                return null;
            }
            if (string.IsNullOrEmpty(fullPathSnapshot))
            {
                return null;
            }
            for (int i = 0; i < 4; i++)
            {
                string fileName;
                try
                {
                    fileName = Path.GetFileName(fullPathSnapshot.TrimEnd('\\'));
                }
                catch
                {
                    break;
                }
                if (string.IsNullOrEmpty(fileName) || !IsGenericDirName(fileName))
                {
                    break;
                }
                string directoryName;
                try
                {
                    directoryName = Path.GetDirectoryName(fullPathSnapshot.TrimEnd('\\'));
                }
                catch
                {
                    break;
                }
                if (string.IsNullOrEmpty(directoryName))
                {
                    break;
                }
                fullPathSnapshot = directoryName;
            }
            return fullPathSnapshot;
        }

        private static bool IsJunkName(string name)
        {
            return GameSessionDetector.ElectionVetoed(Path.GetFileNameWithoutExtension(name ?? ""), null);
        }

        internal static string PickMainExe(string dir)
        {
            return ExecutableCandidateProbe.PickMainExecutable(dir);
        }

        private static void FromSteam(string root, List<ScanHit> hits, HashSet<string> roots)
        {
            string getValueOffset = null;
            try
            {
                getValueOffset = Registry.GetValue("HKEY_CURRENT_USER\\Software\\Valve\\Steam", "SteamPath", null) as string;
            }
            catch
            {
            }
            if (string.IsNullOrEmpty(getValueOffset))
            {
                try
                {
                    getValueOffset = Registry.GetValue("HKEY_LOCAL_MACHINE\\SOFTWARE\\WOW6432Node\\Valve\\Steam", "InstallPath", null) as string;
                }
                catch
                {
                }
            }
            if (!string.IsNullOrEmpty(getValueOffset))
            {
                FromSteamLibraries(getValueOffset.Replace('/', '\\'), root, hits, roots);
            }
        }

        internal static void FromSteamLibraries(string steam, string root, List<ScanHit> hits, HashSet<string> roots)
        {
            HashSet<string> steams = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            steams.Add(steam);
            string path = Path.Combine(steam, "steamapps\\libraryfolders.vdf");
            try
            {
                if (File.Exists(path))
                {
                    foreach (Match matche in Regex.Matches(File.ReadAllText(path), "\"(?:path|[0-9]+)\"\\s+\"((?:\\\\.|[^\"\\\\])*)\"", RegexOptions.IgnoreCase))
                    {
                        try
                        {
                            string replaceResult = matche.Groups[1].Value.Replace("\\\\", "\\").Replace('/', '\\');
                            if (Path.IsPathRooted(replaceResult))
                            {
                                steams.Add(replaceResult);
                            }
                        }
                        catch
                        {
                        }
                    }
                }
            }
            catch
            {
            }
            foreach (string currentSteam in steams)
            {
                string steamappsPath;
                string[] files;
                try
                {
                    steamappsPath = Path.Combine(currentSteam, "steamapps");
                    files = Directory.GetFiles(steamappsPath, "appmanifest_*.acf");
                }
                catch
                {
                    continue;
                }
                foreach (string path2 in files)
                {
                    try
                    {
                        string input = File.ReadAllText(path2);
                        Match match = Regex.Match(input, "\"name\"\\s+\"([^\"]+)\"");
                        Match currentMatch = Regex.Match(input, "\"installdir\"\\s+\"([^\"]+)\"");
                        if (currentMatch.Success)
                        {
                            string name = (match.Success ? match.Groups[1].Value : null);
                            AddManifestHit(root, hits, roots, name, Path.Combine(steamappsPath, "common\\" + currentMatch.Groups[1].Value), null);
                        }
                    }
                    catch
                    {
                    }
                }
            }
        }

        internal static string JsonStr(string json, string key)
        {
            if (string.IsNullOrEmpty(json) || key == null)
            {
                return null;
            }
            for (int i = 0; i < json.Length; i++)
            {
                if (json[i] != '"')
                {
                    continue;
                }
                string text;
                if (!ReadJsonString(json, ref i, out text))
                {
                    return null;
                }
                if (!string.Equals(text, key, StringComparison.Ordinal))
                {
                    i--;
                    continue;
                }
                int j;
                for (j = i; j < json.Length && char.IsWhiteSpace(json[j]); j++)
                {
                }
                if (j >= json.Length || json[j] != ':')
                {
                    i--;
                    continue;
                }
                for (j++; j < json.Length && char.IsWhiteSpace(json[j]); j++)
                {
                }
                string currentText;
                if (!ReadJsonString(json, ref j, out currentText))
                {
                    return null;
                }
                return currentText;
            }
            return null;
        }

        private static bool ReadJsonString(string json, ref int offset, out string value)
        {
            value = null;
            if (offset >= json.Length || json[offset] != '"')
            {
                return false;
            }
            offset++;
            StringBuilder stringBuilder = new StringBuilder();
            while (offset < json.Length)
            {
                char c = json[offset++];
                if (c == '"')
                {
                    value = stringBuilder.ToString();
                    return true;
                }
                if (c < ' ')
                {
                    return false;
                }
                if (c != '\\')
                {
                    stringBuilder.Append(c);
                    continue;
                }
                if (offset >= json.Length)
                {
                    return false;
                }
                switch (json[offset++])
                {
                case '"':
                    stringBuilder.Append('"');
                    break;
                case '\\':
                    stringBuilder.Append('\\');
                    break;
                case '/':
                    stringBuilder.Append('/');
                    break;
                case 'b':
                    stringBuilder.Append('\b');
                    break;
                case 'f':
                    stringBuilder.Append('\f');
                    break;
                case 'n':
                    stringBuilder.Append('\n');
                    break;
                case 'r':
                    stringBuilder.Append('\r');
                    break;
                case 't':
                    stringBuilder.Append('\t');
                    break;
                case 'u':
                {
                    if (json.Length - offset < 4)
                    {
                        return false;
                    }
                    int jsonStringValue = 0;
                    for (int i = 0; i < 4; i++)
                    {
                        char c2 = json[offset++];
                        int remainingC2 = ((c2 >= '0' && c2 <= '9') ? (c2 - 48) : ((c2 >= 'a' && c2 <= 'f') ? (c2 - 97 + 10) : ((c2 >= 'A' && c2 <= 'F') ? (c2 - 65 + 10) : (-1))));
                        if (remainingC2 < 0)
                        {
                            return false;
                        }
                        jsonStringValue = jsonStringValue * 16 + remainingC2;
                    }
                    stringBuilder.Append((char)jsonStringValue);
                    break;
                }
                default:
                    return false;
                }
            }
            return false;
        }

        private static void FromEpic(string root, List<ScanHit> hits, HashSet<string> roots)
        {
            string mdir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic\\EpicGamesLauncher\\Data\\Manifests");
            FromEpicManifests(mdir, root, hits, roots);
        }

        internal static void FromEpicManifests(string mdir, string root, List<ScanHit> hits, HashSet<string> roots)
        {
            string[] files;
            try
            {
                files = Directory.GetFiles(mdir, "*.item");
            }
            catch
            {
                return;
            }
            foreach (string path in files)
            {
                try
                {
                    string json = File.ReadAllText(path);
                    string jsonStrResult = JsonStr(json, "InstallLocation");
                    if (jsonStrResult != null)
                    {
                        string jsonStrResult2 = JsonStr(json, "LaunchExecutable");
                        string exePath = ((jsonStrResult2 != null && jsonStrResult2.Length > 0) ? Path.Combine(jsonStrResult, jsonStrResult2.Replace('/', '\\')) : null);
                        AddManifestHit(root, hits, roots, JsonStr(json, "DisplayName"), jsonStrResult, exePath);
                    }
                }
                catch
                {
                }
            }
        }

        private static void FromGog(string root, List<ScanHit> hits, HashSet<string> roots)
        {
            string[] gogRegistryPaths = new string[2] { "SOFTWARE\\WOW6432Node\\GOG.com\\Games", "SOFTWARE\\GOG.com\\Games" };
            foreach (string name in gogRegistryPaths)
            {
                using (RegistryKey registryKey = Registry.LocalMachine.OpenSubKey(name))
                {
                    if (registryKey == null)
                    {
                        continue;
                    }
                    foreach (string name2 in registryKey.GetSubKeyNames())
                    {
                        try
                        {
                            using (RegistryKey currentRegistryKey = registryKey.OpenSubKey(name2))
                            {
                                if (currentRegistryKey != null)
                                {
                                    string dir = currentRegistryKey.GetValue("path") as string;
                                    string exePath = currentRegistryKey.GetValue("exe") as string;
                                    AddManifestHit(root, hits, roots, currentRegistryKey.GetValue("gameName") as string, dir, exePath);
                                }
                            }
                        }
                        catch
                        {
                        }
                    }
                }
            }
        }

        private static void FromUbisoft(string root, List<ScanHit> hits, HashSet<string> roots)
        {
            using (RegistryKey registryKey = Registry.LocalMachine.OpenSubKey("SOFTWARE\\WOW6432Node\\Ubisoft\\Launcher\\Installs"))
            {
                if (registryKey == null)
                {
                    return;
                }
                foreach (string name in registryKey.GetSubKeyNames())
                {
                    try
                    {
                        using (RegistryKey currentRegistryKey = registryKey.OpenSubKey(name))
                        {
                            if (currentRegistryKey != null)
                            {
                                AddManifestHit(root, hits, roots, null, currentRegistryKey.GetValue("InstallDir") as string, null);
                            }
                        }
                    }
                    catch
                    {
                    }
                }
            }
        }

        private static string[] FixedDriveRoots()
        {
            List<string> fullNames = new List<string>();
            try
            {
                foreach (DriveInfo driveInfo in DriveInfo.GetDrives())
                {
                    try
                    {
                        if (driveInfo.IsReady)
                        {
                            fullNames.Add(driveInfo.RootDirectory.FullName);
                        }
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
            return fullNames.ToArray();
        }

        private static void FromXbox(string root, List<ScanHit> hits, HashSet<string> roots)
        {
            foreach (string path in FixedDriveRoots())
            {
                string[] directories;
                try
                {
                    directories = Directory.GetDirectories(Path.Combine(path, "XboxGames"));
                }
                catch
                {
                    continue;
                }
                foreach (string entryText in directories)
                {
                    try
                    {
                        string name = Path.GetFileName(entryText.TrimEnd('\\'));
                        string contentPath = Path.Combine(entryText, "Content");
                        string message = (Directory.Exists(contentPath) ? contentPath : entryText);
                        string exePath = null;
                        string path2 = Path.Combine(message, "MicrosoftGame.config");
                        if (File.Exists(path2))
                        {
                            string input = File.ReadAllText(path2);
                            Match match = Regex.Match(input, "<Executable[^>]*\\bName\\s*=\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);
                            if (match.Success)
                            {
                                string combinedPath = Path.Combine(message, match.Groups[1].Value.Replace('/', '\\'));
                                if (File.Exists(combinedPath))
                                {
                                    exePath = combinedPath;
                                }
                            }
                            Match currentMatch = Regex.Match(input, "DefaultDisplayName\\s*=\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);
                            if (currentMatch.Success && currentMatch.Groups[1].Value.Trim().Length > 0 && !currentMatch.Groups[1].Value.StartsWith("ms-resource", StringComparison.OrdinalIgnoreCase))
                            {
                                name = currentMatch.Groups[1].Value.Trim();
                            }
                        }
                        AddManifestHit(root, hits, roots, name, message, exePath);
                    }
                    catch
                    {
                    }
                }
            }
        }

        private static void FromMicrosoftStore(string root, List<ScanHit> hits, HashSet<string> roots)
        {
            FromPackageRepository(root, "Software\\Classes\\Local Settings\\Software\\Microsoft\\Windows\\CurrentVersion\\AppModel\\Repository\\Packages", hits, roots);
        }

        internal static void FromPackageRepository(string root, string repoKey, List<ScanHit> hits, HashSet<string> roots)
        {
            using (RegistryKey registryKey = Registry.CurrentUser.OpenSubKey(repoKey))
            {
                if (registryKey == null)
                {
                    return;
                }
                foreach (string entryKey in registryKey.GetSubKeyNames())
                {
                    try
                    {
                        using (RegistryKey currentRegistryKey = registryKey.OpenSubKey(entryKey))
                        {
                            if (currentRegistryKey == null)
                            {
                                continue;
                            }
                            string getValueOffset = currentRegistryKey.GetValue("PackageRootFolder") as string;
                            if (string.IsNullOrEmpty(getValueOffset))
                            {
                                continue;
                            }
                            getValueOffset = getValueOffset.Trim().TrimEnd('\\');
                            if (getValueOffset.IndexOf("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase) < 0 || (!File.Exists(Path.Combine(getValueOffset, "MicrosoftGame.config")) && !File.Exists(Path.Combine(getValueOffset, "xboxservices.config"))))
                            {
                                continue;
                            }
                            string message = null;
                            string path = Path.Combine(getValueOffset, "MicrosoftGame.config");
                            if (File.Exists(path))
                            {
                                try
                                {
                                    Match match = Regex.Match(File.ReadAllText(path), "<Executable[^>]*\\bName\\s*=\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);
                                    if (match.Success)
                                    {
                                        string combinedPath = Path.Combine(getValueOffset, match.Groups[1].Value.Replace('/', '\\'));
                                        if (File.Exists(combinedPath))
                                        {
                                            message = combinedPath;
                                        }
                                    }
                                }
                                catch
                                {
                                }
                            }
                            if (message == null)
                            {
                                string path2 = Path.Combine(getValueOffset, "AppxManifest.xml");
                                if (File.Exists(path2))
                                {
                                    try
                                    {
                                        Match currentMatch = Regex.Match(File.ReadAllText(path2), "<Application\\b[^>]*\\bExecutable\\s*=\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);
                                        if (currentMatch.Success)
                                        {
                                            string combinedPath2 = Path.Combine(getValueOffset, currentMatch.Groups[1].Value.Replace('/', '\\'));
                                            if (File.Exists(combinedPath2))
                                            {
                                                message = combinedPath2;
                                            }
                                        }
                                    }
                                    catch
                                    {
                                    }
                                }
                            }
                            string getValueOffset2 = currentRegistryKey.GetValue("DisplayName") as string;
                            if (string.IsNullOrEmpty(getValueOffset2) || getValueOffset2.StartsWith("@"))
                            {
                                getValueOffset2 = PackageBaseName(entryKey);
                            }
                            AddManifestHit(root, hits, roots, getValueOffset2, getValueOffset, message);
                        }
                    }
                    catch
                    {
                    }
                }
            }
        }

        private static string PackageBaseName(string packageFullName)
        {
            string packageFullNameOffset = packageFullName ?? "";
            int matchIndex = packageFullNameOffset.IndexOf('_');
            if (matchIndex > 0)
            {
                packageFullNameOffset = packageFullNameOffset.Substring(0, matchIndex);
            }
            int matchIndex2 = packageFullNameOffset.IndexOf('.');
            if (matchIndex2 >= 0 && matchIndex2 < packageFullNameOffset.Length - 1)
            {
                packageFullNameOffset = packageFullNameOffset.Substring(matchIndex2 + 1);
            }
            return packageFullNameOffset;
        }

        private static void FromRiot(string root, List<ScanHit> hits, HashSet<string> roots)
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Riot Games\\Metadata");
            string[] directories;
            try
            {
                directories = Directory.GetDirectories(path);
            }
            catch
            {
                return;
            }
            foreach (string path2 in directories)
            {
                try
                {
                    foreach (string path3 in Directory.GetFiles(path2, "*.yaml"))
                    {
                        Match match = Regex.Match(File.ReadAllText(path3), "product_install_full_path:\\s*\"?([^\"\\r\\n]+?)\"?\\s*$", RegexOptions.Multiline);
                        if (match.Success)
                        {
                            string replaceResult = match.Groups[1].Value.Trim().Replace('/', '\\');
                            AddManifestHit(root, hits, roots, Path.GetFileName(replaceResult.TrimEnd('\\')), replaceResult, null);
                        }
                    }
                }
                catch
                {
                }
            }
        }

        private static void FromWeGameApps(string root, List<ScanHit> hits, HashSet<string> roots)
        {
            foreach (string path in FixedDriveRoots())
            {
                string[] gameDirectoryCandidates = new string[3]
                {
                    Path.Combine(path, "WeGameApps"),
                    Path.Combine(path, "Program Files\\WeGameApps"),
                    Path.Combine(path, "Program Files (x86)\\WeGameApps")
                };
                foreach (string path2 in gameDirectoryCandidates)
                {
                    string[] directories;
                    try
                    {
                        directories = Directory.GetDirectories(path2);
                    }
                    catch
                    {
                        continue;
                    }
                    foreach (string entryText in directories)
                    {
                        try
                        {
                            string fileName = Path.GetFileName(entryText.TrimEnd('\\'));
                            if (fileName.Length != 0 && fileName[0] != '.')
                            {
                                AddManifestHit(root, hits, roots, fileName, entryText, null);
                            }
                        }
                        catch
                        {
                        }
                    }
                }
            }
        }

        private static void FromBattleNet(string root, List<ScanHit> hits, HashSet<string> roots)
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Battle.net\\Agent\\product.db");
            if (!File.Exists(path))
            {
                return;
            }
            string @string;
            try
            {
                @string = Encoding.UTF8.GetString(File.ReadAllBytes(path));
            }
            catch
            {
                return;
            }
            foreach (Match matche in Regex.Matches(@string, "[A-Za-z]:[/\\\\][^\\x00-\\x1f\"|?*<>]{2,200}"))
            {
                try
                {
                    string replaceTrimmed = matche.Value.Replace('/', '\\').TrimEnd('\\');
                    if (Directory.Exists(replaceTrimmed) && File.Exists(Path.Combine(replaceTrimmed, ".build.info")))
                    {
                        AddManifestHit(root, hits, roots, Path.GetFileName(replaceTrimmed), replaceTrimmed, null);
                    }
                }
                catch
                {
                }
            }
        }

        private static void FromShortcuts(string root, List<ScanHit> hits, HashSet<string> roots, Func<bool> canceled)
        {
            List<string> folderPaths = new List<string>();
            foreach (Environment.SpecialFolder folder in new Environment.SpecialFolder[4]
            {
                Environment.SpecialFolder.DesktopDirectory,
                Environment.SpecialFolder.CommonDesktopDirectory,
                Environment.SpecialFolder.StartMenu,
                Environment.SpecialFolder.CommonStartMenu
            })
            {
                try
                {
                    string folderPath = Environment.GetFolderPath(folder);
                    if (!string.IsNullOrEmpty(folderPath))
                    {
                        folderPaths.Add(folderPath);
                    }
                }
                catch
                {
                }
            }
            ScanShortcutDirectories(folderPaths, root, hits, roots, canceled);
        }

        internal static void ScanShortcutDirectories(IEnumerable<string> locations, string root, List<ScanHit> hits, HashSet<string> roots, Func<bool> canceled)
        {
            if (locations == null || Stop(canceled))
            {
                return;
            }
            Queue<ShortcutDirectory> queue = new Queue<ShortcutDirectory>();
            HashSet<string> cleanDirResults = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> textsSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string location in locations)
            {
                if (!Stop(canceled) && cleanDirResults.Count < 128)
                {
                    string cleanDirResult = CleanDir(location);
                    if (cleanDirResult != null && cleanDirResults.Add(cleanDirResult))
                    {
                        queue.Enqueue(new ShortcutDirectory
                        {
                            Path = cleanDirResult
                        });
                    }
                    continue;
                }
                break;
            }
            int processedCount = 0;
            while (queue.Count > 0 && !Stop(canceled))
            {
                ShortcutDirectory shortcutDirectory = queue.Dequeue();
                try
                {
                    if ((File.GetAttributes(shortcutDirectory.Path) & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }
                }
                catch
                {
                    continue;
                }
                try
                {
                    foreach (string enumerateFile in Directory.EnumerateFiles(shortcutDirectory.Path, "*.lnk"))
                    {
                        if (Stop(canceled) || processedCount++ >= 1024)
                        {
                            return;
                        }
                        try
                        {
                            if ((File.GetAttributes(enumerateFile) & FileAttributes.ReparsePoint) != 0)
                            {
                                continue;
                            }
                            string shortcut = GameExecutableResolver.ResolveShortcut(enumerateFile);
                            if (string.IsNullOrWhiteSpace(shortcut))
                            {
                                continue;
                            }
                            shortcut = Environment.ExpandEnvironmentVariables(shortcut.Trim().Trim('"')).Replace('/', '\\');
                            if (!Path.IsPathRooted(shortcut) || shortcut.StartsWith("\\\\", StringComparison.Ordinal) || !string.Equals(Path.GetExtension(shortcut), ".exe", StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }
                            shortcut = Path.GetFullPath(shortcut);
                            string executablePath;
                            string error;
                            if (!IsLocalShortcutTarget(shortcut) || !GameExecutableResolver.TryResolve(shortcut, out executablePath, out error))
                            {
                                continue;
                            }
                            string cleanDirResult2 = CleanDir(InferGameRoot(executablePath));
                            if (cleanDirResult2 == null || !UnderRoot(cleanDirResult2, root) || IsSystemOrTooBroad(cleanDirResult2) || IsSystemWideDirName(Path.GetFileName(cleanDirResult2)))
                            {
                                continue;
                            }
                            bool underLibraryRoot = false;
                            foreach (string root2 in roots)
                            {
                                if (UnderRoot(cleanDirResult2, root2))
                                {
                                    underLibraryRoot = true;
                                    break;
                                }
                            }
                            if (underLibraryRoot || textsSet.Contains(cleanDirResult2))
                            {
                                continue;
                            }
                            if (textsSet.Count >= 128)
                            {
                                return;
                            }
                            textsSet.Add(cleanDirResult2);
                            if (LooksLikeGameDir(cleanDirResult2, 3) && !Stop(canceled))
                            {
                                int count = hits.Count;
                                AddManifestHit(root, hits, roots, Path.GetFileName(cleanDirResult2), cleanDirResult2, null);
                                if (hits.Count == count + 1 && !hits[count].NeedsChoice && string.Equals(hits[count].Exe, executablePath, StringComparison.OrdinalIgnoreCase))
                                {
                                    hits[count].Name = Path.GetFileNameWithoutExtension(enumerateFile);
                                }
                            }
                        }
                        catch
                        {
                        }
                    }
                }
                catch
                {
                }
                if (shortcutDirectory.Depth >= 4)
                {
                    continue;
                }
                try
                {
                    foreach (string enumerateDirectory in Directory.EnumerateDirectories(shortcutDirectory.Path))
                    {
                        if (!Stop(canceled) && cleanDirResults.Count < 128)
                        {
                            string fileName = Path.GetFileName(enumerateDirectory);
                            if (fileName.Length != 0 && fileName[0] != '.' && !SkipDirs.Contains(fileName) && cleanDirResults.Add(enumerateDirectory))
                            {
                                queue.Enqueue(new ShortcutDirectory
                                {
                                    Path = enumerateDirectory,
                                    Depth = shortcutDirectory.Depth + 1
                                });
                            }
                            continue;
                        }
                        break;
                    }
                }
                catch
                {
                }
            }
        }

        private static bool IsLocalShortcutTarget(string target)
        {
            try
            {
                string pathRoot = Path.GetPathRoot(target);
                if (new DriveInfo(pathRoot).DriveType == DriveType.Network)
                {
                    return false;
                }
                string substringResult = Path.GetDirectoryName(target).Substring(pathRoot.Length);
                string pathRootSnapshot = pathRoot;
                foreach (string path in substringResult.Split(new char[1] { '\\' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    pathRootSnapshot = Path.Combine(pathRootSnapshot, path);
                    if ((File.GetAttributes(pathRootSnapshot) & FileAttributes.ReparsePoint) != 0)
                    {
                        return false;
                    }
                }
                return (File.GetAttributes(target) & FileAttributes.ReparsePoint) == 0;
            }
            catch
            {
                return false;
            }
        }
    }
}
