using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Xml;
using Microsoft.Win32;

namespace PaviseApp
{
    internal sealed class MaintenanceControl : IMaintenanceControl
    {
        internal const string SubKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\Maintenance";
        private const string ValueName = "MaintenanceDisabled";
        public bool TryRead(out MaintenanceValue value)
        {
            RefuseNativeInTests(); value = null;
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(SubKey))
                {
                    object current = key == null ? null : key.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                    value = new MaintenanceValue { Exists = current != null, Value = current,
                        Kind = current == null ? RegistryValueKind.Unknown : key.GetValueKind(ValueName) };
                    return true;
                }
            }
            catch { return false; }
        }
        public bool TryWrite(int value, MaintenanceValue expected)
        {
            RefuseNativeInTests();
            try { MaintenanceValue current; if (expected == null || !TryRead(out current) || !expected.SameAs(current)) return false;
                using (RegistryKey key = Registry.LocalMachine.CreateSubKey(SubKey)) { if (key == null || !Matches(key, expected)) return false; key.SetValue(ValueName, value, RegistryValueKind.DWord); key.Flush(); return true; } }
            catch { return false; }
        }
        public bool TryDelete(MaintenanceValue expected)
        {
            RefuseNativeInTests();
            try { using (RegistryKey key = Registry.LocalMachine.OpenSubKey(SubKey, true)) { if (!Matches(key, expected)) return false; if (key != null) { key.DeleteValue(ValueName, false); key.Flush(); } return true; } }
            catch { return false; }
        }
        private static bool Matches(RegistryKey key, MaintenanceValue expected)
        {
            if (expected == null) return false;
            object current = key == null ? null : key.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            return expected.SameAs(new MaintenanceValue { Exists = current != null, Value = current,
                Kind = current == null ? RegistryValueKind.Unknown : key.GetValueKind(ValueName) });
        }

        // GetRunningTasks and registered-task XML are read only. Observation cannot
        // prove that all Windows servicing, or every protected task, is idle.
        public MaintenanceRuntimeSnapshot QueryRuntime()
        {
            RefuseNativeInTests();
            if (Environment.OSVersion.Version < new Version(6, 2)) return new MaintenanceRuntimeSnapshot { State = MaintenanceRuntimeState.Unsupported };
            object service = null, running = null;
            var references = new List<object>();
            try
            {
                service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", true));
                Invoke(service, "Connect", BindingFlags.InvokeMethod, new object[0]);
                running = Invoke(service, "GetRunningTasks", BindingFlags.InvokeMethod, new object[] { 1 });
                int total = Convert.ToInt32(Invoke(running, "Count", BindingFlags.GetProperty, new object[0]));
                if (total < 0 || total > 1024) throw new InvalidOperationException("Unexpected task count");
                int maintenanceCount = 0;
                for (int i = 1; i <= total; i++)
                {
                    object task = Invoke(running, "Item", BindingFlags.GetProperty, new object[] { i }); references.Add(task);
                    string path = Convert.ToString(Invoke(task, "Path", BindingFlags.GetProperty, new object[0]));
                    int split = path.LastIndexOf('\\');
                    if (split < 0 || split == path.Length - 1) throw new InvalidOperationException("Invalid task path");
                    object folder = Invoke(service, "GetFolder", BindingFlags.InvokeMethod, new object[] { split == 0 ? "\\" : path.Substring(0, split) }); references.Add(folder);
                    object registered = Invoke(folder, "GetTask", BindingFlags.InvokeMethod, new object[] { path.Substring(split + 1) }); references.Add(registered);
                    string xml = Convert.ToString(Invoke(registered, "Xml", BindingFlags.GetProperty, new object[0]));
                    if (IsMaintenanceTaskXml(xml)) maintenanceCount++;
                }
                return new MaintenanceRuntimeSnapshot { State = maintenanceCount == 0 ? MaintenanceRuntimeState.NotObservedRunning : MaintenanceRuntimeState.Running,
                    RunningTaskCount = maintenanceCount, Detail = "Read-only observation of visible maintenance-tagged tasks; other servicing activity may still run" };
            }
            catch { return new MaintenanceRuntimeSnapshot { State = MaintenanceRuntimeState.Unknown, Detail = "Task runtime status is unavailable; existing maintenance may still run" }; }
            finally
            {
                for (int i = references.Count - 1; i >= 0; i--) Release(references[i]);
                Release(running); Release(service);
            }
        }
        internal static bool IsMaintenanceTaskXml(string xml)
        {
            if (string.IsNullOrEmpty(xml) || xml.Length > 1048576) throw new FormatException("Invalid task XML");
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1048576 };
            var document = new XmlDocument { XmlResolver = null };
            using (var reader = XmlReader.Create(new System.IO.StringReader(xml), settings)) document.Load(reader);
            var names = new XmlNamespaceManager(document.NameTable);
            names.AddNamespace("t", "http://schemas.microsoft.com/windows/2004/02/mit/task");
            XmlNode period = document.SelectSingleNode("/t:Task/t:Settings/t:MaintenanceSettings/t:Period", names);
            return period != null && !string.IsNullOrWhiteSpace(period.InnerText);
        }
        private static object Invoke(object target, string name, BindingFlags kind, object[] arguments)
        { return target.GetType().InvokeMember(name, kind, null, target, arguments); }
        private static void Release(object value) { try { if (value != null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); } catch { } }
        private static void RefuseNativeInTests()
        {
#if PAVISE_SELFTEST || PAVISE_PERFLAB
            throw new InvalidOperationException("Maintenance tests must use a mocked control; native access is disabled.");
#endif
        }
    }
}
