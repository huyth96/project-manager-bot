@echo off
setlocal
chcp 65001 >nul

cd /d "%~dp0"

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
