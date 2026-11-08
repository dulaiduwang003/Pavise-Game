using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace PaviseApp
{
	internal static class LolRuntimeProcesses
	{
		private static readonly ProcessActionFailureBudget cleanupFailures = new ProcessActionFailureBudget();
		private enum SessionRelation
		{
			Unknown,
			Foreign,
			Owned
		}

		private const int ProcessTerminate = 1;

		private const int Synchronize = 1048576;

		private const uint WaitObject0 = 0u;

		private static readonly string[] WeGameNames = new string[8] { "wegame.exe", "wegame_env.exe", "wegameclient.exe", "pallas.exe", "rail.exe", "tcls_core.exe", "browser.exe", "crashpad_handler.exe" };

		private static readonly string[] DownloaderNames = new string[2] { "teniodl.exe", "wegameupdate.exe" };

		private static readonly string[] TencentSessionNames = new string[4] { "wegame_env.exe", "pallas.exe", "rail.exe", "tcls_core.exe" };

		private static readonly string[] ScanCandidateNames = new string[20]
		{
			"leagueclient", "leagueclientux", "leagueclientuxrender", "league of legends", "wegame", "wegame_env", "wegameclient", "pallas", "rail", "tcls_core",
			"browser", "crashpad_handler", "teniodl", "wegameupdate", "crossproxy", "lolaicoach", "aicoachapp", "icreatelol", "tqmcenter", "yxqxunyou"
		};

		private static readonly string[] CleanupCandidateNames = new string[16]
		{
			"wegame", "wegame_env", "wegameclient", "pallas", "rail", "tcls_core", "browser", "crashpad_handler", "teniodl", "wegameupdate",
			"crossproxy", "lolaicoach", "aicoachapp", "icreatelol", "tqmcenter", "yxqxunyou"
		};

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern bool TerminateProcess(IntPtr process, uint exitCode);

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

		internal static bool IsScanCandidateName(string name)
		{
			if (string.IsNullOrEmpty(name))
			{
				return false;
			}
			for (int i = 0; i < ScanCandidateNames.Length; i++)
			{
				if (string.Equals(name, ScanCandidateNames[i], StringComparison.OrdinalIgnoreCase))
				{
					return true;
				}
			}
			return false;
		}

		internal static bool IsCoreIdentityCandidateName(string name)
		{
			if (string.IsNullOrEmpty(name))
			{
				return false;
			}
			string a = (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name.Substring(0, name.Length - 4) : name);
			if (!string.Equals(a, "LeagueClient", StringComparison.OrdinalIgnoreCase) && !string.Equals(a, "LeagueClientUx", StringComparison.OrdinalIgnoreCase))
			{
				return string.Equals(a, "League of Legends", StringComparison.OrdinalIgnoreCase);
			}
			return true;
		}

		internal static bool IsCredentialSourceName(string name)
		{
			if (string.IsNullOrEmpty(name))
			{
				return false;
			}
			string a = (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name.Substring(0, name.Length - 4) : name);
			if (!string.Equals(a, "LeagueClient", StringComparison.OrdinalIgnoreCase))
			{
				return string.Equals(a, "LeagueClientUx", StringComparison.OrdinalIgnoreCase);
			}
			return true;
		}

		internal static bool IsWeGameDiscoveryCandidateName(string name)
		{
			if (string.IsNullOrEmpty(name))
			{
				return false;
			}
			string a = (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name.Substring(0, name.Length - 4) : name);
			if (!string.Equals(a, "wegame", StringComparison.OrdinalIgnoreCase) && !string.Equals(a, "wegame_env", StringComparison.OrdinalIgnoreCase))
			{
				return string.Equals(a, "wegameclient", StringComparison.OrdinalIgnoreCase);
			}
			return true;
		}

		internal static bool IsCleanupCandidateName(string name)
		{
			if (string.IsNullOrEmpty(name))
			{
				return false;
			}
			string a = (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name.Substring(0, name.Length - 4) : name);
			for (int i = 0; i < CleanupCandidateNames.Length; i++)
			{
				if (string.Equals(a, CleanupCandidateNames[i], StringComparison.OrdinalIgnoreCase))
				{
					return true;
				}
			}
			return false;
		}

		internal static bool IsExpectedSession(int expectedSession, int candidateSession)
		{
			if (expectedSession >= 0 && candidateSession >= 0)
			{
				return expectedSession == candidateSession;
			}
			return false;
		}

		internal static bool TryGetCurrentSessionId(out int sessionId)
		{
			sessionId = -1;
			try
			{
				using (Process process = Process.GetCurrentProcess())
				{
					sessionId = process.SessionId;
				}
				return sessionId >= 0;
			}
			catch
			{
				return false;
			}
		}

		private static bool TryGetVerifiedImagePath(Process process, int expectedSession, out string path, out SessionRelation sessionRelation)
		{
			path = null;
			sessionRelation = SessionRelation.Unknown;
			if (process == null || expectedSession < 0)
			{
				return false;
			}
			IntPtr intPtr = IntPtr.Zero;
			try
			{
				int id = process.Id;
				intPtr = Native.OpenProcess(1052672, false, id);
				if (intPtr == IntPtr.Zero)
				{
					int num;
					try
					{
						num = process.SessionId;
					}
					catch
					{
						num = -1;
					}
					if (num >= 0)
					{
						sessionRelation = ((!IsExpectedSession(expectedSession, num)) ? SessionRelation.Foreign : SessionRelation.Owned);
					}
					return false;
				}
				int sessionId;
				if (!Native.TryGetLiveProcessSessionId(intPtr, id, out sessionId))
				{
					return false;
				}
				sessionRelation = ((!IsExpectedSession(expectedSession, sessionId)) ? SessionRelation.Foreign : SessionRelation.Owned);
				if (sessionRelation != SessionRelation.Owned)
				{
					return false;
				}
				path = Native.ImagePath(intPtr);
				return !string.IsNullOrEmpty(path);
			}
			catch
			{
				return false;
			}
			finally
			{
				if (intPtr != IntPtr.Zero)
				{
					Native.CloseHandle(intPtr);
				}
			}
		}

		internal static bool TryGetOwnedImagePath(Process process, int expectedSession, out string path)
		{
			SessionRelation sessionRelation;
			return TryGetVerifiedImagePath(process, expectedSession, out path, out sessionRelation);
		}

		internal static bool IsOwnedCredentialSourceProcess(string lolRoot, int pid, int expectedSession)
		{
			return IsOwnedCredentialSourceProcess(lolRoot, pid, expectedSession, 0L);
		}

		internal static bool IsOwnedLcuListenerPort(string lolRoot, int port)
		{
			if (port <= 0)
			{
				return false;
			}
			int sessionId;
			if (!TryGetCurrentSessionId(out sessionId))
			{
				return false;
			}
			int pid;
			if (!LolNative.TryGetTcpListenerOwner(port, out pid))
			{
				return false;
			}
			return IsOwnedCredentialSourceProcess(lolRoot, pid, sessionId);
		}

		internal static bool IsOwnedCredentialSourceProcess(string lolRoot, int pid, int expectedSession, long expectedCreation)
		{
			if (pid <= 0 || expectedSession < 0 || !LolInstallDiscovery.IsValidLolRoot(lolRoot))
			{
				return false;
			}
			IntPtr intPtr = Native.OpenProcess(1052672, false, pid);
			if (intPtr == IntPtr.Zero)
			{
				return false;
			}
			try
			{
				int sessionId;
				if (!Native.TryGetLiveProcessSessionId(intPtr, pid, out sessionId) || !IsExpectedSession(expectedSession, sessionId))
				{
					return false;
				}
				long creation;
				long cpu;
				ulong io;
				if (expectedCreation > 0 && (!Native.QueryProcessSample(intPtr, out creation, out cpu, out io) || !CredentialCreationMatches(expectedCreation, creation)))
				{
					return false;
				}
				string path = Native.ImagePath(intPtr);
				return IsUnder(path, lolRoot) && IsCredentialSourceName(Path.GetFileName(path));
			}
			finally
			{
				Native.CloseHandle(intPtr);
			}
		}

		internal static bool CredentialCreationMatches(long expectedCreation, long actualCreation)
		{
			if (expectedCreation <= 0 || actualCreation <= 0)
			{
				return false;
			}
			long num = ((expectedCreation >= actualCreation) ? (expectedCreation - actualCreation) : (actualCreation - expectedCreation));
			return num <= 10000;
		}

		internal static bool HasExclusiveCredentialSourceSession(string lolRoot)
		{
			if (!LolInstallDiscovery.IsValidLolRoot(lolRoot))
			{
				return false;
			}
			int sessionId;
			if (!TryGetCurrentSessionId(out sessionId))
			{
				return false;
			}
			bool result = false;
			string[] array = new string[2] { "LeagueClient", "LeagueClientUx" };
			for (int i = 0; i < array.Length; i++)
			{
				Process[] processesByName;
				try
				{
					processesByName = Process.GetProcessesByName(array[i]);
				}
				catch
				{
					return false;
				}
				try
				{
					Process[] array2 = processesByName;
					foreach (Process process in array2)
					{
						string path;
						SessionRelation sessionRelation;
						if (!TryGetVerifiedImagePath(process, sessionId, out path, out sessionRelation))
						{
							return false;
						}
						if (sessionRelation != SessionRelation.Owned)
						{
							return false;
						}
						if (IsUnder(path, lolRoot) && IsCredentialSourceName(Path.GetFileName(path)))
						{
							result = true;
						}
					}
				}
				finally
				{
					DisposeProcesses(processesByName);
				}
			}
			return result;
		}

		internal static bool IsRelevantProcessChange(string name, string path, string lolRoot, string weGameRoot)
		{
			if (!IsScanCandidateName(name))
			{
				return false;
			}
			if (!string.IsNullOrEmpty(path))
			{
				if (!IsUnder(path, lolRoot))
				{
					return IsUnder(path, weGameRoot);
				}
				return true;
			}
			if (!string.Equals(name, "browser", StringComparison.OrdinalIgnoreCase))
			{
				return !string.Equals(name, "crashpad_handler", StringComparison.OrdinalIgnoreCase);
			}
			return false;
		}

		public static LolProcessSnapshot Scan(string lolRoot, string weGameRoot)
		{
			return Scan(lolRoot, weGameRoot, false);
		}

		public static LolProcessSnapshot Scan(string lolRoot, string weGameRoot, bool namesOnly)
		{
			return ScanCore(lolRoot, weGameRoot, namesOnly, false);
		}

		public static LolProcessSnapshot ScanShell(string gameRoot, string weGameRoot, bool namesOnly)
		{
			return ScanCore(gameRoot, weGameRoot, namesOnly, true);
		}

		private static string ValidatedRoot(string root, bool genericRoot)
		{
			if (genericRoot)
			{
				try
				{
					return (!string.IsNullOrEmpty(root) && Directory.Exists(root)) ? root : null;
				}
				catch
				{
					return null;
				}
			}
			if (!LolInstallDiscovery.IsValidLolRoot(root))
			{
				return null;
			}
			return root;
		}

		private static LolProcessSnapshot ScanCore(string lolRoot, string weGameRoot, bool namesOnly, bool genericRoot)
		{
			LolProcessSnapshot lolProcessSnapshot = new LolProcessSnapshot();
			lolRoot = ValidatedRoot(lolRoot, genericRoot);
			if (!LolInstallDiscovery.IsValidWeGameRoot(weGameRoot))
			{
				weGameRoot = null;
			}
			int sessionId;
			if (!TryGetCurrentSessionId(out sessionId))
			{
				return lolProcessSnapshot;
			}
			Process[] processes;
			try
			{
				processes = Process.GetProcesses();
			}
			catch
			{
				return lolProcessSnapshot;
			}
			lolProcessSnapshot.ScanSucceeded = true;
			Process[] array = processes;
			foreach (Process process in array)
			{
				string name = null;
				try
				{
					name = process.ProcessName;
					if (namesOnly && !IsScanCandidateName(name))
					{
						continue;
					}
					string path;
					SessionRelation sessionRelation;
					if (!TryGetVerifiedImagePath(process, sessionId, out path, out sessionRelation))
					{
						if (sessionRelation != SessionRelation.Foreign && IsCoreIdentityCandidateName(name))
						{
							lolProcessSnapshot.CoreIdentityIndeterminate = true;
						}
						continue;
					}
					if (string.IsNullOrEmpty(path))
					{
						if (IsCoreIdentityCandidateName(name))
						{
							lolProcessSnapshot.CoreIdentityIndeterminate = true;
						}
						continue;
					}
					string fileName = Path.GetFileName(path);
					if (IsLeagueClientProcess(path, fileName, lolRoot))
					{
						lolProcessSnapshot.ClientRunning = true;
					}
					if (IsGameProcess(path, fileName, lolRoot))
					{
						lolProcessSnapshot.GameRunning = true;
						lolProcessSnapshot.GameProcessId = process.Id;
					}
					if (IsUxProcess(path, fileName, lolRoot))
					{
						lolProcessSnapshot.UxProcessCount++;
					}
					if (IsMainUxProcess(path, fileName, lolRoot))
					{
						lolProcessSnapshot.MainUxProcessCount++;
					}
					if (IsWeGameProcess(path, fileName, weGameRoot))
					{
						lolProcessSnapshot.WeGameProcessCount++;
					}
					if (IsWeGameMainProcess(path, fileName, weGameRoot))
					{
						lolProcessSnapshot.WeGameMainRunning = true;
					}
					if (IsUnder(path, CombineSafe(lolRoot, "Cross")))
					{
						lolProcessSnapshot.CrossProcessCount++;
					}
				}
				catch
				{
					if (IsCoreIdentityCandidateName(name))
					{
						lolProcessSnapshot.CoreIdentityIndeterminate = true;
					}
				}
				finally
				{
					try
					{
						process.Dispose();
					}
					catch
					{
					}
				}
			}
			return lolProcessSnapshot;
		}

		public static LolCleanupResult Clean(string lolRoot, string weGameRoot)
		{
			return Clean(lolRoot, weGameRoot, false);
		}

		public static LolCleanupResult Clean(string lolRoot, string weGameRoot, bool includeDownloaders)
		{
			return CleanCore(lolRoot, weGameRoot, includeDownloaders, false);
		}

		public static LolCleanupResult CleanShell(string gameRoot, string weGameRoot, bool includeDownloaders)
		{
			return CleanCore(gameRoot, weGameRoot, includeDownloaders, true);
		}

		private static LolCleanupResult CleanCore(string lolRoot, string weGameRoot, bool includeDownloaders, bool genericRoot)
		{
			LolCleanupResult lolCleanupResult = new LolCleanupResult();
			lolRoot = ValidatedRoot(lolRoot, genericRoot);
			if (!LolInstallDiscovery.IsValidWeGameRoot(weGameRoot))
			{
				weGameRoot = null;
			}
			if (lolRoot == null && weGameRoot == null)
			{
				return lolCleanupResult;
			}
			int sessionId;
			if (!TryGetCurrentSessionId(out sessionId))
			{
				return lolCleanupResult;
			}
			Process[] processes;
			try
			{
				processes = Process.GetProcesses();
			}
			catch
			{
				return lolCleanupResult;
			}
			int id;
			using (Process process = Process.GetCurrentProcess())
			{
				id = process.Id;
			}
			List<int> list = new List<int>();
			List<long> list2 = new List<long>();
			List<long> list3 = new List<long>();
			List<string> list4 = new List<string>();
			Process[] array = processes;
			foreach (Process process2 in array)
			{
				IntPtr intPtr = IntPtr.Zero;
				try
				{
					if (process2.Id == id)
					{
						continue;
					}
					try
					{
						string processName = process2.ProcessName;
					}
					catch
					{
						goto end_IL_0090;
					}
					intPtr = Native.OpenProcess(1052672, false, process2.Id);
					int sessionId2;
					if (intPtr == IntPtr.Zero || !Native.TryGetLiveProcessSessionId(intPtr, process2.Id, out sessionId2) || !IsExpectedSession(sessionId, sessionId2))
					{
						continue;
					}
					string text = Native.ImagePath(intPtr);
					long creation;
					long cpu;
					ulong io;
					if (!string.IsNullOrEmpty(text) && IsCleanupTarget(text, Path.GetFileName(text), lolRoot, weGameRoot, includeDownloaders) && Native.QueryProcessSample(intPtr, out creation, out cpu, out io) && creation > 0)
					{
						long item = 0L;
						try
						{
							item = process2.WorkingSet64;
						}
						catch
						{
						}
						list.Add(process2.Id);
						list2.Add(item);
						list3.Add(creation);
						list4.Add(text);
					}
					end_IL_0090:;
				}
				catch
				{
				}
				finally
				{
					if (intPtr != IntPtr.Zero)
					{
						Native.CloseHandle(intPtr);
					}
					try
					{
						process2.Dispose();
					}
					catch
					{
					}
				}
			}
			if (list.Count == 0)
			{
				return lolCleanupResult;
			}
			for (int j = 0; j < list.Count; j++)
			{
				if (!cleanupFailures.Allowed(list[j], list3[j], list4[j], "cleanup")) continue;
				bool cleaned = false;
				bool failedOperation = false;
				IntPtr intPtr2 = IntPtr.Zero;
				try
				{
					intPtr2 = Native.OpenProcess(1052673, false, list[j]);
					int sessionId3;
					if (intPtr2 == IntPtr.Zero)
					{
						failedOperation = Marshal.GetLastWin32Error() != 87;
						continue;
					}
					if (!Native.TryGetLiveProcessSessionId(intPtr2, list[j], out sessionId3)) { failedOperation = true; continue; }
					if (!IsExpectedSession(sessionId, sessionId3)) continue;
					string text2 = Native.ImagePath(intPtr2);
					long creation2;
					long cpu2;
					ulong io2;
					if (string.IsNullOrEmpty(text2) || !Native.QueryProcessSample(intPtr2, out creation2, out cpu2, out io2)) { failedOperation = true; continue; }
					if (creation2 != list3[j] || !string.Equals(text2, list4[j], StringComparison.OrdinalIgnoreCase)
						|| !IsCleanupTarget(text2, Path.GetFileName(text2), lolRoot, weGameRoot, includeDownloaders)) continue;
					failedOperation = true;
					if (TerminateProcess(intPtr2, 0u) && WaitForSingleObject(intPtr2, 1500u) == 0)
					{
						cleaned = true;
						lolCleanupResult.Count++;
						if (list2[j] > 0)
						{
							lolCleanupResult.WorkingSetBytes += list2[j];
						}
					}
				}
				catch
				{
					failedOperation = true;
				}
				finally
				{
					if (cleaned) cleanupFailures.Succeeded(list[j], list3[j], list4[j], "cleanup");
					else if (failedOperation) Logger.Warn("LoL cleanup failure=" + cleanupFailures.Failed(list[j], list3[j], list4[j], "cleanup")
						+ "/3 target=" + list[j] + "/" + list3[j]);
					if (intPtr2 != IntPtr.Zero)
					{
						Native.CloseHandle(intPtr2);
					}
				}
			}
			return lolCleanupResult;
		}

		public static bool IsGameRunning(string lolRoot)
		{
			return AnyProcessMatches(lolRoot, new string[1] { "League of Legends" }, IsGameProcess);
		}

		private static bool AnyProcessMatches(string root, string[] names, Func<string, string, string, bool> predicate)
		{
			if (string.IsNullOrEmpty(root))
			{
				return false;
			}
			int sessionId;
			if (!TryGetCurrentSessionId(out sessionId))
			{
				return false;
			}
			for (int i = 0; i < names.Length; i++)
			{
				string text = names[i];
				if (text.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
				{
					text = text.Substring(0, text.Length - 4);
				}
				Process[] processesByName;
				try
				{
					processesByName = Process.GetProcessesByName(text);
				}
				catch
				{
					continue;
				}
				bool flag = false;
				try
				{
					Process[] array = processesByName;
					foreach (Process process in array)
					{
						try
						{
							string path;
							SessionRelation sessionRelation;
							if (!TryGetVerifiedImagePath(process, sessionId, out path, out sessionRelation) || !predicate(path, Path.GetFileName(path), root))
							{
								continue;
							}
							flag = true;
							break;
						}
						catch
						{
						}
					}
				}
				finally
				{
					DisposeProcesses(processesByName);
				}
				if (flag)
				{
					return true;
				}
			}
			return false;
		}

		internal static bool TryGetGameIdentity(string lolRoot, int preferredPid, out int pid, out long creation)
		{
			pid = 0;
			creation = 0L;
			if (!LolInstallDiscovery.IsValidLolRoot(lolRoot))
			{
				return false;
			}
			int sessionId;
			if (!TryGetCurrentSessionId(out sessionId))
			{
				return false;
			}
			if (preferredPid > 0 && TryGetGameIdentityCore(lolRoot, preferredPid, sessionId, out creation))
			{
				pid = preferredPid;
				return true;
			}
			Process[] processesByName;
			try
			{
				processesByName = Process.GetProcessesByName("League of Legends");
			}
			catch
			{
				return false;
			}
			try
			{
				Process[] array = processesByName;
				foreach (Process process in array)
				{
					try
					{
						long creation2;
						if (!TryGetGameIdentityCore(lolRoot, process.Id, sessionId, out creation2))
						{
							continue;
						}
						pid = process.Id;
						creation = creation2;
						return true;
					}
					catch
					{
					}
				}
			}
			finally
			{
				DisposeProcesses(processesByName);
			}
			return false;
		}

		private static bool TryGetGameIdentityCore(string lolRoot, int pid, int expectedSession, out long creation)
		{
			creation = 0L;
			IntPtr intPtr = IntPtr.Zero;
			try
			{
				intPtr = Native.OpenProcess(1052672, false, pid);
				if (intPtr == IntPtr.Zero)
				{
					return false;
				}
				int sessionId;
				if (!Native.TryGetLiveProcessSessionId(intPtr, pid, out sessionId) || !IsExpectedSession(expectedSession, sessionId))
				{
					return false;
				}
				string path = Native.ImagePath(intPtr);
				if (!IsGameProcess(path, Path.GetFileName(path), lolRoot))
				{
					return false;
				}
				long cpu;
				ulong io;
				return Native.QueryProcessSample(intPtr, out creation, out cpu, out io) && creation != 0;
			}
			catch
			{
				return false;
			}
			finally
			{
				if (intPtr != IntPtr.Zero)
				{
					Native.CloseHandle(intPtr);
				}
			}
		}

		public static bool IsWeGameRunning(string weGameRoot)
		{
			if (LolInstallDiscovery.IsValidWeGameRoot(weGameRoot))
			{
				return AnyProcessMatches(weGameRoot, new string[1] { "wegame.exe" }, IsWeGameMainProcess);
			}
			return false;
		}

		public static bool IsClientRunning(string lolRoot)
		{
			return AnyProcessMatches(lolRoot, new string[1] { "LeagueClient" }, IsLeagueClientProcess);
		}

		public static bool IsUxRunning(string lolRoot)
		{
			return AnyProcessMatches(lolRoot, new string[1] { "LeagueClientUx" }, IsUxProcess);
		}

		private static void DisposeProcesses(Process[] processes)
		{
			if (processes == null)
			{
				return;
			}
			foreach (Process process in processes)
			{
				if (process != null)
				{
					try
					{
						process.Dispose();
					}
					catch
					{
					}
				}
			}
		}

		public static bool IsUnder(string path, string root)
		{
			if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(root))
			{
				return false;
			}
			try
			{
				string fullPath = Path.GetFullPath(path);
				string text = Path.GetFullPath(root).TrimEnd('\\') + "\\";
				if (text.Length <= 3)
				{
					return false;
				}
				return fullPath.StartsWith(text, StringComparison.OrdinalIgnoreCase);
			}
			catch
			{
				return false;
			}
		}

		internal static bool IsCleanupTarget(string path, string file, string lolRoot, string weGameRoot)
		{
			return IsCleanupTarget(path, file, lolRoot, weGameRoot, false);
		}

		internal static bool IsCleanupTarget(string path, string file, string lolRoot, string weGameRoot, bool includeDownloaders)
		{
			if (IsWeGameProcess(path, file, weGameRoot))
			{
				return true;
			}
			if (includeDownloaders && IsDownloaderProcess(path, file, lolRoot, weGameRoot))
			{
				return true;
			}
			if (string.IsNullOrEmpty(lolRoot))
			{
				return false;
			}
			string[] array = new string[6]
			{
				Path.Combine(lolRoot, "Cross"),
				Path.Combine(lolRoot, "LeagueClient", "FeedBack"),
				Path.Combine(lolRoot, "LeagueClient", "NetworkAssist"),
				Path.Combine(lolRoot, "LeagueClient", "TQM"),
				Path.Combine(lolRoot, "LeagueClient", "DiagnosticAssistant"),
				Path.Combine(lolRoot, "Launcher", "qbblinktrial")
			};
			for (int i = 0; i < array.Length; i++)
			{
				if (IsUnder(path, array[i]))
				{
					return true;
				}
			}
			if (IsUnder(path, Path.Combine(lolRoot, "TCLS")))
			{
				for (int j = 0; j < TencentSessionNames.Length; j++)
				{
					if (string.Equals(file, TencentSessionNames[j], StringComparison.OrdinalIgnoreCase))
					{
						return true;
					}
				}
			}
			return false;
		}

		internal static bool IsDownloaderProcess(string path, string file, string lolRoot, string weGameRoot)
		{
			if (!string.IsNullOrEmpty(lolRoot) && IsUnder(path, Path.Combine(lolRoot, "WeGameLauncher", "TenioDL")))
			{
				return true;
			}
			if (string.IsNullOrEmpty(weGameRoot) || !IsUnder(path, weGameRoot))
			{
				return false;
			}
			for (int i = 0; i < DownloaderNames.Length; i++)
			{
				if (string.Equals(file, DownloaderNames[i], StringComparison.OrdinalIgnoreCase))
				{
					return true;
				}
			}
			return false;
		}

		internal static bool IsLeagueClientProcess(string path, string file, string lolRoot)
		{
			if (string.IsNullOrEmpty(lolRoot) || !IsUnder(path, lolRoot))
			{
				return false;
			}
			return string.Equals(file, "LeagueClient.exe", StringComparison.OrdinalIgnoreCase);
		}

		internal static bool IsGameProcess(string path, string file, string lolRoot)
		{
			if (!string.IsNullOrEmpty(lolRoot) && string.Equals(file, "League of Legends.exe", StringComparison.OrdinalIgnoreCase))
			{
				return IsUnder(path, Path.Combine(lolRoot, "Game"));
			}
			return false;
		}

		private static bool IsUxProcess(string path, string file, string lolRoot)
		{
			if (!string.IsNullOrEmpty(lolRoot) && file.StartsWith("LeagueClientUx", StringComparison.OrdinalIgnoreCase))
			{
				return IsUnder(path, lolRoot);
			}
			return false;
		}

		internal static bool IsMainUxProcess(string path, string file, string lolRoot)
		{
			if (!string.IsNullOrEmpty(lolRoot) && string.Equals(file, "LeagueClientUx.exe", StringComparison.OrdinalIgnoreCase))
			{
				return IsUnder(path, lolRoot);
			}
			return false;
		}

		private static bool IsWeGameMainProcess(string path, string file, string weGameRoot)
		{
			if (!string.IsNullOrEmpty(weGameRoot) && IsUnder(path, weGameRoot))
			{
				return string.Equals(file, "wegame.exe", StringComparison.OrdinalIgnoreCase);
			}
			return false;
		}

		private static bool IsWeGameProcess(string path, string file, string weGameRoot)
		{
			if (string.IsNullOrEmpty(weGameRoot) || !IsUnder(path, weGameRoot))
			{
				return false;
			}
			for (int i = 0; i < WeGameNames.Length; i++)
			{
				if (string.Equals(file, WeGameNames[i], StringComparison.OrdinalIgnoreCase))
				{
					return true;
				}
			}
			return false;
		}

		private static string CombineSafe(string root, string child)
		{
			if (string.IsNullOrEmpty(root))
			{
				return null;
			}
			try
			{
				return Path.Combine(root, child);
			}
			catch
			{
				return null;
			}
		}
	}
}
