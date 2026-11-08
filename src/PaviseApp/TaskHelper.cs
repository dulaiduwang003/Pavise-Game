using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Windows.Forms;
using System.Xml;

namespace PaviseApp
{
	internal static class TaskHelper
	{
		public const string TaskName = "PaviseAuto";

		public const string AutostartArgument = "--autostart";

		internal const string AutostartDataArgument = "--autostart-data";

		private const string RepairFailKey = "AutostartRepairFails";

		private const int RepairGiveUp = 3;

		private static int cachedExists = -1;

		private static volatile int cachedEnabled = -1;

		private static readonly object startupGate = new object();

		[ThreadStatic]
		private static string lastSchtasksError;

		public static string LastSchtasksError
		{
			get
			{
				return lastSchtasksError;
			}
		}

		public static bool TaskExists()
		{
			bool flag = Run("/Query /TN PaviseAuto") == 0;
			cachedExists = (flag ? 1 : 0);
			return flag;
		}

		public static bool TaskExistsCached()
		{
			if (cachedExists >= 0)
			{
				return cachedExists == 1;
			}
			return TaskExists();
		}

		public static bool IsStartupTaskEnabled()
		{
			lock (startupGate)
			{
				string stdout;
				bool flag = RunCore("/Query /TN PaviseAuto /XML", true, out stdout) == 0;
				cachedExists = (flag ? 1 : 0);
				bool flag2 = flag && StartupTaskEnabled(stdout);
				cachedEnabled = (flag2 ? 1 : 0);
				return flag2;
			}
		}

		public static bool IsStartupTaskEnabledCached()
		{
			if (cachedEnabled >= 0)
			{
				return cachedEnabled == 1;
			}
			return IsStartupTaskEnabled();
		}

		public static int CreateStartupTask()
		{
			lock (startupGate)
			{
				try
				{
					if (!SelfUpdateInstaller.AllowStartupMaintenance(Application.ExecutablePath)) throw new IOException("Startup maintenance is blocked by an active update or a newer committed version.");
					string arguments = BuildStartupArguments(Paths.Data);
					string executable = StartupPayload.Ensure(Application.ExecutablePath);
					return RegisterStartupTask(executable, arguments, true);
				}
				catch (Exception ex)
				{
					lastSchtasksError = ex.Message;
					return -1;
				}
			}
		}

		private static int RegisterStartupTask(string executable, string arguments, bool allowFallback)
		{
			int num = CreateStartupTaskFromXml(executable, arguments);
			if (num != 0 && allowFallback)
			{
				num = Run("/Create /F /SC ONLOGON /RL HIGHEST /TN PaviseAuto /TR " + QuoteCommandArgument(BuildStartupCommandLine(executable, arguments)));
			}
			if (num == 0)
			{
				cachedExists = 1;
				cachedEnabled = 1;
				Settings.SaveStr("AutostartExe", executable);
				Settings.SaveStr("AutostartRepairFails", "0");
				RemoveStalePayloads(executable);
			}
			return num;
		}

		private static void RemoveStalePayloads(string executable)
		{
			try
			{
				if (!SelfUpdateInstaller.AllowStartupMaintenance(Application.ExecutablePath)) return;
				int num = StartupPayload.Cleanup(StartupPayload.ManagedRoot, executable, Application.ExecutablePath);
				if (num > 0)
				{
					Logger.Log(((Lang.Cur == 0) ? "已删除不再使用的自启副本 " : "Removed unused autostart copies: ") + num);
				}
			}
			catch
			{
			}
		}

		private static int CreateStartupTaskFromXml(string executable, string arguments)
		{
			string text = null;
			try
			{
				string text2 = BuildStartupTaskXml(executable, arguments);
				if (text2 == null)
				{
					lastSchtasksError = Lang.T("t.taskhelper.14");
					return -1;
				}
				string path = (string.IsNullOrEmpty(Paths.Data) ? Path.GetTempPath() : Paths.Data);
				text = Path.Combine(path, "Pavise_" + Guid.NewGuid().ToString("N") + ".xml");
				using (FileStream fileStream = new FileStream(text, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
				{
					byte[] preamble = Encoding.Unicode.GetPreamble();
					byte[] bytes = Encoding.Unicode.GetBytes(text2);
					fileStream.Write(preamble, 0, preamble.Length);
					fileStream.Write(bytes, 0, bytes.Length);
					fileStream.Flush();
				}
				return Run("/Create /F /TN PaviseAuto /XML \"" + text + "\"");
			}
			catch (Exception ex)
			{
				lastSchtasksError = ex.Message;
				return -1;
			}
			finally
			{
				try
				{
					if (text != null)
					{
						File.Delete(text);
					}
				}
				catch
				{
				}
			}
		}

		internal static string BuildStartupTaskXml(string exePath)
		{
			return BuildStartupTaskXml(exePath, BuildStartupArguments(Paths.Data));
		}

		internal static string BuildStartupTaskXml(string exePath, string arguments)
		{
			string text;
			try
			{
				using (WindowsIdentity windowsIdentity = WindowsIdentity.GetCurrent())
				{
					text = windowsIdentity.User.Value;
				}
			}
			catch
			{
				text = null;
			}
			if (string.IsNullOrEmpty(text))
			{
				text = Environment.UserDomainName + "\\" + Environment.UserName;
				if (text.Length <= 1)
				{
					return null;
				}
			}
			string text2 = XmlText(exePath);
			string text3 = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
			return "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n  <RegistrationInfo><URI>\\PaviseAuto</URI></RegistrationInfo>\r\n  <Principals><Principal id=\"Author\"><UserId>" + XmlText(text) + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>\r\n  <Settings><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><ExecutionTimeLimit>PT0S</ExecutionTimeLimit><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy></Settings>\r\n  <Triggers><LogonTrigger><StartBoundary>" + text3 + "</StartBoundary></LogonTrigger></Triggers>\r\n  <Actions Context=\"Author\"><Exec><Command>" + text2 + "</Command><Arguments>" + XmlText(arguments) + "</Arguments><WorkingDirectory>" + XmlText(Path.GetDirectoryName(exePath)) + "</WorkingDirectory></Exec></Actions>\r\n</Task>";
		}

		private static string XmlText(string value)
		{
			return (value ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
		}

		internal static string BuildStartupArguments(string dataPath)
		{
			if (!Paths.IsAbsoluteStartupDataPath(dataPath))
			{
				throw new ArgumentException("An absolute startup data directory is required");
			}
			return "--autostart --autostart-data " + QuoteCommandArgument(Path.GetFullPath(dataPath));
		}

		internal static string BuildStartupCommandLine(string executable, string arguments)
		{
			return QuoteCommandArgument(executable) + " " + arguments;
		}

		internal static string QuoteCommandArgument(string value)
		{
			StringBuilder stringBuilder = new StringBuilder("\"");
			int num = 0;
			string text = value ?? "";
			foreach (char c in text)
			{
				switch (c)
				{
				case '\\':
					num++;
					break;
				case '"':
					stringBuilder.Append('\\', num * 2 + 1);
					stringBuilder.Append(c);
					num = 0;
					break;
				default:
					stringBuilder.Append('\\', num);
					num = 0;
					stringBuilder.Append(c);
					break;
				}
			}
			stringBuilder.Append('\\', num * 2);
			stringBuilder.Append('"');
			return stringBuilder.ToString();
		}

		internal static bool IsVolatileAutostartPath(string path)
		{
			if (string.IsNullOrEmpty(path))
			{
				return true;
			}
			string text = path.Replace('/', '\\').ToLowerInvariant();
			if (!text.Contains("\\temp\\") && !text.Contains("\\tmp\\") && !text.Contains("\\xwechat_files\\") && !text.Contains("\\wechat files\\") && !text.Contains("\\inetcache\\"))
			{
				return text.Contains("\\$recycle.bin\\");
			}
			return true;
		}

		public static int DeleteStartupTask()
		{
			lock (startupGate)
			{
				int num = Run("/Delete /F /TN PaviseAuto");
				if (num == 0)
				{
					cachedExists = 0;
					cachedEnabled = 0;
					Settings.SaveStr("AutostartExe", "");
					RemoveStalePayloads(null);
				}
				return num;
			}
		}

		internal static bool RemoveStartupTaskForUninstall()
		{
			lastSchtasksError = null;
			lock (startupGate)
			{
				bool flag = RemoveStartupTaskForUninstall(ReadStartupTaskPresence, DeleteStartupTask);
				if (flag)
				{
					try
					{
						StartupPayload.Cleanup(StartupPayload.ManagedRoot, Application.ExecutablePath);
					}
					catch
					{
					}
				}
				return flag;
			}
		}

		internal static bool RemoveStartupTaskForUninstall(Func<bool?> readPresence, Func<int> remove)
		{
			try
			{
				bool? flag = readPresence();
				if (!flag.HasValue)
				{
					return false;
				}
				if (flag.Value)
				{
					remove();
					flag = readPresence();
					if (!flag.HasValue || flag.Value)
					{
						return false;
					}
				}
				cachedExists = 0;
				cachedEnabled = 0;
				return true;
			}
			catch
			{
				return false;
			}
		}

		private static bool? ReadStartupTaskPresence()
		{
			object obj = null;
			object obj2 = null;
			object obj3 = null;
			try
			{
				Type typeFromProgID = Type.GetTypeFromProgID("Schedule.Service", true);
				obj = Activator.CreateInstance(typeFromProgID);
				typeFromProgID.InvokeMember("Connect", BindingFlags.InvokeMethod, null, obj, new object[4]
				{
					Type.Missing,
					Type.Missing,
					Type.Missing,
					Type.Missing
				});
				obj2 = typeFromProgID.InvokeMember("GetFolder", BindingFlags.InvokeMethod, null, obj, new object[1] { "\\" });
				try
				{
					obj3 = obj2.GetType().InvokeMember("GetTask", BindingFlags.InvokeMethod, null, obj2, new object[1] { "PaviseAuto" });
					return (obj3 == null) ? null : new bool?(true);
				}
				catch (Exception innerException)
				{
					while (innerException is TargetInvocationException && innerException.InnerException != null)
					{
						innerException = innerException.InnerException;
					}
					if (Marshal.GetHRForException(innerException) == -2147024894)
					{
						return false;
					}
					lastSchtasksError = innerException.Message;
					return null;
				}
			}
			catch (Exception ex)
			{
				lastSchtasksError = ex.Message;
				return null;
			}
			finally
			{
				object[] array = new object[3] { obj3, obj2, obj };
				foreach (object obj4 in array)
				{
					if (obj4 != null && Marshal.IsComObject(obj4))
					{
						try
						{
							Marshal.ReleaseComObject(obj4);
						}
						catch
						{
						}
					}
				}
			}
		}

		public static void RefreshStartupTask()
		{
			lock (startupGate)
			{
				try
				{
					string executablePath = Application.ExecutablePath;
					if (!SelfUpdateInstaller.AllowStartupMaintenance(executablePath)) return;
					if (!TaskExists())
					{
						return;
					}
					string stdout;
					if (RunCore("/Query /TN PaviseAuto /XML", true, out stdout) != 0)
					{
						Logger.Warn(Lang.T("log.taskhelper.1"));
						return;
					}
					bool flag = StartupTaskEnabled(stdout);
					cachedEnabled = (flag ? 1 : 0);
					if (!flag)
					{
						return;
					}
					string text = ParseTaskCommandXml(stdout);
					if (string.IsNullOrWhiteSpace(text))
					{
						Logger.Warn(Lang.T("log.taskhelper.1"));
						return;
					}
					string a = ParseTaskArgumentsXml(stdout);
					if (ShouldPreserveLegacyTarget(executablePath, text, StartupPayload.ManagedRoot))
					{
						Logger.Log(Lang.T("log.taskhelper.2") + executablePath + Lang.T("log.taskhelper.3"));
						return;
					}
					string text2 = BuildStartupArguments(Paths.Data);
					string text3 = StartupPayload.Ensure(executablePath);
					bool flag2 = !string.Equals(a, text2, StringComparison.OrdinalIgnoreCase);
					bool flag3 = StartupTaskSettingsStale(stdout);
					bool flag4 = NeedsStartupTaskRefresh(text3, text);
					if (!flag4 && !flag2 && !flag3)
					{
						Settings.SaveStr("AutostartExe", text3);
						Settings.SaveStr("AutostartRepairFails", "0");
						RemoveStalePayloads(text3);
					}
					else if (flag4 || flag2)
					{
						Logger.Log(flag4 ? (Lang.T("t.taskhelper.4") + (text ?? Lang.T("t.taskhelper.5")) + " " + text3) : (Lang.T("t.taskhelper.6") + text2 + Lang.T("t.taskhelper.7")));
						int num = RegisterStartupTask(text3, text2, true);
						if (num != 0)
						{
							Logger.Warn((flag4 ? Lang.T("log.taskhelper.8") : Lang.T("log.taskhelper.9")) + num + DescribeSchtasksError());
						}
					}
					else
					{
						RepairStartupTaskSettings(text3, text2);
					}
				}
				catch (Exception ex)
				{
					lastSchtasksError = ex.Message;
					Logger.Warn(Lang.T("log.taskhelper.8") + "-1" + DescribeSchtasksError());
				}
			}
		}

		internal static bool ShouldPreserveLegacyTarget(string currentExecutable, string target, string managedRoot)
		{
			return ShouldPreserveLegacyTarget(currentExecutable, target, managedRoot, File.Exists(target));
		}

		internal static bool ShouldPreserveLegacyTarget(string currentExecutable, string target, string managedRoot, bool targetExists)
		{
			if (IsVolatileAutostartPath(currentExecutable) && !IsVolatileAutostartPath(target) && !StartupPayload.IsManagedExecutable(target, managedRoot) && NeedsStartupTaskRefresh(currentExecutable, target))
			{
				return targetExists;
			}
			return false;
		}

		private static void RepairStartupTaskSettings(string executable, string arguments)
		{
			int num = RepairFailCount();
			if (num < 3)
			{
				int num2 = CreateStartupTaskFromXml(executable, arguments);
				if (num2 == 0)
				{
					cachedExists = 1;
					cachedEnabled = 1;
					Settings.SaveStr("AutostartExe", executable);
					Settings.SaveStr("AutostartRepairFails", "0");
					Logger.Log(Lang.T("log.taskhelper.10"));
					RemoveStalePayloads(executable);
				}
				else
				{
					num++;
					Settings.SaveStr("AutostartRepairFails", num.ToString(CultureInfo.InvariantCulture));
					Logger.Warn((num >= 3) ? (Lang.T("log.taskhelper.11") + num2 + DescribeSchtasksError() + Lang.T("t.taskhelper.12")) : (Lang.T("log.taskhelper.13") + num2 + DescribeSchtasksError()));
				}
			}
		}

		private static int RepairFailCount()
		{
			int result;
			if (!int.TryParse(Settings.LoadStr("AutostartRepairFails", "0"), out result) || result <= 0)
			{
				return 0;
			}
			return result;
		}

		private static string DescribeSchtasksError()
		{
			string text = lastSchtasksError;
			if (!string.IsNullOrEmpty(text))
			{
				return Lang.T("t.taskhelper.16") + text;
			}
			return "";
		}

		internal static bool StartupTaskSettingsStale(string xml)
		{
			if (string.IsNullOrWhiteSpace(xml))
			{
				return false;
			}
			try
			{
				XmlDocument xmlDocument = new XmlDocument();
				xmlDocument.XmlResolver = null;
				xmlDocument.LoadXml(xml.TrimStart('\ufeff'));
				XmlNode settings = xmlDocument.SelectSingleNode("/*[local-name()='Task']/*[local-name()='Settings']");
				return !SettingEquals(settings, "ExecutionTimeLimit", "PT0S") || !SettingEquals(settings, "DisallowStartIfOnBatteries", "false") || !SettingEquals(settings, "StopIfGoingOnBatteries", "false");
			}
			catch
			{
				return false;
			}
		}

		internal static bool StartupTaskEnabled(string xml)
		{
			if (string.IsNullOrWhiteSpace(xml))
			{
				return false;
			}
			try
			{
				XmlDocument xmlDocument = new XmlDocument();
				xmlDocument.XmlResolver = null;
				xmlDocument.LoadXml(xml.TrimStart('\ufeff'));
				XmlNode xmlNode = xmlDocument.SelectSingleNode("/*[local-name()='Task']/*[local-name()='Settings']/*[local-name()='Enabled']");
				if (xmlNode == null)
				{
					return true;
				}
				return string.Equals(xmlNode.InnerText.Trim(), "true", StringComparison.OrdinalIgnoreCase);
			}
			catch
			{
				return false;
			}
		}

		private static bool SettingEquals(XmlNode settings, string name, string expected)
		{
			if (settings == null)
			{
				return true;
			}
			XmlNode xmlNode = settings.SelectSingleNode("*[local-name()='" + name + "']");
			if (xmlNode == null)
			{
				return false;
			}
			return string.Equals((xmlNode.InnerText ?? "").Trim(), expected, StringComparison.OrdinalIgnoreCase);
		}

		internal static bool NeedsStartupTaskRefresh(string currentExecutable, string taskExecutable)
		{
			if (string.IsNullOrWhiteSpace(currentExecutable))
			{
				return false;
			}
			if (string.IsNullOrWhiteSpace(taskExecutable))
			{
				return false;
			}
			try
			{
				string fullPath = Path.GetFullPath(currentExecutable.Trim().Trim('"'));
				string fullPath2 = Path.GetFullPath(taskExecutable.Trim().Trim('"'));
				return !string.Equals(fullPath, fullPath2, StringComparison.OrdinalIgnoreCase);
			}
			catch
			{
				return !string.Equals(currentExecutable.Trim().Trim('"'), taskExecutable.Trim().Trim('"'), StringComparison.OrdinalIgnoreCase);
			}
		}

		internal static bool TryReadTaskCommand(out string command)
		{
			string arguments;
			return TryReadTaskTarget(out command, out arguments);
		}

		// Keep the complete original task XML until the replacement application acknowledges startup.
		internal static string CaptureStartupTaskForUpdate()
		{
			lock (startupGate)
			{
				string xml;
				if (RunCore("/Query /TN PaviseAuto /XML", true, out xml) == 0)
				{
					if (string.IsNullOrWhiteSpace(ParseTaskCommandXml(xml)) || ParseTaskArgumentsXml(xml) == null) throw new IOException("The startup task could not be read safely.");
					return xml;
				}
				if (UpdateTaskCaptureCanBeAbsent(ReadStartupTaskPresence())) return null;
				throw new IOException("The startup task exists but could not be captured.");
			}
		}

		internal static bool UpdateTaskCaptureCanBeAbsent(bool? presence)
		{
			if (!presence.HasValue) throw new IOException("The startup task status is unknown; automatic update was stopped.");
			return !presence.Value;
		}

		internal static string BuildStartupTaskUpdateXml(string originalXml, string executable, string dataDirectory)
		{
			XmlDocument document = new XmlDocument(); document.XmlResolver = null;
			using (XmlReader reader = XmlReader.Create(new StringReader(originalXml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null })) document.Load(reader);
			XmlNode exec = document.SelectSingleNode("/*[local-name()='Task']/*[local-name()='Actions']/*[local-name()='Exec']");
			if (exec == null || document.SelectNodes("/*[local-name()='Task']/*[local-name()='Actions']/*[local-name()='Exec']").Count != 1) throw new IOException("The startup task has unsupported actions.");
			foreach (string name in new[] { "Command", "Arguments", "WorkingDirectory" })
			{
				XmlNode node = exec.SelectSingleNode("*[local-name()='" + name + "']");
				if (node == null) { node = document.CreateElement(name, exec.NamespaceURI); exec.AppendChild(node); }
				node.InnerText = name == "Command" ? executable : name == "Arguments" ? BuildStartupArguments(dataDirectory) : Path.GetDirectoryName(executable);
			}
			return document.OuterXml;
		}

		internal static void RetargetStartupTaskForUpdate(string originalXml, string executable, string dataDirectory, string transactionDirectory, out string installedXml)
		{
			installedXml = null;
			if (originalXml == null) return;
			lock (startupGate)
			{
				string current = CaptureStartupTaskForUpdate();
				if (current == null || !SameUpdateTaskXml(current, originalXml)) throw new IOException("The startup task changed while the update was prepared.");
				string updated = BuildStartupTaskUpdateXml(originalXml, executable, dataDirectory);
				installedXml = updated;
				RestoreStartupTaskForUpdate(updated, transactionDirectory);
				if (!StartupTaskMatchesUpdate(originalXml, executable, dataDirectory)) throw new IOException("The updated startup task could not be verified.");
				string captured = CaptureStartupTaskForUpdate();
				if (captured != null) installedXml = captured;
				Settings.SaveStr("AutostartExe", executable);
			}
		}

		internal static bool SameUpdateTaskXml(string first, string second)
		{
			try
			{
				XmlDocument left = new XmlDocument(), right = new XmlDocument(); left.XmlResolver = null; right.XmlResolver = null;
				using (XmlReader reader = XmlReader.Create(new StringReader(first), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null })) left.Load(reader);
				using (XmlReader reader = XmlReader.Create(new StringReader(second), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null })) right.Load(reader);
				return left.DocumentElement.OuterXml == right.DocumentElement.OuterXml;
			}
			catch { return false; }
		}

		internal static bool RestoreStartupTaskIfStillOwned(string originalXml, string installedXml, string transactionDirectory)
		{
			if (installedXml == null) return true;
			lock (startupGate)
			{
				string current = CaptureStartupTaskForUpdate();
				if (current == null || !SameUpdateTaskXml(current, installedXml)) return false;
				RestoreStartupTaskForUpdate(originalXml, transactionDirectory);
				return true;
			}
		}

		internal static void RestoreStartupTaskForUpdate(string xml, string transactionDirectory)
		{
			if (xml == null) return;
			lock (startupGate)
			{
				string path = Path.Combine(transactionDirectory, "startup-task.xml");
				SelfUpdateInstaller.RejectRedirects(path);
				try
				{
					File.WriteAllText(path, xml, Encoding.Unicode);
					if (Run("/Create /F /TN PaviseAuto /XML " + QuoteCommandArgument(path)) != 0) throw new IOException("The startup task could not be written. " + LastSchtasksError);
					cachedExists = 1; cachedEnabled = StartupTaskEnabled(xml) ? 1 : 0;
					Settings.SaveStr("AutostartExe", ParseTaskCommandXml(xml));
				}
				finally { try { File.Delete(path); } catch { } }
			}
		}

		internal static bool StartupTaskMatchesUpdate(string originalXml, string executable, string dataDirectory)
		{
			if (originalXml == null)
			{
#if PAVISE_SELFTEST && PAVISE_UPDATE_INSTALLER_CHECKS
				if (SelfUpdateInstaller.FixtureRoot != null) return true;
#endif
				return ReadStartupTaskPresence() == false;
			}
			lock (startupGate)
			{
				string xml;
				if (RunCore("/Query /TN PaviseAuto /XML", true, out xml) != 0) return false;
				return string.Equals(ParseTaskCommandXml(xml), executable, StringComparison.OrdinalIgnoreCase)
					&& string.Equals(ParseTaskArgumentsXml(xml), BuildStartupArguments(dataDirectory), StringComparison.OrdinalIgnoreCase)
					&& StartupTaskEnabled(xml) == StartupTaskEnabled(originalXml)
					&& StartupPayload.IsVerifiedExecutable(executable, StartupPayload.ManagedRoot);
			}
		}

		internal static bool TryReadTaskTarget(out string command, out string arguments)
		{
			command = null;
			arguments = null;
			string stdout;
			if (RunCore("/Query /TN PaviseAuto /XML", true, out stdout) != 0)
			{
				return false;
			}
			if (!StartupTaskEnabled(stdout))
			{
				return false;
			}
			command = ParseTaskCommandXml(stdout);
			arguments = ParseTaskArgumentsXml(stdout);
			if (!string.IsNullOrWhiteSpace(command))
			{
				return arguments != null;
			}
			return false;
		}

		internal static bool IsCurrentTaskTarget(string currentExecutable, string dataPath, string taskExecutable, string taskArguments)
		{
			try
			{
				return IsCurrentTaskTarget(currentExecutable, dataPath, taskExecutable, taskArguments, StartupPayload.ManagedRoot);
			}
			catch
			{
				return false;
			}
		}

		internal static bool IsCurrentTaskTarget(string currentExecutable, string dataPath, string taskExecutable, string taskArguments, string managedRoot)
		{
			try
			{
				string expectedExecutable = StartupPayload.GetExpectedExecutable(currentExecutable, managedRoot);
				return !NeedsStartupTaskRefresh(expectedExecutable, taskExecutable) && StartupPayload.IsVerifiedExecutable(taskExecutable, managedRoot) && string.Equals(BuildStartupArguments(dataPath), taskArguments, StringComparison.OrdinalIgnoreCase);
			}
			catch
			{
				return false;
			}
		}

		internal static string ParseTaskArgumentsXml(string xml)
		{
			if (string.IsNullOrWhiteSpace(xml))
			{
				return null;
			}
			try
			{
				XmlDocument xmlDocument = new XmlDocument();
				xmlDocument.XmlResolver = null;
				xmlDocument.LoadXml(xml.TrimStart('\ufeff'));
				XmlNode xmlNode = xmlDocument.SelectSingleNode("/*[local-name()='Task']/*[local-name()='Actions']/*[local-name()='Exec']");
				if (xmlNode == null)
				{
					return null;
				}
				XmlNode xmlNode2 = xmlNode.SelectSingleNode("*[local-name()='Arguments']");
				return (xmlNode2 == null) ? "" : (xmlNode2.InnerText ?? "").Trim();
			}
			catch
			{
				return null;
			}
		}

		internal static string ParseTaskCommandXml(string xml)
		{
			if (string.IsNullOrWhiteSpace(xml))
			{
				return null;
			}
			try
			{
				XmlDocument xmlDocument = new XmlDocument();
				xmlDocument.XmlResolver = null;
				xmlDocument.LoadXml(xml.TrimStart('\ufeff'));
				XmlNode xmlNode = xmlDocument.SelectSingleNode("/*[local-name()='Task']/*[local-name()='Actions']/*[local-name()='Exec']/*[local-name()='Command']");
				if (xmlNode == null)
				{
					return null;
				}
				string text = (xmlNode.InnerText ?? "").Trim().Trim('"');
				return (text.Length == 0) ? null : text;
			}
			catch
			{
				return null;
			}
		}

		public static int Run(string arguments)
		{
			string stdout;
			return RunCore(arguments, false, out stdout);
		}

		private static int RunCore(string arguments, bool capture, out string stdout)
		{
			stdout = null;
			lastSchtasksError = null;
			try
			{
				ProcessStartInfo processStartInfo = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), arguments);
				processStartInfo.CreateNoWindow = true;
				processStartInfo.UseShellExecute = false;
				processStartInfo.RedirectStandardOutput = capture;
				processStartInfo.RedirectStandardError = true;
				using (Process process = Process.Start(processStartInfo))
				{
					StringBuilder buf = new StringBuilder();
					StringBuilder err = new StringBuilder();
					if (capture)
					{
						process.OutputDataReceived += delegate(object s, DataReceivedEventArgs e)
						{
							if (e.Data != null)
							{
								lock (buf)
								{
									buf.AppendLine(e.Data);
								}
							}
						};
						process.BeginOutputReadLine();
					}
					process.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e)
					{
						if (e.Data != null)
						{
							lock (err)
							{
								err.AppendLine(e.Data);
							}
						}
					};
					process.BeginErrorReadLine();
					if (!process.WaitForExit(15000))
					{
						try
						{
							process.Kill();
						}
						catch
						{
						}
						lastSchtasksError = Lang.T("t.taskhelper.15");
						return -1;
					}
					process.WaitForExit();
					if (capture)
					{
						lock (buf)
						{
							stdout = buf.ToString();
						}
					}
					lock (err)
					{
						lastSchtasksError = CondenseError(err.ToString());
					}
					return process.ExitCode;
				}
			}
			catch (Exception ex)
			{
				lastSchtasksError = ex.Message;
				return -1;
			}
		}

		private static string CondenseError(string raw)
		{
			if (string.IsNullOrEmpty(raw))
			{
				return null;
			}
			string[] array = raw.Split(new char[2] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
			List<string> list = new List<string>();
			string[] array2 = array;
			foreach (string text in array2)
			{
				string text2 = text.Trim();
				if (text2.Length > 0)
				{
					list.Add(text2);
				}
			}
			if (list.Count == 0)
			{
				return null;
			}
			string text3 = string.Join(" ", list.ToArray());
			if (text3.Length <= 240)
			{
				return text3;
			}
			return text3.Substring(0, 240);
		}
	}
}
