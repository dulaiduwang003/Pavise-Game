using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Xml;

namespace PaviseApp
{
    // CPPC 接口证据不等于 HWP 实际状态。
    internal static class ProcessorPowerPlatform
    {
        internal enum Interface { Unknown, AcpiPState, Cppc }
        private static readonly Lazy<Interface> detected = new Lazy<Interface>(ReadInterface);
        internal static Interface Current { get { return detected.Value; } }

        [DllImport("kernel32.dll")]
        private static extern uint GetActiveProcessorCount(ushort groupNumber);

        // 证据不足时保留最低性能值。
        internal static bool? AutonomousMinimum(Interface platform, uint? requested)
        {
            if (platform == Interface.AcpiPState) return false;
            if (platform == Interface.Cppc && requested == 1) return true;
            return null;
        }

        internal static bool TryResolveMinimumIndices(bool? acMode, bool? dcMode, uint acCandidate,
            uint dcCandidate, Func<bool, uint?> readExisting, out uint ac, out uint dc)
        {
            ac = acCandidate; dc = dcCandidate;
            uint? savedAc = acMode.HasValue ? (uint?)acCandidate : readExisting(true);
            uint? savedDc = dcMode.HasValue ? (uint?)dcCandidate : readExisting(false);
            if (!savedAc.HasValue || !savedDc.HasValue || savedAc.Value > 100 || savedDc.Value > 100) return false;
            ac = savedAc.Value; dc = savedDc.Value;
            return true;
        }

        internal static bool TryReadCapability(string xml, out string processor, out uint implementation)
        {
            processor = null; implementation = 0;
            try
            {
                var doc = new XmlDocument { XmlResolver = null };
                doc.LoadXml(xml);
                var ns = new XmlNamespaceManager(doc.NameTable);
                ns.AddNamespace("e", "http://schemas.microsoft.com/win/2004/08/events/event");
                XmlNode provider = doc.SelectSingleNode("/e:Event/e:System/e:Provider", ns);
                XmlNode id = doc.SelectSingleNode("/e:Event/e:System/e:EventID", ns);
                XmlNode version = doc.SelectSingleNode("/e:Event/e:System/e:Version", ns);
                if (provider == null || provider.Attributes["Name"] == null
                    || provider.Attributes["Name"].Value != "Microsoft-Windows-Kernel-Processor-Power"
                    || id == null || id.InnerText != "55" || version == null || version.InnerText != "0") return false;
                uint group, number;
                if (!ReadField(doc, ns, "Group", out group) || group > ushort.MaxValue
                    || !ReadField(doc, ns, "Number", out number) || number > 63
                    || !ReadField(doc, ns, "PerformanceImplementation", out implementation)) return false;
                processor = group.ToString(CultureInfo.InvariantCulture) + ":" + number.ToString(CultureInfo.InvariantCulture);
                return true;
            }
            catch { return false; }
        }

        private static bool ReadField(XmlDocument doc, XmlNamespaceManager ns, string name, out uint value)
        {
            value = 0;
            XmlNodeList nodes = doc.SelectNodes("/e:Event/e:EventData/e:Data[@Name='" + name + "']", ns);
            return nodes.Count == 1 && uint.TryParse(nodes[0].InnerText, NumberStyles.None,
                CultureInfo.InvariantCulture, out value);
        }

        internal static Interface Classify(IEnumerable<uint> implementations, int expectedCount)
        {
            if (expectedCount <= 0) return Interface.Unknown;
            int count = 0;
            Interface result = Interface.Unknown;
            foreach (uint value in implementations)
            {
                Interface next = value == 1 ? Interface.AcpiPState : value == 3 ? Interface.Cppc : Interface.Unknown;
                if (next == Interface.Unknown || (count > 0 && next != result)) return Interface.Unknown;
                result = next; count++;
            }
            return count == expectedCount ? result : Interface.Unknown;
        }

        internal static bool BootRecordMatchesUptime(DateTime recorded, DateTime estimated, DateTime now)
        {
            return recorded <= now && Math.Abs((recorded - estimated).TotalMinutes) <= 5;
        }

        private static DateTime? ReadBootRecord()
        {
            var query = new EventLogQuery("System", PathType.LogName,
                "*[System[Provider[@Name='Microsoft-Windows-Kernel-General'] and EventID=12]]")
                { ReverseDirection = true };
            using (var reader = new EventLogReader(query))
            using (EventRecord record = reader.ReadEvent(TimeSpan.FromMilliseconds(250)))
                return record == null || !record.TimeCreated.HasValue ? (DateTime?)null : record.TimeCreated.Value.ToUniversalTime();
        }

        private static Interface ReadInterface()
        {
            try
            {
                uint expected = GetActiveProcessorCount(0xFFFF);
                if (expected == 0 || expected > 512) return Interface.Unknown;
                var budget = Stopwatch.StartNew();
                DateTime now = DateTime.UtcNow;
                DateTime estimated = now.AddMilliseconds(-(double)Native.GetTickCount64());
                DateTime? boot = ReadBootRecord();
                if (!boot.HasValue || !BootRecordMatchesUptime(boot.Value, estimated, now)) return Interface.Unknown;
                string since = boot.Value.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
                string filter = "*[System[Provider[@Name='Microsoft-Windows-Kernel-Processor-Power']"
                    + " and EventID=55 and TimeCreated[@SystemTime >= '" + since + "']]]";
                var query = new EventLogQuery("System", PathType.LogName, filter) { ReverseDirection = true };
                var records = new Dictionary<string, uint>();
                using (var reader = new EventLogReader(query))
                {
                    reader.BatchSize = 32;
                    for (int i = 0; i < 1024 && budget.ElapsedMilliseconds < 1500; i++)
                    {
                        using (EventRecord record = reader.ReadEvent(TimeSpan.FromMilliseconds(100)))
                        {
                            if (record == null) break;
                            string processor; uint implementation;
                            if (!TryReadCapability(record.ToXml(), out processor, out implementation)) return Interface.Unknown;
                            if (!records.ContainsKey(processor)) records.Add(processor, implementation);
                            if (records.Count == expected) return Classify(records.Values, (int)expected);
                        }
                    }
                }
            }
            catch { }
            return Interface.Unknown;
        }
    }
}
