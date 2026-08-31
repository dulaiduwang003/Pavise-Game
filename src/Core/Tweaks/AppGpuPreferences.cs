// 文件用途 显式且持久的逐 EXE Windows 显卡偏好 不迁移进程也不做强制
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace PaviseApp
{
    internal enum AppGpuPreferenceResult
    {
        Success, AlreadyPresent, NeedsConfirmation, Unsupported, InvalidPath, Changed,
        Busy, ReadFailed, WriteFailed, JournalFailed, RecoveryPending, NotFound
    }

    internal enum AppGpuPreferenceState
    { Owned, AlreadyLowPower, ExternalChange, PendingApply, PendingRestore, CleanupPending }

    internal enum AppGpuPreferenceWriteResult { Written, NotIssued, Changed, Unknown }

    internal interface IAppGpuPreferenceControl
    {
        bool Supported { get; }
        bool IsExecutablePresent(string path);
        bool TryRead(string path, out string value);
        bool TryGetStagedOriginal(string path, out bool staged, out string original);
        bool TryReleaseStage(string path);
        AppGpuPreferenceWriteResult CompareExchange(string path, string expected, string replacement);
    }

    internal interface IAppGpuPreferenceLedger
    {
        bool TryRead(out string value);
        bool TryWrite(string value);
    }

    internal sealed class AppGpuPreferenceChange
    {
        internal readonly AppGpuPreferenceManager Owner;
        internal readonly string CurrentRaw, BaselineRaw, StageOriginal;
        internal readonly bool Staged;
        public readonly string ExePath, Name, CurrentPreference, OriginalPreference;
        public readonly bool NeedsConfirmation, AlreadyLowPower;

        internal AppGpuPreferenceChange(AppGpuPreferenceManager owner, string path, string current,
            string baseline, bool staged, string stageOriginal)
        {
            Owner = owner; ExePath = path; Name = Path.GetFileNameWithoutExtension(path);
            CurrentRaw = current; BaselineRaw = baseline; Staged = staged; StageOriginal = stageOriginal;
            CurrentPreference = PrefFieldText.ReadField(current, "GpuPreference");
            OriginalPreference = PrefFieldText.ReadField(baseline, "GpuPreference");
            AlreadyLowPower = OriginalPreference == "1";
            NeedsConfirmation = !AlreadyLowPower && !string.IsNullOrEmpty(baseline);
        }
    }

    internal sealed class AppGpuPreferenceEntry
    {
        public readonly string ExePath, Name, CurrentPreference;
        public readonly AppGpuPreferenceState State;
        public readonly bool ReadFailed, CanForget;

        internal AppGpuPreferenceEntry(string path, string name, AppGpuPreferenceState state,
            string current, bool readFailed)
        {
            ExePath = path; Name = name; State = state;
            CurrentPreference = PrefFieldText.ReadField(current, "GpuPreference");
            ReadFailed = readFailed;
            CanForget = state == AppGpuPreferenceState.PendingApply || state == AppGpuPreferenceState.PendingRestore
                || state == AppGpuPreferenceState.ExternalChange;
        }
    }

    internal static class AppGpuPreferences
    {
        internal const string LedgerKey = "AppGpuPreferencesV1";
        private static readonly AppGpuPreferenceManager shared = new AppGpuPreferenceManager(
            new WindowsAppGpuPreferenceControl(), new SettingsAppGpuPreferenceLedger());
#if PAVISE_SELFTEST || PAVISE_PERFLAB
        internal static AppGpuPreferenceManager OverrideForTest;
#endif
        public static AppGpuPreferenceManager Shared
        {
            get
            {
#if PAVISE_SELFTEST || PAVISE_PERFLAB
                if (OverrideForTest != null) return OverrideForTest;
#endif
                return shared;
            }
        }
        public static bool HasManagedPath(string path) { return Shared.HasManagedPath(path); }
        public static bool HasResidue { get { return Shared.HasResidue; } }
        public static bool RestoreAll() { return Shared.RestoreAll(); }
        public static bool AbandonUnprovableForReset() { return Shared.AbandonUnprovableForReset(); }
        public static bool HealFromCrash() { return Shared.HealFromCrash(); }

        internal static bool ConfirmedHybrid(IEnumerable<GpuAdapter> adapters)
        {
            if (adapters == null) return false;
            var integrated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var discrete = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (GpuAdapter adapter in adapters)
            {
                if (adapter == null || !adapter.IntegratedKnown || string.IsNullOrEmpty(adapter.HardwareId)) continue;
                (adapter.Integrated ? integrated : discrete).Add(adapter.HardwareId);
            }
            foreach (string first in integrated)
                foreach (string second in discrete)
                    if (!string.Equals(first, second, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private sealed class SettingsAppGpuPreferenceLedger : IAppGpuPreferenceLedger
        {
            public bool TryRead(out string value) { return Settings.TryLoadStr(LedgerKey, out value); }
            public bool TryWrite(string value) { return Settings.SaveStr(LedgerKey, value); }
        }

        private sealed class WindowsAppGpuPreferenceControl : IAppGpuPreferenceControl
        {
            private const string GpuKey = @"SOFTWARE\Microsoft\DirectX\UserGpuPreferences";
            public bool Supported
            {
                get { RefuseNativeInTests(); return ConfirmedHybrid(GpuInventory.Adapters()); }
            }
            public bool IsExecutablePresent(string path) { RefuseNativeInTests(); return File.Exists(path); }
            public bool TryGetStagedOriginal(string path, out bool staged, out string original)
            { RefuseNativeInTests(); return GpuPrefStage.TryGetStagedOriginal(path, out staged, out original); }
            public bool TryReleaseStage(string path)
            { RefuseNativeInTests(); return GpuPrefStage.TryReleaseForManualPreference(path); }
            public bool TryRead(string path, out string value)
            {
                RefuseNativeInTests(); value = null;
                try
                {
                    using (RegistryKey key = Registry.CurrentUser.OpenSubKey(GpuKey))
                        return Read(key, path, out value);
                }
                catch { return false; }
            }
            private static bool Read(RegistryKey key, string path, out string value)
            {
                value = null;
                if (key == null) return true;
                object raw = key.GetValue(path, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                if (raw == null) return true;
                if (!(raw is string) || key.GetValueKind(path) != RegistryValueKind.String) return false;
                value = (string)raw;
                return true;
            }
            public AppGpuPreferenceWriteResult CompareExchange(string path, string expected, string replacement)
            {
                RefuseNativeInTests();
                bool issued = false;
                try
                {
                    using (RegistryKey key = Registry.CurrentUser.CreateSubKey(GpuKey))
                    {
                        if (key == null) return AppGpuPreferenceWriteResult.NotIssued;
                        string current;
                        if (!Read(key, path, out current)) return AppGpuPreferenceWriteResult.NotIssued;
                        if (!string.Equals(current, expected, StringComparison.Ordinal)) return AppGpuPreferenceWriteResult.Changed;
                        // 注册表没有条件写值的接口 本进程共用预置闸
                        // 靠一次新鲜比对加上调用方的回读来发现变化
                        issued = true;
                        if (replacement == null) key.DeleteValue(path, false);
                        else key.SetValue(path, replacement, RegistryValueKind.String);
                        return AppGpuPreferenceWriteResult.Written;
                    }
                }
                catch { return issued ? AppGpuPreferenceWriteResult.Unknown : AppGpuPreferenceWriteResult.NotIssued; }
            }
            private static void RefuseNativeInTests()
            {
#if PAVISE_SELFTEST || PAVISE_PERFLAB
                throw new InvalidOperationException("App GPU preference native access requires an injected test double.");
#endif
            }
        }
    }

    internal sealed class AppGpuPreferenceManager
    {
        private sealed class Record
        {
            internal char Phase;
            internal string Path, Name, Original, RestoreTarget;
            internal bool Receipt;
        }
        private readonly IAppGpuPreferenceControl control;
        private readonly IAppGpuPreferenceLedger ledger;
        private List<Record> records = new List<Record>();
        private string observedLedger, attemptedLedger;
        private bool loaded, dirty, busy;
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        internal AppGpuPreferenceManager(IAppGpuPreferenceControl control, IAppGpuPreferenceLedger ledger)
        {
            if (control == null || ledger == null) throw new ArgumentNullException();
            this.control = control; this.ledger = ledger;
        }
        public bool Supported { get { try { return control.Supported; } catch { return false; } } }
        public bool HasResidue
        { get { lock (GpuPrefStage.MutationGate) return !Load() || records.Count != 0 || dirty; } }
        public bool HasManagedPath(string path)
        { lock (GpuPrefStage.MutationGate) return !Load() || Find(path) != null; }

        public AppGpuPreferenceResult Prepare(string exePath, out AppGpuPreferenceChange change)
        {
            change = null;
            lock (GpuPrefStage.MutationGate)
            {
                if (busy) return AppGpuPreferenceResult.Busy;
                busy = true;
                try
                {
                    string path;
                    if (!NormalizePath(exePath, out path)) return AppGpuPreferenceResult.InvalidPath;
                    if (!Load()) return AppGpuPreferenceResult.JournalFailed;
                    if (Find(path) != null) return AppGpuPreferenceResult.AlreadyPresent;
                    if (records.Count >= 128) return AppGpuPreferenceResult.JournalFailed;
                    if (!Supported) return AppGpuPreferenceResult.Unsupported;
                    if (!control.IsExecutablePresent(path)) return AppGpuPreferenceResult.InvalidPath;
                    string current, stageOriginal; bool staged;
                    if (!Read(path, out current)) return AppGpuPreferenceResult.ReadFailed;
                    if (!control.TryGetStagedOriginal(path, out staged, out stageOriginal)) return AppGpuPreferenceResult.Busy;
                    string baseline = staged ? RestoreText(current, stageOriginal) : current;
                    if (!ValidPreference(baseline)) return AppGpuPreferenceResult.ReadFailed;
                    change = new AppGpuPreferenceChange(this, path, current, baseline, staged, stageOriginal);
                    return AppGpuPreferenceResult.Success;
                }
                catch { return AppGpuPreferenceResult.ReadFailed; }
                finally { busy = false; }
            }
        }

        public AppGpuPreferenceResult Apply(AppGpuPreferenceChange change, bool confirmExisting)
        {
            lock (GpuPrefStage.MutationGate)
            {
                if (busy) return AppGpuPreferenceResult.Busy;
                busy = true;
                try
                {
                    if (change == null || !ReferenceEquals(change.Owner, this)) return AppGpuPreferenceResult.Changed;
                    if (!Load()) return AppGpuPreferenceResult.JournalFailed;
                    if (Find(change.ExePath) != null) return AppGpuPreferenceResult.AlreadyPresent;
                    if (records.Count >= 128) return AppGpuPreferenceResult.JournalFailed;
                    if (change.NeedsConfirmation && !confirmExisting) return AppGpuPreferenceResult.NeedsConfirmation;
                    if (!Supported) return AppGpuPreferenceResult.Unsupported;
                    if (!control.IsExecutablePresent(change.ExePath)) return AppGpuPreferenceResult.InvalidPath;
                    string current, original; bool staged;
                    if (!Read(change.ExePath, out current)) return AppGpuPreferenceResult.ReadFailed;
                    if (!string.Equals(current, change.CurrentRaw, StringComparison.Ordinal)) return AppGpuPreferenceResult.Changed;
                    if (!control.TryGetStagedOriginal(change.ExePath, out staged, out original)) return AppGpuPreferenceResult.Busy;
                    if (staged != change.Staged || (staged && original != change.StageOriginal)) return AppGpuPreferenceResult.Changed;
                    if (!control.TryReleaseStage(change.ExePath)) return AppGpuPreferenceResult.Busy;
                    if (!Read(change.ExePath, out current)) return AppGpuPreferenceResult.ReadFailed;
                    if (!string.Equals(current, change.BaselineRaw, StringComparison.Ordinal)) return AppGpuPreferenceResult.Changed;
                    var record = new Record { Path = change.ExePath, Name = change.Name,
                        Original = current, Phase = change.AlreadyLowPower ? 'U' : 'P' };
                    records.Add(record);
                    if (!Save()) return AppGpuPreferenceResult.JournalFailed;
                    if (record.Phase == 'U') return AppGpuPreferenceResult.Success;
                    string wanted = PrefFieldText.MergeField(current, "GpuPreference", "1");
                    AppGpuPreferenceWriteResult written = Write(record.Path, current, wanted);
                    if (written == AppGpuPreferenceWriteResult.NotIssued || written == AppGpuPreferenceWriteResult.Changed)
                    {
                        if (!Settle(record)) return AppGpuPreferenceResult.JournalFailed;
                        return written == AppGpuPreferenceWriteResult.Changed ? AppGpuPreferenceResult.Changed : AppGpuPreferenceResult.WriteFailed;
                    }
                    if (written != AppGpuPreferenceWriteResult.Written) return AppGpuPreferenceResult.RecoveryPending;
                    record.Receipt = true;
                    if (!Read(record.Path, out current)) return AppGpuPreferenceResult.ReadFailed;
                    if (PrefFieldText.ReadField(current, "GpuPreference") != "1")
                    {
                        record.Phase = 'E';
                        return Save() ? AppGpuPreferenceResult.Changed : AppGpuPreferenceResult.JournalFailed;
                    }
                    record.Phase = 'O';
                    return Save() ? AppGpuPreferenceResult.Success : AppGpuPreferenceResult.JournalFailed;
                }
                catch { return AppGpuPreferenceResult.RecoveryPending; }
                finally { busy = false; }
            }
        }

        public bool TryGetEntries(out List<AppGpuPreferenceEntry> entries)
        {
            entries = new List<AppGpuPreferenceEntry>();
            lock (GpuPrefStage.MutationGate)
            {
                if (busy || !Load()) return false;
                busy = true;
                try
                {
                    bool changed = false;
                    foreach (Record record in records)
                    {
                        string current;
                        bool read = Read(record.Path, out current);
                        if (read && (record.Phase == 'O' || record.Phase == 'U')
                            && PrefFieldText.ReadField(current, "GpuPreference") != "1")
                        { record.Phase = 'E'; changed = true; }
                        AppGpuPreferenceState state = State(record);
                        entries.Add(new AppGpuPreferenceEntry(record.Path, record.Name, state, current, !read));
                    }
                    return !changed || Save();
                }
                finally { busy = false; }
            }
        }

        public AppGpuPreferenceResult Remove(string exePath)
        {
            lock (GpuPrefStage.MutationGate)
            {
                if (busy) return AppGpuPreferenceResult.Busy;
                busy = true;
                try
                {
                    if (!Load()) return AppGpuPreferenceResult.JournalFailed;
                    Record record = Find(exePath);
                    return record == null ? AppGpuPreferenceResult.NotFound : RemoveRecord(record);
                }
                finally { busy = false; }
            }
        }

        public AppGpuPreferenceResult Forget(string exePath)
        {
            lock (GpuPrefStage.MutationGate)
            {
                if (busy) return AppGpuPreferenceResult.Busy;
                busy = true;
                try
                {
                    if (!Load()) return AppGpuPreferenceResult.JournalFailed;
                    Record record = Find(exePath);
                    if (record == null) return AppGpuPreferenceResult.NotFound;
                    if (record.Phase != 'P' && record.Phase != 'R' && record.Phase != 'E') return AppGpuPreferenceResult.Busy;
                    return Settle(record) ? AppGpuPreferenceResult.Success : AppGpuPreferenceResult.JournalFailed;
                }
                finally { busy = false; }
            }
        }

        public bool RestoreAll()
        {
            lock (GpuPrefStage.MutationGate)
            {
                if (busy) return false;
                busy = true;
                try
                {
                    if (!Load()) return false;
                    bool ok = true;
                    foreach (Record record in new List<Record>(records))
                        if (RemoveRecord(record) != AppGpuPreferenceResult.Success) ok = false;
                    return ok && records.Count == 0 && !dirty;
                }
                finally { busy = false; }
            }
        }

        // 仅供整体重置 崩溃窗口留下的 P(无收据)/R 记录 当前值已被外部改写为高性能
        //   RemoveRecord 对它们永远返回 RecoveryPending 清除全部配置会被无限期拦住
        //   用户已明确要求清空全部数据时 按界面"仅移除记录"的语义结清 保留系统现状并留日志
        //   可认领的 O/U 记录与瞬时失败(读写失败 台账失败)不放弃
        public bool AbandonUnprovableForReset()
        {
            lock (GpuPrefStage.MutationGate)
            {
                if (busy) return false;
                busy = true;
                try
                {
                    if (!Load()) return false;
                    foreach (Record record in new List<Record>(records))
                    {
                        if (record.Phase != 'R' && (record.Phase != 'P' || record.Receipt)) continue;
                        string current;
                        if (!Read(record.Path, out current)) continue;
                        if (PrefFieldText.ReadField(current, "GpuPreference") != "1"
                            || (record.Phase == 'P' && current == record.Original)
                            || (record.Phase == 'R' && current == record.RestoreTarget)) continue;
                        Logger.Log(Lang.T("log.appgpuabandon.1") + record.Name + Lang.T("log.appgpuabandon.2"));
                        if (!Settle(record)) return false;
                    }
                    return records.Count == 0 && !dirty;
                }
                finally { busy = false; }
            }
        }

        // 启动时只做收据对账和清理 正常的 O 和 U 记录保持不动
        // 这里绝不重新施加节能偏好 也不撤销用户长期的选择
        public bool HealFromCrash()
        {
            lock (GpuPrefStage.MutationGate)
            {
                if (busy) return false;
                busy = true;
                try
                {
                    if (!Load()) return false;
                    bool ok = true, changed = false;
                    foreach (Record record in new List<Record>(records))
                    {
                        if (record.Phase == 'S') { if (!Settle(record)) ok = false; continue; }
                        string current;
                        if (!Read(record.Path, out current)) { ok = false; continue; }
                        string preference = PrefFieldText.ReadField(current, "GpuPreference");
                        if (record.Phase == 'P')
                        {
                            if (record.Receipt && preference == "1") { record.Phase = 'O'; changed = true; }
                            else if (current == record.Original || preference != "1")
                            { if (!Settle(record)) ok = false; }
                            else ok = false;
                        }
                        else if (record.Phase == 'R')
                        {
                            if (current == record.RestoreTarget || preference != "1")
                            { if (!Settle(record)) ok = false; }
                            else ok = false;
                        }
                        else if ((record.Phase == 'O' || record.Phase == 'U') && preference != "1")
                        { record.Phase = 'E'; changed = true; }
                    }
                    if ((changed || dirty) && !Save()) ok = false;
                    return ok;
                }
                finally { busy = false; }
            }
        }

        private AppGpuPreferenceResult RemoveRecord(Record record)
        {
            if (record.Phase == 'U' || record.Phase == 'E' || record.Phase == 'S')
                return Settle(record) ? AppGpuPreferenceResult.Success : AppGpuPreferenceResult.JournalFailed;
            string current;
            if (!Read(record.Path, out current)) return AppGpuPreferenceResult.ReadFailed;
            string preference = PrefFieldText.ReadField(current, "GpuPreference");
            if (preference != "1" || (record.Phase == 'P' && current == record.Original)
                || (record.Phase == 'R' && current == record.RestoreTarget))
                return Settle(record) ? AppGpuPreferenceResult.Success : AppGpuPreferenceResult.JournalFailed;
            if (record.Phase == 'R' || (record.Phase == 'P' && !record.Receipt))
                return AppGpuPreferenceResult.RecoveryPending;
            // 只改我们自己那个字段 现有的 WindowedOpt AutoHDR 和未知字段都保留
            string restored = RestoreText(current, record.Original);
            record.RestoreTarget = restored;
            record.Phase = 'R';
            if (!Save())
            {
                // 这个实例知道自己没发出过还原写入 重新加载出来的 R
                // 没法这么假设 但当前活着的调用方可以安全重试
                record.Phase = 'O'; record.RestoreTarget = null;
                return AppGpuPreferenceResult.JournalFailed;
            }
            AppGpuPreferenceWriteResult written = Write(record.Path, current, restored);
            if (written == AppGpuPreferenceWriteResult.NotIssued)
            {
                record.Phase = 'O'; record.RestoreTarget = null;
                return Save() ? AppGpuPreferenceResult.WriteFailed : AppGpuPreferenceResult.JournalFailed;
            }
            if (written == AppGpuPreferenceWriteResult.Changed)
            {
                if (!Read(record.Path, out current)) return AppGpuPreferenceResult.ReadFailed;
                if (PrefFieldText.ReadField(current, "GpuPreference") != "1")
                    return Settle(record) ? AppGpuPreferenceResult.Success : AppGpuPreferenceResult.JournalFailed;
                // 变的只是另一个共享字符串字段 保住所有权 让重试
                // 能在不丢那处编辑的前提下还原显卡偏好
                record.Phase = 'O'; record.RestoreTarget = null;
                return Save() ? AppGpuPreferenceResult.Changed : AppGpuPreferenceResult.JournalFailed;
            }
            if (written != AppGpuPreferenceWriteResult.Written) return AppGpuPreferenceResult.RecoveryPending;
            if (!Read(record.Path, out current)) return AppGpuPreferenceResult.ReadFailed;
            if (PrefFieldText.ReadField(current, "GpuPreference") == "1") return AppGpuPreferenceResult.RecoveryPending;
            return Settle(record) ? AppGpuPreferenceResult.Success : AppGpuPreferenceResult.JournalFailed;
        }

        private bool Settle(Record record)
        {
            record.Phase = 'S'; record.Receipt = false; record.RestoreTarget = null;
            if (!Save()) return false;
            int index = records.IndexOf(record);
            records.Remove(record);
            if (Save()) return true;
            // 清理失败之后 墓碑在磁盘和内存里都要留一份
            records.Insert(Math.Min(index, records.Count), record);
            dirty = true;
            return false;
        }

        private bool Read(string path, out string value)
        {
            value = null;
            try { return control.TryRead(path, out value) && ValidPreference(value); }
            catch { return false; }
        }
        private AppGpuPreferenceWriteResult Write(string path, string expected, string replacement)
        {
            try { return control.CompareExchange(path, expected, replacement); }
            catch { return AppGpuPreferenceWriteResult.Unknown; }
        }
        private Record Find(string path)
        {
            foreach (Record record in records)
                if (string.Equals(record.Path, path, StringComparison.OrdinalIgnoreCase)) return record;
            return null;
        }
        private static AppGpuPreferenceState State(Record record)
        {
            switch (record.Phase)
            {
                case 'O': return AppGpuPreferenceState.Owned;
                case 'U': return AppGpuPreferenceState.AlreadyLowPower;
                case 'E': return AppGpuPreferenceState.ExternalChange;
                case 'R': return AppGpuPreferenceState.PendingRestore;
                case 'S': return AppGpuPreferenceState.CleanupPending;
                default: return AppGpuPreferenceState.PendingApply;
            }
        }
        private static string RestoreText(string current, string original)
        {
            string result = PrefFieldText.RestoreField(current, original, "GpuPreference");
            return result.Length == 0 && original == null ? null : result;
        }
        private static bool NormalizePath(string input, out string path)
        {
            path = null;
            if (string.IsNullOrWhiteSpace(input) || input.Length > 32760 || input.IndexOf('\0') >= 0) return false;
            try
            {
                bool drive = input.Length >= 3 && char.IsLetter(input[0]) && input[1] == ':'
                    && (input[2] == '\\' || input[2] == '/');
                bool unc = input.StartsWith(@"\\", StringComparison.Ordinal)
                    && !input.StartsWith(@"\\.\", StringComparison.Ordinal) && !input.StartsWith(@"\\?\", StringComparison.Ordinal);
                if ((!drive && !unc) || !string.Equals(Path.GetExtension(input), ".exe", StringComparison.OrdinalIgnoreCase)) return false;
                path = Path.GetFullPath(input);
                return path.Length > 3 && path.IndexOf(':', 2) < 0 && Path.GetFileName(path).Length <= 260;
            }
            catch { return false; }
        }
        private static bool ValidPreference(string raw)
        {
            if (raw == null) return true;
            if (raw.Length > 16384 || raw.IndexOf('\0') >= 0) return false;
            int count = 0;
            foreach (string token in raw.Split(';'))
            {
                int equal = token.IndexOf('=');
                if (equal > 0 && string.Equals(token.Substring(0, equal).Trim(), "GpuPreference", StringComparison.OrdinalIgnoreCase))
                    if (++count > 1) return false;
            }
            return true;
        }

        private bool Load()
        {
            string raw;
            try { if (!ledger.TryRead(out raw) || raw == null) return false; }
            catch { return false; }
            if (loaded && raw == observedLedger) return true;
            if (loaded && attemptedLedger != null && raw == attemptedLedger)
            { observedLedger = raw; dirty = false; attemptedLedger = null; return true; }
            if (loaded && dirty) return false;
            List<Record> decoded;
            if (!Decode(raw, out decoded)) return false;
            records = decoded; observedLedger = raw; attemptedLedger = null; loaded = true; dirty = false;
            return true;
        }
        private bool Save()
        {
            string raw = Encode(records);
            attemptedLedger = raw; dirty = true;
            try { ledger.TryWrite(raw); } catch { }
            string observed;
            try
            {
                if (!ledger.TryRead(out observed) || observed != raw) return false;
                observedLedger = observed; attemptedLedger = null; loaded = true; dirty = false;
                return true;
            }
            catch { return false; }
        }
        private static string Encode(List<Record> items)
        {
            if (items.Count == 0) return "";
            var text = new StringBuilder("1\n");
            foreach (Record record in items)
                text.Append(record.Phase).Append('\t').Append(B64(record.Path)).Append('\t').Append(B64(record.Name))
                    .Append('\t').Append(B64(record.Original)).Append('\t').Append(B64(record.RestoreTarget)).Append('\n');
            return text.ToString();
        }
        private static bool Decode(string raw, out List<Record> items)
        {
            items = new List<Record>();
            if (raw.Length == 0) return true;
            if (raw.Length > 4194304 || !raw.StartsWith("1\n", StringComparison.Ordinal)) return false;
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string[] lines = raw.Split('\n');
            if (lines.Length < 3 || lines[lines.Length - 1] != "" || lines.Length > 130) return false;
            try
            {
                for (int i = 1; i < lines.Length - 1; i++)
                {
                    string[] columns = lines[i].Split('\t');
                    if (columns.Length != 5 || columns[0].Length != 1 || "UOPRES".IndexOf(columns[0][0]) < 0) return false;
                    string path = UnB64(columns[1]), canonical;
                    string name = UnB64(columns[2]), original = UnB64(columns[3]), target = UnB64(columns[4]);
                    if (!NormalizePath(path, out canonical) || !string.Equals(path, canonical, StringComparison.OrdinalIgnoreCase)
                        || !paths.Add(path) || name == null || name.Length > 260 || !ValidPreference(original) || !ValidPreference(target)) return false;
                    char phase = columns[0][0];
                    string preference = PrefFieldText.ReadField(original, "GpuPreference");
                    if ((phase == 'U' && preference != "1") || ((phase == 'O' || phase == 'P' || phase == 'R') && preference == "1")) return false;
                    if (phase != 'R' && target != null) return false;
                    if (phase == 'R' && PrefFieldText.ReadField(target, "GpuPreference") != preference) return false;
                    items.Add(new Record { Path = path, Name = name, Original = original, RestoreTarget = target,
                        Phase = phase, Receipt = phase == 'O' });
                }
                return true;
            }
            catch { items.Clear(); return false; }
        }
        private static string B64(string value) { return value == null ? "-" : Convert.ToBase64String(StrictUtf8.GetBytes(value)); }
        private static string UnB64(string value) { return value == "-" ? null : StrictUtf8.GetString(Convert.FromBase64String(value)); }
    }
}
