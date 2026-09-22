@echo off
chcp 936 >nul
cd /d "%~dp0"
echo 正在自检"能不能连上服务器"，请稍候...
echo.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0连接自检.ps1"
