using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace PaviseApp
{
    internal sealed class FileFilterCandidate
    {
        internal string VolumeGuid = "", VolumeDisplay = "", FilterName = "", InstanceName = "", Altitude = "";
        internal string DriverPath = "", DriverIdentity = "", Publisher = "", Description = "", BlockReason = "";
        internal bool Eligible;
        public override string ToString()
        { return VolumeDisplay + "  ·  " + FilterName + "  ·  " + InstanceName + (string.IsNullOrEmpty(Publisher) ? "" : "  ·  " + Publisher) + (Eligible ? "" : "  —  " + BlockReason); }
        internal FileFilterCandidate Copy() { return (FileFilterCandidate)MemberwiseClone(); }
        internal bool SameInstance(FileFilterCandidate other)
        { return other != null && Eq(VolumeGuid, other.VolumeGuid) && Eq(FilterName, other.FilterName)
            && Eq(InstanceName, other.InstanceName) && Altitude == other.Altitude; }
        internal bool SameDriver(FileFilterCandidate other)
        { return other != null && Eq(DriverPath, other.DriverPath) && Eq(DriverIdentity, other.DriverIdentity); }
        internal static bool Eq(string a, string b) { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
    }

    internal sealed class FileFilterObservation
    {
        internal bool VolumePresent, FilterLoaded, DriverMatches;
        internal FileFilterCandidate[] Instances = new FileFilterCandidate[0];
    }

    internal interface IFileFilterNative
    {
        FileFilterCandidate[] Enumerate(out string error);
        bool Observe(FileFilterCandidate expected, out FileFilterObservation observation, out string error);
        bool Detach(FileFilterCandidate target, out string error);
        bool Attach(FileFilterCandidate target, out string error);
    }

    internal static class FileFilterAdmission
    {
        internal static string ProtectedNameReason(string name)
        {
            switch ((name ?? "").ToLowerInvariant())
            {
                case "wdfilter": case "wdboot": case "wdnisdrv": return "Defender protection driver";
                case "bindflt": case "wcifs": case "luafv": case "npsvctrig": case "wof": case "fileinfo": case "storqosflt": case "ucpd": return "Windows system filter";
                case "cldflt": case "filecrypt": return "Encryption or cloud-file filter";
                case "messagetransfer": case "bedaisy": case "easyanticheat": case "easyanticheat_eos": case "vgk": case "faceit": case "ace_base": case "ace-base": return "Protected game or anti-cheat driver";
                // Keep names excluded until vendor identity, ownership and restoration can be verified.
                case "clawflt": case "ahflt": return "Protected filter with unverified ownership";
                default: return "";
            }
        }
        internal static bool IsProtectedName(string name) { return ProtectedNameReason(name).Length != 0; }
        internal static string DisplayReason(string value)
        {
            if(value=="Defender protection driver")return LaboratoryActionText.Pick("Defender 文件保护驱动，不在处理范围", value, "Defender保護ドライバーのため対象外です");
            if(value=="Windows system filter")return LaboratoryActionText.Pick("Windows 系统文件过滤组件，不作为可选第三方目标", value, "Windowsのシステムフィルターのため対象外です");
            if(value=="Encryption or cloud-file filter")return LaboratoryActionText.Pick("加密或云文件过滤组件，分离可能影响文件访问", value, "暗号化・クラウドファイルのフィルターのため対象外です");
            if(value=="Protected game or anti-cheat driver")return LaboratoryActionText.Pick("游戏保护或反作弊驱动，为兼容游戏不处理", value, "ゲーム保護・アンチチートドライバーのため対象外です");
            if(value=="Protected filter with unverified ownership")return LaboratoryActionText.Pick("驱动来源尚未核验，保留保护，不允许分离", value, "所有者を確認できないため保護を維持し、分離しません");
            if(value=="System, Defender or protected game driver")return LaboratoryActionText.Pick("系统、Defender 或反作弊保护驱动", "System, Defender or protected game driver", "システム、Defender、またはアンチチート保護ドライバー");
            if(value=="System, protection, encryption or cloud-file component")return LaboratoryActionText.Pick("系统、保护、加密或云文件组件", value, "システム、保護、暗号化、クラウドファイルのコンポーネント");
            if(value=="No unique registered driver instance owner")return LaboratoryActionText.Pick("未找到可唯一对应的实例注册，无法确认驱动归属及重新附加方式", value, "インスタンスの登録と再接続方法を一意に確認できません");
            if(value=="Driver image unavailable")return LaboratoryActionText.Pick("驱动文件不可核验", value, "ドライバーファイルを確認できません");
            if(value=="Driver signature or owner could not be verified")return LaboratoryActionText.Pick("驱动签名或所有者无法核验", value, "署名または所有者を確認できません");
            if(value=="Multiple instances of this filter are attached to this volume")return LaboratoryActionText.Pick("同卷存在该过滤器的多个实例", value, "同じボリュームに複数のインスタンスがあります");
            if(value=="Driver owner or signed image identity unavailable")return LaboratoryActionText.Pick("驱动所有者或签名文件身份不明", value, "所有者または署名ファイルの識別情報が不明です");
            return value;
        }
        internal static string Reason(FileFilterCandidate item)
        {
            if (item == null || !Regex.IsMatch(item.VolumeGuid ?? "", @"^\\\\\?\\Volume\{[0-9a-fA-F-]{36}\}\\$")) return "Volume identity unavailable";
            Guid volume;
            if (!Guid.TryParse(item.VolumeGuid.Substring(11, 36), out volume)) return "Invalid volume identity";
            if (!Regex.IsMatch(item.FilterName ?? "", @"^[a-zA-Z0-9_.-]{1,128}$") || string.IsNullOrWhiteSpace(item.InstanceName)
                || item.InstanceName.Length > 256 || HasControl(item.InstanceName) || !Regex.IsMatch(item.Altitude ?? "", @"^[0-9]{1,8}(\.[0-9]{1,12})?$")) return "Incomplete instance identity";
            string nameReason=ProtectedNameReason(item.FilterName);
            if (nameReason.Length != 0) return nameReason;
            string description = ((item.Publisher ?? "") + " " + (item.Description ?? "") + " " + item.FilterName).ToLowerInvariant();
            string[] blocked = { "microsoft", "defender", "antimalware", "anti-cheat", "anticheat", "anti cheat", "battleye", "easyanticheat", "vanguard", "faceit", "perfectworld", "perfect world", "完美世界", "tencent", "腾讯", "protection kernel", "encrypt", "bitlocker", "cloud", "onedrive", "dropbox", "filesync", "file sync" };
            foreach (string token in blocked) if (description.Contains(token)) return "System, protection, encryption or cloud-file component";
            if (string.IsNullOrWhiteSpace(item.Publisher) || string.IsNullOrWhiteSpace(item.Description)
                || string.IsNullOrWhiteSpace(item.DriverPath) || !Path.IsPathRooted(item.DriverPath)
                || !item.DriverPath.EndsWith(".sys", StringComparison.OrdinalIgnoreCase)
                || !Regex.IsMatch(item.DriverIdentity ?? "", @"^[0-9A-Fa-f]{64}:[0-9A-Fa-f]{40}$")) return "Driver owner or signed image identity unavailable";
            return "";
        }
        private static bool HasControl(string value) { foreach (char c in value) if (char.IsControl(c)) return true; return false; }
    }

    internal sealed class LaboratoryFileFilterController
    {
        private readonly IFileFilterNative native;
        private readonly ILaboratoryManualStore store;
        private readonly object gate = new object();
        internal LaboratoryFileFilterController(IFileFilterNative native, ILaboratoryManualStore store) { this.native = native; this.store = store; }
        private static string Text(string zh, string en, string ja) { return LaboratoryActionText.Pick(zh, en, ja); }
        private bool Read(out string value) { try { return store.Read(out value); } catch { value="";return false; } }
        private bool Write(string value) { try { return store.Write(value); } catch { return false; } }
        internal bool HasReceipt { get { lock (gate) { string raw; return !Read(out raw) || !string.IsNullOrEmpty(raw); } } }
        internal FileFilterCandidate[] Enumerate(out string error)
        { lock (gate) { try { return native.Enumerate(out error); } catch (Exception e) { error = e.GetType().Name; return new FileFilterCandidate[0]; } } }
        internal LaboratoryManualState Inspect()
        {
            lock (gate)
            {
                string raw;
                if (!Read(out raw)) return new LaboratoryManualState { CanRestore = true, Failure = Text("无法读取恢复记录", "Recovery receipt could not be read", "復元記録を読み取れません") };
                if (!string.IsNullOrEmpty(raw))
                {
                    FileFilterCandidate target; string phase;
                    if (!Decode(raw, out target, out phase)) return new LaboratoryManualState { CanRestore = true, Failure = Text("恢复记录损坏，已停止新操作", "Invalid recovery receipt; new operations stopped", "復元記録が不正なため新規操作を停止しました") };
                    return new LaboratoryManualState { CanRestore = true, Applied = phase == "applied", Summary = Text("已接管：", "Owned instance: ", "管理中: ") + target.VolumeDisplay + " · " + target.FilterName,
                        Reason = Text("恢复将重新附加原实例；不保证还原驱动内部上下文", "Restore reattaches the original instance; internal driver contexts are not guaranteed", "復元は元のインスタンスを再接続します。内部状態の再現は保証されません") };
                }
                string error; FileFilterCandidate[] choices = Enumerate(out error);
                int count = 0; foreach (FileFilterCandidate c in choices) if (c.Eligible) count++;
                string totals=Text("检测到 ", "Detected ", "検出: ")+choices.Length+Text(" 个实例，", " instances, ", " インスタンス、")+count+Text(" 个可选", " eligible", " 件が対象");
                return new LaboratoryManualState { CanApply = count > 0, Summary = totals, Details = DescribeChoices(choices, error), Reason = count > 0
                    ? Text("手动选择一个第三方实例；仅在非对局时应用", "Select one third-party instance; apply only outside a match", "対象のサードパーティ製インスタンスを選択し、対局外で適用してください")
                    : string.IsNullOrEmpty(error) ? totals+Text("；各过滤器原因见详情", "; see details for each exclusion", "。各フィルターの理由は詳細をご確認ください") : error };
            }
        }
        internal static string DescribeChoices(FileFilterCandidate[] choices, string error)
        {
            var groups=new SortedDictionary<string,List<FileFilterCandidate>>(StringComparer.OrdinalIgnoreCase);
            foreach(FileFilterCandidate item in choices??new FileFilterCandidate[0])
            {
                if(item==null)continue;
                List<FileFilterCandidate> group;
                if(!groups.TryGetValue(item.FilterName??"",out group)) {group=new List<FileFilterCandidate>();groups[item.FilterName??""]=group;}
                group.Add(item);
            }
            var details=new StringBuilder();
            if(!string.IsNullOrEmpty(error))details.AppendLine(error).AppendLine();
            if(groups.Count==0)details.AppendLine(Text("没有枚举到固定 NTFS 卷上的文件过滤实例；若提示拒绝访问，请以管理员权限检查。", "No filter instances on fixed NTFS volumes were enumerated; check as administrator if access was denied.", "固定NTFSボリュームのフィルターが列挙されていません。アクセス拒否の場合は管理者権限で確認してください。"));
            foreach(var pair in groups)
            {
                var volumes=new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach(FileFilterCandidate item in pair.Value)volumes.Add(string.IsNullOrEmpty(item.VolumeDisplay)?item.VolumeGuid:item.VolumeDisplay);
                FileFilterCandidate first=pair.Value[0];
                details.Append(pair.Key).Append(" · ").Append(string.Join(", ",new List<string>(volumes).ToArray())).AppendLine();
                details.Append(Text("状态：", "Status: ", "状態: ")).AppendLine(first.Eligible?Text("可手动尝试；尚未验证性能收益", "Eligible for an explicit manual trial; benefit unverified", "手動試行の候補。性能向上は未確認です"):first.BlockReason);
                details.Append(Text("文件厂商：", "Image vendor: ", "ファイルの提供元: ")).AppendLine(string.IsNullOrEmpty(first.Publisher)?Text("未核验", "Unverified", "未確認"):first.Publisher);
                if(!string.IsNullOrEmpty(first.Description))details.Append(Text("组件：", "Component: ", "構成要素: ")).AppendLine(first.Description);
                if(!string.IsNullOrEmpty(first.DriverPath))details.Append(Text("文件：", "Image: ", "ファイル: ")).AppendLine(first.DriverPath);
                foreach(FileFilterCandidate item in pair.Value)
                {
                    if(item.Eligible==first.Eligible&&FileFilterCandidate.Eq(item.BlockReason,first.BlockReason))continue;
                    details.Append(item.VolumeDisplay).Append(" · ").AppendLine(item.Eligible?Text("可手动尝试", "Eligible for a manual trial", "手動試行の候補"):item.BlockReason);
                }
                details.AppendLine();
            }
            details.Append(Text("本次检查仅为读取与归因；未执行驱动分离。文件厂商来自版本信息；准入仍要求可核验签名和原实例注册。", "This check only read and classified filters; nothing was detached. Image vendor is version metadata; eligibility still requires a verifiable signature and original instance registration.", "今回の確認は読み取りと分類のみで、分離は実行していません。提供元はファイルのバージョン情報です。適用には検証できる署名と元のインスタンス登録が必要です。"));
            return details.ToString();
        }
        internal bool Apply(FileFilterCandidate requested, out string error)
        {
            lock (gate)
            {
                error = ""; string raw;
                if (!Read(out raw) || !string.IsNullOrEmpty(raw)) { error = Text("请先结清已有恢复记录", "Resolve the existing recovery receipt first", "先に既存の復元記録を解決してください"); return false; }
                if (requested == null || !requested.Eligible || FileFilterAdmission.Reason(requested).Length != 0) { error = Text("该过滤实例不允许接管", "This filter instance is not eligible", "このフィルターは管理対象にできません"); return false; }
                FileFilterObservation current;
                if (!Observe(requested, out current, out error) || !Ready(current)) return Refuse(ref error);
                if (current.Instances.Length != 1 || !current.Instances[0].SameInstance(requested) || !current.Instances[0].SameDriver(requested)
                    || !current.Instances[0].Eligible) { error = Text("实例或驱动状态已变化，请刷新后重选", "Instance or driver changed; refresh and select again", "状態が変わりました。更新して再選択してください"); return false; }
                FileFilterCandidate target = current.Instances[0].Copy();
                if (!Save(target, "intent")) { error = Text("恢复记录保存失败，未分离驱动", "Receipt save failed; nothing was detached", "復元記録を保存できないため分離しませんでした"); return false; }
                bool changed; string nativeError;
                try { changed = native.Detach(target, out nativeError); } catch (Exception ex) { changed = false; nativeError = ex.GetType().Name; }
                FileFilterObservation after;
                if (!changed || !Observe(target, out after, out error) || !Ready(after) || after.Instances.Length != 0)
                {
                    string restoreError; bool restored = Restore(out restoreError);
                    error = Text("分离未确认", "Detach was not confirmed", "分離を確認できません") + " · " + nativeError + (restored ? "" : " · " + restoreError);
                    return false;
                }
                if (!Save(target, "applied"))
                {
                    string restoreError; bool restored = Restore(out restoreError);
                    error = Text("状态保存失败，已尝试恢复", "State save failed; restoration attempted", "状態保存に失敗したため復元を試みました") + (restored ? "" : " · " + restoreError); return false;
                }
                return true;
            }
        }
        private static bool Refuse(ref string error) { if (string.IsNullOrEmpty(error)) error = Text("卷或原驱动身份无法确认", "Volume or original driver identity could not be confirmed", "ボリュームまたはドライバーを確認できません"); return false; }
        private static bool Ready(FileFilterObservation value) { return value != null && value.VolumePresent && value.FilterLoaded && value.DriverMatches; }
        private bool Observe(FileFilterCandidate target, out FileFilterObservation result, out string error)
        { try { return native.Observe(target, out result, out error); } catch (Exception e) { result = null; error = e.GetType().Name; return false; } }
        internal bool Restore(out string error)
        {
            lock (gate)
            {
                error = ""; string raw;
                if (!Read(out raw)) { error = Text("无法读取恢复记录", "Recovery receipt could not be read", "復元記録を読み取れません"); return false; }
                if (string.IsNullOrEmpty(raw)) return true;
                FileFilterCandidate target; string phase;
                if (!Decode(raw, out target, out phase)) { error = Text("记录损坏，保留原记录等待处理", "Invalid receipt retained for recovery", "不正な記録を復元用に保持します"); return false; }
                FileFilterObservation current;
                if (!Observe(target, out current, out error) || !Ready(current)) return Refuse(ref error);
                if (current.Instances.Length == 1 && current.Instances[0].SameInstance(target) && current.Instances[0].SameDriver(target)) return Clear(out error);
                if (current.Instances.Length != 0) { error = Text("发现外部实例改动，未覆盖", "An external instance change was found; not overwritten", "外部の変更が見つかったため上書きしませんでした"); return false; }
                string attachError;
                try { native.Attach(target, out attachError); } catch (Exception e) { attachError = e.GetType().Name; }
                FileFilterObservation after;
                if (!Observe(target, out after, out error) || !Ready(after) || after.Instances.Length != 1
                    || !after.Instances[0].SameInstance(target) || !after.Instances[0].SameDriver(target))
                { error = Text("恢复未确认，原记录已保留", "Restoration unconfirmed; original receipt retained", "復元を確認できないため元の記録を保持します") + " · " + attachError; return false; }
                return Clear(out error);
            }
        }
        private bool Clear(out string error)
        { error = ""; if (Write("")) return true; error = Text("已附加，但恢复记录尚未清除", "Attached, but recovery receipt could not be cleared", "接続済みですが復元記録を消去できません"); return false; }
        private bool Save(FileFilterCandidate target, string phase)
        {
            try { string raw=Encode(target,phase), checkedPhase;FileFilterCandidate checkedTarget;
                return Decode(raw,out checkedTarget,out checkedPhase) && Write(raw); }
            catch { return false; }
        }
        internal static string Encode(FileFilterCandidate t, string phase)
        {
            string[] fields = { "1", phase, t.VolumeGuid, t.VolumeDisplay, t.FilterName, t.InstanceName, t.Altitude, t.DriverPath, t.DriverIdentity, t.Publisher, t.Description };
            for (int i = 2; i < fields.Length; i++) fields[i] = Convert.ToBase64String(Encoding.UTF8.GetBytes(fields[i] ?? ""));
            return string.Join("|", fields);
        }
        internal static bool Decode(string raw, out FileFilterCandidate target, out string phase)
        {
            target = null; phase = "";
            try
            {
                if (raw == null || raw.Length > 16384) return false;
                string[] f = raw.Split('|'); if (f.Length != 11 || f[0] != "1" || (f[1] != "intent" && f[1] != "applied")) return false;
                var strict = new UTF8Encoding(false, true);
                for (int i = 2; i < f.Length; i++) { f[i] = strict.GetString(Convert.FromBase64String(f[i])); if (f[i].Length > 2048) return false; }
                var value = new FileFilterCandidate { VolumeGuid=f[2], VolumeDisplay=f[3], FilterName=f[4], InstanceName=f[5], Altitude=f[6], DriverPath=f[7], DriverIdentity=f[8], Publisher=f[9], Description=f[10], Eligible=true };
                if (FileFilterAdmission.Reason(value).Length != 0) return false;
                target=value; phase=f[1]; return true;
            }
            catch { return false; }
        }
    }

    internal static class LaboratoryFileFilter
    {
        internal const string Key = "LabFileFilterV1", ReceiptKey = "LabFileFilterReceiptV1";
        private static readonly LaboratoryFileFilterController Controller = new LaboratoryFileFilterController(new FileFilterNative(), new LaboratoryManualSettingsStore(ReceiptKey));
        internal static bool HasReceipt { get { return Controller.HasReceipt; } }
        internal static LaboratoryManualState Inspect() { return Controller.Inspect(); }
        internal static FileFilterCandidate[] Enumerate(out string error) { return Controller.Enumerate(out error); }
        internal static bool Apply(FileFilterCandidate target, out string error) { return Controller.Apply(target, out error); }
        internal static bool Restore(out string error) { return Controller.Restore(out error); }
    }
}
