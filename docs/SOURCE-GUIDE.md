# 源码与验证入口

Pavise 使用 .NET Framework 和 Windows Forms。`build.cmd -b prod` 从 `src/` 递归编译 C# 源码，用 MSVC x64 和 Windows SDK 链接内置原生组件，并核验版本和资源。构建过程不会启动 Pavise。

## 模块入口

| 位置 | 职责 |
| --- | --- |
| [`src/App/Program/`](../src/App/Program/) 和 [`src/App/Lifecycle/`](../src/App/Lifecycle/) | 单实例、启动、托盘、退出和应用生命周期 |
| [`src/Game/Mode/Core/`](../src/Game/Mode/Core/) 和 [`src/Game/Mode/Detection/`](../src/Game/Mode/Detection/) | 对局会话、策略、恢复编排和游戏识别 |
| [`src/Game/Mode/Background/`](../src/Game/Mode/Background/) 和 [`src/Background/`](../src/Background/) | 后台治理、保护规则、Job 管理和恢复 |
| [`src/Scheduling/`](../src/Scheduling/) 和 [`src/Game/Mode/Threads/`](../src/Game/Mode/Threads/) | 游戏线程调度、核心范围和亲和性 |
| [`src/Frame/`](../src/Frame/) 和 [`src/Native/`](../src/Native/) | 帧采集、ETW 前置处理和 CPUID 探测 |
| [`src/Power/`](../src/Power/) 和 [`src/Tuning/`](../src/Tuning/) | 电源计划、计时器和系统调优 |
| [`src/UI/`](../src/UI/) 和 [`src/Localization/`](../src/Localization/) | 窗口、游戏库、界面动效和本地化 |
| [`src/Infrastructure/Recovery/`](../src/Infrastructure/Recovery/) 和 [`src/Update/`](../src/Update/) | 恢复预算、失败记录、更新和卸载 |

同名 `partial` 文件属于同一类型，按职责放在各自目录中。表中的路径相对于仓库根目录。

## 原生构建与回归

- 正式构建读取 [ETW 镜像](../src/Interop/NativeEtwImage.cs)和 [CPUID 镜像](../src/Interop/NativeCpuImage.cs)。修改原生源码后，用 Zig 0.15.2 运行 [`tools/Build-Native.ps1`](../tools/Build-Native.ps1)生成镜像。可将 `zig.exe` 加入 PATH，或使用 `-Zig` 指定其路径。
- 公开回归由 [`tools/Run-PublicTests.ps1`](../tools/Run-PublicTests.ps1) 统一运行。测试范围和单项命令见[测试说明](../tests/README.md)。
