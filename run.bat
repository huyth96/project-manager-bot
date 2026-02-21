@echo off
setlocal

cd /d "%~dp0"

where dotnet >nul 2>nul
if errorlevel 1 (
    echo [ERROR] Khong tim thay dotnet SDK trong PATH.
    echo Cai .NET SDK roi thu lai.
    pause
    exit /b 1
)

echo [INFO] Starting ProjectManagerBot...
dotnet run --project "ProjectManagerBot.csproj"
set "EXIT_CODE=%ERRORLEVEL%"

echo.
if not "%EXIT_CODE%"=="0" (
    echo [ERROR] Bot da dung voi ma loi %EXIT_CODE%.
) else (
    echo [INFO] Bot da dung.
)

pause
exit /b %EXIT_CODE%
