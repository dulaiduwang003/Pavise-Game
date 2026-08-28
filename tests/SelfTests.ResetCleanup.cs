// Isolated reset correctness: unique temporary files, mock restore/registry,
// no real HKCU deletion, optimization loop, process restoration or game launch.
#if PAVISE_SELFTEST
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        internal static int RunResetCleanupRegressionTests()
        {
            string root = Path.Combine(Path.GetTempPath(), "PaviseResetCleanup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string oldLog = Logger.LogPath;
            Func<List<string>> oldRestore = LegacyPurge.RestoreHook;
            Func<bool> oldRegistry = LegacyPurge.DeleteRegistryHook;
            bool oldSkip = LegacyPurge.SkipRegistryDelete;
            Action<string>[] tests =
            {
                ResetRestoreFailureKeepsBothStores,
                ResetRestoreExceptionKeepsBothStores,
                ResetRestoreNullKeepsBothStores,
                ResetPendingProcessResidueKeepsBothStores,
                ResetProcessResidueExceptionKeepsBothStores,
                ResetLockedFileKeepsRegistry,
                ResetRegistryFailureIsNotSuccess,
                ResetRegistryExceptionIsNotSuccess,
                ResetPortableOnlyDeletesOwnedData,
                ResetReadOnlyOwnedFileIsCleared,
                ResetDataOnlyDoesNotTouchRegistry,
                ResetMissingDirectoryIsAlreadyClear,
                ResetUnexpectedOwnedDirectoryIsPreserved,
                ResetRootJunctionIsRefused,
                ResetTreeJunctionCannotReachOutside,
                ResetTreeLockedFileReportsResidual,
                ResetTreeAlreadyGoneIsIdempotent,
                ResetInvalidPathsDoNotStartRestoration,
                ResetLogsCannotRecreateDeletedData
            };
            try
            {
                foreach (Action<string> test in tests)
                {
                    Settings.UseTransientStoreForCurrentProcess();
                    Logger.ResetWriteBarrierForTest();
                    Logger.LogPath = null;
                    Lang.Cur = 0;
                    LegacyPurge.RestoreHook = delegate { return new List<string>(); };
                    LegacyPurge.SkipRegistryDelete = false;
                    // Every scenario must explicitly replace this sentinel; if a
                    // new test forgets, it fails rather than reaching real HKCU.
                    LegacyPurge.DeleteRegistryHook = delegate
                    { throw new InvalidOperationException("Unexpected registry operation in isolated reset test"); };
                    test(root);
                    Console.WriteLine("PASS " + test.Method.Name);
                }
                return tests.Length;
            }
            finally
            {
                LegacyPurge.RestoreHook = oldRestore;
                LegacyPurge.DeleteRegistryHook = oldRegistry;
                LegacyPurge.SkipRegistryDelete = oldSkip;
                Logger.ResetWriteBarrierForTest();
                Logger.LogPath = oldLog;
                Settings.UseTransientStoreForCurrentProcess();
                ResetDeleteScratch(root);
            }
        }

        private static void ResetCheck(bool condition, string message)
        {
            if (!condition) throw new Exception("Reset cleanup regression: " + message);
        }

        private static string ResetDirectory(string root, string name)
        {
            string path = Path.GetFullPath(Path.Combine(root, name));
            ResetCheck(path.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase), "fixture path escaped its root");
            Directory.CreateDirectory(path);
            return path;
        }

        private static string ResetFile(string directory, string name)
        {
            string path = Path.Combine(directory, name);
            File.WriteAllText(path, "owned-fixture-data");
            return path;
        }

        private static void ResetExpectRestoreRejected(string root, string name, Func<List<string>> restore)
        {
            string dir = ResetDirectory(root, name);
            string profile = ResetFile(dir, GameProfileStore.FileName);
            string journal = ResetFile(dir, SuppressionCore.StateFileName);
            string userFile = ResetFile(dir, "user-save.sav");
            int registryCalls = 0;
            LegacyPurge.DeleteRegistryHook = delegate { registryCalls++; return true; };
            LegacyPurge.RestoreHook = restore;
            int files;
            string error;
            ResetCheck(!LegacyPurge.WipeAll(dir, true, "isolated-reset", out files, out error), "restore rejection was reported successful");
            ResetCheck(files == 0 && registryCalls == 0, "restore rejection proceeded to destructive cleanup");
            ResetCheck(!string.IsNullOrEmpty(error) && error.Contains("还原"), "restore failure did not explain its phase");
            ResetCheck(File.ReadAllText(profile) == "owned-fixture-data"
                && File.ReadAllText(journal) == "owned-fixture-data"
                && File.ReadAllText(userFile) == "owned-fixture-data", "restore rejection lost recovery or user data");
            ResetCheck(Settings.SaveStr("ResetRejectedStillWritable", "yes"), "failed restoration prematurely suspended settings writes");
        }

        private static void ResetRestoreFailureKeepsBothStores(string root)
        {
            ResetExpectRestoreRejected(root, "restore-pending", delegate { return new List<string> { "power", "suppression" }; });
        }

        private static void ResetRestoreExceptionKeepsBothStores(string root)
        {
            ResetExpectRestoreRejected(root, "restore-exception", delegate { throw new IOException("isolated restore failure"); });
        }

        private static void ResetRestoreNullKeepsBothStores(string root)
        {
            ResetExpectRestoreRejected(root, "restore-null", delegate { return null; });
        }

        private static void ResetPendingProcessResidueKeepsBothStores(string root)
        {
            for (int pending = 1; pending <= 3; pending++)
            {
                bool boostPending = (pending & 1) != 0;
                bool suppressionPending = (pending & 2) != 0;
                int boostHeals = 0, suppressionHeals = 0, boostChecks = 0, suppressionChecks = 0;
                ResetExpectRestoreRejected(root, "process-pending-" + pending, delegate
                {
                    bool clean = LegacyPurge.ProbeProcessResidueRestore(
                        delegate { boostHeals++; }, delegate { suppressionHeals++; },
                        delegate { boostChecks++; return boostPending; },
                        delegate { suppressionChecks++; return suppressionPending; });
                    return clean ? new List<string>() : new List<string> { "process recovery pending" };
                });
                ResetCheck(boostHeals == 1 && suppressionHeals == 1
                    && boostChecks == 1 && suppressionChecks == 1, "process recovery did not check both journals exactly once");
            }
            ResetCheck(LegacyPurge.ProbeProcessResidueRestore(delegate { }, delegate { },
                delegate { return false; }, delegate { return false; }), "empty process journals did not confirm restoration");
        }

        private static void ResetProcessResidueExceptionKeepsBothStores(string root)
        {
            ResetExpectRestoreRejected(root, "process-restore-throws", delegate
            {
                LegacyPurge.ProbeProcessResidueRestore(
                    delegate { throw new IOException("isolated healing failure"); }, delegate { },
                    delegate { return false; }, delegate { return false; });
                return new List<string>();
            });
            ResetExpectRestoreRejected(root, "process-pending-check-throws", delegate
            {
                LegacyPurge.ProbeProcessResidueRestore(delegate { }, delegate { },
                    delegate { return false; }, delegate { throw new IOException("isolated pending check failure"); });
                return new List<string>();
            });
        }

        private static void ResetLockedFileKeepsRegistry(string root)
        {
            string dir = ResetDirectory(root, "locked-portable-data");
            string locked = ResetFile(dir, GameProfileStore.FileName);
            string userFile = ResetFile(dir, "user-save.sav");
            int registryCalls = 0;
            LegacyPurge.DeleteRegistryHook = delegate { registryCalls++; return true; };
            using (var held = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            {
                int files;
                string error;
                ResetCheck(!LegacyPurge.WipeAll(dir, true, "isolated-reset", out files, out error), "locked file was ignored by reset");
                ResetCheck(registryCalls == 0, "registry deleted despite locked data residual");
                ResetCheck(File.Exists(locked) && File.Exists(userFile), "locked or unrelated file was lost");
                ResetCheck(!string.IsNullOrEmpty(error) && error.Contains(GameProfileStore.FileName), "locked residual path was omitted");
                ResetCheck(!Settings.SaveStr("ResetFailureLateWrite", "no"), "partial cleanup reopened settings writes");
            }
        }

        private static void ResetExpectRegistryRejected(string root, string name, bool throws)
        {
            string dir = ResetDirectory(root, name);
            string owned = ResetFile(dir, GameProfileStore.FileName);
            string foreign = ResetFile(dir, "user-save.sav");
            int registryCalls = 0;
            LegacyPurge.DeleteRegistryHook = delegate
            {
                registryCalls++;
                ResetCheck(!File.Exists(owned), "registry deletion preceded data verification");
                if (throws) throw new IOException("isolated registry failure");
                return false;
            };
            int files;
            string error;
            ResetCheck(!LegacyPurge.WipeAll(dir, true, "isolated-reset", out files, out error), "registry failure was reported as reset success");
            ResetCheck(registryCalls == 1 && files == 1, "registry deletion was retried or data count was inaccurate");
            ResetCheck(!File.Exists(owned) && File.Exists(foreign), "registry failure mishandled data scope");
            ResetCheck(!string.IsNullOrEmpty(error) && error.Contains("HKCU\\Software\\Pavise"), "registry failure did not name the remaining key");
            ResetCheck(!Settings.SaveStr("ResetRegistryFailureLateWrite", "no"), "registry failure reopened settings writes");
        }

        private static void ResetRegistryFailureIsNotSuccess(string root)
        {
            ResetExpectRegistryRejected(root, "registry-denied", false);
        }

        private static void ResetRegistryExceptionIsNotSuccess(string root)
        {
            ResetExpectRegistryRejected(root, "registry-exception", true);
        }

        private static void ResetPortableOnlyDeletesOwnedData(string root)
        {
            string dir = ResetDirectory(root, "portable-allowlist");
            string[] owned =
            {
                "Pavise.games.txt", "Pavise.whitelist.txt", "Pavise.targets.txt", "Pavise.autoignore.txt",
                GameProfileStore.FileName, RendererObservationStore.FileName, IrqSessionLedger.FileName,
                "Pavise.log", "Pavise.log.old", "crash.log", "Pavise.preview.log", "Pavise.freeze.state",
                SuppressionCore.StateFileName, "backdrop.img", "Pavise.20260827.log", "crash.20260827.log",
                "Pavise.whitelist.txt.tmp", "Pavise.whitelist.txt.stale.bak", "Pavise.autoignore.txt.tmp",
                SuppressionCore.StateFileName + ".tmp", SuppressionCore.StateFileName + ".stale.bak",
                GameProfileStore.FileName + "." + Guid.NewGuid().ToString("N") + ".tmp",
                RendererObservationStore.FileName + "." + Guid.NewGuid().ToString("N") + ".tmp",
                IrqSessionLedger.FileName + "." + Guid.NewGuid().ToString("N") + ".tmp",
                "Pavise_" + Guid.NewGuid().ToString("N") + ".xml"
            };
            string[] foreign =
            {
                "Pavise.exe", "Pavise.portable", "user-save.sav", "unrelated.tmp", "Pavise.exe.tmp",
                GameProfileStore.FileName + ".not-a-guid.tmp", RendererObservationStore.FileName + ".user-backup.tmp",
                "Pavise.profiles.datx", "PaviseSomething.log", "crashlog.log", "backdrop-source.png",
                "Pavise_note.xml", "Pavise_zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz.xml",
                "Pavise_" + Guid.NewGuid().ToString("N") + ".xml.bak"
            };
            foreach (string name in owned) ResetFile(dir, name);
            foreach (string name in foreign) ResetFile(dir, name);
            string userDir = ResetDirectory(dir, "UserDirectory");
            string nested = ResetFile(userDir, "Pavise.games.txt");
            int registryCalls = 0;
            LegacyPurge.DeleteRegistryHook = delegate
            {
                registryCalls++;
                foreach (string name in owned) ResetCheck(!File.Exists(Path.Combine(dir, name)), "registry ran while owned data remained");
                return true;
            };
            int files;
            string error;
            ResetCheck(LegacyPurge.WipeAll(dir, true, "isolated-reset", out files, out error), "portable reset failed: " + error);
            ResetCheck(files == owned.Length && error == null && registryCalls == 1, "portable reset result did not match verified deletions");
            foreach (string name in foreign) ResetCheck(File.ReadAllText(Path.Combine(dir, name)) == "owned-fixture-data", "portable reset removed unrelated file " + name);
            ResetCheck(File.Exists(nested) && Directory.Exists(dir), "portable reset recursively deleted a user directory");
        }

        private static void ResetReadOnlyOwnedFileIsCleared(string root)
        {
            string dir = ResetDirectory(root, "readonly-portable");
            string path = ResetFile(dir, RendererObservationStore.FileName);
            File.SetAttributes(path, FileAttributes.ReadOnly);
            LegacyPurge.DeleteRegistryHook = delegate { return true; };
            int files;
            string error;
            ResetCheck(LegacyPurge.WipeAll(dir, true, "isolated-reset", out files, out error), "owned readonly data was not cleared: " + error);
            ResetCheck(files == 1 && !File.Exists(path), "readonly cleanup was not verified");
        }

        private static void ResetDataOnlyDoesNotTouchRegistry(string root)
        {
            string dir = ResetDirectory(root, "data-only");
            string path = ResetFile(dir, GameProfileStore.FileName);
            int registryCalls = 0;
            LegacyPurge.DeleteRegistryHook = delegate { registryCalls++; return true; };
            int files;
            string error;
            ResetCheck(LegacyPurge.WipeAll(dir, false, "isolated-reset", out files, out error), "data-only cleanup failed: " + error);
            ResetCheck(files == 1 && registryCalls == 0 && !File.Exists(path), "data-only cleanup touched registry or missed its file");
            ResetCheck(Settings.SaveStr("ResetDataOnlyStillWritable", "yes"), "data-only reset incorrectly froze settings");
        }

        private static void ResetMissingDirectoryIsAlreadyClear(string root)
        {
            string dir = Path.Combine(root, "already-absent");
            int registryCalls = 0, restoreCalls = 0;
            LegacyPurge.DeleteRegistryHook = delegate { registryCalls++; return true; };
            LegacyPurge.RestoreHook = delegate { restoreCalls++; return new List<string>(); };
            int files;
            string error;
            ResetCheck(LegacyPurge.WipeAll(dir, true, "isolated-reset", out files, out error), "absent data directory was treated as failure: " + error);
            ResetCheck(files == 0 && error == null && registryCalls == 1 && restoreCalls == 1, "absent directory skipped restoration or registry cleanup");
            ResetCheck(!Directory.Exists(dir), "reset recreated absent data directory");
        }

        private static void ResetUnexpectedOwnedDirectoryIsPreserved(string root)
        {
            string dir = ResetDirectory(root, "owned-name-directory");
            string unexpected = ResetDirectory(dir, GameProfileStore.FileName);
            string sentinel = ResetFile(unexpected, "keep.txt");
            int registryCalls = 0;
            LegacyPurge.DeleteRegistryHook = delegate { registryCalls++; return true; };
            int files;
            string error;
            ResetCheck(!LegacyPurge.WipeAll(dir, true, "isolated-reset", out files, out error), "unexpected directory using a known filename was ignored");
            ResetCheck(File.Exists(sentinel) && registryCalls == 0 && files == 0, "unexpected portable directory was recursively removed");
            ResetCheck(error.Contains(GameProfileStore.FileName), "unexpected directory was not reported");
        }

        private static void ResetRootJunctionIsRefused(string root)
        {
            string outside = ResetDirectory(root, "root-link-outside");
            string sentinel = ResetFile(outside, GameProfileStore.FileName);
            string link = Path.Combine(root, "root-link");
            ResetCreateJunction(link, outside);
            int registryCalls = 0, restoreCalls = 0;
            LegacyPurge.DeleteRegistryHook = delegate { registryCalls++; return true; };
            LegacyPurge.RestoreHook = delegate { restoreCalls++; return new List<string>(); };
            int files;
            string error;
            ResetCheck(!LegacyPurge.WipeAll(link, true, "isolated-reset", out files, out error), "root junction passed reset gate");
            ResetCheck(!LegacyPurge.TryDeleteDataTree(link, out files, out error), "root junction passed whole-tree deletion gate");
            ResetCheck(files == 0 && registryCalls == 0 && restoreCalls == 0, "junction rejection invoked restore/delete operations");
            ResetCheck(File.ReadAllText(sentinel) == "owned-fixture-data" && Directory.Exists(link), "root junction or outside target was modified");
        }

        private static void ResetTreeJunctionCannotReachOutside(string root)
        {
            string dir = ResetDirectory(root, "tree-scope");
            string nested = ResetDirectory(dir, "a\\b");
            string normal = ResetFile(dir, "one.txt");
            string readOnly = ResetFile(nested, "只读数据.dat");
            File.SetAttributes(readOnly, FileAttributes.ReadOnly);
            string outside = ResetDirectory(root, "tree-outside");
            string sentinel = ResetFile(outside, "must-survive.txt");
            File.SetAttributes(sentinel, FileAttributes.ReadOnly);
            ResetCreateJunction(Path.Combine(dir, "outside-junction"), outside);
            int files;
            string error;
            ResetCheck(LegacyPurge.TryDeleteDataTree(dir, out files, out error), "owned tree did not fully clear: " + error);
            ResetCheck(files == 2 && !Directory.Exists(dir) && !File.Exists(normal), "owned tree removal was not complete");
            ResetCheck(File.ReadAllText(sentinel) == "owned-fixture-data"
                && (File.GetAttributes(sentinel) & FileAttributes.ReadOnly) != 0, "tree deletion crossed a junction boundary");
        }

        private static void ResetTreeLockedFileReportsResidual(string root)
        {
            string dir = ResetDirectory(root, "tree-locked");
            string path = ResetFile(ResetDirectory(dir, "nested"), "locked.dat");
            using (var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            {
                int files;
                string error;
                ResetCheck(!LegacyPurge.TryDeleteDataTree(dir, out files, out error), "partial tree deletion reported success");
                ResetCheck(files == 0 && Directory.Exists(dir) && File.Exists(path), "locked tree data was not preserved");
                ResetCheck(!string.IsNullOrEmpty(error) && error.Contains("locked.dat"), "locked tree residual was omitted");
            }
        }

        private static void ResetTreeAlreadyGoneIsIdempotent(string root)
        {
            int files;
            string error;
            ResetCheck(LegacyPurge.TryDeleteDataTree(Path.Combine(root, "tree-absent"), out files, out error), "already absent tree was treated as failure");
            ResetCheck(files == 0 && error == null, "absent tree invented deletion count or error");
        }

        private static void ResetInvalidPathsDoNotStartRestoration(string root)
        {
            string dir = ResetDirectory(root, "invalid-path-fixture");
            string file = ResetFile(dir, "not-a-directory");
            int registryCalls = 0, restoreCalls = 0;
            LegacyPurge.DeleteRegistryHook = delegate { registryCalls++; return true; };
            LegacyPurge.RestoreHook = delegate { restoreCalls++; return new List<string>(); };
            foreach (string path in new[] { null, "", "relative-dir", "C:relative-dir", "\\relative-to-drive", Path.GetPathRoot(root), file })
            {
                int files;
                string error;
                ResetCheck(!LegacyPurge.WipeAll(path, true, "isolated-reset", out files, out error), "invalid path passed reset gate");
                ResetCheck(files == 0 && !string.IsNullOrEmpty(error), "invalid path did not report a checked failure");
            }
            ResetCheck(registryCalls == 0 && restoreCalls == 0, "invalid path started restore or registry deletion");
            int treeFiles;
            string treeError;
            ResetCheck(!LegacyPurge.TryDeleteDataTree(Path.GetTempPath(), out treeFiles, out treeError)
                && treeFiles == 0, "shared temporary root passed whole-tree deletion gate");
        }

        private static void ResetLogsCannotRecreateDeletedData(string root)
        {
            string dir = ResetDirectory(root, "log-does-not-return");
            Logger.LogPath = Path.Combine(dir, "Pavise.log");
            string path = ResetFile(dir, GameProfileStore.FileName);
            LegacyPurge.DeleteRegistryHook = delegate { return true; };
            int files;
            string error;
            ResetCheck(LegacyPurge.WipeAll(dir, true, "isolated-reset", out files, out error), "reset with active log failed: " + error);
            ResetCheck(files == 2 && !File.Exists(path) && !File.Exists(Logger.LogPath), "reset logged after deleting its own log");
            Logger.Log("late callback must be dropped");
            Logger.Clear();
            Logger.AppendCrash(Path.Combine(dir, "crash.log"), "late exception must be dropped");
            ResetCheck(!File.Exists(Logger.LogPath), "late logger callback recreated cleared data");
            ResetCheck(!File.Exists(Path.Combine(dir, "crash.log")), "late exception handler recreated crash.log");
            ResetCheck(!Settings.SaveStr("LateResetCallback", "must-not-persist"), "late settings callback persisted after reset");
        }

        private static void ResetCreateJunction(string link, string target)
        {
            // Creation only; no cmd/batch deletion. All paths were allocated under
            // this suite's unique scratch directory and do not refer to user data.
            string script = "New-Item -ItemType Junction -Path '" + link.Replace("'", "''")
                + "' -Target '" + target.Replace("'", "''") + "' -ErrorAction Stop | Out-Null";
            using (Process process = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -NonInteractive -Command \"" + script.Replace("\"", "`\"") + "\"",
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            }))
            {
                ResetCheck(process != null, "junction fixture process did not start");
                if (!process.WaitForExit(10000))
                {
                    try { process.Kill(); } catch { }
                    throw new Exception("Isolated junction fixture creation exceeded 10 seconds");
                }
                ResetCheck(process.ExitCode == 0, "junction fixture creation failed: " + process.StandardError.ReadToEnd());
            }
            ResetCheck((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0, "junction fixture is not a reparse point");
        }

        private static void ResetDeleteScratch(string root)
        {
            string actual = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
            string parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            string name = Path.GetFileName(actual);
            Guid scratchId;
            ResetCheck(string.Equals(Path.GetDirectoryName(actual), parent, StringComparison.OrdinalIgnoreCase)
                && name.StartsWith("PaviseResetCleanup-", StringComparison.Ordinal)
                && Guid.TryParseExact(name.Substring("PaviseResetCleanup-".Length), "N", out scratchId),
                "refusing cleanup outside the exact owned temporary directory");
            if (Directory.Exists(actual)) ResetDeleteScratchLevel(actual, actual);
        }

        private static void ResetDeleteScratchLevel(string root, string path)
        {
            string actual = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
            ResetCheck(string.Equals(root, actual, StringComparison.OrdinalIgnoreCase)
                || actual.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                "scratch cleanup escaped its owned root");
            FileAttributes attributes = File.GetAttributes(actual);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                if ((attributes & FileAttributes.Directory) != 0) Directory.Delete(actual, false);
                else File.Delete(actual);
                return;
            }
            if ((attributes & FileAttributes.Directory) != 0)
            {
                foreach (string entry in Directory.GetFileSystemEntries(actual)) ResetDeleteScratchLevel(root, entry);
                File.SetAttributes(actual, attributes & ~FileAttributes.ReadOnly);
                Directory.Delete(actual, false);
            }
            else
            {
                File.SetAttributes(actual, attributes & ~FileAttributes.ReadOnly);
                File.Delete(actual);
            }
        }
    }
}
#endif
