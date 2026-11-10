using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static class SteamShortcut
    {

        public static bool TryResolve(string urlPath, out string executablePath, out string error)
        {
            string suggestedName;
            return TryResolve(urlPath, out executablePath, out error, out suggestedName);
        }

        public static bool TryResolve(string urlPath, out string executablePath, out string error, out string suggestedName)
        {
            executablePath = null;
            error = null;
            suggestedName = null;
            string content;
            try
            {
                content = File.ReadAllText(urlPath);
            }
            catch
            {
                error = Lang.T("t.steamshortcut.1");
                return false;
            }
            long appId;
            if (!TryParseUrlFile(content, out appId))
            {
                error = Lang.T("t.steamshortcut.2");
                return false;
            }
            string steamRoot = FindSteamRoot();
            if (steamRoot == null)
            {
                error = Lang.T("t.steamshortcut.3");
                return false;
            }
            string message = null;
            foreach (string libraryRoot in LibraryRoots(steamRoot))
            {
                string path = Path.Combine(libraryRoot, "steamapps\\appmanifest_" + appId + ".acf");
                try
                {
                    if (!File.Exists(path))
                    {
                        continue;
                    }
                    string vdfValue = ParseVdfValue(File.ReadAllText(path), "installdir");
                    if (!string.IsNullOrEmpty(vdfValue))
                    {
                        string combinedPath = Path.Combine(libraryRoot, "steamapps\\common\\" + vdfValue);
                        if (Directory.Exists(combinedPath))
                        {
                            message = combinedPath;
                            break;
                        }
                    }
                }
                catch
                {
                }
            }
            if (message == null)
            {
                error = Lang.T("t.steamshortcut.4") + appId + Lang.T("t.steamshortcut.5");
                return false;
            }
            string pickMainExecutableResult = PickMainExecutable(message, Path.GetFileName(message));
            if (pickMainExecutableResult == null)
            {
                error = Lang.T("t.steamshortcut.6") + message;
                return false;
            }
            executablePath = pickMainExecutableResult;
            suggestedName = Path.GetFileName(message);
            return true;
        }

        internal static bool TryParseUrlFile(string content, out long appId)
        {
            appId = 0L;
            if (string.IsNullOrEmpty(content))
            {
                return false;
            }
            foreach (string entryText in content.Split('\n'))
            {
                string entryTextTrimmed = entryText.Trim();
                if (entryTextTrimmed.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))
                {
                    string substringTrimmed = entryTextTrimmed.Substring(4).Trim();
                    if (!substringTrimmed.StartsWith("steam://rungameid/", StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                    string substringResult = substringTrimmed.Substring("steam://rungameid/".Length);
                    int j;
                    for (j = 0; j < substringResult.Length && char.IsDigit(substringResult[j]); j++)
                    {
                    }
                    if (j == 0)
                    {
                        return false;
                    }
                    return long.TryParse(substringResult.Substring(0, j), out appId) && appId > 0;
                }
            }
            return false;
        }

        private static string FindSteamRoot()
        {
            try
            {
                using (RegistryKey registryKey = Registry.CurrentUser.OpenSubKey("Software\\Valve\\Steam"))
                {
                    string getValueOffset = ((registryKey == null) ? null : (registryKey.GetValue("SteamPath") as string));
                    if (!string.IsNullOrEmpty(getValueOffset))
                    {
                        getValueOffset = getValueOffset.Replace('/', '\\');
                        if (Directory.Exists(getValueOffset))
                        {
                            return getValueOffset;
                        }
                    }
                }
            }
            catch
            {
            }
            try
            {
                using (RegistryKey currentRegistryKey = Registry.LocalMachine.OpenSubKey("SOFTWARE\\WOW6432Node\\Valve\\Steam"))
                {
                    string getValueOffset2 = ((currentRegistryKey == null) ? null : (currentRegistryKey.GetValue("InstallPath") as string));
                    if (!string.IsNullOrEmpty(getValueOffset2) && Directory.Exists(getValueOffset2))
                    {
                        return getValueOffset2;
                    }
                }
            }
            catch
            {
            }
            return null;
        }

        private static IEnumerable<string> LibraryRoots(string steamRoot)
        {
            List<string> steamRoots = new List<string>();
            steamRoots.Add(steamRoot);
            List<string> texts = steamRoots;
            foreach (string path in new string[2]
            {
                Path.Combine(steamRoot, "steamapps\\libraryfolders.vdf"),
                Path.Combine(steamRoot, "config\\libraryfolders.vdf")
            })
            {
                try
                {
                    if (!File.Exists(path))
                    {
                        continue;
                    }
                    foreach (string parseLibraryPath in ParseLibraryPaths(File.ReadAllText(path)))
                    {
                        bool rootAlreadyAdded = false;
                        foreach (string text in texts)
                        {
                            if (string.Equals(text, parseLibraryPath, StringComparison.OrdinalIgnoreCase))
                            {
                                rootAlreadyAdded = true;
                                break;
                            }
                        }
                        if (!rootAlreadyAdded && Directory.Exists(parseLibraryPath))
                        {
                            texts.Add(parseLibraryPath);
                        }
                    }
                }
                catch
                {
                }
            }
            return texts;
        }

        internal static List<string> ParseLibraryPaths(string vdfContent)
        {
            List<string> replaceResults = new List<string>();
            if (string.IsNullOrEmpty(vdfContent))
            {
                return replaceResults;
            }
            foreach (Match item in Regex.Matches(vdfContent, "\"path\"\\s+\"((?:\\\\.|[^\"\\\\])*)\""))
            {
                string replaceResult = item.Groups[1].Value.Replace("\\\\", "\\");
                if (replaceResult.Length > 0)
                {
                    replaceResults.Add(replaceResult);
                }
            }
            return replaceResults;
        }

        internal static string ParseVdfValue(string content, string key)
        {
            if (string.IsNullOrEmpty(content))
            {
                return null;
            }
            Match match = Regex.Match(content, "\"" + Regex.Escape(key) + "\"\\s+\"((?:\\\\.|[^\"\\\\])*)\"", RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                return null;
            }
            return match.Groups[1].Value.Replace("\\\\", "\\");
        }

        internal static string PickMainExecutable(string root, string installDirName)
        {
            return ExecutableCandidateProbe.PickMainExecutable(root);
        }
    }
}
