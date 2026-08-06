@rem @author bdth 2074055628@qq.com
@rem file: build Pavise, icon, and manifest
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
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
    echo csc.exe not found - install .NET Framework 4.x
    exit /b 1
)

set REFS=-reference:System.dll -reference:System.Drawing.dll -reference:System.Windows.Forms.dll -reference:System.Core.dll -reference:System.Management.dll -reference:System.Xml.dll
set OUT=Pavise.exe
if not "%~1"=="" set OUT=%~1
set TESTARGS=
if /i "%~2"=="--selftest" set TESTARGS=-define:PAVISE_SELFTEST -recurse:tests\*.cs

echo [1/3] compiling temp exe...
"%CSC%" -nologo -target:winexe -optimize+ -codepage:65001 -out:Pavise.tmp.exe %REFS% %TESTARGS% -recurse:src\*.cs
if errorlevel 1 goto err

echo [2/3] generating Pavise.ico...
.\Pavise.tmp.exe --genicon

echo [3/3] compiling...
set MANIFEST=Pavise.manifest.tmp
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
"%CSC%" -nologo -target:winexe -optimize+ -codepage:65001 -win32icon:Pavise.ico -win32manifest:"%MANIFEST%" -out:"%OUT%" %REFS% %TESTARGS% -recurse:src\*.cs
if errorlevel 1 goto err

del Pavise.tmp.exe "%MANIFEST%" >nul 2>&1
echo.
echo Build OK -^> %OUT%
call :restorecp
goto :eof

:err
echo Build failed
del Pavise.tmp.exe "%MANIFEST%" >nul 2>&1
call :restorecp
exit /b 1

:restorecp
if defined PAVISE_CP_OWNED goto :eof
if defined PAVISE_OLDCP chcp %PAVISE_OLDCP% >nul 2>&1
goto :eof
