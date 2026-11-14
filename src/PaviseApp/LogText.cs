using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace PaviseApp
{
    internal static class LogText
    {
        private sealed class Fragment
        {
            internal readonly string Legacy;
            internal readonly string Chinese;
            internal Fragment(string legacy, string chinese) { Legacy = legacy; Chinese = chinese; }
        }

        // 对照旧记录中的固定片段，保留会话解析所需的原始事件含义。
        private static readonly Fragment[] Fragments = new Fragment[]
        {
            new Fragment("  [UI] ", "  [界面] "),
            new Fragment(" Win32=", " 系统错误码="),
            new Fragment(" action=", " 动作="),
            new Fragment(" activationMs=", " 激活耗时毫秒="),
            new Fragment(" active=", " 活动="),
            new Fragment(" actual failure=", " 实际失败="),
            new Fragment(" adaptation=", " 适配="),
            new Fragment(" attempt=", " 尝试="),
            new Fragment(" birth=", " 创建时间="),
            new Fragment(" candidates-only core-clearing=off", " 仅候选线程；清核=关闭"),
            new Fragment(" capacity=", " 容量="),
            new Fragment(" captureFailures=", " 采样失败次数="),
            new Fragment(" data=", " 数据="),
            new Fragment(" detail=", " 详情="),
            new Fragment(" failed: ", " 失败： "),
            new Fragment(" failed=", " 失败项="),
            new Fragment(" failure=", " 失败详情="),
            new Fragment(" gpu3d=", " GPU三维占用="),
            new Fragment(" gpuRequired=yes", " 需GPU证据=是"),
            new Fragment(" group=", " 处理器组="),
            new Fragment(" hard ", " 硬绑定 "),
            new Fragment(" hard=", " 硬绑定="),
            new Fragment(" inactive=", " 非活动="),
            new Fragment(" mask=0x", " 掩码=0x"),
            new Fragment(" method=duplicate-template copySource=", " 方法=复制模板；复制来源="),
            new Fragment(" method=duplicate/native-write copySource=", " 方法=复制后原生写入；复制来源="),
            new Fragment(" mode=", " 模式="),
            new Fragment(" name=", " 名称="),
            new Fragment(" observation=", " 观测="),
            new Fragment(" old per-game broader-scope / Smart adaptive escalation override(s); the scene rules now decide", " 个旧版逐游戏扩大范围或智能动态扩围配置；现由场景规则决定"),
            new Fragment(" phase=", " 阶段="),
            new Fragment(" physical=0x", " 物理核掩码=0x"),
            new Fragment(" pid ", " 进程编号 "),
            new Fragment(" pid=", " 进程编号="),
            new Fragment(" present=", " 画面提交="),
            new Fragment(" preservedAC=", " 保留交流设置数="),
            new Fragment(" probe failure=", " 探测失败="),
            new Fragment(" profile=", " 配置="),
            new Fragment(" protectedRoots=", " 受保护根进程="),
            new Fragment(" rc ", " 返回码 "),
            new Fragment(" reason=", " 原因="),
            new Fragment(" rebuild=", " 重建="),
            new Fragment(" rebuilds=", " 重建次数="),
            new Fragment(" recovery=", " 恢复="),
            new Fragment(" remaining=", " 剩余="),
            new Fragment(" request=", " 请求="),
            new Fragment(" result=", " 结果="),
            new Fragment(" retryMs=", " 重试间隔毫秒="),
            new Fragment(" sampleCpuMs=", " 采样CPU毫秒="),
            new Fragment(" scope=snapshot; new threads await admission; interrupts are not isolated", " 范围=本次快照；新线程等待准入；不隔离系统中断"),
            new Fragment(" selection=", " 选择="),
            new Fragment(" session=", " 会话="),
            new Fragment(" settings=", " 设置="),
            new Fragment(" sha256=", " SHA256="),
            new Fragment(" skipped=", " 已跳过="),
            new Fragment(" soft=", " 软提示="),
            new Fragment(" source=", " 来源="),
            new Fragment(" stage=", " 步骤="),
            new Fragment(" success=", " 成功="),
            new Fragment(" target=", " 目标="),
            new Fragment(" this session: ", " 本会话： "),
            new Fragment(" tid=", " 线程编号="),
            new Fragment(" times; power-plan switching was set to \"Don't switch\" and the original plan will be restored. It can be re-enabled in the power-plan options", " 次；电源计划已设为“不切换”，随后恢复原计划。可在电源选项中重新开启"),
            new Fragment(" type=", " 类型="),
            new Fragment(" uncertain=", " 待确认="),
            new Fragment(" unknown=", " 未知="),
            new Fragment(" unregistered setting skipped: ", " 已跳过未注册设置： "),
            new Fragment(" unregistered=", " 未注册="),
            new Fragment(" verdict=", " 结论="),
            new Fragment(" verified=", " 已核验="),
            new Fragment(" worker=", " 工作线程="),
            new Fragment(" workerMask=0x", " 工作核掩码=0x"),
            new Fragment(" workers-avoid", " 工作线程避让"),
            new Fragment(" workers=", " 工作线程数="),
            new Fragment(" | soft ", " | 软提示 "),
            new Fragment("(pid ", "（进程编号 "),
            new Fragment("); written back to 0x", "）；已写回 0x"),
            new Fragment(", 0x", ", 0x"),
            new Fragment("/3 pid=", "/3 进程编号="),
            new Fragment("/3 reason=", "/3 原因="),
            new Fragment("/3 stage=", "/3 步骤="),
            new Fragment("/3 target=", "/3 目标="),
            new Fragment("/3; failed capture shared by observation and sweep", "/3；观察与扫描共用失败采样预算"),
            new Fragment("/3; journal retained", "/3；保留恢复日志"),
            new Fragment("/3; original authority retained", "/3；保留原恢复权限"),
            new Fragment("/3; original files retained", "/3；保留原始文件"),
            new Fragment("/3; original ownership retained", "/3；保留原所有权"),
            new Fragment("/3; originals and ownership retained", "/3；保留原值和所有权"),
            new Fragment("/3; owned mark retained", "/3；保留持有标记"),
            new Fragment("/3; owned receipt and handles retained", "/3；保留回执和句柄"),
            new Fragment("/3; ownership retained", "/3；保留所有权"),
            new Fragment("/3; read failed", "/3；读取失败"),
            new Fragment("/3; recovery retained", "/3；保留恢复状态"),
            new Fragment("/3; refresh failed", "/3；刷新失败"),
            new Fragment("/3; retained until parent exit", "/3；保留到父进程退出"),
            new Fragment("/8; journal debt retained", "/8；保留日志债务"),
            new Fragment("/8; ownership retained", "/8；保留所有权"),
            new Fragment(": apply failed; this phase is stopped and restoration is being verified.", "：应用失败，本阶段已停止，正在核验恢复状态。"),
            new Fragment(": target=", "：目标="),
            new Fragment("; active attempts stopped; original session/handles/journal retained", "；已停止主动尝试，保留原会话、句柄和日志"),
            new Fragment("; active attempts stopped; ownership retained", "；已停止主动尝试，保留所有权"),
            new Fragment("; active attempts stopped; receipt and handles retained", "；已停止主动尝试，保留回执和句柄"),
            new Fragment("; active attempts stopped; receipt retained", "；已停止主动尝试，保留回执"),
            new Fragment("; active retries stopped; receipt and handles retained", "；已停止主动重试，保留回执和句柄"),
            new Fragment("; active retries stopped; receipt retained", "；已停止主动重试，保留回执"),
            new Fragment("; apply exhausted", "；应用预算已耗尽"),
            new Fragment("; attempt=", "；尝试="),
            new Fragment("; attempts exhausted for this channel", "；该通道尝试次数已耗尽"),
            new Fragment("; automatic apply stopped for this game/signature", "；已停止该游戏及配置签名的自动应用"),
            new Fragment("; automatic save stopped for this process identity", "；已停止该进程身份的自动保存"),
            new Fragment("; backing off", "；等待退避"),
            new Fragment("; basic display retained. ", "；保留基本显示信息。 "),
            new Fragment("; exhausted for this process identity", "；该进程身份的预算已耗尽"),
            new Fragment("; exhausted; recovery ownership retained", "；预算已耗尽，保留恢复所有权"),
            new Fragment("; failed work stopped for this session", "；本会话已停止失败操作"),
            new Fragment("; lease retained", "；保留租约"),
            new Fragment("; mode=", "；模式="),
            new Fragment("; original ownership retained; active attempts stopped", "；保留原所有权，停止主动尝试"),
            new Fragment("; phase=", "；阶段="),
            new Fragment("; progress=", "；进度="),
            new Fragment("; rebuilding local drawing resources. ", "；正在重建本地绘图资源。 "),
            new Fragment("; receipt retained", "；保留回执"),
            new Fragment("; recovery ownership retained", "；保留恢复所有权"),
            new Fragment("; recovery ownership retained where present", "；已有恢复所有权继续保留"),
            new Fragment("; recovery pending", "；等待恢复"),
            new Fragment("; recovery receipt retained", "；保留恢复回执"),
            new Fragment("; restoration pending", "；恢复尚未完成"),
            new Fragment("; scale=", "；缩放="),
            new Fragment("; size=", "；尺寸="),
            new Fragment("; source unknown; native queries stopped for this phase", "；来源未知，本阶段停止原生查询"),
            new Fragment("; state=", "；状态="),
            new Fragment("; using process-affinity maintenance", "；使用进程亲和性维护"),
            new Fragment("AC AFFINITY ", "反作弊亲和性 "),
            new Fragment("AC AFFINITY recovery pending", "反作弊亲和性等待恢复"),
            new Fragment("AC group-control migration unresolved; governance remains paused", "反作弊分组控制迁移未完成，后台治理继续暂停"),
            new Fragment("AMD ", "AMD "),
            new Fragment("AUTO [", "自动 ["),
            new Fragment("AUTOADD exe=", "自动入库 路径="),
            new Fragment("Animation=", "动效="),
            new Fragment("Another program changed the game's affinity (read back 0x", "其他程序修改了游戏亲和性（读回 0x"),
            new Fragment("Anti-cheat affinity preparation: target=", "反作弊亲和性准备：目标="),
            new Fragment("Anti-cheat affinity recovery: target=", "反作弊亲和性恢复：目标="),
            new Fragment("Audit scan drawing failed; stage=", "体检扫描绘制失败，步骤="),
            new Fragment("Auto-add ", "自动入库 "),
            new Fragment("Auto-add durable save failure=", "自动入库持久保存失败="),
            new Fragment("AutoCore placement exception pid=", "自动选核放置异常，进程编号="),
            new Fragment("Automatic recovery: action=", "自动恢复：动作="),
            new Fragment("Automatic work: action=", "自动操作：动作="),
            new Fragment("BACKGROUND AFFINITY ", "后台亲和性 "),
            new Fragment("Broader background suppression and Smart adaptive escalation are now scene rules; the old separate choices no longer apply: Competitive always uses the wide scope while background control is active, Smart uses the narrow scope and widens it temporarily under sustained CPU saturation", "后台扩大范围及智能动态扩围已并入场景规则，旧独立选项不再使用：竞技在后台治理开启时保持宽范围，智能平时使用窄范围，并在持续高负载时临时扩大"),
            new Fragment("CORE isolation recovery paused after 3 failures; journal and owner retained", "核心隔离恢复连续失败三次后暂停，保留日志和所有者"),
            new Fragment("Capture state is unreadable; system scheduling retained", "采样状态不可读，保留系统调度"),
            new Fragment("Configuration preset identity: session=", "配置预设身份：会话="),
            new Fragment("Configuration preset phase snapshot: session=", "配置预设阶段快照：会话="),
            new Fragment("Coordination active: ", "线程协同已生效： "),
            new Fragment("Critical guardian terminal journal read: attempt=", "关键线程守护终结日志读取：尝试="),
            new Fragment("Critical historical recovery: scope=", "关键线程历史恢复：范围="),
            new Fragment("Critical thread guardian recovery: attempt=", "关键线程守护恢复：尝试="),
            new Fragment("Critical thread guardian recovery: exhausted; authority retained without active retries", "关键线程守护恢复预算已耗尽，保留权限并停止主动重试"),
            new Fragment("Critical thread placed on the manually chosen CPU: ", "关键线程已放置到手动选择的处理器： "),
            new Fragment("Critical thread recovery failure=", "关键线程恢复失败="),
            new Fragment("Critical threads placed on automatically chosen cores: ", "关键线程已放置到自动选择的核心： "),
            new Fragment("Data reset deferred during self-update; user data retained", "自更新期间延后数据重置，保留用户数据"),
            new Fragment("Delivery activity queue failed phase=", "传递优化活动入队失败，阶段="),
            new Fragment("Delivery activity read failed phase=", "传递优化活动读取失败，阶段="),
            new Fragment("EnvCpuMitigationsV1 | ", "EnvCpuMitigationsV1 | "),
            new Fragment("Environment completion | ", "系统环境完成 | "),
            new Fragment("Environment configuration queue | ", "系统环境配置队列 | "),
            new Fragment("Environment graphics ", "系统环境图形 "),
            new Fragment("Environment observation failure=", "系统环境观测失败="),
            new Fragment("Environment observation queue exception=", "系统环境观测入队异常="),
            new Fragment("Environment observation queue failure=", "系统环境观测入队失败="),
            new Fragment("Environment preference restore | ", "系统环境偏好恢复 | "),
            new Fragment("Environment preference save | ", "系统环境偏好保存 | "),
            new Fragment("FOCUS [", "前台 ["),
            new Fragment("FRAME ", "线程协同 "),
            new Fragment("FRAME recovery paused after 3 failures: ", "线程协同恢复连续失败三次后暂停： "),
            new Fragment("Failure isolation retained during panic recovery: ", "紧急恢复期间保留失败隔离： "),
            new Fragment("Further capture stopped for this phase", "本阶段停止继续采样"),
            new Fragment("GAME placement verified pid=", "游戏落核已核验，进程编号="),
            new Fragment("GPU inventory: current PCI adapter mapping is incomplete; target selection unavailable.", "GPU 清单：当前 PCI 适配器映射不完整，暂不能选择控制目标。"),
            new Fragment("GPU power limit: original value restored and verified", "GPU 功耗上限：原值已恢复并核验"),
            new Fragment("Game affinity Job protection unavailable pid=", "游戏亲和性 Job 保护不可用，进程编号="),
            new Fragment("Game affinity Job recovery pending: ", "游戏亲和性 Job 等待恢复： "),
            new Fragment("Game affinity guardian recovery exhausted: ownership retained, no further active attempts", "游戏亲和性守护恢复预算已耗尽，保留所有权，不再主动尝试"),
            new Fragment("Game affinity maintenance ", "游戏亲和性维护 "),
            new Fragment("Game affinity orphan ordering: receipt=", "游戏亲和性遗留顺序检查：回执="),
            new Fragment("Game affinity orphan owner identity: receipt=", "游戏亲和性遗留所有者身份：回执="),
            new Fragment("Game affinity orphan owner ordering: receipt=", "游戏亲和性遗留所有者顺序：回执="),
            new Fragment("Game affinity orphan receipt read failure=", "游戏亲和性遗留回执读取失败="),
            new Fragment("Game affinity orphan recovery: receipt=", "游戏亲和性遗留恢复：回执="),
            new Fragment("Game affinity recovery ordering: target=", "游戏亲和性恢复顺序：目标="),
            new Fragment("Game affinity recovery: target=", "游戏亲和性恢复：目标="),
            new Fragment("Game library: ignored ", "游戏库：已忽略 "),
            new Fragment("Game recovery: target=", "游戏恢复：目标="),
            new Fragment("Guardian recovery ordering exhausted: observations=", "守护恢复顺序预算已耗尽，观测次数="),
            new Fragment("Guardian recovery: phase=", "守护恢复：阶段="),
            new Fragment("Hard affinity recovery: target=", "硬亲和性恢复：目标="),
            new Fragment("Hard thread affinity: ", "线程硬亲和性： "),
            new Fragment("Historical thread recovery is still unconfirmed: ", "历史线程恢复仍未确认： "),
            new Fragment("IRQ ", "中断 "),
            new Fragment("Laboratory ", "实验室 "),
            new Fragment("Laboratory selection failed; session=", "实验室选择失败，会话="),
            new Fragment("LaboratoryAudio release | ", "低缓冲音频释放 | "),
            new Fragment("LaboratoryAudio | ", "低缓冲音频 | "),
            new Fragment("Laptop profile availability: ", "笔记本性能档可用性： "),
            new Fragment("Laptop vendor performance mode applied; the original is restored on session exit", "厂商性能模式已应用，退局后恢复原档位"),
            new Fragment("Legacy NVIDIA recovery paused after 3 failures; journals and ownership retained", "旧 NVIDIA 配置恢复连续失败三次后暂停，保留日志和所有权"),
            new Fragment("Legacy NVIDIA recovery pending: ", "旧 NVIDIA 配置等待恢复： "),
            new Fragment("Legacy NVIDIA recovery queue failed: ", "旧 NVIDIA 配置恢复入队失败： "),
            new Fragment("Legacy RTSS cleanup did not complete; ownership was retained for retry.", "旧 RTSS 清理未完成，保留所有权供有限恢复使用。"),
            new Fragment("Legacy component cleanup retained its receipt: ", "旧组件清理保留回执： "),
            new Fragment("Legacy component cleanup: ", "旧组件清理： "),
            new Fragment("Legacy core placement recovery failure=", "旧核心放置恢复失败="),
            new Fragment("Library running observation failure=", "游戏库运行状态观测失败="),
            new Fragment("Library running observation queue failure=", "游戏库运行状态观测入队失败="),
            new Fragment("Library/auto-ignore recovery pending; further library writes paused", "游戏库或自动忽略等待恢复，暂停继续写入游戏库"),
            new Fragment("Library/auto-ignore transaction could not commit", "游戏库或自动忽略事务无法提交"),
            new Fragment("LoL UI recovery failure=", "LOL 界面恢复失败="),
            new Fragment("LoL addon notice observation failure=", "LOL 附加内容提示观测失败="),
            new Fragment("LoL cleanup failure=", "LOL 清理失败="),
            new Fragment("LoL headless apply failure=", "LOL 无头客户端应用失败="),
            new Fragment("LoL recovery mark failure=", "LOL 恢复标记保存失败="),
            new Fragment("Managed power performance settings changed; rebuilding and verifying", "托管电源性能参数已变化，正在重建并核验"),
            new Fragment("Managed power settings were repeatedly changed by another program (often an OEM control center) more than ", "托管电源参数被其他程序（通常是 OEM 控制中心）反复修改，次数超过 "),
            new Fragment("Managed power settings were repeatedly changed by another program, but saving \"Don't switch\" failed; keeping the current plan without further rebuilds this session", "托管电源参数反复被外部修改，且“不切换”保存失败；保留当前计划，本会话不再重建"),
            new Fragment("Manual laboratory recovery remains pending", "手动实验室恢复仍未完成"),
            new Fragment("NVIDIA game apply paused after 3 failures: ", "NVIDIA 游戏配置应用连续失败三次后暂停： "),
            new Fragment("NVIDIA game profile apply failed: ", "NVIDIA 游戏配置应用失败： "),
            new Fragment("NVIDIA game profile preparation failed: ", "NVIDIA 游戏配置准备失败： "),
            new Fragment("NVIDIA game profile queue failed: ", "NVIDIA 游戏配置入队失败： "),
            new Fragment("NVIDIA pre-stage failed: ", "NVIDIA 预处理失败： "),
            new Fragment("NVIDIA pre-stage paused after 3 failures: ", "NVIDIA 预处理连续失败三次后暂停： "),
            new Fragment("NVIDIA pre-stage queue failed: ", "NVIDIA 预处理入队失败： "),
            new Fragment("NVIDIA recovery paused after 3 failures: ", "NVIDIA 恢复连续失败三次后暂停： "),
            new Fragment("Online quantum update deferred to restart | ", "在线时间片更新延后至重启 | "),
            new Fragment("Overview GPU fallback read failed phase=", "概览 GPU 备用读取失败，阶段="),
            new Fragment("Overview GPU inventory read failed phase=", "概览 GPU 清单读取失败，阶段="),
            new Fragment("PASS ", "成功 "),
            new Fragment("PASS FRAME ", "成功 线程协同 "),
            new Fragment("POWER event=orphan-sweep deleted=", "电源 事件=遗留清理 已删除="),
            new Fragment("POWER event=orphan-sweep failure=", "电源 事件=遗留清理 失败详情="),
            new Fragment("POWER phase=native ", "电源 阶段=原生 "),
            new Fragment("POWER source=", "电源 来源="),
            new Fragment("POWER source=OEM/base reason=", "电源 来源=OEM或基准 原因="),
            new Fragment("Performance automation | ", "性能自动化 | "),
            new Fragment("Performance recovery: action=", "性能恢复：动作="),
            new Fragment("Performance restore | ", "性能还原 | "),
            new Fragment("Power plan: signature=", "电源计划：签名="),
            new Fragment("Privacy admission | ", "隐私项准入 | "),
            new Fragment("Privacy completion | ", "隐私项完成 | "),
            new Fragment("Privacy inspection completion | ", "隐私项检查完成 | "),
            new Fragment("Privacy queue | ", "隐私项队列 | "),
            new Fragment("Privacy restore | ", "隐私项恢复 | "),
            new Fragment("Privacy | ", "隐私项 | "),
            new Fragment("Process path read failed pid=", "进程路径读取失败，进程编号="),
            new Fragment("Process snapshot source failed phase=", "进程快照来源失败，阶段="),
            new Fragment("Profile commit eligibility check failed: ", "配置提交资格检查失败： "),
            new Fragment("RENDERER probe ", "渲染进程探测 "),
            new Fragment("Release notes presentation failed: ", "更新说明展示失败： "),
            new Fragment("Release notes shown; version acknowledgement could not be saved", "更新说明已展示，但版本确认状态未能保存"),
            new Fragment("Removed unused autostart copies: ", "已删除不再使用的自启副本： "),
            new Fragment("Render lane recovery paused after 3 failures; original thread ownership retained", "渲染通道恢复连续失败三次后暂停，保留原线程所有权"),
            new Fragment("Render thread identification waiting: ", "渲染线程识别等待： "),
            new Fragment("Render thread identified (observation only, no priority change): ", "渲染线程已识别（仅观测，未改优先级）： "),
            new Fragment("Renderer ", "渲染进程 "),
            new Fragment("Renderer GPU probe failure=", "渲染进程 GPU 探测失败="),
            new Fragment("Repeated captures were unsuccessful; observation stopped for this process", "多次采样均未成功，已停止观测该进程"),
            new Fragment("Retired environment recovery | ", "退役系统环境恢复 | "),
            new Fragment("SESSION [", "会话 ["),
            new Fragment("STARTUP ", "启动 "),
            new Fragment("STARTUP event=job-handoff ", "启动 事件=任务交接 "),
            new Fragment("STARTUP event=retired-cpu-cap-receipts result=removed", "启动 事件=旧CPU限制回执 结果=已移除"),
            new Fragment("STARTUP event=retired-cpu-cap-receipts result=retained", "启动 事件=旧CPU限制回执 结果=已保留"),
            new Fragment("SUPPRESSION success continuation part=", "后台压制成功，续批编号="),
            new Fragment("Scheduling capture limited; switching to presenter-only capture", "调度采样受限，改用仅画面提交者采样"),
            new Fragment("Security startup inspection exhausted attempt=1/1; no runtime retry: ", "启动安全检查次数已耗尽，尝试=1/1；运行期间不重试： "),
            new Fragment("Security startup inspection sealed products=", "启动安全清单已封存，产品数="),
            new Fragment("Self-update stopped: ", "自更新已停止： "),
            new Fragment("SelfYield apply failed attempt=", "自身让路应用失败，尝试="),
            new Fragment("SelfYield helper restore failed attempt=", "自身让路辅助恢复失败，尝试="),
            new Fragment("SelfYield restore failed attempt=", "自身让路恢复失败，尝试="),
            new Fragment("Session recovery route ", "会话恢复路径 "),
            new Fragment("Suppression ", "后台压制 "),
            new Fragment("Suppression background journal: phase=", "后台压制日志：阶段="),
            new Fragment("THREAD ", "线程 "),
            new Fragment("THREAD [", "线程 ["),
            new Fragment("THREAD exclusive verified: ", "线程整核隔离已核验： "),
            new Fragment("THREAD soft placement audit: ", "线程软放置核验： "),
            new Fragment("THREAD soft placement released: ", "线程软放置已释放： "),
            new Fragment("TID ", "线程编号 "),
            new Fragment("Tamer snapshot: attempt=", "后台治理快照：尝试="),
            new Fragment("Tamer sweep recovery: phase=", "后台治理扫描恢复：阶段="),
            new Fragment("Thread or Job affinity recovery is unresolved; process-affinity recovery records were retained", "线程或 Job 亲和性恢复未完成，已保留进程亲和性恢复记录"),
            new Fragment("Thread recovery could not be queued: ", "线程恢复无法入队： "),
            new Fragment("Thread state changed; owned settings restored and repeated boosting stopped for this process", "线程状态已被修改，本方设置已恢复，并停止对该进程反复提优"),
            new Fragment("Time-slice correction is retired; nothing was written. Choose a Foreground quantum profile instead.", "时间片校正已退役，本次未写入。请改用前台时间片配置。"),
            new Fragment("Time-slice recovery | ", "时间片恢复 | "),
            new Fragment("Update abort retained recovery state: ", "更新中止后保留恢复状态： "),
            new Fragment("Update launch identity unavailable: ", "更新启动身份不可用： "),
            new Fragment("Update startup acknowledgement failed: ", "更新启动确认失败： "),
            new Fragment("Updated startup could not be committed; recovery files retained", "新版启动无法提交，保留恢复文件"),
            new Fragment("VBS | ", "VBS | "),
            new Fragment("Windows restart request", "Windows 重启请求"),
            new Fragment("Workspace brand drawing failed; stage=", "工作区标志绘制失败，步骤="),
            new Fragment("] [PLATFORM] restore-pending ", "] [平台] 等待恢复 "),
            new Fragment("] end | ", "] 结束 | "),
            new Fragment("] event=switched-back scene=", "] 事件=切回 场景="),
            new Fragment("] event=switched-out scene=competitive backgroundPolicy=retain powerPolicy=retain affinityPolicy=retain", "] 事件=切出 场景=竞技 后台策略=保留 电源策略=保留 亲和性策略=保留"),
            new Fragment("] event=switched-out scene=smart backgroundPolicy=release powerPolicy=retain affinityPolicy=retain", "] 事件=切出 场景=智能 后台策略=释放 电源策略=保留 亲和性策略=保留"),
            new Fragment("] result=", "] 结果="),
            new Fragment("active retries stopped; process events remain active", "已停止主动重试，进程事件继续运行"),
            new Fragment("active retries stopped; receipt and handle retained", "已停止主动重试，保留回执和句柄"),
            new Fragment("local-DC/daily", "本机电池与日常设置"),
            new Fragment("local-DC/daily/cache-topology", "本机电池、日常设置与缓存拓扑"),
            new Fragment("restoration pending", "等待还原"),
            new Fragment("INFO ", "信息 "),
            new Fragment("WARN ", "警告 "),
            new Fragment("FAIL ", "失败 "),
            new Fragment("RUNTIME event=identity version=", "运行时 身份 版本="),
            new Fragment(" mvid=", " 构建标识="),
            new Fragment(" os=", " 系统版本="),
            new Fragment(" arch=", " 架构="),
            new Fragment(" repeated=", " 重复次数="),
            new Fragment(" | recovered", " | 已恢复"),
            new Fragment("SUPPRESSION success", "后台压制成功"),
            new Fragment(" targets=", " 目标列表="),
            new Fragment(" event=", " 事件="),
            new Fragment(" version=", " 版本="),
            new Fragment(" restoredProcessCount=", " 已恢复进程数="),
            new Fragment(" level=", " 档位="),
            new Fragment(" count=", " 数量="),
            new Fragment(" expected=", " 预期="),
            new Fragment(" elapsedMs=", " 耗时毫秒="),
            new Fragment(" error=", " 错误码="),
            new Fragment(" lost=", " 丢失数="),
            new Fragment(" tracing=", " 跟踪="),
            new Fragment("SESSION event=end", "会话 事件=结束"),
            new Fragment("POWER event=activation-verified", "电源 事件=激活已核验"),
        };

        private static readonly Dictionary<string, string> StateNames = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "start", "开始" }, { "end", "结束" }, { "started", "已开始" }, { "applied", "已生效" },
            { "restoring", "恢复中" }, { "restored", "已恢复" }, { "restore-failed", "恢复未完成" },
            { "restore-pending-reboot", "恢复待重启" }, { "ready", "已就绪" }, { "pending", "待确认" },
            { "failed", "执行失败" }, { "stopped", "已停止" }, { "unverified", "未核验" },
            { "timer-unverified", "计时器未核验" }, { "on", "开启" }, { "off", "关闭" },
            { "configured", "已配置" }, { "fso-configured", "全屏优化已配置" }, { "pending-reboot", "等待重启" },
            { "timer-requested", "计时器已请求" }, { "timer-locked", "计时器已锁定" }, { "timer-limited", "计时器受限" },
            { "unsupported", "不受支持" }, { "unchanged", "无需修改" }, { "canceled", "已取消" },
            { "skipped", "已跳过" }, { "waiting", "等待中" }, { "exhausted", "预算已耗尽" },
            { "background-applied", "后台治理已生效" }, { "background-level-changed", "后台档位已调整" },
            { "background-affinity-verified", "后台核心范围已核验" }, { "reconcile-verified", "后台状态复核完成" },
            { "reconcile-recovered", "复核后已恢复" }
        };

        internal static string State(string code)
        { string name; return StateNames.TryGetValue(code ?? "", out name) ? name : code ?? ""; }

        private static readonly Dictionary<char, List<Fragment>> LegacyIndex = Index(true);
        private static readonly Dictionary<char, List<Fragment>> ChineseIndex = Index(false);

        private static Dictionary<char, List<Fragment>> Index(bool legacy)
        {
            var index = new Dictionary<char, List<Fragment>>();
            foreach (Fragment fragment in Fragments)
            {
                string text = legacy ? fragment.Legacy : fragment.Chinese;
                List<Fragment> values;
                if (!index.TryGetValue(text[0], out values)) index[text[0]] = values = new List<Fragment>();
                values.Add(fragment);
            }
            return index;
        }

        private static string StateCode(string name)
        { foreach (var pair in StateNames) if (pair.Value == name) return pair.Key; return name; }

        // 名称、路径及进程名单是原始数据，翻译标签时不能改写这些内容。
        private static string Translate(string value, bool toChinese)
        {
            if (string.IsNullOrEmpty(value)) return value ?? "";
            var protectedValues = new List<string>();
            string marker = "\uE000" + Guid.NewGuid().ToString("N") + "_";
            MatchEvaluator protect = match => { string token = marker + protectedValues.Count + "\uE001"; protectedValues.Add(match.Value); return token; };
            value = Regex.Replace(value, @"https?://[^\s]+|[A-Za-z]:[\\/][^\r\n|]*", protect);
            value = Regex.Replace(value, @"(?<prefix>(?: targets=| 目标列表=| name=| 名称=))(?<data>.*)$", match =>
            { string data = match.Groups["data"].Value; string token = marker + protectedValues.Count + "\uE001"; protectedValues.Add(data); return match.Groups["prefix"].Value + token; });
            value = Regex.Replace(value, @"^(?<prefix>(?:(?:INFO|WARN|FAIL|PASS|信息|警告|失败|成功)\s+)?(?:SESSION|会话) \[[^\]]+\]\s*[^|]+\|\s*)(?<name>.*)$", match =>
            { string token = marker + protectedValues.Count + "\uE001"; protectedValues.Add(match.Groups["name"].Value); return match.Groups["prefix"].Value + token; });
            value = Regex.Replace(value, @"(?<prefix>THREAD exclusive verified: |线程整核隔离已核验：\s*)(?<name>.*?)(?= pid=| 进程编号=)", match =>
            { string token = marker + protectedValues.Count + "\uE001"; protectedValues.Add(match.Groups["name"].Value); return match.Groups["prefix"].Value + token; });
            var output = new StringBuilder(value.Length);
            for (int index = 0; index < value.Length;)
            {
                Fragment selected = null; int length = 0;
                List<Fragment> candidates;
                if (!(toChinese ? LegacyIndex : ChineseIndex).TryGetValue(value[index], out candidates))
                { output.Append(value[index++]); continue; }
                foreach (Fragment fragment in candidates)
                {
                    string from = toChinese ? fragment.Legacy : fragment.Chinese;
                    if (from.Length <= length || index + from.Length > value.Length || value[index] != from[0]) continue;
                    if (string.CompareOrdinal(value, index, from, 0, from.Length) == 0) { selected = fragment; length = from.Length; }
                }
                if (selected == null) { output.Append(value[index++]); continue; }
                output.Append(toChinese ? selected.Chinese : selected.Legacy); index += length;
            }
            value = output.ToString();
            for (int index = protectedValues.Count - 1; index >= 0; index--) value = value.Replace(marker + index + "\uE001", protectedValues[index]);
            return value;
        }

        internal static string Chinese(string value)
        { return Translate(value, true); }

        internal static string Canonical(string value)
        {
            value = value ?? "";
            value = Regex.Replace(value, @"^(?<prefix>(?:(?:INFO|WARN|FAIL|PASS|信息|警告|失败|成功)\s+)?(?:SESSION|会话) \[[^\]]+\]\s*)(?<state>[^|\s]+)", match => match.Groups["prefix"].Value + StateCode(match.Groups["state"].Value));
            value = Regex.Replace(value, @"(?<prefix>(?:AUTO|自动) \[[^\]]*\]\s*[^|]+\|\s*)(?<state>[^\s|]+)", match => match.Groups["prefix"].Value + StateCode(match.Groups["state"].Value));
            value = FieldStates(value, false);
            value = Translate(value, false);
            return value;
        }

        internal static string Record(string value)
        {
            value = value ?? "";
            foreach (string level in new[] { "INFO ", "WARN ", "FAIL ", "PASS " })
                if (value.StartsWith(level, StringComparison.Ordinal)) { value = Chinese(level) + value.Substring(level.Length); break; }
            string body = Regex.Replace(value, @"^(?:信息|警告|失败|成功|INFO|WARN|FAIL|PASS)\s+", "");
            bool legacyRecord = body.StartsWith("SESSION [", StringComparison.Ordinal) || body.StartsWith("AUTO [", StringComparison.Ordinal)
                || body.StartsWith("THREAD ", StringComparison.Ordinal) || body.StartsWith("SUPPRESSION ", StringComparison.Ordinal)
                || body.StartsWith("RUNTIME ", StringComparison.Ordinal) || body.StartsWith("STARTUP ", StringComparison.Ordinal)
                || body.StartsWith("POWER ", StringComparison.Ordinal) || body.StartsWith("GAME placement ", StringComparison.Ordinal);
            if (legacyRecord || !Regex.IsMatch(body, @"[\u4e00-\u9fff]")) value = Chinese(value);
            value = Regex.Replace(value, @"^(?<prefix>(?:(?:信息|警告|失败|成功)\s+)?会话 \[[^\]]+\]\s*)(?<state>[^|\s]+)", match => match.Groups["prefix"].Value + State(match.Groups["state"].Value));
            value = Regex.Replace(value, @"(?<prefix>自动 \[[^\]]*\]\s*[^|]+\|\s*)(?<state>[^\s|]+)", match => match.Groups["prefix"].Value + State(match.Groups["state"].Value));
            value = FieldStates(value, true);
            return value;
        }

        // 只转换结构化状态字段，后面的名称、名单和路径不参与状态翻译。
        private static string FieldStates(string value, bool toChinese)
        {
            if (Regex.IsMatch(value, @"^(?:(?:INFO|WARN|FAIL|PASS|信息|警告|失败|成功)\s+)?(?:SESSION|会话) \[")) return value;
            int end = value.Length;
            foreach (string marker in new[] { " targets=", " 目标列表=", " name=", " 名称=" })
            { int found = value.IndexOf(marker, StringComparison.Ordinal); if (found >= 0) end = Math.Min(end, found); }
            Match path = Regex.Match(value, @"https?://|[A-Za-z]:[\\/]");
            if (path.Success) end = Math.Min(end, path.Index);
            string prefix = Regex.Replace(value.Substring(0, end), @"(?<![A-Za-z0-9_])(?<label>(?:result|state|action|event|结果|状态|动作|事件)=)(?<state>[^\s;]+)",
                match => match.Groups["label"].Value + (toChinese ? State(match.Groups["state"].Value) : StateCode(match.Groups["state"].Value)));
            return prefix + value.Substring(end);
        }

        internal static string Module(string value)
        {
            switch (value)
            {
                case "SYSTEM": return "系统"; case "AUTO": return "自动策略"; case "SESSION": return "会话";
                case "THREAD": return "线程"; case "FRAME": return "线程协同"; case "POWER": return "电源";
                case "SUPPRESSION": return "后台压制"; case "STARTUP": return "启动"; case "RUNTIME": return "运行时";
                default: return Chinese(value);
            }
        }

        internal static string DisplayBody(string value)
        {
            value = value ?? "";
            DateTime timestamp;
            if (value.Length >= 19 && DateTime.TryParseExact(value.Substring(0, 19), "yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out timestamp)) value = value.Substring(19).Trim();
            value = Regex.Replace(value, @"^(?:信息|警告|失败|成功|INFO|WARN|FAIL|PASS)\s+", "");
            return Regex.IsMatch(value, @"[\u4e00-\u9fff]") ? value : Chinese(value);
        }
    }
}
