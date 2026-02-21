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
    if exist ".env.example" (
        copy /Y ".env.example" ".env" >nul
        echo [THONG TIN] Da tao file .env tu .env.example.
    ) else (
        echo [CANH BAO] Khong tim thay .env.example de tao file mau.
    )
)

where dotnet >nul 2>nul
if errorlevel 1 (
    echo [LOI] Khong tim thay .NET SDK trong PATH.
    echo Cai .NET SDK roi thu lai.
    pause
    exit /b 1
)

powershell -NoProfile -Command "$token=$env:DISCORD_BOT_TOKEN; if([string]::IsNullOrWhiteSpace($token) -and (Test-Path '.env')){ foreach($rawLine in Get-Content '.env'){ $line=$rawLine.Trim(); if([string]::IsNullOrWhiteSpace($line) -or $line.StartsWith('#')){ continue }; if($line.StartsWith('export ', [System.StringComparison]::OrdinalIgnoreCase)){ $line=$line.Substring(7).Trim() }; $parts=$line.Split('=',2); if($parts.Length -ne 2){ continue }; if($parts[0].Trim() -ne 'DISCORD_BOT_TOKEN'){ continue }; $value=$parts[1].Trim(); if(-not [string]::IsNullOrWhiteSpace($value)){ $token=$value; break } } }; if([string]::IsNullOrWhiteSpace($token)){ exit 1 } else { exit 0 }" >nul 2>nul
if errorlevel 1 (
    echo [LOI] Thieu DISCORD_BOT_TOKEN.
    echo [LOI] Hay mo file .env va dien:
    echo        DISCORD_BOT_TOKEN=token_cua_ban
    echo.
    pause
    exit /b 1
)

if defined RESET_COMMANDS (
    echo [THONG TIN] Che do RESET LENH da bat.
    echo [THONG TIN] Bot se dang ky lai slash command ^(xoa lenh cu khong con trong code^).
    echo [THONG TIN] Neu dung guild command, lenh moi se cap nhat nhanh hon global command.
    echo.
)

echo [THONG TIN] Dang kiem tra instance bot cu...
set "STOPPED_ANY="
for /f %%P in ('powershell -NoProfile -Command "$targets = @(Get-CimInstance Win32_Process).Where({ $_.Name -ieq 'ProjectManagerBot.exe' -or ($_.Name -ieq 'dotnet.exe' -and $_.CommandLine -match 'ProjectManagerBot\.csproj|ProjectManagerBot\.dll') }); foreach($p in $targets){ try { Stop-Process -Id $p.ProcessId -Force -ErrorAction Stop; [Console]::WriteLine($p.ProcessId) } catch {} }"') do (
    echo [THONG TIN] Da dung process bot cu ^(PID %%P^).
    set "STOPPED_ANY=1"
)
if defined STOPPED_ANY (
    timeout /t 1 /nobreak >nul
) else (
    echo [THONG TIN] Khong co instance bot cu dang chay.
)
echo.

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
