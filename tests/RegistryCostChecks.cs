#if PAVISE_SELFTEST && PAVISE_REGISTRY_COST_CHECKS
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;

namespace PaviseApp
{
    // 对局中的两处注册表问题：电源计划通知审计每秒的落盘记录，以及并发写入时的撕裂读。
    // 设置部分在本进程隔离的 HKCU 上运行；电源计划部分使用临时设置库和模拟的电源接口，不改动本机。
    // 读取不经缓存：其他 Pavise 进程（LoL 看门狗、崩溃后接手的进程）写下的值必须马上读到。
    internal static partial class SelfTests
    {
        private static int checks;
        private const BindingFlags AnyStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly IntPtr CurrentUser = new IntPtr(unchecked((int)0x80000001));
        [DllImport("advapi32.dll")] private static extern int RegOverridePredefKey(IntPtr predefined, IntPtr replacement);

        private static void Check(bool value, string reason) { checks++; if (!value) throw new InvalidOperationException("FAIL " + reason); }
        public static bool TryHandleRuntimeMode(string[] args) { throw new InvalidOperationException("The registry cost checks cannot start the product runtime"); }

        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length == 1 && args[0] == "--negative-control") { Console.WriteLine("FAIL: intentional registry cost negative control"); return 3; }
            try
            {
                Logger.WritesEnabled = false; Lang.Init();
                string timing = InLabRegistry<string>(SettingsReadChecks);
                InLabRegistry<bool>(ExecutionRecordChecks);
                PowerPlanAuditChecks();
                Console.WriteLine("PASS registry cost checks=" + checks + " " + timing);
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }

        // ---- 设置读取（隔离注册表） ----

        private static RegistryKey RawKey() { return Registry.CurrentUser.CreateSubKey(Settings.Key); }

        // HKCU 在 body 期间重定向到新建的实验键：Settings 走真实注册表，但不碰用户的设置。
        private static T InLabRegistry<T>(Func<T> body)
        {
            string lab = @"Software\PaviseRegistryCostLab\" + Guid.NewGuid().ToString("N");
            RegistryKey root = Registry.CurrentUser.CreateSubKey(lab);
            Check(root != null && RegOverridePredefKey(CurrentUser, root.Handle.DangerousGetHandle()) == 0, "the test registry could not be isolated");
            try
            {
                using (RegistryKey probe = Registry.CurrentUser.CreateSubKey("PaviseLabSentinel")) { }
                Check(root.OpenSubKey("PaviseLabSentinel") != null, "HKCU is not redirected to the lab key");
                return body();
            }
            finally
            {
                RegOverridePredefKey(CurrentUser, IntPtr.Zero);
                Registry.CurrentUser.DeleteSubKey("PaviseLabSentinel", false);  // 只在重定向失败时才会落到真实 HKCU
                root.Dispose();
                Registry.CurrentUser.DeleteSubKeyTree(lab, false);
                using (RegistryKey parent = Registry.CurrentUser.OpenSubKey(@"Software\PaviseRegistryCostLab", true))
                    if (parent != null && parent.SubKeyCount == 0 && parent.ValueCount == 0) Registry.CurrentUser.DeleteSubKey(@"Software\PaviseRegistryCostLab", false);
                Settings.NoteExternalMutation();
            }
        }

        private static string SettingsReadChecks()
        {
            Check(Settings.LoadStr("LabA", "def") == "def", "an absent value did not return its default");
            Check(Settings.SaveStr("LabA", "1") && Settings.LoadStr("LabA", "def") == "1", "a write was not visible to the next read");
            Check(Settings.SaveStr("LabA", "2") && Settings.LoadStr("LabA", "def") == "2", "an old value survived a write");
            string present; Check(Settings.TryLoadStr("LabA", out present) && present == "2", "the strict read missed a written value");
            Check(Settings.TryLoadStr("LabAbsent", out present) && present == "", "an absent strict read was not reported as empty");

            int generation = Settings.MutationGeneration;
            Check(Settings.Save("LabB", true) && Settings.MutationGeneration == generation + 1, "one write no longer advances the generation by exactly one");
            bool stored;
            Check(Settings.Load("LabB", false) && Settings.TryLoadStoredBool("LabB", out stored) && stored, "a bool write was not visible");
            Check(Settings.TryLoadBool("LabB", false, out stored) && stored && Settings.LoadCached("LabB", false), "a strict or cached bool read missed a write");
            Check(Settings.SaveDurable("LabB", false) && !Settings.Load("LabB", true), "a durable bool write was not visible");
            Settings.Remove("LabA");
            Check(Settings.LoadStr("LabA", "gone") == "gone" && Settings.TryLoadStr("LabA", out present) && present == "", "a removed value was still served");
            Check(Settings.SaveStrDurable("LabA", "3") && Settings.LoadStr("LabA", "") == "3", "a durable write was not visible");

            // 另一个进程（或注册表编辑器）写下的值，下一次读取就要读到。
            Check(Settings.LoadStr("LabA", "") == "3", "the value before the outside edit was not visible");
            using (RegistryKey raw = RawKey()) raw.SetValue("LabA", "external");
            Check(Settings.LoadStr("LabA", "") == "external", "an outside edit was not read at once");
            bool outside; using (RegistryKey raw = RawKey()) raw.SetValue("LabOutside", 1, RegistryValueKind.DWord);
            Check(Settings.TryLoadStoredBool("LabOutside", out outside) && outside, "an outside bool edit was not read at once");
            using (RegistryKey raw = RawKey()) raw.SetValue("LabA", "external-2");
            Settings.NoteExternalMutation();
            Check(Settings.LoadStr("LabA", "") == "external-2", "an announced external edit was not read at once");
            using (RegistryKey raw = RawKey()) raw.SetValue("LabB", 1, RegistryValueKind.DWord);
            Settings.NoteExternalMutation();
            Check(Settings.Load("LabB", false), "an announced external bool edit was not read at once");

            // GPU 身份收据的写入不推进代数，读取必须始终直达注册表。
            string session = "Pavise.GpuIdentity.42." + Guid.NewGuid().ToString("N");
            generation = Settings.MutationGeneration;
            Check(Settings.SaveGpuIdentityReceipt(session) && Settings.TryLoadStr(Settings.GpuIdentityReceiptKey, out present) && present == session,
                "the GPU identity receipt did not read back");
            Check(Settings.MutationGeneration == generation, "the GPU identity receipt advanced the preference generation");
            using (RegistryKey raw = RawKey()) raw.SetValue(Settings.GpuIdentityReceiptKey, "");
            Check(Settings.TryLoadStr(Settings.GpuIdentityReceiptKey, out present) && present == "", "an outside receipt change was not read at once");
            Check(Settings.SaveGpuIdentityReceipt("") && Settings.TryLoadStr(Settings.GpuIdentityReceiptKey, out present) && present == "", "clearing the GPU identity receipt failed");

            // 整键删除后（卸载路径）不再返回旧值。
            Settings.SaveStr("LabA", "before-delete");
            Check(Settings.LoadStr("LabA", "") == "before-delete", "the value before deletion was not visible");
            Registry.CurrentUser.DeleteSubKeyTree(Settings.Key, false);
            Settings.NoteExternalMutation();
            Check(Settings.LoadStr("LabA", "none") == "none", "a deleted key still served its old value");

            // 并发写入时单个读者看到的值不能倒退，也不能读到撕裂的值。
            Settings.SaveStr("LabC", "0");
            const int writes = 1500;
            int regressions = 0, finalMismatches = 0;
            var regressionDetail = new System.Text.StringBuilder();
            bool writerDone = false;
            Exception failure = null;
            var readers = new Thread[3];
            for (int r = 0; r < readers.Length; r++)
            {
                readers[r] = new Thread(delegate()
                {
                    try
                    {
                        int last = -1;
                        while (true)
                        {
                            bool done = Volatile.Read(ref writerDone);
                            int seen;
                            string text = Settings.LoadStr("LabC", "-1");
                            // 撕裂读（含 NUL）解析失败，也算倒退。
                            if (!int.TryParse(text.Split('|')[0], out seen) || text.IndexOf('\0') >= 0) seen = -2;
                            if (seen < last)
                            {
                                Interlocked.Increment(ref regressions);
                                lock (regressionDetail) if (regressionDetail.Length < 400) regressionDetail.Append(" ").Append(last).Append("->").Append(text);
                            }
                            last = Math.Max(last, seen);
                            if (done) { if (seen != writes) Interlocked.Increment(ref finalMismatches); return; }
                        }
                    }
                    catch (Exception error) { failure = error; }
                });
                readers[r].IsBackground = true; readers[r].Start();
            }
            var writer = new Thread(delegate()
            {
                try
                {
                    // 长短交替：每次写入都改变值的长度，正好落在 GetValue 两次查询之间时就会撕裂。
                    for (int i = 1; i <= writes; i++)
                    {
                        string value = i + "|" + new string('p', (i % 2) * 300);
                        if (!(i % 7 == 0 ? Settings.SaveStrDurable("LabC", value) : Settings.SaveStr("LabC", value)))
                            throw new InvalidOperationException("write " + i + " failed");
                    }
                }
                catch (Exception error) { failure = error; }
                finally { Volatile.Write(ref writerDone, true); }
            });
            writer.Start(); writer.Join();
            foreach (Thread reader in readers) Check(reader.Join(10000), "a concurrent reader did not finish");
            Check(failure == null, "the concurrent read/write run failed: " + failure);
            Check(regressions == 0, "a reader saw an older value after a newer one (" + regressions + " times:" + regressionDetail + ")");
            Check(finalMismatches == 0, "a reader missed the final write after it completed");

            // 计时：一次设置读取（打开、查询、关闭注册表）的开销。
            Settings.SaveStr("LabTiming", "x");
            const int reads = 20000;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < reads; i++) Settings.LoadStr("LabTiming", "");
            double readUs = sw.Elapsed.TotalMilliseconds * 1000.0 / reads;
            return "settings_read_us=" + readUs.ToString("F2");
        }

        // ---- 执行记录（隔离注册表） ----
        // 后台压制每纳入一个新进程就写一条执行意图：不刷盘，完成后删除；Pavise 中途崩溃时意图仍在，下次启动照样隔离。
        // 其他动作的意图照旧落盘。旧版本清除时留下的空值，启动时删掉。

        private static int StoredCount(string prefix)
        { string[] names; Check(Settings.TryLoadStoredNames(prefix, out names), "stored names unreadable: " + prefix); return names.Length; }

        private static bool ExecutionRecordChecks()
        {
            ActionFailureStore.ReloadForTest();
            int flushes = 0;
            Settings.BeforeFlushForTest = delegate { flushes++; };
            try
            {
                string reason, background = SuppressionCore.FailureAction, noise = @"C:\Lab\noise.exe";
                Check(ActionFailureStore.TryBeginApply(background, noise, out reason), "background suppression was not admitted: " + reason);
                Check(StoredCount(ActionFailureStore.AttemptPrefix) == 1, "the background execution intent was not written");
                Check(ActionFailureStore.CompleteApply(background, noise), "background suppression did not complete");
                Check(flushes == 0, "background suppression flushed the hive " + flushes + " times");
                Check(StoredCount(ActionFailureStore.AttemptPrefix) == 0, "a completed background intent left a value behind");
                for (int i = 0; i < 20; i++)
                    Check(ActionFailureStore.TryBeginApply(background, noise, out reason) && ActionFailureStore.CompleteApply(background, noise),
                        "a repeated background apply failed: " + reason);
                Check(flushes == 0 && StoredCount(ActionFailureStore.AttemptPrefix) == 0,
                    "repeated background applies flushed " + flushes + " times or left intents behind");

                Check(ActionFailureStore.TryBeginApply("LabDurable", "system", out reason) && flushes == 1, "a durable intent was not flushed (" + flushes + ")");
                Check(ActionFailureStore.CompleteApply("LabDurable", "system") && flushes == 2, "clearing a durable intent was not flushed (" + flushes + ")");
                Check(StoredCount(ActionFailureStore.AttemptPrefix) == 0, "a completed durable intent left a value behind");

                // Pavise 在压制中途崩溃（重载即新进程）。
                Check(ActionFailureStore.TryBeginApply(background, noise, out reason), "the crash fixture was not admitted: " + reason);
                ActionFailureStore.ReloadForTest(); flushes = 0;
                Check(ActionFailureStore.IsBlocked(background, noise), "an unfinished background intent was not isolated after a restart");
                Check(flushes == 1, "the recovered failure record was flushed " + flushes + " times instead of once");
                Check(StoredCount(ActionFailureStore.AttemptPrefix) == 0, "the recovered intent was not cleared");
                Check(StoredCount(ActionFailureStore.Prefix) == 1, "the recovered failure record is missing");

                using (RegistryKey raw = RawKey())
                {
                    raw.SetValue(ActionFailureStore.AttemptPrefix + new string('a', 64), "");
                    raw.SetValue(ActionFailureStore.Prefix + new string('b', 64), "");
                    raw.SetValue("LabUnrelated", "");
                }
                Settings.NoteExternalMutation();
                ActionFailureStore.ReloadForTest();
                Check(!ActionFailureStore.IsBlocked("LabOther", "system"), "an unrelated action was blocked");
                Check(StoredCount(ActionFailureStore.AttemptPrefix) == 0, "an empty legacy intent survived startup");
                Check(StoredCount(ActionFailureStore.Prefix) == 1, "the startup sweep kept an empty failure record or removed a real one");
                Check(ActionFailureStore.IsBlocked(background, noise), "the startup sweep released an isolated action");
                using (RegistryKey raw = RawKey()) Check(raw.GetValue("LabUnrelated") != null, "the startup sweep removed an unrelated value");
                return true;
            }
            finally
            {
                Settings.BeforeFlushForTest = null;
                ActionFailureStore.ReloadForTest();
            }
        }

        // ---- 电源计划通知审计（临时设置库，模拟电源接口） ----

        private static void SetPlan(string field, object value) { typeof(PowerPlan).GetField(field, AnyStatic).SetValue(null, value); }
        private static void SetMode(GameMode mode, string field, object value) { typeof(GameMode).GetField(field, AnyInstance).SetValue(mode, value); }
        private static void Audit(GameMode mode, int key) { typeof(GameMode).GetMethod("RunPowerPlanNotificationAudit", AnyInstance).Invoke(mode, new object[] { 0, key }); }

        private static void PowerPlanAuditChecks()
        {
            Settings.UseTransientStoreForCurrentProcess(); ActionFailureStore.ReloadForTest(); PowerPlan.ResetPlanRestoreForTest();
            int attempts = 0;
            // 写入意图走字符串写入，清除走删除。
            Action<string> countAttempt = delegate(string name) { if (name.StartsWith("ActionAttempt_", StringComparison.Ordinal)) attempts++; };
            Settings.BeforeStringWriteForTest = countAttempt; Settings.BeforeValueWriteForTest = countAttempt;
            try
            {
                Guid target = PowerPlan.BalancedGuid, other = new Guid("11111111-2222-4333-8444-555555555555");
                Guid current = target; int switches = 0;
                PowerPlan.ActivateTargetPlanForTest = delegate { return target; };
                PowerPlan.RestoreCurrentPlanForTest = delegate { return current; };
                PowerPlan.RestoreSetPlanForTest = delegate(Guid plan) { switches++; current = plan; return true; };
                Check(PowerPlan.SelectPlan(target.ToString()), "the fixture plan could not be selected");
                SetPlan("active", true); SetPlan("targetOwned", false);

                var mode = new GameMode(Path.Combine(Path.GetTempPath(), "PaviseRegistryCost-" + Guid.NewGuid().ToString("N")), new SuppressionCore());
                SetMode(mode, "active", true); SetMode(mode, "enabled", true); SetMode(mode, "planActive", true);
                const int key = 3;  // 应用计划 + 处理器调校
                SetMode(mode, "lastPowerPolicyKey", key);

                Check(PowerPlan.EnforceSettled(true), "an in-place plan was not settled");
                Audit(mode, key);
                Check(attempts == 0 && switches == 0, "a settled audit wrote execution records (" + attempts + ") or switched plans (" + switches + ")");
                for (int i = 0; i < 30; i++) Audit(mode, key);
                Check(attempts == 0 && switches == 0, "thirty settled audits wrote " + attempts + " execution records");

                // 别处改走了计划：完整路径写执行记录并改回。
                current = other;
                Check(!PowerPlan.EnforceSettled(true), "a drifted plan was reported settled");
                Audit(mode, key);
                Check(current == target && switches == 1, "the audit did not switch the drifted plan back");
                Check(attempts == 2, "the corrective audit wrote " + attempts + " execution records instead of begin and completion");
                Check(!ActionFailureStore.IsBlocked(PolicyCatalog.KeyPowerPlan, "system"), "the corrective audit isolated the power plan");
                Audit(mode, key);
                Check(attempts == 2 && switches == 1, "the audit after the correction was not read-only");

                // 只读判定的其余拒绝条件。
                Settings.SaveStr(PowerPlan.ExtremeSnapKey, "pending");
                Check(!PowerPlan.EnforceSettled(true), "an idle-policy residue was reported settled");
                Settings.SaveStr(PowerPlan.ExtremeSnapKey, "");
                Check(PowerPlan.EnforceSettled(true), "a cleared idle-policy residue still blocked the read-only audit");
                SetPlan("targetOwned", true); SetPlan("saved", other); SetPlan("tuneState", 1);
                Check(PowerPlan.EnforceSettled(true), "a managed plan with matching tuning was not settled");
                Check(!PowerPlan.EnforceSettled(false), "a managed plan with different tuning was reported settled");
                SetPlan("saved", Guid.Empty);
                Check(!PowerPlan.EnforceSettled(true), "a managed plan without its return plan was reported settled");
                SetPlan("saved", other); SetPlan("targetOwned", false);
                PowerPlan.ActivateTargetPlanForTest = delegate { return Guid.Empty; };
                Check(!PowerPlan.EnforceSettled(true), "an unresolved target was reported settled");
                PowerPlan.ActivateTargetPlanForTest = delegate { return target; };
                PowerPlan.RestoreCurrentPlanForTest = delegate { return null; };
                Check(!PowerPlan.EnforceSettled(true), "an unreadable active plan was reported settled");
                PowerPlan.RestoreCurrentPlanForTest = delegate { return current; };
                SetPlan("active", false);
                Check(!PowerPlan.EnforceSettled(true), "an inactive plan was reported settled");
                SetPlan("active", true);
                Check(PowerPlan.SelectPlan(PowerPlan.NoSwitchChoice) && !PowerPlan.EnforceSettled(true), "the no-switch choice was reported settled");
            }
            finally
            {
                Settings.BeforeStringWriteForTest = null; Settings.BeforeValueWriteForTest = null;
                PowerPlan.ResetPlanRestoreForTest();
                Settings.UseTransientStoreForCurrentProcess(); ActionFailureStore.ReloadForTest();
            }
        }
    }
}
#endif
