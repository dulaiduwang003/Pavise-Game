// @author bdth 2074055628@qq.com
// 文件用途 定位并直接删除英雄联盟附加层目录（客户端更新会重新下载这些组件）

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace PaviseApp
{
    internal static class LolAddonCleaner
    {
        private static readonly object Gate = new object();
        private static readonly string[] CandidatePaths =
        {
            "Cross"
        };

        internal sealed class CandidateInfo
        {
            public string RelativePath;
            public string FullPath;
            public bool Exists;
            public bool IsSafe;
            public long Bytes;
            public int FileCount;
            public string Error;
        }

        internal sealed class Inspection
        {
            public string RootPath;
            public bool IsValidRoot;
            public readonly List<CandidateInfo> Candidates = new List<CandidateInfo>();
            public long CandidateBytes;
            public int CandidateCount;
            public bool IsBlocked;
            public readonly List<string> BlockingProcesses = new List<string>();
            public string Error;

            public bool CanDelete
            {
                get
                {
                    return IsValidRoot && string.IsNullOrEmpty(Error) && !IsBlocked
                        && CandidateCount > 0;
                }
            }
        }

        internal sealed class OperationItem
        {
            public string RelativePath;
            public string SourcePath;
            public long Bytes;
            public bool Success;
            public string Message;
        }

        internal sealed class OperationResult
        {
            public bool Success;
            public bool Changed;
            public long Bytes;
            public int DeletedCount;
            public int FailedCount;
            public string Message;
            public readonly List<OperationItem> Items = new List<OperationItem>();
        }

        public static bool TryResolveRoot(string input, out string root, out string error)
        {
            root = null;
            error = null;
            if (string.IsNullOrWhiteSpace(input))
            {
                error = Lang.T("lolq.err.noinput");
                return false;
            }

            string current;
            try
            {
                current = Environment.ExpandEnvironmentVariables(input.Trim().Trim('"'));
                current = File.Exists(current) ? Path.GetDirectoryName(Path.GetFullPath(current)) : Path.GetFullPath(current);
            }
            catch
            {
                error = Lang.T("lolq.err.badformat");
                return false;
            }

            for (int depth = 0; depth < 12 && !string.IsNullOrEmpty(current); depth++)
            {
                string shapeError;
                if (IsLolRootShape(current, out shapeError))
                {
                    root = current.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    return true;
                }

                string parent;
                try { parent = Directory.GetParent(current) == null ? null : Directory.GetParent(current).FullName; }
                catch { parent = null; }
                if (string.IsNullOrEmpty(parent) || SamePath(parent, current)) break;
                current = parent;
            }

            error = Lang.T("lolq.err.notroot");
            return false;
        }

        public static Inspection Inspect(string root)
        {
            var inspection = new Inspection();
            string normalized;
            string error;
            if (!TryResolveRoot(root, out normalized, out error))
            {
                inspection.Error = error;
                return inspection;
            }

            inspection.RootPath = normalized;
            inspection.IsValidRoot = true;

            List<string> blocking;
            inspection.IsBlocked = HasBlockingProcesses(normalized, out blocking);
            inspection.BlockingProcesses.AddRange(blocking);

            foreach (string relativePath in CandidatePaths)
            {
                CandidateInfo candidate = ScanCandidate(normalized, relativePath);
                inspection.Candidates.Add(candidate);
                if (!candidate.Exists) continue;
                inspection.CandidateCount++;
                inspection.CandidateBytes = AddSaturated(inspection.CandidateBytes, candidate.Bytes);
                if (!candidate.IsSafe)
                    inspection.Error = JoinError(inspection.Error, candidate.RelativePath + "：" + candidate.Error);
            }

            return inspection;
        }

        public static bool HasBlockingProcesses(string root, out List<string> names)
        {
            names = new List<string>();
            string normalized;
            string error;
            if (!TryResolveRoot(root, out normalized, out error))
            {
                names.Add(Lang.T("lolq.err.rootunresolved"));
                return true;
            }
            string prefix = EnsureSeparator(normalized);

            Process[] processes;
            try { processes = Process.GetProcesses(); }
            catch
            {
                names.Add(Lang.T("lolq.err.enumproc"));
                return true;
            }

            try
            {
                foreach (Process process in processes)
                {
                    string name = null;
                    int pid = 0;
                    string path = null;
                    try { name = process.ProcessName; } catch { }
                    try { pid = process.Id; } catch { }
                    try { path = ProcessPath(process); } catch { }
                    if (!IsBlockingProcess(name, path, prefix)) continue;
                    names.Add((string.IsNullOrEmpty(name) ? Lang.T("lolq.proc.unknown") : name) + " (PID " + pid + ")");
                }
            }
            finally
            {
                foreach (Process process in processes)
                    try { process.Dispose(); } catch { }
            }
            return names.Count > 0;
        }

        public static OperationResult Delete(string root)
        {
            lock (Gate)
            {
                Inspection current = Inspect(root);
                OperationResult rejected = RejectDelete(current);
                if (rejected != null) return rejected;

                var result = new OperationResult();
                foreach (CandidateInfo candidate in current.Candidates)
                {
                    if (!candidate.Exists) continue;
                    var operationItem = new OperationItem();
                    operationItem.RelativePath = candidate.RelativePath;
                    operationItem.SourcePath = candidate.FullPath;
                    operationItem.Bytes = candidate.Bytes;
                    result.Items.Add(operationItem);

                    List<string> blocking;
                    if (HasBlockingProcesses(current.RootPath, out blocking))
                    {
                        operationItem.Message = Lang.F("lolq.err.clientrunning", string.Join("、", blocking.ToArray()));
                        result.FailedCount++;
                        continue;
                    }

                    if (File.Exists(operationItem.SourcePath))
                    {
                        operationItem.Message = Lang.T("lolq.item.notdir");
                        result.FailedCount++;
                        continue;
                    }
                    if (!Directory.Exists(operationItem.SourcePath))
                    {
                        operationItem.Message = Lang.T("lolq.item.vanished");
                        result.FailedCount++;
                        continue;
                    }

                    try
                    {
                        EnsureSafeParent(
                            current.RootPath, Path.GetDirectoryName(operationItem.SourcePath));
                        if (!SafeDirectory(operationItem.SourcePath))
                            throw new IOException(Lang.T("lolq.err.srcreparse"));
                        DeleteTree(new DirectoryInfo(operationItem.SourcePath));
                        if (Directory.Exists(operationItem.SourcePath) || File.Exists(operationItem.SourcePath))
                            throw new IOException(Lang.T("lolq.err.deleteincomplete"));
                        operationItem.Success = true;
                        operationItem.Message = Lang.T("lolq.item.deleted");
                        result.DeletedCount++;
                        result.Bytes = AddSaturated(result.Bytes, candidate.Bytes);
                        result.Changed = true;
                    }
                    catch (Exception ex)
                    {
                        operationItem.Message = Lang.F("lolq.item.deletefail", ex.Message);
                        result.FailedCount++;
                    }
                }

                result.Success = result.Changed && result.FailedCount == 0;
                result.Message = result.Success
                    ? Lang.T("lolq.msg.deldone")
                    : result.Changed
                        ? Lang.T("lolq.msg.delpartial")
                        : Lang.T("lolq.msg.delnone");
                return result;
            }
        }

        private static OperationResult RejectDelete(Inspection inspection)
        {
            if (!inspection.IsValidRoot) return Failure(inspection.Error);
            if (!string.IsNullOrEmpty(inspection.Error)) return Failure(inspection.Error);
            if (inspection.IsBlocked)
                return Failure(Lang.F("lolq.err.closefirst", string.Join("、", inspection.BlockingProcesses.ToArray())));
            if (inspection.CandidateCount == 0) return Failure(Lang.T("lolq.err.nocandidate"));
            return null;
        }

        private static OperationResult Failure(string message)
        {
            var result = new OperationResult();
            result.Message = string.IsNullOrEmpty(message) ? Lang.T("lolq.err.generic") : message;
            result.FailedCount = 1;
            return result;
        }

        private static bool IsLolRootShape(string path, out string error)
        {
            error = null;
            try
            {
                var root = new DirectoryInfo(path);
                if (!root.Exists) return false;
                root.Refresh();
                if (IsReparse(root)) return false;
                if (SamePath(root.FullName, Path.GetPathRoot(root.FullName))) return false;

                string game = Path.Combine(root.FullName, "Game");
                string client = Path.Combine(root.FullName, "LeagueClient");
                if (!SafeDirectory(game) || !SafeDirectory(client)) return false;
                if (!File.Exists(Path.Combine(client, "LeagueClient.exe"))) return false;

                bool launcher = File.Exists(Path.Combine(root.FullName, @"TCLS\Client.exe"))
                    || File.Exists(Path.Combine(root.FullName, @"Launcher\Client.exe"))
                    || File.Exists(Path.Combine(root.FullName, @"WeGameLauncher\launcher.exe"))
                    || File.Exists(Path.Combine(root.FullName, @"Riot Client\RiotClientServices.exe"));
                return launcher;
            }
            catch { return false; }
        }

        private static CandidateInfo ScanCandidate(string root, string relativePath)
        {
            var result = new CandidateInfo();
            result.RelativePath = relativePath;
            result.FullPath = CombineRelative(root, relativePath);
            result.IsSafe = true;
            try
            {
                if (File.Exists(result.FullPath))
                {
                    result.Exists = true;
                    result.IsSafe = false;
                    result.Error = Lang.T("lolq.err.candnotdir");
                    return result;
                }
                var directory = new DirectoryInfo(result.FullPath);
                if (!directory.Exists) return result;
                EnsureSafeParent(root, Path.GetDirectoryName(result.FullPath));
                result.Exists = true;
                directory.Refresh();
                if (IsReparse(directory))
                {
                    result.IsSafe = false;
                    result.Error = Lang.T("lolq.err.candreparse");
                    return result;
                }
                string measureError;
                if (!MeasureDirectory(directory, out result.Bytes, out result.FileCount, out measureError))
                {
                    result.IsSafe = false;
                    result.Error = measureError;
                }
            }
            catch (Exception ex)
            {
                result.Exists = true;
                result.IsSafe = false;
                result.Error = ex.Message;
            }
            return result;
        }

        private static bool MeasureDirectory(DirectoryInfo root, out long bytes, out int fileCount, out string error)
        {
            bytes = 0;
            fileCount = 0;
            error = null;
            var pending = new Stack<DirectoryInfo>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                DirectoryInfo current = pending.Pop();
                FileInfo[] files;
                DirectoryInfo[] directories;
                try
                {
                    files = current.GetFiles();
                    directories = current.GetDirectories();
                }
                catch (Exception ex)
                {
                    error = Lang.F("lolq.err.scanfail", ex.Message);
                    return false;
                }

                foreach (FileInfo file in files)
                {
                    try
                    {
                        file.Refresh();
                        if (IsReparse(file))
                        {
                            error = Lang.F("lolq.err.filereparse", file.Name);
                            return false;
                        }
                        bytes = AddSaturated(bytes, file.Length);
                        if (fileCount < int.MaxValue) fileCount++;
                    }
                    catch (Exception ex)
                    {
                        error = Lang.F("lolq.err.readfile", ex.Message);
                        return false;
                    }
                }
                foreach (DirectoryInfo directory in directories)
                {
                    try
                    {
                        directory.Refresh();
                        if (IsReparse(directory))
                        {
                            error = Lang.F("lolq.err.dirreparse", directory.Name);
                            return false;
                        }
                        pending.Push(directory);
                    }
                    catch (Exception ex)
                    {
                        error = Lang.F("lolq.err.readdir", ex.Message);
                        return false;
                    }
                }
            }
            return true;
        }

        private static bool DeleteTree(DirectoryInfo directory)
        {
            bool ok = true;
            FileInfo[] files;
            DirectoryInfo[] children;
            try
            {
                directory.Refresh();
                if (!directory.Exists) return true;
                if (IsReparse(directory))
                {
                    directory.Delete(false);
                    return true;
                }
                files = directory.GetFiles();
                children = directory.GetDirectories();
            }
            catch { return false; }

            foreach (FileInfo file in files)
            {
                try
                {
                    file.Refresh();
                    if ((file.Attributes & (FileAttributes.ReadOnly | FileAttributes.Hidden
                        | FileAttributes.System)) != 0)
                        file.Attributes = FileAttributes.Normal;
                    file.Delete();
                }
                catch { ok = false; }
            }
            foreach (DirectoryInfo child in children)
                if (!DeleteTree(child)) ok = false;

            try
            {
                directory.Refresh();
                if (!directory.Exists) return ok;
                if ((directory.Attributes & (FileAttributes.ReadOnly | FileAttributes.Hidden
                    | FileAttributes.System)) != 0)
                    directory.Attributes = FileAttributes.Normal;
                directory.Delete(false);
            }
            catch { ok = false; }
            return ok;
        }

        private static bool IsBlockingProcess(string name, string path, string rootPrefix)
        {
            string normalizedName = string.IsNullOrEmpty(name) ? "" : name.Trim().ToLowerInvariant();
            bool knownName = normalizedName.StartsWith("wegame", StringComparison.Ordinal)
                || normalizedName == "pallas" || normalizedName == "rail"
                || normalizedName == "tcls_core" || normalizedName == "riotclientservices"
                || normalizedName == "leagueclient" || normalizedName == "leagueclientux"
                || normalizedName == "leagueclientuxrender" || normalizedName == "league of legends"
                || normalizedName == "crossproxy" || normalizedName == "lolaicoach"
                || normalizedName == "aicoachapp" || normalizedName == "icreatelol"
                || normalizedName == "tqmcenter" || normalizedName == "yxqxunyou"
                || normalizedName == "sguard64";

            string full = null;
            if (!string.IsNullOrEmpty(path))
            {
                try { full = Path.GetFullPath(path); }
                catch { full = path; }
            }
            if (full != null && full.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)) return true;
            if (knownName) return true;
            if (full == null) return false;
            string slashPath = full.Replace('/', '\\');
            return slashPath.IndexOf(@"\WeGame\", StringComparison.OrdinalIgnoreCase) >= 0
                && (normalizedName == "browser" || normalizedName == "teniodl"
                    || normalizedName == "crashpad_handler" || normalizedName == "tgp_daemon");
        }

        private static string ProcessPath(Process process)
        {
            IntPtr handle = IntPtr.Zero;
            try
            {
                handle = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, process.Id);
                if (handle != IntPtr.Zero)
                {
                    string path = Native.ImagePath(handle);
                    if (!string.IsNullOrEmpty(path)) return path;
                }
            }
            catch { }
            finally
            {
                if (handle != IntPtr.Zero) Native.CloseHandle(handle);
            }
            try { return process.MainModule == null ? null : process.MainModule.FileName; }
            catch { return null; }
        }

        private static string CombineRelative(string root, string relativePath)
        {
            string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string combined = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
            if (!combined.StartsWith(EnsureSeparator(fullRoot), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(Lang.T("lolq.err.outsideroot"));
            return combined;
        }

        private static void EnsureSafeParent(string root, string parent)
        {
            string normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string current = Path.GetFullPath(parent);
            if (!current.StartsWith(EnsureSeparator(normalizedRoot), StringComparison.OrdinalIgnoreCase)
                && !SamePath(current, normalizedRoot))
                throw new InvalidOperationException(Lang.T("lolq.err.outsiderootr"));
            while (!SamePath(current, normalizedRoot))
            {
                var info = new DirectoryInfo(current);
                if (info.Exists && IsReparse(info)) throw new IOException(Lang.T("lolq.err.pathreparse"));
                DirectoryInfo upper = info.Parent;
                if (upper == null) throw new IOException(Lang.T("lolq.err.pathinvalid"));
                current = upper.FullName;
            }
        }

        private static bool SafeDirectory(string path)
        {
            try
            {
                var info = new DirectoryInfo(path);
                if (!info.Exists) return false;
                info.Refresh();
                return !IsReparse(info);
            }
            catch { return false; }
        }

        private static bool IsReparse(FileSystemInfo info)
        {
            try { return (info.Attributes & FileAttributes.ReparsePoint) != 0; }
            catch { return true; }
        }

        private static string EnsureSeparator(string path)
        {
            return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
        }

        private static bool SamePath(string left, string right)
        {
            if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right)) return false;
            try
            {
                left = Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                right = Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch { return false; }
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }

        private static long AddSaturated(long left, long right)
        {
            if (right <= 0) return left;
            return left > long.MaxValue - right ? long.MaxValue : left + right;
        }

        private static string JoinError(string current, string next)
        {
            if (string.IsNullOrEmpty(next)) return current;
            return string.IsNullOrEmpty(current) ? next : current + "；" + next;
        }
    }
}
