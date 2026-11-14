using System;

namespace PaviseApp
{
    internal static class ReleaseNotes
    {
        private const string SeenKey = "LastSeenNotesVersion";

        public static readonly ReleaseNote[] All = new ReleaseNote[1]
        {
            new ReleaseNote("3.3.1.0", "2026-11-13", new[] {
                "没想到 Pavise 会有这么多人关注和使用。Pavise 的定位是游戏运行时资源编排器，核心是在对局期间协调游戏与后台的资源使用，不是传统的“一键优化”软件。当初做这个项目，就是不想让大家买了硬件之后，还要为了用好自己的电脑，再花钱买各种性能优化软件。有能力的朋友可以贡献代码、分享经验，其他人也能免费用上这些成果。感谢大家的使用、分享和反馈。我会继续维护好 Pavise，坚持开源与免费，把兼容性、稳定性和游戏体验做好。",
                "I never expected so many players to take an interest in Pavise and use it. Pavise is a game runtime resource orchestrator. Its core job is to coordinate resource use between games and background tasks during play; it isn't a traditional one-click optimizer. I started this project because I didn't want players who had already paid for their hardware to have to buy performance optimization software just to make better use of their own PCs. Those who can contribute code or share their experience can help improve it, and everyone else can use those contributions for free. Thank you for using Pavise, sharing it and sending feedback. I'll keep maintaining Pavise, keep it open source and free, and work on compatibility, stability and the gaming experience."
            }, new string[][] {
                new[] { "线程属性读取失败时退避，可读后继续维护原规则；未接管字段仍限制重试次数。",
                    "Thread-read failures back off; correction resumes when reads recover. Fields not yet acquired keep bounded retries." },
                new[] { "合并候选线程诊断，减少重复日志，保留状态变化、字段证据和恢复结果。",
                    "Group candidate-thread diagnostics to reduce repeated logs while retaining state changes, field evidence and restoration results." },
                new[] { "反作弊无目标时跳过服务枚举；概览 GPU 查询失败后退避，切换页面不重置失败记录。",
                    "Skip service enumeration without anti-cheat targets. Failed overview GPU queries back off and retain failure records across pages." },
                new[] { "保护已核实的 AMD 降噪组件，并恢复此前的后台限制。",
                    "Protect verified AMD Noise Suppression components and restore earlier background limits." },
                new[] { "收紧线程查询权限，及时释放未接管句柄；已接管线程保留句柄直至恢复完成。",
                    "Reduce thread-query access and release unowned handles promptly. Keep acquired handles until restoration completes." },
                new[] { "完善浏览器、聊天及创作软件识别，减少自动入库误判；入库来源仅用于诊断。",
                    "Improve browser, chat and creative-app recognition to reduce false library entries. Entry source is diagnostic only." },
                new[] { "后台选核避开已生效的游戏关键线程核心；余核不足时释放收核限制，保留资源治理。",
                    "Keep background work off verified game critical-thread cores. Release affinity limits when spare cores run out, while retaining resource governance." },
                new[] { "更新 ACE 压制规则，完善 ACE、EAC 与 BattlEye 的持续纠偏和退出恢复。",
                    "Updated ACE suppression rules and improved ongoing correction and restoration for ACE, EAC and BattlEye." },
                new[] { "渲染调度提供六种方案，新增游戏优先、依赖协同与专核强化，完善清核维护。",
                    "Render scheduling now offers six strategies, including Game priority, Dependency coordination and Enhanced dedicated rendering, with improved clear-core maintenance." },
                new[] { "游戏核心范围改为手动保存，全选且不隔离时减少额外约束与核验。",
                    "Game CPU selection now requires Save; selecting all CPUs without isolation avoids extra constraints and audits." },
                new[] { "资源优先级并入调度设置，仅保留 GPU 与磁盘调整，移除游戏内存优先级。",
                    "Resource priority moves into scheduling settings, retaining GPU and disk adjustments and removing game memory priority." },
                new[] { "加强微软后台治理，修正后台压制数量显示，完善恢复状态。",
                    "Improved Microsoft background governance, corrected suppression counts and refined restoration status." },
                new[] { "整合性能与后台设置，统一按普通功能和重启电脑分类，精简功能说明。",
                    "Combined performance and background settings, grouped features by PC restart requirements and simplified descriptions." },
                new[] { "优化游戏识别与电源兼容，完善动作、诊断日志及异常提示。",
                    "Improved game detection, power compatibility, action and diagnostic logs, and status messages." },
                new[] { "进程、线程与显卡权限单独核验，进程访问受限不再阻止线程采样和调度。",
                    "Check process, thread and GPU permissions separately. Process access restrictions no longer block thread sampling or scheduling." },
                new[] { "自动编排按全量线程负载和实际核型容量选候选；High 类别支持线程提优，各项设置单独核验。",
                    "Select orchestration candidates using total thread load and observed capacity by core type. High-priority processes support thread boosts; settings are verified separately." },
                new[] { "保留原始采集错误，避免被清理错误覆盖；清理未完成时保留恢复记录。",
                    "Keep the original capture error when cleanup also fails, and retain recovery records until cleanup completes." },
                new[] { "多核协同在角色变化后继续复核，分别接管已确认的固定核心和提优成员，减少长期等待。",
                    "Recheck changed Multicore roles and acquire confirmed placement and boost roles separately, reducing prolonged observation." },
                new[] { "改善 Job 约束下恢复守护的启动兼容；失败重试有上限，后续新会话可重新接管。",
                    "Improve recovery guardian startup under Job constraints. Retries stay bounded; later sessions can acquire targets again." },
                new[] { "兼容 NTFS 的 QWORD 原值，恢复时保留类型、完整数值及后续外部修改。",
                    "Support original NTFS QWORD values, preserving type, full value and later external changes during restoration." },
                new[] { "改善现代待机设备的恢复守护兼容，核验 WMI 来源和跨会话身份，不再强求与桌面外壳同 Job。",
                    "Improve recovery guardians on Modern Standby devices by verifying WMI origin and cross-session identity without requiring the desktop shell in the same Job." },
                new[] { "修复一键配置误报计时器失败、动作日志截断路径和核心范围；反作弊核验忙时延后，成功纠偏不再报异常。",
                    "Fix false timer failures in presets and truncated paths or CPU ranges in action logs. Defer busy anti-cheat checks and log successful correction as information." },
                new[] { "修复游戏与后台 GPU 优先级恢复，读回失败保留记录，恢复时保留外部后写值。",
                    "Fix game and background GPU priority restoration. Keep records after readback failures and preserve later external writes." },
                new[] { "降低后台核心查询权限，区分退出、身份变化和 API 失败，补充线程恢复、反作弊及 NVIDIA 日志。",
                    "Use less access to query background cores. Distinguish exits, identity changes and API failures; expand thread-restoration, anti-cheat and NVIDIA logs." },
                new[] { "修复 ACE SGuard64 拒绝 Job 后无法挪核，改用直接亲和性，记录原范围并在退局时恢复。",
                    "Fix ACE SGuard64 placement when Job assignment is refused: use direct affinity, record original ranges and restore them at game exit." },
                new[] { "移除游戏高性能显卡预选及对应配置项；旧偏好保留，可在 Windows 图形设置中调整。",
                    "Remove high-performance GPU pre-selection and its settings. Existing preferences remain editable in Windows graphics settings." },
                new[] { "修复部分 DX12 游戏 GPU 占用显示 0%，将 Graphics／Compute 渲染负载纳入游戏识别。",
                    "Fix 0% GPU readings in some DX12 games by including Graphics/Compute rendering load in game detection." },
                new[] { "修复主界面在后台无焦点时持续重绘；已确认的启动器按 30、60、120 秒降低渲染探测频率。",
                    "Stop continuous redraws when the main window lacks focus in the background. Back off renderer probes of confirmed launchers to 30, 60 and 120 seconds." },
                new[] { "新增电源计划守护，对局中持续写回外部改动。旧版被改成「不切换」的设置，需重新选择电源计划。",
                    "Add a power-plan guard that keeps correcting external changes during play. Reselect the plan if an older version changed it to No switching." },
                new[] { "主副核编排未预留核心时，单个提优线程退出不影响其余线程；主副候选退出才撤回计划。",
                    "When orchestration reserves no cores, one boosted thread exiting leaves the others active. Only a main or secondary candidate exiting withdraws the plan." },
                new[] { "调度窗口显示最近对局放置前的排队、占用与换核读数，并写入日志；仅供对照，保留至程序退出。",
                    "Show and log pre-placement queueing, CPU use and migration readings from the latest match. For comparison only; kept until app exit." },
                new[] { "独立恢复守护清理未完成时，日志写明停在哪一步及对应错误码。",
                    "When independent recovery guardian cleanup does not finish, the log now states the step it stopped at and the error code." },
                new[] { "反作弊优先使用游戏范围外的核心，包含暂时停泊的核心；渲染选核不再改动无冲突的反作弊范围。",
                    "Prefer anti-cheat cores outside the game range, including parked cores. Render placement leaves nonconflicting anti-cheat ranges unchanged." },
                new[] { "渲染线程调度逐游戏默认关闭；开启时提示效果需自行对照，可能出现性能下降。",
                    "Render thread scheduling is off by default for each game. Enabling it shows a notice that results need your own comparison and performance may drop." },
                new[] { "新增「游戏优先」方案：提高游戏进程优先级，并固定、提优渲染线程；打开渲染线程调度时默认使用。",
                    "Add the Game priority strategy: raises game process priority, and pins and prioritizes the render thread. It is the default when render thread scheduling is turned on." },
            }),

        };

        public static ReleaseNote Current
        {
            get
            {
                ReleaseNote[] all = All;
                foreach (ReleaseNote releaseNote in all)
                {
                    if (string.Equals(releaseNote.Version, App.Version, StringComparison.OrdinalIgnoreCase))
                    {
                        return releaseNote;
                    }
                }
                return null;
            }
        }

        public static bool HasUnseen
        {
            get
            {
                return !string.Equals(Settings.LoadStr(SeenKey, ""), App.Version, StringComparison.OrdinalIgnoreCase);
            }
        }

        public static bool MarkSeen()
        {
            string saved;
            return Settings.SaveStrDurable(SeenKey, App.Version)
                && Settings.TryLoadStr(SeenKey, out saved)
                && string.Equals(saved, App.Version, StringComparison.OrdinalIgnoreCase);
        }
    }
}
