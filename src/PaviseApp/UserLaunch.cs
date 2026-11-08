using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace PaviseApp
{
	internal static class UserLaunch
	{
		[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
		private struct StartupInfo
		{
			public int cb;

			public string lpReserved;

			public string lpDesktop;

			public string lpTitle;

			public int dwX;

			public int dwY;

			public int dwXSize;

			public int dwYSize;

			public int dwXCountChars;

			public int dwYCountChars;

			public int dwFillAttribute;

			public int dwFlags;

			public short wShowWindow;

			public short cbReserved2;

			public IntPtr lpReserved2;

			public IntPtr hStdInput;

			public IntPtr hStdOutput;

			public IntPtr hStdError;
		}

		private struct ProcessInformation
		{
			public IntPtr hProcess;

			public IntPtr hThread;

			public int dwProcessId;

			public int dwThreadId;
		}

		private const uint TokenDuplicate = 2u;

		private const uint TokenQuery = 8u;

		private const uint TokenAllAccess = 983551u;

		private const uint LogonWithProfile = 1u;

		private const uint CreateUnicodeEnvironment = 1024u;

		private const int TokenPrimary = 1;

		private const int SecurityImpersonation = 2;

		[DllImport("user32.dll")]
		private static extern IntPtr GetShellWindow();

		[DllImport("user32.dll", SetLastError = true)]
		private static extern int GetWindowThreadProcessId(IntPtr window, out int processId);

		[DllImport("advapi32.dll", SetLastError = true)]
		private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

		[DllImport("advapi32.dll", SetLastError = true)]
		private static extern bool DuplicateTokenEx(IntPtr existingToken, uint desiredAccess, IntPtr attributes, int impersonationLevel, int tokenType, out IntPtr newToken);

		[DllImport("userenv.dll", SetLastError = true)]
		private static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);

		[DllImport("userenv.dll", SetLastError = true)]
		private static extern bool DestroyEnvironmentBlock(IntPtr environment);

		[DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
		private static extern bool CreateProcessWithTokenW(IntPtr token, uint logonFlags, string applicationName, StringBuilder commandLine, uint creationFlags, IntPtr environment, string currentDirectory, ref StartupInfo startupInfo, out ProcessInformation processInformation);

		public static bool Start(string executable)
		{
			if (string.IsNullOrEmpty(executable) || !File.Exists(executable))
			{
				return false;
			}
			string directoryName;
			try
			{
				directoryName = Path.GetDirectoryName(executable);
			}
			catch
			{
				return false;
			}
			if (StartWithShellToken(executable, directoryName))
			{
				return true;
			}
			return StartViaExplorer(executable);
		}

		private static bool StartWithShellToken(string executable, string workingDirectory)
		{
			IntPtr intPtr = IntPtr.Zero;
			IntPtr token = IntPtr.Zero;
			IntPtr newToken = IntPtr.Zero;
			IntPtr environment = IntPtr.Zero;
			ProcessInformation processInformation = default(ProcessInformation);
			try
			{
				IntPtr shellWindow = GetShellWindow();
				if (shellWindow == IntPtr.Zero)
				{
					return false;
				}
				int processId;
				GetWindowThreadProcessId(shellWindow, out processId);
				if (processId <= 0)
				{
					return false;
				}
				intPtr = Native.OpenProcess(4096, false, processId);
				if (intPtr == IntPtr.Zero)
				{
					return false;
				}
				if (!OpenProcessToken(intPtr, 10u, out token))
				{
					return false;
				}
				if (!DuplicateTokenEx(token, 983551u, IntPtr.Zero, 2, 1, out newToken))
				{
					return false;
				}
				StartupInfo startupInfo = default(StartupInfo);
				startupInfo.cb = Marshal.SizeOf(typeof(StartupInfo));
				startupInfo.lpDesktop = "winsta0\\default";
				StringBuilder commandLine = new StringBuilder("\"" + executable + "\"");
				if (!CreateEnvironmentBlock(out environment, newToken, false))
				{
					environment = IntPtr.Zero;
				}
				if (!CreateProcessWithTokenW(newToken, 1u, executable, commandLine, 1024u, environment, workingDirectory, ref startupInfo, out processInformation))
				{
					return false;
				}
				return true;
			}
			catch
			{
				return false;
			}
			finally
			{
				if (environment != IntPtr.Zero)
				{
					DestroyEnvironmentBlock(environment);
				}
				if (processInformation.hProcess != IntPtr.Zero)
				{
					Native.CloseHandle(processInformation.hProcess);
				}
				if (processInformation.hThread != IntPtr.Zero)
				{
					Native.CloseHandle(processInformation.hThread);
				}
				if (newToken != IntPtr.Zero)
				{
					Native.CloseHandle(newToken);
				}
				if (token != IntPtr.Zero)
				{
					Native.CloseHandle(token);
				}
				if (intPtr != IntPtr.Zero)
				{
					Native.CloseHandle(intPtr);
				}
			}
		}

		private static bool StartViaExplorer(string executable)
		{
			try
			{
				string fileNameValue = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
				if (!File.Exists(fileNameValue))
				{
					return false;
				}
				ProcessStartInfo processStartInfo = new ProcessStartInfo();
				processStartInfo.FileName = fileNameValue;
				processStartInfo.Arguments = "\"" + executable + "\"";
				processStartInfo.UseShellExecute = false;
				processStartInfo.CreateNoWindow = true;
				Process process = Process.Start(processStartInfo);
				if (process == null)
				{
					return false;
				}
				process.Dispose();
				return true;
			}
			catch
			{
				return false;
			}
		}
	}
}
