# 渲染进程交接台架

查两件事的正确性 逐游戏的 压制家族后台 隔离和默认值 以及 误选启动器 再确认真实渲染进程 再把游戏库换成单入口 这条交接链 不测性能

```powershell
./tools/RendererHandoffBench/Run-Bench.ps1 -Repeat 3
./tools/RendererHandoffBench/Run-ResetChecks.ps1
```

要 .NET Framework x64 的 C# 编译器 不用管理员 不启动 Pavise 每次在唯一的 results/run-日期-随机值/ 里编译和存产物 编 src 下全部源码 本台架和渲染 家族相关的那几份专项自测 定义 PAVISE_SELFTEST;PAVISE_RENDERER_BENCH 入口 PaviseApp.RendererHandoffBench

查的内容 状态机和恢复 候选归属 协调器 三组学习 用临时的真 EXE 走安装范围和家族发现 用假快照查进程亲缘 临时 V5 库上的逐游戏策略隔离 家族保护集合 假 GPU 样本下的观察标签 还有用真实夹具进程 GameLauncher GameRenderer OldRenderer 跑完整交接 Standard Competitive Handheld 三档 配家族压制开关 配启动器或 learned 锚 一共 12 种

夹具用内存设置 Settings.UseTransientStoreForCurrentProcess 台架自己的临时库 辅助进程最多活 40 秒 产出 REPORT.md results.json console.log stderr.log production-decision.log 加 source-hashes.json fixture-hashes.json verification.json

Run-ResetChecks.ps1 用临时目录 内存设置和模拟的还原 注册表接口 查 停止确认 系统还原 删文件 清注册表 这个顺序 文件被占 只读文件 便携版 目录链接 停机后的异步写入各查一遍
