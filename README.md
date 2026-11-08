<div align="center">

<a href="https://pavise.club/">
  <img src="docs/icon.png" width="88" height="88" alt="Pavise 标志">
</a>

# PAVISE

### 把资源留给游戏。

Windows 游戏资源管理工具。<br>
按你的选择管理后台、CPU 调度、电源与系统选项，退局后恢复已记录的对局改动。

**3.3.0.8 · 逐游戏配置 · 智能／竞技场景 · 动作与诊断日志**

[![License: GPL-3.0-only](https://img.shields.io/badge/license-GPL--3.0-d6b451?style=flat-square&labelColor=171a21)](LICENSE)
[![Latest release](https://img.shields.io/github/v/release/dulaiduwang003/Pavise-Game?style=flat-square&labelColor=171a21&color=d6b451)](https://github.com/dulaiduwang003/Pavise-Game/releases/latest)
[![GitHub downloads](https://img.shields.io/github/downloads/dulaiduwang003/Pavise-Game/total?style=flat-square&labelColor=171a21&color=d6b451)](https://github.com/dulaiduwang003/Pavise-Game/releases)
[![GitHub stars](https://img.shields.io/github/stars/dulaiduwang003/Pavise-Game?style=flat-square&labelColor=171a21&color=d6b451)](https://github.com/dulaiduwang003/Pavise-Game/stargazers)

**[官网](https://pavise.club/) · [下载与更新](https://github.com/dulaiduwang003/Pavise-Game/releases/latest) · [使用教程](https://pavise.club/docs/) · [问题反馈](https://github.com/dulaiduwang003/Pavise-Game/issues)**

</div>

> **源码与文档持续整理中。** 本仓库已同步 3.3.0.8 源码；正式版下载和版本说明见[发布页](https://github.com/dulaiduwang003/Pavise-Game/releases/tag/v3.3.0.8)。

## 使用前请了解

**软件绝大部分功能仅在对局中生效，退局后自动恢复；不支持退局恢复的功能均已单独标注。** 长期项目按各自说明关闭或恢复，部分操作需要重启。文件删除和缓存清理不属于可恢复的设置改动。

**请勿与其他修改进程优先级或 CPU 亲和性的调度软件同时使用**，避免双方反复覆盖设置。

实际效果取决于游戏、硬件、驱动与后台负载，可在固定场景中对照观察。

## 下载与开始使用

运行环境：**Windows 10 2004（19041）及以上、.NET Framework 4.7.2 或更高版本、管理员权限。** 各功能按本机能力提供。

| 下载方式 | 入口 |
| --- | --- |
| 官网完整 ZIP | [下载 3.3.0.8](https://pavise.club/downloads/Pavise-3.3.0.8.zip) |
| GitHub | [发布与下载](https://github.com/dulaiduwang003/Pavise-Game/releases/latest) |
| 夸克网盘 | [备用下载，请核对版本](https://pan.quark.cn/s/3c8c986b3ea4) |

1. 下载后放到固定目录；使用完整 ZIP 时，将随包文件一起解压。
2. 以管理员权限启动，添加实际游戏 EXE，检查白名单及逐游戏设置。
3. 选择所需功能或一键配置，开启守护后进入游戏。
4. 退局后查看恢复状态；需要排查时，导出该会话的日志。

升级前先退出游戏并正常退出旧版。软件内更新入口为 **关于 → 检查与更新**，更新保留个人数据。

## 从源码构建

在 Windows 上使用系统自带的 .NET Framework C# 编译器和 Windows PowerShell，无需安装 Visual Studio。

```bat
build.cmd -b prod
```

生成文件为 `build\Pavise.exe`，构建过程会核验实现和版本标识。

## 主要功能

| 模块 | 功能 |
| --- | --- |
| **游戏库与识别** | 添加、扫描游戏并保存逐游戏配置；结合进程、窗口和渲染证据识别游戏本体，减少内置网页误识别。 |
| **性能优先级与线程协同** | 管理游戏 CPU、I/O、内存与 GPU 优先级；优先使用画面提交证据识别关键线程，保留有界采样与负载备用策略。 |
| **核心调度与渲染亲和性** | 按游戏设置 CPU 核心范围，渲染亲和性支持自动／手动选核及物理核避让；其他持续高负载线程可使用理想处理器提示。 |
| **后台治理与白名单** | 关闭／正常／激进三档，管理后台资源优先级与效率模式；支持同族压制，保护游戏、必要依赖和白名单。 |
| **对局电源与计时器** | 软件托管或指定电源计划，按支持情况联动厂商性能档位；计时器请求读回核验，退局归还本次临时状态。 |
| **图形、系统与内存** | 按能力提供显卡性能偏好、低延迟、GPU 频率与功耗、CPU 空闲、EPP、音频、内存清理及其他系统选项。 |
| **反作弊减负** | ACE、EasyAntiCheat、BattlEye 独立分组；仅处理准入核验通过的用户态进程或服务，保留游戏依赖保护。 |
| **英雄联盟与 WeGame** | 提供专用启动、客户端优化和附加内容清理；按配置处理 WeGame 界面组件并保留必要依赖。 |
| **系统体检** | 硬件与系统检查、GPU 中断观测、分配调整、重启后验证及恢复；MSI 修复与恢复分别提供入口。 |
| **概览、日志与 AI 调优** | 查看游戏与守护状态、资源读数及本局统计；分别查看和导出动作／诊断日志，生成含本机信息与功能说明的 AI 调优提问。 |

## 场景与后台治理

**智能／竞技决定使用场景，后台治理档位单独选择。** 游戏、白名单和必要依赖持续受保护。

| 场景 | 行为 |
| --- | --- |
| **智能** | 按前台与负载调整治理范围；切出游戏释放后台治理，对局电源与亲和性继续保留。 |
| **竞技** | 对局期间持续治理，切出游戏保留治理与亲和性；普通软件切到前台不会因此自动豁免。 |

正常档降低符合条件的后台资源优先级并启用效率模式；激进档额外限制 Windows 对这些后台线程的临时提优。

**渲染选核联动核心调度范围。** 未开启核心调度时，手动选核显示全核心；选核与核心范围调整下局生效，关闭渲染亲和性立即撤回。核心避让针对可管理的线程与进程，系统中断和受保护进程仍受 Windows 管理。

## 配置预设与电源

作者同款／极限配置用于批量设置功能，与智能／竞技场景分别选择。两套预设的 **CPU 空闲策略均为“跟随原策略”，前台时间片均为“恢复原配置”**；更新软件保留已有选择，重新应用预设时才按新定义设置。

预设中的 VBS、CPU 软件缓解等长期项目会改变相应系统防护，请按功能说明和重启提示处理。

智能与竞技共用电源参数。软件托管时，笔记本沿用旧策略，台式机按 CPU 匹配预置；高性能模板缺失时，可核验后复制原计划作为候选。识别到支持名单中的掌机时，由系统和厂商软件管理电源。3.3.0.8 改善厂商电源兼容，连续冲突时停止切换并恢复原计划。

## 3.3.0.8 更新重点

- 渲染亲和性支持自动／手动选核与整核隔离，联动游戏核心范围。
- 减少线程采样、核心隔离和后台治理开销，移除自动后台节能显卡登记。
- 新增动作／诊断日志，查看压制、落核、清核及恢复结果，支持分别导出。
- 优化鸣潮等游戏识别，减少内置网页被误认作游戏本体。
- 改善笔记本厂商电源兼容，连续冲突时停止切换并恢复原计划。
- 新增安全软件兼容提示，启动时检查一次，支持“不再显示”。
- 修复退局恢复与电源状态误报，减少重复日志和无效警告。

完整变更见 [3.3.0.8 发布说明](https://github.com/dulaiduwang003/Pavise-Game/releases/tag/v3.3.0.8)，历史版本见[发布记录](https://github.com/dulaiduwang003/Pavise-Game/releases)。

## 软件架构

主程序负责配置、游戏识别与会话编排；CPU、后台、电源及系统模块按条件执行。独立守护承担必要的异常退出恢复，本地配置、恢复记录与日志分别保存。

![Pavise 3.3.0.8 软件架构设计图（中文）](docs/diagrams/3.3.0.8/Pavise-3.3.0.8-Architecture-ZH.png)

<details>
<summary>English · Software Architecture Design</summary>

![Pavise 3.3.0.8 Software Architecture Design](docs/diagrams/3.3.0.8/Pavise-3.3.0.8-Architecture-EN.png)

</details>

## 游戏优化执行流程

识别游戏 → 建立会话与配置快照 → 按条件执行 → 核验并记录 → 退局恢复。每项功能按自身开关与能力条件运行。

![Pavise 3.3.0.8 游戏优化执行流程图（中文）](docs/diagrams/3.3.0.8/Pavise-3.3.0.8-Execution-ZH.png)

<details>
<summary>English · Game Optimization Execution Flow</summary>

![Pavise 3.3.0.8 Game Optimization Execution Flow](docs/diagrams/3.3.0.8/Pavise-3.3.0.8-Execution-EN.png)

</details>

采样、应用与恢复失败均有执行上限。恢复预算耗尽后停止主动尝试，保留恢复记录与持有资源，日志显示实际恢复状态。

## 反馈与支持

反馈请附上 **软件版本、Windows 版本、CPU／显卡、游戏名称、相关设置与复现步骤**。动作日志用于查看本局执行了什么，诊断日志用于提供技术细节。

若出现掉帧、卡顿或延迟，可先在相同场景中对照关闭 Pavise 前后的表现，并说明近期是否更新过系统、游戏或驱动。

[提交问题](https://github.com/dulaiduwang003/Pavise-Game/issues) · [使用教程](https://pavise.club/docs/) · [更新记录](https://pavise.club/changelog/)

由 **[bdth](https://github.com/dulaiduwang003)** 创建并维护。Pavise 永久免费，打赏完全自愿。欢迎反馈问题和友好交流。

[自愿打赏](https://pavise.club/support/) · [作者主页](https://github.com/dulaiduwang003)

<p align="center">
  <img src="docs/wechat.png" width="220" alt="作者微信打赏二维码">
  <img src="docs/alipay.png" width="220" alt="作者支付宝打赏二维码">
</p>

项目使用 [GNU GPL v3](LICENSE)，分发与版权说明见 [NOTICE](NOTICE)。
