# Pavise 异构 GPU 研发台架

这是“让核显帮助独显”的隔离可行性台架，不是 Pavise 正式功能，也不会修改现有游戏。

正式台架使用 Direct3D 12 显式创建两个 GPU 设备，并运行同一套固定工作量：

- `single`：场景生成与后处理都在高性能 GPU 上完成；
- `heterogeneous`：高性能 GPU 生成场景，通过 `SHARED_CROSS_ADAPTER` 缓冲区和共享 fence 把结果交给低功耗 GPU，再由低功耗 GPU 完成后处理。

异构模式把共享缓冲区、跨适配器同步、排队等待和传输开销计入 GPU 完成节奏与阶段 wall FPS。它验证的是“游戏或引擎主动支持多适配器时，某类后处理能否获益”，不能证明外部优化器可以无侵入地拆分任意游戏的渲染任务。

## 安全边界

- 不注入游戏，不打开游戏进程，不安装驱动，不改注册表、显卡设置或电源策略。
- 不带参数时只打印计划，不创建 D3D 设备，也不运行负载。
- `Pavise.D3D12CrossAdapterProbe.exe` 只创建设备并验证跨适配器共享堆、缓冲区和 fence，不创建命令队列、不绘制、不计分。
- `--self-test` 只用合成帧时间验证统计与判定逻辑，不创建 D3D 设备。
- 只有显式传入 `--run` 才运行 GPU 基准测试。

## 构建

推荐 Visual Studio C++ Build Tools：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\HeterogeneousGpuBench\build.ps1
```

也支持便携 Zig：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\HeterogeneousGpuBench\build.ps1 -ZigPath C:\path\to\zig.exe
```

默认产物位于 `%TEMP%\Pavise-HeterogeneousGpuBench-bin`，包括 D3D12 正式台架、D3D12 能力探针和保留用于对照失败路径的 D3D11 原型。构建脚本只编译，不会启动程序。

## 使用

只查看将要执行的协议：

```powershell
Pavise.HeterogeneousGpuBench.D3D12.exe
```

只做能力探测：

```powershell
Pavise.D3D12CrossAdapterProbe.exe --primary-index 0 --secondary-index 1
```

真正执行基准测试必须显式确认：

```powershell
Pavise.HeterogeneousGpuBench.D3D12.exe --run --primary-index 0 --secondary-index 1
```

默认使用 1920×1080、每阶段预热 120 帧、测量 600 帧，按 ABBA/BAAB 顺序交替 12 个阶段。可用 `--list-adapters` 查看编号，再用 `--primary-index` 与 `--secondary-index` 固定适配器。

结果目录包含 `frames.csv` 和 `report.txt`。首轮本机正式测试结果见 [RESULT-20260817.md](RESULT-20260817.md)。

原 D3D11 原型保留用于证明接口选择为什么重要：本机在 RTX 3090 创建资源、UHD 770 调用 `OpenSharedResource1` 时返回 `E_INVALIDARG`，因此它不能用于正式计分。

结果包含：

- `frames.csv`：每帧 GPU 完成间隔及两个 GPU 的执行时间；
- `report.txt`：便于人工检查的摘要。

判定默认要求异构模式的中位平均 FPS 至少提高 2%，1% low 至少提高 1%，且多数配对轮次不能倒退。完整实验纪律见 [TEST-PROTOCOL.md](TEST-PROTOCOL.md)。
