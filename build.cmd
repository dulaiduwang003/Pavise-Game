@echo off
chcp 65001 >nul
rem @author bdth 2074055628@qq.com
rem 文件用途 编译 Aegis 并生成程序图标和运行清单
setlocal
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
    echo 找不到 csc.exe 请确认已安装 .NET Framework 4.x
    exit /b 1
)

set REFS=-reference:System.dll -reference:System.Drawing.dll -reference:System.Windows.Forms.dll -reference:System.Core.dll -reference:System.Management.dll
set OUT=Aegis.exe
if not "%~1"=="" set OUT=%~1

echo [1/3] 编译临时 exe...
"%CSC%" -nologo -target:winexe -optimize+ -codepage:65001 -out:Aegis.tmp.exe %REFS% -recurse:src\*.cs
if errorlevel 1 goto err

echo [2/3] 生成 Aegis.ico...
.\Aegis.tmp.exe --genicon

echo [3/3] 编译...
set MANIFEST=Aegis.manifest.tmp
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
"%CSC%" -nologo -target:winexe -optimize+ -codepage:65001 -win32icon:Aegis.ico -win32manifest:"%MANIFEST%" -out:"%OUT%" %REFS% -recurse:src\*.cs
if errorlevel 1 goto err

del Aegis.tmp.exe "%MANIFEST%" >nul 2>&1
echo.
echo 构建成功 -^> %OUT%
goto :eof

:err
echo 构建失败
del Aegis.tmp.exe "%MANIFEST%" >nul 2>&1
exit /b 1
