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
                foreach (GpuAdapter gpuAdapter in facts.Gpus)
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
                    object instance = key == null ? null : key.GetValue("ThreadDpcEnable");
                    facts.ThreadDpc = instance is int ? (int?)instance : null;
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
                    foreach (ManagementBaseObject currentManagementObjectCollection in managementObjectCollection)
                    {
                        using (currentManagementObjectCollection)
                        {
                            f.MemModules++;
                            int memRatedMhzValue = ToMhz(currentManagementObjectCollection["Speed"]);
                            int memConfiguredMhzValue = ToMhz(currentManagementObjectCollection["ConfiguredClockSpeed"]);
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
                                object instance = currentManagementObjectCollection["Capacity"];
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
            List<string> messages = new List<string>();
            foreach (Process process in Process.GetProcesses())
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
                    if (message != null && !messages.Contains(message))
                    {
                        messages.Add(message);
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
            return messages;
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
            List<BypassIoVerdict> bypassIoVerdicts = new List<BypassIoVerdict>();
            if (Native.OsBuild() > 0 && Native.OsBuild() < 22000)
            {
                return bypassIoVerdicts;
            }
            List<string> fileNames = new List<string>();
            try
            {
                Func<List<string>> libraryPaths = LibraryPaths;
                List<string> texts = ((libraryPaths != null) ? libraryPaths() : null);
                if (texts != null)
                {
                    fileNames.AddRange(texts);
                }
            }
            catch
            {
            }
            try
            {
                using (Process process = Process.GetCurrentProcess())
                {
                    fileNames.Add(process.MainModule.FileName);
                }
            }
            catch
            {
            }
            HashSet<string> pathRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string fileName in fileNames)
            {
                if (string.IsNullOrEmpty(fileName))
                {
                    continue;
                }
                string pathRoot;
                try
                {
                    pathRoot = Path.GetPathRoot(fileName);
                }
                catch
                {
                    continue;
                }
                if (string.IsNullOrEmpty(pathRoot) || !pathRoots.Add(pathRoot))
                {
                    continue;
                }
                try
                {
                    if (!File.Exists(fileName))
                    {
                        continue;
                    }
                }
                catch
                {
                    continue;
                }
                BypassIoVerdict bypassIoVerdict = BypassIoProbe.Query(fileName);
                if (bypassIoVerdict.Supported)
                {
                    bypassIoVerdicts.Add(bypassIoVerdict);
                }
                if (bypassIoVerdicts.Count >= 4)
                {
                    break;
                }
            }
            return bypassIoVerdicts;
        }

        public static List<LoadEntry> TopConsumers(int windowMs, int take)
        {
            List<LoadEntry> loadEntries = new List<LoadEntry>();
            try
            {
                Dictionary<int, Sample> samplesById = new Dictionary<int, Sample>();
                foreach (Process process in Process.GetProcesses())
                {
                    using (process)
                    {
                        try
                        {
                            if (process.Id > 4)
                            {
                                samplesById[process.Id] = new Sample
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
                if (samplesById.Count == 0)
                {
                    return loadEntries;
                }
                Thread.Sleep(windowMs);
                double scaledProcessorCount = (double)windowMs / 1000.0 * (double)Environment.ProcessorCount;
                if (scaledProcessorCount <= 0.0)
                {
                    return loadEntries;
                }
                foreach (Process currentProcess in Process.GetProcesses())
                {
                    using (currentProcess)
                    {
                        try
                        {
                            Sample sample;
                            if (samplesById.TryGetValue(currentProcess.Id, out sample) && currentProcess.StartTime.Ticks == sample.Started)
                            {
                                double totalSeconds = (currentProcess.TotalProcessorTime - sample.Cpu).TotalSeconds;
                                if (!(totalSeconds <= 0.0))
                                {
                                    loadEntries.Add(new LoadEntry
                                    {
                                        Name = sample.Name,
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
                return loadEntries;
            }
            loadEntries.Sort((LoadEntry a, LoadEntry b) => b.Ratio.CompareTo(a.Ratio));
            if (loadEntries.Count > take)
            {
                loadEntries.RemoveRange(take, loadEntries.Count - take);
            }
            return loadEntries;
        }

    }
}
