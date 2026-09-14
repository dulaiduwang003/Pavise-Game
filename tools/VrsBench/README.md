# DX12 VRS 台架

离屏跑的 Direct3D 12 小程序 同一份重像素着色负载 分别用 1x1 1x2 2x1 2x2 着色率跑 用 GPU 时间戳比耗时 没窗口 没交换链 不碰游戏

## 编译

```powershell
powershell -ExecutionPolicy Bypass -File ./build.ps1 -ZigPath C:/path/to/zig.exe
```

装了 Visual Studio C++ 生成工具也行

## 运行

```powershell
Pavise.VrsBench.exe --output-dir C:/Temp/VrsResult
```

可选 `--width 1600` `--height 900` `--cycles 14` `--idle-ms 10` VrsBench.vs.cso 和 VrsBench.ps.cso 要和 exe 放一起 程序用低优先级跑 每个样本之间歇一下
