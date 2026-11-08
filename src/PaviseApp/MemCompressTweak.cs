using System;
using System.Runtime.InteropServices;

namespace PaviseApp
{
	internal static class MemCompressTweak
	{
		private const string OnKey = "MemCompressOffByPavise";

		private const string SnapKey = "PrevMMAgent";

		private const string RepairKey = "PrevMemoryCompressionRepairV1";

		internal const string UnavailableOutput = "PAVISE_MMAGENT_UNAVAILABLE";

		private const string ModuleScript = "try { Import-Module -Name ([IO.Path]::Combine($PSHOME, 'Modules', 'MMAgent', 'MMAgent.psd1')) -ErrorAction Stop -WarningAction SilentlyContinue | Out-Null } catch { Write-Output 'PAVISE_MMAGENT_UNAVAILABLE'; exit 0 }\r\n";

		internal const string QueryScript = "try { Import-Module -Name ([IO.Path]::Combine($PSHOME, 'Modules', 'MMAgent', 'MMAgent.psd1')) -ErrorAction Stop -WarningAction SilentlyContinue | Out-Null } catch { Write-Output 'PAVISE_MMAGENT_UNAVAILABLE'; exit 0 }\r\n$mmQuery = Get-Command -Name 'MMAgent\\Get-MMAgent' -ErrorAction SilentlyContinue;\r\nif ($null -eq $mmQuery) { Write-Output 'PAVISE_MMAGENT_UNAVAILABLE'; exit 0 }\r\n$m = & $mmQuery -ErrorAction Stop;\r\nif ($m.MemoryCompression -isnot [bool] -or $m.PageCombining -isnot [bool]) { Write-Output 'PAVISE_MMAGENT_UNAVAILABLE'; exit 0 }\r\nWrite-Output ($m.MemoryCompression.ToString() + ',' + $m.PageCombining.ToString())";

		private static readonly object lk = new object();

		private static bool commandsUnavailable;

		public static bool EnabledByPavise
		{
			get
			{
				return Settings.Load("MemCompressOffByPavise", false);
			}
		}

		public static bool OwnsState
		{
			get
			{
				if (!EnabledByPavise && Settings.LoadStr("PrevMMAgent", "").Length <= 0)
				{
					return Settings.LoadStr("PrevMemoryCompressionRepairV1", "").Length > 0;
				}
				return true;
			}
		}

		[DllImport("kernel32.dll")]
		private static extern bool GetPhysicallyInstalledSystemMemory(out ulong totalMemoryInKilobytes);

		internal static bool TryInstalledMemory(out ulong installedKb)
		{
			installedKb = 0uL;
			try
			{
				return GetPhysicallyInstalledSystemMemory(out installedKb) && installedKb != 0;
			}
			catch
			{
				return false;
			}
		}

		internal static bool ParseState(string output, out bool compression, out bool combining)
		{
			compression = (combining = false);
			if (string.IsNullOrEmpty(output))
			{
				return false;
			}
			string[] trimParts = output.Trim().Split(',');
			if (trimParts.Length < 2)
			{
				return false;
			}
			bool tryParse = bool.TryParse(trimParts[0].Trim(), out compression);
			bool result = bool.TryParse(trimParts[1].Trim(), out combining);
			if (tryParse)
			{
				return result;
			}
			return false;
		}

		internal static bool ParseSnapshot(string snapshot, out bool compression, out bool combining)
		{
			compression = (combining = false);
			if (string.IsNullOrEmpty(snapshot))
			{
				return false;
			}
			string[] snapshotParts = snapshot.Split(',');
			if (snapshotParts.Length != 2 || (snapshotParts[0] != "0" && snapshotParts[0] != "1") || (snapshotParts[1] != "0" && snapshotParts[1] != "1"))
			{
				return false;
			}
			compression = snapshotParts[0] == "1";
			combining = snapshotParts[1] == "1";
			return true;
		}

		internal static bool SnapshotRestored(string snapshot, bool compression, bool combining)
		{
			bool compression2;
			bool combining2;
			if (ParseSnapshot(snapshot, out compression2, out combining2) && (!compression2 || compression))
			{
				if (combining2)
				{
					return combining;
				}
				return true;
			}
			return false;
		}

		internal static string RestoreArguments(string snapshot)
		{
			bool compression;
			bool combining;
			if (!ParseSnapshot(snapshot, out compression, out combining))
			{
				return "";
			}
			string restoreArgumentsResult = "";
			if (compression)
			{
				restoreArgumentsResult += " -MemoryCompression";
			}
			if (combining)
			{
				restoreArgumentsResult += " -PageCombining";
			}
			return restoreArgumentsResult;
		}

		internal static bool QueryState(out bool compression, out bool combining)
		{
			compression = (combining = false);
			lock (lk)
			{
				string output;
				if (!ExecuteCapabilityScript("try { Import-Module -Name ([IO.Path]::Combine($PSHOME, 'Modules', 'MMAgent', 'MMAgent.psd1')) -ErrorAction Stop -WarningAction SilentlyContinue | Out-Null } catch { Write-Output 'PAVISE_MMAGENT_UNAVAILABLE'; exit 0 }\r\n$mmQuery = Get-Command -Name 'MMAgent\\Get-MMAgent' -ErrorAction SilentlyContinue;\r\nif ($null -eq $mmQuery) { Write-Output 'PAVISE_MMAGENT_UNAVAILABLE'; exit 0 }\r\n$m = & $mmQuery -ErrorAction Stop;\r\nif ($m.MemoryCompression -isnot [bool] -or $m.PageCombining -isnot [bool]) { Write-Output 'PAVISE_MMAGENT_UNAVAILABLE'; exit 0 }\r\nWrite-Output ($m.MemoryCompression.ToString() + ',' + $m.PageCombining.ToString())", "mmagent-query", out output))
				{
					return false;
				}
				return ParseState(output, out compression, out combining);
			}
		}

		private static bool ExecuteCapabilityScript(string script, string label, out string output)
		{
			output = "";
			if (commandsUnavailable)
			{
				return false;
			}
			if (!PsRunner.Run(script, label, 20000, out output))
			{
				return false;
			}
			if (string.Equals((output ?? "").Trim(), "PAVISE_MMAGENT_UNAVAILABLE", StringComparison.Ordinal))
			{
				commandsUnavailable = true;
				return false;
			}
			return true;
		}

		private static bool RunCommand(string command, string label, out string output)
		{
			int matchIndex = command.IndexOf(' ');
			string commandSnapshot = ((matchIndex < 0) ? command : command.Substring(0, matchIndex));
			if (commandSnapshot != "Disable-MMAgent" && commandSnapshot != "Enable-MMAgent")
			{
				throw new ArgumentException("Unexpected MMAgent command");
			}
			string substringResult = ((matchIndex < 0) ? "" : command.Substring(matchIndex));
			string script = "try { Import-Module -Name ([IO.Path]::Combine($PSHOME, 'Modules', 'MMAgent', 'MMAgent.psd1')) -ErrorAction Stop -WarningAction SilentlyContinue | Out-Null } catch { Write-Output 'PAVISE_MMAGENT_UNAVAILABLE'; exit 0 }\r\n$mmCommand = Get-Command -Name 'MMAgent\\" + commandSnapshot + "' -ErrorAction SilentlyContinue;\r\nif ($null -eq $mmCommand) { Write-Output 'PAVISE_MMAGENT_UNAVAILABLE'; exit 0 }\r\n& $mmCommand" + substringResult + " -ErrorAction Stop";
			return ExecuteCapabilityScript(script, label, out output);
		}

		public static bool EnableCompression()
		{
			lock (lk)
			{
				bool compression;
				bool combining;
				if (!QueryState(out compression, out combining))
				{
					Logger.Warn(Lang.T("log.memcompress.1"));
					return false;
				}
				if (compression)
				{
					return true;
				}
				string value;
				if (!Settings.TryLoadStr("PrevMemoryCompressionRepairV1", out value) || (value.Length > 0 && value != "0"))
				{
					Logger.Log(Lang.T("log.memcompress.2"));
					return false;
				}
				bool matchesLength = value.Length == 0;
				if (matchesLength && (!Settings.SaveStr("PrevMemoryCompressionRepairV1", "0") || Settings.LoadStr("PrevMemoryCompressionRepairV1", "") != "0"))
				{
					Logger.Log(Lang.T("log.memcompress.2"));
					return false;
				}
				string output;
				RunCommand("Enable-MMAgent -MemoryCompression", "mmagent-enable-compression", out output);
				bool compression2;
				bool combining2;
				bool state = QueryState(out compression2, out combining2);
				if (state && compression2)
				{
					Logger.Log(Lang.T("audit.mem.enabled"));
					return true;
				}
				if (state && !compression2 && matchesLength && !ClearRepairReceipt())
				{
					Logger.Log(Lang.T("log.memcompress.5"));
				}
				Logger.Warn(Lang.T("audit.mem.failed"));
				return false;
			}
		}

		private static bool ClearRepairReceipt()
		{
			string value;
			if (Settings.SaveStr("PrevMemoryCompressionRepairV1", "") && Settings.TryLoadStr("PrevMemoryCompressionRepairV1", out value))
			{
				return value.Length == 0;
			}
			return false;
		}

		private static bool RestoreCompressionRepair()
		{
			string value;
			if (!Settings.TryLoadStr("PrevMemoryCompressionRepairV1", out value))
			{
				return false;
			}
			if (value.Length == 0)
			{
				return true;
			}
			if (value != "0")
			{
				return false;
			}
			bool compression;
			bool combining;
			if (!QueryState(out compression, out combining))
			{
				return false;
			}
			if (compression)
			{
				string output;
				RunCommand("Disable-MMAgent -MemoryCompression", "mmagent-restore-compression", out output);
				if (!QueryState(out compression, out combining) || compression)
				{
					return false;
				}
			}
			return ClearRepairReceipt();
		}

		private static bool RestoreSnapshot(string snapshot)
		{
			bool compression;
			bool combining;
			if (!ParseSnapshot(snapshot, out compression, out combining))
			{
				return false;
			}
			string restoreArgumentsResult = RestoreArguments(snapshot);
			if (restoreArgumentsResult.Length == 0)
			{
				return true;
			}
			string output;
			RunCommand("Enable-MMAgent" + restoreArgumentsResult, "mmagent-restore", out output);
			bool compression2;
			bool combining2;
			if (QueryState(out compression2, out combining2))
			{
				return SnapshotRestored(snapshot, compression2, combining2);
			}
			return false;
		}

		private static bool ClearOwnership()
		{
			bool memCompressOffByPaviseEnabled = Settings.Save("MemCompressOffByPavise", false);
			bool prevMMAgentEnabled = Settings.SaveStr("PrevMMAgent", "");
			string value;
			if (memCompressOffByPaviseEnabled && prevMMAgentEnabled && !Settings.Load("MemCompressOffByPavise", true) && Settings.TryLoadStr("PrevMMAgent", out value))
			{
				return value.Length == 0;
			}
			return false;
		}

		public static bool Restore()
		{
			lock (lk)
			{
				if (!RestoreCompressionRepair())
				{
					Logger.Log(Lang.T("log.memcompress.5"));
					return false;
				}
				string prevMMAgentValue = Settings.LoadStr("PrevMMAgent", "");
				if (prevMMAgentValue.Length > 0 && !RestoreSnapshot(prevMMAgentValue))
				{
					Logger.Log(Lang.T("log.memcompress.5"));
					return false;
				}
				if (!ClearOwnership())
				{
					Logger.Log(Lang.T("log.memcompress.5"));
					return false;
				}
				Logger.Log(Lang.T("log.memcompress.6"));
				return true;
			}
		}
	}
}
