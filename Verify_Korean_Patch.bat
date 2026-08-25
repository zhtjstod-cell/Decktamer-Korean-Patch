@echo off
chcp 65001 >nul
set "PATCH_SCRIPT=%~dp0tools\DecktamerKoreanPatch.ps1"
if "%~1"=="" (
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%PATCH_SCRIPT%" -Mode Verify
) else (
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%PATCH_SCRIPT%" -Mode Verify -GamePath "%~1"
)
set "PATCH_EXIT=%ERRORLEVEL%"
echo.
pause
exit /b %PATCH_EXIT%

