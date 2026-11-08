using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Xml;

namespace PaviseApp
{
    internal sealed class SelfUpdateTarget
    {
        internal string Path, Backup, PreviousHash;
        internal bool Applied;
    }

    internal sealed class SelfUpdateTransaction
    {
        internal string Directory, Nonce, ParentExecutable, ParentHash, DataDirectory;
        internal string LaunchExecutable, NewManagedExecutable, TaskXml, ManifestText, CandidateHash, PreviousCommittedVersion;
        internal int ParentPid, Language;
        internal long ParentBirth;
        internal readonly List<SelfUpdateTarget> Targets = new List<SelfUpdateTarget>();
        internal Process Helper;
    }

    // Require a safe-exit receipt and process identity before replacing the running EXE.
    internal static class SelfUpdateInstaller
    {
        internal const string PendingKey = "SelfUpdatePendingV1";
        internal const string OriginKey = "SelfUpdateOriginExeV1";
        internal const string CommittedVersionKey = "SelfUpdateCommittedVersionV1";
        private const string HelperName = "Pavise.Update.exe";
        private const string PlanName = "transaction.xml";
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
#if PAVISE_SELFTEST && PAVISE_UPDATE_INSTALLER_CHECKS
        internal static string FixtureRoot;
        internal static Func<string> CaptureTaskForTests;
        internal static Action<string> BeforeApplicationStartForTests;
#endif

        private static string UpdateRoot
        {
            get
            {
#if PAVISE_SELFTEST && PAVISE_UPDATE_INSTALLER_CHECKS
                if (FixtureRoot != null) return Path.GetFullPath(FixtureRoot);
#endif
                using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                    return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PaviseAuto.Update", identity.User.Value);
            }
        }

        internal static string CreateStagingDirectory()
        {
            string root = UpdateRoot;
            EnsurePrivateDirectory(Path.GetDirectoryName(root));
            EnsurePrivateDirectory(root);
            string directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
            EnsurePrivateDirectory(directory);
            return directory;
        }

        private static void EnsurePrivateDirectory(string directory)
        {
            RejectRedirects(directory);
#if PAVISE_SELFTEST && PAVISE_UPDATE_INSTALLER_CHECKS
            if (FixtureRoot != null) { System.IO.Directory.CreateDirectory(directory); return; }
#endif
            SecurityIdentifier admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            SecurityIdentifier system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            DirectorySecurity security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            security.SetOwner(admins);
            security.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            Directory.CreateDirectory(directory, security);
            Directory.SetAccessControl(directory, security);
            RejectRedirects(directory);
        }

        internal static void CleanupStagingDirectory(string directory)
        {
            try
            {
                ValidateDirectory(directory);
                if (File.Exists(Path.Combine(directory, PlanName))) return;
                foreach (string name in new[] { "Pavise.update.exe", "Pavise.update.exe.part", "Pavise.download.exe", "Pavise.download.exe.part", "candidate.exe", "candidate.exe.part", "update.exe", "update.exe.part" })
                    DeleteExact(Path.Combine(directory, name));
                foreach (string file in System.IO.Directory.GetFiles(directory, "Pavise.update.*.exe", SearchOption.TopDirectoryOnly))
                {
                    string name = Path.GetFileName(file); Guid id;
                    if (name.Length == "Pavise.update.".Length + 32 + ".exe".Length && Guid.TryParseExact(name.Substring("Pavise.update.".Length, 32), "N", out id)) DeleteExact(file);
                }
                if (System.IO.Directory.GetFileSystemEntries(directory).Length == 0) System.IO.Directory.Delete(directory, false);
            }
            catch { }
        }

        internal static SelfUpdateTransaction Prepare(string stagedExe, SelfUpdatePackage package)
        {
            if (package == null || string.IsNullOrEmpty(package.ManifestText)) throw new InvalidOperationException("Missing signed update manifest.");
            SelfUpdatePackage trusted = SelfUpdateManifest.ParseAndVerify(package.ManifestText);
            if (!Same(trusted.Sha256, package.Sha256) || trusted.Version != package.Version || trusted.Size != package.Size)
                throw new InvalidOperationException("Update package identity changed.");
            string directory = Path.GetDirectoryName(Path.GetFullPath(stagedExe));
            ValidateDirectory(directory);
            if (File.Exists(Path.Combine(directory, PlanName))) throw new IOException("An update already owns this directory.");
            RejectRedirects(stagedExe);
            VerifyCandidate(stagedExe, trusted);
            if (!UpdateChecker.IsNewer(trusted.Version, App.Version)) throw new InvalidOperationException("The update is not newer than this application.");
            ResolvePendingBeforePrepare();
            SelfUpdateTransaction tx = new SelfUpdateTransaction();
            tx.Directory = directory; tx.Nonce = Guid.NewGuid().ToString("N");
            try
            {
            tx.DataDirectory = Path.GetFullPath(Paths.Data);
            tx.ManifestText = trusted.ManifestText; tx.Language = Lang.Cur; tx.CandidateHash = trusted.Sha256;
            tx.PreviousCommittedVersion = Settings.LoadStr(CommittedVersionKey, "");
            using (Process current = Process.GetCurrentProcess())
            {
                tx.ParentPid = current.Id; tx.ParentBirth = current.StartTime.ToUniversalTime().Ticks;
                tx.ParentExecutable = Path.GetFullPath(current.MainModule.FileName);
            }
            RejectRedirects(tx.ParentExecutable);
            tx.ParentHash = HashFile(tx.ParentExecutable);
            bool managed = StartupPayload.IsManagedExecutable(tx.ParentExecutable);
            string origin = managed ? Settings.LoadStr(OriginKey, "") : tx.ParentExecutable;
            if (!string.IsNullOrEmpty(origin) && !StartupPayload.IsManagedExecutable(origin) && File.Exists(origin) && Same(HashFile(origin), tx.ParentHash))
                AddTarget(tx, Path.GetFullPath(origin));
            if (!managed && tx.Targets.Count == 0) throw new IOException("The original executable could not be verified.");
#if PAVISE_SELFTEST && PAVISE_UPDATE_INSTALLER_CHECKS
            tx.TaskXml = CaptureTaskForTests == null ? TaskHelper.CaptureStartupTaskForUpdate() : CaptureTaskForTests();
#else
            tx.TaskXml = TaskHelper.CaptureStartupTaskForUpdate();
#endif
            if (managed || tx.TaskXml != null)
            {
                tx.NewManagedExecutable = StartupPayload.Ensure(stagedExe);
                string config = tx.ParentExecutable + ".config";
                if (File.Exists(config)) CopyExact(config, tx.NewManagedExecutable + ".config", false);
            }
            tx.LaunchExecutable = managed ? tx.NewManagedExecutable : tx.ParentExecutable;
            CopyExact(stagedExe, Path.Combine(directory, "candidate.exe"), Same(stagedExe, Path.Combine(directory, "candidate.exe")));
            if (!Same(stagedExe, Path.Combine(directory, "candidate.exe"))) DeleteExact(stagedExe);
            CopyExact(tx.ParentExecutable, Path.Combine(directory, HelperName), false);
            if (File.Exists(tx.ParentExecutable + ".config")) CopyExact(tx.ParentExecutable + ".config", Path.Combine(directory, HelperName + ".config"), false);
            foreach (SelfUpdateTarget target in tx.Targets) CopyExact(target.Path, Path.Combine(directory, target.Backup), false);
            WritePlan(tx);
            if (!Settings.SaveStrDurable(PendingKey, directory)) throw new IOException("The update recovery location could not be saved.");
            return tx;
            }
            catch { try { CleanupTransaction(tx, false); } catch { } throw; }
        }

        private static void AddTarget(SelfUpdateTransaction tx, string executable)
        {
            RejectRedirects(executable);
            if (!string.Equals(Path.GetExtension(executable), ".exe", StringComparison.OrdinalIgnoreCase)) throw new IOException("The update target is not an executable.");
            foreach (SelfUpdateTarget existing in tx.Targets) if (Same(existing.Path, executable)) return;
            tx.Targets.Add(new SelfUpdateTarget { Path = executable, PreviousHash = tx.ParentHash, Backup = "previous-" + tx.Targets.Count.ToString(CultureInfo.InvariantCulture) + ".exe" });
        }

        internal static void LaunchHelper(SelfUpdateTransaction tx)
        {
            ValidateDirectory(tx.Directory);
            string executable = Path.Combine(tx.Directory, HelperName);
            if (!Same(HashFile(executable), tx.ParentHash)) throw new IOException("The update helper changed.");
            ProcessStartInfo start = new ProcessStartInfo(executable, "--apply-update " + Quote(tx.Directory) + " " + tx.Nonce);
            start.UseShellExecute = false; start.CreateNoWindow = true; start.WindowStyle = ProcessWindowStyle.Normal; start.WorkingDirectory = tx.Directory;
            tx.Helper = Process.Start(start);
            if (tx.Helper == null) throw new IOException("The update window did not start.");
            DateTime deadline = DateTime.UtcNow.AddSeconds(12);
            while (DateTime.UtcNow < deadline)
            {
                if (tx.Helper.HasExited) throw new IOException("The update window exited before it was ready.");
                string receipt = ReadMarker(tx, "ready");
                string expected = tx.Nonce + "|" + tx.Helper.Id.ToString(CultureInfo.InvariantCulture) + "|" + tx.Helper.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture);
                if (receipt == expected) return;
                Thread.Sleep(50);
            }
            throw new TimeoutException("The update window did not become ready in time.");
        }

        internal static void CommitSafeExit(SelfUpdateTransaction tx)
        {
            if (tx == null || tx.Helper == null || tx.Helper.HasExited) throw new IOException("The update window is no longer available.");
            WriteMarker(tx, "safe-exit", tx.Nonce);
        }

        internal static void Abort(SelfUpdateTransaction tx)
        {
            if (tx == null) return;
            try { WriteMarker(tx, "abort", tx.Nonce); } catch { }
            ThreadPool.QueueUserWorkItem(delegate
            {
                try { if (tx.Helper != null && !tx.Helper.WaitForExit(15000)) return; } catch { return; }
                try
                {
                    if (ReadMarker(tx, "applied") != null) return;
                    WriteMarker(tx, "restored", tx.Nonce);
                    CleanupTransaction(tx, false);
                }
                catch { }
            });
        }

        internal static bool TryRun(string[] args)
        {
            if (args == null || args.Length == 0 || args[0] != "--apply-update") return false;
            Environment.ExitCode = 1;
            if (args.Length != 3) return true;
            try
            {
                SelfUpdateTransaction tx = ReadPlan(args[1]);
                if (tx.Nonce != args[2] || !Same(Application.ExecutablePath, Path.Combine(tx.Directory, HelperName)) || !Same(HashFile(Application.ExecutablePath), tx.ParentHash)) return true;
                SelfUpdatePackage package = SelfUpdateManifest.ParseAndVerify(tx.ManifestText);
                VerifyCandidate(Path.Combine(tx.Directory, "candidate.exe"), package);
                Paths.Init(tx.DataDirectory); Lang.Init(); Lang.Cur = tx.Language; Dpi.Init();
                Theme.SetLight(Settings.Load("UiLight", false));
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                using (SelfUpdateInstallerWindow window = new SelfUpdateInstallerWindow())
                {
                    window.Shown += delegate { ThreadPool.QueueUserWorkItem(delegate { RunHelper(tx, package, window); }); };
                    Application.Run(window);
                }
            }
            catch { }
            return true;
        }

        private static void RunHelper(SelfUpdateTransaction tx, SelfUpdatePackage package, SelfUpdateInstallerWindow window)
        {
            Process parent = null, launched = null;
            bool touchedFiles = false;
            string installedTaskXml = null;
            try
            {
                parent = OpenExactProcess(tx.ParentPid, tx.ParentBirth, tx.ParentExecutable);
                if (parent == null) throw new IOException("The original process could not be verified.");
                WriteMarker(tx, "ready", tx.Nonce + "|" + Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture) + "|" + Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture));
                window.Progress(8, Text("正在等待 Pavise 安全退出", "Waiting for Pavise to exit safely"));
                DateTime deadline = DateTime.UtcNow.AddSeconds(100);
                while (ReadMarker(tx, "safe-exit") != tx.Nonce)
                {
                    if (ReadMarker(tx, "abort") == tx.Nonce) { window.FinishSilently(); return; }
                    if (parent.HasExited || DateTime.UtcNow > deadline) throw new IOException("Safe shutdown was not confirmed; the original program was not replaced.");
                    Thread.Sleep(100);
                }
                if (!parent.WaitForExit(60000)) throw new IOException("Pavise has not exited. No files were replaced.");
                if (ReadMarker(tx, "abort") == tx.Nonce) { window.FinishSilently(); return; }
                using (MutexLease lease = AcquireApplicationMutex())
                {
                if (AnyTargetProcessActive(tx)) throw new IOException("Another Pavise instance is still using the update target.");
                VerifyCandidate(Path.Combine(tx.Directory, "candidate.exe"), package);
                window.Progress(30, Text("正在替换原位置的程序", "Replacing the executable in its original location"));
                WriteMarker(tx, "applied", tx.Nonce);
                touchedFiles = true;
                ApplyFiles(tx, package);
                if (tx.TaskXml != null)
                {
                    window.Progress(55, Text("正在更新启动入口", "Updating startup entries"));
                    TaskHelper.RetargetStartupTaskForUpdate(tx.TaskXml, tx.NewManagedExecutable, tx.DataDirectory, tx.Directory, out installedTaskXml);
                }
                }
                window.Progress(75, Text("正在启动并验证新版", "Starting and verifying the new version"));
                launched = StartApplication(tx.LaunchExecutable, tx, false);
                if (launched == null) throw new IOException("The new version could not be started.");
                deadline = DateTime.UtcNow.AddSeconds(90);
                while (DateTime.UtcNow < deadline)
                {
                    if (HealthyReceipt(tx, package, launched))
                    {
                        window.Progress(95, Text("新版已启动，正在清理旧版", "The new version is running; removing the old version"));
                        CleanupPrevious(tx);
                        WriteMarker(tx, "complete", tx.Nonce);
                        Environment.ExitCode = 0;
                        window.Complete();
                        return;
                    }
                    if (launched.HasExited) throw new IOException("The new version exited before startup was confirmed.");
                    Thread.Sleep(100);
                }
                throw new TimeoutException("The new version is still running, but its startup has not been confirmed. Recovery files were retained.");
            }
            catch (Exception error)
            {
                bool newRunning = false;
                try { newRunning = launched != null && !launched.HasExited; } catch { newRunning = true; }
                bool restored = !touchedFiles;
                if (!newRunning && touchedFiles)
                {
                    try
                    {
                        window.Progress(25, Text("更新未完成，正在恢复旧版", "Update incomplete; restoring the previous version"));
                        using (MutexLease lease = AcquireApplicationMutex())
                        {
                        if (AnyTargetProcessActive(tx)) throw new IOException("A Pavise instance is still running; recovery did not overwrite it.");
                        RollbackFiles(tx);
                        if (installedTaskXml != null) TaskHelper.RestoreStartupTaskIfStillOwned(tx.TaskXml, installedTaskXml, tx.Directory);
                        }
                        restored = true;
                        if (string.IsNullOrEmpty(tx.PreviousCommittedVersion)) Settings.Remove(CommittedVersionKey); else Settings.SaveStrDurable(CommittedVersionKey, tx.PreviousCommittedVersion);
                        WriteMarker(tx, "restored", tx.Nonce);
                        try { using (Process old = StartApplication(tx.ParentExecutable, tx, true)) { } } catch { }
                    }
                    catch (Exception restoreError) { error = new IOException(error.Message + "\r\n" + restoreError.Message); }
                }
                string detail = newRunning
                    ? Text("新版仍在运行，但尚未确认初始化完成。恢复文件已保留，请先正常退出 Pavise 后再手动更新。", "The new version is still running but startup is unconfirmed. Recovery files were retained. Exit Pavise normally before updating manually.")
                    : restored ? Text("原程序已保留或恢复。请使用下方入口手动下载最新版。", "The original program was retained or restored. Download the latest release manually below.")
                    : Text("自动恢复未完成，恢复文件已保留。请勿删除更新恢复目录，先按下载页说明处理。", "Automatic recovery is incomplete. Recovery files were retained. Keep the update recovery directory and follow the download-page instructions.");
                try { WriteMarker(tx, "manual", tx.Nonce); } catch { }
                window.Manual(detail + "\r\n\r\n" + error.Message);
            }
            finally { if (parent != null) parent.Dispose(); if (launched != null) launched.Dispose(); }
        }

        private static Process StartApplication(string executable, SelfUpdateTransaction tx, bool rollback)
        {
            RejectRedirects(executable);
            string args = "--update-restart --autostart-data " + Quote(tx.DataDirectory);
            args += " --update-transaction " + Quote(tx.Directory) + " --update-nonce " + tx.Nonce;
            if (rollback) args += " --update-rollback";
            ProcessStartInfo start = new ProcessStartInfo(executable, args);
            start.UseShellExecute = false; start.CreateNoWindow = true; start.WorkingDirectory = Path.GetDirectoryName(executable);
            // Reverify the actual launch target, retaining a read-only lease through Process.Start.
            // This prevents replacement between verification and Windows opening the image.
            using (FileStream target = new FileStream(executable, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (SHA256 hash = SHA256.Create())
            {
                string actual = BitConverter.ToString(hash.ComputeHash(target)).Replace("-", "").ToLowerInvariant();
                if (!Same(actual, rollback ? tx.ParentHash : tx.CandidateHash))
                    throw new IOException("The executable changed before startup; it was not run.");
                RejectRedirects(executable);
#if PAVISE_SELFTEST && PAVISE_UPDATE_INSTALLER_CHECKS
                if (BeforeApplicationStartForTests != null) BeforeApplicationStartForTests(executable);
#endif
                return Process.Start(start);
            }
        }

        internal static void ApplyFiles(SelfUpdateTransaction tx, SelfUpdatePackage package)
        {
            VerifyCandidate(Path.Combine(tx.Directory, "candidate.exe"), package);
            foreach (SelfUpdateTarget target in tx.Targets)
            {
                if (!Same(HashFile(target.Path), target.PreviousHash) || !Same(HashFile(Path.Combine(tx.Directory, target.Backup)), target.PreviousHash))
                    throw new IOException("The original executable changed before replacement.");
                ReplaceExact(Path.Combine(tx.Directory, "candidate.exe"), target.Path, package.Sha256, tx.Nonce);
                target.Applied = true;
            }
        }

        internal static void RollbackFiles(SelfUpdateTransaction tx)
        {
            if (AnyTargetProcessActive(tx)) throw new IOException("An update target is still running; recovery did not overwrite it.");
            for (int i = tx.Targets.Count - 1; i >= 0; i--)
            {
                SelfUpdateTarget target = tx.Targets[i];
                string backup = Path.Combine(tx.Directory, target.Backup);
                if (!Same(HashFile(backup), target.PreviousHash)) throw new IOException("The recovery executable failed verification.");
                if (File.Exists(target.Path) && Same(HashFile(target.Path), target.PreviousHash)) continue;
                if (File.Exists(target.Path) && !Same(HashFile(target.Path), tx.CandidateHash)) throw new IOException("The update target changed; it was not overwritten during recovery.");
                ReplaceExact(backup, target.Path, target.PreviousHash, tx.Nonce);
            }
        }

        private static void ReplaceExact(string source, string target, string expectedHash, string nonce)
        {
            RejectRedirects(source); RejectRedirects(target);
            string stage = target + ".PaviseUpdate." + nonce + ".tmp";
            RejectRedirects(stage);
            if (File.Exists(stage)) throw new IOException("An update staging file already exists.");
            try
            {
                CopyExact(source, stage, false);
                if (!Same(HashFile(stage), expectedHash)) throw new IOException("The replacement copy failed verification.");
                RejectRedirects(target); RejectRedirects(stage);
                if (File.Exists(target)) File.Replace(stage, target, null, true); else File.Move(stage, target);
                if (!Same(HashFile(target), expectedHash)) throw new IOException("The installed executable failed verification.");
            }
            finally { DeleteExact(stage); }
        }

        internal static bool AcknowledgeStartup(string[] args)
        {
            try
            {
                string directory = Argument(args, "--update-transaction");
                string nonce = Argument(args, "--update-nonce");
                bool explicitUpdate = directory != null;
                if (!explicitUpdate) directory = Settings.LoadStr(PendingKey, "");
                if (string.IsNullOrEmpty(directory)) return true;
                SelfUpdateTransaction tx = ReadPlan(directory);
                if (ReadMarker(tx, "restored") == tx.Nonce && Same(Application.ExecutablePath, tx.ParentExecutable) && Same(HashFile(Application.ExecutablePath), tx.ParentHash) && Same(Paths.Data, tx.DataDirectory))
                { QueueCleanup(tx, false); return true; }
                SelfUpdatePackage package = SelfUpdateManifest.ParseAndVerify(tx.ManifestText);
                if (explicitUpdate && tx.Nonce != nonce) return false;
                if (!Same(Application.ExecutablePath, tx.LaunchExecutable) || !Same(Paths.Data, tx.DataDirectory) || App.Version != package.Version || !Same(HashFile(Application.ExecutablePath), package.Sha256)) return !explicitUpdate;
                if (!TaskHelper.StartupTaskMatchesUpdate(tx.TaskXml, tx.NewManagedExecutable, tx.DataDirectory)) return false;
                if (!Settings.SaveStrDurable(CommittedVersionKey, package.Version)) return false;
                using (Process process = Process.GetCurrentProcess())
                    WriteMarker(tx, "healthy", tx.Nonce + "|" + process.Id.ToString(CultureInfo.InvariantCulture) + "|" + process.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture) + "|" + package.Sha256 + "|" + package.Version);
                if (!File.Exists(Path.Combine(tx.Directory, "complete")) && !HelperAlive(tx))
                {
                    CleanupPrevious(tx); WriteMarker(tx, "complete", tx.Nonce);
                }
                QueueCleanup(tx, true);
                return true;
            }
            catch { return false; }
        }

        internal static void RecordLaunch()
        {
            try
            {
                if (!AllowStartupMaintenance(Application.ExecutablePath)) return;
                if (!StartupPayload.IsManagedExecutable(Application.ExecutablePath)) Settings.SaveStrDurable(OriginKey, Path.GetFullPath(Application.ExecutablePath));
            }
            catch { }
        }

        // Startup only inspects abandoned replacements; transaction recovery remains explicit.
        internal static string GetStartupManualFailure()
        {
            try
            {
                string pending = Settings.LoadStr(PendingKey, "");
                if (string.IsNullOrEmpty(pending)) return null;
                SelfUpdateTransaction tx = ReadPlan(pending);
                if (HelperAlive(tx) || ReadMarker(tx, "complete") == tx.Nonce || ReadMarker(tx, "restored") == tx.Nonce) return null;
                return ReadMarker(tx, "applied") == tx.Nonce
                    ? Text("上次自动更新被中断，尚未确认新版启动或恢复完成。恢复文件已保留，当前程序可以继续使用。请先正常退出 Pavise，再通过夸克网盘或 GitHub 按下载页说明手动更新；暂时不要删除恢复目录。", "The previous automatic update was interrupted before startup or recovery was confirmed. Recovery files were retained and the current application remains usable. Exit Pavise normally, then update manually using Quark or GitHub and the download-page instructions. Keep the recovery directory for now.")
                    : Text("上次自动更新在替换程序前中断，原程序未被替换，当前版本可以继续使用。请通过夸克网盘或 GitHub 手动更新。", "The previous automatic update stopped before replacing the executable. The original application is unchanged and remains usable. Update manually using Quark or GitHub.");
            }
            catch { return null; }
        }

        private static void ResolvePendingBeforePrepare()
        {
            string pending = Settings.LoadStr(PendingKey, "");
            if (string.IsNullOrEmpty(pending)) return;
            SelfUpdateTransaction previous = ReadPlan(pending);
            if (HelperAlive(previous)) throw new IOException("Another update is still in progress.");
            bool finalized = ReadMarker(previous, "complete") == previous.Nonce || ReadMarker(previous, "restored") == previous.Nonce;
            if (!finalized && ReadMarker(previous, "applied") != null)
                throw new IOException("A previous replacement has unresolved recovery records. Use the manual update entry before starting another automatic update.");
            // Delete only this transaction's assets after verified completion or no attempted replacement.
            if (!finalized) WriteMarker(previous, "restored", previous.Nonce);
            CleanupTransaction(previous, false);
            if (!string.IsNullOrEmpty(Settings.LoadStr(PendingKey, "")))
                throw new IOException("The previous update's temporary files could not be cleaned. Use the manual update entry.");
        }

        internal static bool IsUncoordinatedStartupBlocked(string[] args)
        {
            bool activeTransaction = false;
            try
            {
                string pending = Settings.LoadStr(PendingKey, "");
                if (string.IsNullOrEmpty(pending)) return false;
                SelfUpdateTransaction tx = ReadPlan(pending);
                if (!HelperAlive(tx)) return false;
                activeTransaction = true;
                string directory = Argument(args, "--update-transaction"), nonce = Argument(args, "--update-nonce");
                if (Same(directory, tx.Directory) && nonce == tx.Nonce && ReadMarker(tx, "safe-exit") == tx.Nonce)
                {
                    string current = Application.ExecutablePath;
                    if (Same(current, tx.LaunchExecutable) && Same(HashFile(current), tx.CandidateHash)) return false;
                    if (ReadMarker(tx, "restored") == tx.Nonce && Same(current, tx.ParentExecutable) && Same(HashFile(current), tx.ParentHash)) return false;
                }
                return true;
            }
            catch { return activeTransaction; }
        }

        internal static bool IsStartupUpdatePending(string[] args)
        {
            try
            {
                if (args == null || Array.IndexOf(args, "--update-restart") < 0 || Array.IndexOf(args, "--update-rollback") >= 0) return false;
                string directory = Argument(args, "--update-transaction"), nonce = Argument(args, "--update-nonce");
                if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(nonce)) return false;
                SelfUpdateTransaction tx = ReadPlan(directory);
                if (tx.Nonce != nonce || !Same(Application.ExecutablePath, tx.LaunchExecutable)) return false;
                if (ReadMarker(tx, "complete") == tx.Nonce || ReadMarker(tx, "restored") == tx.Nonce || ReadMarker(tx, "manual") == tx.Nonce) return false;
                return HelperAlive(tx);
            }
            catch { return false; }
        }

        private sealed class MutexLease : IDisposable
        {
            internal Mutex Mutex;
            public void Dispose() { if (Mutex != null) { try { Mutex.ReleaseMutex(); } finally { Mutex.Dispose(); Mutex = null; } } }
        }
        private static MutexLease AcquireApplicationMutex()
        {
            string name = "Global\\Pavise_SingleInstance";
#if PAVISE_SELFTEST && PAVISE_UPDATE_INSTALLER_CHECKS
            if (FixtureRoot != null) name = "Local\\Pavise_UpdateFixture_" + HashText(FixtureRoot);
#endif
            Mutex mutex = new Mutex(false, name);
            bool owns = false;
            try { try { owns = mutex.WaitOne(3000); } catch (AbandonedMutexException) { owns = true; } if (!owns) throw new IOException("Another Pavise instance still owns the application runtime."); return new MutexLease { Mutex = mutex }; }
            catch { if (owns) mutex.ReleaseMutex(); mutex.Dispose(); throw; }
        }
        private static string HashText(string value)
        { using (SHA256 hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "").Substring(0, 24); }

        private static bool AnyTargetProcessActive(SelfUpdateTransaction tx)
        {
            HashSet<string> paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(tx.ParentExecutable)) paths.Add(tx.ParentExecutable);
            if (!string.IsNullOrEmpty(tx.LaunchExecutable)) paths.Add(tx.LaunchExecutable);
            foreach (SelfUpdateTarget target in tx.Targets) if (!string.IsNullOrEmpty(target.Path)) paths.Add(target.Path);
            HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); foreach (string path in paths) names.Add(Path.GetFileNameWithoutExtension(path));
            int self = Process.GetCurrentProcess().Id;
            foreach (Process process in Process.GetProcesses())
            {
                using (process)
                {
                    try { if (process.Id == self || !names.Contains(process.ProcessName) || process.HasExited) continue; if (paths.Contains(process.MainModule.FileName)) return true; }
                    catch { try { if (names.Contains(process.ProcessName)) return true; } catch { } }
                }
            }
            return false;
        }

        internal static bool AllowStartupMaintenance(string executable)
        {
            try
            {
                string pending = Settings.LoadStr(PendingKey, "");
                if (!string.IsNullOrEmpty(pending))
                {
                    ValidateDirectory(pending);
                    if (File.Exists(Path.Combine(pending, PlanName))) return false;
                }
                Version committed, current;
                if (Version.TryParse(Settings.LoadStr(CommittedVersionKey, ""), out committed) && Version.TryParse(FileVersionInfo.GetVersionInfo(executable).FileVersion, out current) && current < committed) return false;
            }
            catch { return false; }
            return true;
        }

        private static bool HealthyReceipt(SelfUpdateTransaction tx, SelfUpdatePackage package, Process process)
        {
            try
            {
                string expected = tx.Nonce + "|" + process.Id.ToString(CultureInfo.InvariantCulture) + "|" + process.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture) + "|" + package.Sha256 + "|" + package.Version;
                return ReadMarker(tx, "healthy") == expected && !process.HasExited && Same(process.MainModule.FileName, tx.LaunchExecutable) && Same(HashFile(tx.LaunchExecutable), package.Sha256);
            }
            catch { return false; }
        }

        private static bool HelperAlive(SelfUpdateTransaction tx)
        {
            try
            {
                string[] fields = (ReadMarker(tx, "ready") ?? "").Split('|');
                int pid; long birth;
                if (fields.Length != 3 || fields[0] != tx.Nonce || !int.TryParse(fields[1], out pid) || !long.TryParse(fields[2], out birth)) return false;
                using (Process helper = OpenExactProcess(pid, birth, Path.Combine(tx.Directory, HelperName))) return helper != null && !helper.HasExited;
            }
            catch { return false; }
        }

        private static void QueueCleanup(SelfUpdateTransaction tx, bool committed)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                for (int attempt = 0; attempt < 150; attempt++)
                {
                    try
                    {
                        if (ReadMarker(tx, committed ? "complete" : "restored") == tx.Nonce && !HelperAlive(tx)) { CleanupTransaction(tx, committed); return; }
                    }
                    catch { return; }
                    Thread.Sleep(200);
                }
            });
        }

        private static void CleanupPrevious(SelfUpdateTransaction tx)
        {
            // Own only the recorded previous EXE; another update/task may own other autostart hash directories.
            string previousTask = tx.TaskXml == null ? null : TaskHelper.ParseTaskCommandXml(tx.TaskXml);
            bool parentNeedsCleanup = StartupPayload.IsManagedExecutable(tx.ParentExecutable) && !SameExecutablePath(tx.ParentExecutable, tx.NewManagedExecutable) && File.Exists(tx.ParentExecutable);
            bool taskNeedsCleanup = StartupPayload.IsManagedExecutable(previousTask) && !SameExecutablePath(previousTask, tx.NewManagedExecutable) && File.Exists(previousTask);
            if (parentNeedsCleanup || taskNeedsCleanup)
            {
                string task = TaskHelper.CaptureStartupTaskForUpdate();
                string currentTarget = task == null ? null : TaskHelper.ParseTaskCommandXml(task);
                if (!CleanupRecordedManagedPayloads(tx, currentTarget, StartupPayload.ManagedRoot))
                    throw new IOException("The previous executable is still active, belongs to the current startup task, or changed identity. It was retained.");
            }
            foreach (SelfUpdateTarget target in tx.Targets)
            {
                string backup = Path.Combine(tx.Directory, target.Backup);
                if (File.Exists(backup) && Same(HashFile(backup), target.PreviousHash)) DeleteExact(backup);
                if (File.Exists(backup)) throw new IOException("The previous executable could not be removed. Recovery files were retained.");
            }
        }

        internal static bool CleanupRecordedManagedPayloads(SelfUpdateTransaction tx, string currentTaskTarget, string managedRoot)
        {
            if (!DeletePreviousManagedPayload(tx.ParentExecutable, tx.ParentHash, tx.NewManagedExecutable, currentTaskTarget, managedRoot)) return false;
            string previousTask = tx.TaskXml == null ? null : TaskHelper.ParseTaskCommandXml(tx.TaskXml);
            if (!StartupPayload.IsManagedExecutable(previousTask, managedRoot) || SameExecutablePath(previousTask, tx.ParentExecutable) || SameExecutablePath(previousTask, tx.NewManagedExecutable) || !File.Exists(previousTask)) return true;
            // Verify the exact previous content-addressed EXE named in the original task XML.
            if (!StartupPayload.IsVerifiedExecutable(previousTask, managedRoot)) return false;
            string expectedHash = Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(previousTask)));
            return DeletePreviousManagedPayload(previousTask, expectedHash, tx.NewManagedExecutable, currentTaskTarget, managedRoot);
        }

        internal static bool DeletePreviousManagedPayload(string executable, string expectedHash, string keep, string currentTaskTarget, string managedRoot)
        {
            if (!StartupPayload.IsManagedExecutable(executable, managedRoot) || SameExecutablePath(executable, keep) || !File.Exists(executable)) return true;
            if (SameExecutablePath(executable, currentTaskTarget) || !Same(HashFile(executable), expectedHash)) return false;
            if (AnyTargetProcessActive(new SelfUpdateTransaction { ParentExecutable = executable })) return false;
            DeleteExact(executable);
            if (File.Exists(executable)) return false;
            try { string directory = Path.GetDirectoryName(executable); if (System.IO.Directory.GetFileSystemEntries(directory).Length == 0) System.IO.Directory.Delete(directory, false); } catch { }
            return true;
        }

        private static void CleanupTransaction(SelfUpdateTransaction tx, bool committed)
        {
            ValidateDirectory(tx.Directory);
            if (HelperAlive(tx)) return;
            if (committed) CleanupPrevious(tx);
            foreach (SelfUpdateTarget target in tx.Targets)
            {
                string backup = Path.Combine(tx.Directory, target.Backup);
                if (File.Exists(backup) && Same(HashFile(backup), target.PreviousHash)) DeleteExact(backup);
            }
            foreach (string name in new[] { "candidate.exe", "Pavise.update.exe", "Pavise.update.exe.part", "Pavise.download.exe", "Pavise.download.exe.part", "update.exe", "update.exe.part", HelperName, HelperName + ".config", "ready", "safe-exit", "abort", "healthy", "applied", "complete", "restored", "manual" })
                DeleteExact(Path.Combine(tx.Directory, name));
            foreach (string file in System.IO.Directory.GetFiles(tx.Directory, "Pavise.update.*.exe", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileName(file); Guid id;
                if (name.Length == "Pavise.update.".Length + 32 + ".exe".Length && Guid.TryParseExact(name.Substring("Pavise.update.".Length, 32), "N", out id)) DeleteExact(file);
            }
            string[] remaining = System.IO.Directory.GetFileSystemEntries(tx.Directory);
            if (remaining.Length == 1 && Same(remaining[0], Path.Combine(tx.Directory, PlanName))) { DeleteExact(remaining[0]); remaining = System.IO.Directory.GetFileSystemEntries(tx.Directory); }
            if (remaining.Length == 0)
            {
                System.IO.Directory.Delete(tx.Directory, false);
                if (Same(Settings.LoadStr(PendingKey, ""), tx.Directory)) Settings.Remove(PendingKey);
            }
        }

        private static void WritePlan(SelfUpdateTransaction tx)
        {
            XmlWriterSettings settings = new XmlWriterSettings { Encoding = Utf8, Indent = true };
            string path = Path.Combine(tx.Directory, PlanName);
            using (FileStream file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                using (XmlWriter writer = XmlWriter.Create(file, settings))
                {
                    writer.WriteStartElement("PaviseUpdate"); writer.WriteAttributeString("schema", "1");
                    Element(writer, "Nonce", tx.Nonce); Element(writer, "ParentPid", tx.ParentPid.ToString(CultureInfo.InvariantCulture)); Element(writer, "ParentBirth", tx.ParentBirth.ToString(CultureInfo.InvariantCulture));
                    Element(writer, "ParentExecutable", tx.ParentExecutable); Element(writer, "ParentHash", tx.ParentHash); Element(writer, "DataDirectory", tx.DataDirectory);
                    Element(writer, "LaunchExecutable", tx.LaunchExecutable); Element(writer, "NewManagedExecutable", tx.NewManagedExecutable); Element(writer, "TaskXml", tx.TaskXml);
                    Element(writer, "Manifest", tx.ManifestText); Element(writer, "Language", tx.Language.ToString(CultureInfo.InvariantCulture)); Element(writer, "CandidateHash", tx.CandidateHash); Element(writer, "PreviousCommittedVersion", tx.PreviousCommittedVersion);
                    writer.WriteStartElement("Targets");
                    foreach (SelfUpdateTarget target in tx.Targets)
                    {
                        writer.WriteStartElement("File"); writer.WriteAttributeString("path", target.Path); writer.WriteAttributeString("backup", target.Backup); writer.WriteAttributeString("hash", target.PreviousHash); writer.WriteEndElement();
                    }
                    writer.WriteEndElement(); writer.WriteEndElement(); writer.Flush(); file.Flush(true);
                }
            }
        }

        private static void Element(XmlWriter writer, string name, string value) { writer.WriteElementString(name, value ?? ""); }

        private static SelfUpdateTransaction ReadPlan(string directory)
        {
            ValidateDirectory(directory);
            string path = Path.Combine(directory, PlanName); RejectRedirects(path);
            if (new FileInfo(path).Length > 524288) throw new IOException("The update recovery plan is oversized.");
            XmlDocument document = new XmlDocument(); document.XmlResolver = null;
            using (XmlReader reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null })) document.Load(reader);
            XmlElement root = document.DocumentElement;
            if (root == null || root.Name != "PaviseUpdate" || root.GetAttribute("schema") != "1") throw new IOException("The update recovery plan is invalid.");
            SelfUpdateTransaction tx = new SelfUpdateTransaction(); tx.Directory = Path.GetFullPath(directory);
            tx.Nonce = ReadElement(root, "Nonce"); Guid nonce;
            if (!Guid.TryParseExact(tx.Nonce, "N", out nonce)) throw new IOException("Invalid update identity.");
            tx.ParentPid = int.Parse(ReadElement(root, "ParentPid"), CultureInfo.InvariantCulture); tx.ParentBirth = long.Parse(ReadElement(root, "ParentBirth"), CultureInfo.InvariantCulture);
            tx.ParentExecutable = ReadElement(root, "ParentExecutable"); tx.ParentHash = ReadElement(root, "ParentHash"); tx.DataDirectory = ReadElement(root, "DataDirectory");
            tx.LaunchExecutable = ReadElement(root, "LaunchExecutable"); tx.NewManagedExecutable = ReadElement(root, "NewManagedExecutable"); tx.TaskXml = ReadElement(root, "TaskXml");
            if (tx.TaskXml.Length == 0) tx.TaskXml = null;
            tx.ManifestText = ReadElement(root, "Manifest"); tx.Language = int.Parse(ReadElement(root, "Language"), CultureInfo.InvariantCulture); tx.CandidateHash = ReadElement(root, "CandidateHash"); tx.PreviousCommittedVersion = ReadElement(root, "PreviousCommittedVersion");
            if (tx.ParentPid <= 0 || tx.ParentBirth <= 0 || tx.ParentHash.Length != 64 || !Paths.IsAbsoluteStartupDataPath(tx.DataDirectory) || !Paths.IsAbsoluteStartupDataPath(tx.ParentExecutable) || !Paths.IsAbsoluteStartupDataPath(tx.LaunchExecutable)) throw new IOException("Invalid update paths or process identity.");
            XmlNodeList targets = root.SelectNodes("Targets/File");
            if (targets.Count > 2) throw new IOException("Too many replacement targets.");
            foreach (XmlElement element in targets)
            {
                string backup = "previous-" + tx.Targets.Count.ToString(CultureInfo.InvariantCulture) + ".exe";
                if (element.GetAttribute("backup") != backup || element.GetAttribute("hash") != tx.ParentHash) throw new IOException("Invalid recovery file identity.");
                string target = element.GetAttribute("path");
                if (!Paths.IsAbsoluteStartupDataPath(target) || !string.Equals(Path.GetExtension(target), ".exe", StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid replacement target.");
                tx.Targets.Add(new SelfUpdateTarget { Path = target, Backup = backup, PreviousHash = tx.ParentHash });
            }
            if (!Same(tx.LaunchExecutable, tx.ParentExecutable) && !Same(tx.LaunchExecutable, tx.NewManagedExecutable)) throw new IOException("Invalid restart target.");
            if (!string.IsNullOrEmpty(tx.NewManagedExecutable) && !StartupPayload.IsManagedExecutable(tx.NewManagedExecutable)) throw new IOException("Invalid managed restart target.");
            if (!string.IsNullOrEmpty(tx.NewManagedExecutable) && !Same(Path.Combine(StartupPayload.ManagedRoot, tx.CandidateHash, StartupPayload.ExecutableName), tx.NewManagedExecutable)) throw new IOException("The managed payload directory does not match the update executable.");
            return tx;
        }

        private static string ReadElement(XmlElement root, string name)
        {
            XmlNodeList matches = root.SelectNodes(name);
            if (matches.Count != 1) throw new IOException("Missing or duplicate recovery field: " + name);
            return matches[0].InnerText;
        }

        private static void ValidateDirectory(string directory)
        {
            string full = Path.GetFullPath(directory); Guid id;
            if (!Guid.TryParseExact(Path.GetFileName(full), "N", out id) || !Same(Path.GetDirectoryName(full), UpdateRoot)) throw new IOException("Update files are outside the protected update directory.");
            RejectRedirects(full);
#if PAVISE_SELFTEST && PAVISE_UPDATE_INSTALLER_CHECKS
            if (FixtureRoot != null) return;
#endif
            DirectorySecurity security = System.IO.Directory.GetAccessControl(full);
            SecurityIdentifier admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            if (!security.AreAccessRulesProtected || !admins.Equals(security.GetOwner(typeof(SecurityIdentifier)))) throw new IOException("The update directory is not protected.");
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                SecurityIdentifier sid = (SecurityIdentifier)rule.IdentityReference;
                if (rule.AccessControlType == AccessControlType.Allow && !sid.Equals(admins) && !sid.IsWellKnown(WellKnownSidType.LocalSystemSid)) throw new IOException("The update directory allows untrusted access.");
            }
        }

        private static Process OpenExactProcess(int pid, long birth, string executable)
        {
            Process process = null;
            try
            {
                process = Process.GetProcessById(pid);
                if (!process.HasExited && process.StartTime.ToUniversalTime().Ticks == birth && Same(process.MainModule.FileName, executable)) return process;
            }
            catch { }
            if (process != null) process.Dispose(); return null;
        }

        private static void VerifyCandidate(string path, SelfUpdatePackage package)
        {
            RejectRedirects(path);
            if (new FileInfo(path).Length != package.Size || !Same(HashFile(path), package.Sha256)) throw new IOException("The update executable failed verification.");
            if (AssemblyName.GetAssemblyName(path).Version.ToString(4) != package.Version) throw new IOException("The update executable version differs from the signed manifest.");
        }

        internal static string HashFile(string path)
        {
            RejectRedirects(path);
            using (SHA256 hash = SHA256.Create()) using (FileStream file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                return BitConverter.ToString(hash.ComputeHash(file)).Replace("-", "").ToLowerInvariant();
        }

        private static void CopyExact(string source, string target, bool alreadySame)
        {
            if (alreadySame || Same(source, target)) return;
            RejectRedirects(source); RejectRedirects(target);
            if (File.Exists(target)) { if (Same(HashFile(source), HashFile(target))) return; throw new IOException("An update file already exists with different contents."); }
            using (FileStream input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (FileStream output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { input.CopyTo(output); output.Flush(true); }
        }

        private static string ReadMarker(SelfUpdateTransaction tx, string name)
        {
            string path = Path.Combine(tx.Directory, name);
            RejectRedirects(path);
            if (!File.Exists(path)) return null;
            try
            {
                // Accept only a complete nonce/identity receipt while allowing its original writer to finish.
                using (FileStream file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    byte[] bytes = new byte[4097]; int length = 0, read;
                    while (length < bytes.Length && (read = file.Read(bytes, length, bytes.Length - length)) > 0) length += read;
                    if (length > 4096) throw new IOException("An update receipt is oversized.");
                    return Utf8.GetString(bytes, 0, length);
                }
            }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
        }

        private static void WriteMarker(SelfUpdateTransaction tx, string name, string value)
        {
            ValidateDirectory(tx.Directory);
            string path = Path.Combine(tx.Directory, name); RejectRedirects(path);
            using (FileStream file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read)) { byte[] bytes = Utf8.GetBytes(value); file.Write(bytes, 0, bytes.Length); file.Flush(true); }
        }

        private static void DeleteExact(string path) { try { RejectRedirects(path); if (File.Exists(path)) File.Delete(path); } catch { } }
        internal static void RejectRedirects(string path)
        {
            string current = Path.GetFullPath(path);
            while (!string.IsNullOrEmpty(current))
            {
                if ((File.Exists(current) || System.IO.Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("An update path is redirected.");
                current = Path.GetDirectoryName(current);
            }
        }
        private static string Argument(string[] args, string name)
        {
            string result = null;
            if (args == null) return null;
            for (int i = 0; i < args.Length; i++) if (args[i] == name) { if (result != null || i + 1 >= args.Length) throw new ArgumentException("Duplicate or missing update argument."); result = args[++i]; }
            return result;
        }
        private static bool Same(string first, string second) { return string.Equals(first, second, StringComparison.OrdinalIgnoreCase); }
        private static bool SameExecutablePath(string first, string second)
        {
            if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second)) return false;
            try { return Same(Path.GetFullPath(Environment.ExpandEnvironmentVariables(first.Trim().Trim('"'))), Path.GetFullPath(Environment.ExpandEnvironmentVariables(second.Trim().Trim('"')))); }
            catch { return false; }
        }
        private static string Quote(string value) { return TaskHelper.QuoteCommandArgument(value); }
        private static string Text(string zh, string en) { return Lang.Cur == 0 ? zh : en; }
    }
}
