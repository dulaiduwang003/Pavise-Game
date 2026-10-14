using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace PaviseApp
{
    internal sealed class PrivacyPlatform
    {
        internal int Build;
        internal string Edition;
        internal bool Managed;
        internal bool Enterprise
        {
            get { return (Edition ?? "").StartsWith("Enterprise", StringComparison.OrdinalIgnoreCase)
                || (Edition ?? "").StartsWith("IoTEnterprise", StringComparison.OrdinalIgnoreCase)
                || (Edition ?? "").StartsWith("Education", StringComparison.OrdinalIgnoreCase); }
        }
        internal bool PolicyCapable { get { return Enterprise || (Edition ?? "").StartsWith("Professional", StringComparison.OrdinalIgnoreCase); } }
    }

    // 所有目标均来自明确的允许列表。构造、页面构建和配置读取不执行修改。
    internal sealed partial class PrivacyNativeBackend : IPrivacyBackend, IPrivacyRecoveryBackend
    {
        private const string AppPrivacyPath = @"SOFTWARE\Policies\Microsoft\Windows\AppPrivacy";
        private static string R(string hive, string path, string name) { return "reg|" + hive + "|" + path + "|" + name; }
        private sealed class RegSetting
        {
            internal string Target;
            internal int Value;
            internal RegSetting(string hive, string path, string name, int value) { Target = R(hive, path, name); Value = value; }
        }
#if PAVISE_SELFTEST
        internal static Func<PrivacyPlatform> PlatformForTest;
        internal static Func<string> MachineForTest, UserForTest;
        internal static Func<string, string> RegistryReadForTest;
        internal static Func<string, string, bool> RegistryWriteForTest;
#endif
        private static string CurrentMachine()
        {
            string value;
#if PAVISE_SELFTEST
            if (MachineForTest == null) throw new InvalidOperationException("Unmocked privacy machine identity");
            value = MachineForTest();
#else
            using (RegistryKey hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (RegistryKey key = hive.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography"))
                value = key == null ? null : key.GetValue("MachineGuid", null) as string;
#endif
            Guid machine;
            if (!Guid.TryParse(value, out machine) || machine == Guid.Empty) throw new InvalidOperationException("Privacy machine identity unavailable");
            return machine.ToString("N");
        }
        private static string CurrentUser()
        {
            string value;
#if PAVISE_SELFTEST
            if (UserForTest == null) throw new InvalidOperationException("Unmocked privacy user identity");
            value = UserForTest();
#else
            using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
                value = identity.User == null ? null : identity.User.Value;
#endif
            if (string.IsNullOrEmpty(value) || !Regex.IsMatch(value, @"\AS-1-(?:\d+-){1,14}\d+\z", RegexOptions.CultureInvariant))
                throw new InvalidOperationException("Privacy user identity unavailable");
            return new System.Security.Principal.SecurityIdentifier(value).Value;
        }
        private static string ScopeTarget(string target)
        { return "ctx|" + CurrentMachine() + "|" + (target.StartsWith("reg|CU|", StringComparison.Ordinal) ? CurrentUser() : "system") + "|" + target; }
        private static string LocalTarget(string target)
        {
            if (target == null || !target.StartsWith("ctx|", StringComparison.Ordinal)) throw new ArgumentException("Privacy target has no device identity");
            string canonical = PrivacyControl.CanonicalTarget(target);
            string[] parts = target.Split(new[] { '|' }, 4);
            string principal = canonical.StartsWith("reg|CU|", StringComparison.Ordinal) ? CurrentUser() : "system";
            if (!string.Equals(parts[1], CurrentMachine(), StringComparison.OrdinalIgnoreCase) || parts[2] != principal)
                throw new NotSupportedException(AutomaticPolicy.Text("恢复记录属于另一台电脑或另一账户，已保留记录并拒绝修改。", "This recovery record belongs to another device or account. It is retained without changing this system."));
            return canonical;
        }
        private static RegSetting[] SettingsFor(string action, PrivacyPlatform platform)
        {
            if (action == PrivacyActionCatalog.Diagnostics)
            {
                var settings = new List<RegSetting> {
                    new RegSetting("LM", platform.PolicyCapable ? @"SOFTWARE\Policies\Microsoft\Windows\DataCollection"
                        : @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\DataCollection", "AllowTelemetry", platform.Enterprise ? 0 : 1) };
                // 日志及转储限制从 Windows 11 开始提供。
                if (platform.PolicyCapable && platform.Build >= 22000)
                {
                    settings.Add(new RegSetting("LM", @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "LimitDiagnosticLogCollection", 1));
                    settings.Add(new RegSetting("LM", @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "LimitDumpCollection", 1));
                }
                return settings.ToArray();
            }
            if (action == PrivacyActionCatalog.Service) return new RegSetting[0];
            if (action == PrivacyActionCatalog.Ceip) return new[] {
                new RegSetting("LM", platform.PolicyCapable ? @"SOFTWARE\Policies\Microsoft\SQMClient\Windows"
                    : @"SOFTWARE\Microsoft\SQMClient\Windows", "CEIPEnable", 0) };
            if (action == PrivacyActionCatalog.Appraiser) return platform.PolicyCapable ? new[] {
                new RegSetting("LM", @"SOFTWARE\Policies\Microsoft\Windows\AppCompat", "DisableInventory", 1) }
                : new RegSetting[0];
            if (action == PrivacyActionCatalog.ErrorReports)
            {
                string path = platform.PolicyCapable ? @"SOFTWARE\Policies\Microsoft\Windows\Windows Error Reporting"
                    : @"SOFTWARE\Microsoft\Windows\Windows Error Reporting";
                return new[] { new RegSetting("LM", path, "Disabled", 1), new RegSetting("LM", path, "DontSendAdditionalData", 1) };
            }
            if (action == PrivacyActionCatalog.SecurityReports) return new[] {
                new RegSetting("LM", @"SOFTWARE\Policies\Microsoft\MRT", "DontReportInfectionInformation", 1) };
            if (action == PrivacyActionCatalog.Input)
            {
                var settings = new List<RegSetting> { new RegSetting("CU", @"SOFTWARE\Microsoft\Input\TIPC", "Enabled", 0) };
                if (platform.PolicyCapable)
                {
                    settings.Add(new RegSetting("LM", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\TextInput", "AllowLinguisticDataCollection", 0));
                    settings.Add(new RegSetting("LM", @"SOFTWARE\Policies\Microsoft\Windows\TabletPC", "PreventHandwritingDataSharing", 1));
                    settings.Add(new RegSetting("LM", @"SOFTWARE\Policies\Microsoft\Windows\HandwritingErrorReports", "PreventHandwritingErrorReports", 1));
                }
                return settings.ToArray();
            }
            if (action == PrivacyActionCatalog.Personalized) return new[] {
                new RegSetting("CU", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Privacy", "TailoredExperiencesWithDiagnosticDataEnabled", 0),
                new RegSetting("CU", @"SOFTWARE\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 0) };
            if (action == PrivacyActionCatalog.Compatibility) return platform.PolicyCapable ? new[] {
                new RegSetting("LM", @"SOFTWARE\Policies\Microsoft\Windows\AppCompat", "DisablePCA", 1) } : new RegSetting[0];
            if (action == PrivacyActionCatalog.NtfsMemory) return new[] {
                new RegSetting("LM", @"SYSTEM\CurrentControlSet\Control\FileSystem", "NtfsMemoryUsage", 2) };
            if (action == PrivacyActionCatalog.BackgroundGlobal) return platform.PolicyCapable ? new[] {
                new RegSetting("LM", AppPrivacyPath, "LetAppsRunInBackground", 2) } : new[] {
                new RegSetting("CU", @"SOFTWARE\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications", "GlobalUserDisabled", 1) };
            return new RegSetting[0];
        }
        private static PrivacyPlatform Platform()
        {
#if PAVISE_SELFTEST
            if (PlatformForTest == null) throw new InvalidOperationException("Unmocked privacy platform");
            PrivacyPlatform mock = PlatformForTest();
            if (mock == null) throw new InvalidOperationException("Unknown privacy platform");
            return mock;
#else
            using (RegistryKey hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var key = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
            {
                if (key == null) throw new InvalidOperationException("Windows version unavailable");
                int build;
                string edition = key.GetValue("EditionID", null) as string;
                if (string.IsNullOrEmpty(edition) || !int.TryParse(Convert.ToString(key.GetValue("CurrentBuildNumber", null),
                    CultureInfo.InvariantCulture), out build)) throw new InvalidOperationException("Windows version unavailable");
                return new PrivacyPlatform { Build = build, Edition = edition.Trim(), Managed = OrganizationManaged() };
            }
#endif
        }
        private static bool OrganizationManaged()
        {
#if PAVISE_SELFTEST
            throw new InvalidOperationException("Unmocked organization policy");
#else
            // 不绕过域或 MDM 下发的配置。无法读取管理来源时拒绝写入。
            IntPtr domain = IntPtr.Zero;
            int joinStatus;
            try
            {
                uint result = PrivacyNetGetJoinInformation(null, out domain, out joinStatus);
                if (result != 0 || joinStatus < 1 || joinStatus > 3) throw new InvalidOperationException("Organization membership could not be verified");
                if (joinStatus == 3) return true;
            }
            finally { if (domain != IntPtr.Zero) PrivacyNetApiBufferFree(domain); }
            // 系统内置注册项也有 EnrollmentState，不能据此判断企业管理。
            bool registered;
            int managementResult = PrivacyIsDeviceRegisteredWithManagement(out registered, 0, IntPtr.Zero);
            if (managementResult != 0) throw new InvalidOperationException("Device management registration could not be verified");
            return registered;
#endif
        }
        public PrivacyPlan Inspect(string action, string selectedTarget)
        {
            if (PrivacyActionCatalog.Find(action) == null) throw new ArgumentException("Unknown privacy action");
            PrivacyPlatform platform = Platform();
            var plan = new PrivacyPlan { Supported = platform.Build >= 19041, Targets = new PrivacyTarget[0] };
            if (!plan.Supported) { plan.UnavailableReason = "system"; plan.Note = AutomaticPolicy.Text("需要 Windows 10 2004 或更新版本。", "Requires Windows 10 version 2004 or newer."); return plan; }
            if (action == PrivacyActionCatalog.Archives)
            {
                plan.Supported = false;
                plan.UnavailableReason = "unimplemented";
                plan.Note = AutomaticPolicy.Text("ZIP/CAB 外壳集成开关尚未实现，暂不提供修改。", "The ZIP/CAB shell integration switch has not been implemented. Changes are not available yet.");
                return plan;
            }
            if (platform.Managed)
            { plan.Managed = true; plan.Note = AutomaticPolicy.Text("设备受组织管理，请由管理员调整策略。", "This device is managed by an organization. Contact its administrator."); return plan; }
            var targets = new List<PrivacyTarget>();
            string service = action == PrivacyActionCatalog.Service ? "DiagTrack"
                : action == PrivacyActionCatalog.Compatibility && !platform.PolicyCapable ? "PcaSvc" : null;
            if (service != null)
            {
                string current = ServiceRead(service);
                if (current == "missing") { plan.Supported = false; plan.UnavailableReason = "component"; plan.Note = AutomaticPolicy.Text("系统未安装此服务：", "This service is not installed: ") + service; return plan; }
                string startup = ServiceStartRead(service);
                if (current.StartsWith("v:4:", StringComparison.Ordinal) && startup.StartsWith("c:4:", StringComparison.Ordinal))
                {
                    plan.Supported = false;
                    plan.UnavailableReason = "external";
                    plan.Note = AutomaticPolicy.Text("服务仍在运行，但启动类型已被外部禁用；保留该状态，避免无法还原运行状态。", "The service is running with an externally disabled startup type. Its state is preserved because restarting it would require changing that external choice.");
                    return plan;
                }
                targets.Add(new PrivacyTarget { Key = "svc|" + service, Wanted = ServiceWanted(current, false) });
                targets.Add(new PrivacyTarget { Key = "start|" + service, Wanted = ServiceStartWanted(startup, 4) });
            }
            foreach (RegSetting setting in SettingsFor(action, platform))
                targets.Add(new PrivacyTarget { Key = setting.Target, Wanted = "d:" + setting.Value.ToString(CultureInfo.InvariantCulture),
                    RestartRequired = action == PrivacyActionCatalog.NtfsMemory || action == PrivacyActionCatalog.BackgroundGlobal });
            if (action == PrivacyActionCatalog.Ceip || action == PrivacyActionCatalog.Appraiser)
                foreach (string task in TasksFor(action))
                {
                    string current = TaskRead(task);
                    if (current != "missing") targets.Add(new PrivacyTarget { Key = "task|" + task, Wanted = TaskWanted(current, false) });
                }
            if (action == PrivacyActionCatalog.Diagnostics)
            {
                plan.Limited = !platform.Enterprise;
                plan.Note = AutomaticPolicy.Text(platform.Enterprise
                    ? "配置诊断数据关闭；该结果仅确认设置，尚无网络流量验证。"
                    : "此版本保留必需诊断数据，关闭可选数据；不能显示为全部遥测关闭。",
                    platform.Enterprise ? "Configures diagnostic data off. Settings are checked; network traffic is not yet verified."
                    : "This edition retains required diagnostic data while optional data is disabled. This is not complete telemetry shutdown.");
            }
            else if (action == PrivacyActionCatalog.SecurityReports)
            {
                plan.Limited = true;
                plan.Note = AutomaticPolicy.Text("仅控制 MSRT 统计报告。Defender 云保护、样本检测和安全更新保留；不声称全部安全组件遥测关闭。", "Controls MSRT statistical reporting only. Defender cloud protection, sample detection and security updates remain; this is not full security-component telemetry shutdown.");
            }
            else if (action == PrivacyActionCatalog.Ceip || action == PrivacyActionCatalog.Appraiser)
            {
                plan.Limited = action == PrivacyActionCatalog.Appraiser && !platform.PolicyCapable;
                plan.Note = plan.Limited
                    ? AutomaticPolicy.Text("此版本仅限制现有兼容性评估任务，不修改程序清单策略；已运行的任务不会被强制结束。", "On this edition, restricts existing assessment tasks without changing inventory policy. Running tasks are not forcibly stopped.")
                    : AutomaticPolicy.Text("仅控制存在且身份匹配的任务；禁用触发不代表已运行任务已终止。", "Controls only present, identity-matched tasks. Disabling triggers does not confirm running tasks have stopped.");
            }
            else if (action == PrivacyActionCatalog.Input && !platform.PolicyCapable)
            {
                plan.Limited = true;
                plan.Note = AutomaticPolicy.Text("仅设置当前账户的墨迹和键入数据偏好；请在 Windows 诊断与反馈设置中确认，不代表全部输入诊断已关闭。", "Sets the current account's inking and typing preference only. Confirm it in Windows Diagnostics & feedback; this does not verify all input diagnostics are off.");
            }
            else if (action == PrivacyActionCatalog.ErrorReports)
                plan.Note = AutomaticPolicy.Text("关闭 Windows 错误报告发送；保留本地日志和独立配置的故障转储。", "Disables Windows error-report submission; local logs and independently configured crash dumps are retained.");
            else if (action == PrivacyActionCatalog.Compatibility && !platform.PolicyCapable)
                plan.Note = AutomaticPolicy.Text("停用程序兼容性助手服务；关闭开关后恢复原启动类型及运行状态。", "Disables the Program Compatibility Assistant service. Turning this off restores its original startup type and running state.");
            else if (action == PrivacyActionCatalog.NtfsMemory)
                plan.Note = AutomaticPolicy.Text("高级文件系统缓存选项；需重启。内存紧张时可能降低性能。", "Advanced file-system caching option; restart required. Memory pressure can reduce performance.");
            else if (action == PrivacyActionCatalog.BackgroundGlobal)
                plan.Note = platform.PolicyCapable
                    ? AutomaticPolicy.Text("全局限制 Windows 应用，重启后生效；原有逐应用例外保持不变。", "Globally restricts Windows apps after restart; existing per-app exceptions are preserved.")
                    : AutomaticPolicy.Text("限制当前账户的 Windows 应用后台权限，重启后生效；关闭开关后恢复原设置。", "Restricts background permissions for Windows apps in the current account after restart. Turning this off restores the original setting.");
            else plan.Note = "";
            foreach (PrivacyTarget target in targets) target.Key = ScopeTarget(target.Key);
            plan.Targets = targets.ToArray();
            if (targets.Count == 0)
            {
                plan.Supported = false; plan.UnavailableReason = "component";
                plan.Note = AutomaticPolicy.Text("当前系统没有可控制的对应组件或计划任务。", "No applicable component or scheduled task is present on this system.");
            }
            return plan;
        }
        private static string ValidateTarget(string action, string scopedTarget)
        {
            string target = LocalTarget(scopedTarget);
            if (PrivacyActionCatalog.Find(action) == null || string.IsNullOrEmpty(target)) throw new ArgumentException("Unknown privacy target");
            if (action == PrivacyActionCatalog.Service && target == "svc|DiagTrack") return target;
            if (action == PrivacyActionCatalog.Service && target == "start|DiagTrack") return target;
            if (action == PrivacyActionCatalog.Compatibility && (target == "svc|PcaSvc" || target == "start|PcaSvc")) return target;
            if (target.StartsWith("task|", StringComparison.Ordinal))
                foreach (string task in TasksFor(action)) if (target == "task|" + task) return target;
            // 恢复目录保留所有版本的目标，不能按当前版本裁剪历史记录。
            foreach (string edition in new[] { "Enterprise", "Core" })
                foreach (RegSetting entry in SettingsFor(action, new PrivacyPlatform { Edition = edition, Build = int.MaxValue }))
                    if (string.Equals(entry.Target, target, StringComparison.OrdinalIgnoreCase)) return target;
            throw new ArgumentException("Privacy target is outside its action allowlist");
        }
        public string Read(string action, string target)
        {
            target = ValidateTarget(action, target);
            if (target.StartsWith("svc|", StringComparison.Ordinal)) return ServiceRead(target.Substring(4));
            if (target.StartsWith("start|", StringComparison.Ordinal)) return ServiceStartRead(target.Substring(6));
            if (target.StartsWith("task|", StringComparison.Ordinal)) return TaskRead(target.Substring(5));
            return RegistryRead(target);
        }
        public bool Write(string action, string target, string value)
        {
            target = ValidateTarget(action, target);
            if (Platform().Managed) throw new NotSupportedException("Organization-managed privacy settings must be changed by the administrator");
            if (target.StartsWith("svc|", StringComparison.Ordinal)) return ServiceWrite(target.Substring(4), value);
            if (target.StartsWith("start|", StringComparison.Ordinal)) return ServiceStartWrite(target.Substring(6), value);
            if (target.StartsWith("task|", StringComparison.Ordinal)) return TaskWrite(target.Substring(5), value);
            return RegistryWrite(target, value);
        }
        public bool Satisfied(string action, string target, string current, string wanted)
        {
            ValidateTarget(action, target);
            return current == wanted;
        }
        public bool Restore(PerformanceActionStore.Receipt receipt)
        {
            ValidateTarget(receipt.Id, receipt.Target);
            return PerformanceActionStore.Restore(receipt,
                delegate { return Read(receipt.Id, receipt.Target); },
                delegate(string value) { return Write(receipt.Id, receipt.Target, value); },
                delegate(string current) { return current == receipt.Expected; });
        }
        internal static string PackRegistry(object value, RegistryValueKind kind)
        {
            if (value == null) return "absent";
            if (kind == RegistryValueKind.DWord) return "d:" + Convert.ToInt32(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
            if (kind == RegistryValueKind.QWord) return "q:" + Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
            if (kind == RegistryValueKind.String || kind == RegistryValueKind.ExpandString)
                return (kind == RegistryValueKind.String ? "s:" : "e:") + B64((string)value);
            if (kind == RegistryValueKind.MultiString)
            {
                var parts = new List<string>();
                foreach (string item in (string[])value) parts.Add(B64(item));
                return "m:" + string.Join(",", parts.ToArray());
            }
            if (kind == RegistryValueKind.Binary) return "b:" + Convert.ToBase64String((byte[])value);
            throw new NotSupportedException("Unsupported registry value type");
        }
        private static object UnpackRegistry(string text, out RegistryValueKind kind)
        {
            if (text == "absent") { kind = RegistryValueKind.None; return null; }
            if (text == null || text.Length < 2 || text[1] != ':') throw new FormatException("Invalid privacy value");
            string raw = text.Substring(2);
            switch (text[0]) {
                case 'd': kind = RegistryValueKind.DWord; return int.Parse(raw, CultureInfo.InvariantCulture);
                case 'q': kind = RegistryValueKind.QWord; return long.Parse(raw, CultureInfo.InvariantCulture);
                case 's': kind = RegistryValueKind.String; return UnB64(raw);
                case 'e': kind = RegistryValueKind.ExpandString; return UnB64(raw);
                case 'b': kind = RegistryValueKind.Binary; return Convert.FromBase64String(raw);
                case 'm':
                    kind = RegistryValueKind.MultiString;
                    if (raw.Length == 0) return new string[0];
                    string[] parts = raw.Split(',');
                    for (int i = 0; i < parts.Length; i++) parts[i] = UnB64(parts[i]);
                    return parts;
                default: throw new FormatException("Invalid privacy value type");
            }
        }
        internal static string B64(string value) { return Convert.ToBase64String(Encoding.UTF8.GetBytes(value)); }
        internal static string UnB64(string value) { return new UTF8Encoding(false, true).GetString(Convert.FromBase64String(value)); }
        private static string[] RegistryParts(string target)
        {
            string[] parts = target.Split('|');
            if (parts.Length != 4 || parts[0] != "reg" || (parts[1] != "LM" && parts[1] != "CU")
                || parts[2].Length == 0 || parts[3].Length == 0 || parts[2].IndexOf('\0') >= 0 || parts[3].IndexOf('\0') >= 0)
                throw new FormatException("Invalid registry target");
            return parts;
        }
        private static string RegistryRead(string target)
        {
#if PAVISE_SELFTEST
            if (RegistryReadForTest == null) throw new InvalidOperationException("Unmocked privacy registry read");
            string mock = RegistryReadForTest(target);
            if (mock == null) throw new InvalidOperationException("Unknown privacy registry value");
            RegistryValueKind mockKind; UnpackRegistry(mock, out mockKind);
            return mock;
#else
            string[] parts = RegistryParts(target);
            using (RegistryKey hive = RegistryKey.OpenBaseKey(parts[1] == "LM" ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, RegistryView.Registry64))
            using (RegistryKey key = hive.OpenSubKey(parts[2]))
            {
                if (key == null) return "absent";
                object value = key.GetValue(parts[3], null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                return PackRegistry(value, value == null ? RegistryValueKind.None : key.GetValueKind(parts[3]));
            }
#endif
        }
        private static bool RegistryWrite(string target, string text)
        {
            RegistryValueKind kind; object value = UnpackRegistry(text, out kind);
#if PAVISE_SELFTEST
            if (RegistryWriteForTest == null) throw new InvalidOperationException("Unmocked privacy registry write");
            return RegistryWriteForTest(target, text);
#else
            string[] parts = RegistryParts(target);
            using (RegistryKey hive = RegistryKey.OpenBaseKey(parts[1] == "LM" ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, RegistryView.Registry64))
            {
                if (value == null)
                {
                    using (RegistryKey key = hive.OpenSubKey(parts[2], true)) if (key != null) key.DeleteValue(parts[3], false);
                }
                else using (RegistryKey key = hive.CreateSubKey(parts[2]))
                { if (key == null) return false; key.SetValue(parts[3], value, kind); key.Flush(); }
            }
            return RegistryRead(target) == text;
#endif
        }
        [System.Runtime.InteropServices.DllImport("netapi32.dll", EntryPoint = "NetGetJoinInformation", ExactSpelling = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern uint PrivacyNetGetJoinInformation(string server, out IntPtr buffer, out int status);
        [System.Runtime.InteropServices.DllImport("netapi32.dll", EntryPoint = "NetApiBufferFree", ExactSpelling = true)]
        private static extern uint PrivacyNetApiBufferFree(IntPtr buffer);
        [System.Runtime.InteropServices.DllImport("MDMRegistration.dll", EntryPoint = "IsDeviceRegisteredWithManagement", ExactSpelling = true)]
        private static extern int PrivacyIsDeviceRegisteredWithManagement(
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)] out bool registered, uint size, IntPtr user);
    }
}
