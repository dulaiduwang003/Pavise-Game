$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$cscPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$resultDirectory = Join-Path $env:TEMP ('Pavise-PromotionRegression-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $resultDirectory | Out-Null
$sources = @(Get-ChildItem -LiteralPath (Join-Path $repo 'src') -Recurse -File -Filter '*.cs' | Select-Object -ExpandProperty FullName)
foreach ($test in @('PerformancePromotionRegressionRunner.cs','SelfTests.PerformancePromotion.cs',
    'SelfTests.RenderLaneEligibility.cs','SelfTests.Automation.cs','SelfTests.ExtremeMode.cs','SelfTests.FrameCoordination.cs','SelfTests.LogSeverity.cs')) {
    $sources += Join-Path (Join-Path $repo 'tests') $test
}
$output = Join-Path $resultDirectory 'PromotionRegression.exe'
# 源文件清单写入响应文件，避免超过 Windows 命令行长度上限。
$response = Join-Path $resultDirectory 'sources.rsp'
[IO.File]::WriteAllLines($response, [string[]]@($sources | ForEach-Object { '"' + $_ + '"' }), (New-Object Text.UTF8Encoding($true)))
& $cscPath -unsafe+ -nologo -nowarn:0649 -target:exe -platform:x64 -optimize+ -codepage:65001 '-define:PAVISE_SELFTEST;PAVISE_PROMOTION_REGRESSION' -main:PaviseApp.PerformancePromotionRegressionRunner "-out:$output" -reference:System.dll -reference:System.Drawing.dll -reference:System.Windows.Forms.dll -reference:System.Core.dll -reference:System.Management.dll -reference:System.Xml.dll ('@' + $response)
if ($LASTEXITCODE -ne 0) { throw 'Policy regression compilation failed.' }
$lines = & $output 2>&1
$regressionExit = $LASTEXITCODE
$lines | Set-Content -LiteralPath (Join-Path $resultDirectory 'result.txt') -Encoding UTF8
$lines
if ($regressionExit -ne 0) { throw "Policy regression failed: $resultDirectory" }
"RESULT_DIRECTORY=$resultDirectory"
