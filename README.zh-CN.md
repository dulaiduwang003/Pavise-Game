<div align="center">

<a href="https://pavise.club/">
  <img src="docs/icon.png" width="96" height="96" alt="Pavise 标志">
</a>

# PAVISE

### 把机器让给游戏。

Windows 游戏资源管理器：后台进程压制、逐游戏独立配置，<br>
退出游戏后自动还原。

[English](README.md) · **简体中文** · [日本語](README.ja.md)

[![License: GPL-3.0-only](https://img.shields.io/badge/license-GPL--3.0-d6b451?style=flat-square&labelColor=171a21)](LICENSE)
[![Latest release](https://img.shields.io/github/v/release/dulaiduwang003/Pavise-Game?style=flat-square&labelColor=171a21&color=d6b451)](https://pavise.club/changelog/#latest)
[![GitHub downloads](https://img.shields.io/github/downloads/dulaiduwang003/Pavise-Game/total?style=flat-square&labelColor=171a21&color=d6b451)](https://github.com/dulaiduwang003/Pavise-Game/releases)
[![GitHub stars](https://img.shields.io/github/stars/dulaiduwang003/Pavise-Game?style=flat-square&labelColor=171a21&color=d6b451)](https://github.com/dulaiduwang003/Pavise-Game/stargazers)
[![Contributors](https://img.shields.io/github/contributors/dulaiduwang003/Pavise-Game?style=flat-square&labelColor=171a21&color=d6b451)](https://github.com/dulaiduwang003/Pavise-Game/graphs/contributors)

**[官网](https://pavise.club/) &nbsp; / &nbsp; [下载与更新说明](https://pavise.club/changelog/#latest) &nbsp; / &nbsp; [使用教程](https://pavise.club/docs/)**

<br>

<img src="docs/screenshots/overview.png" width="100%" alt="Pavise 概览页：当前游戏、会话状态和资源控制">

</div>

<p align="center">
  <a href="#为什么选择-pavise">为什么选择 Pavise</a> ·
  <a href="#功能">功能</a> ·
  <a href="#模式">模式</a> ·
  <a href="#快速开始">快速开始</a> ·
  <a href="#参与贡献">参与贡献</a> ·
  <a href="#贡献者">贡献者</a>
</p>

## 为什么选择 Pavise

游戏和浏览器、启动器、更新服务以及各种后台任务共用一台机器。Pavise 管理的就是这场争抢：在你玩游戏的时候，让游戏获得更高的调度优先级，并压低符合条件的后台进程所占用的资源。

**游戏结束后，Pavise 会自动还原它记录下来的对局期改动。** 如果 Pavise 异常退出，下次启动会继续尝试恢复，并保留所有未能还原项目的记录。持久设置和文件清理有各自的恢复规则，见[哪些会被还原](#哪些会被还原)。

| 为游戏而做 | 回到桌面照旧 | 看得见发生了什么 |
| :--- | :--- | :--- |
| 自动识别游戏、可配置的后台压制、逐游戏独立配置。 | 记录下来的对局期改动在游戏结束后还原，不会动你日常的系统设置。 | 会话报告、硬件体检、结构化日志，以及可一键复制的诊断摘要。 |

Pavise 纯本地运行，不上传本机数据，不注入游戏进程，也不修改游戏内存。启动时向官方更新源检查一次版本。

它不会凭空产生额外的 CPU 或 GPU 性能。收益取决于后台争抢程度、硬件和游戏本身。请在同一场景下开关对比；系统本身干净、或者游戏完全卡在显卡上时，变化可能很小。

## 功能

| 模块 | 你可以控制什么 |
| :--- | :--- |
| **游戏库** | 添加 EXE、快捷方式或文件夹；扫描 Steam、Epic、GOG、育碧、Riot、WeGame、战网、Xbox 和 Microsoft Store。每个游戏可以保留一套独立配置。 |
| **后台进程** | 统一调整 CPU、磁盘 IO、分页和 GPU 优先级、EcoQoS 与定时器策略。内置保护规则和你的白名单决定哪些进程不受影响。 |
| **CPU 核心** | 选择游戏核心，使用 CCD 和 SMT 快选，留出系统核，还可以把普通后台挡在游戏核心范围之外。 |
| **显卡** | 调用受支持的 NVIDIA、AMD 和 Intel 驱动控制项、显卡偏好、功耗墙和可选的显存策略，原值全部有记录。 |
| **内存与电源** | 托管电源方案、对局期电源策略和可选的内存控制。内存清理等高级策略均可单独配置。 |
| **设备中断** | 观测 DPC 活动，多局对比，查看设备与候选核心，重启后验证手动调整的效果。 |
| **诊断** | 检查硬件能力、调度设置、功耗与温度限制、会话报告和警告。反馈问题时一键复制诊断摘要。 |
| **使用体验** | 中文 / English 即时切换，亮色或暗色主题，功能搜索，收进托盘，管理白名单。 |

**[查看每个模块、每个开关和它们的限制 →](https://pavise.club/docs/)**

通用后台压制始终跳过反作弊进程、Windows 核心服务、输入音频与外设链、硬件控制工具和其它登录账户。反作弊压制是独立开关，默认关闭，只覆盖指定的用户态进程，不控制内核驱动。游戏家族豁免默认开启。

## 模式

| 模式 | 后台范围 | 切出游戏后使用的程序 | 电源与硬件策略 |
| :--- | :--- | :--- | :--- |
| **智能** | 对局一开始就隔离符合条件的后台进程。 | 你正在用的程序和它的家族不参与压制。 | 可选的自适应升档，默认关闭。 |
| **电竞** | 压制范围扩大到符合条件的非游戏进程，有窗口的也不例外。 | 仍在压制范围内；白名单与内置保护仍有效。 | 附加策略仍可自行配置。 |
| **掌机** | 后台范围与电竞相同。 | 与电竞相同。 | 功耗侧让给厂商工具；需要电池，不提供若干面向台式机的策略。 |
| **自定义** | 后台、核心、显卡、内存、电源和系统环境策略逐项自选。 | 取决于你选择的策略。 | 全局调整，或按游戏覆盖。 |

本机不支持的模式不会显示。原先的极限档已在 v2.2.2 移除，它附带的项目现在是各自独立的开关，默认关闭。[模式详细说明](https://pavise.club/docs/modes/)。

## 快速开始

**系统要求：** Windows 10 2004（内部版本 19041）或更高，推荐 Windows 11 24H2。资源管理需要管理员权限。应用界面支持**简体中文和 English**，另有日语文档。

1. **获取 Pavise**：前往[官方下载页](https://pavise.club/changelog/#latest)，阅读更新说明后选择 GitHub 或夸克下载。下载免费。
2. **打开 Pavise**，把游戏的 EXE、快捷方式或文件夹加入游戏库，或者直接扫描已安装的游戏。
3. **选择模式**并检查它的设置。把不希望受影响的程序加入白名单。需要的话给单个游戏做独立配置。
4. **开启守护，开始游戏。** 识别和会话管理自动进行；最小化游戏不会结束会话。
5. **退出游戏。** Pavise 还原记录下来的对局期改动。查看会话报告或诊断摘要，了解这一局发生了什么。

程序目前未数字签名。详细的设置步骤和截图见[图文教程](https://pavise.club/docs/)。

## 哪些会被还原

| 改动 | 恢复行为 |
| :--- | :--- |
| **对局期改动** | 游戏结束时按记录的原值还原。异常退出后，下次启动继续尝试恢复。 |
| **持久设置** | 系统环境页的设置、应用显卡偏好和逐 EXE 兼容设置会一直保留，直到单独还原。部分改动需要重启。 |
| **输入语言** | 进入游戏时切换英文布局的一次性请求不会回退。 |
| **已删除的文件或已清理的缓存** | 设置恢复无法重建它们。可选的 LOL 附加层删除是独立操作，需要明确确认。 |

关闭守护会停止通用会话管理并尝试还原对局期改动。待命策略、持久设置和游戏扩展以各自的开关为准。

<details>
<summary><strong>恢复工具与本地数据</strong></summary>

设置页提供恢复和卸载入口。程序已经打不开时，仓库里有 [Pavise-Rescue.cmd](Pavise-Rescue.cmd)：它先导出诊断信息再尝试恢复，重置电源方案，并**删除游戏库、白名单和全部设置**。跑完必须重启一次；使用前请先阅读[恢复说明](https://pavise.club/docs/recovery/)。

数据默认保存在 `%AppData%\Pavise`，界面和功能开关保存在注册表 `HKCU\Software\Pavise`。在程序旁放置空文件 `Pavise.portable` 后改为保存在程序目录。

</details>

## 界面一览

<table>
<tr>
<td width="50%"><img src="docs/screenshots/library.png" alt="Pavise 游戏库"><br><strong>游戏库</strong><br>游戏、识别与逐游戏配置。</td>
<td width="50%"><img src="docs/screenshots/policy.png" alt="Pavise 优化策略"><br><strong>优化策略</strong><br>控制对局期间使用的策略。</td>
</tr>
<tr>
<td width="50%"><img src="docs/screenshots/graphics.png" alt="Pavise 显卡页"><br><strong>显卡</strong><br>驱动控制项与 GPU 策略。</td>
<td width="50%"><img src="docs/screenshots/interrupt.png" alt="Pavise 设备中断页"><br><strong>设备中断</strong><br>观测、调整、对比结果。</td>
</tr>
</table>

## 文档

| 从这里开始 | 深入了解 |
| :--- | :--- |
| [图文教程](https://pavise.club/docs/) | [模式详解](https://pavise.club/docs/modes/) |
| [更新说明与下载](https://pavise.club/changelog/#latest) | [逐游戏独立配置](https://pavise.club/docs/profiles/) |
| [English 文档](README.md) | [调度机制](https://pavise.club/docs/mechanisms/) |
| [日本語文档](README.ja.md) | [内存与电源策略](https://pavise.club/docs/memory-power/) |

## 从源码构建

在 Windows 上使用 .NET Framework 4.x 编译器构建。构建脚本直接调用系统自带的编译器，不需要 Visual Studio。

```bat
git clone --branch pavise2x https://github.com/dulaiduwang003/Pavise-Game.git
cd Pavise-Game
build.cmd -b dev
```

产物是 `build\Pavise.exe`。构建并运行独立的回归测试程序：

```bat
build.cmd -b dev build\Pavise.selftest.exe --selftest
build\Pavise.selftest.exe
```

## 参与贡献

欢迎 Bug 反馈、修复、文档改进和翻译。

1. 反馈 Bug 请[提交 Issue](https://github.com/dulaiduwang003/Pavise-Game/issues)，附上 Pavise 版本、Windows 版本、硬件、受影响的游戏、相关设置和复现步骤。分享诊断输出前请检查其中是否包含个人信息。
2. Fork 仓库，从 **`pavise2x`** 创建分支，改动保持聚焦。较大的功能请先在 Issue 里讨论方案。
3. 说明改了什么、怎么测的。界面改动附截图，行为修复附相关日志或回归测试。
4. 向 **`pavise2x`** 提交 Pull Request。维护者会在合并前审查并测试。

使用、修改或分发代码前请阅读 [GNU GPLv3](LICENSE)。

## 贡献者

感谢每一位通过代码、测试、Bug 反馈和翻译改进 Pavise 的人。

<a href="https://github.com/dulaiduwang003/Pavise-Game/graphs/contributors">
  <img src="https://raw.githubusercontent.com/dulaiduwang003/Pavise-Game/codex/readme-assets/contributors.svg" alt="Pavise 贡献者：完整名单见 GitHub">
</a>

贡献者头像在改动进入默认分支后以及每天一次由 [GitHub Actions](https://github.com/dulaiduwang003/Pavise-Game/actions/workflows/contributors.yml) 自动刷新。图片最多展示 GitHub 提交历史中的 100 位贡献者。[查看全部贡献者](https://github.com/dulaiduwang003/Pavise-Game/graphs/contributors) · [参与进来](https://github.com/dulaiduwang003/Pavise-Game/issues)

## 支持与社区

由 **[bdth](https://github.com/dulaiduwang003)** 创建并维护。

- **官网：** [pavise.club](https://pavise.club/)
- **Bug 与建议：** [GitHub Issues](https://github.com/dulaiduwang003/Pavise-Game/issues)
- **邮箱：** [2074055628@qq.com](mailto:2074055628@qq.com)
- **QQ 群：** 4 群 `166255062` · 5 群 `1109874913`
- **支持开发：** [捐赠](https://pavise.club/support/)。捐赠完全自愿，Pavise 及其全部功能免费获取。

## 许可

Pavise 采用 **[GNU 通用公共许可证第 3 版](LICENSE)**（仅第 3 版，`GPL-3.0-only`）。Copyright (C) 2026 bdth。

你可以依照 GPLv3 使用、学习、修改和分发 Pavise，也可以商用或收费分发。分发受许可覆盖的作品时，须保留版权及许可声明，注明修改内容和日期，并继续以 GPLv3 授权；分发二进制文件时，须按许可证要求向接收者提供对应源码。

本软件**不提供任何担保**，包括适销性或特定用途适用性担保。完整条款见 [LICENSE](LICENSE)，项目声明见 [NOTICE](NOTICE)。

官方版本继续通过[官网](https://pavise.club/changelog/#latest)免费提供，捐赠始终自愿。
