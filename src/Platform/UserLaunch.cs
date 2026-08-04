// @author bdth 2074055628@qq.com
// 文件用途 以登录用户而非管理员身份启动外部程序

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal static class UserLaunch
    {
        private const uint TokenDuplicate = 0x0002;
        private const uint TokenQuery = 0x0008;
        private const uint TokenAllAccess = 0xF01FF;
        private const uint LogonWithProfile = 0x00000001;
        private const uint CreateUnicodeEnvironment = 0x00000400;
        private const int TokenPrimary = 1;
        private const int SecurityImpersonation = 2;

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

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessInformation
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int dwProcessId;
            public int dwThreadId;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetShellWindow();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowThreadProcessId(IntPtr window, out int processId);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool DuplicateTokenEx(
            IntPtr existingToken, uint desiredAccess, IntPtr attributes,
            int impersonationLevel, int tokenType, out IntPtr newToken);

        [DllImport("userenv.dll", SetLastError = true)]
        private static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);

        [DllImport("userenv.dll", SetLastError = true)]
        private static extern bool DestroyEnvironmentBlock(IntPtr environment);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateProcessWithTokenW(
            IntPtr token, uint logonFlags, string applicationName, string commandLine,
            uint creationFlags, IntPtr environment, string currentDirectory,
            ref StartupInfo startupInfo, out ProcessInformation processInformation);

        public static bool Start(string executable)
        {
            if (string.IsNullOrEmpty(executable) || !File.Exists(executable)) return false;
            string workingDirectory;
            try { workingDirectory = Path.GetDirectoryName(executable); }
            catch { return false; }
            if (StartWithShellToken(executable, workingDirectory)) return true;
            return StartViaExplorer(executable);
        }

        private static bool StartWithShellToken(string executable, string workingDirectory)
        {
            IntPtr shell = IntPtr.Zero;
            IntPtr shellToken = IntPtr.Zero;
            IntPtr primaryToken = IntPtr.Zero;
            IntPtr environment = IntPtr.Zero;
            var info = new ProcessInformation();
            try
            {
                IntPtr window = GetShellWindow();
                if (window == IntPtr.Zero) return false;
                int shellPid;
                GetWindowThreadProcessId(window, out shellPid);
                if (shellPid <= 0) return false;

                shell = Native.OpenProcess(
                    Native.PROCESS_QUERY_LIMITED_INFORMATION, false, shellPid);
                if (shell == IntPtr.Zero) return false;
                if (!OpenProcessToken(shell, TokenDuplicate | TokenQuery, out shellToken))
                    return false;
                if (!DuplicateTokenEx(shellToken, TokenAllAccess, IntPtr.Zero,
                    SecurityImpersonation, TokenPrimary, out primaryToken))
                    return false;

                var startup = new StartupInfo();
                startup.cb = Marshal.SizeOf(typeof(StartupInfo));
                startup.lpDesktop = @"winsta0\default";
                string commandLine = "\"" + executable + "\"";
                if (!CreateEnvironmentBlock(out environment, primaryToken, false))
                    environment = IntPtr.Zero;
                if (!CreateProcessWithTokenW(primaryToken, LogonWithProfile, executable,
                    commandLine, CreateUnicodeEnvironment, environment, workingDirectory,
                    ref startup, out info))
                    return false;
                return true;
            }
            catch { return false; }
            finally
            {
                if (environment != IntPtr.Zero) DestroyEnvironmentBlock(environment);
                if (info.hProcess != IntPtr.Zero) Native.CloseHandle(info.hProcess);
                if (info.hThread != IntPtr.Zero) Native.CloseHandle(info.hThread);
                if (primaryToken != IntPtr.Zero) Native.CloseHandle(primaryToken);
                if (shellToken != IntPtr.Zero) Native.CloseHandle(shellToken);
                if (shell != IntPtr.Zero) Native.CloseHandle(shell);
            }
        }

        private static bool StartViaExplorer(string executable)
        {
            try
            {
                string explorer = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
                if (!File.Exists(explorer)) return false;
                var startInfo = new ProcessStartInfo();
                startInfo.FileName = explorer;
                startInfo.Arguments = "\"" + executable + "\"";
                startInfo.UseShellExecute = false;
                startInfo.CreateNoWindow = true;
                Process process = Process.Start(startInfo);
                if (process == null) return false;
                process.Dispose();
                return true;
            }
            catch { return false; }
        }

    }
}
