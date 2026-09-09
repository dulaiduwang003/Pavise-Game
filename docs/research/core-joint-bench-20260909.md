# 核心隔离 / 核心分配 / 设备中断 联合台架

执行日期：2026-09-09。执行机：13th Gen Intel Core i9-13900K，Windows 10 19045。

## 执行约束

本轮在**非管理员**、**不允许出现任何窗口**的前提下执行，两条约束都影响覆盖范围：

- 不提权。核心隔离的系统范围写入（`NtSetSystemInformation(168)`）要求管理员，提权必然弹 UAC，因此**真实系统写入未执行**。隔离部分只跑到逻辑层。
- 不跑 `tools/IrqLoadBench/Run.ps1`。它的 `IrqCoreLoadUiChecks.cs:128` 会 `form.Show()`（位置在屏幕外 `-20000,-20000`），仍是创建真实窗口。IRQ 覆盖改由主自检的 22 个 Irq* 套件加本台架承担。
- 全程未启动 Pavise 正常运行时。`GameMode` 只构造不 `Start()`，运行时循环在 `Start()` 里。设置走 `UseTransientStoreForCurrentProcess()`，不读写用户真实配置。

## 实测拓扑

先前的隔离回归全部跑在硬编码的假拓扑上（`IsolationFixture` 里 `Physical = {3, 12, 48, 192}`，4 颗物理核、全 SMT、8 逻辑）。本机是混合架构，与该假设差距很大：

| 项 | 值 |
| --- | --- |
| 逻辑 CPU | 32（`all=0xFFFFFFFF`） |
| 物理核 | 24 |
| P 核 | 8 颗带 SMT，`perf=0xFFFF`（16 lp） |
| E 核 | 16 颗无 SMT，`eff=0xFFFF0000`（16 lp） |
| CPU0 所在物理核 | `0x3`，宽度 2 |
| `IsolationSupported` | true |

**这是隔离逻辑第一次在混合架构、单核宽度不一致、32 位宽掩码的真实拓扑上运行。**

## 覆盖与结果

`tools/CoreJointBench/`，40 项检查，连续 5 轮全部通过（累计 200 项，0 失败）。

### [1] 拓扑自洽
物理核掩码互不重叠且并集等于全机；混合机器同时暴露 SMT 与非 SMT 两种核宽。

### [2] 计划校验（真实拓扑）
`CoreScheduling.Validate` 的每条规则在 24 核拓扑上逐条验证：整颗物理核、半颗 SMT 核拒绝、CPU0 所在核拒绝、越界位拒绝、空闲物理核少于两颗拒绝、**正好两颗放行**（边界不能连正确方案一起拒）、隔离开但范围空拒绝、平台不支持拒绝、拓扑戳过期拒绝。

### [3] 隔离引擎（真实拓扑 + 假平台，无系统写入）
`Begin → Allow → Audit → Restore` 全流程。写入序列为：

```
journal -> journal -> process:FFFFFFFF -> journal -> system:FFFFFFC3 -> system:FFFFFFFF -> journal
```

先落收据再写进程准入，再落"准备写系统"阶段，最后才写系统范围——与 `docs/core-scheduling.md` 的退出恢复约定一致。隔离 `0x3C` 时系统允许范围写成 `0xFFFFFFC3`（全机减去隔离核）。另验证 PID 复用不继承准入、外部已有分配时拒绝启用且一个字节都不写。

### [4] 联合：分配 + 中断（真实子进程）

真实子进程 8 线程空转，用 `GetCurrentProcessorNumber()` 采样自己实际落在哪些逻辑 CPU 上。

| 阶段 | 硬亲和性 | 实测落核 |
| --- | --- | --- |
| 手动分配到 `0x3C`（2 颗 P 核） | `0x3C` | `0x14` / `0x3C`（各轮不同，均为子集） |
| 撤中断观测（`includeManual=false`） | `0x3C` 不变 | 仍在 `0x3C` 内 |
| 撤手动放置（`includeManual=true`） | 回到 `0xFFFFFFFF` | 29–32 个逻辑 CPU |

关键结论：**撤销中断观测不会解除手动放置**，两套恢复归属确实分离；完全恢复后进程重新跑满全机。另验证对已有受限亲和性的进程拒绝扩大其范围，且拒绝时不改动原值。

落核数各轮有抖动（8 线程在 1.2 秒内不保证踩遍每个允许的 CPU），但每轮都是允许集合的子集，无越界。

## 未覆盖（本轮）与既有证据

**隔离的真实系统写入**本轮未执行，需要管理员。但它并非空白——`build\` 里留有 2026-09-08 的执行记录，可直接引用：

`core-isolation-live.txt`（17:46）：

```
ordinary observed=FFFFFFCF   admitted observed=30   isolated=30
PASS isolation and admission
PASS original system state restored
```

隔离范围 `0x30`（CPU 4、5，一颗 P 核）。普通进程实测跑遍 `0xFFFFFFCF`——全机减去这两个 CPU，**与隔离范围交集为空**；被准入的进程只落在 `0x30` 上。

`core-isolation-integration.txt`（18:28）：

```
PASS production game placement + isolation: ordinary=FFFFFFCF game=30 isolated=30
PASS observation independence, live process admission restoration and original affinity restoration
PASS isolation rolls back when exact game placement cannot be confirmed
PASS parent crash: independent helper restored system and live child admission
PASS helper crash: durable receipt recovered by live client
PASS all isolation integration checks; system state restored
```

所以"普通进程默认避开隔离区"在一次真实执行中做到了零进入。仍需注意 `docs/core-scheduling.md` 的既有限定：这不是安全边界，硬亲和性绑死的进程、内核中断与 DPC、有特权的任务都可能进入，界面不承诺 0% 占用。单次实测样本也不构成对所有负载的承诺。

## 发现：子进程 stdin 命令在自动化环境下不送达

`CoreIsolationIntegrationRunner` 的采样协议依赖父进程 `StandardInput.WriteLine("go")`，子进程 `Console.ReadLine()` 收命令后采样。在本轮的自动化执行环境（无真实控制台的非交互会话）中，**该命令不送达**：

```
ready=READY
cpu_before=0.015625
cpu_after=0.015625   ← 发送 go 并等待 3 秒后，子进程 CPU 时间纹丝不动
```

子进程一直阻塞在 `Console.ReadLine()`。改用裸流写无 BOM ASCII 并显式 Flush 同样不通。原版 runner 与本台架表现一致，说明不是本台架特有。

影响：在这类环境下跑 `-LiveIsolation`，`PlacementFixture` 会卡在 `Result(ordinary)` 直到 6 秒超时，隔离集成整体失败——失败点在测试脚手架，不在被测功能。

**这是执行环境差异，不是项目缺陷。** 2026-09-08 的 `core-isolation-integration.txt` 全部通过，而它的采样正是走 `StandardInput.WriteLine("go")`，说明交互式终端（有真实控制台）下该通道正常。两者的区别在于本轮的宿主会话没有真实控制台。

结论：`-LiveIsolation` 应在交互式管理员终端里跑，不要放进无控制台的自动化流水线；若将来要上 CI，采样协议需改成不依赖 stdin。

规避方式已在本台架验证可行：子进程不等命令，自己按节奏连续采样并持续写 stdout，父进程丢掉横跨改动时刻的那一行、取下一行。完全不依赖 stdin。

## 复现

```
csc -target:exe -platform:x64 -optimize+ -codepage:65001 ^
    -define:PAVISE_SELFTEST;PAVISE_SELFTEST_RUNNER ^
    -main:PaviseApp.CoreJointBench -out:build\Pavise.jointbench.exe ^
    -recurse:src\*.cs -recurse:tests\*.cs tools\CoreJointBench\CoreJointBench.cs
build\Pavise.jointbench.exe build\core-joint-bench.txt
```

需要管理员的那半：

```
powershell -NoProfile -File tools\Test-CoreScheduling.ps1 -LiveIsolation
```

## 边界

本台架验证的是掩码计算、状态机、恢复归属和进程实际落核，**不测帧数、不测延迟、不产出性能提升结论**。落核正确不等于游戏体验变好。
