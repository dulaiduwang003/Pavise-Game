# 反作弊识别与保护范围

核对日期：2026-09-06。

反作弊页包含八组可选用户态压制、一组改判仅保护的用户态分组（Vanguard）和六组仅保护规则，另有全局的压制强度三档。仅保护名单包括 PunkBuster、Nexon Game Security、Wellbia、完美世界竞技平台、5E 和 B5。通用后台压制会跳过命中的反作弊；总开关关闭不取消这层豁免。仅保护规则不进入 `Tamer` 的压制目标，也不参与反作弊绑核。

## 新增的仅保护分组

| 分组 | 识别规则 | 核对依据 |
| --- | --- | --- |
| PunkBuster | `PnkBstrA`、`PnkBstrB`；`PnkBstr` / `PunkBuster` 名称前缀；`PunkBuster` 专用目录 | [Even Balance 服务说明](https://www.evenbalance.com/downloads.php)明确列出两个服务；[EA 战地 4 支持页](https://help.ea.com/ru/help/battlefield/punkbuster-bans-kicks-troubleshooting/)列出进程和驱动文件。 |
| Nexon Game Security | `BlackCipher` / `BlackCall` / `BlackXchg` 名称前缀，包含 64 位和 `.aes` 形式；`BlackCipher` / `Nexon Game Security` 专用目录 | [MapleStory 支持页](https://support-maplestory.nexon.com/hc/en-us/articles/211274023-NGS-Initialization-Error-0xe1600301)明确称 `BlackCipher.aes` 为后台进程；[第一后裔指南](https://tfd.nexon.com/en/guide/3080687)和 [Nexon 技术支持](https://saz.nexon.com/zh-TW/faq/tech)列出 BlackCipher、BlackCall、BlackXchg 组件。 |
| Wellbia XIGNCODE3 / UNCHEATER | `XIGNCODE` / `Uncheater` / `ucldr_` 名称前缀；`XIGNCODE`、`XIGNCODE3`、`UNCHEATER`、`Wellbia`、`Wellbia.com` 专用目录 | [Wellbia 产品说明](https://www.wellbia.com/?action=SiteComp&module=Html&sSubNo=1)确认产品家族；[UNCHEATER FAQ](https://sega-faq.wellbia.com/index_cn.html)区分运行组件和 `xhunter1.sys` 驱动；[文件分析样本](https://gridinsoft.com/online-virus-scanner/id/ca2165641be651ec49ef4966a713b0ce6c84c4bb55c345c616a3fc9e5c9d560c)记录 `ucldr_ChaosZeroNightmare_GL.exe` 为 Wellbia Security Service。 |

厂商资料用于确认组件归属，前缀是为版本和游戏变体增加的保护性规则，不是厂商承诺的完整文件清单。`ucldr_` 的依据是文件分析样本，不是 Wellbia 发布的命名规范，因此只用于豁免。

## 专用目录与名字普通的辅助进程

除新增三组目录外，也保护 `AntiCheatExpert`、`TenProtect`、`Riot Vanguard`、`EasyAntiCheat`、`EasyAntiCheat_EOS`、`BattlEye`、`EA\AC`、`GameGuard`、`FACEIT AC` 目录内的进程。[EA 官方指南](https://help.ea.com/en/articles/platforms/pc-ea-anticheat/)列有 `C:\Program Files\EA\AC` 安装目录。

目录识别按完整目录段匹配，`EA\AC` 必须是连续两段。不会因为进程位于 `EA`、`Nexon`、`AC` 或 `BlackCipherBackup` 目录就将其豁免；也不会全局豁免 `xm`、`NGService` 这类普通名字。UNC 路径中的服务器名和共享名不充当产品目录；相对路径和含 `.` / `..` 的路径不用于目录归属判断。

进程名只去掉末尾 `.exe`，保留 `EAAntiCheat.GameService` 中的点以及 `.aes`、`.des` 后缀。匹配不区分大小写。后台保护与游戏候选排除共用相同识别入口，避免把目录内的安全组件选作游戏渲染进程。

## 压制强度三档与分组类别

补充日期：2026-09-10。

### 分组类别

`AcGroup` 带 `AcCategory`，判据**不是有没有内核驱动**——BattlEye 有 `BEDaisy.sys`、EAC 有 `EasyAntiCheat.sys`，但它们的用户态服务（`BEService`、`EasyAntiCheat`）压起来既有效又安全，DayZ 上有实测。

真正要分开的是**这一族会不会主动反制第三方工具**：

| 类别 | 进压制目标 | 进豁免名单 | 成员 |
| --- | --- | --- | --- |
| `Suppressible` | 是 | 是 | ace、tp、eac、battleye、eaac、gameguard、faceit、neac |
| `ProtectOnly` | 否 | 是 | vanguard |

Vanguard 单列的依据是 [Riot 官方说明](https://support.riotgames.com/riot/performance/what-is-vanguard)：Vanguard 运行时会阻止某些第三方程序在系统上加载文件，尤其是访问低层系统功能的工具（[OpenRGB 的长期 issue](https://gitlab.com/CalcProgrammer1/OpenRGB/-/issues/316) 是活例子）。压它的失败模式是**游戏起不来**，与"压了没效果"不在一个量级。`vgk` 本来就是内核驱动，用户态碰不到。

改判只影响压制目标，**不影响豁免**：`ProtectOnly` 分组的进程名照旧进 `IsKnownProcess` 与 `IsAntiCheatLikeName`，通用后台压制仍会跳过它们。老配置里若 `Tame_vanguard` 还开着，`Tamer` 构造时说明一次再清掉，不静默失效。

### 强度三档

设置键 `AcModeV1`，默认 `isolated`——它等于本功能一直以来的行为，升级不改变已生效的设置。

| | 温和 Gentle | 均衡 Balanced | 隔离 Isolated |
| --- | --- | --- | --- |
| EcoQoS 小核限频 | 是 | 是 | 是 |
| 磁盘 IO | Low | Low | **VeryLow** |
| 调度优先级 | **不动** | BelowNormal | BelowNormal |
| 绑核到末尾物理核 | 否 | 否 | **是** |

三档映射到既有的 `SuppressionLevel`（`Eco` / `Restrained` / `Isolated`），**压制构成代码一行没改**，只是把 `Tamer.ApplyBatch` 里写死的 `Isolated` 换成读档位。

有效成分（EcoQoS + IO 降级）三档一档都不少，递进的只是介入深度。依据见 `SuppressionCore.Apply` 顶部注释：扫描型反作弊挂起游戏线程时若自己分不到时间片，挂起窗口会从几百毫秒拖到几秒，玩家看到的就是卡死；所以任何一档都不给反作弊喂上「饿死」那三样（IDLE 优先级、页优先级 1、计时器封顶），这三项由 `Desired*` 的 `antiCheat` 标志保护，回归里有断言逐档核对。

绑核只在最高档做：它是三项写入里最容易被反作弊自身保护拒绝的，`PinAntiCheatCores` 已有被拒后不再重试的处理。

## 边界与维护

- 目录与名称是保护性启发规则，不代表验证过文件数字签名。不要据此宣称进程可信、反作弊已启动或某款游戏当前必然使用它。
- 不将 `.sys` 驱动列为可压制进程；内核驱动及集成在游戏本体中的反作弊不能通过独立进程名单控制。
- Session 0 服务、游戏家族和白名单仍有各自保护。名单漏项不必然意味着进程实际被压制。
- 新增的仅保护规则不改变九组开关的专项压制目标及亲和性失败处理。
- 测试覆盖专项开关关闭／开启、普通／扩大后台范围、家族保护关闭／开启、目录边界、缓存身份变化、游戏候选排除，以及新增规则不进入 `Tamer`。
- 未知产品、改名后又移出专用目录的组件仍可能漏识别。遇到新样本，应核对文件路径及厂商资料，补充规则与回归样例，不能宣称名单已经覆盖全部反作弊。

## 验证

在仓库根目录运行 `powershell -NoProfile -File tests\Run-AntiCheatChecks.ps1`，会编译当前源码和指定的隔离测试到 `build\Pavise.anticheat-checks.exe`，运行名单、压制构成、绑核和渲染进程释放回归。配置只存于测试进程内，测试不启动游戏或正常调优运行时。

## 国内平台与厂商补录

核对日期：2026-09-06。

| 条目 | 识别规则 | 核对依据 |
| --- | --- | --- |
| 完美世界竞技平台 PAC（仅保护） | 进程 `完美世界竞技平台`；同名前缀 | [winget 包 PerfectWorld.PerfectWorldArena](https://winstall.app/apps/PerfectWorld.PerfectWorldArena)；[金山毒霸文件说明](https://www.ijinshan.com/envhelp/repairdll-20231216030113.html)记录主程序为 `完美世界竞技平台.exe`；[官方 PAC 说明](https://pvp.wanmei-support.com/)称 PAC 为内核级反作弊组件，未公布独立进程名。 |
| 5E 对战平台（仅保护） | 进程 `5EClient`；同名前缀；`5EClient` 目录 | [Microsoft Q&A 蓝屏工单](https://learn.microsoft.com/zh-cn/answers/questions/3826934/5e-csgo)由官方回复指认 `5EClient.exe`；[5E 反作弊说明](https://www.5eplay.com/article/1194144)未公布独立进程名。 |
| B5 对战平台 BBI（仅保护） | 进程 `B5AntiCheat`、`B5GameService`、`B5GameServiceLoader`、`B5esportsMain`、`B5CSGO`；`B5AntiCheat` / `B5GameService` 前缀；驱动 `B5AntiCheat64` / `B5AntiCheat32` 进内核识别 | [B5 官方帮助](https://www.b5csgo.com.cn/help/137)列出上述进程与驱动文件。 |
| 网易 NEAC 补进程 | `NeacClient`、`OWNeacClient`、`NeacProtect` 加入 neac 组；驱动服务 `NeacSafe64` 进内核识别 | [Microsoft Q&A](https://learn.microsoft.com/zh-cn/answers/questions/3859251/neacprotect)记录 `OWNeacClient.exe` 与 `NeacProtect.exe`；[永劫无间 Steam 讨论](https://steamcommunity.com/app/1203220/discussions/0/3729575905268785715/)记录 `NeacClient.exe`；[CVE-2025-45737](https://www.sentinelone.com/vulnerability-database/cve-2025-45737/)记录 `NeacSafe64.sys`。 |
| 腾讯 TP / ACE 补进程 | `TP3Helper`、`TPHelper` 加入 tp 组；`ACE-Service64` 加入 ace 组；驱动服务 `TesSafe` 进内核识别 | [腾讯电脑管家论坛](https://bbs.guanjia.qq.com/thread-700605-1-1.html)与 [Hybrid Analysis 样本](https://hybrid-analysis.com/sample/a54d7f37b3a19f005ad0559a46ce3beb37997cd8bd96610d7de697f153ff8e3b?environmentId=100)记录 `TP3Helper.exe` 属 TenProtect3；[Glarysoft 启动项](https://www.glarysoft.com/startups/anticheatexpertprotection/aceservice64exe/614622)记录 `ACE-Service64.exe`。 |
| 米哈游 HoYoKProtect（仅识别） | 驱动服务 `HoYoKProtect`、`mhyprot3`、`mhyprot2`；渲染进程前缀 `YuanShen`、`GenshinImpact`、`StarRail`、`ZenlessZoneZero`、`BH3` 用于日志说明 | [知乎 mhyprot2 分析](https://zhuanlan.zhihu.com/p/644102325)与 [蓝点网报道](https://www.landiannews.com/archives/95217.html)记录 `mhyprot2.sys` / `mhyprot3.sys`；无独立用户态进程，不进保护名单。 |

未收录：使命召唤 RICOCHET 没有可写入名单的独立用户态进程。[Activision 官方说明](https://support.activision.com/articles/ricochet-overview)与[Blizzard 公告](https://news.blizzard.com/en-us/article/23733251/ricochet-anti-cheat-call-of-dutys-new-anti-cheat-initiative)记录其内核驱动随游戏启动、退出即卸载，用户态检测跑在游戏进程内，没有独立的常驻服务进程；本目录只收用户态进程，游戏本体不属于反作弊分组，因此不新增条目。日志归因侧另有 `cod` 整名匹配，不受此影响。

未收录的国内厂商：西山居剑网3、盛趣、巨人、畅游、库洛鸣潮均未查到可靠的独立反作弊进程名，多数直接授权腾讯 ACE 或网易 NEAC，已由现有分组覆盖。遇到新样本按上表方式补依据再入库，不要凭猜测写进程名。
