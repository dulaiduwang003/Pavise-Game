using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Xml;
using Microsoft.Win32;

namespace PaviseApp
{
	internal sealed class MaintenanceControl : IMaintenanceControl
	{
		internal const string SubKey = "SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Schedule\\Maintenance";

		private const string ValueName = "MaintenanceDisabled";

		public bool TryRead(out MaintenanceValue value)
		{
			RefuseNativeInTests();
			value = null;
			try
			{
				using (RegistryKey registryKey = Registry.LocalMachine.OpenSubKey("SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Schedule\\Maintenance"))
				{
					object obj = ((registryKey == null) ? null : registryKey.GetValue("MaintenanceDisabled", null, RegistryValueOptions.DoNotExpandEnvironmentNames));
					value = new MaintenanceValue
					{
						Exists = (obj != null),
						Value = obj,
						Kind = ((obj != null) ? registryKey.GetValueKind("MaintenanceDisabled") : RegistryValueKind.Unknown)
					};
					return true;
				}
			}
			catch
			{
				return false;
			}
		}

		public bool TryWrite(int value, MaintenanceValue expected)
		{
			RefuseNativeInTests();
			try
			{
				MaintenanceValue value2;
				if (expected == null || !TryRead(out value2) || !expected.SameAs(value2))
				{
					return false;
				}
				using (RegistryKey registryKey = Registry.LocalMachine.CreateSubKey("SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Schedule\\Maintenance"))
				{
					if (registryKey == null || !Matches(registryKey, expected))
					{
						return false;
					}
					registryKey.SetValue("MaintenanceDisabled", value, RegistryValueKind.DWord);
					registryKey.Flush();
					return true;
				}
			}
			catch
			{
				return false;
			}
		}

		public bool TryDelete(MaintenanceValue expected)
		{
			RefuseNativeInTests();
			try
			{
				using (RegistryKey registryKey = Registry.LocalMachine.OpenSubKey("SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Schedule\\Maintenance", true))
				{
					if (!Matches(registryKey, expected))
					{
						return false;
					}
					if (registryKey != null)
					{
						registryKey.DeleteValue("MaintenanceDisabled", false);
						registryKey.Flush();
					}
					return true;
				}
			}
			catch
			{
				return false;
			}
		}

		private static bool Matches(RegistryKey key, MaintenanceValue expected)
		{
			if (expected == null)
			{
				return false;
			}
			object obj = ((key == null) ? null : key.GetValue("MaintenanceDisabled", null, RegistryValueOptions.DoNotExpandEnvironmentNames));
			return expected.SameAs(new MaintenanceValue
			{
				Exists = (obj != null),
				Value = obj,
				Kind = ((obj != null) ? key.GetValueKind("MaintenanceDisabled") : RegistryValueKind.Unknown)
			});
		}

		public MaintenanceRuntimeSnapshot QueryRuntime()
		{
			RefuseNativeInTests();
			if (Environment.OSVersion.Version < new Version(6, 2))
			{
				MaintenanceRuntimeSnapshot maintenanceRuntimeSnapshot = new MaintenanceRuntimeSnapshot();
				maintenanceRuntimeSnapshot.State = MaintenanceRuntimeState.Unsupported;
				return maintenanceRuntimeSnapshot;
			}
			object obj = null;
			object obj2 = null;
			List<object> list = new List<object>();
			try
			{
				obj = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", true));
				Invoke(obj, "Connect", BindingFlags.InvokeMethod, new object[0]);
				obj2 = Invoke(obj, "GetRunningTasks", BindingFlags.InvokeMethod, new object[1] { 1 });
				int num = Convert.ToInt32(Invoke(obj2, "Count", BindingFlags.GetProperty, new object[0]));
				if (num < 0 || num > 1024)
				{
					throw new InvalidOperationException("Unexpected task count");
				}
				int num2 = 0;
				for (int i = 1; i <= num; i++)
				{
					object obj3 = Invoke(obj2, "Item", BindingFlags.GetProperty, new object[1] { i });
					list.Add(obj3);
					string text = Convert.ToString(Invoke(obj3, "Path", BindingFlags.GetProperty, new object[0]));
					int num3 = text.LastIndexOf('\\');
					if (num3 < 0 || num3 == text.Length - 1)
					{
						throw new InvalidOperationException("Invalid task path");
					}
					object obj4 = Invoke(obj, "GetFolder", BindingFlags.InvokeMethod, new object[1] { (num3 == 0) ? "\\" : text.Substring(0, num3) });
					list.Add(obj4);
					object obj5 = Invoke(obj4, "GetTask", BindingFlags.InvokeMethod, new object[1] { text.Substring(num3 + 1) });
					list.Add(obj5);
					string xml = Convert.ToString(Invoke(obj5, "Xml", BindingFlags.GetProperty, new object[0]));
					if (IsMaintenanceTaskXml(xml))
					{
						num2++;
					}
				}
				MaintenanceRuntimeSnapshot maintenanceRuntimeSnapshot2 = new MaintenanceRuntimeSnapshot();
				maintenanceRuntimeSnapshot2.State = ((num2 == 0) ? MaintenanceRuntimeState.NotObservedRunning : MaintenanceRuntimeState.Running);
				maintenanceRuntimeSnapshot2.RunningTaskCount = num2;
				maintenanceRuntimeSnapshot2.Detail = "Read-only observation of visible maintenance-tagged tasks; other servicing activity may still run";
				return maintenanceRuntimeSnapshot2;
			}
			catch
			{
				MaintenanceRuntimeSnapshot maintenanceRuntimeSnapshot3 = new MaintenanceRuntimeSnapshot();
				maintenanceRuntimeSnapshot3.State = MaintenanceRuntimeState.Unknown;
				maintenanceRuntimeSnapshot3.Detail = "Task runtime status is unavailable; existing maintenance may still run";
				return maintenanceRuntimeSnapshot3;
			}
			finally
			{
				for (int num4 = list.Count - 1; num4 >= 0; num4--)
				{
					Release(list[num4]);
				}
				Release(obj2);
				Release(obj);
			}
		}

		internal static bool IsMaintenanceTaskXml(string xml)
		{
			if (string.IsNullOrEmpty(xml) || xml.Length > 1048576)
			{
				throw new FormatException("Invalid task XML");
			}
			XmlReaderSettings xmlReaderSettings = new XmlReaderSettings();
			xmlReaderSettings.DtdProcessing = DtdProcessing.Prohibit;
			xmlReaderSettings.XmlResolver = null;
			xmlReaderSettings.MaxCharactersInDocument = 1048576L;
			XmlReaderSettings settings = xmlReaderSettings;
			XmlDocument xmlDocument = new XmlDocument();
			xmlDocument.XmlResolver = null;
			XmlDocument xmlDocument2 = xmlDocument;
			using (XmlReader reader = XmlReader.Create(new StringReader(xml), settings))
			{
				xmlDocument2.Load(reader);
			}
			XmlNamespaceManager xmlNamespaceManager = new XmlNamespaceManager(xmlDocument2.NameTable);
			xmlNamespaceManager.AddNamespace("t", "http://schemas.microsoft.com/windows/2004/02/mit/task");
			XmlNode xmlNode = xmlDocument2.SelectSingleNode("/t:Task/t:Settings/t:MaintenanceSettings/t:Period", xmlNamespaceManager);
			if (xmlNode != null)
			{
				return !string.IsNullOrWhiteSpace(xmlNode.InnerText);
			}
			return false;
		}

		private static object Invoke(object target, string name, BindingFlags kind, object[] arguments)
		{
			return target.GetType().InvokeMember(name, kind, null, target, arguments);
		}

		private static void Release(object value)
		{
			try
			{
				if (value != null && Marshal.IsComObject(value))
				{
					Marshal.FinalReleaseComObject(value);
				}
			}
			catch
			{
			}
		}

		private static void RefuseNativeInTests()
		{
		}
	}
}
