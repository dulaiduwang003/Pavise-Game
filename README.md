<div align="center">

<a href="https://pavise.club/">
  <img src="docs/icon.png" width="96" height="96" alt="Pavise logo">
</a>

# PAVISE

### Give your games room to run.

A Windows game resource manager with background process control,<br>
per-game profiles, and automatic recovery after play.

### 手动选择 · 智能／竞技场景

[![License: GPL-3.0-only](https://img.shields.io/badge/license-GPL--3.0-d6b451?style=flat-square&labelColor=171a21)](LICENSE)
[![Latest release](https://img.shields.io/github/v/release/dulaiduwang003/Pavise-Game?style=flat-square&labelColor=171a21&color=d6b451)](https://github.com/dulaiduwang003/Pavise-Game/releases/latest)
[![GitHub downloads](https://img.shields.io/github/downloads/dulaiduwang003/Pavise-Game/total?style=flat-square&labelColor=171a21&color=d6b451)](https://github.com/dulaiduwang003/Pavise-Game/releases)
[![GitHub stars](https://img.shields.io/github/stars/dulaiduwang003/Pavise-Game?style=flat-square&labelColor=171a21&color=d6b451)](https://github.com/dulaiduwang003/Pavise-Game/stargazers)
[![Contributors](https://img.shields.io/github/contributors/dulaiduwang003/Pavise-Game?style=flat-square&labelColor=171a21&color=d6b451)](https://github.com/dulaiduwang003/Pavise-Game/graphs/contributors)

**[官网](https://pavise.club/) · [下载与更新](https://pavise.club/changelog/#latest) · [使用教程](https://pavise.club/docs/)**

</div>

Windows 游戏资源管理工具。各项优化由用户手动选择，保留智能／竞技场景、游戏识别与逐游戏设置；识别到游戏后按已保存的选择执行，结束后恢复有记录的对局改动。

自动校准、台架比较与自动采用结果已移除。游戏运行报告、帧率侦测、性能悬浮栏及 RTSS 集成已完全移除。升级时仅清理有 Pavise 安装凭据且文件身份匹配的 RTSS，保留用户自行安装的版本；也可使用 [Uninstall-RTSS.cmd](Uninstall-RTSS.cmd) 执行相同的归属校验卸载。已有报告文件保留，不再采集或生成新报告。

| 版本 | 说明 | 源码入口 |
| --- | --- | --- |
| 1.x | 早期版本 | [pavise1x](https://github.com/dulaiduwang003/Pavise-Game/tree/pavise1x) |
| 2.x | 手动版本，逐项选择优化策略 | [pavise2x](https://github.com/dulaiduwang003/Pavise-Game/tree/pavise2x) |
| **3.x** | **手动模式，本分支** | [dev](https://github.com/dulaiduwang003/Pavise2x/tree/dev) |

## 功能清单

| 模块 | 功能 |
| --- | --- |
| 游戏库 | 添加 EXE、快捷方式、目录；扫描游戏平台、安装记录和运行中的游戏；搜索、去重、删除、逐游戏配置、强制接管。 |
| 自动识别 | 全屏、无边框与窗口游戏识别；渲染进程及启动器切换；前台游戏自动入库；失败重试；手动删除后忽略、忽略名单管理与重新识别。 |
| 模式与守护 | 手动选择功能；智能／竞技两场景；智能切屏释放会话限制；旧极限／掌机迁移。 |
| 后台治理 | CPU、I/O、内存页及 GPU 调度优先级让位；EcoQoS；计时器策略；重载后台收缩；真实前台进程保护；旧冻结与 CPU 硬限额仅保留恢复路径。 |
| 保护与白名单 | 内置系统、输入、音频、外设和工作应用保护；游戏同族保护或压制；自定义白名单；Steam 平台保护删除与恢复。WeGame 不再内置豁免。 |
| 游戏调度 | 游戏 CPU、内存、I/O、GPU 优先级和节能限制调整；MMCSS；线程提优统一选择关闭、高负载线程（默认）或渲染关键线程（高级），两种策略互斥、仅手动执行；写入条件检查、巡检与退出恢复。 |
| 核心调度 | 按逻辑核心选核；物理核心、CCD、大小核、超线程快选；亲和性；核心隔离并预留系统核；后台硬亲和性；保存逐游戏方案。 |
| 电源 | 通过顶部选择对局电源计划、软件托管计划或“不切换”，保留旧选择，退局还原。保持显示器唤醒；旧版 CPU 空闲调节仅保留恢复。 |
| 内存 | 游戏资源缓存预热；待机内存清理及容量、空闲内存阈值和检查间隔设置。 |
| 更新与维护让路 | 对局暂停更新、下载及系统维护；退局恢复。旧版非必要服务暂停仅保留恢复路径。 |
| 游戏配置减负 | CFG 等进程保护开销调整；游戏盘 NTFS 短文件名设置；逐游戏禁用全屏优化。 |
| 显卡 | NVIDIA 对局性能偏好、着色器缓存、游戏 OpenGL 多线程、后台 3D 限帧；逐游戏 ReBAR；手动 Intel Endurance 偏好；按能力判断和原值恢复。 |
| 系统性能 | 系统增强包：VBS/内存完整性与 hypervisor、Spectre V2/Meltdown 软件缓解、NTFS 最后访问、音频空闲电源；三项存储设备电源设置移为独立高级选项，默认关闭，保留旧选择及恢复记录；前台时间片统一选档和恢复，体检仅查看或跳转。 |
| 低延迟与桌面 | 游戏低延迟；计时器恒定节拍；仅对局期间的 0.5 ms 全局计时器请求、实际精度及状态；对局减少动画与阴影、保留字体平滑。 |
| 系统图形与录制 | HAGS；窗口化游戏优化；系统 VRR；NVIDIA 窗口 G-SYNC；AMD SAM；Game DVR 开关及录屏进程保护。 |
| 系统环境增强 | 增加 NTFS 缓存池，默认关闭；名义内存至少 32 GB、可用至少 8 GB 且不少于总内存的四分之一时才允许开启；开启或恢复需重启。 |
| 反作弊减负 | 用户态进程总开关、厂商分组、处理强度、受限状态与恢复；不操作内核反作弊驱动。 |
| WeGame | 游戏库深度调优中按游戏启用自动脱壳；检测到已观测的渲染进程后退出 WeGame 界面，保留登录与运行组件。 |
| 英雄联盟 | 保留专用的 WeGame 借壳启动、对局真无头、客户端/LCU 状态、立即净化、恢复界面、确认后删除附加层。 |
| 体检与事件 | 硬件、调度、存储、输入链路和显卡中断体检；检查进度、取消与支持项处理；键鼠队列校正已下线，仅保留旧记录恢复；最近对局事件、过滤、折叠、复制摘要、刷新、打开/清除日志和记录开关。 |
| 恢复与清理 | 记录原值；异常退出后恢复；未完成项目保留记录；故障动作列表及退局后单次重试；重置偏好、卸载、着色器缓存清理。 |
| 软件设置 | EXE 改名后的静默开机启动；自动隐藏、托盘；中文/英文/日文；亮暗主题、各模式配色、背景图片与暗化；高 DPI；版本检查、更新说明、官网/反馈/交流/打赏入口。 |

## 使用与编译

Windows 10 2004（19041）起，需要 .NET Framework 4.7.2 或更高版本，使用管理员权限。保留发布包中 `Pavise.exe` 与同名 `.exe.config` 在同目录。窗口尺寸与缩放沿用 3.1.0.5 的固定基准逻辑，当前页面与视觉设计保持不变。添加游戏后开启守护。持久系统设置需单独恢复，部分需重启；删除的文件与清理的缓存不能由配置恢复重建。系统增强涉及安全与虚拟化设置，使用前阅读界面说明。

主程序和独立看门狗使用 Windows 自带 .NET Framework 4.x 编译器，无需 Visual Studio：

```bat
git clone --branch dev https://github.com/dulaiduwang003/Pavise2x.git
cd Pavise2x
build.cmd -b prod
```

输出 `build/Pavise.exe`、同名 `.exe.config` 及旧记录恢复组件。手动模式定向检查：`powershell -NoProfile -File tools/Test-ManualMode.ps1 -OutputDirectory G:/Pavise-Work/20261023/Root/manual-checks-new`。使用新的输出目录；检查使用临时设置和独立桌面，不启动真实产品或硬件负载。

## 作者与打赏

作者 [bdth](https://github.com/dulaiduwang003) · [反馈问题](https://github.com/dulaiduwang003/Pavise-Game/issues) · [打赏页面](https://pavise.club/support/)。软件免费，打赏自愿。

<img src="docs/wechat.png" width="220" alt="作者微信打赏二维码"> <img src="docs/alipay.png" width="220" alt="作者支付宝打赏二维码">

源码使用 [GNU GPL v3](LICENSE)，版权及分发说明见 [NOTICE](NOTICE)。提交修复请以 `dev` 为目标分支，并说明测试结果。
