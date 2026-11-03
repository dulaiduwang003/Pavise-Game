<div align="center">

<a href="https://pavise.club/">
  <img src="docs/icon.png" width="96" height="96" alt="Pavise logo">
</a>

# PAVISE

### 把资源留给游戏。

Windows 游戏资源管理工具。<br>按你的选择管理后台、调度与电源，游戏结束后恢复已记录的对局改动。

**逐游戏配置 · 智能／竞技场景 · 对局结束恢复**

**简体中文** · [English](README.en.md) · [日本語](README.ja.md)

[![License: GPL-3.0-only](https://img.shields.io/badge/license-GPL--3.0-d6b451?style=flat-square&labelColor=171a21)](LICENSE)
[![Latest release](https://img.shields.io/github/v/release/dulaiduwang003/Pavise-Game?style=flat-square&labelColor=171a21&color=d6b451)](https://github.com/dulaiduwang003/Pavise-Game/releases/latest)
[![GitHub downloads](https://img.shields.io/github/downloads/dulaiduwang003/Pavise-Game/total?style=flat-square&labelColor=171a21&color=d6b451)](https://github.com/dulaiduwang003/Pavise-Game/releases)
[![GitHub stars](https://img.shields.io/github/stars/dulaiduwang003/Pavise-Game?style=flat-square&labelColor=171a21&color=d6b451)](https://github.com/dulaiduwang003/Pavise-Game/stargazers)
[![Contributors](https://img.shields.io/github/contributors/dulaiduwang003/Pavise-Game?style=flat-square&labelColor=171a21&color=d6b451)](https://github.com/dulaiduwang003/Pavise-Game/graphs/contributors)

**[官网](https://pavise.club/) · [下载最新版](https://github.com/dulaiduwang003/Pavise-Game/releases/latest) · [使用教程](https://pavise.club/docs/) · [问题反馈](https://github.com/dulaiduwang003/Pavise-Game/issues)**

</div>

> **源码与文档正在整理，完成后会上传到本仓库。** 当前仓库里的代码为历史版本，软件功能以正式发布版及其更新说明为准。

## 3.2.0.3 已紧急下架

**3.2.0.3 已停止提供下载和自动更新，请勿继续下载或升级到该版本。** 修复版本正在测试，后续发布以[官网更新记录](https://pavise.club/changelog/)和 [GitHub Releases](https://github.com/dulaiduwang003/Pavise-Game/releases) 为准。

## Pavise 能做什么

游戏、浏览器、启动器和更新任务共用一台电脑。Pavise 根据你保存的设置，在对局期间调整游戏与后台的资源分配；游戏结束后，恢复它记录下来的对局改动。

你决定启用哪些功能，守护负责在识别到游戏后执行。每个游戏可以保存独立配置，白名单与保护规则决定哪些后台程序不受影响。

| 模块 | 主要功能 |
| --- | --- |
| **游戏库与识别** | 添加游戏、扫描安装目录或运行中的程序，管理逐游戏配置；支持运行窗口、最小化及托盘程序的选择。 |
| **后台治理** | 按规则调整后台 CPU、I/O、内存页和 GPU 调度；管理白名单；对局暂停更新、后台下载及维护任务。 |
| **游戏与核心调度** | 游戏优先级、线程提优、逻辑核心选择、CCD／SMT 快选、核心隔离与后台核心避让。 |
| **托管电源** | 按 CPU 架构、设备类型及智能／竞技场景自动匹配方案；保留日常屏幕、睡眠、合盖与电池设置，也可指定已有计划或选择不切换。 |
| **图形与内存** | 按支持情况配置显卡性能偏好、图形开关、缓存预热与待机内存清理；各项独立选择。 |
| **实验室** | CPU 空闲策略、EPP 功耗让路、GPU 频率／功耗控制和音频低缓冲；由硬件及驱动能力决定是否可用。 |
| **系统体检** | 检查系统配置与设备状态；GPU 中断观测、调整、重启后验收及恢复，MSI 修复与恢复分别提供入口。 |
| **恢复与更新** | 原值记录、未完成恢复重试、日志与诊断摘要、AI 调优说明导出，以及带下载进度和签名校验的软件内更新。 |

概览页显示本局**已压制进程数与已暂停服务数**，仅统计确认生效的项目。功能适用性取决于 Windows、硬件及驱动；实际效果需要结合自己的游戏与后台负载判断。

## 智能、竞技与配置预设

- **智能场景**：兼顾游戏与日常应用交互，按当前状态处理符合条件的后台任务。
- **竞技场景**：使用更积极的对局资源策略，仍遵守前台保护、白名单与功能开关。
- **一键配置：作者同款／极限配置**：批量设置功能选项，与智能／竞技场景分别选择。应用前仍检查硬件支持和当前状态。

## 下载与开始使用

1. 从[官网](https://pavise.club/changelog/#latest)或 [GitHub Releases](https://github.com/dulaiduwang003/Pavise-Game/releases/latest) 下载完整发布包。
2. 解压到固定目录，保留 `Pavise.exe`、同名 `.exe.config` 及随包文件，以管理员权限运行。
3. 添加实际游戏 EXE，检查白名单，并按本机情况选择配置后开启守护。
4. 游戏结束后查看恢复状态；持久系统设置按各自说明恢复，标注需要重启的项目按提示处理。

运行环境：**Windows 10 2004（19041）及以上，.NET Framework 4.7.2 或更高版本**。支持 Windows 11；具体功能按系统与设备能力提供。

### 软件内更新与版本说明

从 **3.2.0.2** 起，可在「关于 → 检查与更新」下载更新，查看进度并在校验后完成替换，保留游戏库、白名单和个人设置。**3.2.0.1 及更早版本需先手动升级一次**，更新前先退出游戏。

当前版本已移除帧率侦测、悬浮 OSD、游戏运行报告及 RTSS 集成。完整变更见[发布记录](https://github.com/dulaiduwang003/Pavise-Game/releases)。

## 文档与反馈

[使用教程](https://pavise.club/docs/) · [更新记录](https://pavise.club/changelog/#latest) · [提交问题](https://github.com/dulaiduwang003/Pavise-Game/issues)

反馈时请附上软件版本、Windows 版本、CPU／显卡、游戏名称、相关设置和复现步骤；需要时补充日志或诊断摘要。

## 作者与打赏

由 **[bdth](https://github.com/dulaiduwang003)** 创建并维护。Pavise 免费提供，**打赏完全自愿**。感谢使用、反馈和支持。

[打赏页面](https://pavise.club/support/) · [作者主页](https://github.com/dulaiduwang003)

<p align="center">
  <img src="docs/wechat.png" width="220" alt="作者微信打赏二维码">
  <img src="docs/alipay.png" width="220" alt="作者支付宝打赏二维码">
</p>

项目许可见 [LICENSE](LICENSE)，项目声明见 [NOTICE](NOTICE)。
