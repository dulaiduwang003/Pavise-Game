# 热预算交换 Demo

给 CPU 和独显共用功耗散热预算的笔记本做的实验 游戏 GPU 受限时 把 CPU 最大性能状态往下压 看 GPU 频率和帧时间会不会更稳 只用临时电源方案 GPU 引擎性能计数器 DxgKrnl Present ETW 和可选的 NVML 不碰游戏进程 不装驱动 不改显卡参数

## 编译

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File ./tools/ThermalExchangeDemo/build.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File ./tools/ThermalExchangeDemo/build.ps1 -ZigPath C:/path/to/zig.exe
```

产物在 %TEMP%/Pavise-ThermalExchange-bin 有 Pavise.ThermalExchangeDemo.exe Pavise.ThermalExchange.FieldTest.exe 和离屏 D3D9 负载 Pavise.ThermalGpuLoad.exe

## 给外面的人测

Pavise.ThermalExchange.FieldTest.exe 是单文件 Win32 程序 双击过一次 UAC 选真正画画面的游戏进程 点开始 先 100% 热 30 秒 再按 100/95/100/90/100/85/100/85/100/90/100/95/100 跑 每档稳 5 秒测 20 秒 每个降档和左右两个 100% 配对 结论三种 数据无效 没有重复收益 候选收益要复验 报告和 CSV 在 exe 旁边的 Pavise-ThermalExchange-Results 里

用游戏自带 benchmark 循环 关垂直同步 帧率上限和动态分辨率 笔记本插电

达标线 GPU 3D 平均占用 80% 以上 100% 基线漂移不超过 5% 候选档两次 FPS 都不降 平均至少 +1% 两次 1% Low 不比相邻基线低 1% 以上

## 命令行

```powershell
Pavise.ThermalExchangeDemo.exe --probe --seconds 10
Pavise.ThermalExchangeDemo.exe --self-test
Pavise.ThermalExchangeDemo.exe --run --pid 1234 --stage-seconds 30 --settle-seconds 8
Pavise.ThermalExchangeDemo.exe --run --pid 1234 --caps 100,95,90,95,100
```

--probe 只读遥测 --self-test 复制再删一份临时电源方案 不激活 --run 才激活并轮转 CPU 上限 默认 100,95,90,85,90,95,100 每秒样本和每档汇总写到当前目录的 Pavise-ThermalExchange-日期-时间.csv 正常结束 Ctrl+C 或崩溃都由看门狗还原原方案 断电的话下次启动读 %TEMP%/PaviseThermalExchange.recovery 清残留
