#if PAVISE_SELFTEST && PAVISE_AUTOSTART_REGRESSION
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Xml;

namespace PaviseApp
{
    internal static class AutostartRegressionRunner
    {
        private static int assertions, suites, failures;
        private static string output;
        private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private static int Main(string[] args)
        {
            if (args.Length != 1) return 2;
            using (var watchdog = new Timer(delegate { Environment.Exit(124); }, null, 45000, Timeout.Infinite))
            {
                try
                {
                    output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
                    Settings.UseTransientStoreForCurrentProcess(); Logger.WritesEnabled = false; Lang.Init();
                    Run("automatic/manual startup visibility decisions", Visibility);
                    Run("single-instance handoff drops stale handles without taking live owner", MutexHandoff);
                    Run("startup data rejects missing relative and root targets", DataValidation);
                    Run("payload survives source rename and removal", PayloadRename);
                    Run("payload versions coexist and corrupted target is immutable", PayloadVersions);
                    Run("portable data and task XML round trip", XmlAndData);
                    Run("task registration XML and fallback use only fake scheduler", SchedulerRegistration);
                    Run("create and refresh preserve disabled tasks and migrate changed targets", CreateAndRefresh);
                    Run("startup enabled state differs from task presence and caches successful changes", StartupEnabledState);
                    Run("tray startup failures preserve scheduler reason and reread actual state", TrayStartupFailures);
                    Run("tray rebuild refreshes external startup changes without showing a window", TrayRebuildStartupState);
                    Run("current task recognizes managed copy and rejects legacy target", TaskIdentity);
                    Run("cleanup remains inside isolated managed root", CleanupBoundary);
                    Console.WriteLine("RESULT suites=" + suites + " failed=" + failures + " assertions=" + assertions
                        + " settings=transient payloads=temp scheduler_called=false registry_mutations=false application_started=false");
                    return failures == 0 ? 0 : 1;
                }
                catch (Exception error) { Console.Error.WriteLine(error); return 1; }
                finally { Logger.WritesEnabled = false; Settings.UseTransientStoreForCurrentProcess(); }
            }
        }
        private static void Run(string name, Action action)
        {
            suites++;
            try { action(); Console.WriteLine("PASS " + name); }
            catch (Exception error)
            {
                while (error is TargetInvocationException && error.InnerException != null) error = error.InnerException;
                failures++; Console.Error.WriteLine("FAIL " + name + ": " + error);
            }
        }
        private static void Check(bool good, string message)
        { assertions++; if (!good) throw new InvalidOperationException(message); }
        private static object Invoke(Type type, string name, Type[] parameters, params object[] args)
        {
            MethodInfo method = type.GetMethod(name, Static, null, parameters, null);
            if (method == null) throw new InvalidOperationException("Missing production startup helper " + type.Name + "." + name);
            return method.Invoke(null, args);
        }
        private static bool Auto(params string[] args)
        { return (bool)Invoke(typeof(Program), "IsAutostart", new[] { typeof(string[]) }, new object[] { args }); }
        private static bool Show(bool auto, bool pending, long requested, long now)
        { return (bool)Invoke(typeof(Program), "ShouldShowStartupPanel", new[] { typeof(bool), typeof(bool), typeof(long), typeof(long) }, auto, pending, requested, now); }
        private static string DataArg(params string[] args)
        { return (string)Invoke(typeof(Paths), "ResolveStartupDataArgument", new[] { typeof(string[]) }, new object[] { args }); }
        private static string Source(string name)
        {
            string dir = Path.Combine(output, name); Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "原始 Pavise & 用户.exe");
            File.Copy(Assembly.GetExecutingAssembly().Location, path, true); return path;
        }
        private static string Root(string name)
        { string path = Path.Combine(output, name); Directory.CreateDirectory(path); return path; }
        private static string Hash(string path)
        { using (SHA256 hash = SHA256.Create()) using (FileStream file = File.OpenRead(path)) return Convert.ToBase64String(hash.ComputeHash(file)); }
        private static bool EqualPath(string first, string second)
        { return string.Equals(Path.GetFullPath(first).TrimEnd('\\'), Path.GetFullPath(second).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); }
        private static bool Throws(Action action)
        { try { action(); return false; } catch { return true; } }

        private static void Visibility()
        {
            Check(!Auto((string[])null) && !Auto() && !Auto("--other"), "Ordinary startup was classified as automatic");
            Check(Auto("--autostart") && Auto("--AUTOSTART") && Auto("--other", "--autostart"), "Automatic invocation was not recognized early");
            Check(!Auto("--autostart-extra") && !Auto("prefix--autostart") && !Auto(" --autostart "), "A partial argument triggered automatic startup");
            long now = new DateTime(2026, 10, 5, 15, 0, 0, DateTimeKind.Utc).Ticks;
            foreach (bool pending in new[] { false, true })
                foreach (long request in new[] { 0L, now - TimeSpan.FromSeconds(31).Ticks, now, now + 1 })
                    Check(Show(false, pending, request, now), "Manual startup unexpectedly hid the panel");
            Check(!Show(true, false, now, now), "Automatic startup showed a panel without a user request");
            Check(!Show(true, true, 0, now), "A legacy pending flag made automatic startup visible");
            Check(!Show(true, true, now - TimeSpan.FromSeconds(30).Ticks - 1, now), "An expired request showed the automatic startup panel");
            Check(!Show(true, true, now + 1, now), "A future timestamp showed the automatic startup panel");
            Check(Show(true, true, now, now) && Show(true, true, now - TimeSpan.FromSeconds(30).Ticks, now), "A fresh manual elevation request was lost");
            Check(!(bool)Invoke(typeof(Program), "ShouldSignalExistingPanel", new[] { typeof(bool) }, true), "Repeated automatic startup would wake the existing panel");
            Check((bool)Invoke(typeof(Program), "ShouldSignalExistingPanel", new[] { typeof(bool) }, false), "Manual repeat startup would no longer wake the existing panel");
            Check(!(bool)Invoke(typeof(Program), "ShouldShowPanelForMissingTray", new[] { typeof(bool), typeof(bool) }, true, false),
                "A missing startup notification area would reveal an automatic startup window");
            Check((bool)Invoke(typeof(Program), "ShouldShowPanelForMissingTray", new[] { typeof(bool), typeof(bool) }, true, true),
                "A fresh manually requested panel would be hidden when the notification area is unavailable");
            Check((bool)Invoke(typeof(Program), "ShouldShowPanelForMissingTray", new[] { typeof(bool), typeof(bool) }, false, false),
                "Manual startup lost its visible fallback when the notification area is unavailable");
            Version currentVersion;
            Check(Version.TryParse(App.Version, out currentVersion) && currentVersion.Build >= 0 && currentVersion.Revision >= 0,
                "The current application version is not a valid four-part release version");
            string precedingVersion = RelativeReleaseVersion(currentVersion, false);
            string futureVersion = RelativeReleaseVersion(currentVersion, true);
            Check((bool)Invoke(typeof(Program), "IsOlderPaviseInstance", new[] { typeof(string), typeof(string), typeof(string) }, "Pavise Auto", App.Author, "0.0.0.0"),
                "An older renamed genuine Pavise could no longer be recognized");
            Check((bool)Invoke(typeof(Program), "IsOlderPaviseInstance", new[] { typeof(string), typeof(string), typeof(string) }, "Pavise Auto", App.Author, precedingVersion),
                "The preceding release would not be replaced by the current release after an executable rename");
            Check(!(bool)Invoke(typeof(Program), "IsOlderPaviseInstance", new[] { typeof(string), typeof(string), typeof(string) }, "Pavise Auto", App.Author, futureVersion),
                "A newer release could be selected for replacement by the current build");
            Check(!(bool)Invoke(typeof(Program), "IsOlderPaviseInstance", new[] { typeof(string), typeof(string), typeof(string) }, "Other product", App.Author, precedingVersion),
                "An unrelated product could be selected for replacement");
            Check(!(bool)Invoke(typeof(Program), "IsOlderPaviseInstance", new[] { typeof(string), typeof(string), typeof(string) }, "Pavise Auto", "Other author", precedingVersion),
                "An unrelated author could be selected for replacement");
            Check(!(bool)Invoke(typeof(Program), "IsOlderPaviseInstance", new[] { typeof(string), typeof(string), typeof(string) }, "Pavise Auto", App.Author, App.Version),
                "The current version would be replaced as an older instance");
            Check(!(bool)Invoke(typeof(Program), "IsOlderPaviseInstance", new[] { typeof(string), typeof(string), typeof(string) }, "Pavise Auto", App.Author, "nonsense"),
                "Unknown process version was considered an older real application");
        }

        private static string RelativeReleaseVersion(Version current, bool newer)
        {
            int[] parts = { current.Major, current.Minor, current.Build, current.Revision };
            for (int i = parts.Length - 1; i >= 0; i--)
            {
                if (newer ? parts[i] == int.MaxValue : parts[i] == 0) continue;
                parts[i] += newer ? 1 : -1;
                for (int trailing = i + 1; trailing < parts.Length; trailing++) parts[trailing] = 0;
                return new Version(parts[0], parts[1], parts[2], parts[3]).ToString();
            }
            throw new InvalidOperationException("The current release has no representable " + (newer ? "newer" : "older") + " version");
        }

        private static void DataValidation()
        {
            string data = Root("argument-validation");
            Check(Throws(delegate { DataArg("--autostart-data"); }), "Missing startup data value was silently accepted");
            Check(Throws(delegate { DataArg("--autostart-data", " "); }), "Blank startup data value was silently accepted");
            Check(Throws(delegate { DataArg("--autostart-data", data, "--autostart-data", data); }), "Duplicate startup data binding was silently accepted");
            Check(DataArg("--AUTOSTART-DATA", data) == data, "Startup data option lost case-insensitive argument semantics");
            foreach (string invalid in new[] { @"\drive-relative", "C:drive-relative", "relative", " " })
                Check(!(bool)Invoke(typeof(Paths), "IsAbsoluteStartupDataPath", new[] { typeof(string) }, invalid),
                    "Startup data predicate accepted a non-absolute path: " + invalid);
            string previousData = Paths.Data, previousDirectory = Environment.CurrentDirectory;
            try
            {
                Paths.Data = data; Environment.CurrentDirectory = output;
                foreach (string invalid in new[] { " ", "relative-data", Path.GetPathRoot(output), Path.GetPathRoot(output).Substring(0, 2) + "drive-relative-data" })
                {
                    Check(Throws(delegate { Invoke(typeof(Paths), "Init", new[] { typeof(string) }, invalid); }), "Invalid startup data target was accepted: " + invalid);
                    Check(Paths.Data == data, "Invalid startup data replaced the live data directory");
                }
            }
            finally { Paths.Data = previousData; Environment.CurrentDirectory = previousDirectory; }
        }

        private static void MutexHandoff()
        {
            string name = "Local\\Pavise_AutostartRegression_" + Guid.NewGuid().ToString("N");
            Mutex owner = null, retained = null; bool ownerOwned = false, replacementOwned = false;
            try
            {
                bool created;
                owner = new Mutex(true, name, out created); ownerOwned = created;
                Check(created, "Unique old-owner mutex fixture already existed");
                retained = new Mutex(true, name, out created);
                Check(!created, "Second handle did not observe the existing owner");
                owner.ReleaseMutex(); ownerOwned = false; owner.Dispose(); owner = null;
                using (var proof = new Mutex(true, name, out created))
                    Check(!created, "Retained secondary handle did not reproduce the stale named-object bug");
                Mutex previous = retained;
                replacementOwned = Program.RecreateSingleInstanceMutex(ref retained, name);
                Check(replacementOwned && retained != null, "Handoff failed to create and own a fresh mutex after the old owner closed");
                Check(!ReferenceEquals(previous, retained), "Handoff retained its stale named-object handle");
                retained.ReleaseMutex(); replacementOwned = false;
            }
            finally
            {
                if (replacementOwned && retained != null) try { retained.ReleaseMutex(); } catch { }
                if (ownerOwned && owner != null) try { owner.ReleaseMutex(); } catch { }
                if (retained != null) retained.Dispose(); if (owner != null) owner.Dispose();
            }

            name = "Local\\Pavise_AutostartRegression_Live_" + Guid.NewGuid().ToString("N");
            using (var ready = new ManualResetEvent(false))
            using (var release = new ManualResetEvent(false))
            {
                Exception workerError = null; bool ownerCreated = false;
                var worker = new Thread(delegate()
                {
                    Mutex live = null; bool owned = false;
                    try
                    {
                        live = new Mutex(true, name, out ownerCreated); owned = ownerCreated;
                        ready.Set();
                        if (!release.WaitOne(5000)) throw new InvalidOperationException("Live-owner fixture was not released");
                    }
                    catch (Exception error) { workerError = error; ready.Set(); }
                    finally
                    {
                        if (owned && live != null) try { live.ReleaseMutex(); } catch { }
                        if (live != null) live.Dispose();
                    }
                });
                worker.IsBackground = true; worker.Start(); retained = null;
                try
                {
                    Check(ready.WaitOne(3000) && ownerCreated && workerError == null, "Live-owner fixture did not start");
                    bool created;
                    retained = new Mutex(true, name, out created);
                    Check(!created, "Live owner was not visible to the secondary handle");
                    Mutex previous = retained;
                    Check(!Program.RecreateSingleInstanceMutex(ref retained, name) && retained != null,
                        "Handoff claimed a new mutex while another owner was still running");
                    Check(!ReferenceEquals(previous, retained), "Failed handoff kept an obsolete handle");
                    Check(!retained.WaitOne(0), "Handoff incorrectly acquired a mutex held by a live foreign thread");
                }
                finally
                {
                    if (retained != null) retained.Dispose(); release.Set();
                    Check(worker.Join(5000), "Live-owner fixture did not terminate");
                }
                Check(workerError == null, "Live-owner fixture failed: " + workerError);
            }
        }

        private static void PayloadRename()
        {
            string source = Source("rename-source"), root = Root("rename-managed");
            string sourceHash = Hash(source);
            string expected = StartupPayload.GetExpectedExecutable(source, root);
            string payload = StartupPayload.Ensure(source, root);
            Check(EqualPath(expected, payload) && File.Exists(payload), "Staged payload differs from its predicted task target");
            Check(!EqualPath(source, payload) && StartupPayload.IsManagedExecutable(payload, root), "Startup still references the renameable source");
            Check(Hash(payload) == sourceHash, "Payload bytes differ from the original build");
            string renamed = Path.Combine(Path.GetDirectoryName(source), "用户真的改名.exe"); File.Move(source, renamed);
            Check(!File.Exists(source) && File.Exists(payload) && Hash(payload) == sourceHash, "Renaming the source invalidated the startup payload");
            File.Delete(renamed);
            Check(!File.Exists(renamed) && File.Exists(payload) && Hash(payload) == sourceHash, "Removing the original EXE invalidated the task payload");
            Check(!StartupPayload.IsManagedExecutable(Path.Combine(root + "-other", Path.GetFileName(payload)), root), "Managed path check accepted a sibling root");
        }

        private static void PayloadVersions()
        {
            string source = Source("versions-source"), root = Root("versions-managed");
            string first = StartupPayload.Ensure(source, root), firstHash = Hash(first);
            DateTime modified = File.GetLastWriteTimeUtc(first);
            using (var held = new FileStream(first, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                string again = StartupPayload.Ensure(source, root);
                Check(EqualPath(again, first) && Hash(again) == firstHash, "Same build staging failed with a running/read-held payload");
                Check(File.GetLastWriteTimeUtc(first) == modified, "Same build staging rewrote a valid immutable payload");
                using (var append = new FileStream(source, FileMode.Append, FileAccess.Write)) append.WriteByte(127);
                string second = StartupPayload.Ensure(source, root);
                Check(!EqualPath(first, second) && File.Exists(first) && File.Exists(second), "New build overwrote the prior build payload");
                Check(Hash(first) == firstHash && Hash(second) == Hash(source), "New payload staging altered the old build or copied wrong bytes");
            }
            string badSource = Source("corrupt-source"), badRoot = Root("corrupt-managed");
            string bad = StartupPayload.GetExpectedExecutable(badSource, badRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(bad)); File.WriteAllBytes(bad, new byte[] { 9, 8, 7 });
            string corruptHash = Hash(bad);
            Check(Throws(delegate { StartupPayload.Ensure(badSource, badRoot); }), "A corrupted target was accepted as a verified payload");
            Check(Hash(bad) == corruptHash, "Staging overwrote an existing corrupted/possibly running payload");
        }

        private static void XmlAndData()
        {
            string data = Root("便携 中文 & 数据 空格") + "\\";
            string arguments = TaskHelper.BuildStartupArguments(data);
            string[] words = ParseArguments(arguments);
            Check(words.Length == 3 && words[0] == "--autostart" && words[1] == "--autostart-data", "Task arguments lost automatic startup or explicit data");
            Check(EqualPath(words[2], data), "Quoted trailing-backslash data path did not survive Windows argument parsing");
            Check(EqualPath(DataArg(words), data), "Automatic data argument did not resolve to the original portable directory");
            Check(DataArg("--other") == null && DataArg((string[])null) == null, "Normal startup invented a managed data override");
            string previous = Paths.Data;
            try
            {
                Invoke(typeof(Paths), "Init", new[] { typeof(string) }, words[2]);
                Check(EqualPath(Paths.Data, data) && Directory.Exists(Paths.Data), "Managed startup drifted into its own payload directory's settings");
            }
            finally { Paths.Data = previous; }
            string executable = @"C:\缓存 & 项目\fake ' payload.exe";
            string xml = TaskHelper.BuildStartupTaskXml(executable, arguments);
            var document = new XmlDocument(); document.XmlResolver = null; document.LoadXml(xml);
            Check(TaskHelper.ParseTaskCommandXml(xml) == executable, "XML special characters altered the command");
            Check(TaskHelper.ParseTaskArgumentsXml(xml) == arguments, "XML escaping altered the quoted data argument");
            Check(!TaskHelper.StartupTaskSettingsStale(xml), "New task retained battery stop or runtime timeout");
            Check(document.SelectSingleNode("//*[local-name()='RunLevel']").InnerText == "HighestAvailable", "New startup task lost highest privileges");
            Check(document.SelectSingleNode("//*[local-name()='LogonTrigger']") != null, "New startup task has no logon trigger");
            Check(document.SelectSingleNode("//*[local-name()='WorkingDirectory']").InnerText == Path.GetDirectoryName(executable),
                "Task working directory does not belong to its managed executable");
            string literalArguments = arguments + " --fixture <tag>";
            string literalXml = TaskHelper.BuildStartupTaskXml(executable, literalArguments);
            Check(xml.Contains("&amp;") && literalXml.Contains("&lt;") && TaskHelper.ParseTaskArgumentsXml(literalXml) == literalArguments,
                "Task XML did not escape literal special characters");
            Check(TaskHelper.StartupTaskEnabled(xml), "New task is not enabled by default");
            string disabled = xml.Replace("<Settings>", "<Settings><Enabled>false</Enabled>");
            Check(!TaskHelper.StartupTaskEnabled(disabled), "An externally disabled task would be treated as enabled");
            Check(!TaskHelper.StartupTaskEnabled("broken XML"), "Unreadable task XML was treated as enabled");
            string commandLine = TaskHelper.BuildStartupCommandLine(executable, arguments);
            string[] commandWords = ParseArguments(commandLine);
            Check(commandWords.Length == 4 && commandWords[0] == executable && commandWords[1] == "--autostart"
                && commandWords[2] == "--autostart-data" && EqualPath(commandWords[3], data),
                "Fallback task command line lost command/data quoting");
            string[] fallbackWords = ParseArguments("/TR " + TaskHelper.QuoteCommandArgument(commandLine));
            Check(fallbackWords.Length == 2 && fallbackWords[0] == "/TR" && fallbackWords[1] == commandLine,
                "Nested /TR quoting did not preserve the entire startup command line");
        }

        private static bool Current(string current, string data, string target, string arguments, string root)
        { return (bool)Invoke(typeof(TaskHelper), "IsCurrentTaskTarget", new[] { typeof(string), typeof(string), typeof(string), typeof(string), typeof(string) }, current, data, target, arguments, root); }
        private static void TaskIdentity()
        {
            string source = Source("task-source"), root = Root("task-managed"), data = Root("原设置 data & 中文");
            string payload = StartupPayload.Ensure(source, root), arguments = TaskHelper.BuildStartupArguments(data);
            Check(Current(source, data, payload, arguments, root), "Correct managed payload was considered stale because its filename differs from source");
            Check(!Current(source, data, source, arguments, root), "Renameable legacy source target was accepted as stable");
            Check(!Current(source, data, payload, "--autostart", root), "A task lacking original data binding was considered current");
            Check(!Current(source, data, payload, TaskHelper.BuildStartupArguments(Root("different-data")), root), "Task pointing to another data store was considered current");
            using (var append = new FileStream(source, FileMode.Append, FileAccess.Write)) append.WriteByte(126);
            Check(!Current(source, data, payload, arguments, root), "Task payload from the previous build was considered current");
            string next = StartupPayload.Ensure(source, root);
            Check(Current(source, data, next, arguments, root), "Fresh build payload failed the current-task identity check");
            string temporary = @"C:\Users\fixture\AppData\Local\Temp\download.exe", stable = @"C:\Games\Pavise.exe";
            Type[] legacy = new[] { typeof(string), typeof(string), typeof(string), typeof(bool) };
            Check((bool)Invoke(typeof(TaskHelper), "ShouldPreserveLegacyTarget", legacy, temporary, stable, root, true),
                "A temporary download would silently hijack a live stable legacy installation");
            Check(!(bool)Invoke(typeof(TaskHelper), "ShouldPreserveLegacyTarget", legacy, temporary, stable, root, false),
                "Renamed/removed legacy task target would block migration to a stable payload");
            Check(!(bool)Invoke(typeof(TaskHelper), "ShouldPreserveLegacyTarget", legacy, temporary, next, root, true),
                "Existing managed payload would block refreshing to a new managed build");
            Check(!(bool)Invoke(typeof(TaskHelper), "ShouldPreserveLegacyTarget", legacy, stable, temporary, root, true),
                "A legacy temporary task target would block migration from a stable application");
        }

        private static void CleanupBoundary()
        {
            string source = Source("cleanup-source"), root = Root("cleanup-managed");
            string first = StartupPayload.Ensure(source, root);
            using (var append = new FileStream(source, FileMode.Append, FileAccess.Write)) append.WriteByte(125);
            string current = StartupPayload.Ensure(source, root);
            string sibling = Root("cleanup-managed-sibling"), sentinel = Path.Combine(sibling, "keep.txt");
            File.WriteAllText(sentinel, "outside managed root");
            Invoke(typeof(StartupPayload), "Cleanup", new[] { typeof(string), typeof(string) }, root, current);
            Check(File.Exists(current) && Hash(current) == Hash(source), "Cleanup removed or changed the current startup payload");
            Check(File.ReadAllText(sentinel) == "outside managed root", "Cleanup escaped the exact managed root");
            Check(!EqualPath(first, current), "Cleanup fixture did not contain an older version");
            Check(!File.Exists(first), "Cleanup kept an unreferenced older payload");
        }

        private static void SchedulerRegistration()
        {
            string source = Source("registration-source"), root = Root("registration-managed");
            string executable = StartupPayload.Ensure(source, root), data = Root("registration 中文 & data");
            string arguments = TaskHelper.BuildStartupArguments(data), previousData = Paths.Data;
            TaskHelper.StartupRunForTest previousRun = TaskHelper.RunForTest;
            try
            {
                Paths.Data = data;
                foreach (int variant in new[] { 0, 1, 2 })
                {
                    Settings.UseTransientStoreForCurrentProcess(); Settings.SaveStr("AutostartExe", "unchanged target");
                    int calls = 0; string xmlPath = null;
                    TaskHelper.RunForTest = delegate(string command, bool capture, out string stdout)
                    {
                        stdout = null; calls++;
                        Check(!capture, "Registration unexpectedly tried a scheduler query");
                        string[] words = ParseArguments(command);
                        Check(Array.IndexOf(words, "/Create") >= 0 && Array.IndexOf(words, "PaviseAuto") >= 0,
                            "Registration attempted an unexpected scheduler command");
                        int xmlIndex = Array.IndexOf(words, "/XML"), trIndex = Array.IndexOf(words, "/TR");
                        if (xmlIndex >= 0)
                        {
                            Check(calls == 1 && trIndex < 0, "Registration did not attempt XML first");
                            xmlPath = words[xmlIndex + 1];
                            Check(EqualPath(Path.GetDirectoryName(xmlPath), data) && File.Exists(xmlPath), "Task XML escaped the isolated data directory");
                            string xml = File.ReadAllText(xmlPath);
                            Check(TaskHelper.ParseTaskCommandXml(xml) == executable && TaskHelper.ParseTaskArgumentsXml(xml) == arguments,
                                "Real registration XML did not target the staged payload and original data");
                            Check(!TaskHelper.StartupTaskSettingsStale(xml), "Real registration XML retained battery/time limits");
                            return variant == 0 ? 0 : 1;
                        }
                        Check(calls == 2 && trIndex >= 0 && xmlIndex < 0, "Registration fallback did not route through /TR");
                        string[] startup = ParseArguments(words[trIndex + 1]);
                        Check(startup.Length == 4 && startup[0] == executable && startup[1] == "--autostart"
                            && startup[2] == "--autostart-data" && EqualPath(startup[3], data),
                            "Real /TR fallback lost the staged command or original data argument");
                        return variant == 2 ? 1 : 0;
                    };
                    int result = (int)Invoke(typeof(TaskHelper), "RegisterStartupTask", new[] { typeof(string), typeof(string), typeof(bool) }, executable, arguments, true);
                    Check(result == (variant == 2 ? 1 : 0) && calls == (variant == 0 ? 1 : 2), "Scheduler success/failure routing result was incorrect");
                    Check(xmlPath != null && !File.Exists(xmlPath), "Temporary task registration XML was not released and cleaned up");
                    Check(Settings.LoadStr("AutostartExe", "") == (variant == 2 ? "unchanged target" : executable),
                        "Task registration persisted a failed target or lost the successful payload target");
                }
            }
            finally { TaskHelper.RunForTest = previousRun; Paths.Data = previousData; Settings.UseTransientStoreForCurrentProcess(); }
        }

        private static void CreateAndRefresh()
        {
            string previousRoot = StartupPayload.ManagedRootForTest, previousData = Paths.Data;
            TaskHelper.StartupRunForTest previousRun = TaskHelper.RunForTest;
            try
            {
                string source = System.Windows.Forms.Application.ExecutablePath;
                Check(EqualPath(source, Assembly.GetExecutingAssembly().Location), "Full-chain fixture source is not its isolated runner");
                foreach (string scenario in new[] { "absent", "disabled", "unreadable", "missing-legacy", "current", "different-build", "different-build-running", "create-fallback" })
                {
                    Settings.UseTransientStoreForCurrentProcess();
                    string root = Root("full-chain-" + scenario), data = Root("full-data-" + scenario + " 中文 & data");
                    StartupPayload.ManagedRootForTest = root; Paths.Data = data;
                    string arguments = TaskHelper.BuildStartupArguments(data), expected = StartupPayload.GetExpectedExecutable(source, root);
                    Settings.SaveStr("AutostartExe", "unchanged target");
                    string taskXml = null;
                    if (scenario == "disabled" || scenario == "missing-legacy")
                        taskXml = TaskHelper.BuildStartupTaskXml(Path.Combine(output, "removed-renamed-legacy.exe"), "--autostart");
                    if (scenario == "disabled") taskXml = taskXml.Replace("<Settings>", "<Settings><Enabled>false</Enabled>");
                    if (scenario == "unreadable") taskXml = "broken XML";
                    string oldTarget = null;
                    if (scenario == "current" || scenario.StartsWith("different-build", StringComparison.Ordinal))
                    {
                        string other = Source("older-build-source-" + scenario);
                        using (var append = new FileStream(other, FileMode.Append, FileAccess.Write)) append.WriteByte(124);
                        oldTarget = StartupPayload.Ensure(other, root);
                        taskXml = TaskHelper.BuildStartupTaskXml(oldTarget, arguments);
                    }
                    if (scenario == "current")
                    {
                        string current = StartupPayload.Ensure(source, root);
                        taskXml = TaskHelper.BuildStartupTaskXml(current, arguments);
                    }
                    // A running copy is mapped without delete sharing; a read handle without FileShare.Delete stands in for it.
                    FileStream running = scenario == "different-build-running"
                        ? new FileStream(oldTarget, FileMode.Open, FileAccess.Read, FileShare.Read) : null;
                    int queries = 0, registrations = 0, fallbacks = 0; var xmlFiles = new List<string>();
                    TaskHelper.RunForTest = delegate(string command, bool capture, out string stdout)
                    {
                        stdout = null; string[] words = ParseArguments(command);
                        if (Array.IndexOf(words, "/Query") >= 0)
                        {
                            queries++;
                            if (taskXml == null) return 1;
                            if (capture) stdout = taskXml;
                            return 0;
                        }
                        Check(Array.IndexOf(words, "/Create") >= 0, "Full-chain startup used an unexpected task mutation");
                        registrations++;
                        int xmlIndex = Array.IndexOf(words, "/XML"), trIndex = Array.IndexOf(words, "/TR");
                        if (xmlIndex >= 0)
                        {
                            string xmlFile = words[xmlIndex + 1]; xmlFiles.Add(xmlFile);
                            Check(EqualPath(Path.GetDirectoryName(xmlFile), data), "Full-chain XML escaped its temporary data store");
                            string proposed = File.ReadAllText(xmlFile);
                            Check(TaskHelper.ParseTaskCommandXml(proposed) == expected
                                && TaskHelper.ParseTaskArgumentsXml(proposed) == arguments,
                                "Full-chain registration did not use its hash payload and bound original data");
                            if (scenario == "create-fallback") return 1;
                            taskXml = proposed; return 0;
                        }
                        Check(trIndex >= 0 && scenario == "create-fallback", "Unexpected full-chain fallback");
                        fallbacks++;
                        string[] startup = ParseArguments(words[trIndex + 1]);
                        Check(startup.Length == 4 && startup[0] == expected && startup[1] == "--autostart"
                            && startup[2] == "--autostart-data" && EqualPath(startup[3], data),
                            "Full-chain create fallback lost its managed target or original settings");
                        taskXml = TaskHelper.BuildStartupTaskXml(expected, arguments); return 0;
                    };
                    try
                    {
                        if (scenario == "create-fallback")
                            Check(TaskHelper.CreateStartupTask() == 0, "Explicit startup creation did not recover through fake /TR fallback");
                        else TaskHelper.RefreshStartupTask();
                    }
                    finally { if (running != null) running.Dispose(); }
                    if (scenario == "absent" || scenario == "disabled" || scenario == "unreadable")
                    {
                        Check(queries == (scenario == "absent" ? 1 : 2) && registrations == 0, "Refresh created or enabled an absent/disabled/unreadable task");
                        Check(Directory.GetFiles(root, "*", SearchOption.AllDirectories).Length == 0,
                            "Refresh copied a payload despite an absent/disabled/unreadable task");
                        Check(Settings.LoadStr("AutostartExe", "") == "unchanged target", "Inactive task refresh changed startup settings");
                    }
                    else
                    {
                        Check(File.Exists(expected) && Hash(expected) == Hash(source), "Create/refresh did not stage the exact current executable");
                        Check(Settings.LoadStr("AutostartExe", "") == expected, "Create/refresh persisted the original renameable executable");
                        Check(TaskHelper.ParseTaskCommandXml(taskXml) == expected && TaskHelper.ParseTaskArgumentsXml(taskXml) == arguments,
                            "Refreshed task lost its current managed target/data binding");
                        Check(registrations == (scenario == "current" ? 0 : scenario == "create-fallback" ? 2 : 1),
                            "Refresh repeatedly registered a current task or failed to migrate a stale target");
                        Check(fallbacks == (scenario == "create-fallback" ? 1 : 0), "Full-chain fallback routing was incorrect");
                        if (scenario == "current" || scenario == "different-build")
                            Check(oldTarget != null && !File.Exists(oldTarget) && !Directory.Exists(Path.GetDirectoryName(oldTarget)),
                                "Refresh kept an unreferenced prior payload (" + scenario + ")");
                        if (scenario == "different-build-running")
                            Check(oldTarget != null && File.Exists(oldTarget), "Refreshing a new build removed a prior payload that was still in use");
                        Check(File.Exists(expected), "Payload cleanup removed the current startup payload");
                    }
                    foreach (string xmlFile in xmlFiles) Check(!File.Exists(xmlFile), "Full-chain task registration left a temporary XML file");
                }
            }
            finally { StartupPayload.ManagedRootForTest = previousRoot; TaskHelper.RunForTest = previousRun; Paths.Data = previousData; Settings.UseTransientStoreForCurrentProcess(); }
        }

        private static bool StartupEnabled(bool cached)
        {
            return (bool)Invoke(typeof(TaskHelper), cached ? "IsStartupTaskEnabledCached" : "IsStartupTaskEnabled", Type.EmptyTypes);
        }

        private static void StartupEnabledState()
        {
            string previousRoot = StartupPayload.ManagedRootForTest, previousData = Paths.Data;
            TaskHelper.StartupRunForTest previousRun = TaskHelper.RunForTest;
            try
            {
                Settings.UseTransientStoreForCurrentProcess();
                string root = Root("enabled-state-managed"), data = Root("enabled-state-data");
                StartupPayload.ManagedRootForTest = root; Paths.Data = data;
                string enabledXml = TaskHelper.BuildStartupTaskXml(Path.Combine(output, "state-existing.exe"), TaskHelper.BuildStartupArguments(data));
                string taskXml = enabledXml.Replace("<Settings>", "<Settings><Enabled>false</Enabled>");
                int queries = 0, creates = 0, deletes = 0;
                TaskHelper.RunForTest = delegate(string command, bool capture, out string stdout)
                {
                    stdout = null; string[] words = ParseArguments(command);
                    if (Array.IndexOf(words, "/Query") >= 0)
                    {
                        queries++;
                        if (taskXml == null) return 1;
                        if (capture) stdout = taskXml;
                        return 0;
                    }
                    if (Array.IndexOf(words, "/Delete") >= 0)
                    { deletes++; taskXml = null; return 0; }
                    Check(Array.IndexOf(words, "/Create") >= 0, "Enabled-state fixture saw an unexpected scheduler operation");
                    creates++;
                    int xmlIndex = Array.IndexOf(words, "/XML");
                    Check(xmlIndex >= 0 && capture == false, "Enabled-state creation did not use the temporary XML fixture");
                    string xmlFile = words[xmlIndex + 1];
                    Check(EqualPath(Path.GetDirectoryName(xmlFile), data), "Enabled-state creation escaped its temporary data store");
                    taskXml = File.ReadAllText(xmlFile); return 0;
                };
                Check(TaskHelper.TaskExists() && TaskHelper.TaskExistsCached(), "A disabled task lost its independent existence state");
                Check(!StartupEnabled(false), "A disabled existing task was displayed as enabled startup");
                int snapshot = queries;
                Check(!StartupEnabled(true) && queries == snapshot, "The disabled enabled-state cache was not used");
                taskXml = enabledXml;
                Check(!StartupEnabled(true) && queries == snapshot, "A cached check unexpectedly queried the scheduler");
                Check(StartupEnabled(false), "A fresh enabled-state read did not observe an externally enabled task");
                snapshot = queries;
                Check(StartupEnabled(true) && queries == snapshot, "Fresh enabled state did not update the cache");
                Check(TaskHelper.DeleteStartupTask() == 0 && deletes == 1, "The fixture task was not successfully deleted");
                Check(!StartupEnabled(true) && !TaskHelper.TaskExistsCached(), "Successful deletion left startup enabled or task existence cached");
                snapshot = queries;
                Check(TaskHelper.CreateStartupTask() == 0 && creates == 1, "The fixture task was not successfully created");
                Check(StartupEnabled(true) && TaskHelper.TaskExistsCached() && queries == snapshot,
                    "Successful creation did not synchronize existence and enabled caches");
                Check(TaskHelper.StartupTaskEnabled(taskXml), "Successful task creation left its XML disabled");
                taskXml = "broken XML";
                Check(TaskHelper.TaskExists() && !StartupEnabled(false) && !StartupEnabled(true),
                    "Unreadable task XML was reported as enabled startup");
                taskXml = null;
                Check(!StartupEnabled(false) && !StartupEnabled(true), "An absent task was reported as enabled startup");
            }
            finally { StartupPayload.ManagedRootForTest = previousRoot; TaskHelper.RunForTest = previousRun; Paths.Data = previousData; Settings.UseTransientStoreForCurrentProcess(); }
        }

        private static void TrayStartupFailures()
        {
            string previousRoot = StartupPayload.ManagedRootForTest, previousData = Paths.Data;
            TaskHelper.StartupRunForTest previousRun = TaskHelper.RunForTest;
            FieldInfo hookField = typeof(TrayMenu).GetField("StartupTaskWarningForTest", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            FieldInfo errorField = typeof(TaskHelper).GetField("lastSchtasksError", Static);
            Check(hookField != null && errorField != null, "The no-UI warning/scheduler-error fixture hooks are unavailable");
            object tray = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(TrayMenu));
            object previousHook = hookField.GetValue(tray);
            try
            {
                MethodInfo toggle = typeof(TrayMenu).GetMethod("ToggleAutostart", BindingFlags.Instance | BindingFlags.NonPublic);
                Check(toggle != null, "The actual tray startup toggle was not found");
                foreach (string scenario in new[] { "create-disabled", "create-absent", "delete" })
                {
                    Settings.UseTransientStoreForCurrentProcess(); Settings.SaveStr("AutostartExe", "unchanged fixture target");
                    string root = Root("tray-failure-managed-" + scenario), data = Root("tray-failure-data-" + scenario);
                    StartupPayload.ManagedRootForTest = root; Paths.Data = data;
                    string enabledXml = TaskHelper.BuildStartupTaskXml(Path.Combine(output, "tray-existing.exe"), TaskHelper.BuildStartupArguments(data));
                    string disabledXml = enabledXml.Replace("<Settings>", "<Settings><Enabled>false</Enabled>");
                    bool deleting = scenario == "delete";
                    string taskXml = deleting ? disabledXml : enabledXml;
                    string reason = "fixture permission denied: " + scenario;
                    int queries = 0, creates = 0, deletes = 0;
                    TaskHelper.RunForTest = delegate(string command, bool capture, out string stdout)
                    {
                        stdout = null; string[] words = ParseArguments(command);
                        if (Array.IndexOf(words, "/Query") >= 0)
                        {
                            queries++;
                            if (taskXml == null) return 1;
                            if (capture) stdout = taskXml;
                            return 0;
                        }
                        if (Array.IndexOf(words, "/Create") >= 0) creates++;
                        else if (Array.IndexOf(words, "/Delete") >= 0) deletes++;
                        else Check(false, "Tray failure fixture saw an unexpected scheduler operation");
                        errorField.SetValue(null, reason);
                        return 7;
                    };
                    Check(StartupEnabled(false) == !deleting, "The opposite-cache tray fixture failed to initialize");
                    taskXml = deleting ? enabledXml : scenario == "create-disabled" ? disabledXml : null;
                    var warnings = new List<string>();
                    hookField.SetValue(tray, new Action<string>(delegate(string message) { warnings.Add(message); }));
                    int beforeToggle = queries;
                    toggle.Invoke(tray, null);
                    Check(creates == (deleting ? 0 : 2) && deletes == (deleting ? 1 : 0),
                        "The tray click used stale cached state or repeated the wrong mutation: " + scenario);
                    Check(warnings.Count == 1, "One failed tray click did not show exactly one warning: " + scenario);
                    Check(warnings[0].IndexOf(reason, StringComparison.Ordinal) >= 0,
                        "The failed tray click lost the original scheduler error during state reread: " + scenario);
                    Check(queries - beforeToggle >= 2, "The tray click did not reread actual task state after a failure: " + scenario);
                    int snapshot = queries;
                    Check(StartupEnabled(true) == deleting && queries == snapshot,
                        "A failed tray click left its actual enabled state incorrectly cached: " + scenario);
                    Check(Settings.LoadStr("AutostartExe", "") == "unchanged fixture target",
                        "A failed tray click persisted its attempted startup target: " + scenario);
                }
            }
            finally
            {
                hookField.SetValue(tray, previousHook); StartupPayload.ManagedRootForTest = previousRoot;
                TaskHelper.RunForTest = previousRun; Paths.Data = previousData; Settings.UseTransientStoreForCurrentProcess();
            }
        }

        private static void TrayRebuildStartupState()
        {
            TaskHelper.StartupRunForTest previousRun = TaskHelper.RunForTest;
            using (var strip = new System.Windows.Forms.ContextMenuStrip())
            try
            {
                object tray = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(TrayMenu));
                object game = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(GameMode));
                BindingFlags instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                FieldInfo stripField = typeof(TrayMenu).GetField("strip", instance);
                FieldInfo gameField = typeof(TrayMenu).GetField("gameMode", instance);
                MethodInfo rebuild = typeof(TrayMenu).GetMethod("Rebuild", instance);
                Check(stripField != null && gameField != null && rebuild != null, "The hidden actual tray rebuild fixture was unavailable");
                stripField.SetValue(tray, strip); gameField.SetValue(tray, game);
                string data = Root("tray-rebuild-data");
                string enabledXml = TaskHelper.BuildStartupTaskXml(Path.Combine(output, "tray-rebuild-existing.exe"), TaskHelper.BuildStartupArguments(data));
                string disabledXml = enabledXml.Replace("<Settings>", "<Settings><Enabled>false</Enabled>");
                string taskXml = enabledXml; int queries = 0;
                TaskHelper.RunForTest = delegate(string command, bool capture, out string stdout)
                {
                    Check(capture && Array.IndexOf(ParseArguments(command), "/Query") >= 0,
                        "Tray rebuild attempted a mutation or non-XML scheduler operation");
                    queries++; stdout = taskXml; return 0;
                };
                Check(!strip.Visible && !strip.IsHandleCreated, "The tray rebuild fixture created or showed a native window");
                Check(StartupEnabled(false) && StartupEnabled(true), "The initial enabled cache fixture was not populated");
                taskXml = disabledXml; int snapshot = queries;
                rebuild.Invoke(tray, null);
                Check(queries == snapshot + 1, "Opening the tray menu did not freshly read startup task XML");
                Check(strip.Items.Count == 4 && !((System.Windows.Forms.ToolStripMenuItem)strip.Items[1]).Checked,
                    "An externally disabled task remained checked when rebuilding the tray menu");
                Check(!strip.Visible && !strip.IsHandleCreated, "Rebuilding the disabled tray menu created or showed a window");
                taskXml = enabledXml; snapshot = queries;
                rebuild.Invoke(tray, null);
                Check(queries == snapshot + 1 && ((System.Windows.Forms.ToolStripMenuItem)strip.Items[1]).Checked,
                    "An externally enabled task did not become checked when rebuilding the tray menu");
                Check(!strip.Visible && !strip.IsHandleCreated, "Rebuilding the enabled tray menu created or showed a window");
            }
            finally { TaskHelper.RunForTest = previousRun; }
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CommandLineToArgvW(string command, out int count);
        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr block);
        private static string[] ParseArguments(string arguments)
        {
            int count; IntPtr block = CommandLineToArgvW("fixture.exe " + arguments, out count);
            if (block == IntPtr.Zero) throw new InvalidOperationException("Windows argument parser unavailable");
            try
            {
                string[] words = new string[count - 1];
                for (int i = 1; i < count; i++) words[i - 1] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(block, i * IntPtr.Size));
                return words;
            }
            finally { LocalFree(block); }
        }
    }
    internal static partial class SelfTests
    {
        public static bool TryHandleRuntimeMode(string[] args)
        { throw new InvalidOperationException("Autostart checks cannot start the application runtime"); }
    }
}
#endif
