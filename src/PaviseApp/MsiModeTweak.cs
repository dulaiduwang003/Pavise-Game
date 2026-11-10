using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static class MsiModeTweak
    {
        internal struct Candidate
        {
            public string InstanceId;

            public string Description;

            public bool HasKey;

            public int? Value;
        }

        private static readonly object lk = new object();

        public static bool EnabledByPavise
        {
            get
            {
                return Settings.Load("MsiOnByPavise", false);
            }
        }

        public static List<Candidate> Scan()
        {
            List<Candidate> candidates = new List<Candidate>();
            HashSet<string> textsSet;
            try
            {
                textsSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string byClas in PresentDevices.ByClass(new Guid("4d36e968-e325-11ce-bfc1-08002be10318")))
                {
                    if (!string.IsNullOrEmpty(byClas))
                    {
                        textsSet.Add(byClas);
                    }
                }
            }
            catch
            {
                return candidates;
            }
            if (textsSet.Count == 0)
            {
                return candidates;
            }
            try
            {
                using (RegistryKey registryKey = Registry.LocalMachine.OpenSubKey("SYSTEM\\CurrentControlSet\\Enum\\PCI"))
                {
                    if (registryKey == null)
                    {
                        return candidates;
                    }
                    foreach (string entryKey in registryKey.GetSubKeyNames())
                    {
                        using (RegistryKey currentRegistryKey = registryKey.OpenSubKey(entryKey))
                        {
                            if (currentRegistryKey == null)
                            {
                                continue;
                            }
                            foreach (string entryKey2 in currentRegistryKey.GetSubKeyNames())
                            {
                                using (RegistryKey candidateRegistryKey = currentRegistryKey.OpenSubKey(entryKey2))
                                {
                                    if (candidateRegistryKey == null)
                                    {
                                        continue;
                                    }
                                    string a = candidateRegistryKey.GetValue("Class") as string;
                                    if (!string.Equals(a, "Display", StringComparison.OrdinalIgnoreCase))
                                    {
                                        continue;
                                    }
                                    string instanceIdValue = "PCI\\" + entryKey + "\\" + entryKey2;
                                    if (textsSet.Contains(instanceIdValue))
                                    {
                                        Candidate currentCandidate = default(Candidate);
                                        currentCandidate.InstanceId = instanceIdValue;
                                        currentCandidate.Description = (candidateRegistryKey.GetValue("DeviceDesc") as string) ?? instanceIdValue;
                                        int matchIndex = currentCandidate.Description.LastIndexOf(';');
                                        if (matchIndex >= 0)
                                        {
                                            currentCandidate.Description = currentCandidate.Description.Substring(matchIndex + 1);
                                        }
                                        using (RegistryKey resolvedRegistryKey = candidateRegistryKey.OpenSubKey("Device Parameters\\Interrupt Management\\MessageSignaledInterruptProperties"))
                                        {
                                            currentCandidate.HasKey = resolvedRegistryKey != null;
                                            object instance = ((resolvedRegistryKey == null) ? null : resolvedRegistryKey.GetValue("MSISupported"));
                                            currentCandidate.Value = ((instance is int) ? new int?((int)instance) : null);
                                        }
                                        candidates.Add(currentCandidate);
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
            return candidates;
        }

        public static List<Candidate> Disabled()
        {
            List<Candidate> candidates = new List<Candidate>();
            foreach (Candidate scan in Scan())
            {
                if (!scan.HasKey)
                {
                    continue;
                }
                int? nullable = scan.Value;
                if (nullable.HasValue)
                {
                    int? currentNullable = scan.Value;
                    if (currentNullable.Value == 0)
                    {
                        candidates.Add(scan);
                    }
                }
            }
            return candidates;
        }

        public static bool Enable()
        {
            lock (lk)
            {
                List<Candidate> candidates = Disabled();
                if (candidates.Count == 0)
                {
                    Logger.Log(Lang.T("log.msimodetweak.1", 0));
                    return true;
                }
                List<string> instanceIds = new List<string>();
                foreach (Candidate candidate in candidates)
                {
                    if (Reg(candidate.InstanceId).Apply(1) || Reg(candidate.InstanceId).HasBackup)
                    {
                        instanceIds.Add(candidate.InstanceId);
                    }
                    else
                    {
                        Logger.Log(Lang.T("log.msimodetweak.2", 0) + candidate.Description);
                    }
                }
                if (instanceIds.Count == 0)
                {
                    return false;
                }
                if (!Settings.SaveStr("MsiList", string.Join(";", instanceIds.ToArray())))
                {
                    foreach (string instanceId in instanceIds)
                    {
                        Reg(instanceId).Restore();
                    }
                    Logger.Log(Lang.T("log.msimodetweak.3", 0));
                    return false;
                }
                Settings.Save("MsiOnByPavise", true);
                Logger.Log(Lang.T("log.msimodetweak.4", 0) + instanceIds.Count + Lang.T("log.msimodetweak.5", 0));
                return true;
            }
        }

        public static bool HasResidue()
        {
            if (!Settings.Load("MsiOnByPavise", false))
            {
                return ParseList(Settings.LoadStr("MsiList", "")).Length > 0;
            }
            return true;
        }

        public static bool Restore()
        {
            lock (lk)
            {
                bool restoreSucceeded = true;
                foreach (string instanceId in ParseList(Settings.LoadStr("MsiList", "")))
                {
                    restoreSucceeded &= Reg(instanceId).Restore();
                }
                if (restoreSucceeded)
                {
                    Settings.SaveStr("MsiList", "");
                    Settings.Save("MsiOnByPavise", false);
                }
                return restoreSucceeded;
            }
        }

        private static ReversibleReg Reg(string instanceId)
        {
            return new ReversibleReg(Registry.LocalMachine, "SYSTEM\\CurrentControlSet\\Enum\\" + instanceId + "\\Device Parameters\\Interrupt Management\\MessageSignaledInterruptProperties", "MSISupported", RegistryValueKind.DWord, "Msi_" + instanceId.Replace('\\', '_'));
        }

        internal static string[] ParseList(string raw)
        {
            return (raw ?? "").Split(new char[1] { ';' }, StringSplitOptions.RemoveEmptyEntries);
        }
    }
}
