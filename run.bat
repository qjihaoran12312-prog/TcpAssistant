@echo off
chcp 65001 >nul
setlocal
cd /d "%~dp0"

set "EXE=src\TcpAssistant\bin\Release\net472\TcpAssistant.exe"
if exist "%EXE%" goto launch

set "DOTNET=C:\Program Files\dotnet\dotnet.exe"
if not exist "%DOTNET%" set "DOTNET=dotnet"

echo 首次运行，正在编译（约 5 秒）...
"%DOTNET%" build "TcpAssistant.sln" -c Release --nologo
if errorlevel 1 (
    echo.
    echo 编译失败：需要 .NET SDK 8.0 或更高版本。
    pause
    exit /b 1
)

:launch
start "" "%EXE%"
