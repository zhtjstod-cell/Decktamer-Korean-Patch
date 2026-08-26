@echo off
setlocal
chcp 65001 >nul
set "PATCH_EXE=%~dp0Decktamer_Korean_Patch.exe"
set "PATCH_SCRIPT=%~dp0tools\DecktamerKoreanPatch.ps1"
set "POWERSHELL_EXE=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
if exist "%PATCH_EXE%" goto run_exe
if exist "%PATCH_SCRIPT%" goto script_found
echo 내장 설치기와 비상용 스크립트를 찾지 못했습니다. ZIP의 폴더 구조를 그대로 유지해 주세요.
pause
exit /b 2

:run_exe
if "%~1"=="" goto run_exe_auto
"%PATCH_EXE%" --install --game "%~1" --no-pause
goto finish

:run_exe_auto
"%PATCH_EXE%" --install --no-pause
goto finish

:script_found
if "%~1"=="" goto run_auto
"%POWERSHELL_EXE%" -NoProfile -ExecutionPolicy Bypass -File "%PATCH_SCRIPT%" -Mode Install -GamePath "%~1"
goto finish

:run_auto
"%POWERSHELL_EXE%" -NoProfile -ExecutionPolicy Bypass -File "%PATCH_SCRIPT%" -Mode Install

:finish
set "PATCH_EXIT=%ERRORLEVEL%"
echo(
if not "%PATCH_EXIT%"=="0" echo 설치에 실패했습니다. 위 오류 내용을 확인하세요.
pause
exit /b %PATCH_EXIT%
