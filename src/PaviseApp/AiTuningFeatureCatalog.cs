using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PaviseApp
{
    internal static class AiTuningFeatureCatalog
    {
        private sealed class Feature
        {
            internal readonly string Key, Title, Purpose, Scene;
            internal Feature(string key, string title, string purpose, string scene)
            { Key = key; Title = title; Purpose = purpose; Scene = scene; }
        }

        internal static string Describe(GameMode mode)
        {
            var stringBuilder = new StringBuilder(12000);
            stringBuilder.AppendLine(Lang.TranslateChinese("条目非开关数；开关指独立开关；偏好非生效，—无单值。恢复仅撤回工具记录，非强制开启/出厂重置；HAGS/SAM/窗口化优化/系统VRR关闭则请求关闭，原值另走恢复维护。会话退局恢复，持久项按重启提示。"));
            int count = 0;
            using (PolicyResolver.BeginReadScope())
            {
                foreach (PolicyItem item in PolicyCatalog.All)
                {
                    // 旧线程提优键没有界面也不再控制执行，不写进目录，免得被当成可调开关。
                    if (PerformancePolicy.IsRetired(item.Key) || item.Key == ThreadBoostSelection.Key) continue;
                    Append(stringBuilder, ref count, PolicyFeature(item), Configuration(item.Key, mode));
                }
                foreach (PerformanceAction action in PerformanceActionCatalog.All)
                    Append(stringBuilder, ref count, ActionFeature(action), Configuration(action.Key, mode));
                foreach (PrivacyAction action in PrivacyActionCatalog.All)
                {
                    Feature feature = action.Key == PrivacyActionCatalog.BackgroundGlobal
                        ? F(action.Key, (Lang.Cur == 3 ? action.Title : action.Zh), Lang.TranslateChinese("限制UWP后台活动，保留逐应用例外，不管普通桌面软件"), Lang.TranslateChinese("UWP后台活跃；可能延迟通知/同步，重启相关应用；家庭版必要时注销，不要求统一重启电脑"))
                        : F(action.Key, (Lang.Cur == 3 ? action.Title : action.Zh), Lang.TranslateChinese("扩大NTFS文件系统缓存池"), Lang.TranslateChinese("内存充足、文件元数据负载高；增加内存占用，需重启"));
                    Append(stringBuilder, ref count, feature, BoolConfiguration(PrivacyControl.PreferencePrefix + action.Key, false));
                }
                foreach (Feature feature in ExtraFeatures()) Append(stringBuilder, ref count, feature, Configuration(feature.Key, mode));
                foreach (Feature feature in CheckupFeatures()) Append(stringBuilder, ref count, feature, Lang.TranslateChinese("未复查"));
                AppendProfiles(stringBuilder, mode);
            }
            stringBuilder.AppendLine(Lang.TranslateChinese("功能条目：") + count.ToString(CultureInfo.InvariantCulture));
            stringBuilder.AppendLine(Lang.TranslateChinese("已下线不可开启：通用/手动LOL脱壳、文件过滤/计时修复/音效减负、Defender、全局ETW、网络旁路/统一限速、后台全冻结/工作集修剪、旧极限档、AMD Anti-Lag、桌面合成调度、游戏高性能显卡预选、游戏内存优先级、独立反作弊引擎/强度/弱核绑定、独立游戏线程提优开关；3.x旧记录按归属恢复，1.x/2.x遗留及旧反作弊、旧微软后台Job写入不再恢复。"));
            return stringBuilder.ToString();
        }

        private static void Append(StringBuilder text, ref int count, Feature feature, string configuration)
        {
            text.AppendLine((++count).ToString(CultureInfo.InvariantCulture) + "." + feature.Title + "[" + feature.Key + "]：" + feature.Purpose
                + Lang.TranslateChinese("。用：") + feature.Scene + Lang.TranslateChinese("。操作：") + Operation(feature.Key) + Lang.TranslateChinese("。当前：") + configuration);
        }

        private static string Operation(string key)
        {
            switch (key)
            {
                case "GmSuppress": return Lang.TranslateChinese("在顶部策略设置→游戏场景下方选择「后台治理」三档，无独立开关");
                case "GmAwake": return Lang.TranslateChinese("内置自动行为，无独立开关");
                case PolicyCatalog.KeyGameScheduling: return Lang.TranslateChinese("游戏卡片深度调优逐游戏开关，「调整」窗口选方案");
                case "GmBoost": return Lang.TranslateChinese("调度方案窗口内逐游戏开关，游戏优先已包含");
                case "GmPauseDl": case "GmGpuDemote": case "GmPauseMaintenanceV1": return Lang.TranslateChinese("随「后台治理」，无独立开关");
                case PerformanceActionCatalog.CpuMitigations: return Lang.TranslateChinese("独立开关；系统增强也包含此项；重新开启独立开关或在一键配置选择极限配置可选扩展，旧版范围不自动扩大");
                case "EnvVbsDisabledV1":
                case PerformanceActionCatalog.LastAccess: case PerformanceActionCatalog.AudioIdle:
                    return Lang.TranslateChinese("随「系统性能增强」组合，无独立开关");
                case PerformanceActionCatalog.StorageIdle: case PerformanceActionCatalog.StorageRuntime: case PerformanceActionCatalog.StorageCold:
                    return Lang.TranslateChinese("随「存储设备电源管理」组合，无独立开关");
                case "ModeSmart": case "ModeCompetitive": return Lang.TranslateChinese("模式行为说明，选运行模式");
                case "ManagedPower2223": case "ManagedPowerDynamic": return Lang.TranslateChinese("电源行为说明，选由软件调度");
                case "PerformancePreset": case "PowerPlanOn":
                case BackgroundGovernancePolicy.SettingKey:
                case "LabCpuIdleStrategyV1": case PerformanceActionCatalog.Quantum:
                case "GameExitGrace": return Lang.TranslateChinese("选择项");
                case "GameFamilyBackground": case "GameDisableFullscreenOptimizationsV1": case PolicyCatalog.KeyAceGovernance:
                case "GmAutoCoresV2": case "GameNvRebarV1": return Lang.TranslateChinese("逐游戏开关");
                case "GameCriticalThreadHardRuleV1": return Lang.TranslateChinese("逐游戏方案窗口，保存下局生效");
                case "GameForceTrigger": return Lang.TranslateChinese("游戏卡片菜单「强制接管」");
                case "GameCoreIsolation": return Lang.TranslateChinese("逐游戏核心计划选项");
                case "Whitelist": return Lang.TranslateChinese("名单管理（进程/EXE/服务）");
                case "GameLibrary": return Lang.TranslateChinese("自动入库开关+游戏管理");
                case "LolColumnEnabled": return Lang.TranslateChinese("专属模块说明，LOL卡片自动启用，子项独立");
                case "LolColumnCleanupEnabled": return Lang.TranslateChinese("开关+立即净化");
                case "LolColumnHeadlessEnabled": return Lang.TranslateChinese("开关+恢复界面");
                case "LolAddonCleanup": case "ManualMemoryCleanup": case "Checkup":
                case "ShaderCleanup": case "SettingsReset":
                case "LogManagement": return Lang.TranslateChinese("动作/诊断切换，刷新/清空/选中复制/导出");
                case "SettingsLogsUninstall": case "SettingsAiTuning":
                case "SoftwareUpdate": return Lang.TranslateChinese("手动");
                case "ConfigurationPresets": return Lang.TranslateChinese("一键配置内点击配置即应用");
                case "StartupSecurityCompatibility": return Lang.TranslateChinese("查看/不再显示");
                case "OverviewTelemetry": case "AuditSlowDrivers": case "AuditPlatform": case "AuditCpu":
                case "AuditMemory": case "AuditGraphics": case "AuditPower": case "AuditInput":
                case "AuditStorageNetwork": case "AuditResults": return Lang.TranslateChinese("只读检测");
                case "AuditGpuIrq": return Lang.TranslateChinese("手动应用/重启后验证");
                case "AuditGpuIrqRestore": return Lang.TranslateChinese("手动恢复");
                case "AuditMsi": case "AuditMemoryCompression": case "AuditNetworkThrottle":
                case "AuditLinkMetric": case "AuditFth": return Lang.TranslateChinese("手动修复/恢复");
                case "AuditQuantum": return Lang.TranslateChinese("前往时间片设置");
                case "AuditFixAll": return Lang.TranslateChinese("手动执行当前可修复项");
                case "SettingsAppearance": return Lang.TranslateChinese("外观选项");
                case "EnvPerformanceBundleV1": case "EnvStoragePowerGroupV1": return Lang.TranslateChinese("组合开关");
                default: return Lang.TranslateChinese("开关");
            }
        }

        private static Feature F(string key, string title, string purpose, string scene)
        { return new Feature(key, title, purpose, scene); }

        private static Feature PolicyFeature(PolicyItem item)
        {
            string key = item.Key;
            switch (key)
            {
                case "PerformancePreset": return F(key, Lang.TranslateChinese("运行模式"), Lang.TranslateChinese("选择智能/竞技，改变后台治理；托管参数相同"), Lang.TranslateChinese("按切屏需求和后台争用选择，不会自动打开所有功能"));
                case "GmSuppress": return F(key, Lang.TranslateChinese("后台治理"), Lang.TranslateChinese("关闭/正常/激进下拉三档，关闭为首项，无独立总开关；两档都把普通后台设为Idle优先级、I/O极低、页面优先级1，请求EcoQoS并忽略其计时器精度请求，HAGS可读且关闭时GPU降为Idle；激进另关闭Windows动态提优并收核；不设CPU配额；微软后台（DiagTrack、DPS、WdiServiceHost、WdiSystemHost、diagsvc、WSearch及工作进程、可信小组件和所属WebView2）并入普通后台，低占用也纳管，系统预设不豁免；ACE按逐游戏「反作弊压制」并入；WeGame主进程按普通后台，仅自身前台时不压；全部已配置/学习的游戏本体均受保护，家族开关或目录缺失不放行；核验后的游戏创建链及LeagueClient/RiotClientServices启动联网宿主豁免，UI辅助仍可治理；激进档把后台收到分区后台核（大小核E核/X3D非缓存CCD/双CCD另一个/其余0号核加末尾核，无分区时末尾1～2个物理核），隔离生效或调度预留核时也在原范围内避让"), Lang.TranslateChinese("后台争用；智能切出放行、竞技切出保留；提优按可核验原值恢复，不强制开启原本禁用的提优；手工进程白名单、服务白名单、家族与运行期保护照常有效；KOOK/YY/TeamSpeak/Discord等语音软件内置保护，QQ、微信、音乐播放器和浏览器不在名单，对局中会被压，需要时手动加白；关闭、移出治理或退局恢复"));
                case BackgroundGovernancePolicy.SettingKey: return F(key, Lang.TranslateChinese("后台治理档位"), Lang.TranslateChinese("顶部策略设置→游戏场景下方，默认正常；两档优先级与效率模式写入相同，激进额外禁止Windows临时提升普通后台线程优先级并收核，也作用于并入的微软后台和ACE"), Lang.TranslateChinese("激进可能使后台响应稍慢；智能前台时放行，竞技持续治理；不冻结或反复杀进程、不改服务启动方式；共享宿主必须全部属于名单；写入/核验/恢复失败各阶段有次数上限；不扩大到游戏、手工白名单、只保护的反作弊或游戏必要依赖"));
                case PolicyCatalog.KeyGameScheduling: return F(key, Lang.TranslateChinese("渲染线程调度"), Lang.TranslateChinese("逐游戏开关：未存方案或方案为游戏优先的游戏默认开，手动关过、存过其他方案但没开过的保持关；无界面的全局总闸默认开；开启即给游戏锁定高性能QoS（不被节流到能效核，Win11计时器请求照常）；六方案：游戏优先（推荐、默认：游戏进程High、磁盘与GPU提优，渲染线程最高、配合线程略提，不固定核心不清核）、渲染优先、渲染专核、多核协同、依赖协同、专核强化（须核验固定核、清核覆盖与运行条件，失效撤回）；除专核强化外都把游戏进程设为高（渲染+2即15、成员+1即14），渲染专核另把渲染线程固定到独立核"), Lang.TranslateChinese("渲染排队/换核/同核竞争/依赖等待明显时对照；CPU满载时其他程序可能变慢；显式开启需确认；保存下局生效，关闭立即撤回，关闭/退局/程序退出恢复本方修改并保留外部后续改动；已接管属性运行期纠偏不设累计上限、失败2..30秒退避，身份/范围未知暂停；效果因游戏而异，不保证增帧"));
                case "GmEnglishInputV1": return F(key, Lang.TranslateChinese("游戏窗口切换英文输入"), Lang.TranslateChinese("入局为游戏窗口尝试一次英文输入布局"), Lang.TranslateChinese("避免输入法误触/组合输入干扰；退局不自动切回原输入法"));
                case "GameFamilyBackground": return F(key, Lang.TranslateChinese("逐游戏家族后台治理"), Lang.TranslateChinese("开启后本局可压制游戏目录内非本体进程、启动链上级、游戏子进程及Steam主程序/网页/覆盖层，不压制游戏本体，LeagueClient/RiotClientServices仍保护"), Lang.TranslateChinese("LoL未开对局真无头时的大厅、常驻启动器或Steam网页占用；可能影响覆盖层/聊天/加载；须先出现已观测渲染，开启有警告；白名单、反作弊、独立录屏与输入音频仍保护"));
                case "GameDisableFullscreenOptimizationsV1": return F(key, Lang.TranslateChinese("逐游戏禁用全屏优化"), Lang.TranslateChinese("设置所选游戏的Windows全屏优化兼容标志"), Lang.TranslateChinese("排查显示或切屏问题；可能回退，下次启动游戏生效"));
                case "GmAutoCoresV2": return F(key, Lang.TranslateChinese("逐游戏核心范围"), Lang.TranslateChinese("在选核页手动保存逻辑CPU范围，下次启动游戏生效；全选且不隔离视为不限制；隔离时后台避让并自动硬亲和性，范围外至少留两颗完整物理核；优先用Job锁定进程范围防止自行改绑，Job不可用且清理完成时回退进程硬亲和性，原因写入诊断日志；各系列CPU按Windows拓扑/CPU Sets及受支持的逐核CPUID确认性能核/能效核，不按型号、编号或SMT猜测，证据不足显示未确认"), Lang.TranslateChinese("减少跨缓存域/SMT争用；范围太窄会限并行；游戏已有自定义线程掩码或显式线程CPU Set ID、线程集合不稳定或读取不明时跳过整进程绑核且禁止亲和性回退；写入前锁占用或线程变动每进程最多试3次；权限拒绝或身份不符停止；退局先清Job限制再恢复原范围，独立守护兜底；不能保证还原游戏运行期自行改的线程分配"));
                case "GameNvRebarV1": return F(key, Lang.TranslateChinese("逐游戏NVIDIA ReBAR强开"), Lang.TranslateChinese("设置游戏驱动ReBAR偏好"), Lang.TranslateChinese("支持ReBAR的NVIDIA平台；可能掉帧，逐游戏测试，重启游戏；关闭或退局恢复"));
                case PolicyCatalog.KeyAceGovernance: return F(key, Lang.TranslateChinese("逐游戏反作弊压制"), Lang.TranslateChinese("已核实的ACE（Program Files\\AntiCheatExpert下且文件名与进程名一致）随后台治理的开关、档位、切出与退局恢复按普通后台治理；关闭则只保护；EAC、BattlEye、Vanguard等其他反作弊始终只保护"), Lang.TranslateChinese("ACE占用明显时保持开；掉线或ACE报错时关闭；后台扫描每轮读取，切换几秒内生效，退局复位只保护；手工加白的ACE照常豁免"));
                case "GmBoost": return F(key, Lang.TranslateChinese("逐游戏资源优先级"), Lang.TranslateChinese("游戏磁盘I/O高、GPU调度类高；需渲染线程调度开启；游戏优先已包含；进程High由方案决定（除专核强化外都设高，已是Realtime保留）；内存优先级已退役"), Lang.TranslateChinese("游戏I/O或GPU被后台争用；需权限与驱动支持并读回；保存下局生效，退局按归属恢复，外部不同值保留"));
                case "PowerPlanOn": return F(key, Lang.TranslateChinese("对局电源计划"), Lang.TranslateChinese("选择不切换、由软件调度或指定已有计划，退局返回原计划；托管时原生逐项写入与回读，优先卓越性能、回退高性能，均缺失时核验后复制原计划（可为平衡）作独立候选，不改原计划；组策略锁定项跳过并记录；隐藏模板按GUID核验，权限或系统拒绝则停止"), Lang.TranslateChinese("减轻省电约束；对局中电源计划守护监视计划存储，外部切走或改托管参数即写回，不设累计上限、不自动改成不切换，对方反复改回时放慢到最多15秒；首次应用失败每项最多3次；指定计划只切换不改参数；读取失败不视为核验成功"));
                case "GmCacheWarmV2": return F(key, Lang.TranslateChinese("缓存预热"), Lang.TranslateChinese("空闲时预读游戏文件进系统缓存，入局取消"), Lang.TranslateChinese("冷加载/切图；热缓存收益小，需空闲内存"));
                case "GmStandbyCleanerV2": return F(key, Lang.TranslateChinese("待机列表清理"), Lang.TranslateChinese("按列表大小、可用内存和轮询阈值清理；可选默认/保守/激进/自定义"), Lang.TranslateChinese("特定内存压力；频繁清理会丢缓存并增加卡顿"));
                case "AutoTimerTick": return F(key, Lang.TranslateChinese("计时器节拍"), Lang.TranslateChinese("useplatformclock=no、useplatformtick=no、disabledynamictick=yes；故障后停止本阶段应用并按有限预算恢复"), Lang.TranslateChinese("计时敏感场景对照试验；可能增耗，持久配置需重启"));
                case "AutoGlobalTimer": return F(key, Lang.TranslateChinese("全局计时器分辨率"), Lang.TranslateChinese("配置全局请求行为，对局按条件请求0.5ms并读回；系统精度不足时显示受限；故障后停止本阶段请求，有限次数恢复旧请求及自身豁免状态，耗尽保留恢复记录；重启后核验环境"), Lang.TranslateChinese("短等待敏感场景；新开启需build20348+，全局配置需重启，运行故障不被环境就绪遮蔽；可能增耗"));
                case "GmPauseUpdate": return F(key, Lang.TranslateChinese("暂停Windows更新"), Lang.TranslateChinese("对局暂停可控制的Windows更新活动"), Lang.TranslateChinese("更新正在下载/安装占资源，退局恢复"));
                case "GmIntelEnduranceV1": return F(key, Lang.TranslateChinese("关闭Intel图形续航限制"), Lang.TranslateChinese("解除支持设备的Endurance Gaming限帧"), Lang.TranslateChinese("Intel驱动确有续航限制；可能增耗"));
                case "NvLowLat": return F(key, Lang.TranslateChinese("NVIDIA驱动低延迟"), Lang.TranslateChinese("开启/关闭适用的驱动低延迟偏好"), Lang.TranslateChinese("受支持API的预渲染排队；不等于通用Reflex，部分需重启游戏"));
                case "NvMaxPerf": return F(key, Lang.TranslateChinese("NVIDIA最高性能偏好"), Lang.TranslateChinese("设置游戏驱动最大性能电源偏好，不超频"), Lang.TranslateChinese("降频/唤醒波动；可能增热，部分需重启游戏"));
                case "NvShaderCache": return F(key, Lang.TranslateChinese("NVIDIA着色器缓存无上限"), Lang.TranslateChinese("调整驱动着色器缓存容量"), Lang.TranslateChinese("缓存淘汰导致重编译；占用磁盘，通常重启游戏"));
                default: return F(key, Lang.Cur == 3 ? Lang.Ui(item.LangKey) : item.LangKey, Lang.TranslateChinese("当前策略项，详细用途未核验"), Lang.TranslateChinese("先核对该版本实现再建议"));
            }
        }

        private static Feature ActionFeature(PerformanceAction action)
        {
            switch (action.Key)
            {
                case PerformanceActionCatalog.Mitigations: return F(action.Key, (Lang.Cur == 3 ? action.Title : action.Zh), Lang.TranslateChinese("关闭库中游戏CFG、EAF/EAF+、IAF、CallerCheck；32位另含SimExec"), Lang.TranslateChinese("对应防护确有开销；降低游戏防护，重启游戏"));
                case PerformanceActionCatalog.ShortNames: return F(action.Key, (Lang.Cur == 3 ? action.Title : action.Zh), Lang.TranslateChinese("停止NTFS游戏卷新增8.3短名称，保留已有名称"), Lang.TranslateChinese("大量文件创建；老软件可能依赖短名称"));
                case PerformanceActionCatalog.OpenGl: return F(action.Key, (Lang.Cur == 3 ? action.Title : action.Zh), Lang.TranslateChinese("设置NVIDIA OpenGL驱动多线程"), Lang.TranslateChinese("NVIDIA OpenGL游戏；可能增加争用，重启游戏"));
                case PerformanceActionCatalog.CpuMitigations: return F(action.Key, (Lang.Cur == 3 ? action.Title : action.Zh), Lang.TranslateChinese("Intel/AMD、Win10 2004+可选扩展关闭策略：Spectre V2/Meltdown、取消SSBD与AMD用户到内核BTI显式启用，适用时涉及MDS/TAA/L1TF组合；保留其他位，关闭恢复原值，不能关闭全部缓解"), Lang.TranslateChinese("相应缓解开销构成瓶颈；降低防护，应用和恢复需重启；旧版范围不自动扩大"));
                case PerformanceActionCatalog.LastAccess: return F(action.Key, (Lang.Cur == 3 ? action.Title : action.Zh), Lang.TranslateChinese("停止NTFS最后访问时间维护；系统增强成员"), Lang.TranslateChinese("文件元数据负载；原本关闭无新增收益，需重启"));
                case PerformanceActionCatalog.Quantum: return F(action.Key, (Lang.Cur == 3 ? action.Title : action.Zh), Lang.TranslateChinese("选择短可变3:1、长固定1:1或调试档；优先在线应用和恢复，系统拒绝或缺少可还原的原值时需重启"), Lang.TranslateChinese("游戏/交互选短可变，后台批算选长固定；比例不是FPS倍数，以在线结果或重启提示为准"));
                case PerformanceActionCatalog.StorageIdle: return F(action.Key, (Lang.Cur == 3 ? action.Title : action.Zh), Lang.TranslateChinese("关闭已登记StorPort磁盘空闲省电；存储组合成员"), Lang.TranslateChinese("磁盘唤醒卡顿；增加功耗，需重启"));
                case PerformanceActionCatalog.StorageRuntime: return F(action.Key, (Lang.Cur == 3 ? action.Title : action.Zh), Lang.TranslateChinese("关闭已登记StorPort控制器运行时省电；存储组合成员"), Lang.TranslateChinese("控制器唤醒延迟；增加功耗，需重启"));
                case PerformanceActionCatalog.StorageCold: return F(action.Key, (Lang.Cur == 3 ? action.Title : action.Zh), Lang.TranslateChinese("禁止已登记StorPort控制器进入D3Cold；存储组合成员"), Lang.TranslateChinese("深度省电唤醒问题；增加睡眠功耗，需重启"));
                case PerformanceActionCatalog.AudioIdle: return F(action.Key, (Lang.Cur == 3 ? action.Title : action.Zh), Lang.TranslateChinese("关闭已有PortCls交流空闲省电；系统增强成员"), Lang.TranslateChinese("首次出声唤醒等待；增加功耗，需重启"));
                default: return F(action.Key, (Lang.Cur == 3 ? action.Title : action.Zh), (Lang.Cur == 3 ? action.Detail : action.DetailZh), Lang.TranslateChinese("以本机支持情况为准"));
            }
        }

        private static Feature[] ExtraFeatures()
        {
            return new[] {
                F("GameModeOn", Lang.TranslateChinese("守护总开关"), Lang.TranslateChinese("启停游戏会话自动优化；概览激活时显示本局已压制进程/已暂停服务数量，去重且只计已确认执行项；智能切出时明确显示后台压制已释放或恢复中，服务数量独立核验"), Lang.TranslateChinese("需要自动优化时开启；失败/待确认不计入，退局或停守护收起；手动持久配置按各自设置处理")),
                F("GmAwake", Lang.TranslateChinese("对局保持唤醒"), Lang.TranslateChinese("守护开启且游戏前台时自动阻止显示器/系统休眠；切出、退局或停守护释放，不读取旧开关偏好"), Lang.TranslateChinese("防止黑屏/休眠，不是禁止CPU空闲，不阻止手动休眠")),
                F("GmPauseDl", Lang.TranslateChinese("传递优化下载让路"), Lang.TranslateChinese("后台治理开启后每局开局确认一次传递优化后台下载，仅确认后台下载活动时暂停（限带宽并停止DoSvc），未知/空闲/前台下载不触发，开局未检测到本局不再暂停，读取失败每局最多3次；智能切出恢复，竞技切出保留，停用或退局恢复"), Lang.TranslateChinese("DO争网或磁盘；仅处理此组件，不控制Steam等下载器")),
                F("GmPauseMaintenanceV1", Lang.TranslateChinese("对局阻止自动维护"), Lang.TranslateChinese("后台治理开启时对局中阻止新的Windows自动维护开始，退局恢复"), Lang.TranslateChinese("维护任务竞争资源；不中止已在运行的维护")),
                F("GmGpuDemote", Lang.TranslateChinese("后台GPU让位"), Lang.TranslateChinese("随后台治理把合格后台的GPU调度类降为Idle；仅HAGS状态可读且关闭时执行，读不出原值则跳过；停用、移出治理范围或退局恢复原值"), Lang.TranslateChinese("后台图形任务争GPU；按场景处理前台，保护游戏、依赖与白名单")),
                F("ConfigurationPresets", Lang.TranslateChinese("一键配置"), Lang.TranslateChinese("调优页分类栏右侧单入口；作者同款/极限配置共") + ConfigurationPresetCatalog.Entries.Length.ToString(CultureInfo.InvariantCulture) + Lang.TranslateChinese("项，系统增强子项分别设置；点击配置即应用，完成提醒重启电脑；手动修改后显示自定义；对局中不应用；游戏卡片深度调优不纳入"), Lang.TranslateChinese("不可用项跳过，失败简要提示可重试；作者同款：正常后台治理、暂停Windows更新、基础CPU缓解关闭范围、关闭VBS、交流音频保持唤醒、计时器恒定节拍与全局计时器分辨率，其余关；极限另开激进后台治理、游戏盘短文件名、CFG减负、限制Windows应用后台、NVIDIA最高性能与低延迟、NTFS最后访问时间减负、扩展CPU缓解范围、存储电源组合、NTFS缓存池、GPU锁频与功耗墙；两套均开多媒体调度、均不关闭Game DVR、CPU空闲跟随原策略、前台时间片恢复原配置；从扩展切回基础先恢复；降低相应防护、需重启；不改智能/竞技运行策略、白名单；作者同款不是Windows出厂恢复")),
                F("OverviewTelemetry", Lang.TranslateChinese("概览硬件/占用监控"), Lang.TranslateChinese("显示CPU/GPU/内存/显示器型号与CPU/GPU/内存占用；窗口前台且无对局每2秒刷新，对局中或失焦每10秒；GPU读取连续失败3次后本次运行暂停读取；隐藏或离页停止读取"), Lang.TranslateChinese("查看资源状态，不测游戏FPS，不采集游戏帧率")),
                F("StartupSecurityCompatibility", Lang.TranslateChinese("杀毒兼容提示"), Lang.TranslateChinese("启动只查一次名称/状态，之后含局内只读快照，下次启动更新；启用含Defender均警告，仅停用不警告，失败未知"), Lang.TranslateChinese("概览游戏/守护行上方黄色SVG，悬停摘要、点击弹窗：加白/行为防护说明；底部左不再显示（跨启动）、右知道了；加白/隐藏不代表放行或已拦截/掉帧；不改守护/安全设置，无顶部入口")),
                F("ModeSmart", Lang.TranslateChinese("智能模式"), Lang.TranslateChinese("CPU≥70%持续10秒扩围，每局最多3次；<60%持续5秒后平稳120秒恢复；切出释放并保护前台"), Lang.TranslateChinese("常切屏、兼顾浏览器/语音；切屏不全清优先级/绑核")),
                F("ModeCompetitive", Lang.TranslateChinese("竞技模式"), Lang.TranslateChinese("较广治理，切出保留，合格软件前台不豁免；保护游戏、白名单、必要依赖"), Lang.TranslateChinese("后台争用；用户会话范围回归2223，不普压Session0服务")),
                F("ManagedPower2223", Lang.TranslateChinese("托管电源"), Lang.TranslateChinese("核验台式机按CPU匹配新版预置，CPU/平台无法安全匹配保留原计划；笔记本、检测中或未知机型沿用3.1.1.0旧策略；不靠电池判机型，UPS不当笔记本；智能/竞技共用所选策略参数；无来源选择器，旧手选无效；复制标准模板，按AC/DC及架构规则逐项设置；对局中参数漂移原地写回，结构不符或被切走才重建，不设累计上限"), Lang.TranslateChinese("按厂商与设备型号自动识别掌机（含ROG Ally和GPD WIN系列）；掌机不切换，不创建/维护托管计划，指定已有计划也不接管，保留原选择偏好，交给系统和厂商软件管理；不把S0或带电池单独视为掌机；维护避开实验室持有参数，换计划先恢复实验室，退局还原；可能增耗增热")),
                F("ManagedPowerDynamic", Lang.TranslateChinese("托管电源动态参数"), Lang.TranslateChinese("交流AC与电池DC分别设置；旧策略采用原竞技基线，台式机新版按CPU匹配，智能/竞技共用；按CPU拓扑与供电特征调整处理器、停泊、EPP与睿频，沿用旧策略设备DC放宽规则；场景/升档/切出不改参数"), Lang.TranslateChinese("由软件调度时自动计算，无额外开关；不切换/指定已有计划优先，退局恢复；实验室覆盖按自身原值恢复")),
                F("GmMmcss", Lang.TranslateChinese("多媒体调度"), Lang.TranslateChinese("独立开关，默认开：SystemResponsiveness=10、Games调度类High、NoLazyMode=1"), Lang.TranslateChinese("参与MMCSS的游戏/多媒体线程；守护开启期间写入，关开关/停守护/退出恢复，崩溃后启动自愈；一键配置两套都开")),
                F("AutoLaptopPerformanceV2", Lang.TranslateChinese("笔记本厂商性能模式"), Lang.TranslateChinese("在顶部策略设置→对局电源下方开启；仅在已核验支持的笔记本显示，检测中及台式机等不支持设备隐藏；联想 SmartFan 核验原档，ASUS 通过运行中的 G-Helper 前五个配置档切 Turbo，由新驱动确认记录核验并在退局恢复完整原配置"), Lang.TranslateChinese("默认关；交流供电、游戏会话且电源非不切换；ASUS DSTS 仅用于接口识别，保留已有自定义 Turbo/全速；外部改档让出，增热/噪声，不等于Windows电源计划")),
                F("Hags", Lang.TranslateChinese("硬件加速GPU调度HAGS"), Lang.TranslateChinese("开启/关闭；原配置按记录恢复"), Lang.TranslateChinese("系统/驱动/GPU支持、状态可读；重启核验")),
                F("WindowedOptimizations", Lang.TranslateChinese("窗口化游戏优化"), Lang.TranslateChinese("开启/关闭窗口/无边框优化；原配置按记录恢复"), Lang.TranslateChinese("Win11≥22H2/DX10、11，状态可读；重启游戏")),
                F("VariableRefreshRate", Lang.TranslateChinese("Windows可变刷新率补充"), Lang.TranslateChinese("开启/关闭DX11全屏VRR补充；原配置按记录恢复"), Lang.TranslateChinese("显示器/驱动支持、状态可读；重启游戏")),
                F("NvVrrWindowed", Lang.TranslateChinese("NVIDIA窗口VRR补充"), Lang.TranslateChinese("对局为已启用全屏G-SYNC的设备补充窗口VRR"), Lang.TranslateChinese("兼容NVIDIA窗口游戏；部分面板闪烁，退局恢复")),
                F("AmdSam", "AMD Smart Access Memory", Lang.TranslateChinese("开启/关闭；原配置按记录恢复；需BIOS ReBAR/Above4G"), Lang.TranslateChinese("AMD支持、状态可读；需重启")),
                F("GameDvrOff", Lang.TranslateChinese("关闭Game DVR与Xbox后台录制"), Lang.TranslateChinese("守护开启期间关闭Windows后台游戏录制，即时回放与录制热键不可用"), Lang.TranslateChinese("录制占编码/GPU/磁盘；关开关、停守护或退出还原；需要录屏时关闭；一键配置两套均不关闭")),
                F("EnvPerformanceBundleV1", Lang.TranslateChinese("系统性能增强组合"), Lang.TranslateChinese("联动VBS/Hypervisor、CPU缓解、NTFS最后访问、交流音频省电四项"), Lang.TranslateChinese("对应开销明显；影响防护、WSL2/Hyper-V及部分反作弊要求；关闭恢复原值，需重启")),
                F("EnvVbsDisabledV1", Lang.TranslateChinese("系统增强·VBS/Hypervisor"), Lang.TranslateChinese("关闭VBS、内存完整性和相关hypervisor启动项"), Lang.TranslateChinese("虚拟化安全开销；影响WSL2/Hyper-V及部分反作弊，需重启")),
                F("EnvStoragePowerGroupV1", Lang.TranslateChinese("存储设备电源管理组合"), Lang.TranslateChinese("联动StorPort磁盘空闲、控制器运行时省电、D3Cold三项"), Lang.TranslateChinese("存储唤醒卡顿；仅修改已有登记配置，增耗；关闭恢复原值，需重启")),
                F("LabEppYieldV1", Lang.TranslateChinese("实验室·EPP功耗让路"), Lang.TranslateChinese("实际自主性能模式支持且GPU忙/CPU有余量时调EPP让共享功耗，验证失败回退"), Lang.TranslateChinese("插电笔记本+托管+可读功耗；台式/电池不执行，可能转CPU瓶颈；换计划/空闲策略先恢复后重采样")),
                F("LabGpuClockMaxV1", Lang.TranslateChinese("实验室·GPU频率锁最大值"), Lang.TranslateChinese("请求支持驱动的最高核心频率，仍受温度/功耗限制"), Lang.TranslateChinese("支持的NVIDIA/AMD接口，频率波动；增热，勿叠加锁频工具；退局NVIDIA回自动、AMD还原")),
                F("LabGpuPowerMaxV1", Lang.TranslateChinese("实验室·GPU功耗墙拉满"), Lang.TranslateChinese("请求驱动允许的最大GPU功耗上限，不改电压"), Lang.TranslateChinese("GPU确受功耗墙限制；增热增耗，关闭/退局恢复")),
                F("LabAudioLowBufferV1", Lang.TranslateChinese("实验室·音频低缓冲流"), Lang.TranslateChinese("为默认输出申请较短共享音频周期并读回"), Lang.TranslateChinese("支持的同设备共享音频，跳过蓝牙；可能爆音/增唤醒，退局释放")),
                F("LabCpuIdleStrategyV1", Lang.TranslateChinese("实验室·CPU空闲策略"), Lang.TranslateChinese("跟随当前计划基准/提高空闲门槛/禁止CPU空闲，在计划生效后覆盖当前计划，无需托管"), Lang.TranslateChinese("交流对局唤醒瓶颈；禁止空闲增热增耗；关闭/切策略/换计划恢复该计划原值，退局恢复")),
                F("Whitelist", Lang.TranslateChinese("白名单与依赖保护"), Lang.TranslateChinese("从运行进程/EXE/拖放添加路径/家族保护，或从已安装服务精确添加服务白名单（共享宿主整体豁免，只豁免普通后台含并入的微软后台与ACE，不启动服务）；运行列表含浏览器、聊天/开发工具及最小化/托盘应用，排除系统与反作弊；内置七类：Windows系统进程、网游加速器、显卡驱动与硬件功耗散热控制、反作弊与游戏保护组件（按组标注ACE按游戏开关压制/仅保护/仅记录）、输入音频与外设驱动链、直播与推流工具、语音通话软件（KOOK/YY/TeamSpeak/Mumble/Discord，进程名须与程序文件名一致）；可查看目录并按具体名称搜索；进程访问受限历史有记录时显示，可查看/清空；直播主程序及专用组件保护已核验子孙，父进程退出后仍保留存活成员，独立组件单独识别，不随游戏家族后台治理关闭；来源筛选、移除、恢复平台保护、恢复预设（删除全部自定义）"), Lang.TranslateChinese("保护语音/外设/录屏/直播；过宽放过负载，无逐项限速")),
                F("GameLibrary", Lang.TranslateChinese("游戏库/自动入库/忽略名单"), Lang.TranslateChinese("添加程序/运行目标（含最小化/托盘游戏）、平台扫描/自动收录、搜索、重命名、换/还原封面、移除/撤销忽略；卡片深度调优含核心范围、渲染线程调度、反作弊压制、家族后台、ReBAR、全屏优化"), Lang.TranslateChinese("识别游戏；自动收录默认关闭，移除后防重复收录；入库仅待检测；入场须连续两窗有画面提交且GPU 3D≥3%，或≥10%且家族最高，无GPU数据不入场；强制接管除外，入场后低负载不退；已确认进程不被启动器改写；登记启动器仍切到渲染进程")),
                F("GameForceTrigger", Lang.TranslateChinese("逐游戏强制接管"), Lang.TranslateChinese("对有明确EXE的游戏开启（需确认），该EXE进程存在即保持游戏会话"), Lang.TranslateChinese("正常识别失败时使用；误用于普通程序会错误触发游戏策略")),
                F("GameCoreIsolation", Lang.TranslateChinese("逐游戏核心隔离/核心选择"), Lang.TranslateChinese("后台避让游戏整颗物理核；按全部/P核/E核/CCD(含缓存)/无SMT/补全SMT快捷选核；手动范围优先于游戏自设的线程分配，照常绑定并记录接管线程数；落核停止区分访问被拒、恢复未结清与读回不符，保留原始阶段和错误码，不凭权限失败断言反作弊拦截"), Lang.TranslateChinese("有备用核且拓扑/分配可靠；范围过小可能回退，不隔离中断")),
                F("GameCriticalThreadHardRuleV1", Lang.TranslateChinese("逐游戏调度方案"), Lang.TranslateChinese("方案窗口选六方案之一；清核让其他游戏任务避开关键核心（游戏优先与依赖协同不可用）；资源优先级开关（游戏优先已包含）；显示上局放置前的排队/占用/换核读数仅供对照；不存线程号，旧手动主核转渲染专核并保留清核，旧对照和温和提优按关闭"), Lang.TranslateChinese("清核可能拖慢多线程游戏，系统任务仍可用这些核；选核遵守游戏核心范围；编辑下局生效，关闭立即恢复；切出停采样保留配置，切回核验")),
                F("LolColumnEnabled", Lang.TranslateChinese("英雄联盟专属卡片"), Lang.TranslateChinese("游戏库有LoL时卡片自动启用，提供借壳启动与对局真无头两个开关"), Lang.TranslateChinese("需核验LOL/客户端身份；受客户端版本影响")),
                F("LolColumnCleanupEnabled", Lang.TranslateChinese("LoL借壳启动"), Lang.TranslateChinese("正常登录后LCU确认大厅可用，精确结束WeGame、Cross及腾讯附加进程；不注入、不改内存、不碰游戏文件；立即净化可手动执行（需LCU就绪）"), Lang.TranslateChinese("平台附属持续占用；WeGame社交/助手暂不可用")),
                F("LolColumnHeadlessEnabled", Lang.TranslateChinese("LoL对局真无头"), Lang.TranslateChinese("进入对局后用LCU原生接口关闭大厅界面，对局结束由守护链拉起；大厅反复重生达上限本局熔断恢复；恢复界面可手动拉起"), Lang.TranslateChinese("大厅占CPU/GPU；需可靠客户端接口")),
                F("LolAddonCleanup", Lang.TranslateChinese("LOL插件检查/删除"), Lang.TranslateChinese("游戏卡片后台检查并提示可清理附加层，点击进入专属优化，用户确认后删除"), Lang.TranslateChinese("排查AI教练、录制等附加层；须先完全退出LoL与WeGame，删除不可恢复，客户端更新或修复会重新下载")),
                F("ManualMemoryCleanup", Lang.TranslateChinese("手动极限清理"), Lang.TranslateChinese("概览内存卡手动执行，需管理员；游戏中仅回收Pavise自身内存；非游戏时合并页面、工作集、文件缓存、注册表缓存、修改页、待机列表"), Lang.TranslateChinese("手动释放内存；清空缓存可能增加后续重载/卡顿")),
                F("Checkup", Lang.TranslateChinese("体检"), Lang.TranslateChinese("约120秒只读采样，可取消/重新体检；按结果提供下列修复入口"), Lang.TranslateChinese("系统排障；不测FPS、1% low、帧时间或输入延迟")),
                F("SettingsStartup", Lang.TranslateChinese("开机启动"), Lang.TranslateChinese("管理Pavise管理员登录启动任务，后台启动进托盘"), Lang.TranslateChinese("需要自动守护")),
                F("AutoHideOnGame", Lang.TranslateChinese("入局自动隐藏"), Lang.TranslateChinese("检测到游戏10秒后收回托盘，每局一次，保留后台守护"), Lang.TranslateChinese("减少界面干扰/可见刷新")),
                F("GameExitGrace", Lang.TranslateChinese("退出宽限"), Lang.TranslateChinese("游戏证据暂失后等0/2/5/8/15/30秒（默认8）再恢复会话策略，后台压制先解除不受等待影响"), Lang.TranslateChinese("避免短暂重启导致反复切换；0秒可能把进程交接误判退局；会延迟退局恢复")),
                F("ShaderCleanup", Lang.TranslateChinese("着色器缓存清理"), Lang.TranslateChinese("手动删受支持的着色器缓存"), Lang.TranslateChinese("排查旧/坏缓存；重编译可能卡顿，不宜每局清")),
                F("SettingsReset", Lang.TranslateChinese("重置设置"), Lang.TranslateChinese("清空设置、游戏库、白名单、忽略记录和日志后重开，保护原值恢复记录；对局中拒绝"), Lang.TranslateChinese("错误配置排障；先恢复已拥有的系统修改，会丢相应偏好")),
                F("SettingsAppearance", Lang.TranslateChinese("视觉定制/窗口/托盘"), Lang.TranslateChinese("语言/明暗/背景图/背景浓度/模式配色/界面缩放（跟随Windows或100%–300%）；") + MotionCount() + Lang.TranslateChinese("款动效（") + (Lang.Cur == 3 ? CoreMotionCatalog.PortugueseChoices : CoreMotionCatalog.ChineseChoices) + Lang.TranslateChinese("）同库，两模式可同选，选中即应用/恢复默认；开合/旋转/蓄能脉冲，配色随主题，失焦/隐藏/离页/对局停播；拖动/最小化/托盘开关"), Lang.TranslateChinese("界面可读性，不增帧")),
                F("LogWritesEnabled", Lang.TranslateChinese("写入日志"), Lang.TranslateChinese("启停Pavise运行日志写入"), Lang.TranslateChinese("减少日志写入或保留排障证据；关闭后记录不完整")),
                F("LogManagement", Lang.TranslateChinese("运行日志"), Lang.TranslateChinese("设置→维护→日志；默认动作日志：当前或最近会话的成功压制进程名单、已生效功能、已核验游戏与渲染线程CPU范围及避让、清核核验状态、恢复与待确认/失败；切换诊断日志查看构建身份与失败阶段；可选保存位置和文件名，动作导出从完整日志提取，诊断导出完整原文件，不限屏幕尾部；关闭写入仍可导出，不改原日志；清空同一日志源"), Lang.TranslateChinese("不重新采样或执行调度；记录不足不代表成功；清空不还原系统")),
                F("SettingsLogsUninstall", Lang.TranslateChinese("版本/说明/支持/卸载"), Lang.TranslateChinese("版本说明/官网/源码/捐赠；卸载先恢复"), Lang.TranslateChinese("版本核验；说明仅展示一次")),
                F("SoftwareUpdate", Lang.TranslateChinese("软件内更新/失败手动下载"), Lang.TranslateChinese("在设置→关于中检查更新；验签下载EXE，显示下载/校验/安装进度；检查和下载可取消，取消后可重新检查，不将旧版误报为新版；更新期间禁止退出、替换不可取消；安全退出后原位替换，新版启动确认后删除旧版并同步启动入口；失败同一弹窗转手动更新、解除关闭限制，提供夸克官网入口/GitHub"), Lang.TranslateChinese("退局/维护结束后联网管理员操作；保留个人数据/恢复资产；替换失败先回滚，不强杀活跃进程；备用下载按页说明更新")),
                F("SettingsAiTuning", Lang.TranslateChinese("AI调优提示词"), Lang.TranslateChinese("只读汇总硬件/全部功能/设置，补充目标、预览/复制"), Lang.TranslateChinese("供自选AI建议，不联网/应用"))
            };
        }

        private static Feature[] CheckupFeatures()
        {
            return new[] {
                F("AuditGpuIrq", Lang.TranslateChinese("体检·显卡中断放置/验证"), Lang.TranslateChinese("采样显卡ISR/DPC落点与P99，有证据时将中断移出游戏保留核，重启后验证；退化/驱动重置则尝试回滚"), Lang.TranslateChinese("至少300个有效事件、单处理器组、可靠保留区/区外核心；游戏中禁止应用，未知/外部配置等情况跳过")),
                F("AuditGpuIrqRestore", Lang.TranslateChinese("体检·恢复显卡中断配置"), Lang.TranslateChinese("还原本工具拥有的显卡IRQ放置；1.x/2.x留下的中断迁移与中断优先级不再恢复"), Lang.TranslateChinese("撤销本工具中断调整或处理残留；依记录恢复，按提示重启")),
                F("AuditMsi", Lang.TranslateChinese("体检·显卡MSI中断修复"), Lang.TranslateChinese("将当前显卡已有MSISupported=0修复为1，可恢复"), Lang.TranslateChinese("显卡MSI被明确关闭；不为所有设备强造设置，需重启；与中断放置不同")),
                F("AuditSlowDrivers", Lang.TranslateChinese("体检·驱动延迟诊断"), Lang.TranslateChinese("展示较慢驱动的最大耗时、P99与设备线索"), Lang.TranslateChinese("排查DPC/ISR延迟；不提供自动修复所有驱动")),
                F("AuditMemoryCompression", Lang.TranslateChinese("体检·内存压缩修复"), Lang.TranslateChinese("读取内存压缩/页面合并，压缩关闭时可启用，并按记录恢复"), Lang.TranslateChinese("排查内存压力；本项不是关闭内存压缩")),
                F("AuditNetworkThrottle", Lang.TranslateChinese("体检·网络限流值修复"), Lang.TranslateChinese("异常NetworkThrottlingIndex修复到10；1–70和0xFFFFFFFF均有效，后者关闭网络节流，保留不改"), Lang.TranslateChinese("不因非默认而修复；旧版修复记录仍可恢复；联网异常应撤回")),
                F("AuditLinkMetric", Lang.TranslateChinese("体检·有线优先修复"), Lang.TranslateChinese("有可靠有线网关却默认走Wi-Fi时调整有线metric；撤销回自动metric"), Lang.TranslateChinese("路由选择异常且无歧义；不承诺保留任意原手动metric")),
                F("AuditFth", Lang.TranslateChinese("体检·容错堆FTH修复"), Lang.TranslateChinese("对命中程序登记FTH排除、移除已挂载兼容补丁引用并保留文件，可恢复"), Lang.TranslateChinese("FTH补丁确实介入；可能影响容错，不是全局关闭所有兼容机制")),
                F("AuditQuantum", Lang.TranslateChinese("体检·前台时间片检查/入口"), Lang.TranslateChinese("显示当前时间片线索并跳转「前台时间片」选择器"), Lang.TranslateChinese("沿用时间片设置，无额外开关")),
                F("AuditFixAll", Lang.TranslateChinese("体检·一键修复"), Lang.TranslateChinese("依次执行当前符合条件的体检修复，保留MSI/FTH等确认流程"), Lang.TranslateChinese("确诊后手动执行，非开启全部优化")),
                F("AuditPlatform", Lang.TranslateChinese("体检·平台能力检测"), Lang.TranslateChinese("检查显卡清单、NVIDIA接口、CPU Sets分区、EcoQoS、Windows版本、3D V-Cache驱动与Windows游戏模式"), Lang.TranslateChinese("能力不等于可由Pavise开关")),
                F("AuditCpu", Lang.TranslateChinese("体检·CPU/后台负载检测"), Lang.TranslateChinese("检查CPU拓扑、整机占用、近7天降频事件、后台占用前三和灯效常驻软件"), Lang.TranslateChinese("定位调度/功耗/后台争用线索")),
                F("AuditMemory", Lang.TranslateChinese("体检·内存检测"), Lang.TranslateChinese("检查内存容量/使用率/余量、页面文件、频率、条数/通道布局及压缩/页面合并"), Lang.TranslateChinese("发现内存压力或配置线索；不提供XMP/内存超频")),
                F("AuditGraphics", Lang.TranslateChinese("体检·显示/图形检测"), Lang.TranslateChinese("检查主屏/多屏刷新率、NVIDIA降频标记、ReBAR、HAGS、MPO、Game DVR"), Lang.TranslateChinese("发现呈现与驱动配置线索；不是刷新率/MPO调整开关")),
                F("AuditPower", Lang.TranslateChinese("体检·电源/系统检测"), Lang.TranslateChinese("检查当前计划、节能模式、供电、核心停泊、无输入降级及Ryzen分支预测优化的系统版本线索"), Lang.TranslateChinese("定位电源策略/系统版本问题，不直接修改BIOS")),
                F("AuditInput", Lang.TranslateChinese("体检·输入链检测"), Lang.TranslateChinese("检查键鼠连接类型/蓝牙、筛选键、辅助功能热键拦截和第三方ThreadDpcEnable改动"), Lang.TranslateChinese("排查输入体验线索，不测真实端到端输入延迟")),
                F("AuditStorageNetwork", Lang.TranslateChinese("体检·存储/网络检测"), Lang.TranslateChinese("检查DirectStorage/BypassIO直通/阻塞原因、链路、默认路由、限流值"), Lang.TranslateChinese("定位存储过滤/网络配置，无网络内核旁路")),
                F("AuditResults", Lang.TranslateChinese("体检·结论/证据/恢复记录"), Lang.TranslateChinese("汇总问题线索、证据等级、操作入口、显卡中断恢复记录"), Lang.TranslateChinese("区分建议/检测/修复，不是增帧测试"))
            };
        }

        private static string MotionCount()
        {
            return CoreMotionCatalog.ChineseChoices.Split('/').Length.ToString(CultureInfo.InvariantCulture);
        }

        private static string Configuration(string key, GameMode mode)
        {
            try
            {
                PolicyItem item = PolicyCatalog.ItemOf(key);
                if (item != null)
                {
                    string value = PolicyResolver.RawGlobalValue(key);
                    if (key == "PerformancePreset") return value == "1" ? Lang.TranslateChinese("竞技") : Lang.TranslateChinese("智能");
                    if (key == BackgroundGovernancePolicy.SettingKey)
                    {
                        string level = BackgroundGovernancePolicy.Load() == BackgroundGovernancePolicy.Aggressive ? Lang.TranslateChinese("激进") : Lang.TranslateChinese("正常");
                        return PolicyResolver.RawGlobalValue("GmSuppress") == "1" ? level + Lang.TranslateChinese("；实际接管未核验") : Lang.TranslateChinese("关闭（保留") + level + Lang.TranslateChinese("偏好）");
                    }
                    if (key == "PowerPlanOn") return PowerConfiguration();
                    // 渲染线程调度的全局值只是总闸，逐游戏开关和方案见下文。
                    if (key == PolicyCatalog.KeyGameScheduling) return value == "1" ? Lang.TranslateChinese("总闸开；逐游戏见下文") : Lang.TranslateChinese("总闸关（逐游戏开关不生效）");
                    if (key == "GmBoost") return Lang.TranslateChinese("逐游戏见下文；游戏优先已包含");
                    if (key == PolicyCatalog.KeyAceGovernance) return Lang.TranslateChinese("逐游戏见下文，默认开");
                    if (key == "GameFamilyBackground" || key == "GmAutoCoresV2" || key == "GameNvRebarV1" || key == "GameDisableFullscreenOptimizationsV1") return Lang.TranslateChinese("逐游戏见下文，默认关");
                    if (item.Kind == PolicyValueKind.Bool) return OnOff(value == "1");
                    return value == "on" ? Lang.TranslateChinese("开启") : value == "off" ? Lang.TranslateChinese("关闭") : Lang.TranslateChinese("保存值未知");
                }
                switch (key)
                {
                    case "GmAwake": return Lang.TranslateChinese("自动随前台游戏执行");
                    case "GmPauseDl": return Lang.TranslateChinese("随后台治理") + OnOff(PolicyResolver.Global().SuppressBackground) + Lang.TranslateChinese("；任务活动未核验");
                    case "GmPauseMaintenanceV1": return Lang.TranslateChinese("随后台治理") + OnOff(PolicyResolver.Global().SuppressBackground);
                    case "GmGpuDemote": return Lang.TranslateChinese("随后台治理") + OnOff(PolicyResolver.Global().SuppressBackground) + Lang.TranslateChinese("；GPU执行条件未核验");
                    case "ConfigurationPresets": {
                        ConfigurationPresetKind? preset = ConfigurationPresetRuntime.CurrentFromSettings();
                        return preset.HasValue ? (preset.Value == ConfigurationPresetKind.Extreme ? Lang.TranslateChinese("极限配置") : Lang.TranslateChinese("作者同款")) + Lang.TranslateChinese("（保存配置匹配，实际生效未核验）") : Lang.TranslateChinese("自定义/未应用预设");
                    }
                    case "ModeSmart": case "ModeCompetitive": return Lang.TranslateChinese("全局模式=") + (PolicyResolver.RawGlobalValue("PerformancePreset") == "1" ? Lang.TranslateChinese("竞技") : Lang.TranslateChinese("智能"));
                    case "GameModeOn": return BoolConfiguration(key, true);
                    case "LogWritesEnabled": return BoolConfiguration(key, true);
                    case "OverviewTelemetry": return Lang.TranslateChinese("运行时只读显示");
                    case "StartupSecurityCompatibility": return Settings.Load(SecurityCompatibilityPresentation.HiddenSettingKey, false) ? Lang.TranslateChinese("不再显示") : Lang.TranslateChinese("显示提示");
                    case "SettingsAppearance": return Lang.TranslateChinese("智能动效=") + CoreMotionCatalog.Name(CoreMotionPreferences.Get(PerformancePreset.Standard)) + Lang.TranslateChinese("；电竞动效=") + CoreMotionCatalog.Name(CoreMotionPreferences.Get(PerformancePreset.Competitive));
                    case "GameForceTrigger": return Lang.TranslateChinese("逐游戏设置见下文");
                    case "ManagedPower2223": case "ManagedPowerDynamic": return PowerConfiguration();
                    case "GmMmcss": return BoolConfiguration(key, true);
                    case "NvVrrWindowed": return BoolConfiguration(key, false);
                    case "AutoLaptopPerformanceV2": return BoolConfiguration(key, false) + Lang.TranslateChinese("；接口=")
                        + (mode == null || !mode.LaptopPerformanceSupportKnown ? Lang.TranslateChinese("未核验") : mode.LaptopPerformanceSupported ? Lang.TranslateChinese("已确认支持") : Lang.TranslateChinese("未确认兼容接口"));
                    case "GameDvrOff": return BoolConfiguration(key, true);
                    case "LolColumnEnabled": case "LolColumnCleanupEnabled": case "LolColumnHeadlessEnabled": return BoolConfiguration(key, false);
                    case "LabEppYieldV1": case "LabGpuClockMaxV1": case "LabGpuPowerMaxV1": case "LabAudioLowBufferV1": return BoolConfiguration(key, false);
                    case "LabCpuIdleStrategyV1": { string selected = Settings.LoadStr(key, "default"); return selected == "threshold" ? Lang.TranslateChinese("提高空闲门槛") : selected == "disabled" ? Lang.TranslateChinese("禁止CPU空闲") : Lang.TranslateChinese("跟随原策略"); }
                    case "EnvPerformanceBundleV1": return Lang.TranslateChinese("成员偏好：") + EnvironmentIntent("EnvVbsDisabledV1") + "；" + EnvironmentIntent("EnvCpuMitigationsV1") + "；" + EnvironmentIntent("EnvNtfsLastAccessV1") + "；" + EnvironmentIntent("EnvAudioIdleV1");
                    case "EnvStoragePowerGroupV1": return Lang.TranslateChinese("成员偏好：") + EnvironmentIntent("EnvStorageIdleV1") + "；" + EnvironmentIntent("EnvStorageRuntimeV1") + "；" + EnvironmentIntent("EnvStorageD3ColdV1");
                    case "EnvQuantumProfileV1": return QuantumConfiguration();
                    case "GameLibrary": return BoolConfiguration("GmAutoAdd", false) + Lang.TranslateChinese("（自动入库）；逐游戏摘要见下文。");
                    case "GameExitGrace": return mode == null ? Lang.TranslateChinese("未取得会话对象，未读取宽限值。") : Lang.TranslateChinese("保存宽限=") + mode.ExitGraceSeconds.ToString(CultureInfo.InvariantCulture) + Lang.TranslateChinese("秒。");
                    case "AutoHideOnGame": return BoolConfiguration(key, true);
                    case "SettingsStartup": return Lang.TranslateChinese("启动任务未核验");
                    case "SoftwareUpdate": return SelfUpdateGate.IsBusy ? Lang.TranslateChinese("正在更新，退出已锁定") : Lang.TranslateChinese("未开始更新");
                    case "Hags": case "AmdSam": case "WindowedOptimizations": case "VariableRefreshRate": return Lang.TranslateChinese("未知");
                    case "GameCoreIsolation": case "GameCriticalThreadHardRuleV1": return Lang.TranslateChinese("逐游戏设置见下文");
                    case "Whitelist": return Lang.TranslateChinese("保留预置及用户规则（不导出路径）");
                    default:
                        PerformanceAction action = PerformanceActionCatalog.Find(key);
                        if (action != null) return action.Environment ? EnvironmentIntent(key) : BoolConfiguration(key, false);
                        if (key == "EnvVbsDisabledV1") return EnvironmentIntent(key);
                        return "—";
                }
            }
            catch { return Lang.TranslateChinese("未知（读取失败）"); }
        }

        private static string EnvironmentIntent(string key)
        {
            string effect;
            switch (key)
            {
                case "EnvVbsDisabledV1": effect = Lang.TranslateChinese("禁用VBS/内存完整性/Hypervisor"); break;
                case PerformanceActionCatalog.CpuMitigations: effect = Lang.TranslateChinese("关闭Spectre V2/Meltdown缓解") + (PerformanceTuning.CpuMitigationsExpanded ? Lang.TranslateChinese("及扩展软件策略（取消SSBD/AMD BTI显式启用，非全部缓解）") : Lang.TranslateChinese("（旧版基础范围，未选择扩展）")); break;
                case PerformanceActionCatalog.LastAccess: effect = Lang.TranslateChinese("停止NTFS最后访问更新"); break;
                case PerformanceActionCatalog.AudioIdle: effect = Lang.TranslateChinese("关闭交流音频空闲省电"); break;
                case PerformanceActionCatalog.StorageIdle: effect = Lang.TranslateChinese("关闭磁盘空闲省电"); break;
                case PerformanceActionCatalog.StorageRuntime: effect = Lang.TranslateChinese("关闭控制器运行时省电"); break;
                case PerformanceActionCatalog.StorageCold: effect = Lang.TranslateChinese("禁止控制器D3Cold"); break;
                default: return Lang.TranslateChinese("修改意图未知");
            }
            try
            {
                bool value;
                bool saved = Settings.TryLoadStoredBool(key, out value);
                if (!saved && key == "EnvVbsDisabledV1") return Lang.TranslateChinese("未保存") + effect + Lang.TranslateChinese("偏好（旧修改/执行状态未知）");
                if (!saved) value = false;
                return (saved ? "" : Lang.TranslateChinese("默认")) + (value ? Lang.TranslateChinese("请求") + effect : Lang.TranslateChinese("不请求") + effect + (key == "EnvVbsDisabledV1" ? Lang.TranslateChinese("（已有修改按原值恢复）") : Lang.TranslateChinese("（按原值恢复）")));
            }
            catch { return effect + Lang.TranslateChinese("偏好未知（读取失败）"); }
        }

        private static string PowerConfiguration()
        {
            if (PowerPlan.HandheldBypass) return Lang.TranslateChinese("掌机自动不切换（系统和厂商软件管理）");
            string choice = PowerPlan.EffectivePlanId;
            if (choice == "off") return Lang.TranslateChinese("不切换");
            if (choice == "managed") return Lang.TranslateChinese("由软件调度");
            return Lang.TranslateChinese("指定已有计划（名称/GUID省略）");
        }

        private static string BoolConfiguration(string key, bool fallback)
        {
            try
            {
                bool value;
                return Settings.TryLoadStoredBool(key, out value)
                    ? OnOff(value) : Lang.TranslateChinese("默认") + OnOff(fallback);
            }
            catch { return Lang.TranslateChinese("未核验（读取失败）"); }
        }

        private static string OnOff(bool value) { return value ? Lang.TranslateChinese("开") : Lang.TranslateChinese("关"); }

        private static void AppendProfiles(StringBuilder text, GameMode mode)
        {
            text.AppendLine(); text.AppendLine(Lang.TranslateChinese("【逐游戏设置（不含路径）】"));
            if (mode == null) { text.AppendLine(Lang.TranslateChinese("未取得游戏会话对象，游戏库配置未核验。")); return; }
            try
            {
                List<GameProfile> profiles = mode.GetProfiles();
                if (profiles.Count == 0) text.AppendLine(Lang.TranslateChinese("当前没有已入库游戏。"));
                int number = 0;
                foreach (GameProfile profile in profiles)
                {
                    text.AppendLine(Lang.TranslateChinese("游戏") + (++number).ToString(CultureInfo.InvariantCulture) + "：" + SafeName(profile.Name));
                    text.AppendLine(Lang.TranslateChinese("  [GameForceTrigger] 强制接管=") + OnOff(profile.ForceTrigger));
                    text.AppendLine(Lang.TranslateChinese("  [GmGameSchedulingV1] 渲染线程调度=") + GameSchedulingState(profile) + Lang.TranslateChinese("；是否实际已调度未核验。"));
                    foreach (string key in new[] { PolicyCatalog.KeyAceGovernance, "GmAutoCoresV2", "GameFamilyBackground", "GameNvRebarV1", "GameDisableFullscreenOptimizationsV1" })
                    {
                        string value;
                        bool specified = profile.Overrides.TryGetValue(key, out value);
                        if (!specified) value = PolicyResolver.RawGlobalValue(key);
                        text.AppendLine("  [" + key + "] " + (specified ? Lang.TranslateChinese("逐游戏保存=") : Lang.TranslateChinese("未逐游戏设置，默认=")) + OnOff(value == "1"));
                    }
                    string coreRaw;
                    profile.Overrides.TryGetValue(ManualCorePlan.Key, out coreRaw);
                    ManualCorePlan plan = ManualCorePlan.Parse(coreRaw);
                    text.AppendLine("  [GmManualCorePlanV2] " + (plan == null ? Lang.TranslateChinese("未保存有效手动核心计划") : Lang.TranslateChinese("逻辑处理器掩码=0x") + plan.GameMask.ToString("X", CultureInfo.InvariantCulture) + Lang.TranslateChinese("；后台核心隔离=") + OnOff(plan.Isolation) + Lang.TranslateChinese("；硬亲和性=") + OnOff(plan.HardAffinity)) + Lang.TranslateChinese("；本机拓扑适用性未核验。"));
                }
                StandbyCleanerOptions cleaner = mode.StandbyCleaningOptions;
                if (cleaner != null) text.AppendLine(Lang.TranslateChinese("[GmStandbyCleanerOptionsV1] 保存阈值：待机列表=") + cleaner.ListMegabytes + Lang.TranslateChinese("MB，可用内存=") + cleaner.FreeMegabytes + Lang.TranslateChinese("MB，轮询=") + cleaner.PollingMilliseconds + Lang.TranslateChinese("ms；是否实际已触发清理未核验。"));
            }
            catch { text.AppendLine(Lang.TranslateChinese("部分游戏配置读取失败，未核验；不能按无配置处理。")); }
        }

        // 逐游戏渲染线程调度：开关是否生效，加上要运行的方案（含清核和资源优先级）。
        internal static string GameSchedulingState(GameProfile profile)
        {
            string raw = CriticalThreadRule.Raw(profile);
            CriticalThreadRule stored = PolicyResolver.StoredSchedulingRule(profile);
            bool on = PolicyResolver.For(profile).GameSchedulingOn;
            string strategy;
            if (stored == null) strategy = Lang.TranslateChinese("已保存方案无效");
            else if (stored.IsOff) strategy = Lang.TranslateChinese("方案为关闭");
            else
            {
                string source = raw == null ? Lang.TranslateChinese("未保存方案，默认") : raw.StartsWith("7|", StringComparison.Ordinal) ? Lang.TranslateChinese("已保存") : Lang.TranslateChinese("旧规则不延用，按默认");
                strategy = source + StrategyName(stored);
                if (!stored.IsGamePriority)
                {
                    string resource;
                    if (!profile.Overrides.TryGetValue("GmBoost", out resource)) resource = PolicyResolver.RawGlobalValue("GmBoost");
                    strategy += Lang.TranslateChinese("；资源优先级=") + OnOff(resource == "1");
                }
            }
            return (on ? Lang.TranslateChinese("开") : Lang.TranslateChinese("关")) + "；" + strategy;
        }

        // 目录固定用中文方案名，不跟界面语言走。
        private static string StrategyName(CriticalThreadRule rule)
        {
            CriticalThreadRule effective = CriticalThreadRule.ParseForRuntime(rule.Encode());
            if (effective == null || effective.IsOff) return Lang.TranslateChinese("关闭");
            string name;
            switch (effective.Strategy)
            {
                case SchedulingStrategy.GamePriority: name = Lang.TranslateChinese("游戏优先"); break;
                case SchedulingStrategy.RenderBoost: name = Lang.TranslateChinese("渲染优先"); break;
                case SchedulingStrategy.RenderCore: name = Lang.TranslateChinese("渲染专核"); break;
                case SchedulingStrategy.DependencyBoost: name = Lang.TranslateChinese("依赖协同"); break;
                case SchedulingStrategy.RenderCoreTimeCritical: name = Lang.TranslateChinese("专核强化"); break;
                default: name = Lang.TranslateChinese("多核协同"); break;
            }
            return name + (effective.ClearCore ? Lang.TranslateChinese("·清核") : "");
        }

        private static string SafeName(string value)
        {
            string replaceResult = (value ?? Lang.TranslateChinese("未命名游戏")).Replace('\r', ' ').Replace('\n', ' ');
            if (Regex.IsMatch(replaceResult, @"[A-Za-z]:[\\/]|\\\\")) return Lang.TranslateChinese("名称含路径，已省略");
            return replaceResult;
        }

        private static string QuantumConfiguration()
        {
            int selected = QuantumProfiles.Selected;
            if (selected == 0) return Lang.TranslateChinese("恢复原配置");
            if (selected == 1) return Lang.TranslateChinese("游戏推荐：短可变3:1");
            if (selected == 8) return Lang.TranslateChinese("后台计算推荐：长固定1:1");
            return Lang.TranslateChinese("对照调试档序号") + selected.ToString(CultureInfo.InvariantCulture) + Lang.TranslateChinese("（不是帧率倍数）");
        }

    }
}
