using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static class FthTweak
    {
        internal sealed class Shim
        {
            public string Exe;

            public string ValueName;

            public string SdbPath;
        }

        internal enum SdbVerdict
        {
            NotFth,
            Fth,
            Unknown
        }

        private static readonly object lk = new object();

        private static List<Shim> cached;

        private static long cachedTicks;

        public static bool RepairedByPavise
        {
            get
            {
                if (Settings.LoadStr("FthCustomBak", "").Length <= 0)
                {
                    return Settings.LoadStr("FthExclusionBak", "").Length > 0;
                }
                return true;
            }
        }

        public static bool FeatureEnabled()
        {
            try
            {
                using (RegistryKey registryKey = Registry.LocalMachine.OpenSubKey("SOFTWARE\\Microsoft\\FTH"))
                {
                    if (registryKey == null)
                    {
                        return false;
                    }
                    object instance = registryKey.GetValue("Enabled");
                    return instance == null || Convert.ToInt64(instance) != 0;
                }
            }
            catch
            {
                return false;
            }
        }

        public static bool TryReadTrigger(out int velocity, out int windowMinutes)
        {
            velocity = 0;
            windowMinutes = 0;
            try
            {
                using (RegistryKey registryKey = Registry.LocalMachine.OpenSubKey("SOFTWARE\\Microsoft\\FTH"))
                {
                    if (registryKey == null)
                    {
                        return false;
                    }
                    object instance = registryKey.GetValue("CrashVelocity");
                    object currentInstance = registryKey.GetValue("CrashWindowInMinutes");
                    if (instance == null || currentInstance == null)
                    {
                        return false;
                    }
                    velocity = Convert.ToInt32(instance);
                    windowMinutes = Convert.ToInt32(currentInstance);
                    return velocity > 0 && windowMinutes > 0;
                }
            }
            catch
            {
                return false;
            }
        }

        public static string[] Exclusions()
        {
            try
            {
                using (RegistryKey registryKey = Registry.LocalMachine.OpenSubKey("SOFTWARE\\Microsoft\\FTH"))
                {
                    if (registryKey == null)
                    {
                        return new string[0];
                    }
                    return (registryKey.GetValue("ExclusionList") as string[]) ?? new string[0];
                }
            }
            catch
            {
                return new string[0];
            }
        }

        public static bool IsExcluded(string exe)
        {
            if (string.IsNullOrEmpty(exe))
            {
                return false;
            }
            foreach (string a in Exclusions())
            {
                if (string.Equals(a, exe, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private static HashSet<string> StateNames()
        {
            HashSet<string> entryTexts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (RegistryKey registryKey = Registry.LocalMachine.OpenSubKey("SOFTWARE\\Microsoft\\FTH\\State"))
                {
                    if (registryKey == null)
                    {
                        return entryTexts;
                    }
                    foreach (string entryText in registryKey.GetValueNames())
                    {
                        if (entryText.Length > 0)
                        {
                            entryTexts.Add(entryText);
                        }
                    }
                    foreach (string entryKey in registryKey.GetSubKeyNames())
                    {
                        if (entryKey.Length > 0)
                        {
                            entryTexts.Add(entryKey);
                        }
                    }
                }
            }
            catch
            {
            }
            return entryTexts;
        }

        internal static SdbVerdict ClassifySdb(string valueName, out string sdbPath)
        {
            sdbPath = null;
            string valueNameSnapshot = valueName;
            if (valueNameSnapshot.EndsWith(".sdb", StringComparison.OrdinalIgnoreCase))
            {
                valueNameSnapshot = valueNameSnapshot.Substring(0, valueNameSnapshot.Length - 4);
            }
            if (valueNameSnapshot.Length == 0)
            {
                return SdbVerdict.Unknown;
            }
            try
            {
                using (RegistryKey registryKey = Registry.LocalMachine.OpenSubKey("SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\AppCompatFlags\\InstalledSDB\\" + valueNameSnapshot))
                {
                    if (registryKey == null)
                    {
                        return SdbVerdict.Unknown;
                    }
                    string getValueOffset = registryKey.GetValue("DatabaseDescription") as string;
                    sdbPath = registryKey.GetValue("DatabasePath") as string;
                    if (string.IsNullOrEmpty(getValueOffset))
                    {
                        return SdbVerdict.Unknown;
                    }
                    return (getValueOffset.IndexOf("fault tolerant heap", StringComparison.OrdinalIgnoreCase) >= 0 || getValueOffset.IndexOf("faulttolerantheap", StringComparison.OrdinalIgnoreCase) >= 0) ? SdbVerdict.Fth : SdbVerdict.NotFth;
                }
            }
            catch
            {
                return SdbVerdict.Unknown;
            }
        }

        private static void InvalidateCache()
        {
            lock (lk)
            {
                cached = null;
                cachedTicks = 0L;
            }
        }

        public static List<Shim> Detect()
        {
            lock (lk)
            {
                if (cached != null && DateTime.UtcNow.Ticks - cachedTicks < 20000000)
                {
                    return new List<Shim>(cached);
                }
            }
            List<Shim> collection = Scan();
            lock (lk)
            {
                cached = collection;
                cachedTicks = DateTime.UtcNow.Ticks;
            }
            return new List<Shim>(collection);
        }

        private static List<Shim> Scan()
        {
            List<Shim> shims = new List<Shim>();
            HashSet<string> textsSet = StateNames();
            try
            {
                using (RegistryKey registryKey = Registry.LocalMachine.OpenSubKey("SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\AppCompatFlags\\Custom"))
                {
                    if (registryKey == null)
                    {
                        return shims;
                    }
                    foreach (string entryKey in registryKey.GetSubKeyNames())
                    {
                        using (RegistryKey currentRegistryKey = registryKey.OpenSubKey(entryKey))
                        {
                            if (currentRegistryKey == null)
                            {
                                continue;
                            }
                            foreach (string entryText in currentRegistryKey.GetValueNames())
                            {
                                if (entryText.EndsWith(".sdb", StringComparison.OrdinalIgnoreCase))
                                {
                                    string sdbPath;
                                    SdbVerdict sdbVerdict = ClassifySdb(entryText, out sdbPath);
                                    if (sdbVerdict != 0 && (sdbVerdict != SdbVerdict.Unknown || textsSet.Contains(entryKey)))
                                    {
                                        shims.Add(new Shim
                                        {
                                            Exe = entryKey,
                                            ValueName = entryText,
                                            SdbPath = sdbPath
                                        });
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch
            {
            }
            return shims;
        }

        public static bool NeedsRepair()
        {
            return Detect().Count > 0;
        }

        public static string Describe()
        {
            if (!FeatureEnabled())
            {
                return Lang.T("fth.off");
            }
            List<Shim> shims = Detect();
            if (shims.Count == 0)
            {
                if (!RepairedByPavise)
                {
                    return Lang.T("fth.clean");
                }
                return Lang.T("fth.cleared");
            }
            List<string> exes = new List<string>();
            foreach (Shim shim in shims)
            {
                if (!exes.Contains(shim.Exe))
                {
                    exes.Add(shim.Exe);
                }
            }
            int velocity;
            int windowMinutes;
            if (TryReadTrigger(out velocity, out windowMinutes))
            {
                return Lang.F("fth.hit.why", string.Join(" ", exes.ToArray()), windowMinutes, velocity);
            }
            return Lang.F("fth.hit", string.Join(" ", exes.ToArray()));
        }

        public static bool Repair()
        {
            lock (lk)
            {
                List<Shim> shims = Detect();
                if (shims.Count == 0)
                {
                    return true;
                }
                List<string> exes = new List<string>();
                foreach (Shim shim in shims)
                {
                    if (!HasName(exes, shim.Exe))
                    {
                        exes.Add(shim.Exe);
                    }
                }
                if (!AddExclusions(exes))
                {
                    Logger.Log(Lang.T("log.fthtweak.1", 0));
                    return false;
                }
                int processedCount = 0;
                foreach (Shim currentShim in shims)
                {
                    if (DropPointer(currentShim))
                    {
                        processedCount++;
                    }
                    else
                    {
                        Logger.Log(Lang.T("log.fthtweak.2", 0) + currentShim.Exe + " " + currentShim.ValueName);
                    }
                }
                InvalidateCache();
                Logger.Log(string.Format(Lang.T("log.fthtweak.3", 0), processedCount, string.Join(" ", exes.ToArray())));
                return processedCount == shims.Count;
            }
        }

        public static bool Restore()
        {
            lock (lk)
            {
                bool restorePointersSucceeded = RestorePointers();
                restorePointersSucceeded &= RestoreExclusions();
                InvalidateCache();
                if (restorePointersSucceeded)
                {
                    Logger.Log(Lang.T("log.fthtweak.4", 0));
                }
                else
                {
                    Logger.Log(Lang.T("log.fthtweak.5", 0));
                }
                return restorePointersSucceeded;
            }
        }

        private static bool AddExclusions(List<string> exes)
        {
            try
            {
                using (RegistryKey registryKey = Registry.LocalMachine.OpenSubKey("SOFTWARE\\Microsoft\\FTH", true))
                {
                    if (registryKey == null)
                    {
                        return false;
                    }
                    object instance = registryKey.GetValue("ExclusionList");
                    if (instance != null)
                    {
                        RegistryValueKind registryValueKind = RegistryValueKind.Unknown;
                        try
                        {
                            registryValueKind = registryKey.GetValueKind("ExclusionList");
                        }
                        catch
                        {
                        }
                        if (registryValueKind != RegistryValueKind.MultiString)
                        {
                            Logger.Warn(Lang.T("log.fthtweak.6", 0) + registryValueKind);
                            return false;
                        }
                    }
                    string[] valueOffset = instance as string[];
                    if (Settings.LoadStr("FthExclusionBak", "").Length == 0)
                    {
                        string encodeListResult = ((valueOffset == null) ? "__pavise_absent__" : EncodeList(valueOffset));
                        Settings.SaveStr("FthExclusionBak", encodeListResult);
                        if (Settings.LoadStr("FthExclusionBak", "") != encodeListResult)
                        {
                            return false;
                        }
                    }
                    List<string> exs = new List<string>();
                    if (valueOffset != null)
                    {
                        exs.AddRange(valueOffset);
                    }
                    foreach (string ex in exes)
                    {
                        if (!HasName(exs, ex))
                        {
                            exs.Add(ex);
                        }
                    }
                    if (valueOffset != null && ListsEqual(valueOffset, exs.ToArray()))
                    {
                        return true;
                    }
                    registryKey.SetValue("ExclusionList", exs.ToArray(), RegistryValueKind.MultiString);
                    string[] getValueOffset = registryKey.GetValue("ExclusionList") as string[];
                    if (getValueOffset == null)
                    {
                        return false;
                    }
                    foreach (string ex2 in exes)
                    {
                        if (!HasName(getValueOffset, ex2))
                        {
                            return false;
                        }
                    }
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        private static bool RestoreExclusions()
        {
            string fthExclusionBakValue = Settings.LoadStr("FthExclusionBak", "");
            if (fthExclusionBakValue.Length == 0)
            {
                return true;
            }
            try
            {
                using (RegistryKey registryKey = Registry.LocalMachine.OpenSubKey("SOFTWARE\\Microsoft\\FTH", true))
                {
                    if (registryKey == null)
                    {
                        return false;
                    }
                    if (fthExclusionBakValue == "__pavise_absent__")
                    {
                        registryKey.DeleteValue("ExclusionList", false);
                        if (registryKey.GetValue("ExclusionList") != null)
                        {
                            return false;
                        }
                    }
                    else
                    {
                        string[] decodeListResult = DecodeList(fthExclusionBakValue);
                        if (decodeListResult == null)
                        {
                            return false;
                        }
                        registryKey.SetValue("ExclusionList", decodeListResult, RegistryValueKind.MultiString);
                        string[] a = registryKey.GetValue("ExclusionList") as string[];
                        if (!ListsEqual(a, decodeListResult))
                        {
                            return false;
                        }
                    }
                }
                Settings.Remove("FthExclusionBak");
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool DropPointer(Shim shim)
        {
            try
            {
                string name = "SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\AppCompatFlags\\Custom\\" + shim.Exe;
                using (RegistryKey registryKey = Registry.LocalMachine.OpenSubKey(name, true))
                {
                    if (registryKey == null)
                    {
                        return false;
                    }
                    object instance = registryKey.GetValue(shim.ValueName);
                    if (instance == null)
                    {
                        return true;
                    }
                    RegistryValueKind valueKind;
                    try
                    {
                        valueKind = registryKey.GetValueKind(shim.ValueName);
                    }
                    catch
                    {
                        return false;
                    }
                    if (valueKind == RegistryValueKind.Unknown)
                    {
                        return false;
                    }
                    object[] instanceItems = new object[7] { shim.Exe, '\u001f', shim.ValueName, '\u001f', null, null, null };
                    int valueKindSnapshot = (int)valueKind;
                    instanceItems[4] = valueKindSnapshot.ToString();
                    instanceItems[5] = '\u001f';
                    instanceItems[6] = EncodeValue(instance);
                    string concatResult = string.Concat(instanceItems);
                    string fthCustomBakValue = Settings.LoadStr("FthCustomBak", "");
                    if (!HasBackupRecord(fthCustomBakValue, shim.Exe, shim.ValueName))
                    {
                        string concatResultOffset = ((fthCustomBakValue.Length == 0) ? concatResult : (fthCustomBakValue + '\u001e' + concatResult));
                        Settings.SaveStr("FthCustomBak", concatResultOffset);
                        if (Settings.LoadStr("FthCustomBak", "") != concatResultOffset)
                        {
                            return false;
                        }
                    }
                    registryKey.DeleteValue(shim.ValueName, false);
                    return registryKey.GetValue(shim.ValueName) == null;
                }
            }
            catch
            {
                return false;
            }
        }

        private static bool RestorePointers()
        {
            string fthCustomBakValue = Settings.LoadStr("FthCustomBak", "");
            if (fthCustomBakValue.Length == 0)
            {
                return true;
            }
            bool restorePointersSucceeded = true;
            foreach (string entryText in fthCustomBakValue.Split('\u001e'))
            {
                if (entryText.Length == 0)
                {
                    continue;
                }
                string[] entryTextParts = entryText.Split('\u001f');
                if (entryTextParts.Length < 4)
                {
                    restorePointersSucceeded = false;
                    continue;
                }
                int result;
                if (!int.TryParse(entryTextParts[2], out result))
                {
                    restorePointersSucceeded = false;
                    continue;
                }
                if (!Enum.IsDefined(typeof(RegistryValueKind), result) || result == 0)
                {
                    restorePointersSucceeded = false;
                    continue;
                }
                object decodeValueResult = DecodeValue(entryTextParts[3], (RegistryValueKind)result);
                if (decodeValueResult == null)
                {
                    restorePointersSucceeded = false;
                    continue;
                }
                try
                {
                    using (RegistryKey registryKey = Registry.LocalMachine.CreateSubKey("SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\AppCompatFlags\\Custom\\" + entryTextParts[0]))
                    {
                        if (registryKey == null)
                        {
                            restorePointersSucceeded = false;
                            continue;
                        }
                        registryKey.SetValue(entryTextParts[1], decodeValueResult, (RegistryValueKind)result);
                        if (registryKey.GetValue(entryTextParts[1]) == null)
                        {
                            restorePointersSucceeded = false;
                        }
                    }
                }
                catch
                {
                    restorePointersSucceeded = false;
                }
            }
            if (restorePointersSucceeded)
            {
                Settings.Remove("FthCustomBak");
            }
            return restorePointersSucceeded;
        }

        internal static string EncodeValue(object value)
        {
            if (value is byte[])
            {
                return "b" + Convert.ToBase64String((byte[])value);
            }
            if (value is string[])
            {
                return "m" + EncodeList((string[])value);
            }
            return "s" + Convert.ToBase64String(Encoding.UTF8.GetBytes(Convert.ToString(value, CultureInfo.InvariantCulture)));
        }

        internal static object DecodeValue(string repr, RegistryValueKind kind)
        {
            try
            {
                if (repr.Length == 0)
                {
                    return null;
                }
                if (repr[0] == 'b')
                {
                    return (kind == RegistryValueKind.Binary) ? Convert.FromBase64String(repr.Substring(1)) : null;
                }
                if (repr[0] == 'm')
                {
                    return (kind == RegistryValueKind.MultiString) ? DecodeList(repr.Substring(1)) : null;
                }
                if (repr[0] != 's')
                {
                    return null;
                }
                if (kind != RegistryValueKind.String && kind != RegistryValueKind.ExpandString && kind != RegistryValueKind.DWord && kind != RegistryValueKind.QWord)
                {
                    return null;
                }
                string @string = Encoding.UTF8.GetString(Convert.FromBase64String(repr.Substring(1)));
                switch (kind)
                {
                case RegistryValueKind.DWord:
                {
                    int result2;
                    return int.TryParse(@string, NumberStyles.Integer, CultureInfo.InvariantCulture, out result2) ? ((object)result2) : null;
                }
                case RegistryValueKind.QWord:
                {
                    long result;
                    return long.TryParse(@string, NumberStyles.Integer, CultureInfo.InvariantCulture, out result) ? ((object)result) : null;
                }
                default:
                    return @string;
                }
            }
            catch
            {
                return null;
            }
        }

        internal static string EncodeList(string[] list)
        {
            StringBuilder stringBuilder = new StringBuilder();
            for (int i = 0; i < list.Length; i++)
            {
                if (i > 0)
                {
                    stringBuilder.Append('\0');
                }
                stringBuilder.Append(list[i]);
            }
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(stringBuilder.ToString()));
        }

        internal static string[] DecodeList(string encoded)
        {
            try
            {
                string @string = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
                string[] result;
                if (@string.Length != 0)
                {
                    char[] separator = new char[1];
                    result = @string.Split(separator);
                }
                else
                {
                    result = new string[0];
                }
                return result;
            }
            catch
            {
                return null;
            }
        }

        private static bool ListsEqual(string[] a, string[] b)
        {
            if (a == null || b == null)
            {
                return a == b;
            }
            if (a.Length != b.Length)
            {
                return false;
            }
            for (int i = 0; i < a.Length; i++)
            {
                if (!string.Equals(a[i], b[i], StringComparison.Ordinal))
                {
                    return false;
                }
            }
            return true;
        }

        internal static bool HasBackupRecord(string stored, string exe, string valueName)
        {
            if (string.IsNullOrEmpty(stored))
            {
                return false;
            }
            foreach (string entryText in stored.Split('\u001e'))
            {
                if (entryText.Length != 0)
                {
                    string[] entryTextParts = entryText.Split('\u001f');
                    if (entryTextParts.Length >= 2 && string.Equals(entryTextParts[0], exe, StringComparison.OrdinalIgnoreCase) && string.Equals(entryTextParts[1], valueName, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private static bool HasName(IEnumerable<string> list, string value)
        {
            if (list == null)
            {
                return false;
            }
            foreach (string item in list)
            {
                if (string.Equals(item, value, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }
    }
}
