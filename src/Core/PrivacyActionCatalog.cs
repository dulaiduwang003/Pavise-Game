using System;

namespace PaviseApp
{
    internal sealed class PrivacyAction
    {
        internal string Key, Zh, En, DetailZh, DetailEn;
        internal bool Advanced;
        internal PrivacyAction(string key, string zh, string en, string dz, string de, bool advanced)
        { Key = key; Zh = zh; En = en; DetailZh = dz; DetailEn = de; Advanced = advanced; }
        internal string Title { get { return AutomaticPolicy.Text(Zh, En); } }
        internal string Detail { get { return AutomaticPolicy.Text(DetailZh, DetailEn); } }
    }

    internal static class PrivacyActionCatalog
    {
        internal const string Diagnostics = "EnvPrivacyDiagnosticsV1", Service = "EnvPrivacyServiceV1",
            Ceip = "EnvPrivacyCeipV1", Appraiser = "EnvPrivacyAppraiserV1", ErrorReports = "EnvPrivacyErrorReportsV1",
            SecurityReports = "EnvPrivacySecurityReportsV1", Input = "EnvPrivacyInputV1", Personalized = "EnvPrivacyPersonalizedV1",
            Compatibility = "EnvPrivacyCompatibilityV1",
            BackgroundGlobal = "EnvPrivacyBackgroundGlobalV1", NtfsMemory = "EnvPrivacyNtfsMemoryV1", Archives = "EnvPrivacyArchivesV1";
        internal static readonly PrivacyAction[] All = {
            new PrivacyAction(Diagnostics, "Windows 诊断数据", "Windows diagnostic data", "限制为系统支持的最低级别。家庭版和专业版仍可能发送必要诊断数据。", "Uses the lowest supported diagnostic level. Home and Pro may still send required diagnostic data.", false),
            new PrivacyAction(Service, "诊断数据服务", "Diagnostic data service", "持续停用 DiagTrack；恢复原启动类型和运行状态。停止此服务不代表关闭全部系统通信。", "Disables DiagTrack persistently; restores its original startup type and running state. This does not disable every system connection.", false),
            new PrivacyAction(Ceip, "体验改善计划", "Customer Experience Improvement Program", "限制系统中存在的体验改善策略和任务，不创建或删除计划任务。", "Restricts existing customer-experience policies and tasks without creating or deleting scheduled tasks.", false),
            new PrivacyAction(Appraiser, "应用兼容性评估上报", "Application compatibility assessment", "限制兼容性评估任务；可能影响 Windows 升级前的兼容性评估。", "Restricts compatibility-assessment tasks; may affect compatibility assessment before Windows upgrades.", true),
            new PrivacyAction(ErrorReports, "Windows 错误报告", "Windows Error Reporting", "限制错误报告发送，不删除已有本地日志和故障转储。", "Restricts error-report submission without deleting existing local logs or crash dumps.", false),
            new PrivacyAction(SecurityReports, "安全组件统计报告", "Security component statistical reporting", "仅限制支持的统计上报；保留 Defender 实时保护、云保护和安全更新。", "Restricts supported statistical reporting while preserving Defender real-time protection, cloud protection and security updates.", false),
            new PrivacyAction(Input, "笔迹和键入数据收集", "Inking and typing data collection", "限制 Windows 输入相关数据收集，具体范围取决于系统版本。", "Restricts Windows input-related data collection; coverage depends on the Windows version.", false),
            new PrivacyAction(Personalized, "禁用广告标识与诊断个性化", "Disable advertising ID and diagnostic personalization", "开启后禁用广告标识及基于诊断数据的个性化体验；关闭后恢复开启前的设置。", "Turn on to disable advertising ID and personalization based on diagnostic data. Turn off to restore the previous settings.", false),
            new PrivacyAction(Compatibility, "程序兼容性助手", "Program Compatibility Assistant", "高级选项：停用兼容性助手；旧软件可能失去兼容问题提示和自动修复。", "Advanced option: disables the compatibility assistant; older applications may lose compatibility warnings and automatic fixes.", true),
            new PrivacyAction(BackgroundGlobal, "禁止应用后台运行", "Disable background apps", "统一禁止 Windows 应用后台活动，关闭后恢复原设置。可能影响通知与同步；不控制普通桌面程序。", "Disables Windows apps' background activity globally; restores the original setting when turned off. Notifications and sync may be affected. Does not control ordinary desktop programs.", false),
            new PrivacyAction(NtfsMemory, "增加 NTFS 缓存池", "Increase NTFS cache pools", "高级选项：设置 MemoryUsage 为 2，需要重启；内存紧张时可能降低性能，不进入推荐档。", "Advanced option: sets MemoryUsage to 2 and requires restart. May reduce performance under memory pressure; excluded from recommended profiles.", true),
            new PrivacyAction(Archives, "ZIP/CAB 文件夹控制", "ZIP/CAB shell folder control", "研究项：功能尚未实现，暂不提供修改。", "Research item: this feature has not been implemented yet.", true)
        };
        internal static PrivacyAction Find(string key)
        { foreach (PrivacyAction action in All) if (action.Key == key) return action; return null; }
    }
}
