using System;
using Microsoft.Win32;

namespace PaviseApp
{
    internal sealed class ReversibleReg
    {
        public const string Absent = "__pavise_absent__";

        private readonly RegistryKey hive;

        private readonly string subKey;

        private readonly string valName;

        private readonly RegistryValueKind kind;

        private readonly string slot;

        public bool HasBackup
        {
            get
            {
                return Settings.LoadStr(slot, "").Length > 0;
            }
        }

        public ReversibleReg(RegistryKey hive, string subKey, string valName, RegistryValueKind kind, string slot)
        {
            this.hive = hive;
            this.subKey = subKey;
            this.valName = valName;
            this.kind = kind;
            this.slot = slot;
        }

        public bool Apply(object newVal)
        {
            try
            {
                using (RegistryKey registryKey = hive.CreateSubKey(subKey))
                {
                    if (registryKey == null)
                    {
                        return false;
                    }
                    string originalPartResult = OriginalPart(Settings.LoadStr(slot, ""));
                    if (originalPartResult.Length == 0)
                    {
                        object instance = registryKey.GetValue(valName);
                        if (instance != null)
                        {
                            RegistryValueKind registryValueKind = RegistryValueKind.Unknown;
                            try
                            {
                                registryValueKind = registryKey.GetValueKind(valName);
                            }
                            catch
                            {
                            }
                            if (registryValueKind != kind)
                            {
                                Logger.Warn(string.Concat(valName, Lang.T("log.reversiblereg.1", 0), registryValueKind, Lang.T("log.reversiblereg.2", 0)));
                                return false;
                            }
                            if (SameByKind(instance, newVal))
                            {
                                return true;
                            }
                        }
                        originalPartResult = Encode(instance);
                        Settings.SaveStr(slot, originalPartResult);
                        if (Settings.LoadStr(slot, "") != originalPartResult)
                        {
                            Logger.Log(Lang.T("log.reversiblereg.3", 0) + valName + Lang.T("log.reversiblereg.4", 0));
                            return false;
                        }
                    }
                    registryKey.SetValue(valName, newVal, kind);
                    object currentInstance = registryKey.GetValue(valName);
                    if (currentInstance == null)
                    {
                        return false;
                    }
                    bool sameByKindSucceeded = SameByKind(currentInstance, newVal);
                    if (sameByKindSucceeded)
                    {
                        Settings.SaveStr(slot, originalPartResult + '\u001f' + Encode(newVal));
                    }
                    return sameByKindSucceeded;
                }
            }
            catch
            {
                return false;
            }
        }

        private string Encode(object value)
        {
            if (value == null)
            {
                return "__pavise_absent__";
            }
            if (kind == RegistryValueKind.Binary)
            {
                return "b" + Convert.ToBase64String((byte[])value);
            }
            return "=" + value;
        }

        private static string OriginalPart(string stored)
        {
            int matchIndex = stored.LastIndexOf('\u001f');
            if (matchIndex >= 0)
            {
                return stored.Substring(0, matchIndex);
            }
            return stored;
        }

        private static string AppliedPart(string stored)
        {
            int matchIndex = stored.LastIndexOf('\u001f');
            if (matchIndex >= 0)
            {
                return stored.Substring(matchIndex + 1);
            }
            return "";
        }

        private bool TryDecode(string repr, out object value)
        {
            value = null;
            try
            {
                if (repr.Length == 0 || repr == "__pavise_absent__")
                {
                    return false;
                }
                if (kind == RegistryValueKind.Binary)
                {
                    if (repr[0] != 'b')
                    {
                        return false;
                    }
                    value = Convert.FromBase64String(repr.Substring(1));
                    return true;
                }
                string substringResult = ((repr[0] == '=') ? repr.Substring(1) : repr);
                if (kind == RegistryValueKind.DWord)
                {
                    long result;
                    if (!long.TryParse(substringResult, out result))
                    {
                        return false;
                    }
                    value = (int)result;
                    return true;
                }
                value = substringResult;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private bool SameByKind(object a, object b)
        {
            try
            {
                if (kind == RegistryValueKind.DWord)
                {
                    return Convert.ToInt64(a) == Convert.ToInt64(b);
                }
                if (kind == RegistryValueKind.Binary)
                {
                    return BytesEqual(a as byte[], b as byte[]);
                }
                return string.Equals(a.ToString(), (b == null) ? "" : b.ToString(), StringComparison.Ordinal);
            }
            catch
            {
                return false;
            }
        }

        private static bool BytesEqual(byte[] a, byte[] b)
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
                if (a[i] != b[i])
                {
                    return false;
                }
            }
            return true;
        }

        public bool Matches(object expected)
        {
            try
            {
                using (RegistryKey registryKey = hive.OpenSubKey(subKey))
                {
                    object instance = ((registryKey == null) ? null : registryKey.GetValue(valName));
                    if (instance == null)
                    {
                        return false;
                    }
                    if (kind == RegistryValueKind.DWord)
                    {
                        return Convert.ToInt64(instance) == Convert.ToInt64(expected);
                    }
                    if (kind == RegistryValueKind.Binary)
                    {
                        return BytesEqual(instance as byte[], expected as byte[]);
                    }
                    return string.Equals(instance.ToString(), (expected == null) ? "" : expected.ToString(), StringComparison.Ordinal);
                }
            }
            catch
            {
                return false;
            }
        }

        public bool Restore()
        {
            string str = Settings.LoadStr(slot, "");
            if (str.Length == 0)
            {
                return true;
            }
            string originalPartResult = OriginalPart(str);
            string appliedPartResult = AppliedPart(str);
            bool originalAbsent = originalPartResult == "__pavise_absent__";
            object fromBase64StringResult = null;
            if (!originalAbsent)
            {
                if (kind == RegistryValueKind.Binary)
                {
                    if (originalPartResult.Length < 1 || originalPartResult[0] != 'b')
                    {
                        Settings.SaveStr(slot, "");
                        Logger.Log(Lang.T("log.reversiblereg.5", 0) + valName + Lang.T("log.reversiblereg.6", 0));
                        return false;
                    }
                    try
                    {
                        fromBase64StringResult = Convert.FromBase64String(originalPartResult.Substring(1));
                    }
                    catch
                    {
                        Settings.SaveStr(slot, "");
                        Logger.Log(Lang.T("log.reversiblereg.5", 0) + valName + Lang.T("log.reversiblereg.7", 0));
                        return false;
                    }
                }
                else
                {
                    string substringResult = ((originalPartResult[0] == '=') ? originalPartResult.Substring(1) : originalPartResult);
                    if (kind == RegistryValueKind.DWord)
                    {
                        long result;
                        if (!long.TryParse(substringResult, out result))
                        {
                            Settings.SaveStr(slot, "");
                            Logger.Log(Lang.T("log.reversiblereg.5", 0) + valName + Lang.T("log.reversiblereg.8", 0) + substringResult + "\" ");
                            return false;
                        }
                        fromBase64StringResult = (int)result;
                    }
                    else
                    {
                        fromBase64StringResult = substringResult;
                    }
                }
            }
            try
            {
                bool readbackMatches = false;
                using (RegistryKey registryKey = hive.OpenSubKey(subKey, true))
                {
                    object instance;
                    if (registryKey != null && appliedPartResult.Length > 0 && TryDecode(appliedPartResult, out instance))
                    {
                        object currentInstance = registryKey.GetValue(valName);
                        if (currentInstance == null || !SameByKind(currentInstance, instance))
                        {
                            Settings.SaveStr(slot, "");
                            Logger.Log(valName + Lang.T("log.reversiblereg.9", 0));
                            return Settings.LoadStr(slot, "").Length == 0;
                        }
                    }
                    if (registryKey == null)
                    {
                        readbackMatches = true;
                    }
                    else if (originalAbsent)
                    {
                        if (registryKey.GetValue(valName) != null)
                        {
                            registryKey.DeleteValue(valName, false);
                        }
                        readbackMatches = registryKey.GetValue(valName) == null;
                    }
                    else
                    {
                        registryKey.SetValue(valName, fromBase64StringResult, kind);
                        object candidateInstance = registryKey.GetValue(valName);
                        RegistryValueKind registryValueKind = RegistryValueKind.Unknown;
                        try
                        {
                            registryValueKind = registryKey.GetValueKind(valName);
                        }
                        catch
                        {
                        }
                        readbackMatches = candidateInstance != null && registryValueKind == kind;
                        if (readbackMatches && kind == RegistryValueKind.DWord)
                        {
                            readbackMatches = Convert.ToInt64(candidateInstance) == Convert.ToInt64(fromBase64StringResult);
                        }
                        else if (readbackMatches && kind == RegistryValueKind.Binary)
                        {
                            readbackMatches = BytesEqual((byte[])candidateInstance, (byte[])fromBase64StringResult);
                        }
                        else if (readbackMatches)
                        {
                            readbackMatches = string.Equals(candidateInstance.ToString(), (fromBase64StringResult == null) ? "" : fromBase64StringResult.ToString(), StringComparison.Ordinal);
                        }
                    }
                }
                if (!readbackMatches)
                {
                    Logger.Log(Lang.T("log.suppressioncore.1", 0) + valName + Lang.T("log.reversiblereg.10", 0));
                    return false;
                }
                Settings.SaveStr(slot, "");
                return Settings.LoadStr(slot, "").Length == 0;
            }
            catch
            {
                Logger.Warn(Lang.T("log.suppressioncore.1", 0) + valName + Lang.T("log.reversiblereg.11", 0));
                return false;
            }
        }
    }
}
