<div align="center">

<a href="https://pavise.club/">
  <img src="docs/icon.png" width="96" height="96" alt="Pavise logo">
</a>

# PAVISE

### Give your games room to run.

A Windows game resource manager with background process control,<br>
per-game profiles, and automatic recovery after play.

### 3.x 自动版本

[![License: GPL-3.0-only](https://img.shields.io/badge/license-GPL--3.0-d6b451?style=flat-square&labelColor=171a21)](LICENSE)
[![Latest release](https://img.shields.io/github/v/release/dulaiduwang003/Pavise-Game?style=flat-square&labelColor=171a21&color=d6b451)](https://github.com/dulaiduwang003/Pavise-Game/releases/latest)
[![GitHub downloads](https://img.shields.io/github/downloads/dulaiduwang003/Pavise-Game/total?style=flat-square&labelColor=171a21&color=d6b451)](https://github.com/dulaiduwang003/Pavise-Game/releases)
[![GitHub stars](https://img.shields.io/github/stars/dulaiduwang003/Pavise-Game?style=flat-square&labelColor=171a21&color=d6b451)](https://github.com/dulaiduwang003/Pavise-Game/stargazers)
[![Contributors](https://img.shields.io/github/contributors/dulaiduwang003/Pavise-Game?style=flat-square&labelColor=171a21&color=d6b451)](https://github.com/dulaiduwang003/Pavise-Game/graphs/contributors)

**[官网](https://pavise.club/) · [下载与更新](https://pavise.club/changelog/#latest) · [使用教程](https://pavise.club/docs/)**

</div>

Windows 游戏资源管理工具。3.x 是自动版本：识别游戏后按模式执行调度，退出游戏后还原有记录的对局改动。核心选择、系统环境和监测显示仍可单独设置。

| 版本 | 说明 | 源码入口 |
| --- | --- | --- |
| 1.x | 早期版本 | [pavise1x](https://github.com/dulaiduwang003/Pavise-Game/tree/pavise1x) |
| 2.x | 手动版本，逐项选择优化策略 | [pavise2x](https://github.com/dulaiduwang003/Pavise-Game/tree/pavise2x) |
| **3.x** | **自动版本，本分支** | [pavise3x](https://github.com/dulaiduwang003/Pavise-Game/tree/pavise3x) |

## 功能清单

| 模块 | 功能 |
| --- | --- |
| 游戏库 | 添加 EXE、快捷方式、目录；扫描游戏平台、安装记录和运行中的游戏；搜索、去重、删除、逐游戏配置、强制接管。 |
| 自动识别 | 全屏、无边框与窗口游戏识别；渲染进程及启动器切换；前台游戏自动入库；失败重试；手动删除后忽略、忽略名单管理与重新识别。 |
| 模式与守护 | 智能、竞技、极限、掌机；智能持续竞争时扩展后台治理范围；守护总开关；自动执行详情与状态；按游戏设置；手动方案优先。 |
| 后台治理 | CPU、I/O、内存页及 GPU 调度优先级让位；EcoQoS；计时器策略；重载后台收缩；极限模式下的后台 CPU 限额与受限挂起。 |
| 保护与白名单 | 内置系统、输入、音频、外设和工作应用保护；游戏同族保护或压制；自定义白名单；Steam、WeGame 平台保护删除与恢复。 |
| 游戏调度 | 游戏 CPU、I/O、GPU 优先级和节能限制调整；MMCSS；线程协同；写入条件检查、巡检与退出恢复。 |
| 核心调度 | 按逻辑核心选核；物理核心、CCD、大小核、超线程快选；亲和性；核心隔离并预留系统核；后台硬亲和性；后台核心自动避让；保存逐游戏方案。 |
| 电源与笔记本 | 对局电源计划选择、软件托管计划和“不切换”；极限 CPU 空闲策略；部分联想、华硕机型的插电性能档；保持显示器唤醒；退局还原。 |
| 内存 | 对局后台工作集回收；游戏资源缓存预热；待机内存清理及容量、空闲内存阈值和检查间隔设置。 |
| 更新与维护让路 | 对局暂停更新、下载、系统维护及支持的可选服务；退局恢复；异常退避与按状态巡检。 |
| 游戏配置减负 | CFG 等进程保护开销调整；按归属记录游戏资源扫描排除；游戏盘 NTFS 短文件名设置；逐游戏禁用全屏优化。 |
| 显卡 | NVIDIA 对局性能偏好、着色器缓存、游戏 OpenGL 多线程、后台 3D 限帧；逐游戏 ReBAR；极限 Intel Endurance 策略；按能力判断和原值恢复。 |
| 系统性能 | 系统增强包：VBS/内存完整性与 hypervisor、Spectre V2/Meltdown 软件缓解、NTFS 最后访问、存储空闲/运行时电源/D3Cold、音频空闲电源；前台时间片独立选档和恢复。 |
| 低延迟与桌面 | 游戏低延迟；计时器恒定节拍；仅对局期间的 0.5 ms 全局计时器请求、实际精度及状态；对局减少动画与阴影、保留字体平滑。 |
| 系统图形与录制 | HAGS；窗口化游戏优化；系统 VRR；NVIDIA 窗口 G-SYNC；AMD SAM；Game DVR 开关及录屏进程保护。 |
| 隐私与后台 | Windows 诊断数据、DiagTrack、体验改善计划、兼容性评估、错误报告、MSRT 统计、笔迹/键入、广告标识/诊断个性化；程序兼容性助手及对局暂停；Windows 应用后台总开关；NTFS 缓存池。 |
| 反作弊减负 | 用户态进程总开关、厂商分组、处理强度、受限状态与恢复；不操作内核反作弊驱动。 |
| 英雄联盟 | WeGame 借壳启动、对局真无头、客户端/LCU 状态、立即净化、恢复界面、确认后删除附加层。 |
| 性能监测 | RTSS 后台安装、检测、卸载与对局启停；保留用户已有实例；核显及多显卡匹配；统计窗口和不可用状态。18 项指标见下表。 |
| 悬浮栏 | 左上、右上、顶部条；字号、间距、颜色、黑色背景透明度；屏幕贴边、多屏及渲染分辨率适配；拖动预览、方向键微调；Ctrl + F10 显示/隐藏及自定义快捷键。 |
| 体检与事件 | 硬件、调度、存储、输入链路和显卡中断体检；检查进度、取消与支持项处理；最近对局事件、过滤、折叠、复制摘要、刷新、打开/清除日志和记录开关。 |
| 恢复与清理 | 记录原值；异常退出后恢复；未完成项目保留记录；故障动作列表及退局后单次重试；重置偏好、卸载、着色器缓存清理。 |
| 软件设置 | EXE 改名后的静默开机启动；自动隐藏、托盘；中文/英文/日文；亮暗主题、各模式配色、背景图片与暗化；高 DPI；版本检查、更新说明、官网/反馈/交流/打赏入口。 |

| 监测分组 | 指标 |
| --- | --- |
| 帧率与时间 | FPS、平均 FPS、1% Low、0.1% Low、帧时间、帧时间标准差 |
| GPU | 利用率、温度、核心频率、功耗、显存用量、显存占用率、显存频率、风扇转速 |
| CPU 与内存 | 游戏 CPU 占用率、CPU 封装温度、CPU 封装功耗、系统内存用量、内存占用率、游戏工作集 |

内存/显存的容量和占用率合并显示，对应软件中的 18 个可选项。温度、功耗等依赖硬件和驱动，不支持时显示 `--`；Low 帧率需要积累有效帧数据。

## 使用与编译

Windows 10 2004（19041）起，使用管理员权限。下载 `Pavise.exe`，添加游戏后开启守护；监测默认关闭，需要时安装 RTSS。持久系统设置需单独恢复，部分需重启；删除的文件与清理的缓存不能由配置恢复重建。系统增强涉及安全与虚拟化设置，使用前阅读界面说明。

Windows 自带 .NET Framework 4.x 编译器即可构建，无需安装 Visual Studio：

```bat
git clone --branch pavise3x https://github.com/dulaiduwang003/Pavise-Game.git
cd Pavise-Game
build.cmd -b prod
```

输出 `build\Pavise.exe`。回归检查：`build.cmd -b dev build\Pavise.selftest.exe --selftest`，随后运行 `build\Pavise.selftest.exe --selftest`。

## 作者与打赏

作者 [bdth](https://github.com/dulaiduwang003) · [反馈问题](https://github.com/dulaiduwang003/Pavise-Game/issues) · [打赏页面](https://pavise.club/support/)。软件免费，打赏自愿。

<img src="docs/wechat.png" width="220" alt="作者微信打赏二维码"> <img src="docs/alipay.png" width="220" alt="作者支付宝打赏二维码">

源码使用 [GNU GPL v3](LICENSE)，版权及分发说明见 [NOTICE](NOTICE)。提交修复请以 `pavise3x` 为目标分支，并说明测试结果。
