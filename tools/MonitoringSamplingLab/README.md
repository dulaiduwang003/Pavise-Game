# 监控采样研究工具

这些工具用于测量读取和采集成本，不改变 Pavise 的生产实现。所有运行器隐藏窗口；不操作已运行的 Pavise、RTSS 或外部采集器。

RTSS 集成与性能监测已下线。此目录的官方导出库和静态 IL 检查工具独立运行，不接入生产监测。

## 官方导出库

```powershell
.\tools\MonitoringSamplingLab\Run-Hardware.ps1 -OfficialProviderDirectory 'C:\path\LHMDataProvider'
```

目录必须包含同一官方 RTSS 版本的 `Exporter.dll`、`LibreHardwareMonitorLib.dll` 和 `HidSharp.dll`。不自动下载或安装这些组件；应先核对发布包与 DLL 哈希。

运行器编译 32 位独立进程，为每轮复制 DLL 并在私有目录写入 `Exporter.config`。顺序为默认、CPU＋GPU、仅 GPU、仅 GPU、CPU＋GPU、默认。每轮 7 次调用，首次作为初始化，后 6 次每秒一次。每个子进程最长 25 秒，并在结束时调用官方关闭路径。

该工具只允许非管理员令牌，避免安装硬件驱动。它确实读取硬件：没有 CPU 温度等权限受限通道时，结果不能代表管理员模式下的传感器成本。原生调用耗时不等于游戏 FPS 或完整输入延迟。

## 静态 IL 检查

`InspectExporter.cs` 可用系统 .NET Framework 编译器单独编译，参数为 `Exporter.dll` 的绝对路径。它使用 `ReflectionOnlyLoadFrom` 和方法体字节码，不执行被检查程序集。

运行结果写入独立输出目录。官方组件及本机测量输入不随源码分发。
