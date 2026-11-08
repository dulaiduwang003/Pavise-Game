# 测试与独立台架

本目录公开当前 3.3.0.8 源码上已验证可运行的回归测试与必要夹具。测试直接编译对应的产品源码，通过内存设置、受控回调或自有子进程验证行为；各入口的隔离范围见下表。历史上尚待适配的测试不列入这套入口。

## 运行

需要 Windows x64、.NET Framework 4.7.2 或更新版本，以及 Windows PowerShell 5.1。使用系统 C# 编译器，无需 Visual Studio、管理员权限或额外 PowerShell 模块。

在仓库根目录运行：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/Run-PublicTests.ps1
```

默认在系统临时目录创建新的结果目录。可使用 `-OutputDirectory C:/path/to/new-results` 指定一个尚不存在的目录。每套检查只有一次执行机会，子进程有时间上限；失败保留输出并停止，不自动重试。结果中的源码路径、硬件信息等仅保存在本机，分享前请自行检查。

## 覆盖范围

| 入口 `tools/Test-*.ps1` | 验证内容 | 隔离方式 |
| --- | --- | --- |
| `SmartGovernanceThreshold` | 智能场景负载阈值、持续时间与退出滞回 | 固定输入与受控时钟，不采集实际 CPU 负载 |
| `BackgroundGovernanceRuntime` | 正常／激进后台治理、提优限制与原值恢复 | 内存回调；原生提优往返仅作用于测试进程自身 |
| `ConfigurationPresetReads` | 配置预设的实际读取与组合 | 产品源码与临时内存设置 |
| `ConfigurationPresetIdentityRetry` | 会话与配置身份、重试上限、过期结果隔离 | 内存后端与真实线程池；包含九种故意破坏保护逻辑的负对照 |
| `AutomaticRetryOwner` | 恢复所有权、预算耗尽、A→B→A 与新会话边界 | 从源码提取实际方法，协作者使用内存夹具 |
| `ConsumerRecovery` | 各调用方的恢复路由、失败边界和持有状态 | 完整产品源码，恢复接口使用内存回调 |
| `TimerRetryBoundary` | 计时器请求、取消、启动身份与有限恢复 | 临时内存设置与模拟计时器接口 |
| `TamerRetry` | 后台扫描失败后的等待与事件唤醒边界 | 实际循环方法、真实时钟和事件，扫描及进程端点为夹具 |
| `SecurityStartup` | 一次性安全软件检查、缓存、超时与子进程退出失败 | 系统提供程序由固定输入替代，子进程由夹具持有 |
| `SourceCleanup` | 自动动作状态翻译、键盘导航与动画准入 | 产品源码、内存设置与未显示的控件，不启动应用入口 |

各入口也可以单独运行，例如：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/Test-ConfigurationPresetIdentityRetry.ps1 -Mutations
```

## CPU 与内存台架

独立台架位于 [tools/CpuMemoryBench](../tools/CpuMemoryBench/README.md)。`probe` 检查拓扑与能力，`smoke` 在自有线程上验证计量及亲和性恢复；符合指定 P/E 拓扑的设备还可运行 `pilot` 和完整受控实验。需要 Python 3，测试不会安装驱动或启动 Pavise。

回归检查的通过次数说明对应断言被执行，不能当成游戏帧率收益或所有硬件兼容性的证明。独立台架测量固定负载；实际游戏效果应在相同硬件、驱动、场景与后台负载下另行对照。GitHub 的公开检查工作流运行构建及上述回归，不自动运行硬件性能实验。

源码采用项目的 [GPL-3.0-only](../LICENSE) 许可证，版权与第三方声明见 [NOTICE](../NOTICE)。
