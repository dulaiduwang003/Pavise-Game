using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PaviseApp
{
    internal sealed class ClockRepairReceipt
    {
        internal string Phase, EntryId, Boot;
        internal int Attempted;
        internal readonly List<KeyValuePair<uint, ulong>> Values = new List<KeyValuePair<uint, ulong>>();
        internal string Encode()
        {
            var fields = new List<string> { "clock1", Phase, new Guid(EntryId).ToString("N"),
                Convert.ToBase64String(Encoding.UTF8.GetBytes(Boot)), Attempted.ToString(CultureInfo.InvariantCulture), Values.Count.ToString(CultureInfo.InvariantCulture) };
            foreach (var value in Values) { fields.Add(value.Key.ToString("X8", CultureInfo.InvariantCulture)); fields.Add(value.Value.ToString(CultureInfo.InvariantCulture)); }
            return string.Join("|", fields.ToArray());
        }
        internal static bool TryParse(string raw, out ClockRepairReceipt result)
        {
            result = null;
            try
            {
                if (string.IsNullOrEmpty(raw) || raw.Length > 2048) return false;
                string[] parts = raw.Split('|'); Guid entry; int count, attempted;
                if (parts.Length < 8 || parts[0] != "clock1" || (parts[1] != "intent" && parts[1] != "applied" && parts[1] != "restoring")
                    || !Guid.TryParseExact(parts[2], "N", out entry) || entry == Guid.Empty
                    || !int.TryParse(parts[4], NumberStyles.None, CultureInfo.InvariantCulture, out attempted)
                    || !int.TryParse(parts[5], NumberStyles.None, CultureInfo.InvariantCulture, out count)
                    || count < 1 || count > 3 || attempted < 0 || attempted > count || parts.Length != 6 + count * 2
                    || (parts[1] == "applied" && attempted != count)) return false;
                string boot = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(parts[3]));
                if (!BootSessionIdentity.Valid(boot)) return false;
                var receipt = new ClockRepairReceipt { Phase = parts[1], EntryId = entry.ToString("B"), Boot = boot, Attempted = attempted };
                var seen = new HashSet<uint>();
                for (int i = 0; i < count; i++)
                {
                    uint type; ulong number;
                    if (!uint.TryParse(parts[6 + i * 2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out type)
                        || !ulong.TryParse(parts[7 + i * 2], NumberStyles.None, CultureInfo.InvariantCulture, out number)
                        || !ClockRepairNative.Forced(type, number) || !seen.Add(type)) return false;
                    receipt.Values.Add(new KeyValuePair<uint, ulong>(type, number));
                }
                result = receipt; return true;
            }
            catch { return false; }
        }
    }

    internal sealed class ClockRepairController
    {
        private readonly object gate = new object();
        private readonly IClockRepairNative native;
        private readonly ILaboratoryManualStore store;
        private readonly Func<bool> conflictingOwner;
        internal ClockRepairController(IClockRepairNative native, ILaboratoryManualStore store, Func<bool> conflictingOwner)
        { this.native = native; this.store = store; this.conflictingOwner = conflictingOwner; }
        private static string T(string zh, string en, string ja) { return LaboratoryActionText.Pick(zh, en, ja); }
        private static string BadReceipt { get { return T("计时恢复记录不可读或损坏，已保留记录", "Clock recovery record is unreadable or damaged; retained", "時刻復元記録を読み取れないか破損しています。記録は保持します"); } }
        internal bool HasReceipt { get { lock (gate) { string raw; return !store.Read(out raw) || !string.IsNullOrEmpty(raw); } } }
        internal bool HasUnfinishedRecovery
        {
            get { lock (gate) { string raw; ClockRepairReceipt receipt; if (!store.Read(out raw)) return true;
                return !string.IsNullOrEmpty(raw) && (!ClockRepairReceipt.TryParse(raw, out receipt) || receipt.Phase != "applied"); } }
        }
        private bool ReadReceipt(out ClockRepairReceipt receipt, out string error)
        {
            receipt = null; error = ""; string raw;
            if (!store.Read(out raw) || (!string.IsNullOrEmpty(raw) && !ClockRepairReceipt.TryParse(raw, out receipt))) { error = BadReceipt; return false; }
            return true;
        }
        private bool Current(ClockRepairReceipt receipt, out ClockRepairSnapshot snapshot, out string error)
        {
            if (!native.Read(out snapshot, out error)) return false;
            Guid current, saved;
            if (snapshot == null || !Guid.TryParse(snapshot.EntryId, out current) || current == Guid.Empty
                || (receipt != null && (!Guid.TryParse(receipt.EntryId, out saved) || current != saved)))
            { error = T("当前启动项已变化；未修改其他启动项", "The current boot entry changed; no other entry was modified", "現在の起動項目が変更されています。他の項目は変更しません"); return false; }
            return true;
        }
        internal LaboratoryManualState Inspect()
        {
            lock (gate)
            {
                ClockRepairReceipt receipt; string error;
                if (!ReadReceipt(out receipt, out error)) return new LaboratoryManualState { CanRestore = true, Failure = error, Reason = error };
                if (receipt != null && conflictingOwner()) return new LaboratoryManualState { Reason = T("请先关闭计时器恒定节拍并恢复其配置；原计时记录已保留", "Turn off Constant timer tick and restore its configuration first; original clock record retained", "先に固定タイマーティックをオフにして復元してください。元の時刻記録は保持します") };
                ClockRepairSnapshot snapshot;
                if (!Current(receipt, out snapshot, out error)) return new LaboratoryManualState { CanRestore = receipt != null, Failure = error, Reason = error };
                if (receipt != null)
                {
                    bool intact = true;
                    foreach (var item in receipt.Values) if (snapshot.Values.ContainsKey(item.Key)) intact = false;
                    bool restart = string.Equals(receipt.Boot, snapshot.BootIdentity, StringComparison.Ordinal);
                    return new LaboratoryManualState { Applied = receipt.Phase == "applied" && intact, CanRestore = true,
                        RestartRequired = receipt.Phase == "applied" && (restart || !BootSessionIdentity.Valid(snapshot.BootIdentity)),
                        Summary = receipt.Phase != "applied" ? T("计时配置恢复尚未完成", "Clock configuration recovery is pending", "時刻設定の復元が未完了です")
                            : !intact ? T("检测到外部计时配置变化", "External clock configuration changes detected", "外部による時刻設定の変更を検出しました")
                            : !BootSessionIdentity.Valid(snapshot.BootIdentity) ? T("配置已修复 · 尚未核验电脑重启", "Configuration repaired · PC restart not yet verified", "設定を修復しました・PC再起動は未確認です")
                            : restart ? T("配置已修复 · 重启电脑生效", "Configuration repaired · PC restart required", "設定を修復しました・PCの再起動が必要です")
                            : T("计时配置已修复", "Clock configuration repaired", "時刻設定を修復済みです"),
                        Reason = receipt.Phase != "applied" || !intact ? T("请先恢复原配置；不会覆盖其他工具的修改", "Restore the original configuration first; external edits are preserved", "元の設定を先に復元してください。外部変更は保持します") : "" };
                }
                if (conflictingOwner()) return new LaboratoryManualState { Reason = T("请先关闭计时器恒定节拍并恢复其配置", "Turn off Constant timer tick and restore its configuration first", "先に固定タイマーティックをオフにして設定を復元してください") };
                if (!BootSessionIdentity.Valid(snapshot.BootIdentity)) return new LaboratoryManualState { Reason = T("无法核验本次启动身份", "The current boot identity could not be verified", "現在の起動識別情報を確認できません") };
                foreach (var value in snapshot.Values) if (ClockRepairNative.Forced(value.Key, value.Value))
                    return new LaboratoryManualState { CanApply = true, Summary = T("发现强制平台计时或 TSC 同步覆盖", "Forced platform clock or TSC synchronization overrides found", "プラットフォーム時計またはTSC同期の強制設定を検出しました") };
                return new LaboratoryManualState { Reason = T("当前未发现待修复的强制计时配置", "No forced clock configuration needs repair", "修復が必要な強制時刻設定はありません") };
            }
        }
        internal bool Apply(out string error)
        {
            lock (gate)
            {
                ClockRepairReceipt existing;
                if (!ReadReceipt(out existing, out error)) return false;
                if (existing != null) { error = T("请先恢复已有计时配置记录", "Restore the existing clock configuration record first", "既存の時刻設定記録を先に復元してください"); return false; }
                if (conflictingOwner()) { error = T("计时器恒定节拍正在管理相关配置", "Constant timer tick already owns these settings", "固定タイマーティックが関連設定を管理しています"); return false; }
                ClockRepairSnapshot before;
                if (!Current(null, out before, out error)) return false;
                if (!BootSessionIdentity.Valid(before.BootIdentity)) { error = T("无法核验本次启动身份", "The current boot identity could not be verified", "現在の起動識別情報を確認できません"); return false; }
                var receipt = new ClockRepairReceipt { Phase = "intent", EntryId = before.EntryId, Boot = before.BootIdentity };
                foreach (uint type in ClockRepairNative.Types) { ulong number; if (before.Values.TryGetValue(type, out number) && ClockRepairNative.Forced(type, number)) receipt.Values.Add(new KeyValuePair<uint, ulong>(type, number)); }
                if (receipt.Values.Count == 0) { error = T("当前未发现待修复的强制计时配置", "No forced clock configuration needs repair", "修復が必要な強制時刻設定はありません"); return false; }
                if (!store.Write(receipt.Encode())) { error = T("无法持久保存原计时配置；未执行修改", "Original clock settings could not be saved; no changes made", "元の時刻設定を保存できません。変更していません"); return false; }
                for (int i = 0; i < receipt.Values.Count; i++)
                {
                    ClockRepairSnapshot current; ulong number;
                    if (conflictingOwner() || !Current(receipt, out current, out error)
                        || !current.Values.TryGetValue(receipt.Values[i].Key, out number) || number != receipt.Values[i].Value)
                    { if (string.IsNullOrEmpty(error)) error = T("计时配置已被其他控制方修改", "Clock settings changed under another controller", "他の制御元が時刻設定を変更しました"); return Rollback(error, out error); }
                    receipt.Attempted = i + 1;
                    if (!store.Write(receipt.Encode())) return Rollback(T("计时修改意图保存失败", "Clock change intent could not be saved", "時刻変更の意図を保存できません"), out error);
                    if (!native.Delete(receipt.EntryId, receipt.Values[i].Key, out error)) return Rollback(error, out error);
                    if (!Current(receipt, out current, out error) || current.Values.ContainsKey(receipt.Values[i].Key))
                    { if (string.IsNullOrEmpty(error)) error = T("计时修改未通过读回核验", "Clock change failed readback verification", "時刻変更の読み戻し確認に失敗しました"); return Rollback(error, out error); }
                }
                receipt.Phase = "applied";
                if (!store.Write(receipt.Encode())) return Rollback(T("计时完成记录保存失败", "Clock completion record could not be saved", "時刻変更の完了記録を保存できません"), out error);
                error = ""; return true;
            }
        }
        private bool Rollback(string failure, out string error)
        {
            string restoreError;
            bool restored = Restore(out restoreError);
            error = failure + (restored ? "" : " | " + restoreError);
            return false;
        }
        internal bool Restore(out string error)
        {
            lock (gate)
            {
                ClockRepairReceipt receipt;
                if (!ReadReceipt(out receipt, out error)) return false;
                if (receipt == null) return true;
                if (conflictingOwner()) { error = T("相关计时配置已有其他控制方，原记录保留且暂不覆盖", "Another controller owns the clock settings; original record retained without overwriting", "他の制御元が時刻設定を管理しています。上書きせず元の記録を保持します"); return false; }
                ClockRepairSnapshot snapshot;
                if (!Current(receipt, out snapshot, out error)) return false;
                receipt.Phase = "restoring";
                if (!store.Write(receipt.Encode())) { error = T("恢复意图保存失败，保留计时记录", "Restore intent could not be saved; clock record retained", "復元意図を保存できません。時刻記録を保持します"); return false; }
                for (int i = 0; i < receipt.Attempted; i++)
                {
                    var item = receipt.Values[i]; ulong actual;
                    if (!Current(receipt, out snapshot, out error)) return false;
                    if (snapshot.Values.TryGetValue(item.Key, out actual))
                    {
                        if (actual == item.Value) continue;
                        error = T("外部计时配置已变化，保留原记录且不覆盖", "External clock settings changed; original record retained without overwriting", "外部の時刻設定が変更されています。上書きせず元の記録を保持します"); return false;
                    }
                    if (!native.Set(receipt.EntryId, item.Key, item.Value, out error)) return false;
                    if (!Current(receipt, out snapshot, out error) || !snapshot.Values.TryGetValue(item.Key, out actual) || actual != item.Value)
                    { if (string.IsNullOrEmpty(error)) error = T("计时恢复未通过读回核验", "Clock restore failed readback verification", "時刻復元の読み戻し確認に失敗しました"); return false; }
                }
                if (!store.Write("")) { error = T("计时已恢复但原记录清理失败", "Clock settings restored, but the record could not be cleared", "時刻設定は復元済みですが記録を消去できません"); return false; }
                error = ""; return true;
            }
        }
        internal bool RecoverInterrupted(out string error)
        {
            lock (gate)
            {
                ClockRepairReceipt receipt;
                if (!ReadReceipt(out receipt, out error)) return false;
                return receipt == null || receipt.Phase == "applied" || Restore(out error);
            }
        }
    }

    internal static class LaboratoryClockRepair
    {
        internal const string Key = "LabClockRepairV1";
        internal const string ReceiptKey = "LabClockRepairReceiptV1";
        private static readonly ClockRepairController controller = new ClockRepairController(new ClockRepairNative(),
            new LaboratoryManualSettingsStore(ReceiptKey), delegate { return Settings.Load("AutoTimerTick", false) || TimerTickTweak.OwnsState; });
        internal static bool HasReceipt { get { return controller.HasReceipt; } }
        internal static bool HasUnfinishedRecovery { get { return controller.HasUnfinishedRecovery; } }
        internal static LaboratoryManualState Inspect() { return controller.Inspect(); }
        internal static bool Apply(out string error) { return controller.Apply(out error); }
        internal static bool Restore(out string error) { return controller.Restore(out error); }
        internal static bool RecoverInterrupted(out string error) { return controller.RecoverInterrupted(out error); }
    }
}
