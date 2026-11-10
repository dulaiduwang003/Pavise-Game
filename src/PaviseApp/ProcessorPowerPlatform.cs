using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Xml;

namespace PaviseApp
{
    internal static class ProcessorPowerPlatform
    {
        internal enum Interface
        {
            Unknown,
            AcpiPState,
            Cppc
        }

        private static readonly Lazy<Interface> detected = new Lazy<Interface>(ReadInterface);

        internal static Interface Current
        {
            get
            {
                return detected.Value;
            }
        }

        [DllImport("kernel32.dll")]
        private static extern uint GetActiveProcessorCount(ushort groupNumber);

        internal static bool? AutonomousMinimum(Interface platform, uint? requested)
        {
            switch (platform)
            {
            case Interface.AcpiPState:
                return false;
            case Interface.Cppc:
                if (requested == 1)
                {
                    return true;
                }
                break;
            }
            return null;
        }

        internal static bool TryResolveMinimumIndices(bool? acMode, bool? dcMode, uint acCandidate, uint dcCandidate, Func<bool, uint?> readExisting, out uint ac, out uint dc)
        {
            ac = acCandidate;
            dc = dcCandidate;
            uint? nullable = (acMode.HasValue ? new uint?(acCandidate) : readExisting(true));
            uint? currentNullable = (dcMode.HasValue ? new uint?(dcCandidate) : readExisting(false));
            if (!nullable.HasValue || !currentNullable.HasValue || nullable.Value > 100 || currentNullable.Value > 100)
            {
                return false;
            }
            ac = nullable.Value;
            dc = currentNullable.Value;
            return true;
        }

        internal static bool TryReadCapability(string xml, out string processor, out uint implementation)
        {
            processor = null;
            implementation = 0u;
            try
            {
                XmlDocument currentXmlDocument = new XmlDocument();
                currentXmlDocument.XmlResolver = null;
                currentXmlDocument.LoadXml(xml);
                XmlNamespaceManager xmlNamespaceManager = new XmlNamespaceManager(currentXmlDocument.NameTable);
                xmlNamespaceManager.AddNamespace("e", "http://schemas.microsoft.com/win/2004/08/events/event");
                XmlNode xmlNode = currentXmlDocument.SelectSingleNode("/e:Event/e:System/e:Provider", xmlNamespaceManager);
                XmlNode singleNode = currentXmlDocument.SelectSingleNode("/e:Event/e:System/e:EventID", xmlNamespaceManager);
                XmlNode currentSingleNode = currentXmlDocument.SelectSingleNode("/e:Event/e:System/e:Version", xmlNamespaceManager);
                if (xmlNode == null || xmlNode.Attributes["Name"] == null || xmlNode.Attributes["Name"].Value != "Microsoft-Windows-Kernel-Processor-Power" || singleNode == null || singleNode.InnerText != "55" || currentSingleNode == null || currentSingleNode.InnerText != "0")
                {
                    return false;
                }
                uint processorGroup;
                uint processorNumber;
                if (!ReadField(currentXmlDocument, xmlNamespaceManager, "Group", out processorGroup) || processorGroup > 65535 || !ReadField(currentXmlDocument, xmlNamespaceManager, "Number", out processorNumber) || processorNumber > 63 || !ReadField(currentXmlDocument, xmlNamespaceManager, "PerformanceImplementation", out implementation))
                {
                    return false;
                }
                processor = processorGroup.ToString(CultureInfo.InvariantCulture) + ":" + processorNumber.ToString(CultureInfo.InvariantCulture);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool ReadField(XmlDocument doc, XmlNamespaceManager ns, string name, out uint value)
        {
            value = 0u;
            XmlNodeList xmlNodeList = doc.SelectNodes("/e:Event/e:EventData/e:Data[@Name='" + name + "']", ns);
            if (xmlNodeList.Count == 1)
            {
                return uint.TryParse(xmlNodeList[0].InnerText, NumberStyles.None, CultureInfo.InvariantCulture, out value);
            }
            return false;
        }

        internal static Interface Classify(IEnumerable<uint> implementations, int expectedCount)
        {
            if (expectedCount <= 0)
            {
                return Interface.Unknown;
            }
            int observedCount = 0;
            Interface detectedInterface = Interface.Unknown;
            foreach (uint implementation in implementations)
            {
                Interface currentInterface;
                switch (implementation)
                {
                case 3u:
                    currentInterface = Interface.Cppc;
                    break;
                case 1u:
                    currentInterface = Interface.AcpiPState;
                    break;
                default:
                    return Interface.Unknown;
                }
                if (observedCount > 0 && currentInterface != detectedInterface)
                {
                    return Interface.Unknown;
                }
                detectedInterface = currentInterface;
                observedCount++;
            }
            return observedCount == expectedCount ? detectedInterface : Interface.Unknown;
        }

        internal static bool BootRecordMatchesUptime(DateTime recorded, DateTime estimated, DateTime now)
        {
            if (recorded <= now)
            {
                return Math.Abs((recorded - estimated).TotalMinutes) <= 5.0;
            }
            return false;
        }

        private static DateTime? ReadBootRecord()
        {
            EventLogQuery eventQuery = new EventLogQuery("System", PathType.LogName, "*[System[Provider[@Name='Microsoft-Windows-Kernel-General'] and EventID=12]]");
            eventQuery.ReverseDirection = true;
            using (EventLogReader eventLogReader = new EventLogReader(eventQuery))
            {
                using (EventRecord eventRecord = eventLogReader.ReadEvent(TimeSpan.FromMilliseconds(250.0)))
                {
                    return (eventRecord == null || !eventRecord.TimeCreated.HasValue) ? null : new DateTime?(eventRecord.TimeCreated.Value.ToUniversalTime());
                }
            }
        }

        private static Interface ReadInterface()
        {
            try
            {
                uint activeProcessorCount = GetActiveProcessorCount(ushort.MaxValue);
                if (activeProcessorCount == 0 || activeProcessorCount > 512)
                {
                    return Interface.Unknown;
                }
                Stopwatch stopwatch = Stopwatch.StartNew();
                DateTime utcNow = DateTime.UtcNow;
                DateTime estimated = utcNow.AddMilliseconds(0.0 - (double)Native.GetTickCount64());
                DateTime? dateTime = ReadBootRecord();
                if (!dateTime.HasValue || !BootRecordMatchesUptime(dateTime.Value, estimated, utcNow))
                {
                    return Interface.Unknown;
                }
                string valueText = dateTime.Value.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
                string query = "*[System[Provider[@Name='Microsoft-Windows-Kernel-Processor-Power'] and EventID=55 and TimeCreated[@SystemTime >= '" + valueText + "']]]";
                EventLogQuery eventQuery = new EventLogQuery("System", PathType.LogName, query);
                eventQuery.ReverseDirection = true;
                Dictionary<string, uint> implementationByProcessor = new Dictionary<string, uint>();
                using (EventLogReader eventLogReader = new EventLogReader(eventQuery))
                {
                    eventLogReader.BatchSize = 32;
                    for (int i = 0; i < 1024 && stopwatch.ElapsedMilliseconds < 1500; i++)
                    {
                        using (EventRecord eventRecord = eventLogReader.ReadEvent(TimeSpan.FromMilliseconds(100.0)))
                        {
                            if (eventRecord == null)
                            {
                                break;
                            }
                            string processor;
                            uint implementation;
                            if (!TryReadCapability(eventRecord.ToXml(), out processor, out implementation))
                            {
                                return Interface.Unknown;
                            }
                            if (!implementationByProcessor.ContainsKey(processor))
                            {
                                implementationByProcessor.Add(processor, implementation);
                            }
                            if (implementationByProcessor.Count == activeProcessorCount)
                            {
                                return Classify(implementationByProcessor.Values, (int)activeProcessorCount);
                            }
                            continue;
                        }
                    }
                }
            }
            catch
            {
            }
            return Interface.Unknown;
        }
    }
}
