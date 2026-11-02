$ErrorActionPreference = 'Stop'
$overnightWorkspace = Split-Path $PSScriptRoot -Parent
$overnightOutput = Join-Path $env:TEMP ('Pavise-OvernightFeatureBoundary-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $overnightOutput
Push-Location $overnightWorkspace
try {
    $overnightCompiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
    $overnightExe = Join-Path $overnightOutput 'Pavise.overnight-boundary-check.exe'
    $overnightInputs = @(Get-ChildItem -LiteralPath (Join-Path $overnightWorkspace 'src') -Recurse -File -Filter '*.cs')
    $overnightInputs += Get-Item -LiteralPath (Join-Path $overnightWorkspace 'tests/OvernightFeatureBoundaryRunner.cs')
    $overnightBefore = @($overnightInputs | ForEach-Object { [pscustomobject]@{Path=$_.FullName;Sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash} })
    & $overnightCompiler -unsafe+ -nologo -target:exe -platform:x64 -optimize+ -codepage:65001 -warn:0 `
        '-define:PAVISE_SELFTEST;PAVISE_OVERNIGHT_FEATURE_BOUNDARY;PAVISE_STABILITY_COMPARISON' `
        -main:PaviseApp.OvernightFeatureBoundaryRunner ('-out:' + $overnightExe) `
        -reference:System.dll -reference:System.Core.dll -reference:System.Drawing.dll `
        -reference:System.Windows.Forms.dll -reference:System.Management.dll -reference:System.Xml.dll `
        '-recurse:src\*.cs' tests\OvernightFeatureBoundaryRunner.cs
    if ($LASTEXITCODE -ne 0) { throw 'Overnight boundary compilation failed.' }
    $overnightResult = & $overnightExe 2>&1 | Out-String
    $overnightExit = $LASTEXITCODE
    [IO.File]::WriteAllText((Join-Path $overnightOutput 'result.txt'), $overnightResult)
    Write-Output $overnightResult
    $overnightChanged = @($overnightBefore | Where-Object {-not(Test-Path -LiteralPath $_.Path) -or (Get-FileHash -LiteralPath $_.Path -Algorithm SHA256).Hash -ne $_.Sha256})
    [pscustomobject]@{SourceUnchanged=$overnightChanged.Count-eq0;ProtectedInputs=$overnightBefore.Count;ChangedInputs=$overnightChanged;ExitCode=$overnightExit;
        RuntimeStarted=$false;NativeSystemWrites=$false;PowerShellStarted=$false;Settings='transient';OutputDirectory=$overnightOutput} |
        ConvertTo-Json -Depth 4 | Out-File -LiteralPath (Join-Path $overnightOutput 'verification.json') -Encoding utf8
    Write-Output ('OUTPUT ' + $overnightOutput)
    if ($overnightChanged.Count-ne0) { throw 'Inputs changed; rerun after sources are frozen.' }
    if ($overnightExit-ne0) { throw 'Overnight boundary checks failed; inspect result.txt.' }
} finally { Pop-Location }
