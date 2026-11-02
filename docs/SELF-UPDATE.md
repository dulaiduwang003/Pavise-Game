# Pavise 软件内更新

客户端从 3.2.0.2 开始包含内置更新器，保持免安装形式。已发布的 3.2.0.1 及更早版本需要先手动升级一次。

## 用户流程

在“关于 → 检查与更新”检查正式版本。用户点击更新后下载完整 EXE，显示下载字节和百分比；校验阶段单独显示，不将下载 100% 视为更新完成。

下载和校验可安全取消；操作结束前主窗口、托盘退出和普通窗口关闭受限。文件替换开始后不能取消。系统关机、任务管理器终止或断电不能由窗口关闭锁保证阻止，事务会保留未完成的恢复记录。

自动更新失败时，同一个弹窗切换为“手动更新”，解除关闭限制，提供夸克下载入口及 GitHub Release。夸克入口指向官方下载页，用户从该页选择网盘；客户端不抓取网盘私有接口。网络失败文案使用“部分地区或网络环境可能无法访问”，不把所有超时都认定为地区限制。

只有完成安全退出回执并确认原进程身份和退出后才替换程序。新版完成运行服务初始化、验证版本、EXE 哈希和启动任务后回执确认，更新器再删除旧 EXE。不会清空游戏库、白名单、偏好、报告或系统恢复台账。

安装验证期间，新启动的主进程也保持退出锁，直到更新器确认完成或切换手动更新后才解除。若上次更新中断且更新器已退出，下次启动会提示保留的恢复状态并提供手动更新；未完成的文件替换事务不会被下一次更新覆盖。

当前协议只更新 EXE，并保留现有 `.exe.config`；运行时要求或伴随文件格式需要不兼容变更时，发布新的更新器协议并引导旧客户端手动更新。

## 发布签名清单

正式元数据是 `https://pavise.club/updates/stable.json`，备用为相同路径的官网 Workers 域名。旧 `version.json` 保持现有兼容字段，只更新其版本值；不能用新清单替换旧格式。

新清单为仅含 `payload` 和 `signature` 的 JSON。`payload` 是无 BOM UTF-8 JSON 字节的 Base64；`signature` 是对原始 payload 字节的 RSA-3072 PKCS#1 v1.5 SHA-256 签名。公钥固定在 `SelfUpdateManifest.TrustedPublicKeyXml`；生产更新器不接受调用方提供的任意公钥。

载荷字段为 `schema`、四段 `version`、EXE 字节数 `size`、小写 SHA-256 `sha256`、`minUpdater`、UTC `expiresUtc` 和 `urls`。拒绝重复键、未知字段、过期签名、降级、超大文件、错误版本、非 HTTPS 或不允许的下载地址。

当前允许的发行文件位置：

- `https://pavise.club/updates/<version>/Pavise.exe`
- `https://pavise-website.2074055628.workers.dev/updates/<version>/Pavise.exe`
- `https://github.com/dulaiduwang003/Pavise-Game/releases/download/v<version>/Pavise.exe`

GitHub 的资产跳转只接受明确列出的官方资产域名。镜像失败重新下载；下载后和独立更新器执行前均验证签名、大小、哈希与程序集版本，不执行下载文件做版本探测。

## 创建发布材料

```powershell
build.cmd -b dev build\release\Pavise.exe
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools\New-SelfUpdateRelease.ps1 `
  -Executable build\release\Pavise.exe
```

先创建构建输出目录。发布脚本会核对 3.2.0.2 的源码、程序集和文件版本，生成新输出目录，包含 `updates/<version>/Pavise.exe`、`updates/stable.json`、元数据和手动下载 ZIP。默认清单有效期 90 天，可用 `-ValidityDays` 指定；长期没有新版时，应使用同一私钥重新签发清单。

生产私钥位于仓库外 `%USERPROFILE%\.codex\pavise-release-signing\update-private.xml`，文件与目录不继承权限，只允许当前发布用户、Administrators 和 SYSTEM。必须备份这把私钥；客户端发布后不能随意换一把新密钥。该密钥用于更新清单，与 Windows EXE 发行者证书是不同机制。不要将私钥上传到下载源、打进发行包或提交仓库。

先上传并验证带版本号的 EXE、GitHub 发行附件及网盘包，再发布签名 `stable.json`，最后更新旧 `version.json` 的版本字段。发布过的同版本文件保持不变；有修复时增加正式版本，避免清单缓存与文件内容不一致。

此工具只生成材料，不上传或修改线上发布。官网必须配置对应直链及合适的缓存；`stable.json` 使用短缓存或重新验证，带版本号的 EXE 可以长期缓存。未部署有效签名清单时，客户端按预期切换到手动更新。

## 事务与自启动

事务目录为 `%ProgramData%\PaviseAuto.Update\<用户 SID>\<随机 ID>`，由 Administrators/SYSTEM 持有写权限，禁止重解析路径。主程序复制自身作为临时独立更新器，以旧进程 PID、创建时间、路径、哈希和随机 nonce 绑定事务，取得 helper 就绪回执后才开始停止运行服务。

原目录 EXE 通过同目录暂存和备份替换；自启动哈希目录创建新版，不在旧哈希目录原地改写。原目录来源从首次支持更新的手动启动开始记录；历史托管副本没有可靠来源时不扫描硬盘猜测旧下载位置。原任务 XML 的启用/禁用状态保持，查询状态未知则停止更新。

旧版本、临时程序和下载内容在新版启动确认后清理；启动失败只在确认新版进程已退出后恢复旧程序和任务。新版仍活跃但未回执时保留恢复资产并转手动，不强杀进程或覆盖正在运行的新版。程序回滚不覆盖系统恢复台账。

## 隔离验证

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools\Test-SelfUpdateManifest.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools\Test-SelfUpdateUi.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools\Test-SelfUpdateInstaller.ps1
```

测试使用独立目录与隐藏或屏幕外窗口。文件事务检查使用编译限定的替身，不运行真实 Pavise 主入口、不改用户计划任务或注册表。完成代码更改后构建并核验身份，当前运行实例仅在用户明确要求时重启。

下载检查通过仅限测试编译的传输映射，把逻辑官网/GitHub HTTPS 请求连接到本机动态端口，仍实际执行 HTTP 请求、跳转检查、取消、超时、镜像重下与文件校验；不访问公网。生产模式另行编译确认不存在传输、公钥或超时注入入口。

下载测试的候选程序集与 URL 版本根据当前 `App.Version` 自动递增，只修改隔离测试输出，不修改正式版本。另行验证同版本和旧版本在建立下载连接前即被拒绝。

官网源码位于本机 `C:\Code\Pavise-Game\website`，静态构建会把 `public/updates/` 原样复制到 `dist/updates/`。`public/_headers` 为更新资源设置 `Cache-Control: no-store`，签名清单使用 JSON 类型，EXE 使用二进制附件类型。正式发布时将发布工具生成的 `updates/` 目录复制到 `website/public/updates/`，构建、验证后再部署；仅修改旧 `version.json` 不会补齐自动更新。

网站联调使用已通过安装器检查的候选 EXE 和临时签名清单，复制到独立网站预览目录，通过 Wrangler 本机静态资源服务实际下载，检查响应头、签名、大小、SHA-256、程序集版本和进度阶段。只在测试编译中使用临时公钥和 loopback 传输，不运行下载的 EXE、不连接生产更新地址，也不部署测试清单。先在官网目录执行 `npm run check`，再运行：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools\Test-SelfUpdateWebsite.ps1 `
  -WebsiteDirectory C:\Code\Pavise-Game\website `
  -InstallerFixtureDirectory <Test-SelfUpdateInstaller.ps1 输出目录>
```

真实 HTTP 回归覆盖了一个曾遗漏的问题：`InvalidDataException` 必须作为可尝试下一可信镜像的错误，否则主源的坏清单、错误长度或坏哈希会提前终止所有下载源。安装器还在实际启动前重新核对目标 EXE 哈希，并持有禁止写入/删除的读句柄直到启动完成；进程回执采用有界共享读取，避免写者尚未关闭时偶发共享冲突。只有完整 nonce、进程身份和版本/哈希吻合才确认成功。

共享工作区有独立功能正在修改时，安装器检查可显式使用 `-DependencySnapshot <已验证的 PowerPlan.cs>`，仅在隔离测试副本替换该依赖并记录来源；这种混合依赖构建只用于更新链路验证，不能作为生产发行包。最终整包仍须从完整共享工作区构建验证。

## 2026-11-03 验证记录

- 清单与下载：174 项断言，30 个 HTTP 场景，其中 26 个使用真实本机 HTTP；覆盖签名、可信地址、长度/哈希/程序集版本、镜像回退、超时、取消及同版本/降级拦截。生产核心另行编译确认没有测试注入接口。
- 安装器：76 项断言，包含真实独立进程的替换、启动回执、旧文件清理，以及候选启动失败后的回滚；仅操作测试目录、测试注册表替身和任务替身。
- 更新界面：109 项检查，覆盖关闭锁、取消、失败后同窗手动下载，以及不同 DPI 下的居中与可读性，窗口位于私有桌面。
- 官网：114 个静态页面构建校验通过；Wrangler 本机联调以 `3.2.0.2 → 3.2.0.3` 测试候选完成下载、签名、文件验证、响应头和进度阶段检查，不执行候选。
- 线上只读检查：主站与备用站的旧 `version.json` 均为 `3.2.0.1`，签名清单 `/updates/stable.json` 均返回 404。本次未部署或发布新版本，也未重启用户正在运行的 Pavise。
- 首轮检查时，约定的签名私钥路径在本机不存在，隔离测试使用临时密钥。确认 `3.2.0.2` 尚未对外发行后，首次正式发布前重新生成 RSA-3072 发行密钥并更新内置公钥；正式私钥仅保存在上述仓库外受限目录。发布材料已通过构建内置的生产验签器验证，测试密钥不参与正式发布。

公开发行目标为 `dulaiduwang003/Pavise-Game`。`Pavise2x` 是私有开发仓库，不用作用户下载地址；软件 GitHub 入口、签名载荷下载地址和官网链接均使用公开发行仓库。
