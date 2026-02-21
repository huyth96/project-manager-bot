@echo off
setlocal
chcp 65001 >nul

cd /d "%~dp0"

set "MODE=%~1"
if /I "%MODE%"=="reset-lenh" set "MODE=reset-commands"
if /I "%MODE%"=="help" goto :help
if /I "%MODE%"=="/?" goto :help

set "RESET_COMMANDS="
if "%MODE%"=="" goto :args-ok
if /I "%MODE%"=="reset-commands" (
    set "RESET_COMMANDS=1"
    goto :args-ok
)

echo [LOI] Tham so khong hop le: %MODE%
echo.
goto :help

:args-ok

if not exist ".env" (
    echo [CANH BAO] Khong tim thay file .env.
    echo [CANH BAO] Hay tao .env tu .env.example va dien token bot truoc khi chay.
)

where dotnet >nul 2>nul
if errorlevel 1 (
    echo [LOI] Khong tim thay .NET SDK trong PATH.
    echo Cai .NET SDK roi thu lai.
    pause
    exit /b 1
)

if defined RESET_COMMANDS (
    echo [THONG TIN] Che do RESET LENH da bat.
    echo [THONG TIN] Bot se dang ky lai slash command (xoa lenh cu khong con trong code).
    echo [THONG TIN] Neu dung guild command, lenh moi se cap nhat nhanh hon global command.
    echo.
)

echo [THONG TIN] Dang khoi chay ProjectManagerBot...
dotnet run --project "ProjectManagerBot.csproj"
set "EXIT_CODE=%ERRORLEVEL%"

echo.
if not "%EXIT_CODE%"=="0" (
    echo [LOI] Bot da dung voi ma loi %EXIT_CODE%.
) else (
    echo [THONG TIN] Bot da dung.
)

pause
exit /b %EXIT_CODE%

:help
echo CACH DUNG:
echo   run.bat
echo      - Chay bot nhu binh thuong.
echo.
echo   run.bat reset-commands
echo   run.bat reset-lenh
echo      - Chay bot o che do reset slash command.
echo.
pause
exit /b 0
