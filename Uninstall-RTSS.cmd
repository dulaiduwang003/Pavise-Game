@echo off
setlocal
set "PAVISE_RTSS_SCRIPT=%~dp0Uninstall-Rtss.ps1"
if not exist "%PAVISE_RTSS_SCRIPT%" set "PAVISE_RTSS_SCRIPT=%~dp0tools\Uninstall-Rtss.ps1"
if not exist "%PAVISE_RTSS_SCRIPT%" (
    echo RTSS removal script was not found. Keep both uninstall files together.
    pause
    exit /b 2
)

powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%PAVISE_RTSS_SCRIPT%" -Uninstall %*
set "PAVISE_RTSS_EXIT=%ERRORLEVEL%"
echo.
pause
exit /b %PAVISE_RTSS_EXIT%
