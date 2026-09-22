@echo off
chcp 65001 >nul
setlocal
rem ===================================================================
rem  修复被控端"开机自启"（在**被控端那台机器**上以管理员身份运行）
rem
rem  为什么要修：早期安装脚本注册的是"用户登录时才启动、且绑定安装时的用户"——
rem  被控机器只要停在登录界面、或重启后没人登录，被控端就永远起不来（实测踩到过）。
rem  本脚本把它改成"开机即启动 + SYSTEM + 最高权限"。
rem
rem  用法：
rem    fix-agent-autostart.cmd                     （默认装目录 E:\RemoteControl\agent）
rem    fix-agent-autostart.cmd "D:\RemoteControl\agent"
rem ===================================================================

set "DIR=%~1"
if "%DIR%"=="" set "DIR=E:\RemoteControl\agent"
set "EXE=%DIR%\Agent.Coordinator.exe"

net session >nul 2>&1
if errorlevel 1 (
    echo [错误] 请以管理员身份运行（右键 -^> 以管理员身份运行）
    pause
    exit /b 1
)

if not exist "%EXE%" (
    echo [错误] 找不到被控端程序：%EXE%
    echo        请把安装目录作为参数传进来，例如：fix-agent-autostart.cmd "D:\RemoteControl\agent"
    pause
    exit /b 1
)

echo ===== 1. 注册开机自启（开机触发 / SYSTEM / 最高权限）=====
schtasks /create /tn RemoteControlAgent /tr "%EXE%" /sc onstart /ru SYSTEM /rl highest /f
if errorlevel 1 (
    echo [错误] 注册失败
    pause
    exit /b 1
)

echo ===== 2. 启动被控端 =====
start "" "%EXE%"
timeout /t 6 /nobreak >nul

echo ===== 3. 结果 =====
schtasks /query /tn RemoteControlAgent /fo LIST | findstr /i "TaskName Status"
echo.
echo 完成：以后开机就会自动启动；稍等 10 秒再让主控端「刷新」看在线列表。
echo 若列表里仍然没有，请把 %DIR%\logs\coordinator-*.log 最后 20 行发给技术支持。
pause
