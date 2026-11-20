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
            stringBuilder.AppendLine("条目非开关数；开关指独立开关；偏好非生效，—无单值。恢复仅撤回工具记录，非强制开启/出厂重置。会话退局恢复，持久项按重启提示。");
            int count = 0;
            using (PolicyResolver.BeginReadScope())
            {
                foreach (PolicyItem item in PolicyCatalog.All)
                {
                    // 性能优先级和游戏线程提优是逐游戏开关 也写进目录 当前值见下文逐游戏部分
                    if (PerformancePolicy.IsRetired(item.Key)) continue;
                    Append(stringBuilder, ref count, PolicyFeature(item), Configuration(item.Key, mode));
                }
                foreach (PerformanceAction action in PerformanceActionCatalog.All)
                    Append(stringBuilder, ref count, ActionFeature(action), Configuration(action.Key, mode));
                foreach (Feature feature in ExtraFeatures()) Append(stringBuilder, ref count, feature, Configuration(feature.Key, mode));
                foreach (Feature feature in CheckupFeatures()) Append(stringBuilder, ref count, feature, "未复查");
                AppendProfiles(stringBuilder, mode);
            }
            stringBuilder.AppendLine("功能条目：" + count.ToString(CultureInfo.InvariantCulture));
            stringBuilder.AppendLine("已下线不可开启：通用/手动LOL脱壳、文件过滤/计时修复/音效减负、Defender、全局ETW、网络旁路/统一限速、后台全冻结/工作集修剪、旧极限档、AMD Anti-Lag、桌面合成调度、游戏高性能显卡预选、游戏内存优先级、独立反作弊引擎/强度/弱核绑定、独立游戏线程提优开关、计时器恒定节拍、前台时间片档位、NTFS最后访问时间/游戏盘短文件名/NTFS缓存池、实验室音频低缓冲、CPU空闲策略的禁止空闲档（实测或公开测试无可测收益）；缓存预热、限制Windows应用后台任务、Windows可变刷新率、窗口化游戏优化、游戏低延迟已整项删除，旧记录直接丢弃；其余3.x已下线功能的旧记录按归属恢复，1.x/2.x遗留及旧反作弊、旧微软后台Job写入不再恢复。");
            return stringBuilder.ToString();
        }

        private static void Append(StringBuilder text, ref int count, Feature feature, string configuration)
        {
            text.AppendLine((++count).ToString(CultureInfo.InvariantCulture) + "." + feature.Title + "[" + feature.Key + "]：" + feature.Purpose
                + "。用：" + feature.Scene + "。操作：" + Operation(feature.Key) + "。当前：" + configuration);
        }

        private static string Operation(string key)
        {
            switch (key)
            {
                case "GmSuppress": return "在顶部策略设置→游戏场景下方选择「后台治理」三档，无独立开关";
                case "GmAwake": return "内置自动行为，无独立开关";
                case PolicyCatalog.KeyGameScheduling: return "游戏卡片深度调优逐游戏开关，「调整」窗口选模式";
                case "GmBoost": case ThreadBoostSelection.Key: return "游戏卡片深度调优逐游戏开关，默认开";
                case PolicyCatalog.KeyStandbyCleaner: return "环境页全局开关，「设置参数」调整阈值与轮询间隔";
                case "GmPauseDl": case "GmPauseMaintenanceV1": return "随「后台治理」，无独立开关";
                case PerformanceActionCatalog.CpuMitigations:
                case "EnvVbsDisabledV1":
                case PerformanceActionCatalog.AudioIdle:
                    return "随「系统增强」统一设置";
                case "ModeSmart": case "ModeCompetitive": return "模式行为说明，选运行模式";
                case "ManagedPower2223": case "ManagedPowerDynamic": return "电源行为说明，选由软件调度";
                case "PerformancePreset": case "PowerPlanOn":
                case BackgroundGovernancePolicy.SettingKey:
                case "LabCpuIdleStrategyV1":
                case "GameExitGrace": return "选择项";
                case "GameFamilyBackground": case "GameDisableFullscreenOptimizationsV1":
                case "GmAutoCoresV2": case "GameNvRebarV1": case AntiCheatSuppressionPolicy.Key: return "逐游戏开关";
                case "GameCriticalThreadHardRuleV1": return "逐游戏亲和性窗口，保存下局生效";
                case "GameForceTrigger": return "游戏卡片菜单「强制接管」";
                case "GameCoreIsolation": return "逐游戏核心计划选项";
                case "Whitelist": return "名单管理（进程/EXE/服务）";
                case "GameLibrary": return "自动入库开关+游戏管理";
                case "LolColumnEnabled": return "专属模块说明，LOL卡片自动启用，子项独立";
                case "LolColumnCleanupEnabled": return "开关+立即净化";
                case "LolColumnHeadlessEnabled": return "开关+恢复界面";
                case "LolAddonCleanup": case "ManualMemoryCleanup": case "Checkup":
                case "ShaderCleanup": case "SettingsReset":
                case "LogManagement": return "动作/诊断切换，刷新/清空/选中复制/导出";
                case "SettingsLogsUninstall": case "SettingsAiTuning":
                case "SoftwareUpdate": return "手动";
                case "OverviewTelemetry": case "AuditSlowDrivers": case "AuditPlatform": case "AuditCpu":
                case "AuditMemory": case "AuditGraphics": case "AuditPower": case "AuditInput":
                case "AuditStorageNetwork": case "AuditResults": return "只读检测";
                case "AuditGpuIrq": return "手动应用/重启后验证";
                case "AuditGpuIrqRestore": return "手动恢复";
                case "AuditMsi": case "AuditMemoryCompression": case "AuditNetworkThrottle":
                case "AuditLinkMetric": case "AuditFth": return "手动修复/恢复";
                case "AuditQuantum": return "有恢复记录时手动恢复";
                case "AuditFixAll": return "手动执行当前可修复项";
                case "SettingsAppearance": return "外观选项";
                case "EnvPerformanceBundleV1": return "环境页「系统增强」开关";
                default: return "开关";
            }
        }

        private static Feature F(string key, string title, string purpose, string scene)
        { return new Feature(key, title, purpose, scene); }

        private static Feature PolicyFeature(PolicyItem item)
        {
            string key = item.Key;
            switch (key)
            {
                case "PerformancePreset": return F(key, "运行模式", "选择智能/竞技，改变后台治理；托管参数相同", "按切屏需求和后台争用选择，不会自动打开所有功能");
                case "GmSuppress": return F(key, "后台治理", "关闭/正常/激进下拉三档，关闭为首项，无独立总开关；两档都把普通后台设为Idle优先级、I/O极低、页面优先级1，请求EcoQoS并忽略其计时器精度请求，不调显卡优先级；两档都关闭Windows动态提优，并把每个普通后台进程放进命名资源Job：优先级锁Idle、CPU硬上限（正常每进程两核且不超过全机25%，激进0.01%）、尝试磁盘限速（正常50MiB/s，激进1MiB/s），磁盘限速不可用时保留CPU限额和I/O极低优先级，无权限/其他会话/嵌套失败时退回逐进程写入，释放与崩溃恢复先清Job的CPU限额与优先级锁，再写回逐进程原值，尚未解除的磁盘限速单独记录；激进另收核；只治理与Pavise同会话的进程，系统服务（会话0）和Windows目录下的系统组件不压制，扩围只多纳入有可见窗口的应用；ACE（Program Files\\AntiCheatExpert下核实）随普通后台治理，其他反作弊只保护；WeGame主进程按普通后台，仅自身前台时不压；全部已配置/学习的游戏本体均受保护，家族开关或目录缺失不放行；核验后的游戏创建链及LeagueClient/RiotClientServices启动联网宿主豁免（开了家族治理的本局启动链除外），UI辅助仍可治理；激进档把后台收到分区后台核（大小核E核/X3D非缓存CCD/双CCD另一个且首选核所在CCD留给游戏/其余取调度评级最低的核，评级相同时0号核加末尾核，无分区时评级最低的1～2个物理核），隔离生效或调度预留核时也在原范围内避让", "后台争用；激进时对局中后台程序基本冻结；智能切出放行、竞技切出保留；提优按可核验原值恢复，不强制开启原本禁用的提优；手工进程白名单、家族与运行期保护照常有效；KOOK/YY/TeamSpeak/Discord等语音软件内置保护，QQ、微信、音乐播放器和浏览器不在名单，对局中会被压，需要时手动加白；关闭、移出治理或退局恢复");
                case BackgroundGovernancePolicy.SettingKey: return F(key, "后台治理档位", "顶部策略设置→游戏场景下方，默认正常；两档优先级与效率模式写入相同，都禁止Windows临时提升普通后台线程优先级并放进资源Job限额（正常每进程两核且不超过25%，激进CPU 0.01%），磁盘限速单独尝试，激进另收核，也作用于ACE", "激进时对局中后台程序基本冻结；智能前台时放行，竞技持续治理；不挂起或杀进程、不改服务启动方式；写入/核验/恢复失败各阶段有次数上限；不扩大到游戏、手工白名单、只保护的反作弊或游戏必要依赖");
                case PolicyCatalog.KeyGameScheduling: return F(key, "渲染线程亲和性", "逐游戏开关，默认关；只做核心放置，不改线程和进程优先级；三模式：自动（推荐：游戏运行满两分钟后两次实测找出卡帧的关键串行线程，每条只设一颗优先运行的物理核（理想处理器，不锁定、不改亲和性，其他游戏线程不受限，后台避开这些核；游戏自己改了就放手），两条合计超一核才放第二条，三条合计超两核且≥10物理核才放第三条，每放一条须剩余核心实测负载留一成余量，否则少放或不放；判定不适合留核的游戏记7天或到游戏更新）、渲染单核（渲染线程固定到最合适一颗核，可选整核避让）、手动（固定到所选1~4个逻辑CPU，可选整核避让，拓扑变化作废）；选核优先性能核、X3D大三级缓存组，避开CPU0", "CPU瓶颈、1% low偏低时对照；整核避让可能拖慢极度依赖多线程的游戏；保存下局生效，关闭立即撤回，关闭/退局/程序退出恢复本方修改并保留外部后续改动；效果因游戏而异");
                case "GameFamilyBackground": return F(key, "压制家族进程", "开启后本局只保留游戏本体：游戏目录内非本体进程、游戏子进程、Steam主程序/网页/覆盖层及本局启动链（含LeagueClient/RiotClientServices，不收核，游戏退出即放开）都按普通后台治理", "LoL未开对局真无头时的大厅、常驻启动器或Steam网页占用；可能影响覆盖层/聊天/加载；须先出现已观测渲染，开启有警告；白名单、反作弊、独立录屏与输入音频仍保护");
                case "GameDisableFullscreenOptimizationsV1": return F(key, "逐游戏禁用全屏优化", "设置所选游戏的Windows全屏优化兼容标志", "排查显示或切屏问题；可能回退，下次启动游戏生效");
                case "GmAutoCoresV2": return F(key, "逐游戏核心范围", "在选核页手动保存逻辑CPU范围，下次启动游戏生效；全选且不隔离视为不限制；隔离时后台避让并自动硬亲和性，范围外至少留两颗完整物理核；优先用Job锁定进程范围防止自行改绑，Job不可用且清理完成时回退进程硬亲和性，原因写入诊断日志；各系列CPU按Windows拓扑/CPU Sets及受支持的逐核CPUID确认性能核/能效核，不按型号、编号或SMT猜测，证据不足显示未确认", "减少跨缓存域/SMT争用；范围太窄会限并行；游戏已有自定义线程掩码或显式线程CPU Set ID、线程集合不稳定或读取不明时跳过整进程绑核且禁止亲和性回退；写入前锁占用或线程变动每进程最多试3次；权限拒绝或身份不符停止；退局先清Job限制再恢复原范围，Pavise异常退出后下次启动按回执恢复；不能保证还原游戏运行期自行改的线程分配");
                case "GameNvRebarV1": return F(key, "逐游戏NVIDIA ReBAR强开", "设置游戏驱动ReBAR偏好", "支持ReBAR的NVIDIA平台；可能掉帧，逐游戏测试，重启游戏；关闭或退局恢复");
                case AntiCheatSuppressionPolicy.Key: return F(key, "逐游戏反作弊压制", "默认关闭，按游戏开启；本游戏对局中压制装在正式位置的ACE/EasyAntiCheat/BattlEye服务：最低CPU、I/O与内存优先级、EcoQoS并忽略计时器精度请求、禁止临时提优、移到最多2个后台物理核（只有1个可用时用1个），每秒核验；不限CPU、不限速、不冻结；游戏目录自带组件与Vanguard只保护，tp/EA/GameGuard/FACEIT/NEAC只记录", "反作弊服务占用明显时可开；干预其进程不保证不被封号；退局、游戏退出或关闭即恢复");
                case "GmBoost": return F(key, "性能优先级", "逐游戏开关，默认开，没有全局开关：游戏进程High、磁盘I/O高、GPU调度类高，并锁定高性能QoS（不被节流到能效核，Win11计时器请求照常）；已是Realtime保留", "游戏与后台争用CPU/磁盘/GPU；需权限与驱动支持并读回；改动下局生效，退局按归属恢复");
                case ThreadBoostSelection.Key: return F(key, "游戏线程提优", "逐游戏开关，默认开，没有全局开关：识别画面提交线程设为TIME_CRITICAL、配合线程+1，识别额度用尽改提最忙线程，不固定核心", "渲染线程排队等CPU时；与亲和性叠加时亲和性只决定在哪些核上跑；退局恢复");
                case "PowerPlanOn": return F(key, "对局电源计划", "选择不切换、由软件调度或指定已有计划，退局返回原计划；托管时原生逐项写入与回读，优先卓越性能、回退高性能，均缺失时核验后复制原计划（可为平衡）作独立候选，不改原计划；组策略锁定项跳过并记录；隐藏模板按GUID核验，权限或系统拒绝则停止", "减轻省电约束；对局中电源计划守护监视计划存储，外部切走或改托管参数即写回，不设累计上限、不自动改成不切换，对方反复改回时放慢到最多15秒；首次应用失败每项最多3次；指定计划只切换不改参数；读取失败不视为核验成功");
                case PolicyCatalog.KeyStandbyCleaner: return F(key, "待机内存清理", "对局中仅在待机列表达到阈值且可用内存低于阈值时清理；可选默认/保守/激进/自定义", "特定内存压力；频繁清理会丢缓存并增加卡顿");
                case "AutoGlobalTimer": return F(key, "全局计时器分辨率", "对局按本机实测选档：先找略高于0.5ms的最细一档并读回、实测Sleep(1)；本机没有就实测0.5ms本身，不理想再往上找到1ms为止，避开正好0.5ms时多等一跳；系统精度不足时显示受限；故障后停止本阶段请求，有限次数恢复旧请求及自身豁免状态，耗尽保留恢复记录；全局配置重启后核验", "短等待敏感场景；Win10 2004至22H2只对自己请求精度的游戏生效，全局配置需build20348+并重启，运行故障不被环境就绪遮蔽；可能增耗");
                case "GmPauseUpdate": return F(key, "暂停Windows更新", "对局暂停可控制的Windows更新活动", "更新正在下载/安装占资源，退局恢复");
                case "NvMaxPerf": return F(key, "NVIDIA最高性能偏好", "设置游戏驱动最大性能电源偏好，不超频", "降频/唤醒波动；可能增热，部分需重启游戏");
                case "NvShaderCache": return F(key, "NVIDIA着色器缓存无上限", "调整驱动着色器缓存容量", "缓存淘汰导致重编译；占用磁盘，通常重启游戏");
                default: return F(key, item.LangKey, "当前策略项，详细用途未核验", "先核对该版本实现再建议");
            }
        }

        private static Feature ActionFeature(PerformanceAction action)
        {
            switch (action.Key)
            {
                case PerformanceActionCatalog.CpuMitigations: return F(action.Key, action.Zh, "手动开启系统增强时，在支持的Intel/AMD和Win10 2004+系统上采用扩展策略，其余设备仅处理适用的基础项。基础范围涉及Spectre V2/Meltdown，扩展范围还涉及SSBD、AMD用户到内核BTI及适用的MDS/TAA/L1TF组合", "相应缓解开销构成瓶颈时评估，开启会降低防护，关闭按归属恢复，应用和恢复需重启，旧版范围不自动扩大");
                case PerformanceActionCatalog.AudioIdle: return F(action.Key, action.Zh, "关闭已有PortCls交流空闲省电；系统增强成员", "首次出声唤醒等待；增加功耗，需重启");
                default: return F(action.Key, action.Zh, action.DetailZh, "以本机支持情况为准");
            }
        }

        private static Feature[] ExtraFeatures()
        {
            return new[] {
                F("GameModeOn", "守护总开关", "启停游戏会话自动优化；概览激活时显示本局已压制进程/已暂停服务数量，去重且只计已确认执行项；智能切出时明确显示后台压制已释放或恢复中，服务数量独立核验", "需要自动优化时开启；失败/待确认不计入，退局或停守护收起；手动持久配置按各自设置处理"),
                F("GmAwake", "对局保持唤醒", "守护开启且游戏前台时自动阻止显示器/系统休眠；切出、退局或停守护释放，不读取旧开关偏好", "防止黑屏/休眠，不是禁止CPU空闲，不阻止手动休眠"),
                F("GmPauseDl", "传递优化下载让路", "后台治理开启后每局开局确认一次传递优化后台下载，仅确认后台下载活动时暂停（限带宽并停止DoSvc），未知/空闲/前台下载不触发，开局未检测到本局不再暂停，读取失败每局最多3次；智能切出恢复，竞技切出保留，停用或退局恢复", "DO争网或磁盘；仅处理此组件，不控制Steam等下载器"),
                F("GmPauseMaintenanceV1", "对局阻止自动维护", "后台治理开启时对局中阻止新的Windows自动维护开始，退局恢复", "维护任务竞争资源；不中止已在运行的维护"),
                F("OverviewTelemetry", "概览硬件/占用监控", "显示CPU/GPU/内存/显示器型号与CPU/GPU/内存占用；窗口前台且无对局每2秒刷新，对局中或失焦每10秒；GPU读取连续失败3次后本次运行暂停读取；隐藏或离页停止读取", "查看资源状态，不测游戏FPS，不采集游戏帧率"),
                F("ModeSmart", "智能模式", "CPU≥70%持续10秒扩围，每局最多3次；<60%持续5秒后平稳120秒恢复；切出释放并保护前台", "常切屏、兼顾浏览器/语音；切屏不全清优先级/绑核"),
                F("ModeCompetitive", "竞技模式", "较广治理，切出保留，合格软件前台不豁免；保护游戏、白名单、必要依赖", "后台争用；用户会话范围回归2223，不普压Session0服务"),
                F("ManagedPower2223", "托管电源", "核验台式机按CPU匹配新版预置，CPU/平台无法安全匹配保留原计划；笔记本、检测中或未知机型沿用3.1.1.0旧策略；不靠电池判机型，UPS不当笔记本；智能/竞技共用所选策略参数；无来源选择器，旧手选无效；复制标准模板，按AC/DC及架构规则逐项设置；对局中参数漂移原地写回，结构不符或被切走才重建，不设累计上限", "按厂商与设备型号自动识别掌机（含ROG Ally和GPD WIN系列）；掌机不切换，不创建/维护托管计划，指定已有计划也不接管，保留原选择偏好，交给系统和厂商软件管理；不把S0或带电池单独视为掌机；维护避开实验室持有参数，换计划先恢复实验室，退局还原；可能增耗增热"),
                F("ManagedPowerDynamic", "托管电源动态参数", "交流AC与电池DC分别设置；旧策略采用原竞技基线，台式机新版按CPU匹配，智能/竞技共用；按CPU拓扑与供电特征调整处理器、停泊、EPP与睿频，沿用旧策略设备DC放宽规则；场景/升档/切出不改参数", "由软件调度时自动计算，无额外开关；不切换/指定已有计划优先，退局恢复；实验室覆盖按自身原值恢复"),
                F("AutoLaptopPerformanceV2", "笔记本厂商性能模式", "在顶部策略设置→对局电源下方开启；仅在已核验支持的笔记本显示，检测中及台式机等不支持设备隐藏；联想 SmartFan 核验原档，ASUS 通过运行中的 G-Helper 前五个配置档切 Turbo，由新驱动确认记录核验并在退局恢复完整原配置", "默认关；交流供电、游戏会话且电源非不切换；ASUS DSTS 仅用于接口识别，保留已有自定义 Turbo/全速；外部改档让出，增热/噪声，不等于Windows电源计划"),
                F("GameDvrOff", "关闭Game DVR与Xbox后台录制", "守护开启期间关闭Windows后台游戏录制，即时回放与录制热键不可用", "录制占编码/GPU/磁盘；关开关、停守护或退出还原；需要录屏时关闭"),
                F("EnvPerformanceBundleV1", "系统增强", "联动VBS/Hypervisor、CPU缓解、交流音频省电三项，旧版单独开启的成员保留原选择", "对应开销明显时评估，开启会降低防护，可能影响WSL2/Hyper-V及部分反作弊要求，关闭按归属恢复，应用或恢复后需重启"),
                F("EnvVbsDisabledV1", "系统增强·VBS/Hypervisor", "关闭VBS、内存完整性和相关hypervisor启动项", "虚拟化安全开销；影响WSL2/Hyper-V及部分反作弊，需重启"),
                F("LabEppYieldV1", "实验室·EPP功耗让路", "实际自主性能模式支持且GPU忙/CPU有余量时调EPP让共享功耗，验证失败回退", "插电笔记本+托管+可读功耗；台式/电池不执行，可能转CPU瓶颈；换计划/空闲策略先恢复后重采样"),
                F("LabGpuClockMaxV1", "实验室·GPU频率锁最大值", "支持绝对频率调节的 AMD 独显将频率下限提高到当前设定的上限，NVIDIA 请求锁定最高核心频率", "需独显驱动支持，实际频率受温度和功耗限制。关闭或退局时 AMD 恢复原下限，NVIDIA 回到自动调频并清除原锁频"),
                F("LabGpuPowerMaxV1", "实验室·GPU功耗墙拉满", "向支持的 AMD 或 NVIDIA 独显请求驱动允许的最高功耗上限，不改电压", "适用于 GPU 受功耗限制的负载，可能增热增耗。关闭或退局时恢复原上限，保留外部后续修改"),
                F("LabCpuIdleStrategyV1", "实验室·CPU空闲策略", "跟随当前计划基准/提高空闲门槛，在计划生效后覆盖当前计划，无需托管", "交流对局唤醒瓶颈；关闭/切策略/换计划恢复该计划原值，退局恢复"),
                F("Whitelist", "白名单与依赖保护", "从运行进程/EXE/拖放添加路径/家族保护；可手动添加外设关键词（进程名/文件描述/产品名/公司名包含即按外设保护，至少2字）；运行列表含浏览器、聊天/开发工具及最小化/托盘应用，排除系统与反作弊；内置八类：Windows系统进程、网游加速器、显卡驱动与硬件功耗散热控制、反作弊与游戏保护组件（按组标注按游戏开启后压制（ACE/EasyAntiCheat/BattlEye）/仅保护/仅记录，反作弊一律不进普通后台治理）、输入音频与外设驱动链、正在连接设备的外设驱动（对局中识别持续连接键鼠手柄厂商私有接口或手柄接口的后台程序，不看品牌，短暂连接不算）、直播与推流工具、语音通话软件（KOOK/YY/TeamSpeak/Mumble/Discord，进程名须与程序文件名一致）；可查看目录并按具体名称搜索；进程访问受限历史有记录时显示，可查看/清空；直播主程序及专用组件保护已核验子孙，父进程退出后仍保留存活成员，独立组件单独识别，开启「压制家族进程」时继续保护；来源筛选、移除、恢复平台保护、恢复预设（删除全部自定义）", "保护语音/外设/录屏/直播；过宽放过负载，无逐项限速"),
                F("GameLibrary", "游戏库/自动入库/忽略名单", "添加程序/运行目标（含最小化/托盘游戏）、平台扫描/自动收录、搜索、重命名、换/还原封面、移除/撤销忽略；卡片深度调优含核心范围、渲染线程调度、压制家族进程、ReBAR、全屏优化", "识别游戏；自动收录默认关闭，移除后防重复收录；入库仅待检测；入场须连续两窗有画面提交且GPU 3D≥3%，或≥10%且家族最高，无GPU数据不入场；强制接管除外，以前已确认渲染且文件未变的登记exe启动即入场，入场后低负载不退；已确认进程不被启动器改写；登记启动器仍切到渲染进程"),
                F("GameForceTrigger", "逐游戏强制接管", "对有明确EXE的游戏开启（需确认），该EXE进程存在即保持游戏会话", "正常识别失败时使用；误用于普通程序会错误触发游戏策略"),
                F("GameCoreIsolation", "逐游戏核心隔离/核心选择", "后台避让游戏整颗物理核；按全部/P核/E核/CCD(含缓存)/无SMT/补全SMT快捷选核；手动范围优先于游戏自设的线程分配，照常绑定并记录接管线程数；落核停止区分访问被拒、恢复未结清与读回不符，保留原始阶段和错误码，不凭权限失败断言反作弊拦截", "有备用核且拓扑/分配可靠；范围过小可能回退，不隔离中断"),
                F("GameCriticalThreadHardRuleV1", "逐游戏亲和性模式", "窗口选自动/渲染单核/手动；渲染单核与手动可选整核避让，自动只设优先核心、不用整核避让；手动在CPU格子里选1~4个逻辑CPU；只调优先级的旧方案（游戏优先、渲染优先、依赖协同）按关闭，旧专核强化按渲染单核+整核避让，旧对照和温和提优按关闭", "选核遵守游戏核心范围；整核避让后其他线程至少留两颗物理核；编辑下局生效，关闭立即恢复"),
                F("LolColumnEnabled", "英雄联盟专属卡片", "游戏库有LoL时卡片自动启用，提供借壳启动与对局真无头两个开关", "需核验LOL/客户端身份；受客户端版本影响"),
                F("LolColumnCleanupEnabled", "LoL借壳启动", "正常登录后LCU确认大厅可用，精确结束WeGame、Cross及腾讯附加进程；不注入、不改内存、不碰游戏文件；立即净化可手动执行（需LCU就绪）", "平台附属持续占用；WeGame社交/助手暂不可用"),
                F("LolColumnHeadlessEnabled", "LoL对局真无头", "进入对局后用LCU原生接口关闭大厅界面，对局结束由守护链拉起；大厅反复重生达上限本局熔断恢复；恢复界面可手动拉起", "大厅占CPU/GPU；需可靠客户端接口"),
                F("LolAddonCleanup", "LOL插件检查/删除", "游戏卡片后台检查并提示可清理附加层，点击进入专属优化，用户确认后删除", "排查AI教练、录制等附加层；须先完全退出LoL与WeGame，删除不可恢复，客户端更新或修复会重新下载"),
                F("ManualMemoryCleanup", "手动极限清理", "概览内存卡手动执行，需管理员；游戏中仅回收Pavise自身内存；非游戏时合并页面、工作集、文件缓存、注册表缓存、修改页、待机列表", "手动释放内存；清空缓存可能增加后续重载/卡顿"),
                F("Checkup", "体检", "约120秒只读采样，可取消/重新体检；按结果提供下列修复入口", "系统排障；不测FPS、1% low、帧时间或输入延迟"),
                F("SettingsStartup", "开机启动", "管理Pavise管理员登录启动任务，后台启动进托盘", "需要自动守护"),
                F("AutoHideOnGame", "入局自动隐藏", "检测到游戏10秒后收回托盘，每局一次，保留后台守护", "减少界面干扰/可见刷新"),
                F("GameExitGrace", "退出宽限", "游戏证据暂失后等0/2/5/8/15/30秒（默认8）再恢复会话策略，后台压制先解除不受等待影响", "避免短暂重启导致反复切换；0秒可能把进程交接误判退局；会延迟退局恢复"),
                F("ShaderCleanup", "着色器缓存清理", "手动删受支持的着色器缓存", "排查旧/坏缓存；重编译可能卡顿，不宜每局清"),
                F("SettingsReset", "重置设置", "清空设置、游戏库、白名单、忽略记录和日志后重开，保护原值恢复记录；对局中拒绝", "错误配置排障；先恢复已拥有的系统修改，会丢相应偏好"),
                F("SettingsAppearance", "视觉定制/窗口/托盘", "外观页顶部调整界面缩放（跟随Windows或100%–300%）；语言/明暗/背景图/背景浓度/模式配色；" + MotionCount() + "款动效（" + CoreMotionCatalog.ChineseChoices + "）用静态缩略图选择，两模式可同选，选中即应用/恢复默认；概览动效开合/旋转/蓄能脉冲，配色随主题，失焦/隐藏/离页/对局停播；拖动/最小化/托盘开关", "界面可读性，不增帧"),
                F("LogWritesEnabled", "写入日志", "启停Pavise运行日志写入", "减少日志写入或保留排障证据；关闭后记录不完整"),
                F("LogManagement", "运行日志", "设置→维护→日志；默认动作日志：当前或最近会话的成功压制进程名单、已生效功能、已核验游戏与渲染线程CPU范围及避让、清核核验状态、恢复与待确认/失败；切换诊断日志查看构建身份与失败阶段；可选保存位置和文件名，动作导出从完整日志提取，诊断导出完整原文件，不限屏幕尾部；关闭写入仍可导出，不改原日志；清空同一日志源", "不重新采样或执行调度；记录不足不代表成功；清空不还原系统"),
                F("SettingsLogsUninstall", "版本/说明/支持/卸载", "版本说明/官网/源码/捐赠；卸载先恢复", "版本核验；说明仅展示一次"),
                F("SoftwareUpdate", "软件内更新/失败手动下载", "在设置→关于中检查更新；验签下载EXE，显示下载/校验/安装进度；检查和下载可取消，取消后可重新检查，不将旧版误报为新版；更新期间禁止退出、替换不可取消；安全退出后原位替换，新版启动确认后删除旧版并同步启动入口；失败同一弹窗转手动更新、解除关闭限制，提供夸克官网入口/GitHub", "退局/维护结束后联网管理员操作；保留个人数据/恢复资产；替换失败先回滚，不强杀活跃进程；备用下载按页说明更新"),
                F("SettingsAiTuning", "AI调优提示词", "只读汇总硬件/全部功能/设置，补充目标、预览/复制", "供自选AI建议，不联网/应用")
            };
        }

        private static Feature[] CheckupFeatures()
        {
            return new[] {
                F("AuditGpuIrq", "体检·显卡中断放置/验证", "采样显卡ISR/DPC落点与P99，有证据时将中断移出游戏保留核，重启后验证；退化/驱动重置则尝试回滚", "至少300个有效事件、单处理器组、可靠保留区/区外核心；游戏中禁止应用，未知/外部配置等情况跳过"),
                F("AuditGpuIrqRestore", "体检·恢复显卡中断配置", "还原本工具拥有的显卡IRQ放置；1.x/2.x留下的中断迁移与中断优先级不再恢复", "撤销本工具中断调整或处理残留；依记录恢复，按提示重启"),
                F("AuditMsi", "体检·显卡MSI中断修复", "将当前显卡已有MSISupported=0修复为1，可恢复", "显卡MSI被明确关闭；不为所有设备强造设置，需重启；与中断放置不同"),
                F("AuditSlowDrivers", "体检·驱动延迟诊断", "展示较慢驱动的最大耗时、P99与设备线索", "排查DPC/ISR延迟；不提供自动修复所有驱动"),
                F("AuditMemoryCompression", "体检·内存压缩修复", "读取内存压缩/页面合并，压缩关闭时可启用，并按记录恢复", "排查内存压力；本项不是关闭内存压缩"),
                F("AuditNetworkThrottle", "体检·网络限流值修复", "异常NetworkThrottlingIndex修复到10；1–70和0xFFFFFFFF均有效，后者关闭网络节流，保留不改", "不因非默认而修复；旧版修复记录仍可恢复；联网异常应撤回"),
                F("AuditLinkMetric", "体检·有线优先修复", "有可靠有线网关却默认走Wi-Fi时调整有线metric；撤销回自动metric", "路由选择异常且无歧义；不承诺保留任意原手动metric"),
                F("AuditFth", "体检·容错堆FTH修复", "对命中程序登记FTH排除、移除已挂载兼容补丁引用并保留文件，可恢复", "FTH补丁确实介入；可能影响容错，不是全局关闭所有兼容机制"),
                F("AuditQuantum", "体检·前台时间片检查", "显示当前时间片线索；有Pavise留下的时间片记录时可一键恢复", "前台时间片档位已下线；其他工具改的值只提示，不代改"),
                F("AuditFixAll", "体检·一键修复", "依次执行当前符合条件的体检修复，保留MSI/FTH等确认流程", "确诊后手动执行，非开启全部优化"),
                F("AuditPlatform", "体检·平台能力检测", "检查显卡清单、NVIDIA接口、CPU Sets分区、EcoQoS、Windows版本、3D V-Cache驱动与Windows游戏模式", "能力不等于可由Pavise开关"),
                F("AuditCpu", "体检·CPU/后台负载检测", "检查CPU拓扑、整机占用、近7天降频事件、后台占用前三和灯效常驻软件", "定位调度/功耗/后台争用线索"),
                F("AuditMemory", "体检·内存检测", "检查内存容量/使用率/余量、页面文件、频率、条数/通道布局及压缩/页面合并", "发现内存压力或配置线索；不提供XMP/内存超频"),
                F("AuditGraphics", "体检·显示/图形检测", "检查主屏/多屏刷新率、NVIDIA降频标记、ReBAR、HAGS、MPO、Game DVR", "发现呈现与驱动配置线索；不是刷新率/MPO调整开关"),
                F("AuditPower", "体检·电源/系统检测", "检查当前计划、节能模式、供电、核心停泊、无输入降级及Ryzen分支预测优化的系统版本线索", "定位电源策略/系统版本问题，不直接修改BIOS"),
                F("AuditInput", "体检·输入链检测", "检查键鼠连接类型/蓝牙、筛选键、辅助功能热键拦截和第三方ThreadDpcEnable改动", "排查输入体验线索，不测真实端到端输入延迟"),
                F("AuditStorageNetwork", "体检·存储/网络检测", "检查DirectStorage/BypassIO直通/阻塞原因、链路、默认路由、限流值", "定位存储过滤/网络配置，无网络内核旁路"),
                F("AuditResults", "体检·结论/证据/恢复记录", "汇总问题线索、证据等级、操作入口、显卡中断恢复记录", "区分建议/检测/修复，不是增帧测试")
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
                    if (key == "PerformancePreset") return value == "1" ? "竞技" : "智能";
                    if (key == BackgroundGovernancePolicy.SettingKey)
                    {
                        string level = BackgroundGovernancePolicy.Load() == BackgroundGovernancePolicy.Aggressive ? "激进" : "正常";
                        return PolicyResolver.RawGlobalValue("GmSuppress") == "1" ? level + "；实际接管未核验" : "关闭（保留" + level + "偏好）";
                    }
                    if (key == "PowerPlanOn") return PowerConfiguration();
                    // 渲染线程调度的全局值只是总闸 逐游戏开关和方案见下文
                    if (key == PolicyCatalog.KeyGameScheduling) return value == "1" ? "总闸开；逐游戏见下文" : "总闸关（逐游戏开关不生效）";
                    if (key == "GmBoost" || key == ThreadBoostSelection.Key) return "逐游戏见下文，默认开";
                    if (key == "GameFamilyBackground" || key == "GmAutoCoresV2" || key == "GameNvRebarV1" || key == "GameDisableFullscreenOptimizationsV1" || key == AntiCheatSuppressionPolicy.Key) return "逐游戏见下文，默认关";
                    if (item.Kind == PolicyValueKind.Bool) return OnOff(value == "1");
                    return value == "on" ? "开启" : value == "off" ? "关闭" : "保存值未知";
                }
                switch (key)
                {
                    case "GmAwake": return "自动随前台游戏执行";
                    case "GmPauseDl": return "随后台治理" + OnOff(PolicyResolver.Global().SuppressBackground) + "；任务活动未核验";
                    case "GmPauseMaintenanceV1": return "随后台治理" + OnOff(PolicyResolver.Global().SuppressBackground);
                    case "ModeSmart": case "ModeCompetitive": return "全局模式=" + (PolicyResolver.RawGlobalValue("PerformancePreset") == "1" ? "竞技" : "智能");
                    case "GameModeOn": return BoolConfiguration(key, true);
                    case "LogWritesEnabled": return BoolConfiguration(key, true);
                    case "OverviewTelemetry": return "运行时只读显示";
                    case "SettingsAppearance": return "智能动效=" + CoreMotionCatalog.Name(CoreMotionPreferences.Get(PerformancePreset.Standard)) + "；电竞动效=" + CoreMotionCatalog.Name(CoreMotionPreferences.Get(PerformancePreset.Competitive));
                    case "GameForceTrigger": return "逐游戏设置见下文";
                    case "ManagedPower2223": case "ManagedPowerDynamic": return PowerConfiguration();
                    case "AutoLaptopPerformanceV2": return BoolConfiguration(key, false) + "；接口="
                        + (mode == null || !mode.LaptopPerformance.SupportKnown ? "未核验" : mode.LaptopPerformance.Supported ? "已确认支持" : "未确认兼容接口");
                    case "GameDvrOff": return BoolConfiguration(key, true);
                    case "LolColumnEnabled": case "LolColumnCleanupEnabled": case "LolColumnHeadlessEnabled": return BoolConfiguration(key, false);
                    case "LabEppYieldV1": case "LabGpuClockMaxV1": case "LabGpuPowerMaxV1": return BoolConfiguration(key, false);
                    case "LabCpuIdleStrategyV1": { string selected = LaboratorySelection.CanonicalIdle(Settings.LoadStr(key, "default")); return selected == "threshold" ? "提高空闲门槛" : "跟随原策略"; }
                    case "EnvPerformanceBundleV1": return "成员偏好：" + EnvironmentIntent("EnvVbsDisabledV1") + "；" + EnvironmentIntent("EnvCpuMitigationsV1") + "；" + EnvironmentIntent("EnvAudioIdleV1");
                    case "GameLibrary": return BoolConfiguration("GmAutoAdd", false) + "（自动入库）；逐游戏摘要见下文。";
                    case "GameExitGrace": return mode == null ? "未取得会话对象，未读取宽限值。" : "保存宽限=" + mode.ExitGraceSeconds.ToString(CultureInfo.InvariantCulture) + "秒。";
                    case "AutoHideOnGame": return BoolConfiguration(key, true);
                    case "SettingsStartup": return "启动任务未核验";
                    case "SoftwareUpdate": return SelfUpdateGate.IsBusy ? "正在更新，退出已锁定" : "未开始更新";
                    case "GameCoreIsolation": case "GameCriticalThreadHardRuleV1": return "逐游戏设置见下文";
                    case "Whitelist": return "保留预置及用户规则（不导出路径）";
                    default:
                        PerformanceAction action = PerformanceActionCatalog.Find(key);
                        if (action != null) return action.Environment ? EnvironmentIntent(key) : BoolConfiguration(key, false);
                        if (key == "EnvVbsDisabledV1") return EnvironmentIntent(key);
                        return "—";
                }
            }
            catch { return "未知（读取失败）"; }
        }

        private static string EnvironmentIntent(string key)
        {
            string effect;
            switch (key)
            {
                case "EnvVbsDisabledV1": effect = "禁用VBS/内存完整性/Hypervisor"; break;
                case PerformanceActionCatalog.CpuMitigations: effect = "关闭Spectre V2/Meltdown缓解" + (PerformanceTuning.CpuMitigationsExpanded ? "及扩展软件策略（取消SSBD/AMD BTI显式启用，非全部缓解）" : "（基础范围）"); break;
                case PerformanceActionCatalog.AudioIdle: effect = "关闭交流音频空闲省电"; break;
                default: return "修改意图未知";
            }
            try
            {
                bool value;
                bool saved = Settings.TryLoadStoredBool(key, out value);
                if (!saved && key == "EnvVbsDisabledV1") return "未保存" + effect + "偏好（旧修改/执行状态未知）";
                if (!saved) value = false;
                return (saved ? "" : "默认") + (value ? "请求" + effect : "不请求" + effect + (key == "EnvVbsDisabledV1" ? "（已有修改按原值恢复）" : "（按原值恢复）"));
            }
            catch { return effect + "偏好未知（读取失败）"; }
        }

        private static string PowerConfiguration()
        {
            if (PowerPlan.HandheldBypass) return "掌机自动不切换（系统和厂商软件管理）";
            string choice = PowerPlan.EffectivePlanId;
            if (choice == "off") return "不切换";
            if (choice == "managed") return "由软件调度";
            return "指定已有计划（名称/GUID省略）";
        }

        private static string BoolConfiguration(string key, bool fallback)
        {
            try
            {
                bool value;
                return Settings.TryLoadStoredBool(key, out value)
                    ? OnOff(value) : "默认" + OnOff(fallback);
            }
            catch { return "未核验（读取失败）"; }
        }

        private static string OnOff(bool value) { return value ? "开" : "关"; }

        private static void AppendProfiles(StringBuilder text, GameMode mode)
        {
            text.AppendLine(); text.AppendLine("【逐游戏设置（不含路径）】");
            if (mode == null) { text.AppendLine("未取得游戏会话对象，游戏库配置未核验。"); return; }
            try
            {
                List<GameProfile> profiles = mode.GetProfiles();
                if (profiles.Count == 0) text.AppendLine("当前没有已入库游戏。");
                int number = 0;
                foreach (GameProfile profile in profiles)
                {
                    text.AppendLine("游戏" + (++number).ToString(CultureInfo.InvariantCulture) + "：" + SafeName(profile.Name));
                    text.AppendLine("  [GameForceTrigger] 强制接管=" + OnOff(profile.ForceTrigger));
                    text.AppendLine("  [GmGameSchedulingV1] 渲染线程亲和性=" + GameSchedulingState(profile) + "；是否实际已放置未核验。");
                    PolicySnapshot game = PolicyResolver.For(profile);
                    text.AppendLine("  [GmBoost] 性能优先级=" + OnOff(game.BoostGame) + (profile.Overrides.ContainsKey("GmBoost") ? "（逐游戏保存）" : "（默认）"));
                    text.AppendLine("  [GmThreadBoostStrategyV1] 游戏线程提优=" + OnOff(game.ThreadBoostOn) + (profile.Overrides.ContainsKey(ThreadBoostSelection.Key) ? "（逐游戏保存）" : "（默认）"));
                    foreach (string key in new[] { "GmAutoCoresV2", "GameFamilyBackground", "GameNvRebarV1", "GameDisableFullscreenOptimizationsV1", AntiCheatSuppressionPolicy.Key })
                    {
                        string value;
                        bool specified = profile.Overrides.TryGetValue(key, out value);
                        if (!specified) value = PolicyResolver.RawGlobalValue(key);
                        text.AppendLine("  [" + key + "] " + (specified ? "逐游戏保存=" : "未逐游戏设置，默认=") + OnOff(value == "1"));
                    }
                    string coreRaw;
                    profile.Overrides.TryGetValue(ManualCorePlan.Key, out coreRaw);
                    ManualCorePlan plan = ManualCorePlan.Parse(coreRaw);
                    text.AppendLine("  [GmManualCorePlanV2] " + (plan == null ? "未保存有效手动核心计划" : "逻辑处理器掩码=0x" + plan.GameMask.ToString("X", CultureInfo.InvariantCulture) + "；后台核心隔离=" + OnOff(plan.Isolation) + "；硬亲和性=" + OnOff(plan.HardAffinity)) + "；本机拓扑适用性未核验。");
                }
                StandbyCleanerOptions cleaner = mode.StandbyCleaningOptions;
                if (cleaner != null) text.AppendLine("[GmStandbyCleanerOptionsV1] 保存阈值：待机列表=" + cleaner.ListMegabytes + "MB，可用内存=" + cleaner.FreeMegabytes + "MB，轮询=" + cleaner.PollingMilliseconds + "ms；是否实际已触发清理未核验。");
            }
            catch { text.AppendLine("部分游戏配置读取失败，未核验；不能按无配置处理。"); }
        }

        // 逐游戏渲染线程亲和性 开关是否生效 加上要运行的模式（含整核避让）
        internal static string GameSchedulingState(GameProfile profile)
        {
            string raw = CriticalThreadRule.Raw(profile);
            CriticalThreadRule stored = PolicyResolver.StoredSchedulingRule(profile);
            bool on = PolicyResolver.For(profile).GameSchedulingOn;
            string strategy;
            if (stored == null) strategy = "已保存模式无效";
            else if (stored.IsOff) strategy = "模式为关闭";
            else
            {
                string source = raw == null ? "未保存模式，默认" : raw.StartsWith("7|", StringComparison.Ordinal) ? "已保存" : "旧规则不延用，按默认";
                strategy = source + StrategyName(stored);
            }
            return (on ? "开" : "关") + "；" + strategy;
        }

        // 目录固定用中文模式名 不跟界面语言走 旧方案先换算成运行模式
        private static string StrategyName(CriticalThreadRule rule)
        {
            CriticalThreadRule effective = CriticalThreadRule.ParseForRuntime(rule.Encode());
            if (effective == null || effective.IsOff) return "关闭";
            if (effective.IsManualPin) return "手动·CPU" + CpuTopology.DescribeRanges(effective.ManualMask) + (effective.ClearCore ? "·整核避让" : "");
            if (effective.IsOrchestration) return "自动";
            return "渲染单核" + (effective.ClearCore ? "·整核避让" : "");
        }

        private static string SafeName(string value)
        {
            string replaceResult = (value ?? "未命名游戏").Replace('\r', ' ').Replace('\n', ' ');
            if (Regex.IsMatch(replaceResult, @"[A-Za-z]:[\\/]|\\\\")) return "名称含路径，已省略";
            return replaceResult;
        }

    }
}
