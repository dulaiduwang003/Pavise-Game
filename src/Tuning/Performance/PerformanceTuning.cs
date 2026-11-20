using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace PaviseApp
{
    internal static partial class PerformanceTuning
    {
        internal const string EnvironmentBundleKey = "EnvPerformanceBundleV1";

        internal const string EnvironmentVbsKey = "EnvVbsDisabledV1";

        private static readonly object gate = new object();

        private static readonly Dictionary<string, int> recoveryFailures = new Dictionary<string, int>(StringComparer.Ordinal);
        private static long recoverySessionSerial;
        internal static long RecoveryCycleSerial { get { return Interlocked.Read(ref recoverySessionSerial); } }

        internal static void BeginRecoveryCycle(long gameSessionSerial)
        {
            lock (SystemConfigurationGate.Sync)
            {
                // 旧会话迟到的工作不能为新会话补充预算
                if (gameSessionSerial > recoverySessionSerial)
                {
                    Interlocked.Exchange(ref recoverySessionSerial, gameSessionSerial);
                    PrivacyControl.BeginRecoveryCycle(gameSessionSerial);
                }
            }
        }

        internal static bool RestoreWithBudget(PerformanceActionStore.Receipt receipt, int maximumFailures, Func<bool> restore)
        {
            if (receipt == null || restore == null) return false;
            lock (SystemConfigurationGate.Sync)
            {
                // 有意排除可变的诊断信息和提交标志
                string key = recoverySessionSerial + "|" + RecoveryPart(receipt.Id) + RecoveryPart(receipt.Target)
                    + RecoveryPart(receipt.Original) + RecoveryPart(receipt.Expected) + RecoveryPart(receipt.VisualOwner);
                UserRestoreScope user = UserRestoreScope.Current;
                if (user != null) return RestoreForUser(user, key, receipt, restore);
                int failures;
                recoveryFailures.TryGetValue(key, out failures);
                Func<bool> phaseChanged = delegate
                {
                    lock (SystemConfigurationGate.Sync)
                    {
                        int remaining;
                        return !key.StartsWith(recoverySessionSerial + "|", StringComparison.Ordinal)
                            || !recoveryFailures.TryGetValue(key, out remaining) || remaining < maximumFailures;
                    }
                };
                if (failures >= maximumFailures) { RecoveryAttemptTrace.Exhausted(phaseChanged); return false; }
                bool restored = false;
                try { restored = restore(); return restored; }
                finally
                {
                    if (restored) recoveryFailures.Remove(key);
                    else
                    {
                        recoveryFailures[key] = ++failures;
                        if (failures >= maximumFailures) RecoveryAttemptTrace.Exhausted(phaseChanged);
                        else RecoveryAttemptTrace.DeferredUntil(DateTime.UtcNow.Ticks);
                        Logger.Warn(Logger.Format("性能恢复：动作={0} 目标={1} 阶段={2} 尝试={3}/{4}{5}", "Performance recovery: action={0} target={1} phase={2} attempt={3}/{4}{5}", receipt.Id, receipt.Target, recoverySessionSerial, failures, maximumFailures, (failures >= maximumFailures ? Logger.Text("；已停止主动重试，保留回执", "; active retries stopped; receipt retained") : Logger.Text("；保留回执", "; receipt retained"))));
                    }
                }
            }
        }

        // 用户亲手关开关 重置或卸载时的恢复算用户操作 不算自动重试
        // 每次操作对同一条回执只写一次 失败不占自动恢复预算 所以预算用完以后用户还能自己恢复
        private sealed class UserRestoreScope : IDisposable
        {
            [ThreadStatic] internal static UserRestoreScope Current;
            internal readonly HashSet<string> Failed = new HashSet<string>(StringComparer.Ordinal);
            internal UserRestoreScope() { Current = this; }
            public void Dispose() { Current = null; }
        }

        // 外层已经是一次用户操作就沿用它 嵌套进来的步骤不另算一次
        internal static IDisposable BeginUserRestore()
        {
            return UserRestoreScope.Current != null ? null : new UserRestoreScope();
        }

        private static bool RestoreForUser(UserRestoreScope user, string key, PerformanceActionStore.Receipt receipt, Func<bool> restore)
        {
            // 这次操作里已经失败过的回执直接跳过 一次点击里不反复写
            if (user.Failed.Contains(key)) return false;
            bool restored = false;
            try { restored = restore(); return restored; }
            finally
            {
                // 用户恢复成功说明故障已经消失 这条回执的自动失败计数清零 之后再失败从头算
                // 每次清零都要用户亲手操作一次 不会自己转起来 失败时不清
                if (restored) recoveryFailures.Remove(key);
                if (!restored)
                {
                    user.Failed.Add(key);
                    Logger.Warn(Logger.Format("性能恢复：动作={0} 目标={1} 阶段={2} 用户操作恢复未完成；保留回执",
                        "Performance recovery: action={0} target={1} phase={2} user restore incomplete; receipt retained",
                        receipt.Id, receipt.Target, recoverySessionSerial));
                }
            }
        }

        private static string RecoveryPart(string value)
        {
            return (value == null ? -1 : value.Length) + ":" + value;
        }

        internal static bool RestorePerformanceReceipt(PerformanceActionStore.Receipt receipt, Func<bool> prepare = null)
        {
            bool restored = RestoreWithBudget(receipt, 5, () => (prepare == null || prepare()) && PerformanceActionBackend.Restore(receipt));
            if (restored) ActionFailureStore.CancelApply(receipt.Id, receipt.Target);
            return restored;
        }

        // 执行意图悬空（上次写入途中进程退出） 但恢复记录里没有该目标 本方总是先落恢复记录再写配置
        // 没有记录就说明没有本方写入需要恢复 按恢复完成结清意图 与按记录恢复后的结清走同一路径
        // 不补写 不改配置 也不退还失败次数 恢复记录读不到时不结清 继续保留隔离
        internal static bool SettleIntentWithoutReceipt(string id, string target)
        {
            List<PerformanceActionStore.Receipt> receipts;
            try
            {
                if (!ReadRecoveryReceipts(out receipts)) return false;
            }
            catch { return false; }
            foreach (PerformanceActionStore.Receipt receipt in receipts)
                if (receipt.Id == id && string.Equals(receipt.Target, target, StringComparison.OrdinalIgnoreCase)) return false;
            ActionFailureStore.CancelApply(id, target);
            return !ActionFailureStore.IsBlocked(id, target);
        }

        internal static bool ReadRecoveryReceipts(out List<PerformanceActionStore.Receipt> receipts)
        {
            List<PerformanceActionStore.Receipt> read = null;
            bool ok = RestoreWithBudget(new PerformanceActionStore.Receipt { Id = "recovery-journal", Target = "performance" }, 5,
                delegate { read = PerformanceActionStore.All(); return true; });
            receipts = read;
            return ok;
        }

        private static readonly HashSet<string> busy = new HashSet<string>();

        private static readonly Dictionary<string, string> states = new Dictionary<string, string>();

        internal static volatile bool EnvironmentWorkClosing;

#if PAVISE_SELFTEST
        internal static Func<bool> CpuMitigationsSupportedForTest;
        internal static Func<WaitCallback, bool> EnvironmentQueueForTest;
        internal static Func<string> BootForTest;
#endif
#if PAVISE_SELFTEST
        internal static Func<bool> VbsConfiguredForTest;
        internal static Func<bool, bool> VbsChangeForTest;
        internal static void ResetForTest()
        { lock (gate) { if (busy.Count != 0) throw new InvalidOperationException("A performance action is still running"); states.Clear(); EnvironmentWorkClosing = false; EnvironmentQueueForTest = null; } }
#endif

        internal static string EnvironmentBundleTitle
        {
            get
            {
                return PerformancePolicy.Text("系统增强 · 需重启", "System enhancement · restart required");
            }
        }

        internal static string EnvironmentBundleDetail
        {
            get
            {
                string detail = PerformancePolicy.Text("关闭 VBS（含内存完整性和 hypervisor）和适用的 CPU 软件缓解，并调整音频设备的交流电源管理。启用时按处理器和系统选择缓解范围。会降低安全防护并影响 WSL2、Hyper-V。关闭后恢复原配置，启用或恢复后需重启电脑。",
                    "Disables VBS (including Memory Integrity and the hypervisor) and applicable CPU software mitigations, and adjusts audio device power management on AC. The CPU and Windows version determine the mitigation scope when enabled. This reduces protection and affects WSL2 and Hyper-V. Turning it off restores the original settings. Restart after enabling or restoring.");
                if (EnvironmentPreference(PerformanceActionCatalog.CpuMitigations))
                {
                    detail += CpuMitigationsExpanded
                        ? PerformancePolicy.Text("\nCPU 缓解已选范围：扩展", "\nSelected CPU mitigation scope: expanded")
                        : PerformancePolicy.Text("\nCPU 缓解已选范围：基础", "\nSelected CPU mitigation scope: basic");
                }
                return detail + EnvironmentBundleIssues();
            }
        }

        internal static bool EnvironmentBundleEnabled
        {
            get
            {
                return EnvironmentEnabledCount() > 0;
            }
        }

        internal static string Boot()
        {
#if PAVISE_SELFTEST
            if (BootForTest != null) return BootForTest();
#endif
            return BootSessionIdentity.Read();
        }

        internal static bool Busy(string id)
        {
            lock (gate)
            {
                return busy.Contains(id);
            }
        }

        internal static string State(string id)
        {
            lock (gate)
            {
                if (busy.Contains(id))
                {
                    return "working";
                }
                string value;
                if (!states.TryGetValue(id, out value))
                {
                    value = Settings.LoadStr(id + "State", "");
                }
                bool live = value == "live-applied" || value == "live-restored";
                if (value != "configured" && value != "restored" && !live)
                {
                    return value;
                }
                object result;
                switch (BootSessionIdentity.Compare(Settings.LoadStr(id + "Boot", ""), Boot()))
                {
                default:
                    result = live ? value : ((value == "configured") ? "pending-reboot" : "restore-pending-reboot");
                    break;
                case IrqRebootState.Rebooted:
                    result = ((value == "configured" || value == "live-applied") ? "ready" : "restored");
                    break;
                case IrqRebootState.Unknown:
                    result = "unverified";
                    break;
                }
                return (string)result;
            }
        }

        internal static bool SetEnvironment(string id, bool on, Action completed)
        {
            PerformanceAction action = PerformanceActionCatalog.Find(id);
            if (action == null || !action.Environment || (on && PerformanceActionCatalog.IsRetired(id)))
            {
                return false;
            }
            lock (gate)
            {
                if (EnvironmentWorkClosing || busy.Contains(id) || busy.Contains("EnvPerformanceBundleV1") || !Settings.Save(id, on))
                {
                    return false;
                }
                busy.Add(id);
            }
            bool setEnvironmentSucceeded = false;
            try
            {
                setEnvironmentSucceeded = QueueEnvironmentWork(delegate
                {
                    try
                    {
                        // 用户关开关 这一次恢复按用户操作算
                        using (on ? null : BeginUserRestore()) ApplyEnvironment(id, on);
                    }
                    catch (Exception ex2)
                    {
                        SetState(id, on ? "failed" : "restore-failed");
                        Logger.Warn(action.Title + " | " + ex2.Message);
                    }
                    finally
                    {
                        lock (gate)
                        {
                            busy.Remove(id);
                            Monitor.PulseAll(gate);
                        }
                        if (completed != null)
                        {
                            try
                            {
                                completed();
                            }
                            catch (Exception ex3)
                            {
                                Logger.Warn(Logger.Format("系统环境完成 | {0}", "Environment completion | {0}", ex3.Message));
                            }
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                Logger.Warn(action.Title + " | " + ex.Message);
            }
            if (!setEnvironmentSucceeded)
            {
                lock (gate)
                {
                    busy.Remove(id);
                    Monitor.PulseAll(gate);
                }
                SetState(id, on ? "failed" : "restore-failed");
            }
            return setEnvironmentSucceeded;
        }

        internal static bool CpuMitigationsExpanded
        {
            get { return Settings.LoadCached(CpuMitigationPolicy.ExpandedPreferenceKey, false); }
        }

        internal static bool CpuMitigationsExpansionSupported()
        {
#if PAVISE_SELFTEST
            if (CpuMitigationsSupportedForTest != null) return CpuMitigationsSupportedForTest();
#endif
            try
            {
                using (Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    "HARDWARE\\DESCRIPTION\\System\\CentralProcessor\\0"))
                    return key != null && CpuMitigationPolicy.Supports(Native.OsBuild(), key.GetValue("VendorIdentifier") as string);
            }
            catch { return false; }
        }

        internal static void SetState(string id, string state)
        {
            lock (gate)
            {
                states[id] = state;
            }
            Settings.SaveStr(id + "State", state);
        }

        internal static string EnvironmentAutomaticCycle(string id)
        {
            return RecoveryCycleSerial + "\n" + id;
        }

        private static string VerifiedEnvironmentState(string id, bool on)
        {
            string value = Settings.LoadStr(id + "State", "");
            if (id == PerformanceActionCatalog.Quantum && (on ? value == "live-applied" : value == "live-restored"))
                return value;
            if (!on)
            {
                if (value != "restored")
                {
                    return "off";
                }
                return value;
            }
            if (value == "configured")
            {
                return value;
            }
            foreach (PerformanceActionStore.Receipt item in PerformanceActionStore.All())
            {
                if (item.Id == id)
                {
                    return "configured";
                }
            }
            return "external";
        }

        private static bool ApplyCpuMitigationsGroup(string[] targets)
        {
            if (CpuMitigationsExpanded && !CpuMitigationsExpansionSupported())
            {
                SetState(PerformanceActionCatalog.CpuMitigations, "unsupported");
                return false;
            }
            Predicate<PerformanceActionStore.Receipt> selected = (PerformanceActionStore.Receipt receipt) => receipt.Id == "EnvCpuMitigationsV1";
            ActionFailureStore.BeginAutomaticCycle("EnvCpuMitigationsV1", "system", RecoveryCycleSerial + "\nexpanded:" + CpuMitigationsExpanded);
            if (ActionFailureStore.IsIsolated("EnvCpuMitigationsV1", "system") && !RestoreWhere(selected))
            {
                ActionFailureStore.Trip("EnvCpuMitigationsV1", "system", "CPU mitigation recovery incomplete before retry");
                SetState("EnvCpuMitigationsV1", "restore-failed");
                return false;
            }
            ActionFailureStore.CancelApply("EnvCpuMitigationsV1", "system");
            ActionFailureStore.BeginAutomaticCycle("EnvCpuMitigationsV1", "system", RecoveryCycleSerial + "\nexpanded:" + CpuMitigationsExpanded);
            string reason;
            if (!ActionFailureStore.TryBeginApply("EnvCpuMitigationsV1", "system", out reason))
            {
                SetState("EnvCpuMitigationsV1", "stopped");
                return false;
            }
            bool settingsChanged = false;
            try
            {
                foreach (string entryText in targets)
                {
                    if (EnvironmentWorkClosing)
                    {
                        throw new OperationCanceledException();
                    }
                    string readResult = PerformanceActionBackend.Read("EnvCpuMitigationsV1", entryText);
                    string wantedRegistryResult = PerformanceActionBackend.WantedRegistry("EnvCpuMitigationsV1", entryText, readResult);
                    if (readResult != wantedRegistryResult)
                    {
                        string bootResult = Boot();
                        if (!BootSessionIdentity.Valid(bootResult))
                        {
                            throw new OperationCanceledException();
                        }
                        if (!Settings.SaveStrDurable("EnvCpuMitigationsV1Boot", bootResult))
                        {
                            throw new InvalidOperationException("CPU mitigation boot journal persistence failed");
                        }
                        settingsChanged = true;
                    }
                    if (!PerformanceActionBackend.Apply("EnvCpuMitigationsV1", entryText, wantedRegistryResult))
                    {
                        throw new InvalidOperationException(entryText + " | " + PerformanceActionBackend.ApplyFailureHint("EnvCpuMitigationsV1", entryText));
                    }
                }
                if (!ActionFailureStore.CompleteApply("EnvCpuMitigationsV1", "system"))
                {
                    bool restoreWhereSucceeded = RestoreWhere(selected);
                    SetState("EnvCpuMitigationsV1", restoreWhereSucceeded ? "stopped" : "restore-failed");
                    return false;
                }
                SetState("EnvCpuMitigationsV1", settingsChanged ? "configured" : VerifiedEnvironmentState("EnvCpuMitigationsV1", true));
                return true;
            }
            catch (NotSupportedException)
            {
                bool restoreWhereSucceeded2 = RestoreWhere(selected);
                if (restoreWhereSucceeded2)
                {
                    ActionFailureStore.CancelApply("EnvCpuMitigationsV1", "system");
                }
                else
                {
                    ActionFailureStore.Trip("EnvCpuMitigationsV1", "system", "CPU mitigation recovery incomplete after unsupported apply");
                }
                SetState("EnvCpuMitigationsV1", restoreWhereSucceeded2 ? "unsupported" : "restore-failed");
                return false;
            }
            catch (OperationCanceledException)
            {
                bool restoreWhereSucceeded3 = RestoreWhere(selected);
                if (restoreWhereSucceeded3)
                {
                    ActionFailureStore.CancelApply("EnvCpuMitigationsV1", "system");
                }
                else
                {
                    ActionFailureStore.Trip("EnvCpuMitigationsV1", "system", "CPU mitigation recovery incomplete after cancelled apply");
                }
                SetState("EnvCpuMitigationsV1", restoreWhereSucceeded3 ? "unverified" : "restore-failed");
                return false;
            }
            catch (Exception ex3)
            {
                ActionFailureStore.Trip("EnvCpuMitigationsV1", "system", ex3.Message);
                bool restoreWhereSucceeded4 = RestoreWhere(selected);
                SetState("EnvCpuMitigationsV1", restoreWhereSucceeded4 ? "stopped" : "restore-failed");
                Logger.Warn("EnvCpuMitigationsV1 | " + ex3.Message);
                return false;
            }
        }

        internal static bool RestoreWhere(Predicate<PerformanceActionStore.Receipt> selected)
        {
            return RestoreFailures(selected).Count == 0;
        }

        private static List<string> RestoreFailures(Predicate<PerformanceActionStore.Receipt> selected, Action<string> progress = null)
        {
            List<string> list = new List<string>();
            List<PerformanceActionStore.Receipt> list2;
            try
            {
                if (!ReadRecoveryReceipts(out list2)) { list.Add("Recovery journal read budget exhausted; records retained"); return list; }
            }
            catch (Exception ex)
            {
                string normalizeErrorOffset = PerformancePolicy.Text("性能配置恢复记录读取失败", "Performance recovery journal could not be read") + " | " + PsRunner.NormalizeError(ex.Message);
                list.Add(normalizeErrorOffset);
                Logger.Warn(normalizeErrorOffset);
                return list;
            }
            foreach (PerformanceActionStore.Receipt item in list2)
            {
                if (!selected(item))
                {
                    continue;
                }
                // 已删除功能的记录直接丢弃 系统里的值保持现状
                if (PerformanceActionStore.IsRemoved(item.Id))
                {
                    if (!PerformanceActionStore.Discard(item))
                    {
                        Logger.Warn(Logger.Format("性能还原 | {0} | {1} | 已删除功能的记录未能清除", "Performance restore | {0} | {1} | records of removed features could not be cleared", item.Id, item.Target));
                    }
                    continue;
                }
                PerformanceAction performanceAction = PerformanceActionCatalog.Find(item.Id);
                string id = ((performanceAction == null) ? item.Id : performanceAction.Title);
                try
                {
                    if (progress != null)
                    {
                        progress(id);
                    }
                }
                catch
                {
                }
                string textResult = null;
                try
                {
                    if (PrivacyControl.IsAction(item.Id))
                    {
                        if (!PrivacyControl.RestoreReceipt(item))
                        {
                            textResult = PerformancePolicy.Text("隐私配置恢复未完成，记录已保留", "Privacy restoration is incomplete; records are retained");
                        }
                    }
                    else if (performanceAction == null)
                    {
                        textResult = PerformancePolicy.Text("无法识别恢复项目，已保留恢复记录", "Unknown recovery action; recovery record retained");
                    }
                    else if (!RestorePerformanceReceipt(item))
                    {
                        textResult = PerformanceActionBackend.RestoreFailureHint(item);
                    }
                }
                catch (Exception ex2)
                {
                    textResult = PsRunner.NormalizeError(ex2.Message);
                }
                if (textResult != null)
                {
                    string textResultOffset = id + " | " + item.Target + " | " + textResult;
                    list.Add(textResultOffset);
                    Logger.Warn(Logger.Format("性能还原 | {0}", "Performance restore | {0}", textResultOffset));
                }
            }
            return list;
        }

        internal static List<string> RestoreAllFailures(Action<string> progress = null)
        {
            lock (gate)
            {
                if (busy.Count > 0)
                {
                    List<string> list = new List<string>();
                    list.Add(PerformancePolicy.Text("环境配置仍在运行，恢复记录已保留，请稍后重试", "Environment configuration is still running; recovery records retained. Retry shortly."));
                    return list;
                }
            }
            // 全部恢复只在重置 卸载和系统版本过低的启动拦截里调用 前两个是用户自己点的
            // 启动拦截在运行期开始前只跑一次 按用户操作算也一样是每条回执写一次
            using (BeginUserRestore()) return RestoreFailures((PerformanceActionStore.Receipt param0) => true, progress);
        }

        internal static bool RestoreAll()
        {
            return RestoreAllFailures().Count == 0;
        }

        internal static bool RestoreSession()
        {
            return RestoreWhere((PerformanceActionStore.Receipt r) => r.Id == "AutoDefenderSessionV1" || r.Id == "AutoBackgroundFpsV1" || r.Id == "MatchVisualEffectsV1");
        }

        internal static string Label(string state)
        {
            switch (state)
            {
            case "live-applied":
                return PerformancePolicy.Text("已在线应用 · 无需重启电脑", "Applied online; no PC restart");
            case "live-restored":
                return PerformancePolicy.Text("已在线恢复 · 无需重启电脑", "Restored online; no PC restart");
            case "working":
                return PerformancePolicy.Text("正在配置", "Configuring");
            case "configured":
                return PerformancePolicy.Text("已配置 · 下次启动生效", "Configured; next launch");
            case "applied":
                return PerformancePolicy.Text("已执行", "Active");
            case "pending-reboot":
                return PerformancePolicy.Text("已配置 · 需重启电脑", "Configured; PC restart required");
            case "restore-pending-reboot":
                return PerformancePolicy.Text("已恢复 · 需重启电脑", "Restored; PC restart required");
            case "ready":
                return PerformancePolicy.Text("已重启 · 配置已写入", "Restarted; configuration written");
            case "unverified":
                return PerformancePolicy.Text("重启状态待确认", "Restart state unverified");
            case "external":
                return PerformancePolicy.Text("系统已配置", "Already configured by system");
            case "off":
                return PerformancePolicy.Text("已关闭", "Off");
            case "unsupported":
                return PerformancePolicy.Text("无适用设备或接口", "No applicable target");
            case "inactive":
                return PerformancePolicy.Text("Defender 未运行 · 已跳过", "Defender off; skipped");
            case "failed":
                return PerformancePolicy.Text("配置未完成 · 查看记录", "Configuration incomplete; see records");
            case "stopped":
                return PerformancePolicy.Text("执行失败 · 已停止应用，保留恢复记录", "Apply failed; further application stopped and recovery records retained");
            case "restore-failed":
                return PerformancePolicy.Text("恢复未完成 · 查看记录", "Restore pending");
            case "restored":
                return PerformancePolicy.Text("已恢复", "Restored");
            default:
                return PerformancePolicy.Text("自动待命", "Automatic standby");
            }
        }

        private static string EnvironmentMemberName(string key)
        {
            switch (key)
            {
            case "EnvCpuMitigationsV1":
                return PerformancePolicy.Text("CPU 缓解", "CPU mitigations");
            case "EnvAudioIdleV1":
                return PerformancePolicy.Text("音频空闲电源", "Audio idle power");
            default:
                return "VBS";
            }
        }

        private static string EnvironmentBundleIssues()
        {
            List<string> list = new List<string>();
            List<string> list2 = new List<string>();
            foreach (string item in EnvironmentMembers())
            {
                string stateResult = State(item);
                if (stateResult == "failed" || stateResult == "stopped")
                {
                    list.Add(EnvironmentMemberName(item));
                }
                if (stateResult == "restore-failed")
                {
                    list2.Add(EnvironmentMemberName(item));
                }
            }
            string environmentBundleIssuesResult = "";
            if (list.Count > 0)
            {
                environmentBundleIssuesResult = environmentBundleIssuesResult + "\n" + PerformancePolicy.Text("执行受限项：", "Blocked settings: ") + string.Join(", ", list.ToArray());
            }
            if (list2.Count > 0)
            {
                environmentBundleIssuesResult = environmentBundleIssuesResult + "\n" + PerformancePolicy.Text("恢复未完成项：", "Restoration incomplete: ") + string.Join(", ", list2.ToArray());
            }
            return environmentBundleIssuesResult;
        }

        private static List<string> EnvironmentMembers()
        {
            List<string> list = new List<string>();
            PerformanceAction[] all = PerformanceActionCatalog.All;
            foreach (PerformanceAction performanceAction in all)
            {
                if (EnvironmentBundleMember(performanceAction))
                {
                    list.Add(performanceAction.Key);
                }
            }
            list.Add("EnvVbsDisabledV1");
            return list;
        }

        private static bool EnvironmentBundleMember(PerformanceAction action)
        {
            // 可开启列表里的系统设置都在整包里
            return action.Environment;
        }

        private static bool EnvironmentPreference(string key)
        {
            if (key == "EnvVbsDisabledV1")
            {
                return Settings.LoadCached(key, VbsTweak.OwnsState);
            }
            return Settings.LoadCached(key, false);
        }

        private static int GroupEnabledCount(List<string> members, Predicate<string> selected)
        {
            int processedCount = 0;
            foreach (string member in members)
            {
                if (selected(member))
                {
                    processedCount++;
                }
            }
            return processedCount;
        }

        internal static int EnvironmentEnabledCount()
        {
            return GroupEnabledCount(EnvironmentMembers(), EnvironmentPreference);
        }

        internal static bool EnvironmentBundleBusy()
        {
            lock (gate)
            {
                return busy.Count > 0;
            }
        }

        internal static string EnvironmentBundleState()
        {
            return GroupState(EnvironmentMembers(), EnvironmentPreference);
        }

        private static string GroupState(List<string> members, Predicate<string> selected)
        {
            if (EnvironmentBundleBusy())
            {
                return "working";
            }
            int selectedCount = 0;
            int supportedSelections = 0;
            bool hasFailure = false;
            bool hasStopped = false;
            bool hasRecoveryFailure = false;
            bool hasPendingReboot = false;
            bool hasRestorePendingReboot = false;
            bool hasUnverified = false;
            bool hasRestored = false;
            foreach (string member in members)
            {
                bool isSelected = selected(member);
                string state = State(member);
                if (isSelected)
                {
                    selectedCount++;
                    if (state != "unsupported")
                    {
                        supportedSelections++;
                    }
                }
                hasFailure |= state == "failed";
                hasStopped |= state == "stopped";
                hasRecoveryFailure |= state == "restore-failed";
                hasPendingReboot |= state == "pending-reboot";
                hasRestorePendingReboot |= state == "restore-pending-reboot";
                hasUnverified |= state == "unverified" || (isSelected && state.Length == 0);
                hasRestored |= state == "restored";
            }
            if (hasRecoveryFailure)
            {
                return "restore-failed";
            }
            if (hasStopped)
            {
                return "stopped";
            }
            if (hasFailure)
            {
                return "failed";
            }
            if (selectedCount > 0 && selectedCount < members.Count)
            {
                return "partial";
            }
            if (hasPendingReboot)
            {
                return "pending-reboot";
            }
            if (hasRestorePendingReboot)
            {
                return "restore-pending-reboot";
            }
            if (hasUnverified)
            {
                return "unverified";
            }
            if (selectedCount > 0)
            {
                if (supportedSelections != 0)
                {
                    return "ready";
                }
                return "unsupported";
            }
            if (!hasRestored)
            {
                return "off";
            }
            return "restored";
        }

        internal static string EnvironmentBundleLabel()
        {
            return GroupLabel(EnvironmentMembers(), EnvironmentPreference);
        }

        private static string GroupLabel(List<string> members, Predicate<string> selected)
        {
            string groupStateResult = GroupState(members, selected);
            if (groupStateResult == "partial")
            {
                return PerformancePolicy.Text("部分启用 · 保留旧版选择 · ", "Partly enabled; existing choices retained · ") + GroupEnabledCount(members, selected) + "/" + members.Count;
            }
            string envPerformanceReadyText = ((groupStateResult == "ready") ? Lang.T("env.performance.ready") : Label(groupStateResult));
            if (groupStateResult == "working")
            {
                return envPerformanceReadyText;
            }
            int processedCount = 0;
            int processedCount2 = 0;
            bool restartRequired = false;
            foreach (string member in members)
            {
                string stateResult = State(member);
                if (stateResult == "unsupported")
                {
                    processedCount++;
                }
                switch (stateResult)
                {
                case "failed":
                case "stopped":
                case "restore-failed":
                    processedCount2++;
                    break;
                }
                restartRequired |= stateResult == "pending-reboot" || stateResult == "restore-pending-reboot";
            }
            if (processedCount2 > 0)
            {
                envPerformanceReadyText = processedCount2 + PerformancePolicy.Text(" 项受限", " blocked settings") + (restartRequired ? PerformancePolicy.Text(" · 已写入项需重启", " · Written settings require restart") : "");
            }
            if (processedCount != 0)
            {
                return envPerformanceReadyText + PerformancePolicy.Text(" · 跳过不适用项：", " · Unsupported settings skipped: ") + processedCount;
            }
            return envPerformanceReadyText;
        }

        private static void RestoreBundlePreferences(List<string> members, bool[] previous)
        {
            for (int i = 0; i < members.Count; i++)
            {
                bool preferenceRestored;
                try
                {
                    preferenceRestored = EnvironmentPreference(members[i]) == previous[i] || Settings.SaveDurable(members[i], previous[i]);
                }
                catch
                {
                    preferenceRestored = false;
                }
                if (!preferenceRestored)
                {
                    Logger.Warn(Logger.Format("系统环境偏好恢复 | {0}", "Environment preference restore | {0}", members[i]));
                }
            }
        }

        internal static bool SetEnvironmentBundle(bool on, Action completed)
        {
            lock (gate)
            {
                if (EnvironmentWorkClosing || busy.Count > 0)
                {
                    return false;
                }
                List<string> preferences = EnvironmentMembers();
                // 缓解范围只随手动开启一起保存 读档和关闭都沿用已有范围
                bool expanded = on && CpuMitigationsExpansionSupported();
                if (on) preferences.Add(CpuMitigationPolicy.ExpandedPreferenceKey);
                bool[] previous = new bool[preferences.Count];
                for (int i = 0; i < preferences.Count; i++)
                {
                    previous[i] = EnvironmentPreference(preferences[i]);
                }
                foreach (string preference in preferences)
                {
                    bool saved;
                    try
                    {
                        bool value = preference == CpuMitigationPolicy.ExpandedPreferenceKey ? expanded : on;
                        saved = Settings.SaveDurable(preference, value);
                    }
                    catch (Exception ex)
                    {
                        saved = false;
                        Logger.Warn(Logger.Format("系统环境偏好保存 | {0}", "Environment preference save | {0}", ex.Message));
                    }
                    if (!saved)
                    {
                        RestoreBundlePreferences(preferences, previous);
                        return false;
                    }
                }
                busy.Add(EnvironmentBundleKey);
                bool queued = false;
                try
                {
                    queued = QueueEnvironmentWork(delegate
                    {
                        try
                        {
                            // 用户关整包 这一次恢复按用户操作算
                            using (on ? null : BeginUserRestore()) ApplyEnvironmentBundle(on);
                        }
                        finally
                        {
                            lock (gate)
                            {
                                busy.Remove(EnvironmentBundleKey);
                                Monitor.PulseAll(gate);
                            }
                            if (completed != null)
                            {
                                try
                                {
                                    completed();
                                }
                                catch
                                {
                                }
                            }
                        }
                    });
                }
                catch (Exception ex2)
                {
                    Logger.Warn(Logger.Format("系统环境配置队列 | {0}", "Environment configuration queue | {0}", ex2.Message));
                }
                if (!queued)
                {
                    busy.Remove(EnvironmentBundleKey);
                    Monitor.PulseAll(gate);
                    RestoreBundlePreferences(preferences, previous);
                }
                return queued;
            }
        }

        internal static bool ApplyEnvironmentBundle(bool on)
        {
            bool result = true;
            PerformanceAction[] all = PerformanceActionCatalog.All;
            foreach (PerformanceAction performanceAction in all)
            {
                if (!EnvironmentBundleMember(performanceAction))
                {
                    continue;
                }
                if (EnvironmentWorkClosing)
                {
                    return false;
                }
                try
                {
                    if (!ApplyEnvironment(performanceAction.Key, on) && State(performanceAction.Key) != "unsupported")
                    {
                        result = false;
                    }
                }
                catch (Exception ex)
                {
                    result = false;
                    SetState(performanceAction.Key, on ? "failed" : "restore-failed");
                    Logger.Warn(performanceAction.Title + " | " + ex.Message);
                }
            }
            if (EnvironmentWorkClosing)
            {
                return false;
            }
            try
            {
                if (!ApplyEnvironmentVbs(on))
                {
                    result = false;
                }
            }
            catch (Exception ex2)
            {
                result = false;
                SetState("EnvVbsDisabledV1", on ? "failed" : "restore-failed");
                Logger.Warn("VBS | " + ex2.Message);
            }
            return result;
        }

        private static bool VbsConfigured()
        {
#if PAVISE_SELFTEST
            if (VbsConfiguredForTest == null) throw new InvalidOperationException("Unmocked VBS configuration read");
            return VbsConfiguredForTest();
#endif
            return VbsTweak.CurrentlyConfiguredOff();
        }

        private static bool ChangeVbs(bool on)
        {
#if PAVISE_SELFTEST
            if (VbsChangeForTest == null) throw new InvalidOperationException("Unmocked VBS configuration write");
            return VbsChangeForTest(on);
#endif
            if (!on)
            {
                return VbsTweak.Restore();
            }
            return VbsTweak.Disable();
        }

        private static bool RestoreOwnedVbs(Func<bool> prepare = null)
        {
            if (VbsTweak.OwnsState)
            {
                bool restored = RestoreWithBudget(new PerformanceActionStore.Receipt { Id = EnvironmentVbsKey, Target = "system" }, 3,
                    () => (prepare == null || prepare()) && ChangeVbs(false) && !VbsTweak.OwnsState);
                if (restored) ActionFailureStore.CancelApply(EnvironmentVbsKey, "system");
                return restored;
            }
            return true;
        }

        private static bool SkipVbsApply(bool on, string state)
        {
            if (!on)
            {
                SetState("EnvVbsDisabledV1", "restore-failed");
                return false;
            }
            bool restoreOwnedVbsSucceeded;
            try
            {
                restoreOwnedVbsSucceeded = RestoreOwnedVbs();
            }
            catch
            {
                restoreOwnedVbsSucceeded = false;
            }
            if (restoreOwnedVbsSucceeded)
            {
                ActionFailureStore.CancelApply("EnvVbsDisabledV1", "system");
            }
            else
            {
                ActionFailureStore.Trip("EnvVbsDisabledV1", "system", "Recovery incomplete after skipped VBS apply");
            }
            SetState("EnvVbsDisabledV1", restoreOwnedVbsSucceeded ? state : "restore-failed");
            return false;
        }

        internal static bool ApplyEnvironmentVbs(bool on)
        {
            if (EnvironmentWorkClosing)
            {
                return false;
            }
            if (on)
            {
                try
                {
                    ActionFailureStore.BeginAutomaticCycle("EnvVbsDisabledV1", "system", RecoveryCycleSerial + "\ndisabled");
                    if (ActionFailureStore.IsIsolated("EnvVbsDisabledV1", "system") && !RestoreOwnedVbs())
                    {
                        throw new InvalidOperationException("Recovery incomplete before retry");
                    }
                }
                catch (Exception ex)
                {
                    ActionFailureStore.Trip("EnvVbsDisabledV1", "system", ex.Message);
                    SetState("EnvVbsDisabledV1", "restore-failed");
                    return false;
                }
                string reason;
                ActionFailureStore.BeginAutomaticCycle("EnvVbsDisabledV1", "system", RecoveryCycleSerial + "\ndisabled");
                if (!ActionFailureStore.TryBeginApply("EnvVbsDisabledV1", "system", out reason))
                {
                    SetState("EnvVbsDisabledV1", VbsTweak.OwnsState ? "restore-failed" : "stopped");
                    return false;
                }
            }
            try
            {
                bool alreadyInRequestedState = on ? VbsConfigured() : !VbsTweak.OwnsState;
                if (alreadyInRequestedState)
                {
                    string previousState = Settings.LoadStr("EnvVbsDisabledV1State", "");
                    bool completed = !on || ActionFailureStore.CompleteApply("EnvVbsDisabledV1", "system");
                    string state = "stopped";
                    if (completed)
                    {
                        bool ownedState = on ? previousState == "configured" || VbsTweak.OwnsState : previousState == "restored";
                        state = ownedState ? (on ? "configured" : "restored") : (on ? "external" : "off");
                    }
                    SetState("EnvVbsDisabledV1", state);
                    return completed;
                }
                string bootResult = Boot();
                if (!BootSessionIdentity.Valid(bootResult) || EnvironmentWorkClosing)
                {
                    if (on)
                    {
                        ActionFailureStore.CancelApply("EnvVbsDisabledV1", "system");
                    }
                    SetState("EnvVbsDisabledV1", on ? "unverified" : "restore-failed");
                    return false;
                }
                bool applied = on ? Settings.SaveStrDurable("EnvVbsDisabledV1Boot", bootResult) && ChangeVbs(true)
                    : RestoreOwnedVbs(() => Settings.SaveStrDurable("EnvVbsDisabledV1Boot", bootResult));
                if (applied)
                {
                    applied = (on ? VbsConfigured() : (!VbsTweak.OwnsState));
                }
                if (!applied)
                {
                    throw new InvalidOperationException("VBS apply or readback failed");
                }
                if (on)
                {
                    applied = ActionFailureStore.CompleteApply("EnvVbsDisabledV1", "system");
                    if (!applied && !RestoreOwnedVbs())
                    {
                        SetState("EnvVbsDisabledV1", "restore-failed");
                        return false;
                    }
                }
                SetState("EnvVbsDisabledV1", (!applied) ? "stopped" : (on ? "configured" : "restored"));
                return applied;
            }
            catch (NotSupportedException)
            {
                return SkipVbsApply(on, "unsupported");
            }
            catch (OperationCanceledException)
            {
                return SkipVbsApply(on, "");
            }
            catch (Exception ex4)
            {
                if (on)
                {
                    ActionFailureStore.Trip("EnvVbsDisabledV1", "system", ex4.Message);
                    try
                    {
                        if (!RestoreOwnedVbs())
                        {
                            SetState("EnvVbsDisabledV1", "restore-failed");
                            return false;
                        }
                    }
                    catch
                    {
                        SetState("EnvVbsDisabledV1", "restore-failed");
                        return false;
                    }
                }
                SetState("EnvVbsDisabledV1", on ? "stopped" : "restore-failed");
                return false;
            }
        }

        internal static bool QueueEnvironmentWork(WaitCallback work)
        {
#if PAVISE_SELFTEST
            if (EnvironmentQueueForTest != null) return EnvironmentQueueForTest(work);
#endif
            return ThreadPool.QueueUserWorkItem(work);
        }

        internal static bool CloseEnvironmentWork(int timeoutMs)
        {
            if (timeoutMs < 0)
            {
                return false;
            }
            EnvironmentWorkClosing = true;
            Stopwatch stopwatch = Stopwatch.StartNew();
            bool gateAcquired = false;
            try
            {
                gateAcquired = Monitor.TryEnter(gate, timeoutMs);
                if (!gateAcquired)
                {
                    return false;
                }
                while (busy.Count > 0)
                {
                    int lowerBound = (int)Math.Max(0L, timeoutMs - stopwatch.ElapsedMilliseconds);
                    if (lowerBound <= 0)
                    {
                        return false;
                    }
                    Monitor.Wait(gate, lowerBound);
                }
                return true;
            }
            finally
            {
                if (gateAcquired)
                {
                    Monitor.Exit(gate);
                }
            }
        }
    }
}
