<div align="center">

<a href="https://pavise.club/">
  <img src="docs/icon.png" width="96" height="96" alt="Pavise 标志">
</a>

# PAVISE

### 把资源留给游戏。

Windows 游戏资源管理工具。<br>
按你的选择管理后台、核心调度与电源，游戏结束后恢复已记录的对局改动。

**逐游戏配置 · 智能／竞技场景 · 对局结束恢复**

[![License: GPL-3.0-only](https://img.shields.io/badge/license-GPL--3.0-d6b451?style=flat-square&labelColor=171a21)](LICENSE)
[![Latest release](https://img.shields.io/github/v/release/dulaiduwang003/Pavise-Game?style=flat-square&labelColor=171a21&color=d6b451)](https://github.com/dulaiduwang003/Pavise-Game/releases/latest)
[![GitHub downloads](https://img.shields.io/github/downloads/dulaiduwang003/Pavise-Game/total?style=flat-square&labelColor=171a21&color=d6b451)](https://github.com/dulaiduwang003/Pavise-Game/releases)
[![GitHub stars](https://img.shields.io/github/stars/dulaiduwang003/Pavise-Game?style=flat-square&labelColor=171a21&color=d6b451)](https://github.com/dulaiduwang003/Pavise-Game/stargazers)

**[官网](https://pavise.club/) · [下载与更新](https://github.com/dulaiduwang003/Pavise-Game/releases) · [使用教程](https://pavise.club/docs/) · [问题反馈](https://github.com/dulaiduwang003/Pavise-Game/issues)**

</div>

## 使用前请了解

**软件绝大部分功能仅在对局中生效，退局后自动恢复；不支持退局恢复的功能均已单独标注。** 性能选项按“对局生效”和“长期生效”分组，持久设置按各自说明恢复，部分需要重启。删除的文件和清理的缓存不能通过设置恢复重建。

**请勿同时运行其他会修改进程优先级或 CPU 绑核的调度软件。** 多个工具可能反复覆盖同一项设置。Pavise 不会替你检测或接管第三方调度规则。

各项功能由你选择，守护按已保存的设置执行。实际效果取决于游戏、硬件和后台负载，建议在固定场景中做对照测试。

> **3.3.0.7 源码：[pavise3x](https://github.com/dulaiduwang003/Pavise-Game/tree/pavise3x)。**

## 主要功能

| 模块 | 功能 |
| --- | --- |
| **游戏库与识别** | 添加游戏 EXE、快捷方式或目录，扫描已安装和运行中的游戏，保存逐游戏配置。 |
| **后台治理** | 关闭／正常／激进三档，管理后台 CPU、I/O、内存页、GPU 优先级与效率模式；后台 GPU 调度及传递优化下载让路统一管理。 |
| **保护与白名单** | 保护游戏、必要依赖及白名单；系统进程、网游加速器、反作弊、输入音频与外设、硬件控制、直播推流工具分组展示，支持搜索与自定义规则。 |
| **游戏优先级与线程** | 统一管理游戏 CPU、内存、I/O、GPU 优先级；线程提优优先识别画面提交线程，连续五次采样失败后保留备用策略。 |
| **核心调度与隔离** | 逐游戏选择核心、CCD／SMT／大小核快选、预留系统核与后台核心避让；关键线程自动绑定优先给一个关键线程独立物理核心，游戏自行调整时让出。 |
| **对局电源** | 软件托管或指定已有计划，支持厂商性能档位联动；托管按设备和 CPU 选择策略，退局恢复原配置。 |
| **图形与实验室** | 按硬件支持提供显卡性能偏好、低延迟、GPU 频率与功耗控制、CPU 空闲策略、EPP 功耗让路及音频低缓冲。 |
| **系统与内存** | 缓存预热、待机内存清理、前台时间片、计时器、图形与录制设置，以及独立的 CPU 软件缓解等系统选项；按界面标注区分生效与恢复方式。 |
| **反作弊减负** | ACE、EasyAntiCheat、BattlEye 分组控制，对核验通过的独立用户态服务按强度处理；保留游戏依赖保护，不操作内核反作弊驱动。 |
| **英雄联盟与 WeGame** | 提供专用启动与客户端优化、附加内容清理提示；可按游戏启用 WeGame 界面脱壳并保留必要运行组件。 |
| **系统体检** | 硬件与系统配置检查、GPU 中断观测、核验后调整分配、重启后验证及恢复；MSI 修复与恢复分别提供入口。 |
| **概览** | 显示当前游戏与守护状态、CPU／GPU 占用、内存及本局治理统计。 |
| **日志与 AI 调优** | 查看技术事件、导出完整日志，生成包含本机信息和功能说明的 AI 调优提问。动作与恢复均有执行上限，未完成的恢复保留依据并写入日志。 |

## 智能、竞技与后台治理

**智能与竞技是使用场景，后台治理档位单独选择。**

| 场景 | 行为 |
| --- | --- |
| **智能** | 保护实际前台软件；CPU 持续 10 秒达到 70% 时临时扩大治理范围，负载回落后收回；切出游戏时释放治理。 |
| **竞技** | 对局期间持续治理，切出游戏不自动释放；普通软件切到前台也不会因此自动豁免。游戏、白名单和必要依赖仍受保护。 |

后台治理的**正常档**降低符合条件的后台资源优先级并启用效率模式；**激进档**额外禁止 Windows 临时提升这些后台线程的优先级。

## 一键配置与电源

**作者同款／极限配置**用于批量设置功能，与智能／竞技场景分别选择。作者同款使用正常后台治理和基础 CPU 软件缓解关闭范围；极限配置使用激进后台治理、扩展 CPU 软件缓解关闭范围，以及 GPU 频率与功耗设置。两套预设均会关闭 VBS 和所选范围内的 CPU 软件缓解，降低相应防护；关闭 VBS 还可能影响依赖虚拟化的功能。应用或恢复时请按功能说明和重启提示处理。

两套预设均使用：

- **CPU 空闲策略：跟随原策略。**
- **前台时间片：恢复原配置。**

更新软件不会自动覆盖现有选择，重新应用预设时才按新定义设置。

智能与竞技共用电源参数。软件托管时，**笔记本使用旧策略，台式机按 CPU 匹配新版预置**；缺少高性能模板时，可核验后复制原计划作为候选。识别到 ROG Ally、GPD WIN 等掌机时，交给系统和厂商软件管理。

## 3.3.0.7 更新重点

- 重做后台治理档位、策略入口和白名单分类，新增直播推流工具保护。
- 改进关键线程自动绑定、核心识别与 Job 核心范围保护，修复开机自启后的守护启动问题。
- 修复连续进出游戏时的 **0.5 ms 计时器请求异常**；实际精度按系统返回结果显示。
- 完善有限恢复与优先级恢复，新增完整日志导出，移除诊断摘要和“已停用动作”入口。
- 调整设备电源策略、配置预设与页面分组，修复网络体检及更新提示问题。

完整变更见 [3.3.0.7 更新说明](https://github.com/dulaiduwang003/Pavise-Game/releases/tag/v3.3.0.7)，历史版本见[发布记录](https://github.com/dulaiduwang003/Pavise-Game/releases)。

## 下载与开始使用

运行环境：**Windows 10 2004（19041）及以上，.NET Framework 4.7.2 或更高版本，管理员权限。** 具体功能按系统、硬件和驱动能力提供。

1. 从[官网](https://pavise.club/)或 [GitHub Releases](https://github.com/dulaiduwang003/Pavise-Game/releases) 下载发布包，解压到固定目录并保留随包文件。
2. 以管理员权限运行，添加实际游戏 EXE，检查白名单，选择所需功能或配置预设。
3. 开启守护后进入游戏，识别与会话管理自动执行。
4. 退局后查看恢复状态；长期生效项目需要关闭或恢复时，按对应说明操作。

升级前请先退出游戏并正常退出旧版。软件内更新入口为“关于 → 检查与更新”；需要手动更新时，使用官方发布包，保留个人数据。

## 反馈与支持

反馈时请附上**软件版本、Windows 版本、CPU／显卡、游戏名称、相关设置和复现步骤**，必要时使用日志导出功能附上完整日志。先确认近期系统或驱动更新，以及关闭 Pavise 后问题是否仍然存在。

[提交问题](https://github.com/dulaiduwang003/Pavise-Game/issues) · [使用教程](https://pavise.club/docs/) · [更新记录](https://pavise.club/changelog/)

由 **[bdth](https://github.com/dulaiduwang003)** 创建并维护。Pavise **永久免费，打赏完全自愿**。如果通过其他渠道付费购买了 Pavise，请向卖家或交易平台申请退款。欢迎友好交流、反馈问题和提交修复。

[自愿打赏](https://pavise.club/support/) · [作者主页](https://github.com/dulaiduwang003)

<p align="center">
  <img src="docs/wechat.png" width="220" alt="作者微信打赏二维码">
  <img src="docs/alipay.png" width="220" alt="作者支付宝打赏二维码">
</p>

项目使用 [GNU GPL v3](LICENSE)，分发与版权说明见 [NOTICE](NOTICE)。
