# Pavise DX12 VRS Bench

独立的纯离屏 DirectX 12 Variable Rate Shading 台架。它不加载游戏、不访问游戏进程、不注入 DLL，也不创建可见窗口。

台架用 GPU 时间戳比较同一套重像素着色负载在 `1x1`、`1x2`、`2x1`、`2x2` 着色率下的耗时。它回答的是“本机硬件执行这类像素负载时，VRS 是否真的减少 GPU 时间”，不代表具体游戏收益，也不能证明图形 DLL 对反作弊安全。

## 构建

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1 -ZigPath C:\path\to\zig.exe
```

也支持已安装 Visual Studio C++ Build Tools 的环境。

## 运行

```powershell
Pavise.VrsBench.exe --output-dir C:\Temp\VrsResult
```

可选参数：

- `--width 1600`
- `--height 900`
- `--cycles 14`
- `--idle-ms 10`

构建目录中的 `VrsBench.vs.cso` 和 `VrsBench.ps.cso` 需要与 EXE 放在一起。

程序是 Windows 子系统 EXE，无控制台和弹窗。默认以低于正常的进程/线程优先级运行，并在每个短 GPU 样本之间让出时间，减少对桌面工作的影响。
