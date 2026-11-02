#if PAVISE_SELFTEST && PAVISE_UPDATE_INSTALLER_CHECKS
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace PaviseApp
{
    internal static class SelfUpdateInstallerChecks
    {
        private static int assertions;
        private static string output;
        private static readonly BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;

        [STAThread]
        private static int Main(string[] args)
        {
            Settings.UseTransientStoreForCurrentProcess(); Logger.WritesEnabled = false;
            string fixtureRoot = Environment.GetEnvironmentVariable("PAVISE_UPDATE_FIXTURE_ROOT");
            if (!string.IsNullOrEmpty(fixtureRoot)) SelfUpdateInstaller.FixtureRoot = fixtureRoot;
            SelfUpdateInstaller.CaptureTaskForTests = delegate { return null; };
            if (SelfUpdateInstaller.TryRun(args)) return Environment.ExitCode;
            if (args.Length == 2 && args[0] == "--fixture-hold") { File.WriteAllText(args[1], "ready"); Thread.Sleep(2500); return 0; }
            if (args.Length == 2 && args[0] == "--fixture-hold-long") { File.WriteAllText(args[1], "ready"); Thread.Sleep(30000); return 0; }
            if (args.Length == 2 && args[0] == "--fixture-cleanup-probe")
            {
                string directory = Path.Combine(Path.GetFullPath(args[1]), "cleanup-probe-child"); Directory.CreateDirectory(directory);
                string helper = Path.Combine(directory, "Pavise.Update.exe"); File.Copy(Assembly.GetExecutingAssembly().Location, helper);
                ProcessStartInfo start = new ProcessStartInfo(helper, "--fixture-hold-long " + TaskHelper.QuoteCommandArgument(Path.Combine(directory, "ready"))); start.UseShellExecute = false; start.CreateNoWindow = true;
                using (Process child = Process.Start(start)) { File.WriteAllText(Path.Combine(directory, "child-pid.txt"), child.Id.ToString()); Thread.Sleep(30000); }
                return 0;
            }
            if (args.Length > 0 && args[0] == "--update-restart")
            {
                Paths.Init(Paths.ResolveStartupDataArgument(args));
                if (Array.IndexOf(args, "--update-rollback") >= 0)
                {
                    File.WriteAllText(Path.Combine(Paths.Data, "rollback-started.txt"), "restored");
                    SelfUpdateInstaller.AcknowledgeStartup(args); Thread.Sleep(3500); return 0;
                }
                if (Environment.GetEnvironmentVariable("PAVISE_UPDATE_FIXTURE_CRASH") == "1") return 71;
                bool healthy = SelfUpdateInstaller.AcknowledgeStartup(args);
                File.WriteAllText(Path.Combine(Paths.Data, "healthy-result.txt"), healthy ? "healthy" : "rejected");
                Thread.Sleep(4500); return healthy ? 0 : 72;
            }
            if (args.Length == 4 && args[0] == "--parent-fixture")
            {
                Paths.Init(args[1]); Lang.Init();
                string directory = SelfUpdateInstaller.CreateStagingDirectory();
                string staged = Path.Combine(directory, "Pavise.update." + Guid.NewGuid().ToString("N") + ".exe"); File.Copy(args[3], staged);
                SelfUpdatePackage package = SelfUpdateManifest.ParseAndVerify(File.ReadAllText(args[2]));
                SelfUpdateTransaction tx = SelfUpdateInstaller.Prepare(staged, package);
                File.WriteAllText(Path.Combine(Paths.Data, "transaction-path.txt"), tx.Directory);
                SelfUpdateInstaller.LaunchHelper(tx); SelfUpdateInstaller.CommitSafeExit(tx); return 0;
            }
            try
            {
                if (args.Length < 1) return 2;
                output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
                Paths.Data = Path.Combine(output, "data"); Directory.CreateDirectory(Paths.Data); Lang.Init();
                SelfUpdateInstaller.FixtureRoot = Path.Combine(output, "protected-fixture");
                FileReplacement(); PartialFailure(); IntegrityFailures(); LaunchTargetIntegrity(); ActiveTarget(); CleanupBoundary(); ManagedCleanupOwnership(); StartupRecoveryNotice(); StartupUpdateLock(); MarkerReadSharing(); TaskXml(); WindowLock();
                if (args.Length == 3) { EndToEnd(args[1], args[2], false); EndToEnd(args[1], args[2], true); }
                Console.WriteLine("PASS installer assertions=" + assertions + " production_started=false real_scheduler_called=false registry_mutations=false fixture_windows=offscreen");
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }

        private static void Check(bool value, string text) { assertions++; if (!value) throw new Exception(text); }
        private static bool Throws(Action action) { try { action(); return false; } catch { return true; } }
        private static string Folder(string name) { string path = Path.Combine(output, name); Directory.CreateDirectory(path); return path; }
        private static SelfUpdateTransaction Transaction(string folder, int targets)
        {
            SelfUpdateTransaction tx = new SelfUpdateTransaction { Directory = folder, Nonce = Guid.NewGuid().ToString("N") };
            string candidate = Path.Combine(folder, "candidate.exe"); File.Copy(Assembly.GetExecutingAssembly().Location, candidate);
            tx.CandidateHash = SelfUpdateInstaller.HashFile(candidate);
            for (int i = 0; i < targets; i++)
            {
                string old = Path.Combine(folder, "original-" + i + ".exe"); File.WriteAllText(old, "old-version-" + i);
                string backup = "previous-" + i + ".exe"; File.Copy(old, Path.Combine(folder, backup));
                tx.Targets.Add(new SelfUpdateTarget { Path = old, Backup = backup, PreviousHash = SelfUpdateInstaller.HashFile(old) });
            }
            return tx;
        }
        private static SelfUpdatePackage Package(SelfUpdateTransaction tx)
        {
            return new SelfUpdatePackage { Version = App.Version, Sha256 = tx.CandidateHash, Size = new FileInfo(Path.Combine(tx.Directory, "candidate.exe")).Length };
        }
        private static void FileReplacement()
        {
            SelfUpdateTransaction tx = Transaction(Folder("replace"), 1);
            string data = Path.Combine(tx.Directory, "user-settings.txt"); File.WriteAllText(data, "preserve");
            string config = tx.Targets[0].Path + ".config"; File.WriteAllText(config, "keep runtime configuration");
            SelfUpdateInstaller.ApplyFiles(tx, Package(tx));
            Check(SelfUpdateInstaller.HashFile(tx.Targets[0].Path) == tx.CandidateHash, "The original filename did not receive the new executable.");
            Check(File.ReadAllText(data) == "preserve" && File.ReadAllText(config) == "keep runtime configuration", "User files or runtime configuration changed.");
            Check(File.Exists(Path.Combine(tx.Directory, tx.Targets[0].Backup)), "The old version was removed before startup acknowledgement.");
            SelfUpdateInstaller.RollbackFiles(tx);
            Check(SelfUpdateInstaller.HashFile(tx.Targets[0].Path) == tx.Targets[0].PreviousHash, "Rollback did not restore the previous executable.");
            Check(Directory.GetFiles(tx.Directory, "*.PaviseUpdate.*.tmp").Length == 0, "Replacement left same-directory temporary files.");
        }
        private static void PartialFailure()
        {
            SelfUpdateTransaction tx = Transaction(Folder("partial"), 2);
            using (FileStream locked = new FileStream(tx.Targets[1].Path, FileMode.Open, FileAccess.Read, FileShare.Read))
                Check(Throws(delegate { SelfUpdateInstaller.ApplyFiles(tx, Package(tx)); }), "A locked executable was overwritten.");
            Check(SelfUpdateInstaller.HashFile(tx.Targets[0].Path) == tx.CandidateHash, "The first target did not reach the partial-commit state.");
            SelfUpdateInstaller.RollbackFiles(tx);
            foreach (SelfUpdateTarget target in tx.Targets) Check(SelfUpdateInstaller.HashFile(target.Path) == target.PreviousHash, "Partial installation did not roll back all targets.");
        }
        private static void IntegrityFailures()
        {
            SelfUpdateTransaction tx = Transaction(Folder("integrity"), 1);
            SelfUpdatePackage package = Package(tx);
            File.AppendAllText(Path.Combine(tx.Directory, "candidate.exe"), "tamper");
            Check(Throws(delegate { SelfUpdateInstaller.ApplyFiles(tx, package); }), "A modified update executable was installed.");
            Check(SelfUpdateInstaller.HashFile(tx.Targets[0].Path) == tx.Targets[0].PreviousHash, "An invalid candidate changed the original.");
            File.WriteAllText(tx.Targets[0].Path, "a different program");
            Check(Throws(delegate { SelfUpdateInstaller.RollbackFiles(tx); }), "Rollback overwrote an unrelated replacement.");
            File.WriteAllText(Path.Combine(tx.Directory, tx.Targets[0].Backup), "tampered backup");
            Check(Throws(delegate { SelfUpdateInstaller.RollbackFiles(tx); }), "A tampered recovery executable was restored.");
        }
        private static Exception StartFailure(SelfUpdateTransaction tx, bool rollback)
        {
            try
            {
                using (Process process = (Process)typeof(SelfUpdateInstaller).GetMethod("StartApplication", Static).Invoke(null, new object[] { tx.Targets[0].Path, tx, rollback })) { }
                return null;
            }
            catch (Exception error)
            {
                while (error is TargetInvocationException && error.InnerException != null) error = error.InnerException;
                return error;
            }
        }
        private static void LaunchTargetIntegrity()
        {
            SelfUpdateTransaction tx = Transaction(Folder("launch-target-integrity"), 1);
            tx.DataDirectory = Paths.Data;
            SelfUpdateInstaller.ApplyFiles(tx, Package(tx));
            string target = tx.Targets[0].Path;
            tx.ParentHash = tx.Targets[0].PreviousHash;
            File.AppendAllText(target, "modified after successful installation verification");
            string changedHash = SelfUpdateInstaller.HashFile(target);
            Exception error = StartFailure(tx, false);
            Check(error is IOException && error.Message.Contains("changed before startup"), "A target modified after installation verification was not rejected before new-version startup.");
            error = StartFailure(tx, true);
            Check(error is IOException && error.Message.Contains("changed before startup"), "A modified rollback target was not rejected before execution.");
            Check(SelfUpdateInstaller.HashFile(target) == changedHash && File.Exists(Path.Combine(tx.Directory, tx.Targets[0].Backup)), "Rejecting startup changed the target or discarded recovery assets.");
            File.Copy(Path.Combine(tx.Directory, "candidate.exe"), target, true);
            bool writeBlocked = false, deleteBlocked = false; int boundaryCalls = 0;
            SelfUpdateInstaller.BeforeApplicationStartForTests = delegate(string executable)
            {
                boundaryCalls++;
                writeBlocked = Throws(delegate { File.WriteAllText(executable, "unexpected writer"); });
                deleteBlocked = Throws(delegate { File.Delete(executable); });
                throw new IOException("fixture stops before starting a child process");
            };
            Exception rollbackError = null;
            try { error = StartFailure(tx, false); rollbackError = StartFailure(tx, true); }
            finally { SelfUpdateInstaller.BeforeApplicationStartForTests = null; }
            Check(error is IOException && error.Message.Contains("fixture stops"), "The launch lease fixture did not reach the Process.Start boundary.");
            Check(writeBlocked && deleteBlocked, "The target was writable or deletable between hash verification and Process.Start.");
            Check(rollbackError is IOException && rollbackError.Message.Contains("changed before startup") && boundaryCalls == 1, "Rollback startup used the new-version hash instead of the recorded previous-version hash.");
            Check(SelfUpdateInstaller.HashFile(target) == tx.CandidateHash, "The launch-boundary lease failed to preserve the executable.");
        }
        private static void CleanupBoundary()
        {
            string stage = SelfUpdateInstaller.CreateStagingDirectory();
            string downloaded = Path.Combine(stage, "Pavise.update." + Guid.NewGuid().ToString("N") + ".exe"); File.WriteAllText(downloaded, "download");
            string unknown = Path.Combine(stage, "personal-file.txt"); File.WriteAllText(unknown, "keep");
            SelfUpdateInstaller.CleanupStagingDirectory(stage);
            Check(!File.Exists(downloaded) && File.Exists(unknown), "Staging cleanup did not stay within its owned filenames.");
            string outside = Folder("outside"); string unrelated = Path.Combine(outside, "candidate.exe"); File.WriteAllText(unrelated, "keep");
            SelfUpdateInstaller.CleanupStagingDirectory(outside); Check(File.Exists(unrelated), "Cleanup escaped the protected fixture root.");
            string owned = SelfUpdateInstaller.CreateStagingDirectory(); File.WriteAllText(Path.Combine(owned, "transaction.xml"), "transaction"); File.WriteAllText(Path.Combine(owned, "candidate.exe"), "candidate");
            SelfUpdateInstaller.CleanupStagingDirectory(owned); Check(File.Exists(Path.Combine(owned, "candidate.exe")), "Download cancellation deleted an installer-owned transaction.");
        }
        private static void ActiveTarget()
        {
            SelfUpdateTransaction tx = Transaction(Folder("active-target"), 1);
            File.Copy(Assembly.GetExecutingAssembly().Location, tx.Targets[0].Path, true);
            string ready = Path.Combine(tx.Directory, "process-ready.txt");
            ProcessStartInfo start = new ProcessStartInfo(tx.Targets[0].Path, "--fixture-hold " + TaskHelper.QuoteCommandArgument(ready)); start.UseShellExecute = false; start.CreateNoWindow = true;
            using (Process process = Process.Start(start))
            {
                DateTime until = DateTime.UtcNow.AddSeconds(3); while (!File.Exists(ready) && DateTime.UtcNow < until) Thread.Sleep(20);
                Check(File.Exists(ready), "The live-target fixture did not start.");
                Check(Throws(delegate { SelfUpdateInstaller.RollbackFiles(tx); }), "Rollback attempted to replace a live executable.");
                Check(SelfUpdateInstaller.HashFile(tx.Targets[0].Path) == tx.CandidateHash, "Rollback changed the live target.");
                Check(process.WaitForExit(5000), "The live-target fixture failed to exit naturally.");
            }
        }
        private static SelfUpdateTransaction PendingFixture()
        {
            string directory = SelfUpdateInstaller.CreateStagingDirectory();
            SelfUpdateTransaction tx = new SelfUpdateTransaction { Directory = directory, Nonce = Guid.NewGuid().ToString("N"), ParentExecutable = Assembly.GetExecutingAssembly().Location, LaunchExecutable = Assembly.GetExecutingAssembly().Location, ParentHash = SelfUpdateInstaller.HashFile(Assembly.GetExecutingAssembly().Location), CandidateHash = SelfUpdateInstaller.HashFile(Assembly.GetExecutingAssembly().Location), DataDirectory = Paths.Data, ManifestText = "private fixture plan", NewManagedExecutable = "", Language = 0 };
            using (Process self = Process.GetCurrentProcess()) { tx.ParentPid = self.Id; tx.ParentBirth = self.StartTime.ToUniversalTime().Ticks; }
            typeof(SelfUpdateInstaller).GetMethod("WritePlan", Static).Invoke(null, new object[] { tx });
            Settings.SaveStrDurable(SelfUpdateInstaller.PendingKey, directory); return tx;
        }
        private static string ManagedFixture(string root, string contents)
        {
            string source = Path.Combine(root, Guid.NewGuid().ToString("N") + ".source"); File.WriteAllText(source, contents);
            string hash = SelfUpdateInstaller.HashFile(source);
            string directory = Path.Combine(root, hash); Directory.CreateDirectory(directory);
            string executable = Path.Combine(directory, StartupPayload.ExecutableName); File.Move(source, executable); return executable;
        }
        private static void ManagedCleanupOwnership()
        {
            string root = Folder("managed-cleanup");
            string previous = ManagedFixture(root, "recorded previous executable");
            string keep = ManagedFixture(root, "transaction new executable");
            string external = ManagedFixture(root, "later external task target");
            string previousHash = SelfUpdateInstaller.HashFile(previous), externalHash = SelfUpdateInstaller.HashFile(external);
            Check(SelfUpdateInstaller.DeletePreviousManagedPayload(previous, previousHash, keep, external, root), "The recorded obsolete executable could not be cleaned.");
            Check(!File.Exists(previous) && File.Exists(keep) && File.Exists(external) && SelfUpdateInstaller.HashFile(external) == externalHash, "Managed cleanup deleted a non-transaction payload or external startup target.");
            previous = ManagedFixture(root, "previous selected by an external task edit"); previousHash = SelfUpdateInstaller.HashFile(previous);
            Check(!SelfUpdateInstaller.DeletePreviousManagedPayload(previous, previousHash, keep, previous, root) && File.Exists(previous), "Cleanup removed the current externally selected task target.");
            string alias = Path.Combine(Path.GetDirectoryName(previous), "..", Path.GetFileName(Path.GetDirectoryName(previous)), StartupPayload.ExecutableName);
            Check(!SelfUpdateInstaller.DeletePreviousManagedPayload(previous, previousHash, keep, alias, root) && File.Exists(previous), "Cleanup failed to recognize the current task target's equivalent absolute path.");
            File.AppendAllText(previous, " changed");
            Check(!SelfUpdateInstaller.DeletePreviousManagedPayload(previous, previousHash, keep, external, root) && File.Exists(previous), "Cleanup removed a file whose recorded identity changed.");
            string oldTask = ManagedFixture(root, "exact original task snapshot payload");
            SelfUpdateTransaction tx = new SelfUpdateTransaction { ParentExecutable = Path.Combine(root, "manual-original.exe"), NewManagedExecutable = keep, ParentHash = previousHash, TaskXml = TaskHelper.BuildStartupTaskXml(oldTask, TaskHelper.BuildStartupArguments(Paths.Data)) };
            Check(SelfUpdateInstaller.CleanupRecordedManagedPayloads(tx, external, root) && !File.Exists(oldTask), "A manual-origin update did not clean its exact recorded old task payload.");
            Check(File.Exists(keep) && File.Exists(external) && SelfUpdateInstaller.HashFile(external) == externalHash, "Cleaning the recorded task payload removed an unrelated payload.");
        }
        private static void StartupRecoveryNotice()
        {
            Settings.Remove(SelfUpdateInstaller.PendingKey);
            Check(SelfUpdateInstaller.GetStartupManualFailure() == null, "Ordinary startup invented an update recovery warning.");
            SelfUpdateTransaction tx = PendingFixture();
            string planHash = SelfUpdateInstaller.HashFile(Path.Combine(tx.Directory, "transaction.xml"));
            Check(!string.IsNullOrEmpty(SelfUpdateInstaller.GetStartupManualFailure()), "An abandoned pre-replacement update was silently ignored.");
            Check(SelfUpdateInstaller.HashFile(Path.Combine(tx.Directory, "transaction.xml")) == planHash, "Startup failure inspection mutated the recovery plan.");
            File.WriteAllText(Path.Combine(tx.Directory, "applied"), tx.Nonce);
            Check(SelfUpdateInstaller.GetStartupManualFailure().Contains("恢复文件"), "An abandoned replacement did not explain retained recovery files.");
            Check(Throws(delegate { typeof(SelfUpdateInstaller).GetMethod("ResolvePendingBeforePrepare", Static).Invoke(null, null); }), "A new automatic update overwrote unresolved replacement records.");
            Check(File.Exists(Path.Combine(tx.Directory, "transaction.xml")), "Unresolved replacement records were deleted.");
            File.WriteAllText(Path.Combine(tx.Directory, "restored"), tx.Nonce);
            Check(SelfUpdateInstaller.GetStartupManualFailure() == null, "A confirmed restoration still showed an abandoned-update warning.");
            File.Delete(Path.Combine(tx.Directory, "restored")); File.WriteAllText(Path.Combine(tx.Directory, "complete"), tx.Nonce);
            Check(SelfUpdateInstaller.GetStartupManualFailure() == null, "A completed update still showed an abandoned-update warning.");
            typeof(SelfUpdateInstaller).GetMethod("ResolvePendingBeforePrepare", Static).Invoke(null, null);
            Check(!Directory.Exists(tx.Directory) && Settings.LoadStr(SelfUpdateInstaller.PendingKey, "") == "", "A completed stale transaction was not cleaned before another update.");
            tx = PendingFixture();
            typeof(SelfUpdateInstaller).GetMethod("ResolvePendingBeforePrepare", Static).Invoke(null, null);
            Check(!Directory.Exists(tx.Directory) && Settings.LoadStr(SelfUpdateInstaller.PendingKey, "") == "", "A pre-replacement orphan was not safely discarded.");
            Settings.SaveStrDurable(SelfUpdateInstaller.PendingKey, Folder("not-a-private-update"));
            Check(SelfUpdateInstaller.GetStartupManualFailure() == null, "Untrusted recovery locations were treated as valid update plans.");
            Settings.Remove(SelfUpdateInstaller.PendingKey);
        }
        private static void StartupUpdateLock()
        {
            SelfUpdateTransaction tx = PendingFixture();
            string helper = Path.Combine(tx.Directory, "Pavise.Update.exe"); File.Copy(Assembly.GetExecutingAssembly().Location, helper);
            string ready = Path.Combine(output, "startup-lock-ready.txt");
            ProcessStartInfo start = new ProcessStartInfo(helper, "--fixture-hold " + TaskHelper.QuoteCommandArgument(ready)); start.UseShellExecute = false; start.CreateNoWindow = true;
            string[] args = new[] { "--update-restart", "--update-transaction", tx.Directory, "--update-nonce", tx.Nonce };
            using (Process process = Process.Start(start))
            {
                DateTime until = DateTime.UtcNow.AddSeconds(3); while (!File.Exists(ready) && DateTime.UtcNow < until) Thread.Sleep(20);
                Check(File.Exists(ready), "The startup-lock helper fixture did not become ready.");
                File.WriteAllText(Path.Combine(tx.Directory, "ready"), tx.Nonce + "|" + process.Id + "|" + process.StartTime.ToUniversalTime().Ticks);
                Check(SelfUpdateInstaller.IsStartupUpdatePending(args), "A new process did not retain its startup update lock while the helper was active.");
                Check(SelfUpdateInstaller.GetStartupManualFailure() == null, "An active helper was mistaken for an abandoned update.");
                File.WriteAllText(Path.Combine(tx.Directory, "complete"), tx.Nonce);
                Check(!SelfUpdateInstaller.IsStartupUpdatePending(args), "Confirmed completion did not release the startup update lock."); File.Delete(Path.Combine(tx.Directory, "complete"));
                File.WriteAllText(Path.Combine(tx.Directory, "manual"), tx.Nonce);
                Check(!SelfUpdateInstaller.IsStartupUpdatePending(args), "Switching to manual update did not release the startup update lock."); File.Delete(Path.Combine(tx.Directory, "manual"));
                File.WriteAllText(Path.Combine(tx.Directory, "restored"), tx.Nonce);
                Check(!SelfUpdateInstaller.IsStartupUpdatePending(args), "Confirmed restoration did not release the startup update lock."); File.Delete(Path.Combine(tx.Directory, "restored"));
                Check(!SelfUpdateInstaller.IsStartupUpdatePending(new[] { "--update-restart", "--update-transaction", tx.Directory, "--update-nonce", "wrong" }), "An incorrect nonce acquired the startup update lock.");
                Check(!SelfUpdateInstaller.IsStartupUpdatePending(new[] { "--update-restart", "--update-transaction", tx.Directory, "--update-nonce", tx.Nonce, "--update-rollback" }), "A rollback startup acquired the new-version installation lock.");
                Check(process.WaitForExit(5000), "The startup-lock helper fixture did not exit naturally.");
            }
            Check(!SelfUpdateInstaller.IsStartupUpdatePending(args), "An exited helper left the startup update lock active.");
            typeof(SelfUpdateInstaller).GetMethod("ResolvePendingBeforePrepare", Static).Invoke(null, null);
        }
        private static void MarkerReadSharing()
        {
            SelfUpdateTransaction tx = PendingFixture();
            using (FileStream writer = new FileStream(Path.Combine(tx.Directory, "ready"), FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                byte[] bytes = Encoding.UTF8.GetBytes(tx.Nonce); writer.Write(bytes, 0, bytes.Length); writer.Flush(true);
                string receipt = (string)typeof(SelfUpdateInstaller).GetMethod("ReadMarker", Static).Invoke(null, new object[] { tx, "ready" });
                Check(receipt == tx.Nonce, "Polling a flushed receipt collided with its still-open writer.");
            }
            typeof(SelfUpdateInstaller).GetMethod("ResolvePendingBeforePrepare", Static).Invoke(null, null);
        }
        private static void TaskXml()
        {
            string original = "<Task xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\"><Settings><Enabled>false</Enabled><ExecutionTimeLimit>PT0S</ExecutionTimeLimit></Settings><Actions><Exec><Command>C:\\old.exe</Command><Arguments>--autostart</Arguments><WorkingDirectory>C:\\</WorkingDirectory></Exec></Actions></Task>";
            string rewritten = TaskHelper.BuildStartupTaskUpdateXml(original, "C:\\new folder\\PaviseAuto.exe", "C:\\portable data");
            Check(!TaskHelper.StartupTaskEnabled(rewritten), "Updating a disabled task enabled it.");
            Check(TaskHelper.ParseTaskCommandXml(rewritten) == "C:\\new folder\\PaviseAuto.exe", "Startup task target did not change.");
            Check(TaskHelper.ParseTaskArgumentsXml(rewritten) == TaskHelper.BuildStartupArguments("C:\\portable data"), "Portable data was lost in the startup task.");
            Check(rewritten.Contains("<ExecutionTimeLimit>PT0S</ExecutionTimeLimit>"), "Unrelated startup task settings changed.");
            Check(TaskHelper.UpdateTaskCaptureCanBeAbsent(false) && !TaskHelper.UpdateTaskCaptureCanBeAbsent(true), "Task absence was not distinguished from presence.");
            Check(Throws(delegate { TaskHelper.UpdateTaskCaptureCanBeAbsent(null); }), "Unknown task state was treated as absent.");
            Check(Throws(delegate { TaskHelper.BuildStartupTaskUpdateXml("<!DOCTYPE Task [<!ENTITY x SYSTEM 'file:///C:/Windows/win.ini'>]><Task>&x;</Task>", "C:\\new.exe", "C:\\data"); }), "Task XML allowed external entities.");
        }
        private static void WindowLock()
        {
            using (SelfUpdateInstallerWindow window = new SelfUpdateInstallerWindow())
            {
                FormClosingEventArgs closing = new FormClosingEventArgs(CloseReason.UserClosing, false);
                typeof(SelfUpdateInstallerWindow).GetMethod("OnFormClosing", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(window, new object[] { closing });
                Check(closing.Cancel, "Installation window could be closed while busy.");
                IntPtr handle = window.Handle; window.Manual("fixture failure"); Application.DoEvents();
                closing = new FormClosingEventArgs(CloseReason.UserClosing, false);
                typeof(SelfUpdateInstallerWindow).GetMethod("OnFormClosing", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(window, new object[] { closing });
                Check(!closing.Cancel && window.Text.Contains("手动更新"), "Failure did not switch the same window to closable manual update.");
            }
        }
        private static void EndToEnd(string candidate, string manifest, bool crash)
        {
            string scenario = Folder(crash ? "end-to-end-rollback" : "end-to-end-success");
            string parent = Path.Combine(scenario, "Original Pavise.exe"); File.Copy(Assembly.GetExecutingAssembly().Location, parent);
            string oldHash = SelfUpdateInstaller.HashFile(parent), newHash = SelfUpdateInstaller.HashFile(candidate);
            string data = Path.Combine(scenario, "portable-data"); Directory.CreateDirectory(data); File.WriteAllText(Path.Combine(data, "preferences.txt"), "preserved");
            string root = Path.Combine(scenario, "updates");
            ProcessStartInfo start = new ProcessStartInfo(parent, "--parent-fixture " + TaskHelper.QuoteCommandArgument(data) + " " + TaskHelper.QuoteCommandArgument(manifest) + " " + TaskHelper.QuoteCommandArgument(candidate));
            start.UseShellExecute = false; start.CreateNoWindow = true; start.RedirectStandardOutput = true; start.RedirectStandardError = true;
            start.EnvironmentVariables["PAVISE_UPDATE_FIXTURE_ROOT"] = root;
            start.EnvironmentVariables["PAVISE_UPDATE_FIXTURE_CRASH"] = crash ? "1" : "0";
            using (Process child = Process.Start(start))
            {
                Check(child.WaitForExit(18000), "Fixture parent did not complete its safe-exit handoff.");
                string errors = child.StandardError.ReadToEnd(); Check(child.ExitCode == 0, "Fixture parent failed: " + errors);
            }
            DateTime until = DateTime.UtcNow.AddSeconds(18);
            string marker = Path.Combine(data, crash ? "rollback-started.txt" : "healthy-result.txt");
            while (DateTime.UtcNow < until && !File.Exists(marker)) Thread.Sleep(100);
            Check(File.Exists(marker), "The replacement fixture did not report " + (crash ? "rollback" : "healthy startup"));
            Check(SelfUpdateInstaller.HashFile(parent) == (crash ? oldHash : newHash), "The original path has the wrong executable after handoff.");
            Check(File.ReadAllText(Path.Combine(data, "preferences.txt")) == "preserved", "End-to-end installation modified user data.");
            if (!crash)
            {
                string tx = File.ReadAllText(Path.Combine(data, "transaction-path.txt"));
                while (DateTime.UtcNow < until && Directory.Exists(tx)) Thread.Sleep(100);
                Check(!Directory.Exists(tx), "Successful update left its old executable or updater temporary directory.");
            }
            Console.WriteLine("PASS end-to-end " + (crash ? "rollback-after-startup-failure" : "replacement-acknowledgement-cleanup"));
        }
    }
}
#endif
