using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Xml;

namespace PaviseApp
{
    internal sealed partial class PrivacyNativeBackend
    {
#if PAVISE_SELFTEST
        internal static Func<string, string> TaskReadForTest;
        internal static Func<string, string, bool> TaskWriteForTest;
#endif
        private static string[] TasksFor(string action)
        {
            if (action == PrivacyActionCatalog.Ceip) return new[] {
                @"\Microsoft\Windows\Customer Experience Improvement Program\Consolidator",
                @"\Microsoft\Windows\Customer Experience Improvement Program\UsbCeip",
                @"\Microsoft\Windows\Customer Experience Improvement Program\KernelCeipTask" };
            if (action == PrivacyActionCatalog.Appraiser) return new[] {
                @"\Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser",
                @"\Microsoft\Windows\Application Experience\ProgramDataUpdater" };
            return new string[0];
        }
        private static object Com(object instance, string member, BindingFlags flags, params object[] args)
        { return instance.GetType().InvokeMember(member, flags, null, instance, args, CultureInfo.InvariantCulture); }
        private static void ReleaseCom(object instance)
        { if (instance != null && Marshal.IsComObject(instance)) Marshal.FinalReleaseComObject(instance); }
        private static object OpenTask(string path, out object scheduler, out object folder)
        {
            scheduler = folder = null;
            Type type = Type.GetTypeFromProgID("Schedule.Service");
            if (type == null) throw new NotSupportedException("Task Scheduler interface unavailable");
            scheduler = Activator.CreateInstance(type);
            Com(scheduler, "Connect", BindingFlags.InvokeMethod, Type.Missing, Type.Missing, Type.Missing, Type.Missing);
            int separator = path.LastIndexOf('\\');
            folder = Com(scheduler, "GetFolder", BindingFlags.InvokeMethod, path.Substring(0, separator));
            return Com(folder, "GetTask", BindingFlags.InvokeMethod, path.Substring(separator + 1));
        }
        internal static bool MissingTask(Exception error)
        {
            while (error is TargetInvocationException && error.InnerException != null) error = error.InnerException;
            // COM 的缺失错误可能被 CLR 转换为文件或目录异常。
            return error != null && (error.HResult == unchecked((int)0x80070002) || error.HResult == unchecked((int)0x80070003));
        }
        internal static string TaskIdentity(string xml)
        {
            var document = new XmlDocument { XmlResolver = null };
            using (var reader = XmlReader.Create(new System.IO.StringReader(xml),
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1024 * 1024 }))
                document.Load(reader);
            var ns = new XmlNamespaceManager(document.NameTable);
            ns.AddNamespace("t", "http://schemas.microsoft.com/windows/2004/02/mit/task");
            XmlNode enabled = document.SelectSingleNode("/t:Task/t:Settings/t:Enabled", ns);
            if (enabled != null) enabled.ParentNode.RemoveChild(enabled);
            using (var hash = SHA256.Create())
                return Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(document.OuterXml)));
        }
        private static string TaskRead(string path)
        {
#if PAVISE_SELFTEST
            if (TaskReadForTest == null) throw new InvalidOperationException("Unmocked privacy task read");
            string mock = TaskReadForTest(path);
            if (mock == null) throw new InvalidOperationException("Unknown scheduled task");
            return mock;
#else
            object scheduler = null, folder = null, task = null;
            try
            {
                task = OpenTask(path, out scheduler, out folder);
                return "t:" + (Convert.ToBoolean(Com(task, "Enabled", BindingFlags.GetProperty), CultureInfo.InvariantCulture) ? "1:" : "0:")
                    + TaskIdentity((string)Com(task, "Xml", BindingFlags.GetProperty));
            }
            catch (Exception error) { if (MissingTask(error)) return "missing"; throw; }
            finally { ReleaseCom(task); ReleaseCom(folder); ReleaseCom(scheduler); }
#endif
        }
        internal static string TaskWanted(string current, bool enabled)
        {
            string[] parts = (current ?? "").Split(':');
            if (parts.Length != 3 || parts[0] != "t" || (parts[1] != "0" && parts[1] != "1")
                || Convert.FromBase64String(parts[2]).Length != 32) throw new FormatException("Unknown task identity");
            return "t:" + (enabled ? "1:" : "0:") + parts[2];
        }
        private static bool TaskWrite(string path, string value)
        {
            string[] wanted = (value ?? "").Split(':');
            if (wanted.Length != 3 || wanted[0] != "t" || (wanted[1] != "0" && wanted[1] != "1")) return false;
            string current = TaskRead(path);
            if (current == "missing") return false;
            string[] actual = current.Split(':');
            if (actual.Length != 3 || actual[2] != wanted[2]) return false;
#if PAVISE_SELFTEST
            if (TaskWriteForTest == null) throw new InvalidOperationException("Unmocked privacy task write");
            return TaskWriteForTest(path, value);
#else
            object scheduler = null, folder = null, task = null;
            try
            {
                task = OpenTask(path, out scheduler, out folder);
                // Recheck identity on the exact COM object whose enabled flag will change.
                if (TaskIdentity((string)Com(task, "Xml", BindingFlags.GetProperty)) != wanted[2]) return false;
                Com(task, "Enabled", BindingFlags.SetProperty, wanted[1] == "1");
                return TaskRead(path) == value;
            }
            finally { ReleaseCom(task); ReleaseCom(folder); ReleaseCom(scheduler); }
#endif
        }
    }
}
