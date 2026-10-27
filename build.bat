@echo off
chcp 65001 >nul
setlocal
cd /d "%~dp0"

set "DOTNET=C:\Program Files\dotnet\dotnet.exe"
if not exist "%DOTNET%" set "DOTNET=dotnet"

echo ============ 编译 ============
"%DOTNET%" build "TcpAssistant.sln" -c Release --nologo
if errorlevel 1 (
    echo 编译失败。
    pause
    exit /b 1
)

echo.
echo ============ 自测 ============
"tests\SelfTest\bin\Release\net472\SelfTest.exe"
set "CODE=%ERRORLEVEL%"

echo.
if "%CODE%"=="0" (
    echo 全部自测通过。
) else (
    echo 存在失败项，请查看上面的输出。
)
pause
exit /b %CODE%
