# 测试与独立台架

下表列出已在 3.3.1.3 源码上验证的公开回归测试。每项测试编译对应的产品源码，通过内存设置、受控回调或测试创建的子进程验证行为。尚待适配的历史测试未列入表中。

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
| `DpiScaling` | 界面缩放、屏幕边界与缩放偏好兼容 | 实际缩放代码，屏幕、DPI 与设置使用内存替身，不显示窗口 |
| `SmartGovernanceThreshold` | 智能场景负载阈值、持续时间与退出滞回 | 固定输入与受控时钟，不采集实际 CPU 负载 |
| `BackgroundGovernanceRuntime` | 正常／激进后台治理、提优限制与原值恢复 | 内存回调；原生提优往返仅作用于测试进程自身 |
| `AutomaticRetryOwner` | 恢复所有权、预算耗尽、A→B→A 与新会话边界 | 从源码提取待测方法，用内存夹具替代其依赖 |
| `ConsumerRecovery` | 各调用方的恢复路由、失败边界和持有状态 | 完整产品源码，恢复接口使用内存回调 |
| `TimerRetryBoundary` | 计时器请求、取消、启动身份与有限恢复 | 临时内存设置与模拟计时器接口 |
| `SecurityStartup` | 一次性安全软件检查、缓存、超时与子进程退出失败 | 系统提供程序由固定输入替代，子进程由夹具持有 |
| `SourceCleanup` | 自动动作状态翻译、键盘导航与动画准入 | 产品源码、内存设置与未显示的控件，不启动应用入口 |
| `ChineseLogs` | 中文日志写入、新旧格式兼容、会话归属、告警级别及进程名和路径保留 | 产品源码、临时内存设置与测试目录日志，不启动应用或原生工作线程 |

各入口也可以单独运行，例如：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/Test-DpiScaling.ps1 -Mutations
```

## CPU 与内存台架

独立台架位于 [tools/CpuMemoryBench](../tools/CpuMemoryBench/README.md)。`probe` 检查拓扑与能力，`smoke` 在自有线程上验证计量及亲和性恢复；符合指定 P/E 拓扑的设备还可运行 `pilot` 和完整受控实验。需要 Python 3，测试不会安装驱动或启动 Pavise。

回归结果对应各测试的断言和运行环境。台架使用固定负载。评估游戏帧率时，应固定硬件、驱动、游戏场景和后台负载，在实际游戏中做对照。GitHub 的公开工作流只运行构建和上述回归检查。

源码采用项目的 [GPL-3.0-only](../LICENSE) 许可证，版权与第三方声明见 [NOTICE](../NOTICE)。
