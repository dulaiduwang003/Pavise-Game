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
			uint? nullable2 = (dcMode.HasValue ? new uint?(dcCandidate) : readExisting(false));
			if (!nullable.HasValue || !nullable2.HasValue || nullable.Value > 100 || nullable2.Value > 100)
			{
				return false;
			}
			ac = nullable.Value;
			dc = nullable2.Value;
			return true;
		}

		internal static bool TryReadCapability(string xml, out string processor, out uint implementation)
		{
			processor = null;
			implementation = 0u;
			try
			{
				XmlDocument xmlDocument = new XmlDocument();
				xmlDocument.XmlResolver = null;
				XmlDocument xmlDocument2 = xmlDocument;
				xmlDocument2.LoadXml(xml);
				XmlNamespaceManager xmlNamespaceManager = new XmlNamespaceManager(xmlDocument2.NameTable);
				xmlNamespaceManager.AddNamespace("e", "http://schemas.microsoft.com/win/2004/08/events/event");
				XmlNode xmlNode = xmlDocument2.SelectSingleNode("/e:Event/e:System/e:Provider", xmlNamespaceManager);
				XmlNode xmlNode2 = xmlDocument2.SelectSingleNode("/e:Event/e:System/e:EventID", xmlNamespaceManager);
				XmlNode xmlNode3 = xmlDocument2.SelectSingleNode("/e:Event/e:System/e:Version", xmlNamespaceManager);
				if (xmlNode == null || xmlNode.Attributes["Name"] == null || xmlNode.Attributes["Name"].Value != "Microsoft-Windows-Kernel-Processor-Power" || xmlNode2 == null || xmlNode2.InnerText != "55" || xmlNode3 == null || xmlNode3.InnerText != "0")
				{
					return false;
				}
				uint value;
				uint value2;
				if (!ReadField(xmlDocument2, xmlNamespaceManager, "Group", out value) || value > 65535 || !ReadField(xmlDocument2, xmlNamespaceManager, "Number", out value2) || value2 > 63 || !ReadField(xmlDocument2, xmlNamespaceManager, "PerformanceImplementation", out implementation))
				{
					return false;
				}
				processor = value.ToString(CultureInfo.InvariantCulture) + ":" + value2.ToString(CultureInfo.InvariantCulture);
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
			int processedCount = 0;
			Interface @interface = Interface.Unknown;
			using (IEnumerator<uint> enumerator = implementations.GetEnumerator())
			{
				while (enumerator.MoveNext())
				{
					int classifyValue;
					switch (enumerator.Current)
					{
					default:
						classifyValue = 0;
						break;
					case 3u:
						classifyValue = 2;
						break;
					case 1u:
						classifyValue = 1;
						break;
					}
					Interface interface2 = (Interface)classifyValue;
					if (interface2 == Interface.Unknown || (processedCount > 0 && interface2 != @interface))
					{
						return Interface.Unknown;
					}
					@interface = interface2;
					processedCount++;
				}
			}
			if (processedCount != expectedCount)
			{
				return Interface.Unknown;
			}
			return @interface;
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
			EventLogQuery eventLogQuery = new EventLogQuery("System", PathType.LogName, "*[System[Provider[@Name='Microsoft-Windows-Kernel-General'] and EventID=12]]");
			eventLogQuery.ReverseDirection = true;
			EventLogQuery eventQuery = eventLogQuery;
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
				EventLogQuery eventLogQuery = new EventLogQuery("System", PathType.LogName, query);
				eventLogQuery.ReverseDirection = true;
				EventLogQuery eventQuery = eventLogQuery;
				Dictionary<string, uint> dictionary = new Dictionary<string, uint>();
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
							if (!dictionary.ContainsKey(processor))
							{
								dictionary.Add(processor, implementation);
							}
							if (dictionary.Count == activeProcessorCount)
							{
								return Classify(dictionary.Values, (int)activeProcessorCount);
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
