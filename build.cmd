@rem @author bdth 2074055628@qq.com
@rem file: dev compiler and protected production build dispatcher
@rem ASCII ONLY. cmd decodes this file with the codepage the console had at
@rem startup (936 here) and chcp does NOT change that. One UTF-8 CJK char
@rem shifts the parser and comment text gets executed as a command.
@echo off
rem when called from dev.cmd it owns the codepage; do not restore it early
rem or the caller's remaining output lands on the wrong codepage.
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
if /i "%~2"=="prod" goto prod
goto usage

:prod
set "PROD_OUT=build\Pavise.exe"
if not "%~3"=="" set "PROD_OUT=%~3"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-protected.ps1" -Output "%PROD_OUT%" -Force
set "BUILD_EXIT=%ERRORLEVEL%"
call :restorecp
exit /b %BUILD_EXIT%

:dev
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
    echo csc.exe not found - install .NET Framework 4.x
    exit /b 1
)

set REFS=-reference:System.dll -reference:System.Drawing.dll -reference:System.Windows.Forms.dll -reference:System.Core.dll -reference:System.Management.dll -reference:System.Xml.dll
set OUT=build\Pavise.exe
if not "%~3"=="" set OUT=%~3
if /i "%~4"=="--selftest" goto selftest

rem App.Version in Program.cs is the single version source. Keep the external
rem update manifest synchronized before every development or production build.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\Sync-VersionManifest.ps1"
if errorlevel 1 goto err


echo [1/3] compiling temp exe...
"%CSC%" -nologo -target:winexe -optimize+ -codepage:65001 -out:build\Pavise.tmp.exe %REFS% -recurse:src\*.cs
if errorlevel 1 goto err

echo [2/3] generating Pavise.ico...
.\build\Pavise.tmp.exe --genicon

echo [3/3] compiling...
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
>> "%MANIFEST%" echo ^</assembly^>
"%CSC%" -nologo -target:winexe -optimize+ -codepage:65001 -win32icon:build\Pavise.ico -win32manifest:"%MANIFEST%" -out:"%OUT%" %REFS% -recurse:src\*.cs
if errorlevel 1 goto err

del build\Pavise.tmp.exe "%MANIFEST%" >nul 2>&1
echo.
echo Build OK -^> %OUT%
call :restorecp
goto :eof

:selftest
rem A dedicated console entry point cannot launch the normal tuning runtime.
rem Do not rewrite the icon/version manifest or require administrator rights.
if "%~3"=="" set OUT=build\Pavise.selftest.exe
echo [selftest] compiling isolated regression runner...
"%CSC%" -nologo -target:exe -platform:x64 -optimize+ -codepage:65001 -define:PAVISE_SELFTEST;PAVISE_SELFTEST_RUNNER -main:PaviseApp.SelfTestRunner -out:"%OUT%" %REFS% -recurse:src\*.cs -recurse:tests\*.cs
set BUILD_EXIT=%ERRORLEVEL%
call :restorecp
exit /b %BUILD_EXIT%

:err
echo Build failed
del build\Pavise.tmp.exe "%MANIFEST%" >nul 2>&1
call :restorecp
exit /b 1

:usage
echo Usage: build.cmd -b dev [output.exe] [--selftest]
echo        build.cmd -b prod [output.exe]
call :restorecp
exit /b 2

:restorecp
if defined PAVISE_CP_OWNED goto :eof
if defined PAVISE_OLDCP chcp %PAVISE_OLDCP% >nul 2>&1
goto :eof
