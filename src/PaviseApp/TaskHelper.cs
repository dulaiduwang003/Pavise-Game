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
			bool matchesRun = Run("/Query /TN PaviseAuto") == 0;
			cachedExists = (matchesRun ? 1 : 0);
			return matchesRun;
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
				bool matchesRunCore = RunCore("/Query /TN PaviseAuto /XML", true, out stdout) == 0;
				cachedExists = (matchesRunCore ? 1 : 0);
				bool matchesMatchesRunCore = matchesRunCore && StartupTaskEnabled(stdout);
				cachedEnabled = (matchesMatchesRunCore ? 1 : 0);
				return matchesMatchesRunCore;
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
			int startupTaskFromXml = CreateStartupTaskFromXml(executable, arguments);
			if (startupTaskFromXml != 0 && allowFallback)
			{
				startupTaskFromXml = Run("/Create /F /SC ONLOGON /RL HIGHEST /TN PaviseAuto /TR " + QuoteCommandArgument(BuildStartupCommandLine(executable, arguments)));
			}
			if (startupTaskFromXml == 0)
			{
				cachedExists = 1;
				cachedEnabled = 1;
				Settings.SaveStr("AutostartExe", executable);
				Settings.SaveStr("AutostartRepairFails", "0");
				RemoveStalePayloads(executable);
			}
			return startupTaskFromXml;
		}

		private static void RemoveStalePayloads(string executable)
		{
			try
			{
				if (!SelfUpdateInstaller.AllowStartupMaintenance(Application.ExecutablePath)) return;
				int cleanupResult = StartupPayload.Cleanup(StartupPayload.ManagedRoot, executable, Application.ExecutablePath);
				if (cleanupResult > 0)
				{
					Logger.Log(((Lang.Cur == 0) ? "已删除不再使用的自启副本 " : "Removed unused autostart copies: ") + cleanupResult);
				}
			}
			catch
			{
			}
		}

		private static int CreateStartupTaskFromXml(string executable, string arguments)
		{
			string combinedPath = null;
			try
			{
				string startupTaskXml = BuildStartupTaskXml(executable, arguments);
				if (startupTaskXml == null)
				{
					lastSchtasksError = Lang.T("t.taskhelper.14");
					return -1;
				}
				string path = (string.IsNullOrEmpty(Paths.Data) ? Path.GetTempPath() : Paths.Data);
				combinedPath = Path.Combine(path, "Pavise_" + Guid.NewGuid().ToString("N") + ".xml");
				using (FileStream fileStream = new FileStream(combinedPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
				{
					byte[] preamble = Encoding.Unicode.GetPreamble();
					byte[] bytes = Encoding.Unicode.GetBytes(startupTaskXml);
					fileStream.Write(preamble, 0, preamble.Length);
					fileStream.Write(bytes, 0, bytes.Length);
					fileStream.Flush();
				}
				return Run("/Create /F /TN PaviseAuto /XML \"" + combinedPath + "\"");
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
					if (combinedPath != null)
					{
						File.Delete(combinedPath);
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
			string value;
			try
			{
				using (WindowsIdentity windowsIdentity = WindowsIdentity.GetCurrent())
				{
					value = windowsIdentity.User.Value;
				}
			}
			catch
			{
				value = null;
			}
			if (string.IsNullOrEmpty(value))
			{
				value = Environment.UserDomainName + "\\" + Environment.UserName;
				if (value.Length <= 1)
				{
					return null;
				}
			}
			string xmlTextResult = XmlText(exePath);
			string nowText = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
			return "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n  <RegistrationInfo><URI>\\PaviseAuto</URI></RegistrationInfo>\r\n  <Principals><Principal id=\"Author\"><UserId>" + XmlText(value) + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>\r\n  <Settings><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><ExecutionTimeLimit>PT0S</ExecutionTimeLimit><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy></Settings>\r\n  <Triggers><LogonTrigger><StartBoundary>" + nowText + "</StartBoundary></LogonTrigger></Triggers>\r\n  <Actions Context=\"Author\"><Exec><Command>" + xmlTextResult + "</Command><Arguments>" + XmlText(arguments) + "</Arguments><WorkingDirectory>" + XmlText(Path.GetDirectoryName(exePath)) + "</WorkingDirectory></Exec></Actions>\r\n</Task>";
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
			int processedCount = 0;
			string valueOffset = value ?? "";
			foreach (char c in valueOffset)
			{
				switch (c)
				{
				case '\\':
					processedCount++;
					break;
				case '"':
					stringBuilder.Append('\\', processedCount * 2 + 1);
					stringBuilder.Append(c);
					processedCount = 0;
					break;
				default:
					stringBuilder.Append('\\', processedCount);
					processedCount = 0;
					stringBuilder.Append(c);
					break;
				}
			}
			stringBuilder.Append('\\', processedCount * 2);
			stringBuilder.Append('"');
			return stringBuilder.ToString();
		}

		internal static bool IsVolatileAutostartPath(string path)
		{
			if (string.IsNullOrEmpty(path))
			{
				return true;
			}
			string toLowerInvariantResult = path.Replace('/', '\\').ToLowerInvariant();
			if (!toLowerInvariantResult.Contains("\\temp\\") && !toLowerInvariantResult.Contains("\\tmp\\") && !toLowerInvariantResult.Contains("\\xwechat_files\\") && !toLowerInvariantResult.Contains("\\wechat files\\") && !toLowerInvariantResult.Contains("\\inetcache\\"))
			{
				return toLowerInvariantResult.Contains("\\$recycle.bin\\");
			}
			return true;
		}

		public static int DeleteStartupTask()
		{
			lock (startupGate)
			{
				int runResult = Run("/Delete /F /TN PaviseAuto");
				if (runResult == 0)
				{
					cachedExists = 0;
					cachedEnabled = 0;
					Settings.SaveStr("AutostartExe", "");
					RemoveStalePayloads(null);
				}
				return runResult;
			}
		}

		internal static bool RemoveStartupTaskForUninstall()
		{
			lastSchtasksError = null;
			lock (startupGate)
			{
				bool removeStartupTaskForUninstallSucceeded = RemoveStartupTaskForUninstall(ReadStartupTaskPresence, DeleteStartupTask);
				if (removeStartupTaskForUninstallSucceeded)
				{
					try
					{
						StartupPayload.Cleanup(StartupPayload.ManagedRoot, Application.ExecutablePath);
					}
					catch
					{
					}
				}
				return removeStartupTaskForUninstallSucceeded;
			}
		}

		internal static bool RemoveStartupTaskForUninstall(Func<bool?> readPresence, Func<int> remove)
		{
			try
			{
				bool? readPresenceResult = readPresence();
				if (!readPresenceResult.HasValue)
				{
					return false;
				}
				if (readPresenceResult.Value)
				{
					remove();
					readPresenceResult = readPresence();
					if (!readPresenceResult.HasValue || readPresenceResult.Value)
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
			object instance = null;
			object invokeMemberResult = null;
			object invokeMemberResult2 = null;
			try
			{
				Type typeFromProgID = Type.GetTypeFromProgID("Schedule.Service", true);
				instance = Activator.CreateInstance(typeFromProgID);
				typeFromProgID.InvokeMember("Connect", BindingFlags.InvokeMethod, null, instance, new object[4]
				{
					Type.Missing,
					Type.Missing,
					Type.Missing,
					Type.Missing
				});
				invokeMemberResult = typeFromProgID.InvokeMember("GetFolder", BindingFlags.InvokeMethod, null, instance, new object[1] { "\\" });
				try
				{
					invokeMemberResult2 = invokeMemberResult.GetType().InvokeMember("GetTask", BindingFlags.InvokeMethod, null, invokeMemberResult, new object[1] { "PaviseAuto" });
					return (invokeMemberResult2 == null) ? null : new bool?(true);
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
				object[] instanceItems = new object[3] { invokeMemberResult2, invokeMemberResult, instance };
				foreach (object instanceEntry in instanceItems)
				{
					if (instanceEntry != null && Marshal.IsComObject(instanceEntry))
					{
						try
						{
							Marshal.ReleaseComObject(instanceEntry);
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
					bool startupTaskEnabledSucceeded = StartupTaskEnabled(stdout);
					cachedEnabled = (startupTaskEnabledSucceeded ? 1 : 0);
					if (!startupTaskEnabledSucceeded)
					{
						return;
					}
					string taskCommandXml = ParseTaskCommandXml(stdout);
					if (string.IsNullOrWhiteSpace(taskCommandXml))
					{
						Logger.Warn(Lang.T("log.taskhelper.1"));
						return;
					}
					string a = ParseTaskArgumentsXml(stdout);
					if (ShouldPreserveLegacyTarget(executablePath, taskCommandXml, StartupPayload.ManagedRoot))
					{
						Logger.Log(Lang.T("log.taskhelper.2") + executablePath + Lang.T("log.taskhelper.3"));
						return;
					}
					string startupArguments = BuildStartupArguments(Paths.Data);
					string ensureResult = StartupPayload.Ensure(executablePath);
					bool isNotValuesEqual = !string.Equals(a, startupArguments, StringComparison.OrdinalIgnoreCase);
					bool startupTaskSettingsStaleSucceeded = StartupTaskSettingsStale(stdout);
					bool needsStartupTaskRefresh = NeedsStartupTaskRefresh(ensureResult, taskCommandXml);
					if (!needsStartupTaskRefresh && !isNotValuesEqual && !startupTaskSettingsStaleSucceeded)
					{
						Settings.SaveStr("AutostartExe", ensureResult);
						Settings.SaveStr("AutostartRepairFails", "0");
						RemoveStalePayloads(ensureResult);
					}
					else if (needsStartupTaskRefresh || isNotValuesEqual)
					{
						Logger.Log(needsStartupTaskRefresh ? (Lang.T("t.taskhelper.4") + (taskCommandXml ?? Lang.T("t.taskhelper.5")) + " " + ensureResult) : (Lang.T("t.taskhelper.6") + startupArguments + Lang.T("t.taskhelper.7")));
						int registerStartupTaskResult = RegisterStartupTask(ensureResult, startupArguments, true);
						if (registerStartupTaskResult != 0)
						{
							Logger.Warn((needsStartupTaskRefresh ? Lang.T("log.taskhelper.8") : Lang.T("log.taskhelper.9")) + registerStartupTaskResult + DescribeSchtasksError());
						}
					}
					else
					{
						RepairStartupTaskSettings(ensureResult, startupArguments);
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
			int processedCount = RepairFailCount();
			if (processedCount < 3)
			{
				int startupTaskFromXml = CreateStartupTaskFromXml(executable, arguments);
				if (startupTaskFromXml == 0)
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
					processedCount++;
					Settings.SaveStr("AutostartRepairFails", processedCount.ToString(CultureInfo.InvariantCulture));
					Logger.Warn((processedCount >= 3) ? (Lang.T("log.taskhelper.11") + startupTaskFromXml + DescribeSchtasksError() + Lang.T("t.taskhelper.12")) : (Lang.T("log.taskhelper.13") + startupTaskFromXml + DescribeSchtasksError()));
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
			string lastSchtasksErrorSnapshot = lastSchtasksError;
			if (!string.IsNullOrEmpty(lastSchtasksErrorSnapshot))
			{
				return Lang.T("t.taskhelper.16") + lastSchtasksErrorSnapshot;
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
				string trimTrimmed = (xmlNode.InnerText ?? "").Trim().Trim('"');
				return (trimTrimmed.Length == 0) ? null : trimTrimmed;
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
			string[] rawParts = raw.Split(new char[2] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
			List<string> list = new List<string>();
			string[] textEntries = rawParts;
			foreach (string entryText in textEntries)
			{
				string entryTextTrimmed = entryText.Trim();
				if (entryTextTrimmed.Length > 0)
				{
					list.Add(entryTextTrimmed);
				}
			}
			if (list.Count == 0)
			{
				return null;
			}
			string joinResult = string.Join(" ", list.ToArray());
			if (joinResult.Length <= 240)
			{
				return joinResult;
			}
			return joinResult.Substring(0, 240);
		}
	}
}
