// @author bdth 2074055628@qq.com

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace AegisApp
{
    internal static class LolQuarantineManager
    {
        private const string WarehouseName = ".aegis-lol-quarantine";
        private const string LegacyWarehouseName = ".aegis-quarantine";
        private const string PayloadName = "payload";
        private const string ManifestName = "manifest.aegis";
        private const string LockFileName = ".operation.lock";
        private const string ManifestHeader = "AEGIS_LOL_QUARANTINE_V1";
        private static readonly object Gate = new object();
        private static readonly string[] CandidatePaths =
        {
            "Cross"
        };

        private static readonly string[] KnownPaths =
        {
            "Cross",
            @"LeagueClient\DiagnosticAssistant",
            @"LeagueClient\FeedBack",
            @"LeagueClient\NetworkAssist",
            @"LeagueClient\TQM",
            @"WeGameLauncher\TenioDL"
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

        internal sealed class QuarantineInfo
        {
            public string Name;
            public string Version;
            public DateTime CreatedUtc;
            public string ManifestPath;
            public long Bytes;
            public int ItemCount;
            public bool Discardable;
            public int MissingCount;
        }

        internal sealed class Inspection
        {
            public string RootPath;
            public bool IsValidRoot;
            public string Version;
            public readonly List<CandidateInfo> Candidates = new List<CandidateInfo>();
            public long CandidateBytes;
            public int CandidateCount;
            public readonly List<QuarantineInfo> Active = new List<QuarantineInfo>();
            public long QuarantinedBytes;
            public bool IsBlocked;
            public readonly List<string> BlockingProcesses = new List<string>();
            public string Error;

            public bool CanQuarantine
            {
                get
                {
                    return IsValidRoot && string.IsNullOrEmpty(Error) && !IsBlocked
                        && Active.Count == 0 && CandidateCount > 0;
                }
            }

            public bool CanRestore
            {
                get
                {
                    return IsValidRoot && string.IsNullOrEmpty(Error) && !IsBlocked
                        && Active.Count == 1;
                }
            }

            public bool CanDiscard
            {
                get
                {
                    return IsValidRoot && !IsBlocked && Active.Count == 1 && Active[0].Discardable;
                }
            }
        }

        internal sealed class OperationItem
        {
            public string RelativePath;
            public string SourcePath;
            public string DestinationPath;
            public long Bytes;
            public bool Success;
            public string Message;
        }

        internal sealed class OperationResult
        {
            public bool Success;
            public bool Changed;
            public long Bytes;
            public int MovedCount;
            public int FailedCount;
            public string SetName;
            public string Message;
            public readonly List<OperationItem> Items = new List<OperationItem>();
        }

        private sealed class ManifestItem
        {
            public string RelativePath;
            public long Bytes;
            public int FileCount;
        }

        private sealed class ManifestData
        {
            public string State;
            public string RootPath;
            public string Version;
            public long CreatedUtcTicks;
            public readonly List<ManifestItem> Items = new List<ManifestItem>();
        }

        private static string WarehousePath(string root)
        {
            string parent;
            try { parent = Path.GetDirectoryName(Path.GetFullPath(root)); }
            catch { return null; }
            if (string.IsNullOrEmpty(parent)) return null;
            return Path.Combine(parent, WarehouseName);
        }

        private static string LegacyWarehousePath(string root)
        {
            try { return Path.Combine(Path.GetFullPath(root), LegacyWarehouseName); }
            catch { return null; }
        }

        public static string FindRoot()
        {
            string root;
            string error;
            string discovered = null;
            try { discovered = LolInstallDiscovery.FindLolRoot(null); } catch { }
            return TryResolveRoot(discovered, out root, out error) ? root : null;
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
            inspection.Version = DetectVersion(normalized);

            List<string> blocking;
            inspection.IsBlocked = HasBlockingProcesses(normalized, out blocking);
            inspection.BlockingProcesses.AddRange(blocking);

            string storageError;
            List<QuarantineInfo> active = ReadActiveQuarantines(normalized, out storageError);
            inspection.Active.AddRange(active);
            foreach (QuarantineInfo item in active)
                inspection.QuarantinedBytes = AddSaturated(inspection.QuarantinedBytes, item.Bytes);

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

            inspection.Error = JoinError(inspection.Error, storageError);
            return inspection;
        }

        public static bool HasBlockingProcesses(string root, out List<string> names)
        {
            names = new List<string>();
            string normalized;
            string error;
            if (!TryResolveRoot(root, out normalized, out error)) return false;
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

        public static OperationResult Quarantine(string root)
        {
            lock (Gate)
            {
                Inspection initial = Inspect(root);
                OperationResult rejected = RejectQuarantine(initial);
                if (rejected != null) return rejected;

                FileStream operationLock;
                string lockError;
                if (!TryAcquireWarehouse(initial.RootPath, out operationLock, out lockError))
                    return Failure(lockError);

                OperationResult result;
                using (operationLock)
                {
                    result = QuarantineLocked(initial.RootPath);
                    SweepDeadSets(initial.RootPath);
                }
                TryRemoveEmptyWarehouse(initial.RootPath);
                return result;
            }
        }

        private static OperationResult QuarantineLocked(string rootPath)
        {
            Inspection current = Inspect(rootPath);
            OperationResult rejected = RejectQuarantine(current);
            if (rejected != null) return rejected;

            string warehouse = WarehousePath(current.RootPath);
            if (string.IsNullOrEmpty(warehouse))
                return Failure(Lang.T("lolq.err.warehousefile"));
            string setName = BuildSetName(current.Version, warehouse);
            string setPath = Path.Combine(warehouse, setName);
            string payloadPath = Path.Combine(setPath, PayloadName);
            string manifestPath = Path.Combine(setPath, ManifestName);
            var manifest = new ManifestData();
            manifest.State = "active";
            manifest.RootPath = current.RootPath;
            manifest.Version = current.Version;
            manifest.CreatedUtcTicks = DateTime.UtcNow.Ticks;
            foreach (CandidateInfo candidate in current.Candidates)
            {
                if (!candidate.Exists) continue;
                var manifestItem = new ManifestItem();
                manifestItem.RelativePath = candidate.RelativePath;
                manifestItem.Bytes = candidate.Bytes;
                manifestItem.FileCount = candidate.FileCount;
                manifest.Items.Add(manifestItem);
            }

            try
            {
                Directory.CreateDirectory(payloadPath);
                EnsureSafeParent(warehouse, payloadPath);
            }
            catch (Exception ex)
            {
                DeleteTree(new DirectoryInfo(setPath));
                return Failure(Lang.F("lolq.err.createset", ex.Message));
            }

            if (!WriteManifest(manifestPath, manifest))
            {
                DeleteTree(new DirectoryInfo(setPath));
                return Failure(Lang.T("lolq.err.writemanifest"));
            }

            var result = new OperationResult();
            result.SetName = setName;
            foreach (ManifestItem item in manifest.Items)
            {
                var operationItem = NewOperationItem(current.RootPath, setPath, item);
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
                if (Directory.Exists(operationItem.DestinationPath) || File.Exists(operationItem.DestinationPath))
                {
                    operationItem.Message = Lang.T("lolq.item.destexists");
                    result.FailedCount++;
                    continue;
                }

                try
                {
                    EnsureSafeParent(
                        current.RootPath, Path.GetDirectoryName(operationItem.SourcePath));
                    if (!SafeDirectory(operationItem.SourcePath))
                        throw new IOException(Lang.T("lolq.err.srcreparse"));
                    string destinationParent = Path.GetDirectoryName(operationItem.DestinationPath);
                    Directory.CreateDirectory(destinationParent);
                    EnsureSafeParent(warehouse, destinationParent);
                    EnsureSameVolume(operationItem.SourcePath, operationItem.DestinationPath);
                    Directory.Move(operationItem.SourcePath, operationItem.DestinationPath);
                    operationItem.Success = true;
                    operationItem.Message = Lang.T("lolq.item.quarantined");
                    result.MovedCount++;
                    result.Bytes = AddSaturated(result.Bytes, item.Bytes);
                    result.Changed = true;
                }
                catch (Exception ex)
                {
                    operationItem.Message = Lang.F("lolq.item.quarantinefail", ex.Message);
                    result.FailedCount++;
                }
            }

            if (!result.Changed)
            {
                if (!DeleteTree(new DirectoryInfo(setPath)))
                {
                    manifest.State = "failed";
                    WriteManifest(manifestPath, manifest);
                }
            }
            else if (result.FailedCount > 0)
            {
                var moved = new List<ManifestItem>();
                foreach (OperationItem oi in result.Items)
                {
                    if (!oi.Success) continue;
                    foreach (ManifestItem mi in manifest.Items)
                        if (string.Equals(mi.RelativePath, oi.RelativePath, StringComparison.OrdinalIgnoreCase))
                            moved.Add(mi);
                }
                if (moved.Count > 0 && moved.Count != manifest.Items.Count)
                {
                    manifest.Items.Clear();
                    manifest.Items.AddRange(moved);
                    WriteManifest(manifestPath, manifest);
                }
            }
            result.Success = result.Changed && result.FailedCount == 0;
            result.Message = result.Success
                ? Lang.T("lolq.msg.qdone")
                : result.Changed
                    ? Lang.T("lolq.msg.qpartial")
                    : Lang.T("lolq.msg.qnone");
            return result;
        }

        public static OperationResult Restore(string root)
        {
            Inspection inspection = Inspect(root);
            OperationResult rejected = RejectRestore(inspection, null);
            if (rejected != null) return rejected;
            return Restore(root, inspection.Active[0].Name);
        }

        public static OperationResult Restore(string root, string setName)
        {
            lock (Gate)
            {
                Inspection initial = Inspect(root);
                OperationResult rejected = RejectRestore(initial, setName);
                if (rejected != null) return rejected;

                FileStream operationLock;
                string lockError;
                if (!TryAcquireWarehouse(initial.RootPath, out operationLock, out lockError))
                    return Failure(lockError);

                OperationResult result;
                using (operationLock)
                {
                    result = RestoreLocked(initial.RootPath, setName);
                    SweepDeadSets(initial.RootPath);
                }
                TryRemoveEmptyWarehouse(initial.RootPath);
                return result;
            }
        }

        private static OperationResult RestoreLocked(string rootPath, string setName)
        {
            Inspection current = Inspect(rootPath);
            OperationResult rejected = RejectRestore(current, setName);
            if (rejected != null) return rejected;

            QuarantineInfo selected = null;
            foreach (QuarantineInfo info in current.Active)
                if (string.Equals(info.Name, setName, StringComparison.OrdinalIgnoreCase)) selected = info;
            if (selected == null) return Failure(Lang.T("lolq.err.nosuchset"));

            string setPath = Path.GetDirectoryName(selected.ManifestPath);
            try
            {
                EnsureSafeParent(Path.GetDirectoryName(setPath), setPath);
                if (!SafeDirectory(setPath)) return Failure(Lang.T("lolq.err.setreparse"));
            }
            catch (Exception ex)
            {
                return Failure(Lang.F("lolq.err.setinvalidex", ex.Message));
            }
            ManifestData manifest;
            string manifestError;
            if (!TryReadManifest(selected.ManifestPath, out manifest, out manifestError))
                return Failure(Lang.F("lolq.err.manifestunavailable", manifestError));
            if (!SamePath(manifest.RootPath, current.RootPath))
                return Failure(Lang.T("lolq.err.manifestmismatch"));

            var result = new OperationResult();
            result.SetName = selected.Name;
            bool missingContent = false;
            foreach (ManifestItem item in manifest.Items)
            {
                var operationItem = NewRestoreItem(current.RootPath, setPath, item);
                result.Items.Add(operationItem);

                List<string> blocking;
                if (HasBlockingProcesses(current.RootPath, out blocking))
                {
                    operationItem.Message = Lang.F("lolq.err.clientrunning", string.Join("、", blocking.ToArray()));
                    result.FailedCount++;
                    continue;
                }

                bool sourceDirectory = Directory.Exists(operationItem.SourcePath);
                bool sourceFile = File.Exists(operationItem.SourcePath);
                bool destinationDirectory = Directory.Exists(operationItem.DestinationPath);
                bool destinationFile = File.Exists(operationItem.DestinationPath);
                if (sourceFile)
                {
                    operationItem.Message = Lang.T("lolq.item.notdir");
                    result.FailedCount++;
                    continue;
                }
                if (!sourceDirectory)
                {
                    if (destinationDirectory || destinationFile)
                    {
                        operationItem.Success = true;
                        operationItem.Message = Lang.T("lolq.item.alreadyinplace");
                    }
                    else
                    {
                        operationItem.Message = Lang.T("lolq.item.payloadmissing");
                        result.FailedCount++;
                        missingContent = true;
                    }
                    continue;
                }
                if (destinationDirectory || destinationFile)
                {
                    operationItem.Message = Lang.T("lolq.item.occupied");
                    result.FailedCount++;
                    continue;
                }

                try
                {
                    EnsureSafeParent(
                        setPath, Path.GetDirectoryName(operationItem.SourcePath));
                    if (!SafeDirectory(operationItem.SourcePath))
                        throw new IOException(Lang.T("lolq.err.payloadreparse"));
                    string destinationParent = Path.GetDirectoryName(operationItem.DestinationPath);
                    if (File.Exists(destinationParent)) throw new IOException(Lang.T("lolq.err.parentisfile"));
                    EnsureSafeParent(current.RootPath, destinationParent);
                    Directory.CreateDirectory(destinationParent);
                    EnsureSafeParent(current.RootPath, destinationParent);
                    EnsureSameVolume(operationItem.SourcePath, operationItem.DestinationPath);
                    Directory.Move(operationItem.SourcePath, operationItem.DestinationPath);
                    operationItem.Success = true;
                    operationItem.Message = Lang.T("lolq.item.restored");
                    result.MovedCount++;
                    result.Bytes = AddSaturated(result.Bytes, item.Bytes);
                    result.Changed = true;
                }
                catch (Exception ex)
                {
                    operationItem.Message = Lang.F("lolq.item.restorefail", ex.Message);
                    result.FailedCount++;
                }
            }

            bool remains = false;
            foreach (ManifestItem item in manifest.Items)
            {
                string payload = CombineRelative(Path.Combine(setPath, PayloadName), item.RelativePath);
                if (Directory.Exists(payload) || File.Exists(payload)) remains = true;
            }
            if (!remains && !missingContent)
            {
                if (!DeleteTree(new DirectoryInfo(setPath)))
                {
                    manifest.State = "restored";
                    if (!WriteManifest(selected.ManifestPath, manifest))
                    {
                        result.FailedCount++;
                        result.Message = Lang.T("lolq.msg.rstatefail");
                    }
                }
            }

            result.Success = !remains && result.FailedCount == 0;
            if (string.IsNullOrEmpty(result.Message))
                result.Message = missingContent
                    ? Lang.T("lolq.msg.rmissing")
                    : result.Success
                    ? Lang.T("lolq.msg.rdone")
                    : result.Changed
                        ? Lang.T("lolq.msg.rpartial")
                        : Lang.T("lolq.msg.rnone");
            return result;
        }

        public static OperationResult Discard(string root, string setName)
        {
            lock (Gate)
            {
                Inspection initial = Inspect(root);
                OperationResult rejected = RejectDiscard(initial, setName);
                if (rejected != null) return rejected;

                FileStream operationLock;
                string lockError;
                if (!TryAcquireWarehouse(initial.RootPath, out operationLock, out lockError))
                    return Failure(lockError);

                OperationResult result;
                using (operationLock)
                {
                    result = DiscardLocked(initial.RootPath, setName);
                    SweepDeadSets(initial.RootPath);
                }
                TryRemoveEmptyWarehouse(initial.RootPath);
                return result;
            }
        }

        private static OperationResult DiscardLocked(string rootPath, string setName)
        {
            Inspection current = Inspect(rootPath);
            OperationResult rejected = RejectDiscard(current, setName);
            if (rejected != null) return rejected;

            QuarantineInfo selected = null;
            foreach (QuarantineInfo info in current.Active)
                if (string.Equals(info.Name, setName, StringComparison.OrdinalIgnoreCase)) selected = info;
            if (selected == null) return Failure(Lang.T("lolq.err.nosuchset"));
            if (!selected.Discardable)
                return Failure(Lang.T("lolq.err.stillunique"));

            string setPath = Path.GetDirectoryName(selected.ManifestPath);
            string warehouse = Path.GetDirectoryName(setPath);
            try
            {
                EnsureSafeParent(warehouse, setPath);
                if (!SafeDirectory(setPath)) return Failure(Lang.T("lolq.err.setreparse"));
                if (SamePath(setPath, warehouse)) return Failure(Lang.T("lolq.err.setinvalid"));
            }
            catch (Exception ex)
            {
                return Failure(Lang.F("lolq.err.setinvalidex", ex.Message));
            }

            ManifestData manifest;
            string manifestError;
            if (!TryReadManifest(selected.ManifestPath, out manifest, out manifestError))
                return Failure(Lang.F("lolq.err.manifestunavailable", manifestError));
            if (!SamePath(manifest.RootPath, current.RootPath))
                return Failure(Lang.T("lolq.err.manifestmismatch"));

            var result = new OperationResult();
            result.SetName = selected.Name;
            foreach (ManifestItem item in manifest.Items)
            {
                string payload = CombineRelative(Path.Combine(setPath, PayloadName), item.RelativePath);
                string original = CombineRelative(current.RootPath, item.RelativePath);
                if (!Directory.Exists(payload) && !File.Exists(payload)) continue;
                if (!Directory.Exists(original) && !File.Exists(original))
                    return Failure(Lang.T("lolq.err.stillunique"));
            }

            if (!DeleteTree(new DirectoryInfo(setPath)))
            {
                result.FailedCount++;
                result.Message = Lang.T("lolq.msg.discardpartial");
                return result;
            }

            result.Changed = true;
            result.Success = true;
            result.Bytes = selected.Bytes;
            result.Message = Lang.T("lolq.msg.discarddone");
            return result;
        }

        private static void SweepDeadSets(string rootPath)
        {
            SweepDeadSetsIn(WarehousePath(rootPath));
            SweepDeadSetsIn(LegacyWarehousePath(rootPath));
        }

        private static void SweepDeadSetsIn(string warehousePath)
        {
            if (string.IsNullOrEmpty(warehousePath)) return;
            DirectoryInfo[] sets;
            try
            {
                var warehouse = new DirectoryInfo(warehousePath);
                warehouse.Refresh();
                if (!warehouse.Exists || IsReparse(warehouse)) return;
                sets = warehouse.GetDirectories();
            }
            catch { return; }
            foreach (DirectoryInfo set in sets)
            {
                try
                {
                    set.Refresh();
                    if (IsReparse(set)) continue;
                    ManifestData manifest;
                    string manifestError;
                    if (!TryReadManifest(Path.Combine(set.FullName, ManifestName), out manifest, out manifestError))
                        continue;
                    if (string.Equals(manifest.State, "active", StringComparison.Ordinal)) continue;
                    if (PayloadHasContents(set.FullName)) continue;
                    DeleteTree(set);
                }
                catch { }
            }
        }

        private static void TryRemoveEmptyWarehouse(string rootPath)
        {
            TryRemoveEmptyWarehouseAt(WarehousePath(rootPath));
            TryRemoveEmptyWarehouseAt(LegacyWarehousePath(rootPath));
        }

        private static void TryRemoveEmptyWarehouseAt(string warehousePath)
        {
            if (string.IsNullOrEmpty(warehousePath)) return;
            try
            {
                var warehouse = new DirectoryInfo(warehousePath);
                warehouse.Refresh();
                if (!warehouse.Exists || IsReparse(warehouse)) return;
                if (warehouse.GetDirectories().Length > 0) return;
                FileInfo[] files = warehouse.GetFiles();
                foreach (FileInfo file in files)
                    if (!string.Equals(file.Name, LockFileName, StringComparison.OrdinalIgnoreCase)) return;
                foreach (FileInfo file in files)
                {
                    file.Refresh();
                    if ((file.Attributes & (FileAttributes.ReadOnly | FileAttributes.Hidden
                        | FileAttributes.System)) != 0)
                        file.Attributes = FileAttributes.Normal;
                    file.Delete();
                }
                warehouse.Refresh();
                if (!warehouse.Exists) return;
                if ((warehouse.Attributes & (FileAttributes.ReadOnly | FileAttributes.Hidden
                    | FileAttributes.System)) != 0)
                    warehouse.Attributes = FileAttributes.Normal;
                warehouse.Delete(false);
            }
            catch { }
        }

        private static OperationResult RejectDiscard(Inspection inspection, string setName)
        {
            if (!inspection.IsValidRoot) return Failure(inspection.Error);
            if (inspection.IsBlocked)
                return Failure(Lang.F("lolq.err.closefirst", string.Join("、", inspection.BlockingProcesses.ToArray())));
            if (inspection.Active.Count == 0) return Failure(Lang.T("lolq.err.nodiscard"));
            if (string.IsNullOrEmpty(setName)) return Failure(Lang.T("lolq.err.nosetname"));
            foreach (QuarantineInfo info in inspection.Active)
                if (string.Equals(info.Name, setName, StringComparison.OrdinalIgnoreCase)) return null;
            return Failure(Lang.T("lolq.err.nosuchset"));
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

        internal static bool SelfTestManifest(out string error)
        {
            error = null;
            var source = new ManifestData();
            source.State = "active";
            source.RootPath = @"E:\测试|目录";
            source.Version = "16.14=测试";
            source.CreatedUtcTicks = DateTime.UtcNow.Ticks;
            source.Items.Add(new ManifestItem
            {
                RelativePath = @"LeagueClient\FeedBack",
                Bytes = 123456789,
                FileCount = 42
            });
            string[] lines = ManifestLines(source);
            ManifestData parsed;
            if (!TryParseManifest(lines, out parsed, out error)) return false;
            if (parsed.State != source.State || parsed.RootPath != source.RootPath
                || parsed.Version != source.Version || parsed.CreatedUtcTicks != source.CreatedUtcTicks
                || parsed.Items.Count != 1 || parsed.Items[0].RelativePath != source.Items[0].RelativePath
                || parsed.Items[0].Bytes != source.Items[0].Bytes
                || parsed.Items[0].FileCount != source.Items[0].FileCount)
            {
                error = "quarantine manifest round-trip mismatch";
                return false;
            }
            return true;
        }

        private static OperationResult RejectQuarantine(Inspection inspection)
        {
            if (!inspection.IsValidRoot) return Failure(inspection.Error);
            if (!string.IsNullOrEmpty(inspection.Error)) return Failure(inspection.Error);
            if (inspection.IsBlocked)
                return Failure(Lang.F("lolq.err.closefirst", string.Join("、", inspection.BlockingProcesses.ToArray())));
            if (inspection.Active.Count > 0) return Failure(Lang.T("lolq.err.hasactive"));
            if (inspection.CandidateCount == 0) return Failure(Lang.T("lolq.err.nocandidate"));
            return null;
        }

        private static OperationResult RejectRestore(Inspection inspection, string setName)
        {
            if (!inspection.IsValidRoot) return Failure(inspection.Error);
            if (!string.IsNullOrEmpty(inspection.Error)) return Failure(inspection.Error);
            if (inspection.IsBlocked)
                return Failure(Lang.F("lolq.err.closefirst", string.Join("、", inspection.BlockingProcesses.ToArray())));
            if (inspection.Active.Count == 0) return Failure(Lang.T("lolq.err.norestore"));
            if (string.IsNullOrEmpty(setName) && inspection.Active.Count != 1)
                return Failure(Lang.T("lolq.err.multiset"));
            if (!string.IsNullOrEmpty(setName))
            {
                bool found = false;
                foreach (QuarantineInfo info in inspection.Active)
                    if (string.Equals(info.Name, setName, StringComparison.OrdinalIgnoreCase)) found = true;
                if (!found) return Failure(Lang.T("lolq.err.nosuchset"));
            }
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

        private static string DetectVersion(string root)
        {
            string[] executables =
            {
                Path.Combine(root, @"LeagueClient\LeagueClient.exe"),
                Path.Combine(root, @"Game\League of Legends.exe"),
                Path.Combine(root, @"TCLS\Client.exe"),
                Path.Combine(root, @"Launcher\Client.exe")
            };
            foreach (string executable in executables)
            {
                try
                {
                    if (!File.Exists(executable)) continue;
                    FileVersionInfo info = FileVersionInfo.GetVersionInfo(executable);
                    string value = !string.IsNullOrWhiteSpace(info.FileVersion)
                        ? info.FileVersion : info.ProductVersion;
                    if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
                }
                catch { }
            }
            return "unknown";
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

        private static bool TryAcquireWarehouse(string root, out FileStream operationLock, out string error)
        {
            operationLock = null;
            error = null;
            string warehouse = WarehousePath(root);
            if (string.IsNullOrEmpty(warehouse))
            {
                error = Lang.T("lolq.err.warehousefile");
                return false;
            }
            try
            {
                if (File.Exists(warehouse))
                {
                    error = Lang.T("lolq.err.warehousefile");
                    return false;
                }
                Directory.CreateDirectory(warehouse);
                var directory = new DirectoryInfo(warehouse);
                directory.Refresh();
                if (IsReparse(directory))
                {
                    error = Lang.T("lolq.err.warehousereparse");
                    return false;
                }
                directory.Attributes = directory.Attributes | FileAttributes.Hidden;
                operationLock = new FileStream(Path.Combine(warehouse, LockFileName),
                    FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                return true;
            }
            catch (Exception ex)
            {
                if (operationLock != null)
                {
                    try { operationLock.Dispose(); } catch { }
                    operationLock = null;
                }
                error = Lang.F("lolq.err.warehouselocked", ex.Message);
                return false;
            }
        }

        private static List<QuarantineInfo> ReadActiveQuarantines(string root, out string error)
        {
            error = null;
            var active = new List<QuarantineInfo>();
            string primaryError;
            ReadWarehouse(root, WarehousePath(root), active, out primaryError);
            string legacyError;
            ReadWarehouse(root, LegacyWarehousePath(root), active, out legacyError);
            error = JoinError(primaryError, legacyError);
            active.Sort(delegate(QuarantineInfo left, QuarantineInfo right)
            {
                return right.CreatedUtc.CompareTo(left.CreatedUtc);
            });
            return active;
        }

        private static void ReadWarehouse(
            string root, string warehouse, List<QuarantineInfo> active, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(warehouse)) return;
            if (File.Exists(warehouse))
            {
                error = Lang.T("lolq.err.warehousefile");
                return;
            }
            if (!Directory.Exists(warehouse)) return;

            DirectoryInfo warehouseInfo;
            try
            {
                warehouseInfo = new DirectoryInfo(warehouse);
                warehouseInfo.Refresh();
                if (IsReparse(warehouseInfo))
                {
                    error = Lang.T("lolq.err.warehousereparse");
                    return;
                }
            }
            catch (Exception ex)
            {
                error = Lang.F("lolq.err.readwarehouse", ex.Message);
                return;
            }

            DirectoryInfo[] sets;
            try { sets = warehouseInfo.GetDirectories(); }
            catch (Exception ex)
            {
                error = Lang.F("lolq.err.enumsets", ex.Message);
                return;
            }
            foreach (DirectoryInfo set in sets)
            {
                try
                {
                    set.Refresh();
                    if (IsReparse(set))
                    {
                        error = JoinError(error, Lang.F("lolq.err.setisreparse", set.Name));
                        continue;
                    }
                    string manifestPath = Path.Combine(set.FullName, ManifestName);
                    ManifestData manifest;
                    string manifestError;
                    if (!TryReadManifest(manifestPath, out manifest, out manifestError))
                    {
                        if (PayloadHasContents(set.FullName))
                            error = JoinError(error, Lang.F("lolq.err.setbadmanifest", set.Name, manifestError));
                        continue;
                    }
                    if (!SamePath(root, manifest.RootPath))
                    {
                        if (PayloadHasContents(set.FullName))
                            error = JoinError(error, Lang.F("lolq.err.setrootmismatch", set.Name));
                        continue;
                    }

                    long bytes = 0;
                    int count = 0;
                    int missing = 0;
                    int recoverable = 0;
                    foreach (ManifestItem item in manifest.Items)
                    {
                        string payload = CombineRelative(Path.Combine(set.FullName, PayloadName), item.RelativePath);
                        string original = CombineRelative(root, item.RelativePath);
                        bool inPayload = Directory.Exists(payload) || File.Exists(payload);
                        bool inPlace = Directory.Exists(original) || File.Exists(original);
                        if (inPayload)
                        {
                            bytes = AddSaturated(bytes, item.Bytes);
                            count++;
                            if (!inPlace) recoverable++;
                        }
                        else if (!inPlace) missing++;
                    }
                    if (!string.Equals(manifest.State, "active", StringComparison.Ordinal))
                    {
                        if (count > 0)
                            error = JoinError(error, Lang.F("lolq.err.setstatemismatch", set.Name));
                        continue;
                    }
                    var info = new QuarantineInfo();
                    info.MissingCount = missing;
                    info.Name = set.Name;
                    info.Version = manifest.Version;
                    info.CreatedUtc = SafeUtc(manifest.CreatedUtcTicks);
                    info.ManifestPath = manifestPath;
                    info.Bytes = bytes;
                    info.ItemCount = manifest.Items.Count;
                    info.Discardable = recoverable == 0;
                    active.Add(info);
                }
                catch (Exception ex)
                {
                    error = JoinError(error, Lang.F("lolq.err.setreadfail", set.Name, ex.Message));
                }
            }
        }

        private static bool WriteManifest(string path, ManifestData manifest)
        {
            return AtomicFile.WriteLines(path, ManifestLines(manifest), "英雄联盟隔离清单");
        }

        private static string[] ManifestLines(ManifestData manifest)
        {
            var lines = new List<string>();
            lines.Add(ManifestHeader);
            lines.Add("state=" + manifest.State);
            lines.Add("root64=" + Encode(manifest.RootPath));
            lines.Add("version64=" + Encode(manifest.Version));
            lines.Add("createdUtcTicks=" + manifest.CreatedUtcTicks);
            foreach (ManifestItem item in manifest.Items)
                lines.Add("item=" + item.Bytes + "|" + item.FileCount + "|" + Encode(item.RelativePath));
            return lines.ToArray();
        }

        private static bool TryReadManifest(string path, out ManifestData manifest, out string error)
        {
            manifest = null;
            error = null;
            if (!File.Exists(path))
            {
                error = Lang.T("lolq.err.manifestmissing");
                return false;
            }
            var lines = new List<string>();
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream, new UTF8Encoding(false, true), true))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (line.Length > 8192 || lines.Count >= 64)
                        {
                            error = Lang.T("lolq.err.manifestlong");
                            return false;
                        }
                        lines.Add(line);
                    }
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            return TryParseManifest(lines.ToArray(), out manifest, out error);
        }

        private static bool TryParseManifest(string[] lines, out ManifestData manifest, out string error)
        {
            manifest = null;
            error = null;
            if (lines == null || lines.Length < 5 || lines[0] != ManifestHeader)
            {
                error = Lang.T("lolq.err.manifestformat");
                return false;
            }

            var parsed = new ManifestData();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int index = 1; index < lines.Length; index++)
            {
                string line = lines[index];
                if (line.StartsWith("state=", StringComparison.Ordinal))
                {
                    parsed.State = line.Substring(6);
                    continue;
                }
                if (line.StartsWith("root64=", StringComparison.Ordinal))
                {
                    if (!TryDecode(line.Substring(7), out parsed.RootPath))
                    {
                        error = Lang.T("lolq.err.rootencoding");
                        return false;
                    }
                    continue;
                }
                if (line.StartsWith("version64=", StringComparison.Ordinal))
                {
                    if (!TryDecode(line.Substring(10), out parsed.Version))
                    {
                        error = Lang.T("lolq.err.versionencoding");
                        return false;
                    }
                    continue;
                }
                if (line.StartsWith("createdUtcTicks=", StringComparison.Ordinal))
                {
                    if (!long.TryParse(line.Substring(16), out parsed.CreatedUtcTicks))
                    {
                        error = Lang.T("lolq.err.timefield");
                        return false;
                    }
                    continue;
                }
                if (!line.StartsWith("item=", StringComparison.Ordinal))
                {
                    error = Lang.T("lolq.err.unknownfield");
                    return false;
                }

                string[] fields = line.Substring(5).Split(new[] { '|' }, 3);
                long bytes;
                int files;
                string relative;
                if (fields.Length != 3 || !long.TryParse(fields[0], out bytes) || bytes < 0
                    || !int.TryParse(fields[1], out files) || files < 0
                    || !TryDecode(fields[2], out relative) || !IsAllowedRelativePath(relative)
                    || !seen.Add(relative))
                {
                    error = Lang.T("lolq.err.baditem");
                    return false;
                }
                parsed.Items.Add(new ManifestItem
                {
                    RelativePath = relative,
                    Bytes = bytes,
                    FileCount = files
                });
            }

            if (parsed.State != "active" && parsed.State != "restored" && parsed.State != "failed")
            {
                error = Lang.T("lolq.err.badstate");
                return false;
            }
            if (string.IsNullOrEmpty(parsed.RootPath) || string.IsNullOrEmpty(parsed.Version)
                || parsed.CreatedUtcTicks <= 0 || parsed.Items.Count == 0)
            {
                error = Lang.T("lolq.err.missingfield");
                return false;
            }
            manifest = parsed;
            return true;
        }

        private static OperationItem NewOperationItem(string root, string setPath, ManifestItem item)
        {
            var result = new OperationItem();
            result.RelativePath = item.RelativePath;
            result.SourcePath = CombineRelative(root, item.RelativePath);
            result.DestinationPath = CombineRelative(Path.Combine(setPath, PayloadName), item.RelativePath);
            result.Bytes = item.Bytes;
            return result;
        }

        private static OperationItem NewRestoreItem(string root, string setPath, ManifestItem item)
        {
            var result = new OperationItem();
            result.RelativePath = item.RelativePath;
            result.SourcePath = CombineRelative(Path.Combine(setPath, PayloadName), item.RelativePath);
            result.DestinationPath = CombineRelative(root, item.RelativePath);
            result.Bytes = item.Bytes;
            return result;
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

        private static string BuildSetName(string version, string warehouse)
        {
            string safeVersion = SafeSegment(version);
            string stamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ");
            string basis = safeVersion + "_" + stamp;
            string candidate = basis;
            int suffix = 2;
            while (Directory.Exists(Path.Combine(warehouse, candidate)) || File.Exists(Path.Combine(warehouse, candidate)))
            {
                candidate = basis + "_" + suffix;
                suffix++;
            }
            return candidate;
        }

        private static string SafeSegment(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "unknown";
            char[] invalid = Path.GetInvalidFileNameChars();
            var output = new StringBuilder();
            foreach (char c in value.Trim())
            {
                bool blocked = char.IsControl(c);
                foreach (char bad in invalid)
                    if (c == bad) blocked = true;
                output.Append(blocked || char.IsWhiteSpace(c) ? '_' : c);
                if (output.Length >= 80) break;
            }
            string result = output.ToString().Trim('.', '_');
            return result.Length == 0 ? "unknown" : result;
        }

        private static string Encode(string value)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? ""));
        }

        private static bool TryDecode(string value, out string decoded)
        {
            decoded = null;
            try
            {
                if (value == null || value.Length > 4096) return false;
                byte[] bytes = Convert.FromBase64String(value);
                if (bytes.Length > 3072) return false;
                decoded = new UTF8Encoding(false, true).GetString(bytes);
                return true;
            }
            catch { return false; }
        }

        private static bool IsAllowedRelativePath(string relativePath)
        {
            foreach (string candidate in KnownPaths)
                if (string.Equals(candidate, relativePath, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
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

        private static void EnsureSameVolume(string source, string destination)
        {
            string sourceRoot = Path.GetPathRoot(Path.GetFullPath(source));
            string destinationRoot = Path.GetPathRoot(Path.GetFullPath(destination));
            if (!string.Equals(sourceRoot, destinationRoot, StringComparison.OrdinalIgnoreCase))
                throw new IOException(Lang.T("lolq.err.crossvolume"));
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

        private static bool PayloadHasContents(string setPath)
        {
            string payload = Path.Combine(setPath, PayloadName);
            try
            {
                if (!Directory.Exists(payload)) return false;
                using (IEnumerator<string> entries = Directory.EnumerateFileSystemEntries(payload).GetEnumerator())
                    return entries.MoveNext();
            }
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

        private static DateTime SafeUtc(long ticks)
        {
            try { return new DateTime(ticks, DateTimeKind.Utc); }
            catch { return DateTime.MinValue; }
        }
    }
}
