using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace PaviseApp
{
	internal static class LolWatchdog
	{
		private sealed class LaunchOwner { internal Process Process; internal EventWaitHandle Ready; }
		private static readonly object launchGate = new object();
		private static readonly Dictionary<string, LaunchOwner> launchOwners = new Dictionary<string, LaunchOwner>(StringComparer.OrdinalIgnoreCase);
		private const int Synchronize = 1048576;

		private const int ReconnectBindWindowSeconds = 20;

		private const int UnknownPhaseExitGraceSeconds = 12;

		private const int CredentialGenerationCheckSeconds = 3;

		private const int SameMatchSlowPollSeconds = 120;

		private const int SameMatchWatchLimitMinutes = 30;

		private const uint WaitObject0 = 0u;

		private const uint WaitTimeout = 258u;

		private const uint HandlePollMs = 5000u;

		private const int ReadyTimeoutMs = 5000;

		private const int ReadyTokenBytes = 32;

		private const string ReadyEventPrefix = "Pavise_LolWatchdogReady_";

		private const string GuardMutexPrefix = "Pavise_LolWatchdog_";

		private const string AliveEventPrefix = "Pavise_LolWatchdogAlive_";

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

		public static bool TryHandle(string[] args)
		{
			if (args == null || args.Length == 0 || !string.Equals(args[0], "--lol-watchdog", StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}
			if (args.Length != 2 && args.Length != 4 && args.Length != 5)
			{
				return true;
			}
			string getFullPathTrimmed;
			try
			{
				getFullPathTrimmed = Path.GetFullPath(args[1]).TrimEnd('\\');
			}
			catch
			{
				return true;
			}
			if (!LolInstallDiscovery.IsValidLolRoot(getFullPathTrimmed))
			{
				return true;
			}
			int result = 0;
			long result2 = 0L;
			if (args.Length >= 4 && (!int.TryParse(args[2], out result) || result <= 0 || !long.TryParse(args[3], out result2) || result2 <= 0))
			{
				return true;
			}
			string message = ((args.Length == 5) ? args[4] : null);
			if (message != null && !IsReadyToken(message))
			{
				return true;
			}
			Run(getFullPathTrimmed, result, result2, message);
			return true;
		}

		public static bool StartDetached(string lolRoot)
		{
			int pid;
			long creation;
			if (LolRuntimeProcesses.TryGetGameIdentity(lolRoot, 0, out pid, out creation))
			{
				return StartDetached(lolRoot, pid, creation);
			}
			return false;
		}

		public static bool StartDetached(string lolRoot, int pid, long creation)
		{
			lock (launchGate) return StartDetachedCore(lolRoot, pid, creation);
		}

		private static bool StartDetachedCore(string lolRoot, int pid, long creation)
		{
			try
			{
				lolRoot = Path.GetFullPath(lolRoot).TrimEnd('\\');
			}
			catch
			{
				return false;
			}
			if (!LolInstallDiscovery.IsValidLolRoot(lolRoot))
			{
				return false;
			}
			IntPtr handle;
			if (!TryOpenGameWaitHandle(lolRoot, pid, creation, out handle))
			{
				return false;
			}
			Native.CloseHandle(handle);
			string launchKey = lolRoot + "|" + pid + "|" + creation;
			LaunchOwner owner;
			if (launchOwners.TryGetValue(launchKey, out owner))
			{
				try
				{
					if (!owner.Process.HasExited)
						return owner.Ready.WaitOne(0) && LolHeadlessLease.Matches(lolRoot, pid, creation);
				}
				catch { return false; }
				owner.Ready.Dispose(); owner.Process.Dispose(); launchOwners.Remove(launchKey);
			}
			if (ExistingGuardIsReady(lolRoot, pid, creation) && SiblingPaviseProcessAlive())
			{
				return true;
			}
			EventWaitHandle eventWaitHandle = null;
			Process process = null;
			try
			{
				string readyToken = CreateReadyToken();
				string name = ReadyEventName(lolRoot, pid, creation, readyToken);
				bool createdNew;
				eventWaitHandle = new EventWaitHandle(false, EventResetMode.ManualReset, name, out createdNew);
				if (!createdNew)
				{
					return false;
				}
				string executablePath = Application.ExecutablePath;
				ProcessStartInfo processStartInfo = new ProcessStartInfo();
				processStartInfo.FileName = executablePath;
				processStartInfo.Arguments = "--lol-watchdog " + Quote(lolRoot) + " " + pid + " " + creation + " " + readyToken;
				processStartInfo.WorkingDirectory = Path.GetDirectoryName(executablePath);
				processStartInfo.UseShellExecute = false;
				processStartInfo.CreateNoWindow = true;
				processStartInfo.WindowStyle = ProcessWindowStyle.Hidden;
				process = Process.Start(processStartInfo);
				if (process == null)
				{
					return false;
				}
				launchOwners[launchKey] = new LaunchOwner { Process = process, Ready = eventWaitHandle };
				if (!eventWaitHandle.WaitOne(5000))
				{
					return false;
				}
				try
				{
					if (process.HasExited)
					{
						return false;
					}
				}
				catch
				{
					return false;
				}
				return LolHeadlessLease.Matches(lolRoot, pid, creation);
			}
			catch
			{
				return false;
			}
			finally
			{
				// A ready timeout retains the original helper and event so later calls observe the same owner.
				bool exited = process == null;
				try { if (process != null) exited = process.HasExited; } catch { }
				if (exited && process != null)
				{
					try
					{
						process.Dispose();
					}
					catch
					{
					}
				}
				if (exited && eventWaitHandle != null)
				{
					try
					{
						eventWaitHandle.Close();
					}
					catch
					{
					}
				}
				if (exited) launchOwners.Remove(launchKey);
			}
		}

		private static void Run(string lolRoot, int pid, long creation, string readyToken)
		{
			Mutex mutex = null;
			EventWaitHandle eventWaitHandle = null;
			bool createdNew = false;
			bool globalNamespace = false;
			IntPtr handle = IntPtr.Zero;
			try
			{
				try
				{
					mutex = new Mutex(true, GuardMutexName(lolRoot, pid, creation, true), out createdNew);
					globalNamespace = true;
				}
				catch
				{
					mutex = new Mutex(true, GuardMutexName(lolRoot, pid, creation, false), out createdNew);
				}
				if (!createdNew || !LeaseStillOwned(lolRoot, pid, creation) || (pid > 0 && creation > 0 && !TryOpenGameWaitHandle(lolRoot, pid, creation, out handle)) || (handle != IntPtr.Zero && WaitForSingleObject(handle, 0u) != 258))
				{
					return;
				}
				bool createdNew2;
				eventWaitHandle = new EventWaitHandle(false, EventResetMode.ManualReset, AliveEventName(lolRoot, pid, creation, globalNamespace), out createdNew2);
				if (createdNew2 && eventWaitHandle.Set() && (readyToken == null || (!(handle == IntPtr.Zero) && SignalReady(lolRoot, pid, creation, readyToken))))
				{
					IntPtr preparedGameHandle = handle;
					handle = IntPtr.Zero;
					if (WaitForGameExit(lolRoot, pid, creation, preparedGameHandle))
					{
						TryRestore(lolRoot, pid, creation);
					}
				}
			}
			finally
			{
				if (handle != IntPtr.Zero)
				{
					Native.CloseHandle(handle);
				}
				if (eventWaitHandle != null)
				{
					try
					{
						eventWaitHandle.Reset();
					}
					catch
					{
					}
					try
					{
						eventWaitHandle.Close();
					}
					catch
					{
					}
				}
				if (mutex != null)
				{
					if (createdNew)
					{
						try
						{
							mutex.ReleaseMutex();
						}
						catch
						{
						}
					}
					try
					{
						mutex.Close();
					}
					catch
					{
					}
				}
			}
		}

		internal static bool TryOpenGameWaitHandle(string lolRoot, int pid, long creation, out IntPtr handle)
		{
			handle = IntPtr.Zero;
			if (pid <= 0 || creation <= 0 || !LolInstallDiscovery.IsValidLolRoot(lolRoot))
			{
				return false;
			}
			IntPtr intPtr = IntPtr.Zero;
			try
			{
				intPtr = Native.OpenProcess(1052672, false, pid);
				if (intPtr == IntPtr.Zero)
				{
					return false;
				}
				string path = Native.ImagePath(intPtr);
				if (!LolRuntimeProcesses.IsGameProcess(path, Path.GetFileName(path), lolRoot))
				{
					return false;
				}
				long creation2;
				long cpu;
				ulong io;
				if (!Native.QueryProcessSample(intPtr, out creation2, out cpu, out io) || creation2 != creation)
				{
					return false;
				}
				handle = intPtr;
				intPtr = IntPtr.Zero;
				return true;
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

		private static bool WaitForGameExit(string lolRoot, int initialPid, long initialCreation, IntPtr preparedGameHandle)
		{
			DateTime dateTime = DateTime.MinValue;
			LolLcuCredentials lolLcuCredentials = null;
			int waitForGameExitValue = 0;
			int waitForGameExitValue2 = 0;
			int failures = 0;
			long credentialGenerationStampResult = LolLcuCredentialSource.CredentialGenerationStamp(lolRoot);
			DateTime dateTime2 = DateTime.MinValue;
			IntPtr handle = preparedGameHandle;
			if (initialPid <= 0 || initialCreation <= 0)
			{
				return false;
			}
			bool waitForGameExitReady = false;
			try
			{
				if (!LeaseStillOwned(lolRoot, initialPid, initialCreation))
				{
					return false;
				}
				if (handle != IntPtr.Zero || TryOpenGameWaitHandle(lolRoot, initialPid, initialCreation, out handle))
				{
					waitForGameExitReady = true;
					uint waitForOwnedGameExitResult = WaitForOwnedGameExit(handle, lolRoot, initialPid, initialCreation);
					Native.CloseHandle(handle);
					handle = IntPtr.Zero;
					if (waitForOwnedGameExitResult != 0)
					{
						return false;
					}
				}
				else
				{
					if (!LeaseStillOwned(lolRoot, initialPid, initialCreation) || !OriginalGameIdentityGone(lolRoot, initialPid, initialCreation))
					{
						return false;
					}
					waitForGameExitReady = true;
				}
				DateTime utcNow = DateTime.UtcNow;
				DateTime dateTime3 = DateTime.MinValue;
				DateTime dateTime4 = DateTime.MinValue;
				DateTime dateTime5 = utcNow.AddSeconds(20.0);
				bool waitForGameExitReady2 = false;
				while (true)
				{
					if (!LeaseStillOwned(lolRoot, initialPid, initialCreation))
					{
						return false;
					}
					if (!LolRuntimeProcesses.IsClientRunning(lolRoot))
					{
						if (dateTime3 == DateTime.MinValue)
						{
							dateTime3 = DateTime.UtcNow;
						}
						if ((DateTime.UtcNow - dateTime3).TotalMinutes >= 2.0)
						{
							return false;
						}
						Thread.Sleep(1500);
						continue;
					}
					dateTime3 = DateTime.MinValue;
					if (DateTime.UtcNow >= dateTime2)
					{
						long credentialGenerationStampResult2 = LolLcuCredentialSource.CredentialGenerationStamp(lolRoot);
						if (credentialGenerationStampResult2 != credentialGenerationStampResult)
						{
							credentialGenerationStampResult = credentialGenerationStampResult2;
							lolLcuCredentials = null;
							waitForGameExitValue2 = 0;
							failures = 0;
							dateTime = DateTime.MinValue;
						}
						dateTime2 = DateTime.UtcNow.AddSeconds(3.0);
					}
					if (lolLcuCredentials == null && DateTime.UtcNow >= dateTime)
					{
						lolLcuCredentials = LolLcuCredentialSource.Find(lolRoot);
						if (lolLcuCredentials == null)
						{
							dateTime = NextCredentialLookup(ref failures);
						}
						else
						{
							failures = 0;
							waitForGameExitValue2 = 0;
						}
					}
					string gameflowPhase = ((lolLcuCredentials == null) ? null : LolLcuClient.GetGameflowPhase(lolLcuCredentials));
					if (lolLcuCredentials != null)
					{
						if (gameflowPhase == null)
						{
							if (++waitForGameExitValue2 >= 3)
							{
								lolLcuCredentials = null;
								waitForGameExitValue2 = 0;
								dateTime = NextCredentialLookup(ref failures);
							}
						}
						else
						{
							waitForGameExitValue2 = 0;
						}
					}
					bool isNotIsNullOrEmpty = !string.IsNullOrEmpty(gameflowPhase);
					bool isSameMatchPhase = LolOptimizationService.IsSameMatchPhase(gameflowPhase);
					int pid;
					long creation;
					if (waitForGameExitReady && !waitForGameExitReady2 && isSameMatchPhase && DateTime.UtcNow < dateTime5 && LolRuntimeProcesses.TryGetGameIdentity(lolRoot, 0, out pid, out creation) && creation > initialCreation && TryOpenGameWaitHandle(lolRoot, pid, creation, out handle))
					{
						waitForGameExitReady2 = true;
						uint waitForOwnedGameExitResult2 = WaitForOwnedGameExit(handle, lolRoot, initialPid, initialCreation);
						Native.CloseHandle(handle);
						handle = IntPtr.Zero;
						if (waitForOwnedGameExitResult2 != 0)
						{
							return false;
						}
						utcNow = DateTime.UtcNow;
						waitForGameExitValue = 0;
						continue;
					}
					bool isGameRunning = LolRuntimeProcesses.IsGameRunning(lolRoot);
					if (isSameMatchPhase && !isGameRunning)
					{
						if (dateTime4 == DateTime.MinValue)
						{
							dateTime4 = DateTime.UtcNow;
						}
						if ((DateTime.UtcNow - dateTime4).TotalMinutes >= 30.0)
						{
							return false;
						}
					}
					else
					{
						dateTime4 = DateTime.MinValue;
					}
					if (!isGameRunning && isNotIsNullOrEmpty && LolOptimizationService.IsConfirmedMatchExitPhase(gameflowPhase))
					{
						if (++waitForGameExitValue >= 2)
						{
							return true;
						}
					}
					else
					{
						waitForGameExitValue = 0;
					}
					if (!isGameRunning && (DateTime.UtcNow - utcNow).TotalSeconds >= 12.0 && !isNotIsNullOrEmpty)
					{
						break;
					}
					int millisecondsTimeout = ((DateTime.UtcNow < dateTime5) ? 1500 : ((dateTime4 != DateTime.MinValue && (DateTime.UtcNow - dateTime4).TotalSeconds >= 120.0) ? 30000 : 6000));
					Thread.Sleep(millisecondsTimeout);
				}
				return true;
			}
			finally
			{
				if (handle != IntPtr.Zero)
				{
					Native.CloseHandle(handle);
				}
			}
		}

		private static uint WaitForOwnedGameExit(IntPtr handle, string lolRoot, int leasePid, long leaseCreation)
		{
			do
			{
				uint waitForSingleObjectResult = WaitForSingleObject(handle, 5000u);
				switch (waitForSingleObjectResult)
				{
				case 0u:
					return waitForSingleObjectResult;
				default:
					return waitForSingleObjectResult;
				case 258u:
					break;
				}
			}
			while (LeaseStillOwned(lolRoot, leasePid, leaseCreation));
			return uint.MaxValue;
		}

		internal static bool OriginalGameIdentityGone(string lolRoot, int pid, long creation)
		{
			IntPtr intPtr = IntPtr.Zero;
			try
			{
				intPtr = Native.OpenProcess(4096, false, pid);
				if (intPtr == IntPtr.Zero)
				{
					return Marshal.GetLastWin32Error() == 87;
				}
				long creation2;
				long cpu;
				ulong io;
				if (!Native.QueryProcessSample(intPtr, out creation2, out cpu, out io))
				{
					return false;
				}
				if (creation2 != creation)
				{
					return true;
				}
				string path = Native.ImagePath(intPtr);
				return !LolRuntimeProcesses.IsGameProcess(path, Path.GetFileName(path), lolRoot);
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

		private static bool TryRestore(string lolRoot, int leasePid, long leaseCreation)
		{
			int restoreAttempts = 0, clearAttempts = 0;
			DateTime dateTime = DateTime.UtcNow.AddMinutes(2.0);
			DateTime dateTime2 = DateTime.UtcNow.AddMinutes(30.0);
			DateTime dateTime3 = DateTime.MinValue;
			DateTime dateTime4 = DateTime.MinValue;
			LolLcuCredentials lolLcuCredentials = null;
			int failures = 0;
			int processedCount = 0;
			bool restoreReady = false;
			long credentialGenerationStampResult = LolLcuCredentialSource.CredentialGenerationStamp(lolRoot);
			DateTime dateTime5 = DateTime.MinValue;
			while (DateTime.UtcNow < dateTime && DateTime.UtcNow < dateTime2)
			{
				if (!LeaseStillOwned(lolRoot, leasePid, leaseCreation))
				{
					return false;
				}
				if (!LolRuntimeProcesses.IsClientRunning(lolRoot))
				{
					dateTime = DateTime.UtcNow.AddMinutes(2.0);
					Thread.Sleep(1500);
					continue;
				}
				if (DateTime.UtcNow >= dateTime5)
				{
					long credentialGenerationStampResult2 = LolLcuCredentialSource.CredentialGenerationStamp(lolRoot);
					if (credentialGenerationStampResult2 != credentialGenerationStampResult)
					{
						credentialGenerationStampResult = credentialGenerationStampResult2;
						lolLcuCredentials = null;
						failures = 0;
						processedCount = 0;
						dateTime3 = DateTime.MinValue;
						dateTime4 = DateTime.MinValue;
					}
					dateTime5 = DateTime.UtcNow.AddSeconds(3.0);
				}
				if (lolLcuCredentials == null && DateTime.UtcNow >= dateTime3)
				{
					lolLcuCredentials = LolLcuCredentialSource.Find(lolRoot);
					if (lolLcuCredentials == null)
					{
						dateTime3 = NextCredentialLookup(ref failures);
					}
					else
					{
						failures = 0;
						dateTime3 = DateTime.MaxValue;
					}
				}
				string gameflowPhase = ((lolLcuCredentials == null) ? null : LolLcuClient.GetGameflowPhase(lolLcuCredentials));
				int pid;
				long creation;
				if (leaseCreation > 0 && LolRuntimeProcesses.TryGetGameIdentity(lolRoot, 0, out pid, out creation) && creation > leaseCreation)
				{
					return false;
				}
				if (lolLcuCredentials != null && gameflowPhase == null)
				{
					lolLcuCredentials = null;
					dateTime3 = NextCredentialLookup(ref failures);
					dateTime = DateTime.UtcNow.AddMinutes(2.0);
					Thread.Sleep(1500);
					continue;
				}
				if (LolOptimizationService.IsSameMatchPhase(gameflowPhase))
				{
					dateTime = DateTime.UtcNow.AddMinutes(2.0);
					Thread.Sleep(6000);
					continue;
				}
				bool allConditionsMet = !restoreReady && lolLcuCredentials != null && DateTime.UtcNow >= dateTime4;
				if (allConditionsMet && ++restoreAttempts <= 3 && LolLcuClient.RestoreUx(lolLcuCredentials, lolRoot))
				{
					restoreReady = true;
				}
				if (restoreReady)
				{
					if (++clearAttempts > 3) return false;
					if (LolHeadlessLease.ClearIfMatches(lolRoot, leasePid, leaseCreation) || !LeaseStillOwned(lolRoot, leasePid, leaseCreation))
					{
						return true;
					}
					Thread.Sleep(1500);
					continue;
				}
				if (allConditionsMet)
				{
					if (restoreAttempts >= 3) return false;
					if (processedCount < int.MaxValue)
					{
						processedCount++;
					}
					dateTime4 = DateTime.UtcNow.AddSeconds(LolOptimizationService.RestoreCredentialRetrySeconds(processedCount));
				}
				if (lolLcuCredentials == null)
				{
					dateTime = DateTime.UtcNow.AddMinutes(2.0);
				}
				Thread.Sleep(1500);
			}
			return false;
		}

		private static DateTime NextCredentialLookup(ref int failures)
		{
			if (failures < int.MaxValue)
			{
				failures++;
			}
			return DateTime.UtcNow.AddSeconds(LolOptimizationService.CredentialRetrySeconds(failures));
		}

		private static bool LeaseStillOwned(string lolRoot, int leasePid, long leaseCreation)
		{
			return LolHeadlessLease.Matches(lolRoot, leasePid, leaseCreation);
		}

		private static bool SiblingPaviseProcessAlive()
		{
			string fullPath;
			string fileNameWithoutExtension;
			int id;
			try
			{
				fullPath = Path.GetFullPath(Application.ExecutablePath);
				fileNameWithoutExtension = Path.GetFileNameWithoutExtension(fullPath);
				using (Process process = Process.GetCurrentProcess())
				{
					id = process.Id;
				}
			}
			catch
			{
				return false;
			}
			if (string.IsNullOrEmpty(fileNameWithoutExtension))
			{
				return false;
			}
			Process[] processesByName;
			try
			{
				processesByName = Process.GetProcessesByName(fileNameWithoutExtension);
			}
			catch
			{
				return false;
			}
			try
			{
				Process[] processesByNameSnapshot = processesByName;
				foreach (Process process2 in processesByNameSnapshot)
				{
					try
					{
						if (process2.Id == id)
						{
							continue;
						}
						IntPtr intPtr = Native.OpenProcess(4096, false, process2.Id);
						if (intPtr == IntPtr.Zero)
						{
							continue;
						}
						try
						{
							string imagePathResult = Native.ImagePath(intPtr);
							if (imagePathResult != null && string.Equals(Path.GetFullPath(imagePathResult), fullPath, StringComparison.OrdinalIgnoreCase))
							{
								return true;
							}
						}
						finally
						{
							Native.CloseHandle(intPtr);
						}
					}
					catch
					{
					}
				}
			}
			finally
			{
				Process[] processesByNameSnapshot2 = processesByName;
				foreach (Process process3 in processesByNameSnapshot2)
				{
					try
					{
						process3.Dispose();
					}
					catch
					{
					}
				}
			}
			return false;
		}

		internal static bool ExistingGuardIsReady(string lolRoot, int pid, long creation)
		{
			if (pid <= 0 || creation <= 0 || !LolHeadlessLease.Matches(lolRoot, pid, creation))
			{
				return false;
			}
			if (!GuardObjectsReady(GuardMutexName(lolRoot, pid, creation, true), AliveEventName(lolRoot, pid, creation, true)))
			{
				return GuardObjectsReady(GuardMutexName(lolRoot, pid, creation, false), AliveEventName(lolRoot, pid, creation, false));
			}
			return true;
		}

		internal static bool GuardObjectsReady(string mutexName, string aliveEventName)
		{
			EventWaitHandle eventWaitHandle = null;
			Mutex mutex = null;
			bool guardObjectsReadyReady = false;
			try
			{
				eventWaitHandle = EventWaitHandle.OpenExisting(aliveEventName);
				if (!eventWaitHandle.WaitOne(0))
				{
					return false;
				}
				mutex = Mutex.OpenExisting(mutexName);
				try
				{
					guardObjectsReadyReady = mutex.WaitOne(0, false);
				}
				catch (AbandonedMutexException)
				{
					guardObjectsReadyReady = true;
				}
				if (!guardObjectsReadyReady)
				{
					return true;
				}
				try
				{
					mutex.ReleaseMutex();
				}
				catch
				{
				}
				guardObjectsReadyReady = false;
				return false;
			}
			catch
			{
				return false;
			}
			finally
			{
				if (guardObjectsReadyReady && mutex != null)
				{
					try
					{
						mutex.ReleaseMutex();
					}
					catch
					{
					}
				}
				if (mutex != null)
				{
					try
					{
						mutex.Close();
					}
					catch
					{
					}
				}
				if (eventWaitHandle != null)
				{
					try
					{
						eventWaitHandle.Close();
					}
					catch
					{
					}
				}
			}
		}

		internal static string GuardMutexName(string lolRoot, int pid, long creation, bool globalNamespace)
		{
			return (globalNamespace ? "Global\\" : "") + "Pavise_LolWatchdog_" + GuardIdentity(lolRoot, pid, creation);
		}

		internal static string AliveEventName(string lolRoot, int pid, long creation, bool globalNamespace)
		{
			return (globalNamespace ? "Global\\" : "") + "Pavise_LolWatchdogAlive_" + GuardIdentity(lolRoot, pid, creation);
		}

		private static string GuardIdentity(string lolRoot, int pid, long creation)
		{
			return LolHeadlessLease.StableHash(lolRoot + "|" + pid + "|" + creation);
		}

		internal static string CreateReadyToken()
		{
			byte[] rawBytes = new byte[32];
			using (RandomNumberGenerator randomNumberGenerator = RandomNumberGenerator.Create())
			{
				randomNumberGenerator.GetBytes(rawBytes);
			}
			StringBuilder stringBuilder = new StringBuilder(rawBytes.Length * 2);
			for (int i = 0; i < rawBytes.Length; i++)
			{
				stringBuilder.Append(rawBytes[i].ToString("x2"));
			}
			return stringBuilder.ToString();
		}

		internal static bool IsReadyToken(string token)
		{
			if (token == null || token.Length != 64)
			{
				return false;
			}
			foreach (char c in token)
			{
				if ((c < '0' || c > '9') && (c < 'a' || c > 'f') && (c < 'A' || c > 'F'))
				{
					return false;
				}
			}
			return true;
		}

		internal static string ReadyEventName(string lolRoot, int pid, long creation, string readyToken)
		{
			if (!IsReadyToken(readyToken) || pid <= 0 || creation <= 0)
			{
				return null;
			}
			return "Pavise_LolWatchdogReady_" + LolHeadlessLease.StableHash(lolRoot) + "_" + pid + "_" + creation + "_" + readyToken;
		}

		internal static bool SignalReady(string lolRoot, int pid, long creation, string readyToken)
		{
			string yEventName = ReadyEventName(lolRoot, pid, creation, readyToken);
			if (yEventName == null)
			{
				return false;
			}
			EventWaitHandle eventWaitHandle = null;
			try
			{
				eventWaitHandle = EventWaitHandle.OpenExisting(yEventName);
				return eventWaitHandle.Set();
			}
			catch
			{
				return false;
			}
			finally
			{
				if (eventWaitHandle != null)
				{
					try
					{
						eventWaitHandle.Close();
					}
					catch
					{
					}
				}
			}
		}

		private static string Quote(string value)
		{
			return "\"" + (value ?? "").Replace("\"", "") + "\"";
		}
	}
}
