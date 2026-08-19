# Pavise Thermal Exchange Demo

这是“跨芯片热预算交换器”的隔离实验版。目标不是凭空加速 CPU，而是在 CPU 与独显共享整机功耗/散热预算、且游戏已经 GPU 受限时，试探性降低 CPU 最大性能状态，观察释放出的预算能否换来更稳定的 GPU 频率和帧时间。

它目前不接 Pavise 正式设置，也不会自动常驻。程序只使用系统级接口：临时电源方案、Windows GPU Engine 性能计数器、DxgKrnl Present ETW，以及可选的 NVIDIA NVML。它不打开游戏进程句柄、不读写游戏内存、不注入 DLL、不安装驱动，也不修改 GPU 参数。任何反作弊兼容性都不能作绝对保证，因此仍应先在无排名风险的场景验证。

## 安全边界

- `--probe` 只读遥测，不改变系统状态。
- `--self-test` 只复制并删除一个临时电源方案，绝不激活它。
- 只有显式运行 `--run` 才会激活临时方案并轮转 CPU 上限；若系统策略拒绝操作，程序会直接报错并恢复。
- 原电源方案的内容不被改写。正常结束、Ctrl+C 和主进程崩溃时，程序/独立看门狗会恢复原方案并删除临时方案。
- 系统断电无法让任何用户态清理代码运行；下次启动 Demo 会读取 `%TEMP%\PaviseThermalExchange.recovery` 并清理残留。临时方案本身只修改 CPU 最大性能百分比。

## 构建

推荐 Visual Studio C++ Build Tools（“使用 C++ 的桌面开发”工作负载）：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\ThermalExchangeDemo\build.ps1
```

默认输出到 `%TEMP%\Pavise-ThermalExchange-bin\Pavise.ThermalExchangeDemo.exe`，不污染仓库。同时生成仅用于无游戏时验收链路的 `Pavise.ThermalGpuLoad.exe`；它创建不激活的 D3D9 窗口并固定在屏幕外，不属于正式功能。
测试台架通过只读共享内存把自身帧时间交给 Demo；这条通道只对 Pavise 测试负载存在，普通游戏仍使用系统 ETW，绝不在游戏进程内布置采样代码。

也可使用便携 Zig 编译器，不必安装 IDE：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\ThermalExchangeDemo\build.ps1 -ZigPath C:\path\to\zig.exe
```

## 使用

### 给外部测试者的一键版

构建产物 `Pavise.ThermalExchange.FieldTest.exe` 是可单文件分发的 Win32 程序，不需要安装 .NET、VC++ 运行库、Python 或 AI。双击后接受一次 UAC，界面会列出当前有可见窗口的候选进程；选择真正负责渲染的游戏进程并开始即可。

标准协议固定为：

1. 100% CPU 上限预热 30 秒；
2. 按 `100/95/100/90/100/85/100/85/100/90/100/95/100` 运行；
3. 每档先稳定 5 秒，再测量 20 秒；
4. 每个 95/90/85 档分别和左右相邻的 100% 基线配对，抵消线性热漂移；
5. 自动检查帧时间、GPU 负载和基线漂移，并给出“数据无效 / 无重复收益 / 候选收益需复验”三类结论。

测试者应使用游戏内置 benchmark 循环；没有 benchmark 时固定人物与镜头，期间不要操作。关闭垂直同步、帧率上限和动态分辨率，笔记本必须接通电源。完成后把 EXE 同目录下 `Pavise-ThermalExchange-Results` 文件夹内同一时间戳的中文报告与 CSV 一起发回。

这套自动判定故意偏保守：GPU 3D 平均占用须至少 80%，100% 基线最大漂移不得超过 5%；同一候选档的两次 FPS 都不能下降、平均至少 +1%，两次 1% Low 均不得低于相邻基线 1% 以上。通过仍只代表“值得复验”，不直接证明普适收益。

先做只读探测：

```powershell
Pavise.ThermalExchangeDemo.exe --probe --seconds 10
```

再确认电源沙箱能安全创建和删除：

```powershell
Pavise.ThermalExchangeDemo.exe --self-test
```

实验时先启动并稳定游戏，记下真正负责渲染的进程 PID，再运行：

```powershell
Pavise.ThermalExchangeDemo.exe --run --pid 1234 --stage-seconds 30 --settle-seconds 8
```

默认顺序为 `100,95,90,85,90,95,100`。回程重复档位用来暴露热浸透和场景漂移；也可用 `--caps` 自定义，例如：

```powershell
Pavise.ThermalExchangeDemo.exe --run --pid 1234 --caps 100,95,90,95,100
```

每秒样本和每阶段汇总写到当前目录的 `Pavise-ThermalExchange-日期-时间.csv`。重点看：

- 平均 FPS 与 1% low 是否在两个方向的重复档位上都改善；
- GPU 3D 占用是否接近饱和，GPU 频率/功耗是否随 CPU 上限下降而上升；
- CPU 上限继续下降后 FPS 是否突然恶化，以此确定边界；
- 100% 的首尾结果是否明显漂移。若首尾差异很大，本轮不应下结论。

这不是“所有电脑都开”的优化。桌面机、CPU 受限游戏、独立散热设计以及 GPU 已经撞电压/频率上限的机器，理论上都可能没有收益甚至倒退。正式接入前应做 ABBA 多轮测试，并把“无稳定收益”作为默认判定。
