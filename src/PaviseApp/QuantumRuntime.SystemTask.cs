using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml;

namespace PaviseApp
{
	internal static partial class QuantumRuntime
	{
		private const string TaskPrefix = "Pavise.Quantum.Once.";
		private const string TaskSecurity = "D:P(A;;GA;;;SY)(A;;GA;;;BA)";

		internal static string BuildHelperScript(uint value, long deadlineTicks)
		{
			if (value > 63 || deadlineTicks <= 0) throw new ArgumentOutOfRangeException();
			// The SYSTEM helper accepts only two validated invariant integers.
			return "$ErrorActionPreference='Stop';try { Add-Type -TypeDefinition @'\n"
				+ "using System;using System.Runtime.InteropServices;public static class PaviseQuantumOnce {"
				+ "[DllImport(\"ntdll.dll\",ExactSpelling=true)]static extern int NtSetSystemInformation(int c,ref uint v,uint n);"
				+ "[DllImport(\"ntdll.dll\",ExactSpelling=true)]static extern int RtlAdjustPrivilege(uint p,[MarshalAs(UnmanagedType.U1)]bool e,[MarshalAs(UnmanagedType.U1)]bool t,[MarshalAs(UnmanagedType.U1)]out bool o);"
				+ "public static int Apply(uint v){bool old;int p=RtlAdjustPrivilege(7,true,false,out old);if(p!=0)return p;try{return NtSetSystemInformation(39,ref v,4);}finally{bool x;if(RtlAdjustPrivilege(7,old,false,out x)!=0)throw new InvalidOperationException();}}}\n'@;"
				+ "if([DateTime]::UtcNow.Ticks -gt " + deadlineTicks.ToString(CultureInfo.InvariantCulture) + "L){exit 1460};"
				+ "$s=[PaviseQuantumOnce]::Apply([uint32]" + value.ToString(CultureInfo.InvariantCulture) + ");exit $s"
				+ "}catch{exit 1359}";
		}

		internal static string BuildTaskXml(string encodedScript)
		{
			if (string.IsNullOrEmpty(encodedScript) || encodedScript.Length > 16000)
				throw new ArgumentException("Invalid fixed helper payload");
			// SYSTEM execution is restricted to BuildHelperScript output.
			Convert.FromBase64String(encodedScript);
			string command = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell\\v1.0\\powershell.exe");
			return "<?xml version=\"1.0\" encoding=\"UTF-16\"?><Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">"
				+ "<RegistrationInfo><Description>Pavise one-shot online quantum update</Description></RegistrationInfo><Triggers/>"
				+ "<Principals><Principal id=\"System\"><UserId>S-1-5-18</UserId><RunLevel>HighestAvailable</RunLevel></Principal></Principals>"
				+ "<Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>"
				+ "<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><AllowHardTerminate>true</AllowHardTerminate><StartWhenAvailable>false</StartWhenAvailable>"
				+ "<RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable><AllowStartOnDemand>true</AllowStartOnDemand><Enabled>true</Enabled><Hidden>true</Hidden>"
				+ "<RunOnlyIfIdle>false</RunOnlyIfIdle><WakeToRun>false</WakeToRun><ExecutionTimeLimit>PT20S</ExecutionTimeLimit><Priority>7</Priority></Settings>"
				+ "<Actions Context=\"System\"><Exec><Command>" + SecurityElement.Escape(command)
				+ "</Command><Arguments>-NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand " + encodedScript
				+ "</Arguments><WorkingDirectory>" + SecurityElement.Escape(Environment.SystemDirectory) + "</WorkingDirectory></Exec></Actions></Task>";
		}

		private static object Call(object target, string method, params object[] args)
		{
			return target.GetType().InvokeMember(method, BindingFlags.InvokeMethod, null, target, args);
		}
		private static object Property(object target, string property)
		{
			return target.GetType().InvokeMember(property, BindingFlags.GetProperty, null, target, null);
		}
		private static void Release(object value)
		{
			if (value != null && Marshal.IsComObject(value)) try { Marshal.ReleaseComObject(value); } catch { }
		}
		private static object ConnectScheduler()
		{
			object service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", true));
			try { Call(service, "Connect", Type.Missing, Type.Missing, Type.Missing, Type.Missing); return service; }
			catch { Release(service); throw; }
		}
		private static bool Terminal(object task)
		{
			int state = Convert.ToInt32(Property(task, "State"), CultureInfo.InvariantCulture);
			if (state != 1 && state != 3) return false;
			object instances = null;
			try
			{
				instances = Call(task, "GetInstances", 0);
				return Convert.ToInt32(Property(instances, "Count"), CultureInfo.InvariantCulture) == 0;
			}
			finally { Release(instances); }
		}
		private static bool StopAndWait(object task)
		{
			try { if (!Terminal(task)) Call(task, "Stop", 0); } catch { }
			Stopwatch clock = Stopwatch.StartNew();
			while (clock.ElapsedMilliseconds < 3000)
			{
				try { if (Terminal(task)) return true; } catch { return false; }
				Thread.Sleep(50);
			}
			return false;
		}

		internal static bool HasCompletedRun(bool terminal, int status, DateTime beforeRun, DateTime afterRun)
		{
			// A run is acknowledged only by a fresh task result, not idle state or the default zero.
			return terminal && afterRun > beforeRun && status != 0x41303 && status != 0x41301;
		}

		internal static bool IsOwnedTaskName(string name)
		{
			Guid nonce;
			return name != null && name.StartsWith(TaskPrefix, StringComparison.Ordinal)
				&& Guid.TryParseExact(name.Substring(TaskPrefix.Length), "N", out nonce) && nonce != Guid.Empty;
		}

		internal static bool OwnsTaskXml(string xml)
		{
			if (string.IsNullOrEmpty(xml) || xml.Length > 24000) return false;
			try
			{
				var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit,
					XmlResolver = null, MaxCharactersInDocument = 24000 };
				var document = new XmlDocument { XmlResolver = null };
				using (var reader = XmlReader.Create(new StringReader(xml), settings)) document.Load(reader);
				var ns = new XmlNamespaceManager(document.NameTable);
				ns.AddNamespace("t", "http://schemas.microsoft.com/windows/2004/02/mit/task");
				XmlNode principal = document.SelectSingleNode("/t:Task/t:Principals/t:Principal", ns);
				XmlNode actions = document.SelectSingleNode("/t:Task/t:Actions", ns);
				if (principal == null || actions == null || principal.Attributes["id"] == null
					|| actions.Attributes["Context"] == null
					|| principal.Attributes["id"].Value != actions.Attributes["Context"].Value
					|| document.SelectNodes("/t:Task/t:Principals/*", ns).Count != 1
					|| actions.ChildNodes.Count != 1 || actions.FirstChild.LocalName != "Exec"
					|| document.SelectNodes("/t:Task/t:Triggers/*", ns).Count != 0) return false;
				Func<string, string> func = path => { XmlNode node = document.SelectSingleNode(path, ns); return node == null ? "" : node.InnerText; };
				if (func("/t:Task/t:Principals/t:Principal/t:UserId") != "S-1-5-18"
					|| func("/t:Task/t:Principals/t:Principal/t:RunLevel") != "HighestAvailable"
					|| func("/t:Task/t:RegistrationInfo/t:Description") != "Pavise one-shot online quantum update"
					|| func("/t:Task/t:Settings/t:Hidden") != "true"
					|| func("/t:Task/t:Settings/t:ExecutionTimeLimit") != "PT20S") return false;
				string logon = func("/t:Task/t:Principals/t:Principal/t:LogonType");
				if (logon.Length != 0 && logon != "ServiceAccount") return false;
				string expectedCommand = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell\\v1.0\\powershell.exe");
				if (!string.Equals(func("/t:Task/t:Actions/t:Exec/t:Command"), expectedCommand, StringComparison.OrdinalIgnoreCase)
					|| !string.Equals(func("/t:Task/t:Actions/t:Exec/t:WorkingDirectory"), Environment.SystemDirectory, StringComparison.OrdinalIgnoreCase)) return false;
				const string prefix = "-NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand ";
				string arguments = func("/t:Task/t:Actions/t:Exec/t:Arguments");
				if (!arguments.StartsWith(prefix, StringComparison.Ordinal)) return false;
				string encoded = arguments.Substring(prefix.Length);
				byte[] bytes = Convert.FromBase64String(encoded);
				if (bytes.Length > 12000 || encoded != Convert.ToBase64String(bytes)) return false;
				string script = Encoding.Unicode.GetString(bytes);
				Match numbers = Regex.Match(script, @"if\(\[DateTime\]::UtcNow\.Ticks -gt ([0-9]+)L\)\{exit 1460\};\$s=\[PaviseQuantumOnce\]::Apply\(\[uint32\]([0-9]+)\);exit \$s");
				uint value; long deadline;
				return numbers.Success && uint.TryParse(numbers.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out value)
					&& value <= 63 && long.TryParse(numbers.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out deadline)
					&& deadline > 0 && string.Equals(script, BuildHelperScript(value, deadline), StringComparison.Ordinal);
			}
			catch { return false; }
		}

		private static bool ReapOwnedTasks()
		{
			object service = null, folder = null, tasks = null;
			try
			{
				service = ConnectScheduler(); folder = Call(service, "GetFolder", "\\");
				tasks = Call(folder, "GetTasks", 1); // Include our hidden one-shot tasks.
				int count = Convert.ToInt32(Property(tasks, "Count"), CultureInfo.InvariantCulture);
				if (count < 0 || count > 10000) return false;
				// Enumerate backwards because successful deletion changes the collection.
				for (int i = count; i > 0; i--)
				{
					object task = null;
					try
					{
						task = tasks.GetType().InvokeMember("Item", BindingFlags.GetProperty, null, tasks, new object[] { i });
						string name = Convert.ToString(Property(task, "Name"), CultureInfo.InvariantCulture);
						if (!IsOwnedTaskName(name)) continue;
						if (!OwnsTaskXml(Convert.ToString(Property(task, "Xml"), CultureInfo.InvariantCulture))) return false;
						if (!StopAndWait(task)) return false;
						Call(folder, "DeleteTask", name, 0);
					}
					finally { Release(task); }
				}
				return true;
			}
			catch { return false; }
			finally { Release(tasks); Release(folder); Release(service); }
		}

		private static QuantumRuntimeResult ApplyViaSystemTask(uint value)
		{
			object service = null, folder = null, task = null, instance = null;
			string name = TaskPrefix + Guid.NewGuid().ToString("N");
			bool created = false, terminal = false;
			try
			{
				service = ConnectScheduler(); folder = Call(service, "GetFolder", "\\");
				string script = BuildHelperScript(value, DateTime.UtcNow.AddSeconds(12).Ticks);
				string xml = BuildTaskXml(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
				// TASK_CREATE only; never update or overwrite an existing task.
				task = Call(folder, "RegisterTask", name, xml, 2, "SYSTEM", null, 5, TaskSecurity);
				created = true;
				DateTime beforeRun = Convert.ToDateTime(Property(task, "LastRunTime"), CultureInfo.InvariantCulture);
				instance = Call(task, "Run", new object[] { null });
				Stopwatch clock = Stopwatch.StartNew();
				while (clock.ElapsedMilliseconds < 15000)
				{
					int status = unchecked((int)Convert.ToInt64(Property(task, "LastTaskResult"), CultureInfo.InvariantCulture));
					DateTime afterRun = Convert.ToDateTime(Property(task, "LastRunTime"), CultureInfo.InvariantCulture);
					// HAS_NOT_RUN is not completion; require terminal state and a completed exit code.
					if (HasCompletedRun(Terminal(task), status, beforeRun, afterRun))
					{
						terminal = true;
						return DecodeStatus(status, true);
					}
					Thread.Sleep(50);
				}
				terminal = StopAndWait(task);
				return new QuantumRuntimeResult(terminal ? QuantumRuntimeState.Failed : QuantumRuntimeState.Uncertain,
					1460, "Online quantum helper timed out", true);
			}
			catch (Exception error)
			{
				terminal = task == null || StopAndWait(task);
				return new QuantumRuntimeResult(terminal ? QuantumRuntimeState.Unavailable : QuantumRuntimeState.Uncertain,
					0, "Online quantum helper unavailable: " + error.GetType().Name, true);
			}
			finally
			{
				if (created)
				{
					if (terminal) try { Call(folder, "DeleteTask", name, 0); } catch { }
					// Reap unfinished one-shot tasks before the next operation, including after crashes.
				}
				Release(instance); Release(task); Release(folder); Release(service);
			}
		}
	}
}
