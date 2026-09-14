# 导航检查

主窗口导航的界面回归 侧栏 分类切换 搜索 返回和 Escape 内页标签 滚动 重建 键盘激活 淡入遮罩 背景图渲染

```powershell
./tools/NavigationBench/Run.ps1
./tools/NavigationBench/Run-LibraryChecks.ps1
```

Run.ps1 编到临时目录 把真实主窗口建在屏幕外 设置只存进程内 游戏库是临时的 调优本体 游戏监测和压制线程都不启动 中英文明暗主题截图和 100% 到 300% 的侧栏布局检查随日志一起存

Run-LibraryChecks.ps1 查添加游戏对话框里已安装和运行中合并的那份列表 发现顺序 去重 实时刷新 键盘操作 图标绘制 白名单选择器的异步结果和滚动条 全部用假候选 不弹窗 日志在 %TEMP% 下
