using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace PaviseApp
{
    internal sealed class CacheWarmEnvironment
    {
        internal bool Ac;
        internal ulong Total, Available;
        internal bool Ready { get { return Ac && Total > 0 && Available >= 4UL * 1024 * 1024 * 1024 && Available >= Total / 4; } }
        internal long Budget { get { return !Ready ? 0 : (long)Math.Min(2UL * 1024 * 1024 * 1024, Math.Min(Total / 16, (Available - 4UL * 1024 * 1024 * 1024) / 2)); } }
    }

    internal static class CacheWarmPlatform
    {
        [StructLayout(LayoutKind.Sequential)] private struct Memory
        { internal uint Length, Load; internal ulong Total, Available, PageTotal, PageAvailable, VirtualTotal, VirtualAvailable, Extended; }
        [StructLayout(LayoutKind.Sequential)] private struct Power
        { internal byte Ac, Flags, Percent, Saver; internal uint Life, FullLife; }
        [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref Memory value);
        [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out Power value);
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentThread();
        [DllImport("kernel32.dll")] private static extern bool SetThreadPriority(IntPtr thread, int priority);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, int inputSize, byte[] output, int outputSize, out int returned, IntPtr overlapped);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint size, uint flags);

        internal static CacheWarmEnvironment Sample()
        {
            var m = new Memory { Length = (uint)Marshal.SizeOf(typeof(Memory)) }; Power p;
            if (!GlobalMemoryStatusEx(ref m) || !GetSystemPowerStatus(out p)) return new CacheWarmEnvironment();
            return new CacheWarmEnvironment { Ac = p.Ac == 1 && p.Saver == 0, Total = m.Total, Available = m.Available };
        }
        internal static bool Background(bool begin) { return SetThreadPriority(GetCurrentThread(), begin ? 0x10000 : 0x20000); }
        internal static bool SolidState(string root)
        {
            try
            {
                string drive = Path.GetPathRoot(root);
                if (drive.Length != 3 || new DriveInfo(drive).DriveType != DriveType.Fixed) return false;
                using (var handle = CreateFile(@"\\.\" + drive.Substring(0, 2), 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero))
                {
                    if (handle.IsInvalid) return false;
                    // StorageDeviceSeekPenaltyProperty Unknown/unsupported is ineligible
                    byte[] query = new byte[12], answer = new byte[12]; query[0] = 7; int count;
                    return DeviceIoControl(handle, 0x2D1400, query, query.Length, answer, answer.Length, out count, IntPtr.Zero)
                        && count >= 9 && BitConverter.ToUInt32(answer, 4) >= 9 && answer[8] == 0;
                }
            }
            catch { return false; }
        }
        internal static bool SafePath(string path)
        {
            try
            {
                for (string part = Path.GetFullPath(path); !string.IsNullOrEmpty(part); part = Path.GetDirectoryName(part))
                    if ((File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0) return false;
                return true;
            }
            catch { return false; }
        }
        internal static bool HandleUnder(FileStream stream, string root)
        {
            var path = new StringBuilder(32768);
            uint n = GetFinalPathNameByHandle(stream.SafeFileHandle, path, (uint)path.Capacity, 0);
            if (n == 0 || n >= path.Capacity) return false;
            string resolved = path.ToString();
            if (resolved.StartsWith(@"\\?\", StringComparison.Ordinal)) resolved = resolved.Substring(4);
            return resolved.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
        }
    }

    internal static class CacheWarmEngine
    {
        internal const int ChunkBytes = 1024 * 1024;
        private static readonly HashSet<string> AssetExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".pak", ".ucas", ".utoc", ".vpk", ".wad", ".wad.client", ".bundle", ".assets", ".resource", ".resS", ".ba2", ".bsa", ".rpf", ".archive" };
        internal static bool Asset(string path)
        { return AssetExtensions.Contains(Path.GetExtension(path)) || path.EndsWith(".wad.client", StringComparison.OrdinalIgnoreCase); }
        private static bool SkipDirectory(string name)
        {
            string n = name.ToLowerInvariant();
            return n == "logs" || n == "crash" || n == "cache" || n == "shadercache" || n == "__installer"
                || n == "easyanticheat" || n == "battleye" || n == "anticheat" || n == "ace" || n == "tensafe";
        }
        internal static List<string> Candidates(string root, Func<bool> allowed)
        {
            var files = new List<FileInfo>();
            var pending = new Queue<KeyValuePair<string, int>>();
            pending.Enqueue(new KeyValuePair<string, int>(root, 0));
            var watch = Stopwatch.StartNew(); int entries = 0;
            while (pending.Count > 0 && entries < 5000 && watch.ElapsedMilliseconds < 10000 && allowed())
            {
                var dir = pending.Dequeue();
                if (!CacheWarmPlatform.SafePath(dir.Key)) continue;
                try
                {
                    foreach (string path in Directory.EnumerateFileSystemEntries(dir.Key))
                    {
                        if (++entries > 5000 || watch.ElapsedMilliseconds >= 10000 || !allowed()) break;
                        try
                        {
                            FileAttributes attributes = File.GetAttributes(path);
                            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Offline)) != 0) continue;
                            if ((attributes & FileAttributes.Directory) != 0)
                            {
                                if (dir.Value < 8 && pending.Count < 512 && !SkipDirectory(Path.GetFileName(path)))
                                    pending.Enqueue(new KeyValuePair<string, int>(path, dir.Value + 1));
                            }
                            else if (Asset(path))
                            {
                                var info = new FileInfo(path);
                                if (info.Length >= ChunkBytes) files.Add(info);
                            }
                        }
                        catch (IOException) { } catch (UnauthorizedAccessException) { }
                    }
                }
                catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            files.Sort(delegate(FileInfo a, FileInfo b) { int n = b.LastAccessTimeUtc.CompareTo(a.LastAccessTimeUtc); return n != 0 ? n : StringComparer.OrdinalIgnoreCase.Compare(a.FullName, b.FullName); });
            var result = new List<string>(); foreach (var file in files) result.Add(file.FullName); return result;
        }
        // The engine only reads ordinary asset files No executable loading
        // process access shader compilation memory locking or cache purging
        internal static long Warm(string root, long budget, Func<bool> allowed, Func<int, bool> wait, Action<long> progress)
        {
            long total = 0; byte[] buffer = new byte[ChunkBytes];
            foreach (string path in Candidates(root, allowed))
            {
                if (total >= budget || !allowed()) break;
                if (!CacheWarmPlatform.SafePath(path)) continue;
                try
                {
                    // Do not use SequentialScan its cache-behind eviction hint
                    // conflicts with retaining recently read data for later use
                    using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.None))
                    {
                        if (!CacheWarmPlatform.HandleUnder(stream, root)) continue;
                        long fileBudget = Math.Min(stream.Length, 256L * 1024 * 1024), fileRead = 0;
                        while (total < budget && fileRead < fileBudget && allowed())
                        {
                            int count = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, Math.Min(budget - total, fileBudget - fileRead)));
                            if (count == 0) break;
                            total += count; fileRead += count; progress(total);
                            // 1 MiB per >=32 ms at most ~31 MiB/s excluding I/O
                            if (wait(32)) return total;
                        }
                    }
                }
                catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            return total;
        }
    }

    internal sealed class CacheWarmRunner
    {
        private readonly object gate = new object();
        private Thread worker;
        private ManualResetEvent cancel;
        private string attempted;
        private string status = "cachewarm.idle";
        internal string Status { get { lock (gate) return status; } }
        private void SetStatus(string value) { lock (gate) status = value; }
        private void StopAttempt(ManualResetEvent token)
        {
            lock (gate)
            {
                if (!object.ReferenceEquals(cancel, token)) return;
                status = "cachewarm.stopped";
                // Cancellation or a temporarily failed admission does not count as done; the same identity should get another warm-up chance after recovery
                attempted = null;
            }
        }
        internal void Update(string key, string executable, string root, Func<bool> allowed)
        {
            lock (gate)
            {
                if (worker != null && worker.IsAlive)
                {
                    if (!string.Equals(key, attempted, StringComparison.OrdinalIgnoreCase))
                    {
                        // A new path must not be blocked by the old task's 90 s wait, so clear the attempt marker
                        // Even path G -> H -> G restarts G once the cancel has drained
                        if (cancel != null) cancel.Set();
                        attempted = null;
                    }
                    return;
                }
                if (string.Equals(key, attempted, StringComparison.OrdinalIgnoreCase)) return;
                if (cancel != null) cancel.Dispose();
                attempted = key; cancel = new ManualResetEvent(false);
                ManualResetEvent token = cancel;
                status = "cachewarm.waiting";
                worker = new Thread(delegate()
                {
                    bool background = false;
                    try
                    {
                        if (token.WaitOne(90000) || !allowed()) { StopAttempt(token); return; }
                        background = CacheWarmPlatform.Background(true);
                        if (!background) { SetStatus("cachewarm.skipped"); return; }
                        string safeRoot = GameInstallScope.RestrictFallback(executable, root);
                        var environment = CacheWarmPlatform.Sample();
                        if (safeRoot == null || !CacheWarmPlatform.SafePath(safeRoot) || !CacheWarmPlatform.SolidState(safeRoot)
                            || !environment.Ready || environment.Budget < CacheWarmEngine.ChunkBytes)
                        { SetStatus("cachewarm.skipped"); return; }
                        Func<bool> mayRead = delegate { return !token.WaitOne(0) && allowed() && CacheWarmPlatform.Sample().Ready; };
                        SetStatus("cachewarm.running");
                        long bytes = CacheWarmEngine.Warm(safeRoot, environment.Budget, mayRead, token.WaitOne, delegate { });
                        // Attempts that reached the read phase keep their dedup record, so that fluctuating memory pressure does not
                        // re-read the same files and reset the budget every 90 s; a path or session change still retries under a new key
                        SetStatus(mayRead() ? "cachewarm.done" : "cachewarm.stopped");
                        Logger.Log(Lang.T("gm.cachewarm") + " · " + Lang.T(Status) + " · " + (bytes / 1048576) + " MiB");
                    }
                    catch (Exception ex) { SetStatus("cachewarm.skipped"); Logger.Log(Lang.T("gm.cachewarm") + " · " + ex.Message); }
                    finally { if (background) CacheWarmPlatform.Background(false); }
                }) { IsBackground = true, Name = "Pavise cache warm-up" };
                worker.Start();
            }
        }
        internal void Cancel(bool retry)
        { lock (gate) { if (cancel != null) cancel.Set(); if (retry) attempted = null; } }
        internal bool Drain(int milliseconds)
        {
            Thread thread; lock (gate) { if (cancel != null) cancel.Set(); thread = worker; }
            bool stopped = thread == null || thread == Thread.CurrentThread || thread.Join(milliseconds);
            lock (gate) if (stopped && worker == thread) { worker = null; if (cancel != null) { cancel.Dispose(); cancel = null; } }
            return stopped;
        }
    }
}
