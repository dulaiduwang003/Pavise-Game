# 中断外场探测器

只读的 ETW 采集小程序 看一局游戏里中断压力落在哪 采 Present ISR DPC 上下文切换和内核映像 要管理员权限 因为内核 ETW 会话需要

## 给测试的人

1. 先开游戏 进训练场或回放这种能重复的场景
2. 管理员运行 Pavise.InterruptFabric.FieldTest.exe 列表里选真正画画面的那个游戏进程 别选启动器和反作弊
3. 点开始 程序会缩小 正常玩三分钟 别切出去 别同时开别的跟踪工具
4. 跑完把 exe 旁边 Pavise-InterruptFabric-结果-* 文件夹里三个文件都发回来 报告 逐秒 CSV 事件 CSV

帧从 DXGI D3D9 的 PresentStart 和 DxgKrnl 的 PresentHistoryStart 两路采 哪路更全用哪路 中途关掉程序 ETW 会话一起停

## 自测

`Pavise.InterruptFabric.FieldTest.exe --self-test` 用假数据过一遍分析和输出 不提权 不开 ETW
