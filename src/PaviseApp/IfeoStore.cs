using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static class IfeoStore
    {

        internal static readonly RegistryKey Hive = Registry.LocalMachine;

        internal static readonly string RootOverride = null;

        public static string RootPath
        {
            get
            {
                return RootOverride ?? "SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Image File Execution Options";
            }
        }

        public static string[] ParseList(string raw)
        {
            return (raw ?? "").Split(new char[1] { ';' }, StringSplitOptions.RemoveEmptyEntries);
        }

        public static void RemoveFromList(string listKey, string exe)
        {
            List<string> entryTexts = new List<string>();
            foreach (string entryText in ParseList(Settings.LoadStr(listKey, "")))
            {
                if (!string.Equals(entryText, exe, StringComparison.OrdinalIgnoreCase))
                {
                    entryTexts.Add(entryText);
                }
            }
            Settings.SaveStr(listKey, string.Join(";", entryTexts.ToArray()));
        }

        public static void CleanupEmpty(string exe, string subKey, bool ourSub, bool ourExe)
        {
            try
            {
                using (RegistryKey registryKey = Hive.OpenSubKey(RootPath, true))
                {
                    if (registryKey == null)
                    {
                        return;
                    }
                    if (subKey != null && ourSub)
                    {
                        using (RegistryKey currentRegistryKey = registryKey.OpenSubKey(exe, true))
                        {
                            if (currentRegistryKey != null)
                            {
                                using (RegistryKey candidateRegistryKey = currentRegistryKey.OpenSubKey(subKey))
                                {
                                    if (candidateRegistryKey != null && candidateRegistryKey.ValueCount == 0 && candidateRegistryKey.SubKeyCount == 0)
                                    {
                                        currentRegistryKey.DeleteSubKey(subKey, false);
                                    }
                                }
                            }
                        }
                    }
                    if (!ourExe)
                    {
                        return;
                    }
                    using (RegistryKey resolvedRegistryKey = registryKey.OpenSubKey(exe))
                    {
                        if (resolvedRegistryKey == null || resolvedRegistryKey.ValueCount != 0 || resolvedRegistryKey.SubKeyCount != 0)
                        {
                            return;
                        }
                    }
                    registryKey.DeleteSubKey(exe, false);
                }
            }
            catch
            {
            }
        }
    }
}
