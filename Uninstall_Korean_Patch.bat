@echo off
chcp 65001 >nul
set "PATCH_SCRIPT=%~dp0tools\DecktamerKoreanPatch.ps1"
if "%~1"=="" (
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%PATCH_SCRIPT%" -Mode Uninstall
) else (
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%PATCH_SCRIPT%" -Mode Uninstall -GamePath "%~1"
)
set "PATCH_EXIT=%ERRORLEVEL%"
echo.
if not "%PATCH_EXIT%"=="0" echo 제거에 실패했습니다. 위 오류 내용을 확인하세요.
pause
exit /b %PATCH_EXIT%

