# Pavise 游戏台架（GameBench）

用一个自有的、可复现的 3D 负载当"游戏"，在真机上测 Pavise 对游戏的影响，并检查各项功能是否真的生效、退局是否原样还原。
前身是自动模式时期的 CalibrationBench（自动模式已砍），被测程序本身未改，只加了 `--uncapped`（可撕裂、不限帧）。

## 目录

| 文件 | 作用 |
| --- | --- |
| `Bench.cpp` `BenchCommon.hpp` `Dx11.cpp` `Dx12.cpp` | 被测程序 BenchGame.exe（DX11/DX12，原生 C++） |
| `build.ps1` | 用 Zig 编译 BenchGame.exe |
| `BenchProbe.cs` | 探针：每 25 ms 采样 Pavise / BenchGame / 守护进程的每线程 CPU 周期与切换次数，每秒记录游戏的调度属性 |
| `BenchNoise.cs` | 可控的后台噪声进程（CPU 占用、可选 IO） |
| `Run-PaviseBench.ps1` | 跑一次实验：Pavise 开/关/保持，噪声，探针，可见的 BenchGame，截取 Pavise 日志，再调用分析 |
| `Analyze-BenchRun.ps1` | 帧统计（平均、1%/0.1% Low、卡顿）、Pavise 精确 CPU、卡顿时 Pavise 是否在忙，写 `summary.json` |
| `StallTrace.cs` | 内核追踪分析：渲染线程被切走的区间、卡顿帧内的 DPC/ISR 与驱动名、某驱动 DPC 的时刻表 |
| `SysSnap.cs` `Snapshot-System.ps1` | 系统状态快照：相关注册表、服务、电源方案/滑块/请求、计时器、计划任务、QoS、每个进程的调度属性 |
| `Compare-Snapshots.py` `Run-FeatureCheck.ps1` | 开局前 / 对局中 / 退局后三次快照对比，得出"生效了什么、哪些没还原" |
| `Validate.ps1` | 被测程序自检与隐藏冒烟（不出硬件结论） |

## 构建

```powershell
# BenchGame.exe（需要完整的官方 Zig 发行包，测过 0.16.0 与 0.15.2）
./tools/GameBench/build.ps1 -ZigPath "$env:TEMP\Pavise-Zig-0.16.0\zig-x86_64-windows-0.16.0\zig.exe"
# 探针、噪声、快照、追踪分析（.NET Framework 4 自带的 csc）
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
& $csc -nologo -optimize+ -platform:x64 -out:"$env:LOCALAPPDATA\PaviseBenchProbe\BenchProbe.exe" tools\GameBench\BenchProbe.cs
& $csc -nologo -optimize+ -platform:x64 -out:"$env:LOCALAPPDATA\PaviseBenchNoise\BenchNoise.exe" tools\GameBench\BenchNoise.cs
& $csc -nologo -optimize+ -platform:x64 -r:System.ServiceProcess.dll -out:"$env:LOCALAPPDATA\PaviseSysSnap\SysSnap.exe" tools\GameBench\SysSnap.cs
& $csc -nologo -optimize+ -platform:x64 -out:"$env:LOCALAPPDATA\StallTrace\StallTrace.exe" tools\GameBench\StallTrace.cs
```

`Run-PaviseBench.ps1` 默认在 `C:\Game\PaviseBench\BenchGame.exe` 找被测程序；把它加进 Pavise 游戏库（单独一个配置）后 Pavise 才会接管。

## 分阶段计时版 Pavise

把 `build.cmd` 里的 `-define:PAVISE_STABILITY_COMPARISON` 换成 `-define:PAVISE_STABILITY_COMPARISON;PAVISE_LOOP_PROFILE`
编出的 Pavise 每 20 秒写一行 `LOOP`：主循环各阶段的 CPU/墙钟、注册表读写次数（按键名）、线程池回调耗时（`span:`）、GC 次数，
并把长寿线程的名字和系统 TID 写进日志（`THREAD 名字 tid=…`），探针据此按线程归因。正式构建里这些调用全部被编译器去掉。

## 常用实验

```powershell
cd tools\GameBench
# Pavise 开/关各一次，不限帧
.\Run-PaviseBench.ps1 -Label on  -Seconds 60 -Pavise on  -Uncapped
.\Run-PaviseBench.ps1 -Label off -Seconds 60 -Pavise off -Uncapped
# CPU 被 12 个噪声线程占满时
.\Run-PaviseBench.ps1 -Label busy -Seconds 60 -Pavise on -Uncapped -NoiseThreads 12 -NoiseDuty 100
# 全功能生效/还原检查（Pavise 须已在运行）
.\Run-FeatureCheck.ps1 -Label competitive -Seconds 100
```

找卡顿元凶：一边跑台架一边用提权的 `logman` 抓内核追踪（进程、线程、模块、上下文切换、DPC、ISR，`-ct perf`），
然后 `StallTrace.exe trace.etl BenchGame 3 x --frames <run>\bench\frames.csv` 逐个卡顿帧列出当时的切换和中断；
台架帧时间与追踪时间戳都是原始 QPC，可以直接对齐。

## 被测程序

```text
BenchGame.exe --api dx11|dx12 --scene main|workers|gpu|mixed|stream|memory|startup|switch|wake|thermal
  --seconds N --seed N --width N --height N --output <新的运行目录> --nonce <id>
  [--uncapped] [--background-load none|cpu|io|gpu] [--hidden] [--warp] [--self-test]
```

主进程另起四个同名子进程（cpu/io/gpu/ui 角色），只有 `--background-load` 选中的角色做持续负载。可见运行必须拿到真正的前台，
失去前台会立即以退出码 4 结束。`--uncapped` 在支持时用 `DXGI_PRESENT_ALLOW_TEARING` 不限帧呈现，manifest 里记 `tearing`。

| 场景 | 负载 |
| --- | --- |
| `main` | 城市场景，逐物体视锥剔除与单独提交，固定相机路线 |
| `workers` | 最多 8 个任务线程，8192 个刚体，每帧 12 步积分 |
| `gpu` | 每像素 32 次材质光照迭代 |
| `mixed` | 任务图加 12 次着色迭代 |
| `stream` | 每帧读取、解压并上传固定大小的资源块 |
| `memory` | 按可用内存/显存预算轮换工作集与纹理 |
| `startup` | 资源生成、着色器与管线初始化直到首帧 |
| `switch` | 每两秒在渲染窗口与自有的桌面响应窗口之间切换前台 |
| `wake` | 60 Hz 睡眠/唤醒节奏 |
| `thermal` | 持续的物理、着色与资源读取 |

`frames.csv`：`sequence,qpc,frame_ms,cpu_ms,gpu_ms,latency_ms,valid,phase`。`frame_ms` 是相邻两帧开始时刻之差（不是显示节奏），
`cpu_ms` 是提交 GPU 之前的模拟与剔除，`gpu_ms` 来自 GPU 时间戳查询。只用 `phase=foreground` 的行算 FPS/Low；第 0 行是边界记录，不计入统计。

局限：只有 DX11/DX12，没有真实的显示/输入延迟、音频连续性和真实游戏资源；`completed=1` 只表示负载跑完，不代表任何优化有效。
