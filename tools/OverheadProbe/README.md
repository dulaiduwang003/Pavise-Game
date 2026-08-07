# OverheadProbe —— Pavise 自身开销测量工具链

用来回答一个问题：**Pavise 自己吃不吃性能？** 全部是黑盒外部测量，不依赖 Pavise 内部埋点。

## 工具

| 工具 | 作用 |
|---|---|
| `OverheadProbe.cs` | 采样目标进程的 CPU/线程/句柄/工作集。用 `NtQuerySystemInformation` 读，**不需要打开进程句柄**，所以对提权进程也有效 |
| `ThreadProbe.cs` | 按**线程**拆解 CPU 与上下文切换，定位开销归属到具体线程 |
| `HotspotBench.cs` | 微基准：量化 `Process.GetProcesses()`、逐进程句柄查询、`Logger.Log`、`EnumWindows` 的单次成本 |
| `SnapshotBench.cs` | 验证单次 `NtQuerySystemInformation` 能否替代逐进程句柄查询，并交叉校验每个字段 |
| `IoOffsetCheck.cs` | 校验 `SYSTEM_PROCESS_INFORMATION` 的 IO 计数器偏移与 `GetProcessIoCounters` 一致 |
| `FrameProbe.cs` | 假游戏：全屏窗口 + 稳定渲染循环，统计帧间隔分布（p99/p99.9/max/卡顿数）。这是"吃不吃游戏性能"的真正判据 |
| `DetectDiag.cs` | 诊断窗口证据：前台/可见/全屏覆盖率 |
| `ProfileDiag.cs` | 诊断游戏档案解析与成员判定，定位"为什么没识别到游戏" |

## 编排脚本

- `RegisterProbeGame.ps1 -Action add|remove` —— 把帧探针注册成 Pavise 的游戏档案（**追加**，不覆盖已有档案，且自动备份）
- `RunActiveTest.ps1 -PaviseExe <exe> -Label <名字>` —— 完整跑一轮"激活态"测量
- `RestoreEnvironment.ps1` —— 还原测试对本机的全部改动

## 编译

```
csc -target:exe -optimize+ -out:OverheadProbe.exe -reference:System.dll -reference:System.Core.dll OverheadProbe.cs
csc -target:winexe -optimize+ -out:FrameBench.exe -reference:System.dll -reference:System.Drawing.dll -reference:System.Windows.Forms.dll -reference:System.Core.dll FrameProbe.cs
```

其余同理，单文件独立编译。

## 坑

- **.ps1 必须纯 ASCII**，除非带 BOM。PowerShell 用 ANSI 代码页解无 BOM 的脚本，中文会把语法打碎（和 `build.cmd` 同一个坑）。
- **测量提权进程不能用 `Process.Handle`**，普通权限拿不到。用 `NtQuerySystemInformation` 绕过。
- 测激活态前先确认注册表 `HKCU\Software\Pavise\GameModeOn = 1`，否则 Pavise 根本不会激活，测出来的是空转数据。
- 别用 `Stop-Process -Force` 杀 Pavise：会残留 `Global\Pavise_Exit` 内核对象，下次启动抛 `UnauthorizedAccessException`。用退出事件优雅关闭。
