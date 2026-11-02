$ErrorActionPreference = 'Stop'
$packageRepo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$packageSource = Join-Path $packageRepo 'build\Pavise-Auto-3.1.0.0-Overnight.exe'
$packageDesktop = [Environment]::GetFolderPath('Desktop')
$packageName = 'Pavise Auto 3.1.0.0 凌晨性能优化.exe'
$packageExe = Join-Path $packageDesktop $packageName
$packageZip = Join-Path $packageDesktop 'Pavise-Auto-3.1.0.0-凌晨性能优化.zip'
$packageStage = Join-Path $packageRepo ('build\overnight-package-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $packageStage
$packageVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($packageSource).FileVersion
if ($packageVersion -ne '3.1.0.0') { throw "Wrong production version $packageVersion" }
$packageStream = [IO.File]::OpenRead($packageSource)
$packagePe = [Reflection.PortableExecutable.PEReader]::new($packageStream)
try {
    $packageMetadata = [Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($packagePe)
    $packageTypes = @($packageMetadata.TypeDefinitions | ForEach-Object { $packageMetadata.GetString($packageMetadata.GetTypeDefinition($_).Name) })
    $packageMethods = @($packageMetadata.MethodDefinitions | ForEach-Object { $packageMetadata.GetString($packageMetadata.GetMethodDefinition($_).Name) })
    foreach ($packageExpected in @('FrameMemoryProtection','NativeYieldChild','HelperScope')) { if($packageExpected -notin $packageTypes) { throw "Missing production type $packageExpected" } }
    if ($packageTypes -contains 'SelfTests' -or $packageTypes -contains 'SelfYieldHelperChecks' -or $packageMethods -contains 'HelperScopeForTest') { throw 'Test seam found in production assembly' }
} finally { $packagePe.Dispose(); $packageStream.Dispose() }
$packageBytes = [IO.File]::ReadAllBytes($packageSource)
if ([Text.Encoding]::UTF8.GetString($packageBytes) -notmatch 'requestedExecutionLevel level="requireAdministrator"') { throw 'Administrator manifest missing' }
Copy-Item -LiteralPath $packageSource -Destination $packageExe -Force
Copy-Item -LiteralPath $packageSource -Destination (Join-Path $packageStage $packageName)
foreach ($packageLegal in @('LICENSE','NOTICE')) { Copy-Item -LiteralPath (Join-Path $packageRepo $packageLegal) -Destination (Join-Path $packageStage $packageLegal) }
$packageNotes = @'
Pavise Auto 3.1.0.0 凌晨性能优化

页面保护：内存受限时保护低优先级关键线程页面，退局恢复。
辅助避让：游戏硬核心范围与后台物理核明确分离时，让自有 PowerShell 使用后台核心。全核、重叠、未知或不适用时跳过。
命令快到超时期限时恢复原核心，给辅助任务留下完成机会。
保留外部修改，完善退出及异常恢复。

以上两项无需重启电脑，重新启动新版软件后按现有对局策略参与。
游戏核心方案仍由用户决定，软件不会为启用辅助避让而修改它。
内存充足、默认页面优先级或默认全核游戏，不保证触发这些优化。
此次没有测量真实游戏 FPS，合成循环吞吐不代表游戏帧率。
GPU 实时档、统一线程数量、统一处理器提示和统一解压没有加入。
'@
[IO.File]::WriteAllText((Join-Path $packageStage '更新说明.txt'),$packageNotes,[Text.UTF8Encoding]::new($false))
Compress-Archive -LiteralPath (Get-ChildItem -LiteralPath $packageStage -File | Select-Object -ExpandProperty FullName) -DestinationPath $packageZip -Force
$packageHash = (Get-FileHash -LiteralPath $packageExe -Algorithm SHA256).Hash
if ($packageHash -ne (Get-FileHash -LiteralPath $packageSource -Algorithm SHA256).Hash) { throw 'Desktop copy hash differs' }
$packageArchive = [IO.Compression.ZipFile]::OpenRead($packageZip)
try {
    if ($packageArchive.Entries.Count -ne 4) { throw 'Wrong package entry count' }
    $packageEntry = $packageArchive.GetEntry($packageName)
    if ($null -eq $packageEntry) { throw 'Executable missing from archive' }
    $packageEntryStream = $packageEntry.Open()
    $packageSha = [Security.Cryptography.SHA256]::Create()
    try { $packageEntryHash = [BitConverter]::ToString($packageSha.ComputeHash($packageEntryStream)).Replace('-','') }
    finally { $packageSha.Dispose(); $packageEntryStream.Dispose() }
    if ($packageEntryHash -ne $packageHash) { throw 'Archive executable hash differs' }
} finally { $packageArchive.Dispose() }
$packageReport = [pscustomobject]@{version=$packageVersion;exe=$packageExe;exeBytes=(Get-Item -LiteralPath $packageExe).Length;exeSha256=$packageHash;zip=$packageZip;zipBytes=(Get-Item -LiteralPath $packageZip).Length;zipSha256=(Get-FileHash -LiteralPath $packageZip -Algorithm SHA256).Hash;zipEntries=4;productionOnly=$true;adminManifest=$true;runtimeStarted=$false}
$packageReport | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $packageRepo 'build\candidate-lab-20261005\overnight-package-verification.json') -Encoding UTF8
$packageReport | ConvertTo-Json -Compress
