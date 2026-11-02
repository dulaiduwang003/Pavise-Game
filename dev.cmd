@echo off
for /f "tokens=2 delims=:" %%a in ('chcp') do set "PAVISE_OLDCP=%%a"
set "PAVISE_OLDCP=%PAVISE_OLDCP: =%"
chcp 65001 >nul
setlocal
set PAVISE_CP_OWNED=1
cd /d "%~dp0"

set OUT=build\Pavise.dev.exe
set MODE=%~1
if /i "%MODE%"=="test" goto test

powershell -NoProfile -Command "try{[System.Threading.EventWaitHandle]::OpenExisting('Global\Pavise_Exit').Set()}catch{}" >nul 2>&1
set /a WAITED=0
:waitexit
tasklist /fi "imagename eq Pavise*" 2>nul | find /i "Pavise" >nul || goto killdone
set /a WAITED+=1
if %WAITED% geq 8 goto forcekill
ping -n 2 127.0.0.1 >nul
goto waitexit
:forcekill
echo Old instance ignored the exit signal, force-killing (next start runs crash recovery)
powershell -NoProfile -Command "Start-Process taskkill -ArgumentList '/f','/im','Pavise*' -Verb RunAs -Wait" >nul 2>&1
ping -n 2 127.0.0.1 >nul
:killdone

call "%~dp0build.cmd" -b dev %OUT%
if errorlevel 1 (
    echo Build failed, not launching
    call :restorecp
    exit /b 1
)
echo Launching %OUT% ...
start "" "%~dp0%OUT%"
call :restorecp
exit /b 0

:test
set "TESTDIR=%TEMP%\PaviseSelftest-%RANDOM%-%RANDOM%"
if exist "%TESTDIR%" goto test
mkdir "%TESTDIR%"
if errorlevel 1 (
    call :restorecp
    exit /b 1
)
call "%~dp0build.cmd" -b dev "%TESTDIR%\Pavise.selftest.exe" --selftest
if errorlevel 1 (
    echo Build failed, self-test skipped
    call :restorecp
    exit /b 1
)
set "REPORT=%TESTDIR%\results.txt"
echo Running self-test...
"%TESTDIR%\Pavise.selftest.exe" --selftest "%REPORT%"
set TEST_EXIT=%ERRORLEVEL%
echo.
findstr /b /c:"FAIL" /c:"TOTAL" "%REPORT%"
echo Full report: %REPORT%
call :restorecp
exit /b %TEST_EXIT%

:restorecp
if defined PAVISE_OLDCP chcp %PAVISE_OLDCP% >nul 2>&1
goto :eof
