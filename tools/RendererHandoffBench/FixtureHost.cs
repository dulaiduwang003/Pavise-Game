using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

// 无害的短命进程树夹具 编译一次 在隔离的台架运行里把同一个二进制
// 复制成 GameLauncher.exe 和 GameRenderer.exe
// 这个辅助程序不检查 不启动 也不修改任何生产进程
// 协议
//   GameLauncher.exe --root --renderer 后面跟 GameRenderer.exe 的绝对路径
//     子进程就绪后 stdout 输出 READY|launcherPid|rendererPid
//   GameRenderer.exe --leaf
//     stdout 输出 READY|pid
//   两者都接受 stdin 的 exit 不分大小写 或者 EOF 表示请求干净退出
//     其余输入行忽略 出错走 stderr 的 ERROR|message
//   每个进程最多等 40 秒 root 的清理再多等 2 秒
internal static class FixtureHost
{
    private const int LifetimeMilliseconds = 40000;
    private const int ReadyTimeoutMilliseconds = 5000;
    private const int GracefulStopMilliseconds = 1500;
    private const int TotalStopMilliseconds = 2000;

    private static int Main(string[] args)
    {
        Stopwatch lifetime = Stopwatch.StartNew();
        try
        {
            if (args.Length == 1 && args[0] == "--leaf")
            {
                Task stopRequested = ReadStopRequest();
                Console.Out.WriteLine("READY|" + CurrentProcessId());
                Console.Out.Flush();
                stopRequested.Wait(Remaining(lifetime, LifetimeMilliseconds));
                return 0;
            }

            if (args.Length == 3 && args[0] == "--root" && args[1] == "--renderer")
            {
                return RunRoot(ValidateRendererPath(args[2]), lifetime);
            }

            WriteError("Usage: --root --renderer <absolute GameRenderer.exe path> or --leaf");
            return 2;
        }
        catch (Exception error)
        {
            WriteError(error.Message);
            return 1;
        }
    }

    private static int RunRoot(string rendererPath, Stopwatch lifetime)
    {
        Process child = new Process();
        bool started = false;
        IntPtr ownedHandle = IntPtr.Zero;
        int exitCode = 0;
        try
        {
            Task stopRequested = ReadStopRequest();
            if (stopRequested.IsCompleted || Remaining(lifetime, LifetimeMilliseconds) == 0)
            {
                return 0;
            }

            child.StartInfo = new ProcessStartInfo
            {
                FileName = rendererPath,
                Arguments = "--leaf",
                WorkingDirectory = Path.GetDirectoryName(rendererPath),
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = false
            };

            // 创建期间一直攥着只读租约 这样校验过的副本
            // 就没法在内容检查和 Process.Start 之间被掉包
            using (FileStream rendererFile = new FileStream(
                rendererPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                VerifyFixtureCopy(rendererFile);
                started = child.Start();
            }

            if (!started)
            {
                throw new InvalidOperationException("Could not start the owned renderer fixture.");
            }

            // 留住 Start 给的那个原生进程句柄 清理时不重新打开 PID
            // 也不按可执行文件名去找进程
            ownedHandle = child.Handle;
            int rendererPid = child.Id;
            Task<string> readyLine = ReadReadyLine(child);
            int readyBudget = Math.Min(ReadyTimeoutMilliseconds,
                Remaining(lifetime, LifetimeMilliseconds));
            int completed = Task.WaitAny(new Task[] { stopRequested, readyLine }, readyBudget);
            if (completed == -1)
            {
                throw new TimeoutException("The owned renderer fixture did not become ready.");
            }

            if (completed == 1)
            {
                string expected = "READY|" + rendererPid.ToString(CultureInfo.InvariantCulture);
                if (!String.Equals(readyLine.Result, expected, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Invalid renderer fixture readiness response.");
                }

                Console.Out.WriteLine(String.Format(CultureInfo.InvariantCulture,
                    "READY|{0}|{1}", CurrentProcessId(), rendererPid));
                Console.Out.Flush();
                stopRequested.Wait(Remaining(lifetime, LifetimeMilliseconds));
            }
        }
        catch (Exception error)
        {
            WriteError(error.Message);
            exitCode = 1;
        }
        finally
        {
            if (!StopOwnedChild(child, started, ownedHandle))
            {
                exitCode = 1;
            }
            child.Dispose();
        }

        return exitCode;
    }

    private static string ValidateRendererPath(string suppliedPath)
    {
        if (String.IsNullOrWhiteSpace(suppliedPath) || !Path.IsPathRooted(suppliedPath))
        {
            throw new ArgumentException("Renderer path must be a nonempty absolute local path.");
        }

        // UNC 和网络路径不收 C:foo.exe 这种盘符相对路径也不收
        string volume = Path.GetPathRoot(suppliedPath);
        if (volume.Length != 3 || volume[1] != ':' ||
            (volume[2] != Path.DirectorySeparatorChar && volume[2] != Path.AltDirectorySeparatorChar))
        {
            throw new ArgumentException("Renderer path must be an absolute local drive path.");
        }

        string rendererPath = Path.GetFullPath(suppliedPath);
        if (!String.Equals(Path.GetFileName(rendererPath), "GameRenderer.exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Only a fixture named GameRenderer.exe may be started.");
        }

        string launcherDirectory = Path.GetDirectoryName(Path.GetFullPath(SelfPath()));
        string rendererDirectory = Path.GetDirectoryName(rendererPath);
        bool sameDirectory = SamePath(launcherDirectory, rendererDirectory);
        bool menuToClient =
            String.Equals(Path.GetFileName(launcherDirectory), "Menu", StringComparison.OrdinalIgnoreCase) &&
            String.Equals(Path.GetFileName(rendererDirectory), "Client", StringComparison.OrdinalIgnoreCase) &&
            SamePath(Path.GetDirectoryName(launcherDirectory), Path.GetDirectoryName(rendererDirectory));

        if (!sameDirectory && !menuToClient)
        {
            throw new ArgumentException("Renderer must be beside the launcher or in its sibling Client directory from Menu.");
        }

        if (!File.Exists(rendererPath))
        {
            throw new FileNotFoundException("The renderer fixture copy does not exist.", rendererPath);
        }

        return rendererPath;
    }

    private static void VerifyFixtureCopy(Stream rendererFile)
    {
        byte[] launcherHash;
        byte[] rendererHash;
        using (SHA256 sha = SHA256.Create())
        using (FileStream launcherFile = new FileStream(SelfPath(), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            launcherHash = sha.ComputeHash(launcherFile);
            rendererHash = sha.ComputeHash(rendererFile);
        }

        for (int i = 0; i < launcherHash.Length; i++)
        {
            if (launcherHash[i] != rendererHash[i])
            {
                throw new InvalidOperationException("Renderer is not an identical copy of this harmless fixture helper.");
            }
        }
    }

    private static Task ReadStopRequest()
    {
        return Task.Factory.StartNew(delegate
        {
            try
            {
                string line;
                while ((line = Console.In.ReadLine()) != null)
                {
                    if (String.Equals(line.Trim(), "exit", StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }
                }
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    private static Task<string> ReadReadyLine(Process child)
    {
        return Task.Factory.StartNew(delegate
        {
            try { return child.StandardOutput.ReadLine(); }
            catch (IOException) { return null; }
            catch (ObjectDisposedException) { return null; }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    private static bool StopOwnedChild(Process child, bool started, IntPtr ownedHandle)
    {
        if (!started)
        {
            return true;
        }

        Stopwatch cleanup = Stopwatch.StartNew();
        try
        {
            if (!child.HasExited)
            {
                try
                {
                    child.StandardInput.WriteLine("exit");
                    child.StandardInput.Flush();
                    child.StandardInput.Close();
                }
                catch (IOException) { }
                catch (InvalidOperationException) { }

                child.WaitForExit(Remaining(cleanup, GracefulStopMilliseconds));
            }

            if (!child.HasExited)
            {
                if (ownedHandle == IntPtr.Zero || child.Handle != ownedHandle)
                {
                    WriteError("Owned renderer identity unavailable; refusing a replacement process lookup.");
                    return false;
                }

                child.Kill();
                child.WaitForExit(Remaining(cleanup, TotalStopMilliseconds));
            }

            if (!child.HasExited)
            {
                WriteError("Owned renderer did not terminate within the cleanup budget.");
                return false;
            }

            return true;
        }
        catch (Exception error)
        {
            WriteError("Owned renderer cleanup failed: " + error.Message);
            return false;
        }
    }

    private static int Remaining(Stopwatch elapsed, int budgetMilliseconds)
    {
        return (int)Math.Max(0L, budgetMilliseconds - elapsed.ElapsedMilliseconds);
    }

    private static bool SamePath(string left, string right)
    {
        return String.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static string SelfPath()
    {
        return Assembly.GetExecutingAssembly().Location;
    }

    private static string CurrentProcessId()
    {
        using (Process current = Process.GetCurrentProcess())
        {
            return current.Id.ToString(CultureInfo.InvariantCulture);
        }
    }

    private static void WriteError(string message)
    {
        Console.Error.WriteLine("ERROR|" + message.Replace('\r', ' ').Replace('\n', ' ').Replace('|', '/'));
        Console.Error.Flush();
    }
}
