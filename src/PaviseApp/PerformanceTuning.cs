using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;

namespace PaviseApp
{
    internal static partial class PerformanceTuning
    {
        internal const string EnvironmentBundleKey = "EnvPerformanceBundleV1";

        internal const string StorageGroupKey = "EnvStoragePowerGroupV1";

        internal const string EnvironmentVbsKey = "EnvVbsDisabledV1";

        private static readonly object gate = new object();
        private static readonly object selectionSignatureGate = new object();
        private static int selectionSignatureGeneration = -1;
        private static bool selectionSignatureVisuals;
        private static string selectionSignature;

        private static readonly Dictionary<string, int> recoveryFailures = new Dictionary<string, int>(StringComparer.Ordinal);
        private static long recoverySessionSerial;
        internal static long RecoveryCycleSerial { get { return Interlocked.Read(ref recoverySessionSerial); } }

        internal static void BeginRecoveryCycle(long gameSessionSerial)
        {
            lock (SystemConfigurationGate.Sync)
            {
                // 旧会话迟到的工作不能为新会话补充预算。
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
                // 有意排除可变的诊断信息和提交标志。
                string key = recoverySessionSerial + "|" + RecoveryPart(receipt.Id) + RecoveryPart(receipt.Target)
                    + RecoveryPart(receipt.Original) + RecoveryPart(receipt.Expected) + RecoveryPart(receipt.VisualOwner);
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
                        Logger.Warn("性能恢复：动作=" + receipt.Id + " 目标=" + receipt.Target
                            + " 阶段=" + recoverySessionSerial + " 尝试=" + failures + "/" + maximumFailures
                            + (failures >= maximumFailures ? "；已停止主动重试，保留回执" : "；保留回执"));
                    }
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

        private static volatile HashSet<string> storageOwned = new HashSet<string>(StringComparer.Ordinal);

        internal static volatile bool EnvironmentWorkClosing;

#if PAVISE_SELFTEST
        internal static Func<bool> CpuMitigationsSupportedForTest;
        internal static Func<WaitCallback, bool> EnvironmentQueueForTest;
        internal static Func<string> BootForTest;
#endif

        internal static string EnvironmentBundleTitle
        {
            get
            {
                return PerformancePolicy.Text("系统性能增强 · 需重启", "System performance boost · restart required");
            }
        }

        internal static string EnvironmentBundleDetail
        {
            get
            {
                return PerformancePolicy.Text("统一控制关闭 VBS（含内存完整性和 hypervisor）、CPU 软件缓解、NTFS 最后访问时间和音频电源管理，共四项。开启或恢复后需重启电脑；关闭时恢复原配置。不适用项自动跳过。关闭 hypervisor 会影响 WSL2、Hyper-V 等虚拟化功能。存储电源管理已移到下方单独的高级选项。", "Combines four settings: disabling VBS (including Memory Integrity and the hypervisor), CPU software mitigations, NTFS last-access updates and audio power management. Restart after enabling or restoring. Switching off restores original settings; unsupported targets are skipped. Disabling the hypervisor affects WSL2, Hyper-V and other virtualization features. Storage power settings are a separate advanced option below.") + EnvironmentBundleIssues();
            }
        }

        internal static bool EnvironmentBundleEnabled
        {
            get
            {
                return EnvironmentEnabledCount() > 0;
            }
        }

        internal static string StorageGroupTitle
        {
            get
            {
                return PerformancePolicy.Text("存储设备电源管理 · 高级 · 需重启", "Storage device power management · Advanced · restart required");
            }
        }

        internal static string StorageGroupDetail
        {
            get
            {
                string textResult = PerformancePolicy.Text("对已登记 StorPort 的设备持久关闭三项电源管理：磁盘空闲电源管理、存储控制器运行时电源管理和 D3Cold。默认关闭；开启或恢复后需重启电脑，关闭时恢复原配置，不适用项自动跳过。这些设备将不再进入对应的省电状态，空闲与睡眠功耗可能上升。与电源计划在对局期间调整的存储设置相互独立。", "Persistently disables three power-management settings on devices with registered StorPort configuration: disk idle power management, storage-controller runtime power management and D3Cold. Off by default. Restart after enabling or restoring; switching off restores original settings and unsupported targets are skipped. These devices stop entering the matching power-saving states, so idle and sleep power may rise. Independent of the storage settings the power plan adjusts during matches.");
                HashSet<string> owned = StorageOwned();
                foreach (string storageMember in StorageMembers())
                {
                    string message = textResult;
                    textResult = message + "\n" + EnvironmentMemberName(storageMember) + PerformancePolicy.Text("：", ": ") + StorageMemberLabel(storageMember, owned);
                }
                return textResult;
            }
        }

        private static string StorageRecoveryPendingText
        {
            get
            {
                return PerformancePolicy.Text("原配置待恢复 · 关闭此项即可恢复", "Original configuration not yet restored · switch off to restore");
            }
        }

        internal static bool StorageGroupEnabled
        {
            get
            {
                HashSet<string> textsSet = StorageOwned();
                foreach (string storageMember in StorageMembers())
                {
                    if (EnvironmentPreference(storageMember) || textsSet.Contains(storageMember) || State(storageMember) == "restore-failed")
                    {
                        return true;
                    }
                }
                return false;
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

        internal static bool ActionSelected(string id)
        {
            PerformanceAction performanceAction = PerformanceActionCatalog.Find(id);
            if (performanceAction == null || performanceAction.Environment)
            {
                return false;
            }
            if (id == "MatchVisualEffectsV1")
            {
                return MatchVisualEffects.Enabled;
            }
            return Settings.LoadCached(id, false);
        }

        internal static bool LegacyActionSelected(string id)
        {
            return ActionSelected(id);
        }

        internal static string ActionSelectionSignature()
        {
            lock (selectionSignatureGate)
            {
                int generation = Settings.MutationGeneration;
                bool visuals = MatchVisualEffects.Enabled;
                if (selectionSignature != null && selectionSignatureGeneration == generation
                    && selectionSignatureVisuals == visuals)
                    return selectionSignature;
                string signature = BuildActionSelectionSignature();
                if (generation == Settings.MutationGeneration && visuals == MatchVisualEffects.Enabled)
                {
                    selectionSignature = signature;
                    selectionSignatureGeneration = generation;
                    selectionSignatureVisuals = visuals;
                }
                return signature;
            }
        }

        private static string BuildActionSelectionSignature()
        {
            List<string> texts = new List<string>();
            foreach (PerformanceAction performanceAction in PerformanceActionCatalog.All)
            {
                if (!performanceAction.Environment && ActionSelected(performanceAction.Key))
                {
                    texts.Add(performanceAction.Key);
                }
            }
            return string.Join(",", texts.ToArray());
        }

        internal static string State(string id)
        {
            lock (gate)
            {
                if (busy.Contains(id))
                {
                    return "working";
                }
                string state;
                if (!states.TryGetValue(id, out state))
                {
                    state = Settings.LoadStr(id + "State", "");
                }
                bool live = state == "live-applied" || state == "live-restored";
                if (state != "configured" && state != "restored" && !live)
                {
                    return state;
                }
                object result;
                switch (BootSessionIdentity.Compare(Settings.LoadStr(id + "Boot", ""), Boot()))
                {
                default:
                    result = live ? state : ((state == "configured") ? "pending-reboot" : "restore-pending-reboot");
                    break;
                case IrqRebootState.Rebooted:
                    result = ((state == "configured" || state == "live-applied") ? "ready" : "restored");
                    break;
                case IrqRebootState.Unknown:
                    result = "unverified";
                    break;
                }
                return (string)result;
            }
        }

        internal static bool SetQuantumProfile(int index, Action completed)
        {
            if (index < 0 || index >= QuantumProfiles.Values.Length)
            {
                return false;
            }
            lock (gate)
            {
                if (EnvironmentWorkClosing || busy.Contains("EnvQuantumProfileV1") || busy.Contains("EnvPerformanceBundleV1") || busy.Contains("EnvStoragePowerGroupV1"))
                {
                    return false;
                }
                string selectedQuantumProfile = Settings.LoadStr("EnvQuantumSelectedProfileV1", "38");
                bool previousOn = Settings.LoadCached(PerformanceActionCatalog.Quantum, false);
                if (index > 0 && !Settings.SaveStr("EnvQuantumSelectedProfileV1", QuantumProfiles.Values[index].ToString(CultureInfo.InvariantCulture)))
                {
                    return false;
                }
                if (SetEnvironment("EnvQuantumProfileV1", index > 0, completed))
                {
                    return true;
                }
                Settings.SaveStr("EnvQuantumSelectedProfileV1", selectedQuantumProfile);
                Settings.SaveDurable(PerformanceActionCatalog.Quantum, previousOn);
                return false;
            }
        }

        internal static bool SetEnvironment(string id, bool on, Action completed)
        {
            PerformanceAction action = PerformanceActionCatalog.Find(id);
            if (action == null || !action.Environment)
            {
                return false;
            }
            lock (gate)
            {
                if (EnvironmentWorkClosing || busy.Contains(id) || busy.Contains("EnvPerformanceBundleV1") || busy.Contains("EnvStoragePowerGroupV1") || !Settings.Save(id, on))
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
                        ApplyEnvironment(id, on);
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
                                Logger.Warn("系统环境完成 | " + ex3.Message);
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

        internal static bool SetCpuMitigations(bool on, Action completed, bool expanded = true)
        {
            lock (gate)
            {
                if (EnvironmentWorkClosing || busy.Contains(PerformanceActionCatalog.CpuMitigations)
                    || busy.Contains(EnvironmentBundleKey) || busy.Contains(StorageGroupKey)) return false;
                if (on && expanded && !CpuMitigationsExpansionSupported())
                {
                    SetState(PerformanceActionCatalog.CpuMitigations, "unsupported");
                    return false;
                }
                bool previous = CpuMitigationsExpanded;
                bool previousOn = Settings.LoadCached(PerformanceActionCatalog.CpuMitigations, false);
                // 扩大范围的意图一直保留到其持有值恢复，收到关闭请求后也不提前丢弃。
                if (on && !Settings.SaveDurable(CpuMitigationPolicy.ExpandedPreferenceKey, expanded)) return false;
                if (SetEnvironment(PerformanceActionCatalog.CpuMitigations, on, completed)) return true;
                if (on) Settings.SaveDurable(CpuMitigationPolicy.ExpandedPreferenceKey, previous);
                Settings.SaveDurable(PerformanceActionCatalog.CpuMitigations, previousOn);
                return false;
            }
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
            return RecoveryCycleSerial + "\n" + id
                + (id == PerformanceActionCatalog.Quantum ? "\nquantum:" + QuantumProfiles.Target : "");
        }

        private static string VerifiedEnvironmentState(string id, bool on)
        {
            string str = Settings.LoadStr(id + "State", "");
            if (id == PerformanceActionCatalog.Quantum && (on ? str == "live-applied" : str == "live-restored"))
                return str;
            if (!on)
            {
                if (str != "restored")
                {
                    return "off";
                }
                return str;
            }
            if (str == "configured")
            {
                return str;
            }
            foreach (PerformanceActionStore.Receipt all in PerformanceActionStore.All())
            {
                if (all.Id == id)
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
            List<string> normalizeErrorOffsets = new List<string>();
            List<PerformanceActionStore.Receipt> receipts;
            try
            {
                if (!ReadRecoveryReceipts(out receipts)) { normalizeErrorOffsets.Add("Recovery journal read budget exhausted; records retained"); return normalizeErrorOffsets; }
            }
            catch (Exception ex)
            {
                string normalizeErrorOffset = PerformancePolicy.Text("性能配置恢复记录读取失败", "Performance recovery journal could not be read") + " | " + PsRunner.NormalizeError(ex.Message);
                normalizeErrorOffsets.Add(normalizeErrorOffset);
                Logger.Warn(normalizeErrorOffset);
                return normalizeErrorOffsets;
            }
            foreach (PerformanceActionStore.Receipt receipt in receipts)
            {
                if (!selected(receipt))
                {
                    continue;
                }
                PerformanceAction performanceAction = PerformanceActionCatalog.Find(receipt.Id);
                string id = ((performanceAction == null) ? receipt.Id : performanceAction.Title);
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
                    if (PrivacyControl.IsAction(receipt.Id))
                    {
                        if (!PrivacyControl.RestoreReceipt(receipt))
                        {
                            textResult = PerformancePolicy.Text("隐私配置恢复未完成，记录已保留", "Privacy restoration is incomplete; records are retained");
                        }
                    }
                    else if (performanceAction == null)
                    {
                        textResult = PerformancePolicy.Text("无法识别恢复项目，已保留恢复记录", "Unknown recovery action; recovery record retained");
                    }
                    else if (!RestorePerformanceReceipt(receipt))
                    {
                        textResult = PerformanceActionBackend.RestoreFailureHint(receipt);
                    }
                }
                catch (Exception ex2)
                {
                    textResult = PsRunner.NormalizeError(ex2.Message);
                }
                if (textResult != null)
                {
                    string textResultOffset = id + " | " + receipt.Target + " | " + textResult;
                    normalizeErrorOffsets.Add(textResultOffset);
                    Logger.Warn("性能还原 | " + textResultOffset);
                }
            }
            return normalizeErrorOffsets;
        }

        internal static List<string> RestoreAllFailures(Action<string> progress = null)
        {
            lock (gate)
            {
                if (busy.Count > 0)
                {
                    List<string> texts = new List<string>();
                    texts.Add(PerformancePolicy.Text("环境配置仍在运行，恢复记录已保留，请稍后重试", "Environment configuration is still running; recovery records retained. Retry shortly."));
                    return texts;
                }
            }
            return RestoreFailures((PerformanceActionStore.Receipt param0) => true, progress);
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
            case "EnvNtfsLastAccessV1":
                return PerformancePolicy.Text("NTFS 访问时间", "NTFS last access");
            case "EnvStorageIdleV1":
                return PerformancePolicy.Text("存储空闲电源", "Storage idle power");
            case "EnvStorageRuntimeV1":
                return PerformancePolicy.Text("存储运行时电源", "Storage runtime power");
            case "EnvStorageD3ColdV1":
                return PerformancePolicy.Text("存储 D3Cold", "Storage D3Cold");
            case "EnvAudioIdleV1":
                return PerformancePolicy.Text("音频空闲电源", "Audio idle power");
            default:
                return "VBS";
            }
        }

        private static string EnvironmentBundleIssues()
        {
            List<string> environmentMemberNames = new List<string>();
            List<string> currentEnvironmentMemberNames = new List<string>();
            foreach (string environmentMember in EnvironmentMembers())
            {
                string stateResult = State(environmentMember);
                if (stateResult == "failed" || stateResult == "stopped")
                {
                    environmentMemberNames.Add(EnvironmentMemberName(environmentMember));
                }
                if (stateResult == "restore-failed")
                {
                    currentEnvironmentMemberNames.Add(EnvironmentMemberName(environmentMember));
                }
            }
            string environmentBundleIssuesResult = "";
            if (environmentMemberNames.Count > 0)
            {
                environmentBundleIssuesResult = environmentBundleIssuesResult + "\n" + PerformancePolicy.Text("执行受限项：", "Blocked settings: ") + string.Join(", ", environmentMemberNames.ToArray());
            }
            if (currentEnvironmentMemberNames.Count > 0)
            {
                environmentBundleIssuesResult = environmentBundleIssuesResult + "\n" + PerformancePolicy.Text("恢复未完成项：", "Restoration incomplete: ") + string.Join(", ", currentEnvironmentMemberNames.ToArray());
            }
            return environmentBundleIssuesResult;
        }

        private static List<string> EnvironmentMembers()
        {
            List<string> texts = new List<string>();
            foreach (PerformanceAction performanceAction in PerformanceActionCatalog.All)
            {
                if (EnvironmentBundleMember(performanceAction))
                {
                    texts.Add(performanceAction.Key);
                }
            }
            texts.Add("EnvVbsDisabledV1");
            return texts;
        }

        private static bool EnvironmentBundleMember(PerformanceAction action)
        {
            if (action.Environment && action.Key != "EnvQuantumProfileV1")
            {
                return !StorageMember(action.Key);
            }
            return false;
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
                    Logger.Warn("系统环境偏好恢复 | " + members[i]);
                }
            }
        }

        internal static bool SetEnvironmentBundle(bool on, Action completed)
        {
            return SetEnvironmentGroup("EnvPerformanceBundleV1", EnvironmentMembers(), on, completed, ApplyEnvironmentBundle);
        }

        private static bool SetEnvironmentGroup(string busyKey, List<string> members, bool on, Action completed, Func<bool, bool> apply)
        {
            bool[] conditionMetItems = new bool[members.Count];
            bool setEnvironmentGroupSucceeded = false;
            lock (gate)
            {
                if (EnvironmentWorkClosing || busy.Count > 0)
                {
                    return false;
                }
                for (int i = 0; i < members.Count; i++)
                {
                    conditionMetItems[i] = EnvironmentPreference(members[i]);
                }
                for (int j = 0; j < members.Count; j++)
                {
                    bool saveDurableSucceeded;
                    try
                    {
                        saveDurableSucceeded = Settings.SaveDurable(members[j], on);
                    }
                    catch (Exception ex)
                    {
                        saveDurableSucceeded = false;
                        Logger.Warn("系统环境偏好保存 | " + ex.Message);
                    }
                    if (!saveDurableSucceeded)
                    {
                        RestoreBundlePreferences(members, conditionMetItems);
                        return false;
                    }
                }
                busy.Add(busyKey);
                try
                {
                    setEnvironmentGroupSucceeded = QueueEnvironmentWork(delegate
                    {
                        try
                        {
                            apply(on);
                        }
                        finally
                        {
                            lock (gate)
                            {
                                busy.Remove(busyKey);
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
                    Logger.Warn("系统环境配置队列 | " + ex2.Message);
                }
                if (!setEnvironmentGroupSucceeded)
                {
                    lock (gate)
                    {
                        busy.Remove(busyKey);
                        Monitor.PulseAll(gate);
                        RestoreBundlePreferences(members, conditionMetItems);
                    }
                }
            }
            return setEnvironmentGroupSucceeded;
        }

        internal static bool ApplyEnvironmentBundle(bool on)
        {
            bool result = true;
            foreach (PerformanceAction performanceAction in PerformanceActionCatalog.All)
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

        internal static bool StorageMember(string key)
        {
            if (key != "EnvStorageIdleV1" && key != "EnvStorageRuntimeV1")
            {
                return key == "EnvStorageD3ColdV1";
            }
            return true;
        }

        internal static List<string> StorageMembers()
        {
            List<string> texts = new List<string>();
            foreach (PerformanceAction performanceAction in PerformanceActionCatalog.All)
            {
                if (performanceAction.Environment && StorageMember(performanceAction.Key))
                {
                    texts.Add(performanceAction.Key);
                }
            }
            return texts;
        }

        private static string StorageMemberLabel(string key, HashSet<string> owned)
        {
            string stateResult = State(key);
            if (StorageRecoveryPending(key, owned) && stateResult != "restore-failed" && stateResult != "working")
            {
                return StorageRecoveryPendingText;
            }
            if (stateResult.Length == 0)
            {
                if (!EnvironmentPreference(key))
                {
                    return Label("off");
                }
                return Lang.T("env.performance.pending");
            }
            if (stateResult != "ready")
            {
                return Label(stateResult);
            }
            return Lang.T("env.performance.ready");
        }

        private static HashSet<string> StorageOwned()
        {
            HashSet<string> ids = storageOwned;
            try
            {
                List<PerformanceActionStore.Receipt> receipts;
                if (PerformanceActionStore.TryAll(out receipts))
                {
                    ids = new HashSet<string>(StringComparer.Ordinal);
                    foreach (PerformanceActionStore.Receipt receipt in receipts)
                    {
                        if (StorageMember(receipt.Id))
                        {
                            ids.Add(receipt.Id);
                        }
                    }
                }
            }
            catch
            {
                ids = new HashSet<string>(StorageMembers(), StringComparer.Ordinal);
            }
            storageOwned = ids;
            return ids;
        }

        private static bool StorageRecoveryPending(string key, HashSet<string> owned)
        {
            if (!EnvironmentPreference(key))
            {
                return owned.Contains(key);
            }
            return false;
        }

        internal static int StorageEnabledCount()
        {
            return GroupEnabledCount(StorageMembers(), EnvironmentPreference);
        }

        internal static string StorageGroupState()
        {
            List<string> texts = StorageMembers();
            string groupStateResult = GroupState(texts, EnvironmentPreference);
            switch (groupStateResult)
            {
            case "working":
            case "restore-failed":
            case "stopped":
            case "failed":
                return groupStateResult;
            default:
            {
                HashSet<string> owned = StorageOwned();
                {
                    foreach (string text in texts)
                    {
                        if (StorageRecoveryPending(text, owned))
                        {
                            return "recovery-pending";
                        }
                    }
                    return groupStateResult;
                }
            }
            }
        }

        internal static string StorageGroupLabel()
        {
            if (StorageGroupState() == "recovery-pending")
            {
                return StorageRecoveryPendingText;
            }
            return GroupLabel(StorageMembers(), EnvironmentPreference);
        }

        internal static bool SetStorageGroup(bool on, Action completed)
        {
            return SetEnvironmentGroup("EnvStoragePowerGroupV1", StorageMembers(), on, completed, ApplyStorageGroup);
        }

        internal static bool ApplyStorageGroup(bool on)
        {
            bool result = true;
            foreach (string storageMember in StorageMembers())
            {
                if (EnvironmentWorkClosing)
                {
                    return false;
                }
                try
                {
                    if (!ApplyEnvironment(storageMember, on) && State(storageMember) != "unsupported")
                    {
                        result = false;
                    }
                }
                catch (Exception ex)
                {
                    result = false;
                    SetState(storageMember, on ? "failed" : "restore-failed");
                    Logger.Warn(PerformanceActionCatalog.Find(storageMember).Title + " | " + ex.Message);
                }
            }
            return result;
        }

        private static bool VbsConfigured()
        {
            return VbsTweak.CurrentlyConfiguredOff();
        }

        private static bool ChangeVbs(bool on)
        {
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
