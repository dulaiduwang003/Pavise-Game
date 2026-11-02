@echo off
if defined PAVISE_CP_OWNED goto cpready
for /f "tokens=2 delims=:" %%a in ('chcp') do set "PAVISE_OLDCP=%%a"
set "PAVISE_OLDCP=%PAVISE_OLDCP: =%"
chcp 65001 >nul
:cpready
setlocal
cd /d "%~dp0"
if not exist build mkdir build

if /i not "%~1"=="-b" goto usage
if /i "%~2"=="dev" goto dev
if /i "%~2"=="prod" goto dev
goto usage

:dev
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
    echo csc.exe not found - install .NET Framework 4.x
    exit /b 1
)

set REFS=-unsafe+ -reference:System.dll -reference:System.Drawing.dll -reference:System.Windows.Forms.dll -reference:System.Core.dll -reference:System.Management.dll -reference:System.Xml.dll -reference:System.Security.dll
set POWER_RESOURCE_RSP=build\PowerPlans.resources.rsp
> "%POWER_RESOURCE_RSP%" (for %%P in (src\Assets\PowerPlans\*.pow) do @echo -resource:"%%P",Pavise.PowerPlans.%%~nxP)
set OUT=build\Pavise.exe
if not "%~3"=="" set OUT=%~3
if /i "%~4"=="--selftest" goto selftest

if not exist src\Assets\Pavise.ico (
    echo The verified application icon is missing from src\Assets.
    goto err
)

echo [1/2] compiling the Pavise workspace...
set MANIFEST=build\Pavise.manifest.tmp
>  "%MANIFEST%" echo ^<?xml version="1.0" encoding="UTF-8" standalone="yes"?^>
>> "%MANIFEST%" echo ^<assembly xmlns="urn:schemas-microsoft-com:asm.v1" manifestVersion="1.0"^>
>> "%MANIFEST%" echo   ^<trustInfo xmlns="urn:schemas-microsoft-com:asm.v3"^>
>> "%MANIFEST%" echo     ^<security^>
>> "%MANIFEST%" echo       ^<requestedPrivileges^>
>> "%MANIFEST%" echo         ^<requestedExecutionLevel level="requireAdministrator" uiAccess="false"/^>
>> "%MANIFEST%" echo       ^</requestedPrivileges^>
>> "%MANIFEST%" echo     ^</security^>
>> "%MANIFEST%" echo   ^</trustInfo^>
>> "%MANIFEST%" echo   ^<application xmlns="urn:schemas-microsoft-com:asm.v3"^>
>> "%MANIFEST%" echo     ^<windowsSettings^>
>> "%MANIFEST%" echo       ^<dpiAware xmlns="http://schemas.microsoft.com/SMI/2005/WindowsSettings"^>true/pm^</dpiAware^>
>> "%MANIFEST%" echo       ^<dpiAwareness xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings"^>PerMonitorV2^</dpiAwareness^>
>> "%MANIFEST%" echo     ^</windowsSettings^>
>> "%MANIFEST%" echo   ^</application^>
>> "%MANIFEST%" echo   ^<compatibility xmlns="urn:schemas-microsoft-com:compatibility.v1"^>
>> "%MANIFEST%" echo     ^<application^>^<supportedOS Id="{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}" /^>^</application^>
>> "%MANIFEST%" echo   ^</compatibility^>
>> "%MANIFEST%" echo ^</assembly^>
"%CSC%" -nologo -target:winexe -optimize+ -codepage:65001 -define:PAVISE_STABILITY_COMPARISON -win32icon:src\Assets\Pavise.ico -win32manifest:"%MANIFEST%" -out:"%OUT%" %REFS% -resource:"tools\Uninstall-Rtss.ps1",Pavise.LegacyCleanup.RtssUninstall.ps1 @"%POWER_RESOURCE_RSP%" -recurse:src\*.cs
if errorlevel 1 goto err
echo [2/2] checking implementation and version identity...
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\Verify-BuildIdentity.ps1" -Executable "%OUT%"
if errorlevel 1 goto err

copy /y "Pavise.exe.config" "%OUT%.config" >nul
if errorlevel 1 goto err
del build\Pavise.tmp.exe "%MANIFEST%" >nul 2>&1
echo.
for %%F in ("%OUT%") do set "PAVISE_OUTPUT_DIR=%%~dpF"
if /i not "%PAVISE_OUTPUT_DIR%"=="%CD%\" (
    copy /y "LICENSE" "%PAVISE_OUTPUT_DIR%LICENSE" >nul
    if errorlevel 1 goto err
    copy /y "NOTICE" "%PAVISE_OUTPUT_DIR%NOTICE" >nul
    if errorlevel 1 goto err
    copy /y "Uninstall-RTSS.cmd" "%PAVISE_OUTPUT_DIR%Uninstall-RTSS.cmd" >nul
    if errorlevel 1 goto err
    copy /y "tools\Uninstall-Rtss.ps1" "%PAVISE_OUTPUT_DIR%Uninstall-Rtss.ps1" >nul
    if errorlevel 1 goto err
)
echo Build OK -^> %OUT%
call :restorecp
goto :eof

:selftest
if "%~3"=="" set OUT=build\Pavise.selftest.exe
echo [selftest] compiling isolated regression runner...
"%CSC%" -nologo -target:exe -platform:x64 -optimize+ -codepage:65001 -main:PaviseApp.OfficialBuildContractChecks -out:"%OUT%" %REFS% -resource:"tools\Uninstall-Rtss.ps1",Pavise.LegacyCleanup.RtssUninstall.ps1 @"%POWER_RESOURCE_RSP%" -recurse:src\*.cs tests\Official3106\BuildContractChecks.cs
set BUILD_EXIT=%ERRORLEVEL%
call :restorecp
exit /b %BUILD_EXIT%

:err
echo Build failed
del build\Pavise.tmp.exe "%MANIFEST%" >nul 2>&1
call :restorecp
exit /b 1

:usage
echo Usage: build.cmd -b dev [output.exe] [--selftest ^| --compile-only]
echo        prod is the same build the exe is shipped as compiled
echo        Builds use src\Assets\Pavise.ico and never run a Pavise entry point.
call :restorecp
exit /b 2

:restorecp
if defined PAVISE_CP_OWNED goto :eof
if defined PAVISE_OLDCP chcp %PAVISE_OLDCP% >nul 2>&1
goto :eof
