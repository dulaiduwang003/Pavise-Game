@rem @author bdth 2074055628@qq.com
@rem file: reproducible protected release build
@rem ASCII ONLY. See build.cmd for the codepage reason.
@echo off
setlocal
cd /d "%~dp0"

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-protected.ps1" %*
exit /b %errorlevel%
