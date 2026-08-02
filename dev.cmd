@rem @author bdth 2074055628@qq.com
@rem file: dev loop - kill running instance, rebuild, relaunch
@echo off
chcp 65001 >nul
setlocal
cd /d "%~dp0"

set OUT=Pavise.dev.exe
set MODE=%~1

rem 先结束正在运行的 Pavise 实例：全局单实例互斥会让新实例静默退出，文件占用也会挡住编译输出
rem 优先发全局退出事件走优雅还原链（无需提权）；等不到再提权强杀兜底
powershell -NoProfile -Command "try{[System.Threading.EventWaitHandle]::OpenExisting('Global\Pavise_Exit').Set()}catch{}" >nul 2>&1
set /a WAITED=0
:waitexit
tasklist /fi "imagename eq Pavise*" 2>nul | find /i "Pavise" >nul || goto killdone
set /a WAITED+=1
if %WAITED% geq 8 goto forcekill
ping -n 2 127.0.0.1 >nul
goto waitexit
:forcekill
echo 旧实例未响应退出信号，提权强杀（下次启动会走崩溃恢复链）
powershell -NoProfile -Command "Start-Process taskkill -ArgumentList '/f','/im','Pavise*' -Verb RunAs -Wait" >nul 2>&1
ping -n 2 127.0.0.1 >nul
:killdone

if /i "%MODE%"=="test" goto test

call "%~dp0build.cmd" %OUT%
if errorlevel 1 (
    echo 构建失败，未启动
    exit /b 1
)
echo 启动 %OUT% ...
start "" "%~dp0%OUT%"
exit /b 0

:test
call "%~dp0build.cmd" Pavise.selftest.work.exe --selftest
if errorlevel 1 (
    echo 构建失败，未运行自测
    exit /b 1
)
set REPORT=%TEMP%\Pavise.selftest.txt
echo 运行自测...
start /wait "" "%~dp0Pavise.selftest.work.exe" --selftest "%REPORT%"
echo.
findstr /b /c:"FAIL" /c:"TOTAL" "%REPORT%"
echo 完整报告: %REPORT%
exit /b 0
