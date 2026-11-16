param([string]$OutputDirectory, [switch]$Mutations)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $OutputDirectory) { $OutputDirectory = Join-Path ([IO.Path]::GetTempPath()) ('Pavise-DpiScaling-' + [guid]::NewGuid().ToString('N')) }
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Use a fresh output directory; earlier results are retained.' }
$null = New-Item -ItemType Directory -Path $output
$utf8 = New-Object Text.UTF8Encoding($false)
$manifest = @('src/PaviseApp/Dpi.cs', 'tests/DpiScalingChecks.cs', 'tools/Test-DpiScaling.ps1') | ForEach-Object {
    $original = Join-Path $workspace $_
    $copy = Join-Path $output ([IO.Path]::GetFileName($_))
    Copy-Item -LiteralPath $original -Destination $copy
    [pscustomobject]@{ RelativePath=$_; Original=$original; Frozen=$copy; SHA256=(Get-FileHash -LiteralPath $copy -Algorithm SHA256).Hash }
}
$product = Join-Path $output 'Dpi.cs'
$fixture = Join-Path $output 'DpiScalingChecks.cs'
$source = [IO.File]::ReadAllText($product)
$framework = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
$results = New-Object 'Collections.Generic.List[object]'

function Run-Checks([string]$name, [string]$expectedFailure) {
    $exe = Join-Path $output ($name + '.exe')
    $compileLog = Join-Path $output ($name + '-compile.log')
    $arguments = @('-nologo', '-noconfig', '-nostdlib+', '-target:exe', '-platform:x64', '-optimize+', '-codepage:65001', '-warn:4', ('-out:' + $exe))
    $arguments += @('mscorlib.dll', 'System.dll', 'System.Core.dll', 'System.Drawing.dll') | ForEach-Object { '-reference:' + (Join-Path $framework $_) }
    $arguments += @($product, $fixture)
    & $compiler @arguments *> $compileLog
    if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath $compileLog; throw ('Fixture compilation failed: ' + $name) }
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName=$exe; $start.WorkingDirectory=$output; $start.UseShellExecute=$false; $start.CreateNoWindow=$true; $start.WindowStyle='Hidden'
    $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
    $start.StandardOutputEncoding=$utf8; $start.StandardErrorEncoding=$utf8
    $child = [Diagnostics.Process]::Start($start)
    try {
        $stdout=$child.StandardOutput.ReadToEndAsync(); $stderr=$child.StandardError.ReadToEndAsync()
        if (-not $child.WaitForExit(15000)) { $child.Kill(); throw ('Owned fixture timed out without retry: ' + $name) }
        $child.WaitForExit()
        $text=$stdout.Result+$stderr.Result
        [IO.File]::WriteAllText((Join-Path $output ($name + '-result.txt')), $text, $utf8)
        $expectedPass = [string]::IsNullOrEmpty($expectedFailure)
        if (($child.ExitCode -eq 0) -ne $expectedPass -or (-not $expectedPass -and $text.IndexOf($expectedFailure, [StringComparison]::Ordinal) -lt 0)) {
            Write-Output $text
            throw ('Unexpected behavior assertion: ' + $name)
        }
        $results.Add([pscustomobject]@{Name=$name; Passed=$expectedPass; ExitCode=$child.ExitCode; ExpectedFailure=$expectedFailure; Detected=(-not $expectedPass)})
        if ($expectedPass) { Write-Output $text.Trim() } else { Write-Output ('DETECTED ' + $name) }
    } finally { $child.Dispose() }
}

try {
    Run-Checks 'production' ''
    if ($Mutations) {
        # 每个负对照只修改冻结副本，必须编译成功并在指定行为断言处失败。
        $cases = @(
            @{Name='old-primary-screen-cap'; Find='Rectangle area = currentWorkingArea;'; Replace='Rectangle area = Screen.PrimaryScreen.WorkingArea;'; Failure='大副屏不能被小主屏封顶'},
            @{Name='old-client-screen-cap'; Find='upperBound = Math.Min(upperBound, RequestedScaleFor(currentDpi));'; Replace='upperBound = Math.Min(upperBound, ScaleFor(currentDpi));'; Failure='明确客户区不能再套用缓存屏幕上限'},
            @{Name='ignore-custom-scale'; Find='return (float)scalePreferencePercent / 100f;'; Replace='return 1f;'; Failure='加载手动比例'},
            @{Name='accept-invalid-step'; Find='percent >= 100 && percent <= 300 && percent % 25 == 0'; Replace='percent >= 100 && percent <= 300'; Failure='非法保存值没有回到自动: 101'},
            @{Name='forget-custom-preference'; Find='scalePreferencePercent = percent;'; Replace='scalePreferencePercent = 0;'; Failure='非法手动比例改变偏好'},
            @{Name='remove-client-custom-cap'; Find='upperBound = Math.Min(upperBound, RequestedScaleFor(currentDpi));'; Replace='upperBound = Math.Min(upperBound, MaxScale);'; Failure='明确客户区不能再套用缓存屏幕上限'},
            @{Name='ignore-preview-nofit'; Find='if (NoFit || fitW <= 0 || fitH <= 0) return scale;'; Replace='if (fitW <= 0 || fitH <= 0) return scale;'; Failure='预览模式错误地套用了显示器适配'},
            @{Name='same-dpi-hides-area-change'; Find='return previousDpi != currentDpi || previousArea != currentWorkingArea;'; Replace='return previousDpi != currentDpi;'; Failure='同 DPI 不同工作区未被识别'},
            @{Name='refresh-overwrites-fitted-scale'; Find='return previousDpi != currentDpi || previousArea != currentWorkingArea;'; Replace='Scale = ScaleFor(currentDpi); return previousDpi != currentDpi || previousArea != currentWorkingArea;'; Failure='相同环境刷新覆盖客户区适配'},
            @{Name='invalid-dpi-replaces-valid'; Find='if (dpi > 0) currentDpi = dpi;'; Replace='currentDpi = dpi;'; Failure='两个读取均失败却修改环境'}
        )
        foreach ($case in $cases) {
            if ([regex]::Matches($source, [regex]::Escape($case.Find)).Count -ne 1) { throw ('Mutation anchor must match once: ' + $case.Name) }
            [IO.File]::WriteAllText($product, $source.Replace($case.Find, $case.Replace), $utf8)
            Run-Checks $case.Name $case.Failure
        }
    }
} finally {
    [IO.File]::WriteAllText($product, $source, $utf8)
}
$drift = @($manifest | Where-Object { (Get-FileHash -LiteralPath $_.Original -Algorithm SHA256).Hash -ne $_.SHA256 } | Select-Object -ExpandProperty RelativePath)
$verification = [pscustomobject]@{Passed=$drift.Count -eq 0; Results=$results.ToArray(); SourceManifest=$manifest; SourceDrift=$drift; ProductStarted=$false; VisibleWindows=0; NativeWrites=0; RegistryWrites=0; Boundary='Real Dpi.cs; Screen/Native/Settings endpoints replaced with deterministic in-memory inputs'}
$verification | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'verification.json') -Encoding UTF8
if ($drift.Count) { throw 'Sources changed during the checks; results do not describe the current workspace.' }
Write-Output ('OUTPUT ' + $output)
