# Present / 唤醒协同实验

这是可运行的调度原型和反例测试，不是正式游戏优化功能。测试只创建并调整自己的线程、隐藏 D3D9 窗口和独立 ETW 会话，不修改游戏、驱动、注册表、电源计划或其他应用。

## 自研路线

1. 配对 DXGI / D3D9 的成功 Present 开始、结束事件，取得实际提交线程。CPU 时间最多的线程仅作为旧算法对照。
2. 从 CSwitch 重建每个 CPU 当前运行的线程，再关联 ReadyThread 的目标线程，寻找同进程中经常唤醒提交线程的工作线程。当前 Windows 的经典 ReadyThread 头部线程 ID 为 `-1`，直接使用它会识别失败。
3. 只在自有夹具中，把提交线程和发现的工作线程同时设为 AboveNormal。旧 CPU 耗时策略把最大耗时线程设为 Highest。测试结束核对原值恢复。

`group` 使用已知工作线程，是上限对照；`wake-group` 必须通过事件自动发现它，并核对夹具的真实线程 ID，不能直接使用已知 ID 代替发现。当前只接受单处理器组，所有被提优线程都属于本测试进程。

## 运行

使用已有的 Zig 或兼容 Windows C++ 编译器。示例在项目根目录执行：

```powershell
$labDir = Join-Path $env:TEMP 'Pavise-PresentLaneLab'
New-Item -ItemType Directory -Path $labDir -Force | Out-Null
zig c++ -target x86_64-windows-gnu -std=c++17 -O2 tools\PresentLaneLab\PresentLaneLab.cpp -o "$labDir\PresentWakeLaneLab.exe" -ld3d9 -ladvapi32 -luser32 -lole32
& tools\PresentLaneLab\Run.ps1 -BenchPath "$labDir\PresentWakeLaneLab.exe" -IncludeWakeGroup -Rounds 3 -Seconds 6
python tools\PresentLaneLab\Analyze.py '<RUN_DIRECTORY>'
& tools\PresentLaneLab\Check-Policy.ps1
```

脚本通过正常 UAC 启动隐藏测试控制器，输出其 PID 和结果目录。默认三轮，共 54 次测试；每次 6 秒测量，另有采样和预热，总计约 8 分钟。对照程序模拟 PAVISE 的 High 进程优先级。失败立即停止，原始 CSV、标准错误和失败原因保留。

每个场景的候选被两个新基线夹住，交替反转测试顺序。开始前声明基线速率漂移上限为 10%；某场景任一轮超过此限，就不输出该场景的汇总提升比例，仍保留线程归属和零进展窗口。该门槛只是本实验的质量要求，不是统计显著性检验。

结果目录包含源码与测试二进制快照、源码及二进制 SHA256、机器信息、逐次 CSV、聚合数据、电源计划前后检查与分析 JSON。原始数据不写入仓库；停止控制器前应等当前子进程退出，它有独立 30 秒看门狗，只能终止自己。`Check-Policy.ps1` 使用临时配置运行 AUTO 策略目录及旧线程通道恢复回归，不启动正式守护。

## 结论边界

- 循环次数和循环间隔不是游戏 FPS、显示帧时间或输入延迟；隐藏窗口运行在当前默认显示适配器，不代表 RTX 独显游戏负载。
- 同逻辑 CPU 争抢是有意构造的反例，不代表所有真实游戏。CPU 耗时法也并非在所有负载下变慢；旧 C# 夹具在这台电脑上甚至更快。
- 采样在测量前结束，因此收益数据不包含长期监测的开销。正式接入前还需计算全系统调度事件采集的成本、处理多处理器组与线程重建、连续性与反作弊兼容、游戏切换和崩溃恢复。
- 唤醒关系是依赖候选，不证明完整关键路径；睡眠、锁、GPU 等待和多个任务池需要分别验证。没有将试验算法默认开启或宣称跨游戏收益。
- `LockRescueLab.cpp` 保存了未通过的 WCT 路线：夹具可能先等待普通事件，接口无法返回该事件的所有者。它不能证明锁救援有效，不能作为正式调度器使用。

事件布局参照 [Microsoft ReadyThread](https://learn.microsoft.com/en-us/windows/win32/etw/readythread)、[Microsoft CSwitch](https://learn.microsoft.com/en-us/windows/win32/etw/cswitch)，运行时 Present 定义参照 [Intel PresentMon 的 ETW 定义](https://github.com/GameTechDev/PresentMon/tree/main/PresentData/ETW)。WCT 支持的同步对象见 [Microsoft 文档](https://learn.microsoft.com/en-us/windows/win32/debug/wait-chain-traversal)。
