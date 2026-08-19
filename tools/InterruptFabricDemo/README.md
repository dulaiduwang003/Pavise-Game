# Pavise Interrupt Fabric 外场探测器

当前版本：FieldTest 1.2 / 协议 3。

这是 Interrupt Fabric 的第一阶段可行性测试，不是优化器。它只读取 Windows ETW：游戏 Present、ISR、DPC、上下文切换和内核映像，不注入游戏、不安装驱动、不修改电源/GPU/中断设置。

## 给测试者

1. 启动游戏，进入训练场、回放或可稳定重复的实战场景；不要在排位中首测。
2. 右键以管理员身份运行 `Pavise.InterruptFabric.FieldTest.exe`，在列表里选择真正渲染画面的游戏进程。不要选启动器、登录器、反作弊进程或网页助手。
3. 点击“开始 3 分钟测试”。程序会最小化；测试期间正常玩，不要切出游戏，也不要同时运行 WPR、xperf、LatencyMon 等系统跟踪工具。
4. 完成后，把程序旁 `Pavise-InterruptFabric-结果-*` 文件夹内的三个文件全部发回：报告、逐秒 CSV、事件 CSV。

帧源同时采集属于游戏进程本身的 DXGI/D3D9 Runtime PresentStart，以及用于 Vulkan/非标准运行时的 DxgKrnl PresentHistoryStart。程序按正式 180 秒区间内的有效帧间隔数量自动选择更完整的一路，避免稀疏 Runtime 事件覆盖真实连续帧流。如果报告仍显示“没有捕获到游戏帧”，它只会判定“当前呈现路径未获得兼容帧事件”，不会断言用户选错了进程；报告还会列出两路原始计数。

## 安全边界

- 只读采集，不实施优化。
- 会请求管理员权限，因为 Windows 内核 ETW 会话需要权限。
- 测试中关闭程序也会停止以本进程 ID 命名的 ETW 会话；不会留下电源计划或注册表改动。
- 这是未签名的研发版 EXE，Windows SmartScreen 可能提示未知发布者。
- 进程外系统跟踪通常比注入风险低，但无法替任何反作弊厂商作绝对兼容保证，首测应使用无排名风险场景。

## 开发自检

`Pavise.InterruptFabric.FieldTest.exe --self-test`

该模式不提权、不显示窗口、不启动 ETW，只用合成数据验证分析器和文件输出。
