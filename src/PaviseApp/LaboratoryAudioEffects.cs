using System;
using System.Globalization;
using System.Text;

namespace PaviseApp
{
    internal enum AudioEffectsProperty { Empty = 0, Enabled = 1, Disabled = 2 }

    internal sealed class AudioEffectsEndpoint
    {
        internal string Id = "", Name = "", EffectIdentity = "";
        internal AudioEffectsProperty Property;
        internal bool Active, LegacyEffects, ModernEffects;
        internal bool HasRegisteredEffects { get { return LegacyEffects || ModernEffects; } }
    }

    internal interface IAudioEffectsNative
    {
        bool DefaultEndpoint(out AudioEffectsEndpoint endpoint, out string error);
        bool Read(string id, out AudioEffectsEndpoint endpoint, out string error);
        bool Write(string id, AudioEffectsProperty property, out string error);
    }

    // Manual endpoint transactions restore on exit/startup under the caller's serialization gate.
    internal sealed class LaboratoryAudioEffectsController
    {
        private readonly object sync = new object();
        private readonly IAudioEffectsNative native;
        private readonly ILaboratoryManualStore store;
        private sealed class Receipt
        {
            internal string Endpoint, Name, Effects, Phase;
            internal AudioEffectsProperty Original;
        }
        internal LaboratoryAudioEffectsController(IAudioEffectsNative native, ILaboratoryManualStore store)
        { this.native = native; this.store = store; }

        private static string T(string zh, string en, string ja) { return LaboratoryActionText.Pick(zh, en, ja); }
        internal bool HasReceipt { get { lock (sync) { string raw; return !store.Read(out raw) || !string.IsNullOrEmpty(raw); } } }

        internal LaboratoryManualState Inspect()
        {
            lock (sync)
            {
                string raw, error; AudioEffectsEndpoint endpoint;
                if (!store.Read(out raw)) return Blocked(T("无法读取音频效果恢复记录", "Audio-effects recovery record is unreadable", "音声効果の復元記録を読み取れません"), true);
                if (!string.IsNullOrEmpty(raw))
                {
                    Receipt receipt;
                    if (!Decode(raw, out receipt)) return Blocked(T("音频效果恢复记录异常，请保留记录", "Audio-effects recovery record is invalid; retain it", "音声効果の復元記録が無効です。記録を保持してください"), true);
                    if (!native.Read(receipt.Endpoint, out endpoint, out error)) return Blocked(error, true);
                    if (endpoint.EffectIdentity != receipt.Effects) return Blocked(T("音效配置已变化，保留恢复记录", "Effect configuration changed; recovery record retained", "効果設定が変わりました。復元記録を保持します"), true);
                    return new LaboratoryManualState { CanRestore = true, Applied = endpoint.Property == AudioEffectsProperty.Disabled,
                        Summary = endpoint.Property == AudioEffectsProperty.Disabled
                            ? T("已请求旁路系统效果 · ", "System-effects bypass requested · ", "システム効果のバイパスを要求済み · ") + endpoint.Name
                            : T("设置已变化，可清理本次恢复记录", "Setting changed; this recovery record can be cleared", "設定が変わりました。この復元記録を整理できます") };
                }
                if (!native.DefaultEndpoint(out endpoint, out error)) return Blocked(error, false);
                if (!ValidEndpoint(endpoint)) return Blocked(T("输出设备身份不完整，无法记录原值", "Output identity is incomplete; originals cannot be recorded", "出力デバイスの識別情報が不完全なため、元の値を記録できません"), false);
                if (!endpoint.Active) return Blocked(T("默认输出设备未就绪", "Default output endpoint is not ready", "既定の出力デバイスが準備できていません"), false);
                if (endpoint.Property == AudioEffectsProperty.Disabled) return new LaboratoryManualState { Summary = T("当前输出已禁用系统效果，无需重复应用", "System effects are already disabled on this output", "この出力ではシステム効果が既に無効です") };
                if (!endpoint.HasRegisteredEffects) return Blocked(T("当前输出未发现可请求旁路的系统效果", "No registered system effects found on this output", "この出力にバイパス要求の対象となるシステム効果が見つかりません"), false);
                return new LaboratoryManualState { CanApply = true, Summary = endpoint.Name,
                    Reason = T("仅请求旁路当前输出的受支持系统效果，实际音频收益待验证", "Requests bypass of supported effects on the current output; audio benefits are unverified", "現在の出力の対応効果のみをバイパス要求します。効果は未検証です") };
            }
        }

        private static LaboratoryManualState Blocked(string reason, bool restore)
        { return new LaboratoryManualState { CanRestore = restore, Reason = reason ?? "", Failure = restore ? reason ?? "" : "" }; }

        internal bool Apply(out string error)
        {
            lock (sync)
            {
                error = "";
                string raw; AudioEffectsEndpoint endpoint;
                if (!store.Read(out raw)) { error = T("无法读取恢复记录", "Cannot read recovery record", "復元記録を読み取れません"); return false; }
                if (!string.IsNullOrEmpty(raw))
                {
                    Receipt current; AudioEffectsEndpoint actual, currentDefault; string ignored;
                    if (!Decode(raw, out current)) { error = T("恢复记录异常，禁止新增写入", "Invalid recovery record; new writes blocked", "復元記録が無効なため、新たな書き込みを停止します"); return false; }
                    if (native.Read(current.Endpoint, out actual, out ignored) && actual.EffectIdentity == current.Effects
                        && actual.Property == AudioEffectsProperty.Disabled && native.DefaultEndpoint(out currentDefault, out ignored)
                        && currentDefault.Id == current.Endpoint) return true;
                    if (!RestoreInternal(out error)) return false;
                }
                if (!native.DefaultEndpoint(out endpoint, out error)) return false;
                if (!ValidEndpoint(endpoint)) { error = T("输出设备身份不完整，未修改", "Output identity is incomplete; not changed", "出力デバイスの識別情報が不完全なため、変更していません"); return false; }
                if (endpoint.Property == AudioEffectsProperty.Disabled) return true;
                if (!endpoint.Active || !endpoint.HasRegisteredEffects)
                { error = T("当前输出没有可核验的受支持系统效果", "Current output has no verifiable supported system effects", "現在の出力に検証可能な対応システム効果がありません"); return false; }
                string displayName = endpoint.Name ?? "";
                if (displayName.Length > 1024) displayName = displayName.Substring(0, 1024);
                var receipt = new Receipt { Endpoint = endpoint.Id, Name = displayName, Effects = endpoint.EffectIdentity, Original = endpoint.Property, Phase = "I" };
                if (!store.Write(Encode(receipt))) { error = T("恢复记录保存失败，未修改音频", "Recovery record save failed; audio was not changed", "復元記録を保存できないため、音声設定は変更していません"); return false; }
                AudioEffectsEndpoint before;
                if (!native.Read(receipt.Endpoint, out before, out error) || before.EffectIdentity != receipt.Effects || before.Property != receipt.Original)
                {
                    string reason = error;
                    if (!store.Write("")) error = T("设备设置变化，恢复记录待清理", "Endpoint changed; recovery record cleanup pending", "デバイス設定が変わりました。復元記録の整理が必要です");
                    else error = string.IsNullOrEmpty(reason) ? T("设备设置已变化，未应用", "Endpoint setting changed; not applied", "デバイス設定が変わったため、適用しませんでした") : reason;
                    return false;
                }
                bool written = native.Write(receipt.Endpoint, AudioEffectsProperty.Disabled, out error);
                AudioEffectsEndpoint after; string readError;
                bool confirmed = native.Read(receipt.Endpoint, out after, out readError) && after.EffectIdentity == receipt.Effects && after.Property == AudioEffectsProperty.Disabled;
                if (!written || !confirmed)
                {
                    string originalError = string.IsNullOrEmpty(error) ? readError : error;
                    string recoveryError;
                    if (!RestoreInternal(out recoveryError)) error = T("应用未确认，恢复尚未完成：", "Apply was not confirmed; recovery remains pending: ", "適用を確認できず、復元も未完了です: ") + recoveryError;
                    else error = string.IsNullOrEmpty(originalError) ? T("音频效果写后核验失败，已撤销", "Audio-effects readback failed; change reverted", "音声効果の書き込み後の確認に失敗し、変更を取り消しました") : originalError;
                    return false;
                }
                receipt.Phase = "A";
                if (!store.Write(Encode(receipt)))
                {
                    string recoveryError;
                    bool restored = RestoreInternal(out recoveryError);
                    error = restored ? T("应用记录保存失败，已恢复原值", "Applied record save failed; original restored", "適用記録を保存できず、元の設定に戻しました")
                        : T("应用记录保存失败，恢复待完成：", "Applied record save failed; recovery pending: ", "適用記録を保存できず、復元が未完了です: ") + recoveryError;
                    return false;
                }
                return true;
            }
        }

        internal bool Restore(out string error) { lock (sync) return RestoreInternal(out error); }
        private bool RestoreInternal(out string error)
        {
            error = ""; string raw;
            if (!store.Read(out raw)) { error = T("无法读取音频恢复记录", "Cannot read audio recovery record", "音声の復元記録を読み取れません"); return false; }
            if (string.IsNullOrEmpty(raw)) return true;
            Receipt receipt;
            if (!Decode(raw, out receipt)) { error = T("音频恢复记录异常，未覆盖设备设置", "Invalid audio recovery record; endpoint was not overwritten", "音声の復元記録が無効なため、設定を上書きしていません"); return false; }
            AudioEffectsEndpoint actual;
            if (!native.Read(receipt.Endpoint, out actual, out error)) return false;
            if (actual.EffectIdentity != receipt.Effects) { error = T("原设备音效配置已变化，保留恢复记录", "Original endpoint effects changed; record retained", "元のデバイスの効果設定が変わったため、記録を保持します"); return false; }
            if (actual.Property == AudioEffectsProperty.Disabled)
            {
                receipt.Phase = "R";
                if (!store.Write(Encode(receipt))) { error = T("无法保存恢复意图", "Cannot save restore intent", "復元要求を保存できません"); return false; }
                string writeError;
                native.Write(receipt.Endpoint, receipt.Original, out writeError);
                AudioEffectsEndpoint after; string readError;
                bool confirmed = native.Read(receipt.Endpoint, out after, out readError) && after.EffectIdentity == receipt.Effects && after.Property == receipt.Original;
                if (!confirmed)
                { error = !string.IsNullOrEmpty(writeError) ? writeError : !string.IsNullOrEmpty(readError) ? readError : T("原值尚未确认，保留恢复记录", "Original value is not confirmed; record retained", "元の値を確認できないため、復元記録を保持します"); return false; }
                // A backend may report an error after the value was committed. Confirmed
                // original readback, rather than the HRESULT alone, settles the debt.
            }
            // A different current value belongs to another writer; leave it unchanged.
            if (!store.Write("")) { error = T("原值已保留，恢复记录清理失败", "Original/external value retained; record cleanup failed", "元の値または外部設定を保持しましたが、記録を整理できません"); return false; }
            return true;
        }

        private static string B64(string value) { return Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? "")); }
        private static bool ValidEndpoint(AudioEffectsEndpoint endpoint)
        { return endpoint != null && !string.IsNullOrEmpty(endpoint.Id) && endpoint.Id.Length <= 2048 && endpoint.Id.IndexOf('\0') < 0
            && !string.IsNullOrEmpty(endpoint.EffectIdentity) && endpoint.EffectIdentity.Length <= 4096
            && (endpoint.Property == AudioEffectsProperty.Empty || endpoint.Property == AudioEffectsProperty.Enabled || endpoint.Property == AudioEffectsProperty.Disabled); }
        private static string Encode(Receipt receipt)
        { return "1;" + receipt.Phase + ";" + B64(receipt.Endpoint) + ";" + ((int)receipt.Original).ToString(CultureInfo.InvariantCulture) + ";" + B64(receipt.Effects) + ";" + B64(receipt.Name); }
        private static bool Decode(string raw, out Receipt receipt)
        {
            receipt = null;
            try
            {
                if (raw == null || raw.Length > 16384) return false;
                string[] parts = raw.Split(';'); int original;
                if (parts.Length != 6 || parts[0] != "1" || (parts[1] != "I" && parts[1] != "A" && parts[1] != "R")
                    || !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out original) || original < 0 || original > 1) return false;
                var utf8 = new UTF8Encoding(false, true);
                string endpoint = utf8.GetString(Convert.FromBase64String(parts[2]));
                string effects = utf8.GetString(Convert.FromBase64String(parts[4]));
                string name = utf8.GetString(Convert.FromBase64String(parts[5]));
                if (endpoint.Length == 0 || endpoint.Length > 2048 || endpoint.IndexOf('\0') >= 0 || effects.Length == 0 || effects.Length > 4096 || name.Length > 1024) return false;
                receipt = new Receipt { Endpoint = endpoint, Effects = effects, Name = name, Original = (AudioEffectsProperty)original, Phase = parts[1] };
                return true;
            }
            catch { return false; }
        }
    }

    internal static class LaboratoryAudioEffects
    {
        internal const string Key = "LabAudioEffectsV1";
        internal const string ReceiptKey = "LabAudioEffectsReceiptV1";
        private static readonly LaboratoryAudioEffectsController controller = new LaboratoryAudioEffectsController(CreateNative(), new LaboratoryManualSettingsStore(ReceiptKey));
        private static IAudioEffectsNative CreateNative()
        {
#if PAVISE_SELFTEST
            return new AudioEffectsUnavailableNative();
#else
            return new AudioEffectsNative();
#endif
        }
        internal static bool HasReceipt { get { return controller.HasReceipt; } }
        internal static LaboratoryManualState Inspect() { return controller.Inspect(); }
        internal static bool Apply(out string error) { return controller.Apply(out error); }
        internal static bool Restore(out string error) { return controller.Restore(out error); }
    }

    internal sealed class AudioEffectsUnavailableNative : IAudioEffectsNative
    {
        private static string Reason { get { return LaboratoryActionText.Pick("隔离测试未启用设备后端", "Device backend disabled for isolated tests", "分離テストではデバイスのバックエンドを無効にしています"); } }
        public bool DefaultEndpoint(out AudioEffectsEndpoint endpoint, out string error) { endpoint = null; error = Reason; return false; }
        public bool Read(string id, out AudioEffectsEndpoint endpoint, out string error) { endpoint = null; error = Reason; return false; }
        public bool Write(string id, AudioEffectsProperty property, out string error) { error = Reason; return false; }
    }
}
