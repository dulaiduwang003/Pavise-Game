using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

// Harmless short-lived process tree fixture, compiled once, the isolated bench run copies the same binary
// as GameLauncher.exe and GameRenderer.exe
// This helper never inspects, launches or modifies any production process
// Protocol
//   GameLauncher.exe --root --renderer followed by the absolute path of GameRenderer.exe
//     prints READY|launcherPid|rendererPid to stdout once the child is ready
//   GameRenderer.exe --leaf
//     prints READY|pid to stdout
//   Both accept a case-insensitive exit on stdin, or EOF, as a request for clean shutdown
//     other input lines are ignored, errors go to stderr as ERROR|message
//   Each process waits at most 40 seconds, root cleanup waits 2 seconds more
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

            // Hold the read-only lease throughout creation so the verified copy
            // cannot be swapped out between the content check and Process.Start
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

            // Keep the native process handle Start returned, cleanup never reopens by PID
            // nor looks the process up by executable name
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

        // UNC and network paths are rejected, so are drive-relative paths like C:foo.exe
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
