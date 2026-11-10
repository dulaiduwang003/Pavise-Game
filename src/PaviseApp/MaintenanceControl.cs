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

        public bool TryRead(out MaintenanceValue value)
        {
            RefuseNativeInTests();
            value = null;
            try
            {
                using (RegistryKey registryKey = Registry.LocalMachine.OpenSubKey("SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Schedule\\Maintenance"))
                {
                    object instance = ((registryKey == null) ? null : registryKey.GetValue("MaintenanceDisabled", null, RegistryValueOptions.DoNotExpandEnvironmentNames));
                    value = new MaintenanceValue
                    {
                        Exists = (instance != null),
                        Value = instance,
                        Kind = ((instance != null) ? registryKey.GetValueKind("MaintenanceDisabled") : RegistryValueKind.Unknown)
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
                MaintenanceValue maintenanceValue;
                if (expected == null || !TryRead(out maintenanceValue) || !expected.SameAs(maintenanceValue))
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
            object value = ((key == null) ? null : key.GetValue("MaintenanceDisabled", null, RegistryValueOptions.DoNotExpandEnvironmentNames));
            return expected.SameAs(new MaintenanceValue
            {
                Exists = (value != null),
                Value = value,
                Kind = ((value != null) ? key.GetValueKind("MaintenanceDisabled") : RegistryValueKind.Unknown)
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
            object instance = null;
            object invokeResult = null;
            List<object> instances = new List<object>();
            try
            {
                instance = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", true));
                Invoke(instance, "Connect", BindingFlags.InvokeMethod, new object[0]);
                invokeResult = Invoke(instance, "GetRunningTasks", BindingFlags.InvokeMethod, new object[1] { 1 });
                int toInt32Result = Convert.ToInt32(Invoke(invokeResult, "Count", BindingFlags.GetProperty, new object[0]));
                if (toInt32Result < 0 || toInt32Result > 1024)
                {
                    throw new InvalidOperationException("Unexpected task count");
                }
                int runningTaskCountValue = 0;
                for (int i = 1; i <= toInt32Result; i++)
                {
                    object invokeResult2 = Invoke(invokeResult, "Item", BindingFlags.GetProperty, new object[1] { i });
                    instances.Add(invokeResult2);
                    string convertText = Convert.ToString(Invoke(invokeResult2, "Path", BindingFlags.GetProperty, new object[0]));
                    int matchIndex = convertText.LastIndexOf('\\');
                    if (matchIndex < 0 || matchIndex == convertText.Length - 1)
                    {
                        throw new InvalidOperationException("Invalid task path");
                    }
                    object invokeResult3 = Invoke(instance, "GetFolder", BindingFlags.InvokeMethod, new object[1] { (matchIndex == 0) ? "\\" : convertText.Substring(0, matchIndex) });
                    instances.Add(invokeResult3);
                    object invokeResult4 = Invoke(invokeResult3, "GetTask", BindingFlags.InvokeMethod, new object[1] { convertText.Substring(matchIndex + 1) });
                    instances.Add(invokeResult4);
                    string xml = Convert.ToString(Invoke(invokeResult4, "Xml", BindingFlags.GetProperty, new object[0]));
                    if (IsMaintenanceTaskXml(xml))
                    {
                        runningTaskCountValue++;
                    }
                }
                MaintenanceRuntimeSnapshot currentMaintenanceRuntimeSnapshot = new MaintenanceRuntimeSnapshot();
                currentMaintenanceRuntimeSnapshot.State = ((runningTaskCountValue == 0) ? MaintenanceRuntimeState.NotObservedRunning : MaintenanceRuntimeState.Running);
                currentMaintenanceRuntimeSnapshot.RunningTaskCount = runningTaskCountValue;
                currentMaintenanceRuntimeSnapshot.Detail = "Read-only observation of visible maintenance-tagged tasks; other servicing activity may still run";
                return currentMaintenanceRuntimeSnapshot;
            }
            catch
            {
                MaintenanceRuntimeSnapshot candidateMaintenanceRuntimeSnapshot = new MaintenanceRuntimeSnapshot();
                candidateMaintenanceRuntimeSnapshot.State = MaintenanceRuntimeState.Unknown;
                candidateMaintenanceRuntimeSnapshot.Detail = "Task runtime status is unavailable; existing maintenance may still run";
                return candidateMaintenanceRuntimeSnapshot;
            }
            finally
            {
                for (int listIndex = instances.Count - 1; listIndex >= 0; listIndex--)
                {
                    Release(instances[listIndex]);
                }
                Release(invokeResult);
                Release(instance);
            }
        }

        internal static bool IsMaintenanceTaskXml(string xml)
        {
            if (string.IsNullOrEmpty(xml) || xml.Length > 1048576)
            {
                throw new FormatException("Invalid task XML");
            }
            XmlReaderSettings settings = new XmlReaderSettings();
            settings.DtdProcessing = DtdProcessing.Prohibit;
            settings.XmlResolver = null;
            settings.MaxCharactersInDocument = 1048576L;
            XmlDocument currentXmlDocument = new XmlDocument();
            currentXmlDocument.XmlResolver = null;
            using (XmlReader reader = XmlReader.Create(new StringReader(xml), settings))
            {
                currentXmlDocument.Load(reader);
            }
            XmlNamespaceManager xmlNamespaceManager = new XmlNamespaceManager(currentXmlDocument.NameTable);
            xmlNamespaceManager.AddNamespace("t", "http://schemas.microsoft.com/windows/2004/02/mit/task");
            XmlNode xmlNode = currentXmlDocument.SelectSingleNode("/t:Task/t:Settings/t:MaintenanceSettings/t:Period", xmlNamespaceManager);
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
