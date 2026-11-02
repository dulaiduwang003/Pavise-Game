#if PAVISE_SELFTEST && PAVISE_CLEANUP_END_TO_END
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static class CleanupEndToEndRunner
    {
        private static string output;
        private static int assertions, suites, failed;
        private static int Main(string[] args)
        {
            if (args.Length == 2 && args[0] == "--native-only") return NativeMain(args[1]);
            if (args.Length != 1) return 2;
            output = Path.GetFullPath(args[0]);
            Directory.CreateDirectory(output);
            Settings.UseTransientStoreForCurrentProcess();
            Logger.WritesEnabled = false; Lang.Init();
            using (var watchdog = new Timer(delegate { Environment.Exit(124); }, null, 30000, Timeout.Infinite))
            {
                Run("clear-all restores actual receipts before deleting owned portable data", ResetSuccess);
                Run("uninstall verifies existing task removal before actual restoration and deletion", UninstallSuccess);
                Run("absent startup task still permits actual uninstall without a delete command", UninstallAbsentTask);
                Run("failed restore preserves files and the exact remaining receipt until retry", ResetPartialFailure);
                Run("uninstall restoration failure retains records after task removal", UninstallRestoreFailure);
                Run("unknown or still-present task prevents uninstall restoration and deletion", UninstallTaskFailures);
                Run("failed runtime stop prevents reset and uninstall side effects", StopFailures);
                Run("locked owned data prevents registry deletion after confirmed restoration", LockedData);
                Run("registry failure reports incomplete cleanup and seals late writers", RegistryFailure);
                Run("unknown action receipt blocks clear-all without querying a native backend", UnknownAction);
                Console.WriteLine("RESULT suites=" + suites + " failed=" + failed + " assertions=" + assertions
                    + " settings=transient backend=mocked task_scheduler=mocked runtime_started=false native_writes=0");
                return failed == 0 ? 0 : 1;
            }
        }
        private static int NativeMain(string resultDirectory)
        {
            output = Path.GetFullPath(resultDirectory); Directory.CreateDirectory(output);
            var originalOutput = Console.Out; var originalError = Console.Error;
            using (var results = new StreamWriter(Path.Combine(output, "native-result.txt"), false, new UTF8Encoding(false)))
            {
                results.AutoFlush = true; Console.SetOut(results); Console.SetError(results);
                try
                {
                    using (var identity = WindowsIdentity.GetCurrent())
                        Check(new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator),
                            "Native fixture requires an administrator process; it does not elevate itself");
                    Settings.UseTransientStoreForCurrentProcess(); Logger.WritesEnabled = false; Lang.Init();
                    for (int iteration = 1; iteration <= 2; iteration++)
                    {
                        int captured = iteration;
                        Run("native clear-all round " + iteration + " restores managed flags and preserves unmanaged flags", delegate { NativeCleanup(false, captured); });
                        Run("native uninstall round " + iteration + " restores managed flags and preserves unmanaged flags", delegate { NativeCleanup(true, captured); });
                    }
                    Console.WriteLine("RESULT native_suites=" + suites + " failed=" + failed + " assertions=" + assertions
                        + " settings=transient task_scheduler=mocked runtime_started=false native_targets=owned-guid-only");
                    return failed == 0 ? 0 : 1;
                }
                catch (Exception error) { Console.Error.WriteLine("FAIL native fixture setup: " + error); return 2; }
                finally { Console.SetOut(originalOutput); Console.SetError(originalError); }
            }
        }
        private static void Run(string name, Action test)
        {
            suites++;
            try { test(); Console.WriteLine("PASS " + name); }
            catch (Exception error) { failed++; Console.Error.WriteLine("FAIL " + name + ": " + error); }
        }
        private static void Check(bool good, string message)
        { assertions++; if (!good) throw new InvalidOperationException(message); }

        private static void NativeCleanup(bool uninstall, int iteration)
        {
            using (var n = new NativeFixture())
            {
                Fixture f = n.Files;
                n.SeedUnmanaged();
                string before = n.ReadManaged(), unmanagedBefore = n.ReadUnmanaged();
                Dictionary<string, string> rawBefore = n.ReadRaw(), persistedBefore = n.ReadPersistentValues();
                Check(unmanagedBefore.Contains("\"StackPivot\":\"ON\"") && unmanagedBefore.Contains("\"Relocate\":\"ON\""),
                    "The unrelated Payload/ASLR seed did not take effect");
                Check(before.Split(';').Length == 6, "Native baseline did not return all managed mitigation components");
                Check(PerformanceActionBackend.Apply(PerformanceActionCatalog.Mitigations, n.Target, "off64"),
                    "Native apply/readback did not commit an off64 receipt");
                Check(PerformanceActionBackend.MitigationsOff(n.ReadManaged(), false), "Native apply did not read back the managed flags as OFF");
                CheckRawPreserved(rawBefore, n.ReadRaw(), true);
                CheckPersistentUnmanaged(persistedBefore, n.ReadPersistentValues());
                var receipts = PerformanceActionStore.All();
                Check(receipts.Count == 1 && receipts[0].Target == n.Target && receipts[0].Original == before && receipts[0].Committed,
                    "Native apply did not retain the exact original baseline receipt");
                Check(n.ReadUnmanaged() == unmanagedBefore, "Applying the managed mitigation set damaged unrelated Payload/ASLR flags");

                int files; string failure;
                if (uninstall)
                {
                    bool present = true;
                    UninstallReport report = UninstallMode.Execute(f.Data, f.Data, f.Stop(true), delegate
                    {
                        return TaskHelper.RemoveStartupTaskForUninstall(delegate { return (bool?)present; },
                            delegate { present = false; return 0; });
                    });
                    Check(report.Cleared && report.Failure == null && report.Files == f.Owned.Count && !present,
                        "Native uninstall failed its restoration/delete chain: " + report.Failure);
                }
                else Check(Program.TryResetUserData(f.Data, f.Stop(true), out files, out failure) && files == f.Owned.Count,
                    "Native clear-all failed its restoration/delete chain: " + failure);
                string after = n.ReadManaged(), unmanagedAfter = n.ReadUnmanaged();
                Check(after == before, "Native restoration changed the original managed vector: before=" + before + " after=" + after);
                Check(unmanagedAfter == unmanagedBefore, "Native restoration changed the unrelated Payload/ASLR baseline");
                CheckRawPreserved(rawBefore, n.ReadRaw(), false);
                CheckDictionaryEqual(persistedBefore, n.ReadPersistentValues(), "Native restoration changed persisted IFEO values");
                Check(PerformanceActionStore.All().Count == 0 && f.RestoreCalls == 1 && f.RegistryDeletes == 1,
                    "Native restoration did not clear its confirmed receipt before deleting data/settings");
                f.CheckPreservedAndDeleted(); f.CheckLateWritesSealed();

                int reads = n.NativeReads, writes = n.NativeWrites;
                if (uninstall)
                {
                    int deletes = 0;
                    UninstallReport repeated = UninstallMode.Execute(f.Data, f.Data, f.Stop(true), delegate
                    {
                        return TaskHelper.RemoveStartupTaskForUninstall(delegate { return false; }, delegate { deletes++; return 0; });
                    });
                    Check(repeated.Cleared && repeated.Files == 0 && repeated.Failure == null && deletes == 0,
                        "Repeating an already completed native uninstall was not idempotent");
                }
                else Check(Program.TryResetUserData(f.Data, f.Stop(true), out files, out failure) && files == 0 && failure == null,
                    "Repeating already completed native clear-all was not idempotent");
                Check(n.NativeReads == reads && n.NativeWrites == writes && PerformanceActionStore.All().Count == 0,
                    "Repeated cleanup re-applied or re-restored a deleted native receipt");
                f.CheckPreservedAndDeleted();
                Console.WriteLine("NATIVE " + (uninstall ? "uninstall" : "clear-all") + " round=" + iteration
                    + " target=" + n.Target + " original=" + before + " restored=" + after
                    + " unmanaged_unchanged=true read_calls=" + n.NativeReads + " write_calls=" + n.NativeWrites);
            }
        }

        private static void CheckRawPreserved(Dictionary<string, string> before, Dictionary<string, string> after, bool applying)
        {
            Check(before.Count == after.Count && before.ContainsKey("CFG.ControlFlowGuard") && before.ContainsKey("Payload.ModuleList"),
                "Native raw read omitted policy fields");
            var managed = new HashSet<string>(StringComparer.Ordinal) { "CFG.ControlFlowGuard", "Payload.EnableExportAddressFilter",
                "Payload.EnableExportAddressFilterPlus", "Payload.EnableImportAddressFilter", "Payload.EnableRopCallerCheck" };
            foreach (var entry in before)
            {
                string actual; Check(after.TryGetValue(entry.Key, out actual), "Native raw field disappeared: " + entry.Key);
                ulong originalBits, actualBits;
                if (applying && managed.Contains(entry.Key))
                {
                    Check(ulong.TryParse(entry.Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out originalBits)
                        && ulong.TryParse(actual, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out actualBits), "Invalid raw policy bits: " + entry.Key);
                    actualBits = ulong.Parse(actual, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    Check((originalBits & ~3UL) == (actualBits & ~3UL), "Native apply changed unowned raw bits: " + entry.Key);
                }
                else Check(entry.Value == actual, "Native mitigation changed a raw field: " + entry.Key + " before=" + entry.Value + " after=" + actual);
            }
        }
        private static void CheckDictionaryEqual(Dictionary<string, string> before, Dictionary<string, string> after, string message)
        {
            Check(before.Count == after.Count, message + " (count)");
            foreach (var entry in before) { string actual; Check(after.TryGetValue(entry.Key, out actual) && actual == entry.Value, message + ": " + entry.Key); }
        }
        private static void CheckPersistentUnmanaged(Dictionary<string, string> before, Dictionary<string, string> after)
        {
            Check(before.Count == after.Count, "Native apply changed the seeded persisted value count");
            foreach (var entry in before)
            {
                string actual; Check(after.TryGetValue(entry.Key, out actual), "Native apply removed a persisted value: " + entry.Key);
                if (entry.Key.EndsWith("|MitigationOptions", StringComparison.Ordinal))
                {
                    Check(entry.Value.StartsWith("Binary:", StringComparison.Ordinal) && actual.StartsWith("Binary:", StringComparison.Ordinal),
                        "Native mitigation options lost their binary type");
                    byte[] oldBytes = Convert.FromBase64String(entry.Value.Substring(7)), newBytes = Convert.FromBase64String(actual.Substring(7));
                    Check(oldBytes.Length == newBytes.Length && (oldBytes.Length == 16 || oldBytes.Length == 24),
                        "Native mitigation options changed their supported length");
                    for (int i = 0; i < oldBytes.Length; i++)
                    {
                        int mask = i == 5 || i == 12 || i == 13 ? 3 : i == 10 || i == 11 ? 0x30 : 0;
                        Check((oldBytes[i] & ~mask) == (newBytes[i] & ~mask),
                            "Native apply changed unowned persisted bits at byte " + i);
                    }
                }
                else Check(actual == entry.Value, "Native apply changed an unrelated persisted value: " + entry.Key);
            }
        }

        private static void ResetSuccess()
        {
            using (var f = new Fixture())
            {
                string target = f.Seed("historical-work.exe");
                int files; string failure; int progress = 0;
                Check(Program.TryResetUserData(f.Data, f.Stop(true), out files, out failure,
                    delegate { progress++; throw new InvalidOperationException("Detached progress observer"); }),
                    "Actual clear-all failed: " + failure);
                Check(failure == null && files == f.Owned.Count && progress > 0, "Clear-all result/count was inaccurate");
                Check(f.RestoreCalls == 1 && f.RegistryDeletes == 1 && f.Value(target) == Fixture.Original,
                    "Clear-all did not restore the original mitigation snapshot before deleting settings");
                Check(PerformanceActionStore.All().Count == 0, "Clear-all left a restored receipt");
                f.CheckPreservedAndDeleted();
                Check(f.Events[0] == "stop" && f.Events[1] == "restore" && f.Events[f.Events.Count - 1] == "registry-delete",
                    "Clear-all stage ordering changed");
                f.CheckLateWritesSealed();
            }
        }
        private static void UninstallSuccess()
        {
            using (var f = new Fixture())
            {
                string target = f.Seed("League of Legends.exe"); bool present = true;
                UninstallReport report = UninstallMode.Execute(f.Data, f.Data, f.Stop(true), delegate
                {
                    return TaskHelper.RemoveStartupTaskForUninstall(
                        delegate { f.Events.Add("task-query"); return (bool?)present; },
                        delegate { f.Events.Add("task-remove"); present = false; return 5; });
                });
                Check(report.Cleared && report.Failure == null && report.Files == f.Owned.Count && report.Leftovers == 0,
                    "Actual uninstall did not confirm its complete owned-data cleanup");
                Check(!present && f.Value(target) == Fixture.Original && PerformanceActionStore.All().Count == 0,
                    "Uninstall failed to verify task absence or restore its receipt");
                Check(string.Join(",", f.Events.ToArray()) == "stop,task-query,task-remove,task-query,restore,registry-delete",
                    "Uninstall did not remove/verify the task before restoration and data cleanup");
                f.CheckPreservedAndDeleted(); f.CheckLateWritesSealed();
            }
        }
        private static void UninstallAbsentTask()
        {
            using (var f = new Fixture())
            {
                string target = f.Seed("left4dead2.exe"); int queries = 0, deletes = 0;
                UninstallReport report = UninstallMode.Execute(f.Data, f.Data, f.Stop(true), delegate
                {
                    return TaskHelper.RemoveStartupTaskForUninstall(
                        delegate { queries++; return false; }, delegate { deletes++; return 0; });
                });
                Check(report.Cleared && queries == 1 && deletes == 0, "Absent task was deleted or prevented uninstall");
                Check(f.Value(target) == Fixture.Original && f.RestoreCalls == 1 && f.RegistryDeletes == 1,
                    "Absent-task uninstall skipped the real restore/delete chain");
                f.CheckPreservedAndDeleted();
            }
        }
        private static void ResetPartialFailure()
        {
            using (var f = new Fixture())
            {
                string good = f.Seed("success.exe"), bad = f.Seed("failed.exe"); f.FailTarget = bad;
                int files; string failure;
                Check(!Program.TryResetUserData(f.Data, f.Stop(true), out files, out failure), "Failed restore reported successful clear-all");
                Check(files == 0 && !string.IsNullOrEmpty(failure) && failure.Contains(bad) && f.RegistryDeletes == 0,
                    "Failed target was hidden or settings deletion was admitted");
                Check(f.Value(good) == Fixture.Original && f.Value(bad) == Fixture.Applied,
                    "Partial restore lost the successful restoration or overwrote the failed target");
                var receipts = PerformanceActionStore.All();
                Check(receipts.Count == 1 && receipts[0].Target == bad && receipts[0].Original == Fixture.Original,
                    "The exact failed receipt/original value was not preserved");
                f.CheckOwnedPresent();
                Check(Settings.SaveStr("post-failure", "retry-allowed"), "Failed restoration incorrectly sealed retry settings");
                f.FailTarget = null;
                Check(Program.TryResetUserData(f.Data, f.Stop(true), out files, out failure), "Retry could not consume the preserved receipt");
                Check(f.Value(bad) == Fixture.Original && PerformanceActionStore.All().Count == 0 && f.RegistryDeletes == 1,
                    "Retry failed to restore and clear only its remaining receipt");
                f.CheckPreservedAndDeleted();
            }
        }
        private static void UninstallRestoreFailure()
        {
            using (var f = new Fixture())
            {
                string target = f.Seed("failed-uninstall.exe"); f.FailTarget = target; bool present = true;
                UninstallReport report = UninstallMode.Execute(f.Data, f.Data, f.Stop(true), delegate
                {
                    return TaskHelper.RemoveStartupTaskForUninstall(delegate { return (bool?)present; },
                        delegate { present = false; return 0; });
                });
                Check(!report.Cleared && report.Files == 0 && report.Failure.Contains(target), "Uninstall hid its incomplete restore");
                Check(!present && f.RestoreCalls == 1 && f.RegistryDeletes == 0 && PerformanceActionStore.All().Count == 1,
                    "Failed uninstall deleted settings or forgot its recovery state after task removal");
                f.CheckOwnedPresent();
            }
        }
        private static void UninstallTaskFailures()
        {
            for (int scenario = 0; scenario < 3; scenario++)
            using (var f = new Fixture())
            {
                string target = f.Seed("task-blocked.exe"); int captured = scenario, queries = 0, deletes = 0;
                UninstallReport report = UninstallMode.Execute(f.Data, f.Data, f.Stop(true), delegate
                {
                    return TaskHelper.RemoveStartupTaskForUninstall(delegate
                    {
                        queries++; if (captured == 2) throw new IOException("Scheduler unavailable");
                        return captured == 0 ? (bool?)null : true;
                    }, delegate { deletes++; return 0; });
                });
                Check(!report.Cleared && !string.IsNullOrEmpty(report.Failure) && report.Files == 0,
                    "Unknown or still-present task was reported removed");
                Check(f.RestoreCalls == 0 && f.RegistryDeletes == 0 && f.Value(target) == Fixture.Applied
                    && PerformanceActionStore.All().Count == 1, "Task-removal failure reached the restoration/wipe stage");
                Check(queries == (scenario == 1 ? 2 : 1) && deletes == (scenario == 1 ? 1 : 0),
                    "Task removal did not re-query actual absence or attempted deletion after unknown presence");
                f.CheckOwnedPresent();
            }
        }
        private static void StopFailures()
        {
            for (int scenario = 0; scenario < 3; scenario++)
            using (var f = new Fixture())
            {
                string target = f.Seed("stop-blocked.exe"); int taskCalls = 0;
                Func<bool> stop = scenario == 0 ? null : scenario == 1 ? f.Stop(false)
                    : (Func<bool>)delegate { throw new IOException("Shutdown denied"); };
                int files; string failure;
                Check(!Program.TryResetUserData(f.Data, stop, out files, out failure) && files == 0 && !string.IsNullOrEmpty(failure),
                    "Reset admitted a missing, failed or throwing runtime stop");
                UninstallReport report = UninstallMode.Execute(f.Data, f.Data, stop, delegate { taskCalls++; return true; });
                Check(!report.Cleared && !string.IsNullOrEmpty(report.Failure), "Uninstall admitted a missing, failed or throwing stop");
                Check(taskCalls == 0 && f.RestoreCalls == 0 && f.RegistryDeletes == 0 && f.Value(target) == Fixture.Applied,
                    "Failed stop reached scheduler, restoration or deletion");
                Check(PerformanceActionStore.All().Count == 1, "Failed stop consumed its recovery receipt");
                f.CheckOwnedPresent();
            }
        }
        private static void LockedData()
        {
            using (var f = new Fixture())
            {
                string target = f.Seed("locked-files.exe");
                int files; string failure;
                using (var locked = new FileStream(f.Owned[0], FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    Check(!Program.TryResetUserData(f.Data, f.Stop(true), out files, out failure), "Locked owned data reported fully deleted");
                    Check(!string.IsNullOrEmpty(failure) && File.Exists(f.Owned[0]) && f.RegistryDeletes == 0,
                        "Locked-data failure erased settings or omitted its error");
                }
                Check(f.Value(target) == Fixture.Original && PerformanceActionStore.All().Count == 0,
                    "File deletion failure undid a confirmed original restoration");
                Check(Settings.LoadStr("fixture-marker", "") == "keep-until-delete", "Settings disappeared after a file deletion failure");
                Check(!Settings.SaveStr("late", "write"), "Failed file cleanup allowed a late writer to repopulate already deleted data");
                f.CheckForeignPreserved();
            }
        }
        private static void RegistryFailure()
        {
            using (var f = new Fixture())
            {
                string target = f.Seed("registry-failure.exe"); f.RegistrySuccess = false;
                int files; string failure;
                Check(!Program.TryResetUserData(f.Data, f.Stop(true), out files, out failure), "Failed registry deletion reported successful reset");
                Check(files == f.Owned.Count && !string.IsNullOrEmpty(failure) && f.RegistryDeletes == 1,
                    "Registry failure misreported completed file cleanup");
                Check(f.Value(target) == Fixture.Original && PerformanceActionStore.All().Count == 0,
                    "Registry cleanup failure discarded an unfinished original restoration");
                Check(Settings.LoadStr("fixture-marker", "") == "keep-until-delete", "Failed registry delete destroyed the isolated settings");
                f.CheckPreservedAndDeleted(); f.CheckLateWritesSealed();
            }
        }
        private static void UnknownAction()
        {
            using (var f = new Fixture())
            {
                string target = Path.Combine(f.Data, "unknown-receipt.exe"); string current = "original";
                Check(PerformanceActionStore.Change("UnknownFutureAction", target, "owned", delegate { return current; },
                    delegate(string value) { current = value; return true; }, delegate(string value) { return value == "owned"; }),
                    "Could not seed an actual unknown-action receipt");
                int backendCalls = 0;
                PerformanceActionBackend.ReadForTest = delegate { backendCalls++; throw new InvalidOperationException("Unknown action read"); };
                PerformanceActionBackend.WriteForTest = delegate { backendCalls++; throw new InvalidOperationException("Unknown action write"); };
                int files; string failure;
                Check(!Program.TryResetUserData(f.Data, f.Stop(true), out files, out failure), "Unknown action receipt was erased as restored");
                var receipts = PerformanceActionStore.All();
                Check(backendCalls == 0 && files == 0 && f.RegistryDeletes == 0 && !string.IsNullOrEmpty(failure)
                    && failure.Contains("UnknownFutureAction"), "Unknown action queried a backend or admitted deletion without an explicit failure");
                Check(receipts.Count == 1 && receipts[0].Original == "original" && receipts[0].Target == target && current == "owned",
                    "Unknown action failed to preserve its exact original receipt and system state");
                f.CheckOwnedPresent();
            }
        }

        private sealed class NativeFixture : IDisposable
        {
            private const string IfeoRoot = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";
            private const string Prefix = "PaviseCleanupNative-";
            internal readonly Fixture Files;
            internal readonly string Target;
            private readonly string leaf;
            private bool owned;
            internal int NativeReads, NativeWrites;
            private const string RawSource = @"
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
public static class PaviseCleanupRawReader
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    private const string Prefix = ""Microsoft.Samples.PowerShell.Commands.Ntdll"";
    private static void Export(Assembly module, string target, string typeName, string kindName, uint flags, string prefix, Dictionary<string,string> output)
    {
        Type native = module.GetType(Prefix, true), type = module.GetType(Prefix + ""+"" + typeName, true);
        Type kind = module.GetType(Prefix + ""+IMAGE_MITIGATION_POLICY"", true);
        object buffer = Activator.CreateInstance(type); MethodInfo query = null;
        foreach (MethodInfo method in native.GetMethods(Flags))
            if (method.Name == ""QueryImageMitigationPolicy"" && method.IsGenericMethodDefinition
                && method.GetGenericArguments().Length == 1 && method.GetParameters().Length == 4) query = method.MakeGenericMethod(type);
        if (query == null) throw new NotSupportedException(""Native query interface unavailable"");
        FieldInfo modules = type.GetField(""ModuleList"");
        if (modules != null)
        {
            var marshal = (MarshalAsAttribute)Attribute.GetCustomAttribute(modules, typeof(MarshalAsAttribute));
            if (marshal == null || marshal.SizeConst != 512) throw new NotSupportedException(""Unsupported module list"");
            modules.SetValue(buffer, new char[marshal.SizeConst]);
        }
        object[] args = { target, Enum.Parse(kind, kindName), flags, buffer };
        int status = (int)query.Invoke(null, args);
        if (status != 0 && status != unchecked((int)0xc0000034)) throw new InvalidOperationException(""Native query failed: "" + status);
        if (status == 0) buffer = args[3];
        foreach (FieldInfo field in type.GetFields())
        {
            object value = field.GetValue(buffer);
            if (field.FieldType == typeof(char[])) output[prefix + field.Name] = Convert.ToBase64String(Encoding.Unicode.GetBytes(new string((char[])value)));
            else
            {
                FieldInfo state = value.GetType().GetField(""PolicyState"");
                if (state == null || state.FieldType != typeof(ulong)) throw new NotSupportedException(""Unexpected policy field"");
                output[prefix + field.Name] = ((ulong)state.GetValue(value)).ToString(""X16"");
            }
        }
    }
    public static Dictionary<string,string> Read(Assembly module, string target)
    {
        var values = new Dictionary<string,string>(StringComparer.Ordinal);
        Export(module, target, ""RTL_IMAGE_MITIGATION_CONTROL_FLOW_GUARD_POLICY"", ""ImageControlFlowGuardPolicy"", 0, ""CFG."", values);
        Export(module, target, ""RTL_IMAGE_MITIGATION_PAYLOAD_RESTRICTION_POLICY"", ""ImagePayloadRestrictionPolicy"", 0, ""Payload."", values);
        Export(module, target, ""RTL_IMAGE_MITIGATION_PAYLOAD_RESTRICTION_POLICY"", ""ImagePayloadRestrictionPolicy"", 8, ""AuditPayload."", values);
        Export(module, target, ""RTL_IMAGE_MITIGATION_ASLR_POLICY"", ""ImageAslrPolicy"", 0, ""ASLR."", values);
        return values;
    }
}";
            internal NativeFixture()
            {
                Files = new Fixture();
                leaf = Prefix + Guid.NewGuid().ToString("N") + ".exe";
                Target = Path.Combine(Files.Data, leaf);
                try
                {
                    using (var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                    using (var existing = hive.OpenSubKey(IfeoRoot + "\\" + leaf))
                        Check(existing == null, "A pre-existing native fixture key must never be owned or removed");
                    owned = true;
                    File.WriteAllText(Target, "foreign sentinel"); Files.Foreign.Add(Target);
                    PerformanceActionBackend.ReadForTest = delegate(string id, string target)
                    {
                        CheckTarget(id, target); NativeReads++;
                        return Execute(PerformanceActionBackend.MitigationQuery(target));
                    };
                    PerformanceActionBackend.WriteForTest = delegate(string id, string target, string value)
                    {
                        CheckTarget(id, target); NativeWrites++;
                        Execute(PerformanceActionBackend.MitigationWrite(target, value)); return true;
                    };
                }
                catch { Dispose(); throw; }
            }
            private void CheckTarget(string id, string target)
            {
                Guid parsed;
                if (id != PerformanceActionCatalog.Mitigations || !string.Equals(target, Target, StringComparison.Ordinal)
                    || !owned || !leaf.StartsWith(Prefix, StringComparison.Ordinal) || !leaf.EndsWith(".exe", StringComparison.Ordinal)
                    || !Guid.TryParseExact(leaf.Substring(Prefix.Length, leaf.Length - Prefix.Length - 4), "N", out parsed))
                    throw new InvalidOperationException("Native cleanup fixture rejected a non-owned target");
            }
            private static string Execute(string script)
            {
                string result, failure;
                if (!PsRunner.RunWithFailure("$ErrorActionPreference='Stop'; " + script, "Owned cleanup native fixture", 15000, out result, out failure))
                    throw new InvalidOperationException(failure);
                return result.Trim();
            }
            internal void SeedUnmanaged()
            {
                CheckTarget(PerformanceActionCatalog.Mitigations, Target);
                Execute("Import-Module ProcessMitigations; Set-ProcessMitigation -Name " + PerformanceActionBackend.Quote(Target)
                    + " -Enable StrictCFG,EnableRopStackPivot,ForceRelocateImages -Force on -ErrorAction Stop;"
                    + " Set-ProcessMitigation -Name " + PerformanceActionBackend.Quote(Target)
                    + " -Enable CFG,EnableExportAddressFilter -Force on -ErrorAction Stop;"
                    + " Set-ProcessMitigation -Name " + PerformanceActionBackend.Quote(Target)
                    + " -Enable AuditEnableExportAddressFilter -ErrorAction Stop");
                using (var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var image = hive.OpenSubKey(IfeoRoot + "\\" + leaf, true))
                {
                    Check(image != null, "Native fixture did not create its own image mitigation filter");
                    image.SetValue("PaviseCleanupNativeSentinel", Encoding.UTF8.GetBytes(leaf), RegistryValueKind.Binary);
                    int matchingFilters = 0;
                    foreach (string child in image.GetSubKeyNames())
                        using (var filter = image.OpenSubKey(child, true))
                        {
                            if (filter == null || !string.Equals(filter.GetValue("FilterFullPath") as string, Target, StringComparison.OrdinalIgnoreCase)) continue;
                            matchingFilters++;
                            byte[] bits = filter.GetValue("MitigationOptions") as byte[];
                            Check(bits != null && bits.Length == 24, "Unexpected owned mitigation fixture layout");
                            bits[5] |= 8; bits[23] |= 64;
                            filter.SetValue("MitigationOptions", bits, RegistryValueKind.Binary);
                        }
                    Check(matchingFilters == 1, "Native fixture expected exactly one matching full-path filter");
                }
            }
            internal string ReadManaged()
            { return PerformanceActionBackend.Read(PerformanceActionCatalog.Mitigations, Target); }
            internal string ReadUnmanaged()
            {
                CheckTarget(PerformanceActionCatalog.Mitigations, Target);
                return Execute("Import-Module ProcessMitigations; $q=Get-ProcessMitigation -Name " + PerformanceActionBackend.Quote(Target)
                    + "; if($null-eq$q){throw 'Missing seeded mitigation state'}; [ordered]@{ASLR=$q.ASLR;"
                    + "Relocate=$q.ASLR.ForceRelocateImages.ToString();StackPivot=$q.Payload.EnableRopStackPivot.ToString();"
                    + "StackPivotOverride=$q.Payload.OverrideEnableRopStackPivot.ToString()} | ConvertTo-Json -Depth 6 -Compress");
            }
            internal Dictionary<string, string> ReadRaw()
            {
                CheckTarget(PerformanceActionCatalog.Mitigations, Target);
                string source = Convert.ToBase64String(Encoding.UTF8.GetBytes(RawSource));
                string result = Execute("Import-Module ProcessMitigations; Add-Type -TypeDefinition ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('"
                    + source + "'))) -ErrorAction Stop; $a=(Get-Command Set-ProcessMitigation).ImplementingType.Assembly;"
                    + " $r=[PaviseCleanupRawReader]::Read($a," + PerformanceActionBackend.Quote(Target)
                    + "); foreach($k in ($r.Keys | Sort-Object)){Write-Output ($k+'='+$r[$k])}");
                var values = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (string line in result.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    int separator = line.IndexOf('=');
                    if (separator <= 0) throw new FormatException("Unexpected raw mitigation output");
                    values.Add(line.Substring(0, separator), line.Substring(separator + 1));
                }
                return values;
            }
            internal Dictionary<string, string> ReadPersistentValues()
            {
                CheckTarget(PerformanceActionCatalog.Mitigations, Target);
                var values = new Dictionary<string, string>(StringComparer.Ordinal);
                using (var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var image = hive.OpenSubKey(IfeoRoot + "\\" + leaf, false))
                {
                    Check(image != null, "Owned fixture mitigation filter disappeared during the test");
                    ExportValues(image, "", values);
                }
                return values;
            }
            private static void ExportValues(RegistryKey key, string prefix, Dictionary<string, string> values)
            {
                foreach (string name in key.GetValueNames())
                {
                    object value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                    string encoded = value is byte[] ? Convert.ToBase64String((byte[])value)
                        : value is string[] ? Convert.ToBase64String(Encoding.Unicode.GetBytes(string.Join("\0", (string[])value)))
                        : Convert.ToString(value, CultureInfo.InvariantCulture);
                    values.Add(prefix + "|" + name, key.GetValueKind(name) + ":" + encoded);
                }
                foreach (string child in key.GetSubKeyNames())
                    using (var nested = key.OpenSubKey(child, false)) ExportValues(nested, prefix + "/" + child, values);
            }
            public void Dispose()
            {
                try
                {
                    if (owned)
                    {
                        CheckTarget(PerformanceActionCatalog.Mitigations, Target);
                        using (var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                        using (var parent = hive.OpenSubKey(IfeoRoot, true))
                        {
                            if (parent == null) throw new IOException("Cannot verify/remove the owned native fixture key");
                            parent.DeleteSubKeyTree(leaf, false);
                            using (var remaining = parent.OpenSubKey(leaf)) Check(remaining == null, "Native fixture IFEO key survived its finally cleanup");
                        }
                        owned = false;
                        Console.WriteLine("NATIVE_FINALLY removed_owned_key=" + leaf);
                    }
                }
                finally { Files.Dispose(); }
            }
        }

        private sealed class Fixture : IDisposable
        {
            internal const string Original = "ON:True;NOTSET:False;OFF:True;ON:False;NOTSET:False;ON:True";
            internal const string Applied = "OFF:False;OFF:False;OFF:False;OFF:False;OFF:False;OFF:False";
            internal readonly string Data;
            internal readonly List<string> Owned = new List<string>(), Foreign = new List<string>(), Events = new List<string>();
            private readonly Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            internal int RestoreCalls, RegistryDeletes;
            internal bool RegistrySuccess = true;
            internal string FailTarget;
            internal Fixture()
            {
                Settings.UseTransientStoreForCurrentProcess(); PerformanceTuning.ResetForTest();
                Logger.ResetWriteBarrierForTest(); Logger.WritesEnabled = false;
                Data = Path.Combine(output, "case-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Data);
                string temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
                Check(Path.GetFullPath(Data).StartsWith(temp, StringComparison.OrdinalIgnoreCase), "Cleanup fixture escaped the system temporary directory");
                foreach (string name in new[] { GameProfileStore.FileName, "Pavise.autoignore.txt", "Pavise.log",
                    "Pavise.whitelist.txt.tmp", GameProfileStore.FileName + "." + Guid.NewGuid().ToString("N") + ".tmp" })
                { string file = Path.Combine(Data, name); File.WriteAllText(file, "owned inert data"); Owned.Add(file); }
                foreach (string name in new[] { "Pavise.exe", "user-save.sav", "Pavise.manual.xml", GameProfileStore.FileName + ".not-a-guid.tmp" })
                { string file = Path.Combine(Data, name); File.WriteAllText(file, "foreign sentinel"); Foreign.Add(file); }
                string nested = Path.Combine(Data, "UserFolder"); Directory.CreateDirectory(nested);
                string nestedFile = Path.Combine(nested, "Pavise.games.txt"); File.WriteAllText(nestedFile, "foreign sentinel"); Foreign.Add(nestedFile);
                string external = Path.Combine(output, "outside-" + Guid.NewGuid().ToString("N") + ".sentinel");
                File.WriteAllText(external, "foreign sentinel"); Foreign.Add(external);
                Logger.LogPath = Path.Combine(Data, "Pavise.log");
                Check(Settings.SaveStr("fixture-marker", "keep-until-delete"), "Could not seed transient settings");
                PerformanceActionBackend.ReadForTest = delegate(string id, string target)
                { if (id != PerformanceActionCatalog.Mitigations) throw new InvalidOperationException("Unexpected native read"); return values[target]; };
                PerformanceActionBackend.WriteForTest = delegate(string id, string target, string next)
                {
                    if (id != PerformanceActionCatalog.Mitigations) throw new InvalidOperationException("Unexpected native write");
                    if (next != "off32" && target == FailTarget) return false;
                    values[target] = next == "off32" ? Applied : next; return true;
                };
                LegacyPurge.RestoreHook = delegate { RestoreCalls++; Events.Add("restore"); return PerformanceTuning.RestoreAllFailures(); };
                LegacyPurge.DeleteRegistryHook = delegate
                {
                    RegistryDeletes++; Events.Add("registry-delete");
                    Check(PerformanceActionStore.All().Count == 0, "Settings deletion raced an unfinished receipt");
                    foreach (string file in Owned) Check(!File.Exists(file), "Settings deletion ran before an owned file was removed");
                    return RegistrySuccess;
                };
            }
            internal string Seed(string leaf)
            {
                string target = Path.Combine(Data, leaf); values[target] = Original;
                Check(PerformanceActionBackend.Apply(PerformanceActionCatalog.Mitigations, target, "off32"), "Could not seed actual component restoration receipt");
                Check(values[target] == Applied && PerformanceActionStore.All().Count > 0, "Actual receipt was not committed");
                return target;
            }
            internal string Value(string target) { return values[target]; }
            internal Func<bool> Stop(bool result) { return delegate { Events.Add("stop"); return result; }; }
            internal void CheckOwnedPresent() { foreach (string file in Owned) Check(File.Exists(file), "Owned file was deleted before restoration was confirmed: " + file); CheckForeignPreserved(); }
            internal void CheckForeignPreserved()
            { foreach (string file in Foreign) Check(File.Exists(file) && File.ReadAllText(file) == "foreign sentinel", "Portable cleanup damaged an unrelated file: " + file); }
            internal void CheckPreservedAndDeleted() { foreach (string file in Owned) Check(!File.Exists(file), "Owned file survived successful cleanup: " + file); CheckForeignPreserved(); }
            internal void CheckLateWritesSealed()
            {
                bool wrote = true; Exception problem = null;
                using (var done = new ManualResetEvent(false))
                {
                    var delayed = new Thread(delegate()
                    {
                        try { wrote = Settings.SaveStr("delayed-callback", "should-be-rejected"); Logger.WritesEnabled = true; Logger.Log("late callback"); Logger.Clear(); }
                        catch (Exception error) { problem = error; }
                        finally { done.Set(); }
                    });
                    delayed.IsBackground = true; delayed.Start();
                    Check(done.WaitOne(1500), "Bounded delayed callback did not finish");
                    Check(delayed.Join(1000) && problem == null && !wrote, "Late callback wrote settings after the deletion barrier");
                }
                Check(Settings.LoadStr("delayed-callback", "absent") == "absent" && !File.Exists(Logger.LogPath),
                    "Late log or settings callback recreated cleared data");
            }
            public void Dispose()
            {
                LegacyPurge.RestoreHook = null; LegacyPurge.DeleteRegistryHook = null; LegacyPurge.SkipRegistryDelete = false;
                PerformanceActionBackend.ReadForTest = null; PerformanceActionBackend.WriteForTest = null;
                PerformanceTuning.ResetForTest(); Settings.UseTransientStoreForCurrentProcess();
                Logger.WritesEnabled = false; Logger.LogPath = null;
            }
        }
    }
    internal static partial class SelfTests
    {
        public static bool TryHandleRuntimeMode(string[] args)
        { throw new InvalidOperationException("Cleanup fixtures cannot start optimization runtime"); }
    }
}
#endif
