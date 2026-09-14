# 双显卡协作台架

Direct3D 12 写的 让核显帮独显干活 的可行性台架 同一份固定负载跑两种模式

- single 场景和后处理都在独显上
- heterogeneous 独显算场景 通过跨适配器共享缓冲和共享 fence 交给核显做后处理

测的是离屏计算吞吐 没有交换链 不碰游戏 传输路径 `--transfer-path direct|copy` 核显输入 `--secondary-input shared|local`

## 编译

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File ./tools/HeterogeneousGpuBench/build.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File ./tools/HeterogeneousGpuBench/build.ps1 -ZigPath C:/path/to/zig.exe
```

产物在 %TEMP%/Pavise-HeterogeneousGpuBench-bin 可用 -OutputDirectory 改 会一起编出 D3D12 台架 跨适配器探针和早期的 D3D11 原型 build-manifest.json 记着编译器和各文件哈希

## 运行

```powershell
$bench = Join-Path $env:TEMP "Pavise-HeterogeneousGpuBench-bin/Pavise.HeterogeneousGpuBench.D3D12.exe"
& $bench --list-adapters
& $bench --self-test
& $bench --validate-only --primary-index 1 --secondary-index 0 --transfer-path copy --secondary-input shared
& $bench --run --primary-index 1 --secondary-index 0 --transfer-path copy --secondary-input shared --precondition-seconds 30
```

先 --list-adapters 确认哪个编号是独显哪个是核显 默认 1920x1080 每阶段热 120 帧测 600 帧 12 个阶段按 ABBA BAAB 交替 --run 无效退出 2 NO_BENEFIT 和 CANDIDATE 都退出 0

Run-ControlledBench.ps1 包一层 检查构建清单和哈希 看 CPU 键鼠 供电和 N 卡温度 结果放在唯一的 results/<时间>-<用例>-<后缀> 目录

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File ./tools/HeterogeneousGpuBench/Run-ControlledBench.ps1 -BenchPath $bench -PrimaryIndex 1 -SecondaryIndex 0 -CaseName copy-screening
```

-ValidateOnly 只查像素不计时 -AllowBackgroundLoadScreening 把 CPU 空闲门槛从 20% 放到 50% 结果会标成 background_loaded_screening

产出 frames.csv phases.csv summary.json validation.json report.txt

算候选收益要 计算吞吐和 wall 吞吐中位数都至少 +2% 1% low 至少 +1% 三分之二以上的配对不退步 再过漂移和像素校验 规则细节看 TEST-PROTOCOL.md RTX 3090 加 UHD 770 那轮结果在 RESULT-20260817.md 实验登记在 EXPERIMENT-20260827.json 和 EXPERIMENT-20260827-LOCAL-INPUT.json
