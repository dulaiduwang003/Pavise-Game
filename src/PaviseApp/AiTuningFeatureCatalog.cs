using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PaviseApp
{
    // Read-only export: one concise purpose/usage entry per current capability.
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
            text.AppendLine("条目数非开关数；当前仅保存偏好非生效，—无单值。恢复仅撤回工具记录，非强制开启/出厂重置；HAGS/SAM/窗口化优化/系统VRR关闭则请求关闭，原值另走恢复维护。会话退局恢复，持久项按重启提示。");
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
                        ? F(action.Key, action.Zh, "限制UWP后台活动，保留逐应用例外，不管普通桌面软件", "UWP后台活跃；可能延迟通知/同步，需重启应用或系统")
                        : F(action.Key, action.Zh, "扩大NTFS文件系统缓存池", "内存充足、文件元数据负载高；增加内存占用，需重启");
                    Append(text, ref count, feature, BoolConfiguration(PrivacyControl.PreferencePrefix + action.Key, false));
                }
                foreach (Feature feature in ExtraFeatures()) Append(text, ref count, feature, Configuration(feature.Key, mode));
                foreach (Feature feature in CheckupFeatures()) Append(text, ref count, feature, "未复查");
                AppendProfiles(text, mode);
            }
            text.AppendLine("功能条目：" + count.ToString(CultureInfo.InvariantCulture));
            text.AppendLine("不可开启：通用/手动LOL脱壳、文件过滤/计时修复/音效减负、Defender、全局ETW、网络旁路/统一限速、后台全冻结/工作集修剪/CPU硬上限、旧极限档、AMD Anti-Lag；仅恢复旧记录。");
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
                case "GmBoostCpuV1": case "GmBoostMemoryV1": case "GmBoostIoV1": case "GmBoostGpuV1":
                    return "随「性能优先级」，无独立开关";
                case "EnvVbsDisabledV1": case PerformanceActionCatalog.CpuMitigations:
                case PerformanceActionCatalog.LastAccess: case PerformanceActionCatalog.AudioIdle:
                    return "随「系统性能增强」组合，无独立开关";
                case PerformanceActionCatalog.StorageIdle: case PerformanceActionCatalog.StorageRuntime: case PerformanceActionCatalog.StorageCold:
                    return "随「存储设备电源管理」组合，无独立开关";
                case "ModeSmart": case "ModeCompetitive": return "模式行为说明，选运行模式";
                case "ManagedPower2223": case "ManagedPowerHandheld": return "电源行为说明，选由软件调度";
                case "AntiCheatWeakCoreAffinity": return "专项内置，无独立开关";
                case "PerformancePreset": case "PowerPlanOn": case "AcModeV1":
                case "LabCpuIdleStrategyV1": case PerformanceActionCatalog.Quantum:
                case "GameExitGrace": return "选择项";
                case "GameFamilyBackground": case "GameDisableFullscreenOptimizationsV1":
                case "GmAutoCoresV2": case "GameNvRebarV1": case "GameCriticalThreadHardRuleV1": case "GameForceTrigger": return "逐游戏开关";
                case "GameCoreIsolation": return "逐游戏核心计划选项";
                case "AntiCheatMaster": return "总开关+可处理分组";
                case "AntiCheatCompatibility": return "查看/整表清空兼容名单";
                case "Whitelist": return "名单管理";
                case "GameLibrary": return "自动入库开关+游戏管理";
                case "LolColumnEnabled": return "专属模块说明，LOL卡片启用，子项独立";
                case "LolColumnCleanupEnabled": return "开关+手动清理/恢复";
                case "LolAddonCleanup": case "ManualMemoryCleanup": case "Checkup":
                case "FaultRecovery": case "ShaderCleanup": case "SettingsReset":
                case "SettingsLogsUninstall": case "SettingsAiTuning": case "DiagnosticCopy":
                case "SoftwareUpdate": return "手动操作";
                case "ConfigurationPresets": return "新手引导内点击配置即应用";
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
                default: return "独立开关";
            }
        }

        private static Feature F(string key, string title, string purpose, string scene)
        { return new Feature(key, title, purpose, scene); }

        private static Feature PolicyFeature(PolicyItem item)
        {
            string key = item.Key;
            switch (key)
            {
                case "PerformancePreset": return F(key, "运行模式", "选择智能/竞技，改变后台治理和托管电源行为", "按切屏需求和后台争用选择，不会自动打开所有功能");
                case "GmSuppress": return F(key, "后台治理", "降低合格后台CPU、I/O、页面优先级，按支持请求EcoQoS；最高档Idle/VeryLow/页面1", "后台争用资源；排除前台、游戏、关键依赖、白名单，范围回归2223");
                case "GmThreadBoostStrategyV1": return F(key, "游戏线程提优", "优先提优可识别的DirectX画面提交线程，否则选最忙线程；渲染采用TimeCritical", "关键线程被抢占；最忙不一定是渲染，普通High进程不因此进入实时类");
                case "GmEnglishInputV1": return F(key, "游戏窗口切换英文输入", "入局为游戏窗口尝试一次英文输入布局，退局尝试还原", "避免输入法误触/组合输入干扰");
                case "GameFamilyBackground": return F(key, "逐游戏家族后台治理", "识别真实游戏渲染进程后处理非必要启动器/附属负载", "平台后台持续占用；必要依赖仍受保护");
                case "GameDisableFullscreenOptimizationsV1": return F(key, "逐游戏禁用全屏优化", "设置所选游戏的Windows全屏优化兼容标志", "排查显示或切屏问题；可能回退，重启游戏");
                case "GmAutoCoresV2": return F(key, "逐游戏核心范围调度", "按保存的逻辑CPU范围运行，支持硬亲和性与后台隔离", "减少跨缓存域/SMT争用；范围太窄会限并行，需可信拓扑");
                case "GameNvRebarV1": return F(key, "逐游戏NVIDIA ReBAR", "设置游戏驱动ReBAR偏好", "支持ReBAR的NVIDIA平台；逐游戏测试，重启游戏");
                case "GmBoost": return F(key, "性能优先级", "联动游戏CPU High、页面5、磁盘I/O高和GPU调度提优，各项读回核验", "对局有CPU/磁盘/GPU资源竞争；退局恢复");
                case "GmBoostCpuV1": return F(key, "性能优先级·CPU", "维持游戏进程High，随总开关联动", "CPU调度竞争，不增加物理算力");
                case "GmBoostMemoryV1": return F(key, "性能优先级·内存", "维持页面优先级5，随总开关联动", "原优先级偏低；默认已为5时无新增收益，不是驻留");
                case "GmBoostIoV1": return F(key, "性能优先级·I/O", "请求游戏高磁盘I/O优先级，随总开关联动", "游戏与后台争磁盘，需权限/存储栈支持");
                case "GmBoostGpuV1": return F(key, "性能优先级·GPU", "请求并读回WDDM游戏调度等级，随总开关联动", "多个程序争GPU；不是CPU TimeCritical");
                case "GmGpuDemote": return F(key, "后台GPU让位", "降低合格后台图形任务GPU调度优先级", "浏览器/其他3D程序与游戏争GPU");
                case "PowerPlanOn": return F(key, "对局电源计划", "选择不切换、由软件调度或指定已有计划，退局返回原计划", "减轻省电约束；自动匹配规则见下文");
                case "GmCacheWarmV2": return F(key, "缓存预热", "空闲时预读游戏文件进系统缓存，入局取消", "冷加载/切图；热缓存收益小，需空闲内存");
                case "GmStandbyCleanerV2": return F(key, "待机列表清理", "按列表大小、可用内存和轮询阈值清理；可选默认/保守/激进/自定义", "特定内存压力；频繁清理会丢缓存并增加卡顿");
                case "GmDwmBoost": return F(key, "桌面合成调度", "调整桌面合成相关多媒体调度，不关闭DWM", "桌面合成竞争；效果取决于呈现路径");
                case "AutoTimerTick": return F(key, "计时器节拍", "useplatformclock=no、useplatformtick=no、disabledynamictick=yes", "计时敏感场景对照试验；可能增耗，持久配置需重启");
                case "AutoGlobalTimer": return F(key, "全局计时器分辨率", "配置全局请求行为，对局按条件请求0.5ms并读回", "短等待敏感场景；新开启需build20348+，全局配置需重启，可能增耗");
                case "GmPauseUpdate": return F(key, "暂停Windows更新", "对局暂停可控制的Windows更新活动", "更新正在下载/安装占资源，退局恢复");
                case "GmIntelEnduranceV1": return F(key, "关闭Intel图形续航限制", "解除支持设备的Endurance Gaming限帧", "Intel驱动确有续航限制；可能增耗");
                case "NvLowLat": return F(key, "NVIDIA驱动低延迟", "开启/关闭适用的驱动低延迟偏好", "受支持API的预渲染排队；不等于通用Reflex，部分需重启游戏");
                case "GmPauseDl": return F(key, "暂停传递优化下载", "暂停Windows Delivery Optimization下载", "此组件占网/磁盘；不限制Steam等其他下载器");
                case "GmPauseMaintenanceV1": return F(key, "暂停自动维护", "对局暂停受支持的Windows自动维护", "维护任务正在竞争资源，退局恢复");
                case "GmAwake": return F(key, "对局保持唤醒", "游戏前台时阻止显示器/系统休眠", "防止黑屏/休眠，不是禁止CPU空闲");
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
                case PerformanceActionCatalog.CpuMitigations: return F(action.Key, action.Zh, "关闭这两类可配置软件缓解，保留其他缓解位；系统增强成员", "缓解开销构成瓶颈；降低防护，需重启");
                case PerformanceActionCatalog.LastAccess: return F(action.Key, action.Zh, "停止NTFS最后访问时间维护；系统增强成员", "文件元数据负载；原本关闭无新增收益，需重启");
                case PerformanceActionCatalog.Quantum: return F(action.Key, action.Zh, "选择短可变3:1、长固定1:1或调试档，也可恢复原配置", "游戏/交互选短可变，后台批算选长固定；比例不是FPS倍数，需重启");
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
                F("GameModeOn", "守护总开关", "启停游戏会话自动优化，独立于后台/反作弊开关；概览激活时显示本局已压制进程/已暂停服务数量，去重且只计已确认执行项", "需要自动优化时开启；失败/待确认不计入，退局或停守护收起；手动持久配置按各自设置处理"),
                F("ConfigurationPresets", "新手引导", "单入口；作者同款/极限配置34项；点击配置即应用，完成提醒重启电脑；手动修改后显示自定义", "不可用项跳过，失败简要提示可重试；极限开启GPU锁频/功耗请求，执行仍核验；不改智能/竞技运行策略、游戏配置、白名单；作者同款不是Windows出厂恢复"),
                F("OverviewTelemetry", "概览硬件/占用监控", "显示硬件型号和空闲时资源占用；游戏中停止概览读取，占用显示游戏中；退出后恢复", "查看资源状态，不测游戏FPS，不采集游戏帧率"),
                F("ModeSmart", "智能模式", "随负载扩大后台治理，切出释放普通治理；托管可受压升档", "常切屏、兼顾浏览器/语音；切屏不全清优先级/绑核"),
                F("ModeCompetitive", "竞技模式", "较广后台治理，切屏保留；豁免实际前台、白名单、依赖；使用竞技电源", "后台争用；用户会话范围回归2223，不普压Session0服务"),
                F("ManagedPower2223", "托管电源", "按架构/智能或竞技自动选择完整来源，无来源选择器，旧手选无效；内部使用AMD通用/AMD低延迟/AMD X3D/Intel Non-K/Intel K系列性能/笔记本台式通用；独立方案保留来源CPU/IO/空闲/自主模式，回填本机日常AC偏好及全部DC基准", "非对称缓存保护停泊/异构调度，ARM/虚拟机/未知保留原计划，掌机按移动端CPU路由，工作站/多处理器组/多插槽用通用计划；换来源先恢复实验室，退局还原；可能增耗增热"),
                F("ManagedPowerUniversal", "通用电源路由", "移动端智能、混合核心AMD移动端、解锁型移动端，以及线程撕裂者/至强/EPYC工作站、多处理器组/多插槽使用通用计划", "普通同构AMD移动端竞技用AMD低延迟；按CPU/核心结构自动匹配，无掌机识别开关；不切换/指定已有计划优先，退局恢复"),
                F("GmMmcss", "多媒体调度", "SystemResponsiveness=10、Games调度High、NoLazyMode=1", "参与MMCSS的游戏/多媒体线程；持久配置需重启"),
                F("AutoLaptopPerformanceV2", "笔记本厂商性能模式", "在性能→游戏性能开启，调用兼容联想/华硕接口切厂商性能/散热档，退局恢复原档", "默认关；交流供电、游戏会话且电源非不切换；外部改档则让出，增热/噪声，不等于Windows电源计划"),
                F("GpuPrefStageOn", "游戏高性能显卡预选", "登记库中游戏Windows高性能GPU偏好", "双显卡误选；build17134+、交流供电，重启游戏"),
                F("AppGpuAutoV1", "后台节能显卡登记", "为合格后台登记节能GPU偏好", "混合显卡减少独显竞争；重启目标程序"),
                F("Hags", "硬件加速GPU调度HAGS", "开启/关闭；原配置按记录恢复", "系统/驱动/GPU支持、状态可读；重启核验"),
                F("WindowedOptimizations", "窗口化游戏优化", "开启/关闭窗口/无边框优化；原配置按记录恢复", "Win11≥22H2/DX10、11，状态可读；重启游戏"),
                F("VariableRefreshRate", "Windows可变刷新率补充", "开启/关闭DX11全屏VRR补充；原配置按记录恢复", "显示器/驱动支持、状态可读；重启游戏"),
                F("NvVrrWindowed", "NVIDIA窗口VRR补充", "对局为已启用全屏G-SYNC的设备补充窗口VRR", "兼容NVIDIA窗口游戏；部分面板闪烁，退局恢复"),
                F("AmdSam", "AMD Smart Access Memory", "开启/关闭；原配置按记录恢复；需BIOS ReBAR/Above4G", "AMD支持、状态可读；需重启"),
                F("GameDvrOff", "关闭后台游戏录制", "关闭Windows后台游戏录制", "录制占编码/GPU/磁盘资源；不控制其他录屏软件"),
                F("EnvPerformanceBundleV1", "系统性能增强组合", "联动VBS/Hypervisor、CPU缓解、NTFS最后访问、交流音频省电四项", "对应开销明显；影响防护、WSL2/Hyper-V及部分反作弊要求；关闭恢复原值，需重启"),
                F("EnvVbsDisabledV1", "系统增强·VBS/Hypervisor", "关闭VBS、内存完整性和相关hypervisor启动项", "虚拟化安全开销；影响WSL2/Hyper-V及部分反作弊，需重启"),
                F("EnvStoragePowerGroupV1", "存储设备电源管理组合", "联动StorPort磁盘空闲、控制器运行时省电、D3Cold三项", "存储唤醒卡顿；仅修改已有登记配置，增耗；关闭恢复原值，需重启"),
                F("LabEppYieldV1", "实验室·EPP功耗让路", "实际自主性能模式支持且GPU忙/CPU有余量时调EPP让共享功耗，验证失败回退", "插电笔记本+托管+可读功耗；台式/电池不执行，可能转CPU瓶颈；换来源/空闲策略先恢复后重采样"),
                F("LabGpuClockMaxV1", "实验室·GPU频率锁最大值", "请求支持驱动的最高核心频率，仍受温度/功耗限制", "支持的NVIDIA/AMD接口，频率波动；增热，勿叠加锁频工具；退局NVIDIA回自动、AMD还原"),
                F("LabGpuPowerMaxV1", "实验室·GPU功耗墙拉满", "请求驱动允许的最大GPU功耗上限，不改电压", "GPU确受功耗墙限制；增热增耗，关闭/退局恢复"),
                F("LabAudioLowBufferV1", "实验室·音频低缓冲流", "为默认输出申请较短共享音频周期并读回", "支持的同设备共享音频，跳过蓝牙；可能爆音/增唤醒，退局释放"),
                F("LabCpuIdleStrategyV1", "实验室·CPU空闲策略", "跟随所选来源基准/提高空闲门槛/禁止CPU空闲，在来源生效后覆盖当前计划，无需托管", "交流对局唤醒瓶颈；禁止空闲增热增耗；关闭/切策略/换计划恢复该来源原值，退局恢复"),
                F("AntiCheatMaster", "反作弊专项压制", "管理可核验ACE/EAC/BattlEye专用服务进程资源，不改驱动", "反作弊负载争用；总开关+组开关，保护启动器/必要依赖，过强可能影响稳定"),
                F("AcModeV1", "反作弊强度", "温和：原CPU/Low I/O；均衡：BelowNormal/Low；强力：BelowNormal/VeryLow；均为页面3", "按争用选择；强力回归2223，不封计时器、不禁动态提优"),
                F("AntiCheatWeakCoreAffinity", "反作弊弱核硬绑定", "优先能效/弱物理核，无能效等级选辅助核，考虑SMT/缓存域并避让游戏核", "随专项内置，无独立开关；确认拓扑/身份后执行，持续冲突停止，退局按归属恢复"),
                F("AntiCheatCompatibility", "反作弊兼容名单/范围说明", "查看写入拒绝后的兼容保护名单，可整表清空让后续重试；查看仅保护/仅记录产品及进程范围", "排查兼容性；清空名单不等于关闭反作弊"),
                F("Whitelist", "白名单与依赖保护", "从运行进程/EXE/拖放添加路径/家族保护；运行列表含浏览器、聊天/开发工具及最小化/托盘应用，排除系统与反作弊；查看范围、移除可删项、恢复平台/预设；预置回归2223，保留用户项及依赖", "保护语音/外设/录屏；过宽放过负载，无逐项限速"),
                F("GameLibrary", "游戏库/自动入库/忽略名单", "添加程序/运行目标（含最小化/托盘游戏）、平台扫描/自动收录、搜索/筛选、重命名、换/还原封面、移除/撤销忽略", "识别游戏；自动收录默认关闭，移除后防重复收录"),
                F("GameForceTrigger", "逐游戏强制触发优化", "对已确认游戏启用强制会话触发", "正常识别失败时使用；误用于普通程序会错误触发游戏策略"),
                F("GameCoreIsolation", "逐游戏核心隔离/核心选择", "后台避让游戏整颗物理核；按全部/P核/E核/CCD/缓存域选核、保留单线程/补全SMT、查看绑定证据", "有备用核且拓扑/分配可靠；范围过小可能回退，不隔离中断"),
                F("GameCriticalThreadHardRuleV1", "逐游戏渲染线程硬亲和性", "识别DirectX提交线程，持续≥35%单核忙碌后绑一个逻辑CPU，其他线程避让整核", "至少3物理核，每核选一个逻辑CPU，不要求SMT；前台识别、切出保留、退局恢复"),
                F("LolColumnEnabled", "英雄联盟独立增强", "提供LOL专属附属清理与无头客户端管理", "需核验LOL/客户端身份；受客户端版本影响"),
                F("LolColumnCleanupEnabled", "LOL附属进程清理/恢复", "按游戏阶段清理非必要平台负载，提供立即清理/恢复", "平台附属持续占用；社交/助手可能暂不可用"),
                F("LolColumnHeadlessEnabled", "LOL无头客户端", "按游戏阶段降低/暂停客户端界面负载，保留游戏主体", "客户端占CPU/GPU；需可靠客户端接口，回大厅恢复"),
                F("LolAddonCleanup", "LOL插件检查/删除", "检查可识别附加文件，用户选择后删除", "排查异常插件；运行占用时阻止，删除需平台修复/重下恢复"),
                F("ManualMemoryCleanup", "手动极限清理", "游戏中仅回收Pavise自身内存，切屏仍保护；非游戏才允许更广清理", "手动释放内存；清空缓存可能增加后续重载/卡顿"),
                F("Checkup", "体检", "约120秒采样，可取消/重新体检；展示检测结果并提供下列适用修复入口，检测本身不应用修改", "系统排障；不测FPS、1% low、帧时间或输入延迟，不能充当游戏性能基准"),
                F("FaultRecovery", "故障/恢复维护", "查看失败/原值记录，重试恢复本工具修改", "异常退出/失败后恢复，含退役项历史记录"),
                F("SettingsStartup", "开机启动", "管理Pavise登录启动任务", "需要自动守护"),
                F("AutoHideOnGame", "入局自动隐藏", "隐藏主窗口，保留后台守护，可从托盘打开", "减少界面干扰/可见刷新"),
                F("GameExitGrace", "退出宽限", "游戏证据暂失后等0/2/5/8/15/30秒再恢复会话策略", "避免短暂重启导致反复切换；会延迟退局恢复"),
                F("ShaderCleanup", "着色器缓存清理", "手动删受支持的着色器缓存", "排查旧/坏缓存；重编译可能卡顿，不宜每局清"),
                F("SettingsReset", "重置设置", "重置偏好/数据，保护原值恢复记录", "错误配置排障；先恢复已拥有的系统修改，会丢相应偏好"),
                F("SettingsAppearance", "视觉定制/窗口/托盘", "语言/明暗/模式配色/背景浓度；16款动效（" + CoreMotionCatalog.ChineseChoices + "）同库，两模式可同选，选中即应用/恢复默认；开合/旋转/蓄能脉冲，配色随主题，失焦/隐藏/离页/对局停播；拖动/最小化/托盘开关", "界面可读性，不增帧"),
                F("LogWritesEnabled", "写入日志", "启停Pavise运行日志写入", "减少日志写入或保留排障证据；关闭后记录不完整"),
                F("DiagnosticCopy", "复制诊断/清空日志", "复制诊断摘要供排障，可刷新/清空日志", "反馈故障；清空日志不恢复系统修改，分享前检查内容"),
                F("SettingsLogsUninstall", "版本/说明/支持/卸载", "版本说明/官网/源码/捐赠；卸载先恢复", "版本核验；说明仅展示一次"),
                F("SoftwareUpdate", "软件内更新/失败手动下载", "验签下载EXE，显示下载/校验/安装进度；下载可取消，更新期间禁止退出、替换不可取消；安全退出后原位替换，新版启动确认后删除旧版并同步启动入口；失败同一弹窗转手动更新、解除关闭限制，提供夸克官网入口/GitHub", "退局/维护结束后联网管理员操作；保留个人数据/恢复资产；替换失败先回滚，不强杀活跃进程；备用下载按页说明更新"),
                F("SettingsAiTuning", "AI调优提示词", "只读汇总硬件、全部功能和设置，可补充目标、预览/复制", "交给用户选择的AI获取建议；不自动联网或应用")
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
                F("AuditNetworkThrottle", "体检·网络限流值修复", "把异常NetworkThrottlingIndex修复到10，可还原", "只修异常值，不是后台限速或通用网络加速"),
                F("AuditLinkMetric", "体检·有线优先修复", "有可靠有线网关却默认走Wi-Fi时调整有线metric；撤销回自动metric", "路由选择异常且无歧义；不承诺保留任意原手动metric"),
                F("AuditFth", "体检·容错堆FTH修复", "对命中程序登记FTH排除、移除已挂载兼容补丁引用并保留文件，可恢复", "FTH补丁确实介入；可能影响容错，不是全局关闭所有兼容机制"),
                F("AuditQuantum", "体检·前台时间片检查/入口", "显示当前时间片线索并跳转「前台时间片」选择器", "与目录中的时间片是同一功能，不额外提供独立开关"),
                F("AuditFixAll", "体检·一键修复", "依次执行当前符合条件的体检修复，保留MSI/FTH等确认流程", "已有诊断结果且用户明确操作；不是开启全部优化"),
                F("AuditPlatform", "体检·平台能力检测", "检查显卡清单、NVIDIA接口、CPU Sets分区、EcoQoS、Windows版本、3D V-Cache驱动与Windows游戏模式", "判断本机能力；检测结果不等于这些能力均可由Pavise开关"),
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
                    if (key == "PowerPlanOn") return PowerConfiguration();
                    if (PerformancePolicy.IsGamePriorityOption(key)) return "随总开关" + OnOff(PolicyResolver.RawGlobalValue("GmBoost") == "1");
                    if (key == "GameFamilyBackground" || key == "GmAutoCoresV2" || key == "GameNvRebarV1" || key == "GameDisableFullscreenOptimizationsV1") return "逐游戏见下文，默认关";
                    if (item.Kind == PolicyValueKind.Bool) return OnOff(value == "1");
                    return value == "on" ? "开启" : value == "off" ? "关闭" : "保存值未知";
                }
                switch (key)
                {
                    case "ConfigurationPresets": {
                        ConfigurationPresetKind? preset = ConfigurationPresetRuntime.CurrentFromSettings();
                        return preset.HasValue ? (preset.Value == ConfigurationPresetKind.Extreme ? "极限配置" : "作者同款") + "（保存配置匹配，实际生效未核验）" : "自定义/未应用预设";
                    }
                    case "ModeSmart": case "ModeCompetitive": return "全局模式=" + (PolicyResolver.RawGlobalValue("PerformancePreset") == "1" ? "竞技" : "智能");
                    case "GameModeOn": return BoolConfiguration(key, true);
                    case "LogWritesEnabled": return BoolConfiguration(key, true);
                    case "OverviewTelemetry": return "运行时只读显示";
                    case "SettingsAppearance": return "智能动效=" + CoreMotionCatalog.Name(CoreMotionPreferences.Get(PerformancePreset.Standard)) + "；电竞动效=" + CoreMotionCatalog.Name(CoreMotionPreferences.Get(PerformancePreset.Competitive));
                    case "GameForceTrigger": return "逐游戏设置见下文";
                    case "ManagedPower2223": case "ManagedPowerHandheld": return PowerConfiguration();
                    case "AntiCheatMaster": return BoolConfiguration("TameOn", false);
                    case "AcModeV1": { string modeToken = Settings.LoadStr("AcModeV1", "isolated"); return modeToken == "gentle" ? "温和" : modeToken == "balanced" ? "均衡" : "强力"; }
                    case "AntiCheatWeakCoreAffinity": return "随专项，无独立开关";
                    case "AntiCheatCompatibility": return AntiCheatConfiguration();
                    case "GmMmcss": return BoolConfiguration(key, true);
                    case "AutoLaptopPerformanceV2": return BoolConfiguration(key, false) + "；接口="
                        + (mode == null || !mode.LaptopPerformanceSupportKnown ? "未核验" : mode.LaptopPerformanceSupported ? "已确认支持" : "未确认兼容接口");
                    case "GpuPrefStageOn": return BoolConfiguration(key, true);
                    case "AppGpuAutoV1": return BoolConfiguration(key, false);
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
                case PerformanceActionCatalog.CpuMitigations: effect = "关闭Spectre V2/Meltdown缓解"; break;
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
                    CriticalThreadRule thread = CriticalThreadRule.Parse(threadRaw);
                    text.AppendLine("  [GameCriticalThreadHardRuleV1] " + (thread == null ? "未保存有效渲染线程绑定规则" : (thread.Automatic ? "已保存自动选核规则" : "已保存指定逻辑处理器候选规则") + "；其他工作线程避让=" + OnOff(thread.WorkerAvoidance)) + "；是否实际已绑定未核验。");
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
