using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static partial class SystemAudit
    {

        private sealed class Facts
        {
            public bool Nv;

            public GpuAdapter[] Gpus = new GpuAdapter[0];

            public bool NvHardware;

            public bool IntegratedOnly;

            public bool Partition;

            public bool Eco;

            public bool EcoFull;

            public bool Hags;

            public bool HagsKnown;

            public bool InstalledMemoryKnown;

            public bool MemoryCompressionKnown;

            public bool CompressionOn;

            public bool CombiningOn;

            public ulong InstalledMemoryKb;

            public bool MpoOff;

            public bool Dvr;

            public bool SuppressOn;

            public string Plan = Lang.T("t.systemaudithardware.3");

            public bool MemOk;

            public double UsedRatio;

            public double TotalGb;

            public double AvailGb;

            public bool PageOk;

            public double PageFileGb;

            public string Link;

            public List<InputDevice> Inputs;

            public AccessibilityState Access;

            public int? ThreadDpc;

            public int OsBuild;

            public bool RyzenCpu;

            public int MemModules;

            public int MemConfiguredMhz;

            public int MemRatedMhz;

            public List<int> AllHz;

            public List<string> RgbSuites;

            public int ThrottleEvents7d;

            public int MsiOffCount;

            public List<double> MemModuleGb;

            public List<BypassIoVerdict> BypassIo;
        }

        public sealed class LoadEntry
        {
            public string Name;

            public double Ratio;
        }

        private sealed class Sample
        {
            public string Name;

            public long Started;

            public TimeSpan Cpu;
        }

        private static Facts Gather()
        {
            Facts facts = new Facts();
            try
            {
                facts.OsBuild = Native.OsBuild();
            }
            catch
            {
            }
            try
            {
                using (RegistryKey registryKey = Registry.LocalMachine.OpenSubKey("HARDWARE\\DESCRIPTION\\System\\CentralProcessor\\0"))
                {
                    string getValueOffset = ((registryKey == null) ? null : (registryKey.GetValue("ProcessorNameString") as string));
                    facts.RyzenCpu = getValueOffset != null && getValueOffset.IndexOf("AMD Ryzen", StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch
            {
            }
            try
            {
                GatherMemoryModules(facts);
            }
            catch
            {
            }
            try
            {
                facts.BypassIo = GatherBypassIo();
            }
            catch
            {
            }
            try
            {
                facts.AllHz = DisplayGuard.AllRefreshRates();
            }
            catch
            {
            }
            try
            {
                facts.RgbSuites = ScanRgbSuites();
            }
            catch
            {
            }
            try
            {
                facts.ThrottleEvents7d = CountCpuThrottleEvents();
            }
            catch
            {
            }
            try
            {
                facts.MsiOffCount = MsiModeTweak.Disabled().Count;
            }
            catch
            {
            }
            try
            {
                facts.Nv = NvApi.Available;
            }
            catch
            {
            }
            try
            {
                facts.Gpus = GpuInventory.Adapters();
                GpuAdapter[] gpus = facts.Gpus;
                foreach (GpuAdapter gpuAdapter in gpus)
                {
                    if (gpuAdapter.Vendor == GpuVendor.Nvidia)
                    {
                        facts.NvHardware = true;
                    }
                }
                facts.IntegratedOnly = GpuInventory.IntegratedOnly;
            }
            catch
            {
            }
            try
            {
                facts.Partition = CpuTopology.HasSafeBackgroundPartition();
            }
            catch
            {
            }
            try
            {
                facts.Eco = Native.PowerThrottlingSupported;
            }
            catch
            {
            }
            facts.EcoFull = facts.Eco && WindowsBuild() >= 22000;
            try
            {
                bool supported;
                facts.HagsKnown = HagsTweak.TryQueryState(out supported, out facts.Hags);
            }
            catch
            {
                facts.HagsKnown = false;
            }
            try
            {
                facts.InstalledMemoryKnown = MemCompressTweak.TryInstalledMemory(out facts.InstalledMemoryKb);
            }
            catch
            {
            }
            try
            {
                facts.MemoryCompressionKnown = MemCompressTweak.QueryState(out facts.CompressionOn, out facts.CombiningOn);
            }
            catch
            {
            }
            try
            {
                facts.MpoOff = MpoTweak.CurrentlyDisabled();
            }
            catch
            {
            }
            facts.Dvr = GameDvrOn();
            try
            {
                facts.SuppressOn = Settings.Load("GmSuppress", true);
            }
            catch
            {
                facts.SuppressOn = true;
            }
            try
            {
                facts.Plan = PowerPlan.CurrentPlanLabel();
            }
            catch
            {
            }
            facts.MemOk = TryMemory(out facts.UsedRatio, out facts.TotalGb, out facts.AvailGb);
            facts.PageOk = TryPageFile(out facts.PageFileGb);
            try
            {
                facts.Link = LinkKind();
            }
            catch
            {
            }
            try
            {
                facts.Inputs = InputChainProbe.Devices();
            }
            catch
            {
            }
            try
            {
                facts.Access = InputChainProbe.ReadAccessibility();
            }
            catch
            {
            }
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey("SYSTEM\\CurrentControlSet\\Control\\Session Manager\\kernel"))
                {
                    object value = key == null ? null : key.GetValue("ThreadDpcEnable");
                    facts.ThreadDpc = value is int ? (int?)value : null;
                }
            }
            catch
            {
            }
            return facts;
        }

        public static bool TryPageFile(out double pageFileGb)
        {
            pageFileGb = 0.0;
            try
            {
                MemoryStatusEx buffer = default(MemoryStatusEx);
                buffer.Length = (uint)Marshal.SizeOf(typeof(MemoryStatusEx));
                if (!GlobalMemoryStatusEx(ref buffer) || buffer.TotalPhys == 0)
                {
                    return false;
                }
                double totalPageFileRatio = (double)buffer.TotalPageFile / 1073741824.0;
                double totalPhysRatio = (double)buffer.TotalPhys / 1073741824.0;
                pageFileGb = Math.Max(0.0, totalPageFileRatio - totalPhysRatio);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void GatherMemoryModules(Facts f)
        {
            using (ManagementObjectSearcher managementObjectSearcher = new ManagementObjectSearcher("SELECT Speed, ConfiguredClockSpeed, Capacity FROM Win32_PhysicalMemory"))
            {
                managementObjectSearcher.Options.Timeout = TimeSpan.FromSeconds(10.0);
                f.MemModuleGb = new List<double>();
                using (ManagementObjectCollection managementObjectCollection = managementObjectSearcher.Get())
                {
                    foreach (ManagementBaseObject item in managementObjectCollection)
                    {
                        using (item)
                        {
                            f.MemModules++;
                            int memRatedMhzValue = ToMhz(item["Speed"]);
                            int memConfiguredMhzValue = ToMhz(item["ConfiguredClockSpeed"]);
                            if (memRatedMhzValue > f.MemRatedMhz)
                            {
                                f.MemRatedMhz = memRatedMhzValue;
                            }
                            if (memConfiguredMhzValue > f.MemConfiguredMhz)
                            {
                                f.MemConfiguredMhz = memConfiguredMhzValue;
                            }
                            try
                            {
                                object instance = item["Capacity"];
                                if (instance != null)
                                {
                                    f.MemModuleGb.Add((double)Convert.ToUInt64(instance) / 1073741824.0);
                                }
                            }
                            catch
                            {
                            }
                        }
                    }
                }
            }
        }

        private static List<string> ScanRgbSuites()
        {
            List<string> list = new List<string>();
            Process[] processes = Process.GetProcesses();
            foreach (Process process in processes)
            {
                try
                {
                    string toLowerInvariantResult = process.ProcessName.ToLowerInvariant();
                    string message = null;
                    if (toLowerInvariantResult.Contains("icue"))
                    {
                        message = "Corsair iCUE";
                    }
                    else if (toLowerInvariantResult.Contains("lightingservice"))
                    {
                        message = "ASUS Aura";
                    }
                    else if (toLowerInvariantResult.Contains("armourycrate") || toLowerInvariantResult.Contains("armouryswagent"))
                    {
                        message = "Armoury Crate";
                    }
                    else if (toLowerInvariantResult.Contains("razerappengine") || toLowerInvariantResult.Contains("rzsynapse") || toLowerInvariantResult.Contains("razer synapse"))
                    {
                        message = "Razer Synapse";
                    }
                    else if (toLowerInvariantResult.Contains("mysticlight"))
                    {
                        message = "MSI Mystic Light";
                    }
                    else if (toLowerInvariantResult.Contains("signalrgb"))
                    {
                        message = "SignalRGB";
                    }
                    if (message != null && !list.Contains(message))
                    {
                        list.Add(message);
                    }
                }
                catch
                {
                }
                finally
                {
                    try
                    {
                        process.Dispose();
                    }
                    catch
                    {
                    }
                }
            }
            return list;
        }

        private static int CountCpuThrottleEvents()
        {
            string query = "*[System[Provider[@Name='Microsoft-Windows-Kernel-Processor-Power'] and (EventID=37) and TimeCreated[timediff(@SystemTime) <= 604800000]]]";
            EventLogQuery eventQuery = new EventLogQuery("System", PathType.LogName, query);
            int processedCount = 0;
            using (EventLogReader eventLogReader = new EventLogReader(eventQuery))
            {
                EventRecord eventRecord = eventLogReader.ReadEvent();
                while (eventRecord != null && processedCount < 200)
                {
                    using (eventRecord)
                    {
                        processedCount++;
                    }
                    eventRecord = eventLogReader.ReadEvent();
                }
            }
            return processedCount;
        }

        private static List<BypassIoVerdict> GatherBypassIo()
        {
            List<BypassIoVerdict> list = new List<BypassIoVerdict>();
            if (Native.OsBuild() > 0 && Native.OsBuild() < 22000)
            {
                return list;
            }
            List<string> list2 = new List<string>();
            try
            {
                Func<List<string>> libraryPaths = LibraryPaths;
                List<string> list3 = ((libraryPaths != null) ? libraryPaths() : null);
                if (list3 != null)
                {
                    list2.AddRange(list3);
                }
            }
            catch
            {
            }
            try
            {
                using (Process process = Process.GetCurrentProcess())
                {
                    list2.Add(process.MainModule.FileName);
                }
            }
            catch
            {
            }
            HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string item in list2)
            {
                if (string.IsNullOrEmpty(item))
                {
                    continue;
                }
                string pathRoot;
                try
                {
                    pathRoot = Path.GetPathRoot(item);
                }
                catch
                {
                    continue;
                }
                if (string.IsNullOrEmpty(pathRoot) || !hashSet.Add(pathRoot))
                {
                    continue;
                }
                try
                {
                    if (!File.Exists(item))
                    {
                        continue;
                    }
                }
                catch
                {
                    continue;
                }
                BypassIoVerdict bypassIoVerdict = BypassIoProbe.Query(item);
                if (bypassIoVerdict.Supported)
                {
                    list.Add(bypassIoVerdict);
                }
                if (list.Count >= 4)
                {
                    break;
                }
            }
            return list;
        }

        public static List<LoadEntry> TopConsumers(int windowMs, int take)
        {
            List<LoadEntry> list = new List<LoadEntry>();
            try
            {
                Dictionary<int, Sample> dictionary = new Dictionary<int, Sample>();
                Process[] processes = Process.GetProcesses();
                foreach (Process process in processes)
                {
                    using (process)
                    {
                        try
                        {
                            if (process.Id > 4)
                            {
                                dictionary[process.Id] = new Sample
                                {
                                    Name = process.ProcessName,
                                    Started = process.StartTime.Ticks,
                                    Cpu = process.TotalProcessorTime
                                };
                            }
                        }
                        catch
                        {
                        }
                    }
                }
                if (dictionary.Count == 0)
                {
                    return list;
                }
                Thread.Sleep(windowMs);
                double scaledProcessorCount = (double)windowMs / 1000.0 * (double)Environment.ProcessorCount;
                if (scaledProcessorCount <= 0.0)
                {
                    return list;
                }
                Process[] processes2 = Process.GetProcesses();
                foreach (Process process3 in processes2)
                {
                    using (process3)
                    {
                        try
                        {
                            Sample value;
                            if (dictionary.TryGetValue(process3.Id, out value) && process3.StartTime.Ticks == value.Started)
                            {
                                double totalSeconds = (process3.TotalProcessorTime - value.Cpu).TotalSeconds;
                                if (!(totalSeconds <= 0.0))
                                {
                                    list.Add(new LoadEntry
                                    {
                                        Name = value.Name,
                                        Ratio = totalSeconds / scaledProcessorCount
                                    });
                                }
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
                return list;
            }
            list.Sort((LoadEntry a, LoadEntry b) => b.Ratio.CompareTo(a.Ratio));
            if (list.Count > take)
            {
                list.RemoveRange(take, list.Count - take);
            }
            return list;
        }

    }
}
