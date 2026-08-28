# Pavise 异构 GPU 研发台架

这是“让核显帮助独显”的隔离可行性台架，不是 Pavise 正式功能，也不会修改现有游戏。

当前台架使用 Direct3D 12 显式创建两个 GPU 设备，并运行同一套固定工作量：

- `single`：场景生成与后处理都在高性能 GPU 上完成；
- `heterogeneous`：高性能 GPU 生成场景，通过 `SHARED_CROSS_ADAPTER` 缓冲区和共享 fence 把结果交给低功耗 GPU，再由低功耗 GPU 完成后处理。

异构模式把已实现的共享缓冲区、跨适配器同步、排队等待和复制开销计入 GPU 完成节奏与阶段 wall FPS。提供 `direct`（场景直接写共享资源）和 `copy`（场景先写主 GPU 本地资源，再复制到共享资源）两条路径，分别测量。

辅助 GPU 的输入另由 `--secondary-input shared|local` 控制，默认 `shared`，与上述主 GPU 传输路径是两个独立选项：`shared` 直接从共享缓冲区执行后处理；`local` 先在辅助 GPU 上把共享数据复制到普通 `DEFAULT` 缓冲区，再执行相同后处理。辅助端复制、屏障和后处理的全部成本计入 `post_gpu_ms`、完成吞吐和 wall 吞吐，不能只比较复制之后的着色器时间。

对 UHD 630 这样的 UMA 核显，`local/DEFAULT` 是资源分配方式，不代表获得了独立显存。它用于检验一次复制能否减少后续重复读取共享资源的代价；这只是待验证假设，复制也可能让结果更慢。

**当前只测 compute-stage 吞吐，不是游戏显示 FPS。** 程序不捕获游戏、不创建交换链、不调用 `Present`；单卡输出留在主 GPU，双卡输出留在辅助 GPU，未包含辅助 GPU 到实际显示适配器的完整回传。它验证的是“应用主动支持多适配器时，某类后处理是否有候选收益”，不能证明外部优化器可以无侵入地拆分任意游戏渲染任务。

## 安全边界

- 不注入游戏，不打开游戏进程，不安装驱动，不改注册表、显卡设置或电源策略。
- 不带参数时只打印计划，不创建 D3D 设备，也不运行负载。
- `Pavise.D3D12CrossAdapterProbe.exe` 只创建设备并验证跨适配器共享堆、缓冲区和 fence，不创建命令队列、不绘制、不计分。
- `--self-test` 只用合成数据验证统计与判定逻辑，不创建 D3D 设备。
- `--validate-only` 会运行有限的 GPU 像素正确性检查，不计性能分数。
- 只有显式传入 `--run` 才进行性能测量；`--run` 的正确性校验必须通过。

## 构建

推荐 Visual Studio C++ Build Tools：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\HeterogeneousGpuBench\build.ps1
```

也支持便携 Zig（显式传入时优先使用，不要求另装 Windows SDK）：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\HeterogeneousGpuBench\build.ps1 -ZigPath C:\path\to\zig.exe
```

无 SDK 时使用 Zig 自带的 MinGW 头文件与导入库，链接 `d3dcompiler_47`；有完整 SDK 导入库时可使用它们。Windows x64 上已验证 Zig 0.14.1 可在无 MSVC/Windows SDK 的环境编译三个目标。构建脚本不下载或安装工具，也不会启动产物。

默认产物位于 `%TEMP%\Pavise-HeterogeneousGpuBench-bin`，可用 `-OutputDirectory` 指定。包括 D3D12 台架、D3D12 能力探针和保留用于历史对照的 D3D11 原型；对象文件和 Zig 缓存也限制在输出目录，不写入仓库根目录。

`build-manifest.json` 保存编译器路径/版本、每个输入 Cpp 与目标 EXE 的 SHA-256、UTC 构建时间。只有 `status: success` 可用于正式测试；编译失败或源码在编译期间改变会拒绝成功状态。测试归档时保存该清单，并核对实际运行的文件哈希。

## 使用

查看计划、执行 CPU 自测、列出适配器：

```powershell
$bench = Join-Path $env:TEMP "Pavise-HeterogeneousGpuBench-bin\Pavise.HeterogeneousGpuBench.D3D12.exe"
& $bench
& $bench --self-test
& $bench --list-adapters
```

必须根据 `--list-adapters` 的名称和 LUID 确认独显/核显，不能猜测 `0/1` 的含义。2026-08-27 本机已核对的组合为主 GPU `1`（RTX 2070 Max-Q）到辅助 GPU `0`（UHD 630）；下例沿用这组编号，其他机器需重新核对：

```powershell
$primaryIndex = 1
$secondaryIndex = 0
```

受控实验建议从包装器启动，保留环境、构建证据和独立结果目录：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\HeterogeneousGpuBench\Run-ControlledBench.ps1 -BenchPath $bench -PrimaryIndex $primaryIndex -SecondaryIndex $secondaryIndex -CaseName copy-screening
```

包装器默认 `copy / scene100 / post4 / 1080p`，预运行 10 秒、每阶段预热 120 帧、测量 600 帧、3 个交错块。它先校验成功构建清单及源码/EXE 哈希，再检查 CPU、键鼠空闲、供电与 NVIDIA 温度，并记录可用的功耗/时钟。默认性能预检要求连续三个 CPU 样本不超过 20%。`-ValidateOnly` 只做不计时的正确性检查，因此不要求 CPU 低占用，但键鼠、AC、温度和超时守护仍生效。GPU engine 活动仅在计时前后快照，不在每帧运行重型枚举。

包装器的辅助输入参数为 `-SecondaryInput shared|local`，默认 `shared`；它会将选择写入请求，并核对台架 summary 顶层和 config 中的对应值。

当前受控包装器**必须读取到有效 NVIDIA 温度**：遥测缺失、非数值/NaN、查询超时或查询失败时，拒绝启动或停止自有台架，不在失去温度保护后继续运行。恢复输入、失去 AC、NVIDIA 温度达到默认 83°C 或超过整轮时限也只停止自有台架，不关闭远控/后台程序。温度保护仅覆盖 NVIDIA，不覆盖 CPU/核显，不是整机热安全保证。

如果测前已明确记录后台负载无法排除，可以显式选择**带后台负载的探索性筛查**，不能把它当成自动降低标准的重试：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\HeterogeneousGpuBench\Run-ControlledBench.ps1 -BenchPath $bench -PrimaryIndex $primaryIndex -SecondaryIndex $secondaryIndex -CaseName copy-loaded-screening -AllowBackgroundLoadScreening
```

该开关只把性能预检的 CPU 上限从 20% 调到 50%，正确性、计时、漂移、收益、键鼠、AC、温度与超时门槛不变。结果标记为 `environment_regime: background_loaded_screening`，**不得宣称闲置电脑收益**；即使出现正候选，也须在安静环境独立确认。`environment_valid` 仅表示已观察的守护通过，并不意味着后台 GPU 负载已受控制或机器无噪声。

下列直接调用适合局部排错，不包含上述环境守护；不要在守护失败后用它绕过保护：

只做能力探测：

```powershell
& (Join-Path $env:TEMP "Pavise-HeterogeneousGpuBench-bin\Pavise.D3D12CrossAdapterProbe.exe") --primary-index $primaryIndex --secondary-index $secondaryIndex
```

确认列表中的适配器身份后，先做 GPU 像素正确性检查：

```powershell
& $bench --validate-only --primary-index $primaryIndex --secondary-index $secondaryIndex --transfer-path copy --secondary-input shared
```

新增辅助 GPU 局部输入路径必须在新构建上单独校验，不能沿用旧 `shared` 校验结果；`direct/local` 和 `copy/local` 都需通过。局部输入的校验读取辅助 GPU 实际使用的 `DEFAULT` 输入缓冲区，而非只读回复制前的共享源。

执行性能测试的示例（适配器编号按本机列表替换）：

```powershell
& $bench --run --primary-index $primaryIndex --secondary-index $secondaryIndex --transfer-path copy --secondary-input shared --precondition-seconds 30
```

默认使用 1920×1080、每阶段预热 120 帧、测量 600 帧，按 ABBA/BAAB 顺序交替 12 个阶段。这是短筛查配置；正式确认需冻结参数、增加采样并独立重复。`--precondition-seconds` 默认 0，示例显式增加预运行；它不能保证机器已热稳定，也不能清除后台干扰。

用户不动鼠标并不等于机器空闲：远控/桌面绘制、录屏、后台 CPU/GPU 工作仍可能影响结果。不得静默结束远控或服务器程序；受扰数据应标记并完整复验。

本次四用例矩阵及后台负载筛查的测前修订记录于 [EXPERIMENT-20260827.json](EXPERIMENT-20260827.json)。首次严格空闲尝试没有启动 GPU；它不能算作一轮性能数据。当前组合的资源探针以及 1080p `100/4` 下 `direct/copy` 三 seed 像素预检均已通过，但正确性通过不等于已经证明性能收益。

原四用例均为 `NO_BENEFIT`。后续局部输入对照的假设、固定矩阵和停止条件已在测前登记到 [EXPERIMENT-20260827-LOCAL-INPUT.json](EXPERIMENT-20260827-LOCAL-INPUT.json)：保持 `direct / post4 / 1080p`，分别在 `scene100`、`scene600` 比较 `shared/local`，并在同一新二进制上重跑 shared 对照。不能只比旧双卡实现快就称为加速，仍须相对各自单卡基线通过原有全部门槛。原 C++/runner/EXE/manifest 已归档在 `results/build-original-7443C580`，不得用新构建覆盖历史证据。

[RESULT-20260817.md](RESULT-20260817.md) 是旧机器 RTX 3090 + UHD 770 的历史结果，不是本次机器/驱动/程序的测量。旧测试存在其他电脑操作，其结论待受控复验。

原 D3D11 原型保留用于回溯历史失败路径：旧环境在 RTX 3090 创建资源、UHD 770 调用 `OpenSharedResource1` 时返回 `E_INVALIDARG`，不使用该失败路径计分。

性能测试结果包含：

- `frames.csv`：每帧 GPU 完成间隔及两个 GPU 的执行时间；
- `phases.csv`：全部阶段的统计与顺序；
- `summary.json`：schema 2，明确区分 `INVALID`、`NO_BENEFIT` 和 `CANDIDATE`；
- `validation.json`：固定多 seed 的全图像素比较、CPU 参考抽样和陈旧输出检查；
- `report.txt`：便于人工检查的摘要。

局部输入扩展保持 schema 2：`secondary_input` 写入 `summary.json` 顶层/config 与 `validation.json`，并作为 CSV 末列附加。分析时必须同时核对 `transfer_path`、`secondary_input`、二进制哈希与环境标签；不要把旧版缺少该字段的结果当作局部输入测量。

受控包装器另外在唯一的 `results/<时间>-<用例>-<随机后缀>` 目录保存 `request.json`（含构建清单）、前后环境、`telemetry.json`、stdout/stderr 和 `controlled-result.json`；原生台架文件位于其 `bench` 子目录。每次启动前在 `artifact-snapshot` 中备份实际源码、EXE、runner 和原始构建清单，便于后续代码变化后回溯。后台 GPU 负载未受控制、没有测量游戏显示 FPS、不可直接实装，都会明确记录。

`--run` 无效退出 2，有效的 `NO_BENEFIT` 和 `CANDIDATE` 都退出 0，不能把退出 0 当成获得收益。`--validate-only` 成功只表示像素检查通过，没有性能 verdict。

候选门槛包括计算与 wall 中位吞吐均至少 `+2%`、1% low 至少 `+1%`、至少 2/3 局部配对不退步，以及漂移/计时一致性/正确性要求。所有结果都标记 `scope: owned_offscreen_compute_only`、`product_ready: false`；即使候选通过，仍须完成实际工作和最终显示链路验证才能实装。完整协议见 [TEST-PROTOCOL.md](TEST-PROTOCOL.md)。
