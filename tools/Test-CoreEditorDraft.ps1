$ErrorActionPreference = 'Stop'
$taskWorkspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskOutput = Join-Path $taskWorkspace 'build\core-editor-draft-regression'
$null = New-Item -ItemType Directory -Path $taskOutput -Force
$taskSources = @(Get-ChildItem -LiteralPath (Join-Path $taskWorkspace 'src') -Recurse -Filter '*.cs' | ForEach-Object { $_.FullName })
$taskSources += Join-Path $taskWorkspace 'tests\CoreEditorDraftRegressionRunner.cs'
$taskCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$taskExe = Join-Path $taskOutput 'CoreEditorDraftTests.exe'
$taskCompileLog = Join-Path $taskOutput 'compile.log'
$taskResponse = Join-Path $taskOutput 'sources.rsp'
[IO.File]::WriteAllLines($taskResponse, [string[]]@($taskSources | ForEach-Object { '"' + $_ + '"' }), [Text.UTF8Encoding]::new($true))
& $taskCompiler -unsafe+ /nologo /nowarn:0649 /target:exe /platform:x64 /optimize+ /codepage:65001 '/define:PAVISE_SELFTEST;PAVISE_AUTO_BENCH;PAVISE_CORE_DRAFT_AUDIT' /main:PaviseApp.CoreEditorDraftRegressionRunner ('/out:' + $taskExe) /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Management.dll /reference:System.Xml.dll /reference:System.Web.Extensions.dll ('@' + $taskResponse) *> $taskCompileLog
if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath $taskCompileLog; throw 'Core editor draft test compilation failed' }
& $taskExe | Tee-Object -FilePath (Join-Path $taskOutput 'result.txt')
if ($LASTEXITCODE -ne 0) { throw 'Core editor draft tests failed' }
Write-Output ('OUTPUT ' + $taskOutput)
