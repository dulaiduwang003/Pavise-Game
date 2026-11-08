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
            var text = new StringBuilder(12000);
            text.AppendLine("条目非开关数；开关指独立开关；偏好非生效，—无单值。恢复仅撤回工具记录，非强制开启/出厂重置；HAGS/SAM/窗口化优化/系统VRR关闭则请求关闭，原值另走恢复维护。会话退局恢复，持久项按重启提示。");
            int count = 0;
            using (PolicyResolver.BeginReadScope())
            {
                foreach (PolicyItem item in PolicyCatalog.All)
                {
                    if (PerformancePolicy.IsRetired(item.Key)) continue;
                    Append(text, ref count, PolicyFeature(item), Configuration(item.Key, mode));
                }
                foreach (PerformanceAction action in PerformanceActionCatalog.All)
                    Append(text, ref count, ActionFeature(action), Configuration(action.Key, mode));
                foreach (PrivacyAction action in PrivacyActionCatalog.All)
                {
                    Feature feature = action.Key == PrivacyActionCatalog.BackgroundGlobal
                        ? F(action.Key, action.Zh, "限制UWP后台活动，保留逐应用例外，不管普通桌面软件", "UWP后台活跃；可能延迟通知/同步，重启相关应用；家庭版必要时注销，不要求统一重启电脑")
                        : F(action.Key, action.Zh, "扩大NTFS文件系统缓存池", "内存充足、文件元数据负载高；增加内存占用，需重启");
                    Append(text, ref count, feature, BoolConfiguration(PrivacyControl.PreferencePrefix + action.Key, false));
                }
                foreach (Feature feature in ExtraFeatures()) Append(text, ref count, feature, Configuration(feature.Key, mode));
                foreach (Feature feature in CheckupFeatures()) Append(text, ref count, feature, "未复查");
                AppendProfiles(text, mode);
            }
            text.AppendLine("功能条目：" + count.ToString(CultureInfo.InvariantCulture));
            text.AppendLine("不可开启：通用/手动LOL脱壳、文件过滤/计时修复/音效减负、Defender、全局ETW、网络旁路/统一限速、后台全冻结/工作集修剪、旧极限档、AMD Anti-Lag；仅恢复旧记录。");
            return text.ToString();
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
                case "GmPauseDl": case "GmGpuDemote": return "随「后台治理」，无独立开关";
                case PerformanceActionCatalog.CpuMitigations: return "独立开关；系统增强也包含此项；重新开启独立开关或在一键配置选择极限配置可选扩展，旧版范围不自动扩大";
                case "EnvVbsDisabledV1":
                case PerformanceActionCatalog.LastAccess: case PerformanceActionCatalog.AudioIdle:
                    return "随「系统性能增强」组合，无独立开关";
                case PerformanceActionCatalog.StorageIdle: case PerformanceActionCatalog.StorageRuntime: case PerformanceActionCatalog.StorageCold:
                    return "随「存储设备电源管理」组合，无独立开关";
                case "ModeSmart": case "ModeCompetitive": return "模式行为说明，选运行模式";
                case "ManagedPower2223": case "ManagedPowerDynamic": case "ManagedPowerHandheld": return "电源行为说明，选由软件调度";
                case "AntiCheatWeakCoreAffinity": return "专项内置，无独立开关";
                case "PerformancePreset": case "PowerPlanOn": case "AcModeV1":
                case BackgroundGovernancePolicy.SettingKey:
                case "LabCpuIdleStrategyV1": case PerformanceActionCatalog.Quantum:
                case "GameExitGrace": return "选择项";
                case "GameFamilyBackground": case "GameDisableFullscreenOptimizationsV1":
                case "GmAutoCoresV2": case "GameNvRebarV1": case "GameCriticalThreadHardRuleV1": case "GameForceTrigger": return "逐游戏开关";
                case "GameCoreIsolation": return "逐游戏核心计划选项";
                case "AntiCheatGroups": return "各可处理分组独立开启/关闭，无总开关";
                case "AntiCheatCompatibility": return "查看/整表清空兼容名单";
                case "Whitelist": return "名单管理";
                case "GameLibrary": return "自动入库开关+游戏管理";
                case "LolColumnEnabled": return "专属模块说明，LOL卡片启用，子项独立";
                case "LolColumnCleanupEnabled": return "开关+手动清理/恢复";
                case "LolAddonCleanup": case "ManualMemoryCleanup": case "Checkup":
                case "ShaderCleanup": case "SettingsReset":
                case "LogManagement": return "动作/诊断切换，刷新/清空/选中复制/导出";
                case "SettingsLogsUninstall": case "SettingsAiTuning":
                case "SoftwareUpdate": return "手动";
                case "ConfigurationPresets": return "一键配置内点击配置即应用";
                case "StartupSecurityCompatibility": return "查看/不再显示";
                case "OverviewTelemetry": case "AuditSlowDrivers": case "AuditPlatform": case "AuditCpu":
                case "AuditMemory": case "AuditGraphics": case "AuditPower": case "AuditInput":
                case "AuditStorageNetwork": case "AuditResults": return "只读检测";
                case "AuditGpuIrq": return "手动应用/重启后验证";
                case "AuditGpuIrqRestore": return "手动恢复";
                case "AuditMsi": case "AuditMemoryCompression": case "AuditNetworkThrottle":
                case "AuditLinkMetric": case "AuditFth": return "手动修复/恢复";
                case "AuditQuantum": return "前往时间片设置";
                case "AuditFixAll": return "手动执行当前可修复项";
                case "SettingsAppearance": return "外观选项";
                case "EnvPerformanceBundleV1": case "EnvStoragePowerGroupV1": return "组合开关";
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
                case "GmSuppress": return F(key, "后台治理", "关闭/正常/激进下拉三档，关闭为首项，无独立总开关；两档均降低合格后台CPU、I/O、页面及GPU优先级，按支持请求EcoQoS；激进另关闭Windows动态提优；并按活动暂停后台传递优化下载；全部已配置/学习的游戏本体均受保护，家族开关或目录缺失不放行，兼容旧名称记录和路径格式；核验后的游戏创建链及LeagueClient/RiotClientServices启动联网宿主豁免，UI辅助仍可治理", "后台争用；智能切出放行、竞技切出保留；提优按可核验原值恢复，不强制开启原本禁用的提优；GPU需系统/驱动支持；智能保护前台；两场景保护游戏、依赖及白名单，关闭、移出治理或退局恢复本次限制");
                case BackgroundGovernancePolicy.SettingKey: return F(key, "后台治理档位", "入口在顶部策略设置→游戏场景下方；默认正常；正常降低普通后台进程的CPU、I/O、内存及GPU优先级并启用效率模式；激进在此基础上禁止Windows临时提升普通后台线程优先级；反作弊压制有独立规则，不受此档位影响", "激进可能让后台软件响应稍慢；智能前台时放行，竞技持续治理；关闭或退局恢复原优先级与提升设置，全部可逆；不使用Job或冻结，不扩大到游戏、白名单、反作弊或游戏必要依赖");
                case "GmThreadBoostStrategyV1": return F(key, "游戏线程提优", "优先提优可识别的DirectX画面提交线程，否则选最忙线程；渲染采用TimeCritical，健康租约不定时撤回重采样；证据不足计入连续5次未成功采样上限，提优与仅观测共用预算，耗尽后按原规则判断CPU占用候选备用提优，备用也有限次", "关键线程被抢占；最忙不一定是渲染，普通High进程不因此进入实时类；实际准备或持有单核硬绑定的同一线程先恢复自有提优并暂停重复提优，其他线程保持；旧硬绑定恢复未结清时暂缓该进程线程提优；游戏改回优先级后本进程停止争抢；同值保存、worker重建及同局返回已耗尽配置不补次；成功提优每5秒轻量核验，仅观测成功后仍每30秒刷新角色");
                case "GmEnglishInputV1": return F(key, "游戏窗口切换英文输入", "入局为游戏窗口尝试一次英文输入布局，退局尝试还原", "避免输入法误触/组合输入干扰");
                case "GameFamilyBackground": return F(key, "逐游戏家族后台治理", "识别真实游戏渲染进程后处理非必要启动器/附属负载，不压制游戏本体", "平台后台持续占用；必要依赖仍受保护");
                case "GameDisableFullscreenOptimizationsV1": return F(key, "逐游戏禁用全屏优化", "设置所选游戏的Windows全屏优化兼容标志", "排查显示或切屏问题；可能回退，重启游戏");
                case "GmAutoCoresV2": return F(key, "逐游戏核心范围调度", "按保存的逻辑CPU范围运行，优先用Job锁定进程范围防止自行改绑，后台隔离挪核同样优先Job，保留系统核心保留机制；切回立即核验，已绑定后的短暂读写故障有限重试；各系列CPU按Windows拓扑/CPU Sets及受支持的逐核CPUID确认性能核/能效核，不按型号、编号或SMT猜测，证据不足显示未确认", "减少跨缓存域/SMT争用；范围太窄会限并行，需可信拓扑；关闭SMT仍可识别混合核心，类型识别不改变已保存范围；支持已有Job的兼容嵌套；检测到游戏已有自定义线程掩码或显式线程CPU Set ID、线程集合不稳定或读取不明时跳过整进程绑核且禁止进程亲和性回退；其他Job不可用且清理完成时仅回退进程硬亲和性，原因写入诊断日志；权限拒绝或身份不符仍停止；退局/异常先清Job限制再恢复原范围（进程范围），独立守护兜底；初始准入不能保证完整还原运行期间游戏自行改变的线程分配");
                case "GameNvRebarV1": return F(key, "逐游戏NVIDIA ReBAR", "设置游戏驱动ReBAR偏好", "支持ReBAR的NVIDIA平台；逐游戏测试，重启游戏");
                case "GmBoost": return F(key, "性能优先级", "一个开关统一游戏CPU High、内存页面优先级5、磁盘I/O高和WDDM GPU调度；分别回读和恢复，单项失败不连带其他项", "对局CPU/磁盘/GPU竞争；CPU新记录按写入阶段和当前匹配值归还，外部不同值保留，旧记录按历史High匹配恢复；内存优先级不增加容量、不锁页，已为5无新增收益；需相应权限及驱动支持，退局恢复");
                case "PowerPlanOn": return F(key, "对局电源计划", "选择不切换、由软件调度或指定已有计划，退局返回原计划；托管时原生逐项写入与回读，优先卓越性能、回退高性能，均缺失时核验后复制原计划（可为平衡）作独立候选，不改原计划；隐藏模板按GUID核验，权限或系统拒绝则停止；未注册项跳过，读取失败不视为核验成功", "减轻省电约束；指定计划只切换、每15秒核对活动GUID，不改参数；临时失败最多尝试3次，恢复核验后才再应用，耗尽本局停止，不可读候选保留恢复记录；设备分流见下文");
                case "GmCacheWarmV2": return F(key, "缓存预热", "空闲时预读游戏文件进系统缓存，入局取消", "冷加载/切图；热缓存收益小，需空闲内存");
                case "GmStandbyCleanerV2": return F(key, "待机列表清理", "按列表大小、可用内存和轮询阈值清理；可选默认/保守/激进/自定义", "特定内存压力；频繁清理会丢缓存并增加卡顿");
                case "GmDwmBoost": return F(key, "桌面合成调度", "调整桌面合成相关多媒体调度，不关闭DWM", "桌面合成竞争；效果取决于呈现路径");
                case "AutoTimerTick": return F(key, "计时器节拍", "useplatformclock=no、useplatformtick=no、disabledynamictick=yes；故障后停止本阶段应用并按有限预算恢复", "计时敏感场景对照试验；可能增耗，持久配置需重启");
                case "AutoGlobalTimer": return F(key, "全局计时器分辨率", "配置全局请求行为，对局按条件请求0.5ms并读回；系统精度不足时显示受限；故障后停止本阶段请求，有限次数恢复旧请求及自身豁免状态，耗尽保留恢复记录；重启后核验环境", "短等待敏感场景；新开启需build20348+，全局配置需重启，运行故障不被环境就绪遮蔽；可能增耗");
                case "GmPauseUpdate": return F(key, "暂停Windows更新", "对局暂停可控制的Windows更新活动", "更新正在下载/安装占资源，退局恢复");
                case "GmIntelEnduranceV1": return F(key, "关闭Intel图形续航限制", "解除支持设备的Endurance Gaming限帧", "Intel驱动确有续航限制；可能增耗");
                case "NvLowLat": return F(key, "NVIDIA驱动低延迟", "开启/关闭适用的驱动低延迟偏好", "受支持API的预渲染排队；不等于通用Reflex，部分需重启游戏");
                case "GmPauseMaintenanceV1": return F(key, "暂停自动维护", "对局暂停受支持的Windows自动维护", "维护任务正在竞争资源，退局恢复");
                case "NvMaxPerf": return F(key, "NVIDIA最高性能偏好", "设置游戏驱动最大性能电源偏好，不超频", "降频/唤醒波动；可能增热，部分需重启游戏");
                case "NvShaderCache": return F(key, "NVIDIA着色器缓存容量", "调整驱动着色器缓存容量", "缓存淘汰导致重编译；占用磁盘，通常重启游戏");
                default: return F(key, item.LangKey, "当前策略项，详细用途未核验", "先核对该版本实现再建议");
            }
        }

        private static Feature ActionFeature(PerformanceAction action)
        {
            switch (action.Key)
            {
                case PerformanceActionCatalog.Mitigations: return F(action.Key, action.Zh, "关闭库中游戏CFG、EAF/EAF+、IAF、CallerCheck；32位另含SimExec", "对应防护确有开销；降低游戏防护，重启游戏");
                case PerformanceActionCatalog.ShortNames: return F(action.Key, action.Zh, "停止NTFS游戏卷新增8.3短名称，保留已有名称", "大量文件创建；老软件可能依赖短名称");
                case PerformanceActionCatalog.OpenGl: return F(action.Key, action.Zh, "设置NVIDIA OpenGL驱动多线程", "NVIDIA OpenGL游戏；可能增加争用，重启游戏");
                case PerformanceActionCatalog.CpuMitigations: return F(action.Key, action.Zh, "Intel/AMD、Win10 2004+可选扩展关闭策略：Spectre V2/Meltdown、取消SSBD与AMD用户到内核BTI显式启用，适用时涉及MDS/TAA/L1TF组合；保留其他位，关闭恢复原值，不能关闭全部缓解", "相应缓解开销构成瓶颈；降低防护，应用和恢复需重启；旧版范围不自动扩大");
                case PerformanceActionCatalog.LastAccess: return F(action.Key, action.Zh, "停止NTFS最后访问时间维护；系统增强成员", "文件元数据负载；原本关闭无新增收益，需重启");
                case PerformanceActionCatalog.Quantum: return F(action.Key, action.Zh, "选择短可变3:1、长固定1:1或调试档；优先在线应用和恢复，系统拒绝或缺少可还原的原值时需重启", "游戏/交互选短可变，后台批算选长固定；比例不是FPS倍数，以在线结果或重启提示为准");
                case PerformanceActionCatalog.StorageIdle: return F(action.Key, action.Zh, "关闭已登记StorPort磁盘空闲省电；存储组合成员", "磁盘唤醒卡顿；增加功耗，需重启");
                case PerformanceActionCatalog.StorageRuntime: return F(action.Key, action.Zh, "关闭已登记StorPort控制器运行时省电；存储组合成员", "控制器唤醒延迟；增加功耗，需重启");
                case PerformanceActionCatalog.StorageCold: return F(action.Key, action.Zh, "禁止已登记StorPort控制器进入D3Cold；存储组合成员", "深度省电唤醒问题；增加睡眠功耗，需重启");
                case PerformanceActionCatalog.AudioIdle: return F(action.Key, action.Zh, "关闭已有PortCls交流空闲省电；系统增强成员", "首次出声唤醒等待；增加功耗，需重启");
                default: return F(action.Key, action.Zh, action.DetailZh, "以本机支持情况为准");
            }
        }

        private static Feature[] ExtraFeatures()
        {
            return new[] {
                F("GameModeOn", "守护总开关", "启停游戏会话自动优化，独立于后台/反作弊开关；概览激活时显示本局已压制进程/已暂停服务数量，去重且只计已确认执行项；智能切出时明确显示后台压制已释放或恢复中，服务数量独立核验", "需要自动优化时开启；失败/待确认不计入，退局或停守护收起；手动持久配置按各自设置处理"),
                F("GmAwake", "对局保持唤醒", "守护开启且游戏前台时自动阻止显示器/系统休眠；切出、退局或停守护释放，不读取旧开关偏好", "防止黑屏/休眠，不是禁止CPU空闲，不阻止手动休眠"),
                F("GmPauseDl", "传递优化下载让路", "后台治理开启后低频异步查询DO任务；仅确认后台下载活动时暂停，未知/空闲/前台下载不触发；智能切出恢复，竞技切出保留，停用或退局恢复", "DO争网或磁盘；仅处理此组件，不控制Steam等下载器，不依据白名单判断系统下载任务"),
                F("GmGpuDemote", "后台GPU让位", "随后台治理降低合格后台的GPU调度优先级；HAGS允许且驱动支持时执行，读不出原值则跳过；停用、移出治理范围或退局恢复原值", "后台图形任务争GPU；按场景处理前台，保护游戏、依赖与白名单，不读取旧独立开关"),
                F("ConfigurationPresets", "一键配置", "单入口；作者同款/极限配置34项，系统增强子项分别设置；点击配置即应用，完成提醒重启电脑；手动修改后显示自定义", "不可用项跳过，失败简要提示可重试；作者同款采用作者当前配置：正常后台治理、基础CPU缓解关闭范围、关闭VBS、交流音频保持唤醒、NTFS最后访问时间减负关闭，启用计时器恒定节拍与全局计时器分辨率；从扩展切回基础先恢复；极限选激进后台治理、开启GPU锁频/功耗请求及关闭可配置CPU软件缓解的扩展范围；两套CPU空闲跟随原策略、前台时间片恢复原配置；两套均降低相应防护、需重启；执行仍核验；不改智能/竞技运行策略、游戏配置、白名单；作者同款不是Windows出厂恢复"),
                F("OverviewTelemetry", "概览硬件/占用监控", "显示硬件型号；概览可见时持续低频异步读取CPU/GPU占用和内存，游戏中继续显示实时数据；隐藏或离页停止读取", "查看资源状态，不测游戏FPS，不采集游戏帧率"),
                F("StartupSecurityCompatibility", "杀毒兼容提示", "启动只查一次名称/状态，之后含局内只读快照，下次启动更新；启用含Defender均警告，仅停用不警告，失败未知", "概览游戏/守护行上方黄色SVG，悬停摘要、点击弹窗：加白/行为防护说明；底部左不再显示（跨启动）、右知道了；加白/隐藏不代表放行或已拦截/掉帧；不改守护/安全设置，无顶部入口"),
                F("ModeSmart", "智能模式", "CPU≥70%持续10秒扩围；<60%持续5秒后平稳120秒恢复；切出释放并保护前台", "常切屏、兼顾浏览器/语音；切屏不全清优先级/绑核"),
                F("ModeCompetitive", "竞技模式", "较广治理，切出保留，合格软件前台不豁免；保护游戏、白名单、必要依赖", "后台争用；用户会话范围回归2223，不普压Session0服务"),
                F("ManagedPower2223", "托管电源", "核验台式机按CPU匹配新版预置，CPU/平台无法安全匹配保留原计划；笔记本、检测中或未知机型沿用3.1.1.0旧策略；不靠电池判机型，UPS不当笔记本；智能/竞技共用所选策略参数；无来源选择器，旧手选无效；复制标准模板，按AC/DC及架构规则逐项设置；切计划通知及时拉回，每15秒核验托管参数漂移，异常按当前来源重建，每局限3次，再被改（如厂商控制中心）自动改为不切换并退回原计划", "按厂商与设备型号自动识别掌机（含ROG Ally和GPD WIN系列）；掌机不切换，不创建/维护托管计划，指定已有计划也不接管，保留原选择偏好，交给系统和厂商软件管理；不把S0或带电池单独视为掌机；维护避开实验室持有参数，换计划先恢复实验室，退局还原；可能增耗增热"),
                F("ManagedPowerDynamic", "托管电源动态参数", "交流AC与电池DC分别设置；旧策略采用原竞技基线，台式机新版按CPU匹配，智能/竞技共用；按CPU拓扑与供电特征调整处理器、停泊、EPP与睿频，沿用旧策略设备DC放宽规则；场景/升档/切出不改参数", "由软件调度时自动计算，无额外开关；不切换/指定已有计划优先，退局恢复；实验室覆盖按自身原值恢复"),
                F("GmMmcss", "多媒体调度", "SystemResponsiveness=10、Games调度High、NoLazyMode=1", "参与MMCSS的游戏/多媒体线程；持久配置需重启"),
                F("AutoLaptopPerformanceV2", "笔记本厂商性能模式", "在顶部策略设置→对局电源下方开启；仅在已核验支持的笔记本显示，检测中及台式机等不支持设备隐藏；联想 SmartFan 核验原档，ASUS 通过运行中的 G-Helper 前五个配置档切 Turbo，由新驱动确认记录核验并在退局恢复完整原配置", "默认关；交流供电、游戏会话且电源非不切换；ASUS DSTS 仅用于接口识别，保留已有自定义 Turbo/全速；外部改档让出，增热/噪声，不等于Windows电源计划"),
                F("GpuPrefStageOn", "游戏高性能显卡预选", "登记库中游戏Windows高性能GPU偏好", "双显卡误选；build17134+、交流供电，重启游戏"),
                F("Hags", "硬件加速GPU调度HAGS", "开启/关闭；原配置按记录恢复", "系统/驱动/GPU支持、状态可读；重启核验"),
                F("WindowedOptimizations", "窗口化游戏优化", "开启/关闭窗口/无边框优化；原配置按记录恢复", "Win11≥22H2/DX10、11，状态可读；重启游戏"),
                F("VariableRefreshRate", "Windows可变刷新率补充", "开启/关闭DX11全屏VRR补充；原配置按记录恢复", "显示器/驱动支持、状态可读；重启游戏"),
                F("NvVrrWindowed", "NVIDIA窗口VRR补充", "对局为已启用全屏G-SYNC的设备补充窗口VRR", "兼容NVIDIA窗口游戏；部分面板闪烁，退局恢复"),
                F("AmdSam", "AMD Smart Access Memory", "开启/关闭；原配置按记录恢复；需BIOS ReBAR/Above4G", "AMD支持、状态可读；需重启"),
                F("GameDvrOff", "关闭后台游戏录制", "关闭Windows后台游戏录制", "录制占编码/GPU/磁盘资源；不控制其他录屏软件"),
                F("EnvPerformanceBundleV1", "系统性能增强组合", "联动VBS/Hypervisor、CPU缓解、NTFS最后访问、交流音频省电四项", "对应开销明显；影响防护、WSL2/Hyper-V及部分反作弊要求；关闭恢复原值，需重启"),
                F("EnvVbsDisabledV1", "系统增强·VBS/Hypervisor", "关闭VBS、内存完整性和相关hypervisor启动项", "虚拟化安全开销；影响WSL2/Hyper-V及部分反作弊，需重启"),
                F("EnvStoragePowerGroupV1", "存储设备电源管理组合", "联动StorPort磁盘空闲、控制器运行时省电、D3Cold三项", "存储唤醒卡顿；仅修改已有登记配置，增耗；关闭恢复原值，需重启"),
                F("LabEppYieldV1", "实验室·EPP功耗让路", "实际自主性能模式支持且GPU忙/CPU有余量时调EPP让共享功耗，验证失败回退", "插电笔记本+托管+可读功耗；台式/电池不执行，可能转CPU瓶颈；换计划/空闲策略先恢复后重采样"),
                F("LabGpuClockMaxV1", "实验室·GPU频率锁最大值", "请求支持驱动的最高核心频率，仍受温度/功耗限制", "支持的NVIDIA/AMD接口，频率波动；增热，勿叠加锁频工具；退局NVIDIA回自动、AMD还原"),
                F("LabGpuPowerMaxV1", "实验室·GPU功耗墙拉满", "请求驱动允许的最大GPU功耗上限，不改电压", "GPU确受功耗墙限制；增热增耗，关闭/退局恢复"),
                F("LabAudioLowBufferV1", "实验室·音频低缓冲流", "为默认输出申请较短共享音频周期并读回", "支持的同设备共享音频，跳过蓝牙；可能爆音/增唤醒，退局释放"),
                F("LabCpuIdleStrategyV1", "实验室·CPU空闲策略", "跟随当前计划基准/提高空闲门槛/禁止CPU空闲，在计划生效后覆盖当前计划，无需托管", "交流对局唤醒瓶颈；禁止空闲增热增耗；关闭/切策略/换计划恢复该计划原值，退局恢复"),
                F("AntiCheatGroups", "反作弊专项压制", "ACE/EAC/BattlEye各组直接开启/关闭，全部关闭即停止，无总开关；游戏会话稳定后仅治理已核验独立服务，不改驱动", "旧总关闭偏好升级保持关闭；资源治理和绑核均排除游戏/启动器祖先及不明依赖，身份或会话无法核验时保留原设置；仅保护/只记录分组不压制；过强可能影响稳定"),
                F("AcModeV1", "反作弊强度", "温和：BelowNormal/Low I/O/页面3；均衡：BelowNormal/VeryLow/页面2并禁动态提优；强力：Idle/VeryLow/页面1、禁动态提优、支持时忽略高精度计时器请求；三档均启用EcoQoS", "温和/均衡保留计时器请求，温和保留动态提优；按4/2/1秒核验、有限纠偏；降档按目标恢复、关闭组恢复原值，强力可能增加游戏等待"),
                F("AntiCheatWeakCoreAffinity", "反作弊弱核硬绑定", "对局中核验独立服务身份/拓扑后共享完整物理核（含SMT）；温和最多4个能效/其他缓存域核或2个同构辅助核，均衡2核，强力1核；避让游戏保护核，优先Job锁定核心范围", "随分组内置，无独立开关；排除游戏/启动器祖先，保留CPU Sets；Job不可用且清理完成后仅进程亲和性回退，原因记入诊断，不启用CPU占用硬上限；切档先恢复再绑定，退局/异常先解除Job限制再按归属恢复，失败显示原因"),
                F("AntiCheatCompatibility", "反作弊兼容名单/范围说明", "查看写入拒绝后的兼容保护名单，可整表清空让后续重试；查看仅保护/仅记录产品及进程范围", "排查兼容性；清空名单不等于关闭反作弊"),
                F("Whitelist", "白名单与依赖保护", "从运行进程/EXE/拖放添加路径/家族保护；运行列表含浏览器、聊天/开发工具及最小化/托盘应用，排除系统与反作弊；内置六类：Windows系统进程、网游加速器、显卡驱动与硬件功耗散热控制、反作弊与游戏保护组件、输入音频与外设驱动链、直播与推流工具；各类可查看目录并按具体名称搜索，系统预置集中展示且匹配规则保留；直播主程序及专用组件保护已核验子孙，父进程退出后仍保留存活成员，独立组件单独识别，不随游戏家族后台治理关闭；查看范围、移除可删项、恢复平台/预设；预置回归2223，保留用户项及依赖", "保护语音/外设/录屏/直播；过宽放过负载，无逐项限速"),
                F("GameLibrary", "游戏库/自动入库/忽略名单", "添加程序/运行目标（含最小化/托盘游戏）、平台扫描/自动收录、搜索/筛选、重命名、换/还原封面、移除/撤销忽略", "识别游戏；自动收录默认关闭，移除后防重复收录"),
                F("GameForceTrigger", "逐游戏强制触发优化", "对已确认游戏启用强制会话触发", "正常识别失败时使用；误用于普通程序会错误触发游戏策略"),
                F("GameCoreIsolation", "逐游戏核心隔离/核心选择", "后台避让游戏整颗物理核；按全部/P核/E核/CCD/缓存域选核、保留单线程/补全SMT、查看绑定证据", "有备用核且拓扑/分配可靠；范围过小可能回退，不隔离中断"),
                F("GameCriticalThreadHardRuleV1", "逐游戏渲染线程亲和性", "独立弹窗选自动/手动；手动仅指定一个逻辑CPU，不保存线程号，仍自动识别：持续≥30%单核，DirectX呈现优先，否则观察30秒选明显领先者，≥3个负载相近则不绑；≥70%的其他线程仅设优先核，可迁走；最多4个，自动优选P核/优选核/X3D并尽量避开CPU0；两次一致后先核验并避让其他游戏线程（含SMT，最多512），再绑主线程并隔离外部默认调度；新线程下次核验接管，不隔离中断/DPC或外部强绑；同一线程不叠加额外线程提优，旧手动规则保持", "候选跟随核心调度，未开则全核；范围冲突保存前解决，换核/范围下局生效，关闭立即撤回；至少3物理核，不要求SMT，留两核给工作线程；前台识别、切出保留已有绑定并暂停采样与新增、切回立即核验、退局恢复；自动模式遇游戏改回分配立即让出，连续失读或候选反复更换则停止本局重绑；权限不足/自定义分配撤回，同局重开不补失败预算；诊断含实际范围、覆盖、优先级读回及内存耗时记录，不保证增帧"),
                F("LolColumnEnabled", "英雄联盟独立增强", "提供LOL专属附属清理与无头客户端管理", "需核验LOL/客户端身份；受客户端版本影响"),
                F("LolColumnCleanupEnabled", "LOL附属进程清理/恢复", "按游戏阶段清理非必要平台负载，提供立即清理/恢复", "平台附属持续占用；社交/助手可能暂不可用"),
                F("LolColumnHeadlessEnabled", "LOL无头客户端", "按游戏阶段降低/暂停客户端界面负载，保留游戏主体", "客户端占CPU/GPU；需可靠客户端接口，回大厅恢复"),
                F("LolAddonCleanup", "LOL插件检查/删除", "游戏卡片后台检查并提示可清理附加层，点击进入专属优化，用户确认后删除", "排查异常插件；运行占用时阻止，删除需平台修复/重下恢复"),
                F("ManualMemoryCleanup", "手动极限清理", "游戏中仅回收Pavise自身内存，切屏仍保护；非游戏才允许更广清理", "手动释放内存；清空缓存可能增加后续重载/卡顿"),
                F("Checkup", "体检", "约120秒只读采样，可取消/重新体检；按结果提供下列修复入口", "系统排障；不测FPS、1% low、帧时间或输入延迟"),
                F("SettingsStartup", "开机启动", "管理Pavise登录启动任务", "需要自动守护"),
                F("AutoHideOnGame", "入局自动隐藏", "隐藏主窗口，保留后台守护，可从托盘打开", "减少界面干扰/可见刷新"),
                F("GameExitGrace", "退出宽限", "游戏证据暂失后等0/2/5/8/15/30秒再恢复会话策略", "避免短暂重启导致反复切换；会延迟退局恢复"),
                F("ShaderCleanup", "着色器缓存清理", "手动删受支持的着色器缓存", "排查旧/坏缓存；重编译可能卡顿，不宜每局清"),
                F("SettingsReset", "重置设置", "重置偏好/数据，保护原值恢复记录", "错误配置排障；先恢复已拥有的系统修改，会丢相应偏好"),
                F("SettingsAppearance", "视觉定制/窗口/托盘", "语言/明暗/模式配色/背景浓度；16款动效（" + CoreMotionCatalog.ChineseChoices + "）同库，两模式可同选，选中即应用/恢复默认；开合/旋转/蓄能脉冲，配色随主题，失焦/隐藏/离页/对局停播；拖动/最小化/托盘开关", "界面可读性，不增帧"),
                F("LogWritesEnabled", "写入日志", "启停Pavise运行日志写入", "减少日志写入或保留排障证据；关闭后记录不完整"),
                F("LogManagement", "运行日志", "默认动作日志：当前或最近会话的成功压制进程名单、已生效功能、已核验游戏与渲染线程CPU范围及避让、清核核验状态、恢复与待确认/失败；切换诊断日志查看构建身份与失败阶段；可选保存位置和文件名，动作导出从完整日志提取，诊断导出完整原文件，不限屏幕尾部；关闭写入仍可导出，不改原日志；清空同一日志源", "不重新采样或执行调度；记录不足不代表成功；清空不还原系统"),
                F("SettingsLogsUninstall", "版本/说明/支持/卸载", "版本说明/官网/源码/捐赠；卸载先恢复", "版本核验；说明仅展示一次"),
                F("SoftwareUpdate", "软件内更新/失败手动下载", "验签下载EXE，显示下载/校验/安装进度；检查和下载可取消，取消后可重新检查，不将旧版误报为新版；更新期间禁止退出、替换不可取消；安全退出后原位替换，新版启动确认后删除旧版并同步启动入口；失败同一弹窗转手动更新、解除关闭限制，提供夸克官网入口/GitHub", "退局/维护结束后联网管理员操作；保留个人数据/恢复资产；替换失败先回滚，不强杀活跃进程；备用下载按页说明更新"),
                F("SettingsAiTuning", "AI调优提示词", "只读汇总硬件/全部功能/设置，补充目标、预览/复制", "供自选AI建议，不联网/应用")
            };
        }

        private static Feature[] CheckupFeatures()
        {
            return new[] {
                F("AuditGpuIrq", "体检·显卡中断放置/验证", "采样显卡ISR/DPC落点与P99，有证据时将中断移出游戏保留核，重启后验证；退化/驱动重置则尝试回滚", "至少300个有效事件、单处理器组、可靠保留区/区外核心；游戏中禁止应用，未知/外部配置等情况跳过"),
                F("AuditGpuIrqRestore", "体检·恢复显卡中断配置", "还原本工具拥有的显卡IRQ放置及历史中断优先级配置", "撤销本工具中断调整或处理残留；依记录恢复，按提示重启"),
                F("AuditMsi", "体检·显卡MSI中断修复", "将当前显卡已有MSISupported=0修复为1，可恢复", "显卡MSI被明确关闭；不为所有设备强造设置，需重启；与中断放置不同"),
                F("AuditSlowDrivers", "体检·驱动延迟诊断", "展示较慢驱动的最大耗时、P99与设备线索", "排查DPC/ISR延迟；不提供自动修复所有驱动"),
                F("AuditMemoryCompression", "体检·内存压缩修复", "读取内存压缩/页面合并，压缩关闭时可启用，并按记录恢复", "排查内存压力；本项不是关闭内存压缩"),
                F("AuditNetworkThrottle", "体检·网络限流值修复", "异常NetworkThrottlingIndex修复到10；1–70和0xFFFFFFFF均有效，后者关闭网络节流，保留不改", "不因非默认而修复；旧版修复记录仍可恢复；联网异常应撤回"),
                F("AuditLinkMetric", "体检·有线优先修复", "有可靠有线网关却默认走Wi-Fi时调整有线metric；撤销回自动metric", "路由选择异常且无歧义；不承诺保留任意原手动metric"),
                F("AuditFth", "体检·容错堆FTH修复", "对命中程序登记FTH排除、移除已挂载兼容补丁引用并保留文件，可恢复", "FTH补丁确实介入；可能影响容错，不是全局关闭所有兼容机制"),
                F("AuditQuantum", "体检·前台时间片检查/入口", "显示当前时间片线索并跳转「前台时间片」选择器", "沿用时间片设置，无额外开关"),
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
                    if (PerformancePolicy.IsGamePriorityOption(key)) return "随总开关" + OnOff(PolicyResolver.RawGlobalValue("GmBoost") == "1");
                    if (key == "GameFamilyBackground" || key == "GmAutoCoresV2" || key == "GameNvRebarV1" || key == "GameDisableFullscreenOptimizationsV1") return "逐游戏见下文，默认关";
                    if (item.Kind == PolicyValueKind.Bool) return OnOff(value == "1");
                    return value == "on" ? "开启" : value == "off" ? "关闭" : "保存值未知";
                }
                switch (key)
                {
                    case "GmAwake": return "自动随前台游戏执行";
                    case "GmPauseDl": return "随后台治理" + OnOff(PolicyResolver.Global().SuppressBackground) + "；任务活动未核验";
                    case "GmGpuDemote": return "随后台治理" + OnOff(PolicyResolver.Global().SuppressBackground) + "；GPU执行条件未核验";
                    case "ConfigurationPresets": {
                        ConfigurationPresetKind? preset = ConfigurationPresetRuntime.CurrentFromSettings();
                        return preset.HasValue ? (preset.Value == ConfigurationPresetKind.Extreme ? "极限配置" : "作者同款") + "（保存配置匹配，实际生效未核验）" : "自定义/未应用预设";
                    }
                    case "ModeSmart": case "ModeCompetitive": return "全局模式=" + (PolicyResolver.RawGlobalValue("PerformancePreset") == "1" ? "竞技" : "智能");
                    case "GameModeOn": return BoolConfiguration(key, true);
                    case "LogWritesEnabled": return BoolConfiguration(key, true);
                    case "OverviewTelemetry": return "运行时只读显示";
                    case "StartupSecurityCompatibility": return Settings.Load(SecurityCompatibilityPresentation.HiddenSettingKey, false) ? "不再显示" : "显示提示";
                    case "SettingsAppearance": return "智能动效=" + CoreMotionCatalog.Name(CoreMotionPreferences.Get(PerformancePreset.Standard)) + "；电竞动效=" + CoreMotionCatalog.Name(CoreMotionPreferences.Get(PerformancePreset.Competitive));
                    case "GameForceTrigger": return "逐游戏设置见下文";
                    case "ManagedPower2223": case "ManagedPowerDynamic": case "ManagedPowerHandheld": return PowerConfiguration();
                    case "AntiCheatGroups": return AntiCheatConfiguration();
                    case "AcModeV1": { string modeToken = Settings.LoadStr("AcModeV1", "isolated"); return modeToken == "gentle" ? "温和" : modeToken == "balanced" ? "均衡" : "强力"; }
                    case "AntiCheatWeakCoreAffinity": return "随专项，无独立开关";
                    case "AntiCheatCompatibility": return AntiCheatConfiguration();
                    case "GmMmcss": return BoolConfiguration(key, true);
                    case "AutoLaptopPerformanceV2": return BoolConfiguration(key, false) + "；接口="
                        + (mode == null || !mode.LaptopPerformanceSupportKnown ? "未核验" : mode.LaptopPerformanceSupported ? "已确认支持" : "未确认兼容接口");
                    case "GpuPrefStageOn": return BoolConfiguration(key, true);
                    case "GameDvrOff": return BoolConfiguration(key, true);
                    case "LolColumnEnabled": case "LolColumnCleanupEnabled": case "LolColumnHeadlessEnabled": return BoolConfiguration(key, false);
                    case "LabEppYieldV1": case "LabGpuClockMaxV1": case "LabGpuPowerMaxV1": case "LabAudioLowBufferV1": return BoolConfiguration(key, false);
                    case "LabCpuIdleStrategyV1": { string selected = Settings.LoadStr(key, "default"); return selected == "threshold" ? "提高空闲门槛" : selected == "disabled" ? "禁止CPU空闲" : "跟随原策略"; }
                    case "EnvPerformanceBundleV1": return "成员偏好：" + EnvironmentIntent("EnvVbsDisabledV1") + "；" + EnvironmentIntent("EnvCpuMitigationsV1") + "；" + EnvironmentIntent("EnvNtfsLastAccessV1") + "；" + EnvironmentIntent("EnvAudioIdleV1");
                    case "EnvStoragePowerGroupV1": return "成员偏好：" + EnvironmentIntent("EnvStorageIdleV1") + "；" + EnvironmentIntent("EnvStorageRuntimeV1") + "；" + EnvironmentIntent("EnvStorageD3ColdV1");
                    case "EnvQuantumProfileV1": return QuantumConfiguration();
                    case "GameLibrary": return BoolConfiguration("GmAutoAdd", false) + "（自动入库）；逐游戏摘要见下文。";
                    case "GameExitGrace": return mode == null ? "未取得会话对象，未读取宽限值。" : "保存宽限=" + mode.ExitGraceSeconds.ToString(CultureInfo.InvariantCulture) + "秒。";
                    case "AutoHideOnGame": return BoolConfiguration(key, true);
                    case "SettingsStartup": return "启动任务未核验";
                    case "SoftwareUpdate": return SelfUpdateGate.IsBusy ? "正在更新，退出已锁定" : "未开始更新";
                    case "Hags": case "AmdSam": case "WindowedOptimizations": case "VariableRefreshRate": case "NvVrrWindowed": return "未知";
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
                case PerformanceActionCatalog.CpuMitigations: effect = "关闭Spectre V2/Meltdown缓解" + (PerformanceTuning.CpuMitigationsExpanded ? "及扩展软件策略（取消SSBD/AMD BTI显式启用，非全部缓解）" : "（旧版基础范围，未选择扩展）"); break;
                case PerformanceActionCatalog.LastAccess: effect = "停止NTFS最后访问更新"; break;
                case PerformanceActionCatalog.AudioIdle: effect = "关闭交流音频空闲省电"; break;
                case PerformanceActionCatalog.StorageIdle: effect = "关闭磁盘空闲省电"; break;
                case PerformanceActionCatalog.StorageRuntime: effect = "关闭控制器运行时省电"; break;
                case PerformanceActionCatalog.StorageCold: effect = "禁止控制器D3Cold"; break;
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

        private static string AntiCheatConfiguration()
        {
            var text = new StringBuilder();
            foreach (AcGroup group in AntiCheatCatalog.Groups)
            {
                if (text.Length > 0) text.Append("；");
                text.Append(group.Key).Append('=');
                text.Append(group.Suppressible ? BoolConfiguration("Tame_" + group.Key, group.Default) : group.RecordOnly ? "仅记录，不可压制" : "仅保护，不可压制");
            }
            return text.ToString();
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
                    text.AppendLine("  [GameForceTrigger] 强制触发=" + OnOff(profile.ForceTrigger));
                    foreach (string key in new[] { "GmAutoCoresV2", "GameFamilyBackground", "GameNvRebarV1", "GameDisableFullscreenOptimizationsV1" })
                    {
                        string value;
                        bool specified = profile.Overrides.TryGetValue(key, out value);
                        if (!specified) value = PolicyResolver.RawGlobalValue(key);
                        text.AppendLine("  [" + key + "] " + (specified ? "逐游戏保存=" : "未逐游戏开启，默认=") + OnOff(value == "1"));
                    }
                    string coreRaw;
                    profile.Overrides.TryGetValue(ManualCorePlan.Key, out coreRaw);
                    ManualCorePlan plan = ManualCorePlan.Parse(coreRaw);
                    text.AppendLine("  [GmManualCorePlanV2] " + (plan == null ? "未保存有效手动核心计划" : "逻辑处理器掩码=0x" + plan.GameMask.ToString("X", CultureInfo.InvariantCulture) + "；后台核心隔离=" + OnOff(plan.Isolation) + "；硬亲和性=" + OnOff(plan.HardAffinity)) + "；本机拓扑适用性未核验。");
                    string threadRaw;
                    profile.Overrides.TryGetValue(CriticalThreadRule.Key, out threadRaw);
                    CriticalThreadRule thread = CriticalThreadRule.ParseForRuntime(threadRaw);
                    text.AppendLine("  [GameCriticalThreadHardRuleV1] " + (thread == null ? "未保存有效渲染线程绑定规则" : (thread.ManualTarget ? "已保存手动目标 " + CriticalThreadRule.ProcessorName(thread.Targets[0]) : thread.Automatic ? "已保存自动选核规则" : "已保存旧指定候选规则") + "；其他工作线程避让=" + OnOff(thread.WorkerAvoidance)) + "；是否实际已绑定未核验。");
                }
                StandbyCleanerOptions cleaner = mode.StandbyCleaningOptions;
                if (cleaner != null) text.AppendLine("[GmStandbyCleanerOptionsV1] 保存阈值：待机列表=" + cleaner.ListMegabytes + "MB，可用内存=" + cleaner.FreeMegabytes + "MB，轮询=" + cleaner.PollingMilliseconds + "ms；是否实际已触发清理未核验。");
            }
            catch { text.AppendLine("部分游戏配置读取失败，未核验；不能按无配置处理。"); }
        }

        private static string SafeName(string value)
        {
            string text = (value ?? "未命名游戏").Replace('\r', ' ').Replace('\n', ' ');
            if (Regex.IsMatch(text, @"[A-Za-z]:[\\/]|\\\\")) return "名称含路径，已省略";
            return text;
        }

        private static string QuantumConfiguration()
        {
            int selected = QuantumProfiles.Selected;
            if (selected == 0) return "恢复原配置";
            if (selected == 1) return "游戏推荐：短可变3:1";
            if (selected == 8) return "后台计算推荐：长固定1:1";
            return "对照调试档序号" + selected.ToString(CultureInfo.InvariantCulture) + "（不是帧率倍数）";
        }

    }
}
